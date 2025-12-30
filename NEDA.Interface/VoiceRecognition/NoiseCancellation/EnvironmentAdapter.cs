using System;
using System.Collections.Generic;
using System.Linq;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;

namespace NEDA.Interface.VoiceRecognition.NoiseCancellation;
{
    /// <summary>
    /// Ortam ses profillerini analiz eden ve gürültü iptalini dinamik olarak uyarlayan adaptif sistem.
    /// Gerçek zamanlı akustik ortam analizi ile en uygun filtreleme stratejilerini uygular.
    /// </summary>
    public class EnvironmentAdapter : IEnvironmentAdapter, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly AudioProfileManager _audioProfileManager;
        private readonly NoiseFilterPipeline _filterPipeline;
        private readonly EnvironmentAnalyzer _environmentAnalyzer;
        private readonly AdaptiveFilterEngine _adaptiveEngine;

        private EnvironmentProfile _currentProfile;
        private List<AudioSample> _noiseSamples;
        private CancellationTokenSource _analysisCancellation;
        private bool _isInitialized;
        private bool _isDisposed;

        /// <summary>
        /// Geçerli ortam profili değiştiğinde tetiklenen olay;
        /// </summary>
        public event EventHandler<EnvironmentChangedEventArgs> EnvironmentChanged;

        /// <summary>
        /// Gürültü seviyesi kritik eşiği aştığında tetiklenen olay;
        /// </summary>
        public event EventHandler<NoiseThresholdExceededEventArgs> NoiseThresholdExceeded;

        /// <summary>
        /// Ortam adaptasyon sistemi başlatıcısı;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="configuration">Adaptasyon konfigürasyonu</param>
        public EnvironmentAdapter(ILogger logger, EnvironmentAdapterConfig configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? EnvironmentAdapterConfig.Default;

            InitializeComponents();
            _logger.Info("EnvironmentAdapter initialized successfully");
        }

        private void InitializeComponents()
        {
            _audioProfileManager = new AudioProfileManager(_configuration.ProfileSettings);
            _filterPipeline = new NoiseFilterPipeline(_configuration.FilterSettings);
            _environmentAnalyzer = new EnvironmentAnalyzer(_configuration.AnalysisSettings);
            _adaptiveEngine = new AdaptiveFilterEngine(_configuration.AdaptationSettings);

            _noiseSamples = new List<AudioSample>(_configuration.SampleBufferSize);
            _analysisCancellation = new CancellationTokenSource();

            _currentProfile = EnvironmentProfile.CreateDefault();
            _isInitialized = true;
        }

        /// <summary>
        /// Ortam sesini analiz eder ve gürültü iptali parametrelerini uyarlar;
        /// </summary>
        /// <param name="audioInput">Ham ses girişi</param>
        /// <returns>Adapte edilmiş ses çıktısı</returns>
        public AudioBuffer ProcessAudio(AudioBuffer audioInput)
        {
            ValidateInitialization();

            try
            {
                // 1. Ortam analizi yap;
                var analysisResult = AnalyzeEnvironment(audioInput);

                // 2. Ortam değiştiyse profili güncelle;
                if (analysisResult.EnvironmentChanged)
                {
                    UpdateEnvironmentProfile(analysisResult);
                }

                // 3. Gürültü filtreleme uygula;
                var filteredAudio = ApplyNoiseCancellation(audioInput, analysisResult);

                // 4. Geri besleme için kalite metriğini hesapla;
                CalculateQualityMetric(filteredAudio, analysisResult);

                return filteredAudio;
            }
            catch (Exception ex)
            {
                _logger.Error($"Audio processing failed: {ex.Message}", ex);
                throw new EnvironmentAdaptationException("Failed to process audio with environment adaptation", ex);
            }
        }

        /// <summary>
        /// Ortamı gerçek zamanlı olarak analiz eder;
        /// </summary>
        private EnvironmentAnalysis AnalyzeEnvironment(AudioBuffer audioInput)
        {
            var analysis = new EnvironmentAnalysis();

            // Gürültü örneklerini topla;
            CollectNoiseSamples(audioInput);

            // Frekans analizi yap;
            var frequencyAnalysis = _environmentAnalyzer.AnalyzeFrequencySpectrum(audioInput);
            analysis.FrequencyProfile = frequencyAnalysis;

            // Gürültü türünü tespit et;
            analysis.NoiseType = IdentifyNoiseType(frequencyAnalysis);

            // Ses seviyesini hesapla;
            analysis.DecibelLevel = CalculateDecibelLevel(audioInput);
            analysis.IsCriticalNoise = analysis.DecibelLevel > _configuration.CriticalNoiseThreshold;

            // Ortam türünü belirle (sessiz ofis, kalabalık cafe, vs.)
            analysis.EnvironmentType = DetermineEnvironmentType(analysis);

            // Değişim tespiti;
            analysis.EnvironmentChanged = DetectEnvironmentChange(analysis);

            return analysis;
        }

        /// <summary>
        /// Gürültü örneklerini toplar ve karakteristiğini analiz eder;
        /// </summary>
        private void CollectNoiseSamples(AudioBuffer audioInput)
        {
            var noiseSample = AudioSample.CreateFromBuffer(audioInput, DateTime.UtcNow);
            _noiseSamples.Add(noiseSample);

            // Buffer boyutunu kontrol et;
            if (_noiseSamples.Count > _configuration.SampleBufferSize)
            {
                _noiseSamples.RemoveAt(0);
            }

            // Gürültü desenlerini analiz et;
            if (_noiseSamples.Count >= _configuration.MinimumSamplesForPattern)
            {
                AnalyzeNoisePatterns();
            }
        }

        /// <summary>
        /// Gürültü türünü tanımlar (sürekli, ani, periyodik, vs.)
        /// </summary>
        private NoiseType IdentifyNoiseType(FrequencyAnalysis frequencyAnalysis)
        {
            if (frequencyAnalysis.DominantFrequency < 100)
                return NoiseType.LowFrequencyRumble;

            if (frequencyAnalysis.HasSharpPeaks)
                return NoiseType.Impulsive;

            if (frequencyAnalysis.IsConsistent)
                return NoiseType.Continuous;

            if (frequencyAnalysis.HasPeriodicPattern)
                return NoiseType.Periodic;

            return NoiseType.WhiteNoise;
        }

        /// <summary>
        /// Ortam profili değişikliğini tespit eder;
        /// </summary>
        private bool DetectEnvironmentChange(EnvironmentAnalysis newAnalysis)
        {
            if (_currentProfile == null)
                return true;

            var changeThreshold = _configuration.ChangeDetectionThreshold;

            // Desibel seviyesi değişimi;
            var dbChange = Math.Abs(newAnalysis.DecibelLevel - _currentProfile.AverageDecibelLevel);
            if (dbChange > changeThreshold.DecibelChange)
                return true;

            // Frekans profili değişimi;
            var freqSimilarity = CalculateFrequencySimilarity(
                newAnalysis.FrequencyProfile,
                _currentProfile.FrequencyProfile);

            if (freqSimilarity < changeThreshold.FrequencySimilarity)
                return true;

            // Gürültü türü değişimi;
            if (newAnalysis.NoiseType != _currentProfile.PrimaryNoiseType)
                return true;

            return false;
        }

        /// <summary>
        /// Ortam profilini günceller;
        /// </summary>
        private void UpdateEnvironmentProfile(EnvironmentAnalysis analysis)
        {
            var oldProfile = _currentProfile;

            _currentProfile = new EnvironmentProfile;
            {
                EnvironmentType = analysis.EnvironmentType,
                PrimaryNoiseType = analysis.NoiseType,
                AverageDecibelLevel = analysis.DecibelLevel,
                FrequencyProfile = analysis.FrequencyProfile,
                DetectionTime = DateTime.UtcNow,
                NoiseSamples = _noiseSamples.ToArray()
            };

            // Filtre parametrelerini uyarla;
            _adaptiveEngine.AdaptFilters(_currentProfile, _filterPipeline);

            // Olay tetikle;
            OnEnvironmentChanged(oldProfile, _currentProfile);

            _logger.Info($"Environment profile updated: {_currentProfile.EnvironmentType}");
        }

        /// <summary>
        /// Gürültü iptali filtrelerini uygular;
        /// </summary>
        private AudioBuffer ApplyNoiseCancellation(AudioBuffer audioInput, EnvironmentAnalysis analysis)
        {
            // Mevcut profile göre filtre seç;
            var filterStrategy = SelectFilterStrategy(analysis);

            // Filtre zincirini yapılandır;
            _filterPipeline.Configure(filterStrategy);

            // Ses işleme uygula;
            var processedAudio = _filterPipeline.Process(audioInput);

            // Kalite kontrol;
            var qualityCheck = VerifyAudioQuality(processedAudio);
            if (!qualityCheck.IsAcceptable)
            {
                _logger.Warn($"Audio quality below threshold: {qualityCheck.QualityScore}");
                processedAudio = ApplyFallbackFiltering(audioInput);
            }

            return processedAudio;
        }

        /// <summary>
        /// Ortama en uygun filtre stratejisini seçer;
        /// </summary>
        private FilterStrategy SelectFilterStrategy(EnvironmentAnalysis analysis)
        {
            switch (analysis.EnvironmentType)
            {
                case EnvironmentType.QuietOffice:
                    return FilterStrategy.LightFiltering;

                case EnvironmentType.NoisyOffice:
                    return FilterStrategy.ModerateFiltering;

                case EnvironmentType.PublicSpace:
                    return FilterStrategy.AggressiveFiltering;

                case EnvironmentType.Transportation:
                    return FilterStrategy.AdaptiveMultiBand;

                case EnvironmentType.Industrial:
                    return FilterStrategy.HeavyDuty;

                default:
                    return FilterStrategy.Balanced;
            }
        }

        /// <summary>
        /// Ses kalitesini doğrular;
        /// </summary>
        private QualityVerification VerifyAudioQuality(AudioBuffer audio)
        {
            var verification = new QualityVerification();

            // Sinyal-gürültü oranı;
            verification.SignalToNoiseRatio = CalculateSNR(audio);

            // Ses doğallığı;
            verification.NaturalnessScore = CalculateNaturalness(audio);

            // Bozulma seviyesi;
            verification.DistortionLevel = CalculateDistortion(audio);

            // Toplam kalite skoru;
            verification.QualityScore = CalculateOverallQuality(verification);
            verification.IsAcceptable = verification.QualityScore >= _configuration.MinimumQualityThreshold;

            return verification;
        }

        /// <summary>
        /// Acil durum filtresi uygular;
        /// </summary>
        private AudioBuffer ApplyFallbackFiltering(AudioBuffer audioInput)
        {
            _logger.Warn("Applying fallback filtering strategy");

            var fallbackFilter = new BasicNoiseGateFilter(
                _configuration.FallbackSettings.Threshold,
                _configuration.FallbackSettings.Attack,
                _configuration.FallbackSettings.Release);

            return fallbackFilter.Process(audioInput);
        }

        /// <summary>
        /// Ortam kalibrasyonu yapar (manuel)
        /// </summary>
        public void Calibrate(CalibrationMode mode, AudioBuffer calibrationSample = null)
        {
            ValidateInitialization();

            try
            {
                switch (mode)
                {
                    case CalibrationMode.AutoCalibration:
                        PerformAutoCalibration();
                        break;

                    case CalibrationMode.ManualCalibration:
                        if (calibrationSample == null)
                            throw new ArgumentNullException(nameof(calibrationSample));
                        PerformManualCalibration(calibrationSample);
                        break;

                    case CalibrationMode.QuickCalibration:
                        PerformQuickCalibration();
                        break;

                    case CalibrationMode.ResetToDefaults:
                        ResetToDefaultProfile();
                        break;
                }

                _logger.Info($"Calibration completed: {mode}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Calibration failed: {ex.Message}", ex);
                throw new CalibrationException("Environment calibration failed", ex);
            }
        }

        /// <summary>
        /// Otomatik kalibrasyon gerçekleştirir;
        /// </summary>
        private void PerformAutoCalibration()
        {
            // 5 saniyelik sessizlik örnekle;
            var silenceSample = CaptureSilenceSample(TimeSpan.FromSeconds(5));

            // 5 saniyelik tipik gürültü örnekle;
            var noiseSample = CaptureAmbientNoiseSample(TimeSpan.FromSeconds(5));

            // Referans profili oluştur;
            var referenceProfile = CreateReferenceProfile(silenceSample, noiseSample);

            // Adaptif motoru kalibre et;
            _adaptiveEngine.Calibrate(referenceProfile);

            // Profili güncelle;
            _currentProfile = referenceProfile;
        }

        /// <summary>
        /// Ortam izlemeyi başlatır;
        /// </summary>
        public void StartContinuousMonitoring()
        {
            if (_analysisCancellation.IsCancellationRequested)
            {
                _analysisCancellation = new CancellationTokenSource();
            }

            Task.Run(() => ContinuousMonitoringLoop(_analysisCancellation.Token));
            _logger.Info("Continuous environment monitoring started");
        }

        /// <summary>
        /// Sürekli ortam izleme döngüsü;
        /// </summary>
        private async Task ContinuousMonitoringLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Ortam örneklemesi al;
                    var ambientSample = CaptureAmbientSample(TimeSpan.FromMilliseconds(500));

                    // Hızlı analiz yap;
                    var quickAnalysis = QuickEnvironmentAnalysis(ambientSample);

                    // Ani gürültü patlamalarını kontrol et;
                    if (quickAnalysis.HasNoiseSpike)
                    {
                        OnNoiseThresholdExceeded(quickAnalysis);
                    }

                    // Profil değişikliğini kontrol et;
                    if (quickAnalysis.SignificantChange)
                    {
                        await UpdateProfileAsync(quickAnalysis, cancellationToken);
                    }

                    // Bekle;
                    await Task.Delay(_configuration.MonitoringInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Monitoring loop error: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Geçerli ortam profilini getirir;
        /// </summary>
        public EnvironmentProfile GetCurrentProfile()
        {
            return _currentProfile?.Clone();
        }

        /// <summary>
        /// Tarihsel ortam verilerini getirir;
        /// </summary>
        public EnvironmentHistory GetHistory(TimeSpan timeRange)
        {
            var fromTime = DateTime.UtcNow - timeRange;
            var historicalProfiles = _audioProfileManager.GetProfilesSince(fromTime);

            return new EnvironmentHistory;
            {
                Profiles = historicalProfiles,
                TimeRange = timeRange,
                EnvironmentChanges = historicalProfiles.Count - 1,
                AverageNoiseLevel = historicalProfiles.Average(p => p.AverageDecibelLevel)
            };
        }

        /// <summary>
        /// Ortam değişikliği olayını tetikler;
        /// </summary>
        protected virtual void OnEnvironmentChanged(EnvironmentProfile oldProfile, EnvironmentProfile newProfile)
        {
            EnvironmentChanged?.Invoke(this, new EnvironmentChangedEventArgs;
            {
                OldProfile = oldProfile,
                NewProfile = newProfile,
                ChangeTime = DateTime.UtcNow;
            });
        }

        /// <summary>
        /// Gürültü eşiği aşımı olayını tetikler;
        /// </summary>
        protected virtual void OnNoiseThresholdExceeded(QuickAnalysis analysis)
        {
            NoiseThresholdExceeded?.Invoke(this, new NoiseThresholdExceededEventArgs;
            {
                DecibelLevel = analysis.DecibelLevel,
                Threshold = _configuration.CriticalNoiseThreshold,
                Timestamp = DateTime.UtcNow,
                NoiseType = analysis.DetectedNoiseType;
            });
        }

        private void ValidateInitialization()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("EnvironmentAdapter is not initialized");

            if (_isDisposed)
                throw new ObjectDisposedException(nameof(EnvironmentAdapter));
        }

        /// <summary>
        /// Kaynakları serbest bırakır;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                _analysisCancellation?.Cancel();
                _analysisCancellation?.Dispose();

                _filterPipeline?.Dispose();
                _adaptiveEngine?.Dispose();

                _noiseSamples?.Clear();

                _isDisposed = true;
                _isInitialized = false;

                _logger.Info("EnvironmentAdapter disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Dispose error: {ex.Message}", ex);
            }
        }

        #region Yardımcı Sınıflar ve Yapılar;

        /// <summary>
        /// Ortam adaptasyon konfigürasyonu;
        /// </summary>
        public class EnvironmentAdapterConfig;
        {
            public static EnvironmentAdapterConfig Default => new EnvironmentAdapterConfig;
            {
                ProfileSettings = AudioProfileSettings.Default,
                FilterSettings = FilterPipelineSettings.Default,
                AnalysisSettings = AnalysisSettings.Default,
                AdaptationSettings = AdaptationSettings.Default,
                SampleBufferSize = 1000,
                MinimumSamplesForPattern = 50,
                CriticalNoiseThreshold = 85.0, // dB;
                MonitoringInterval = TimeSpan.FromSeconds(2),
                MinimumQualityThreshold = 0.7,
                ChangeDetectionThreshold = new ChangeThresholds;
                {
                    DecibelChange = 10.0,
                    FrequencySimilarity = 0.8;
                },
                FallbackSettings = FallbackFilterSettings.Default;
            };

            public AudioProfileSettings ProfileSettings { get; set; }
            public FilterPipelineSettings FilterSettings { get; set; }
            public AnalysisSettings AnalysisSettings { get; set; }
            public AdaptationSettings AdaptationSettings { get; set; }
            public int SampleBufferSize { get; set; }
            public int MinimumSamplesForPattern { get; set; }
            public double CriticalNoiseThreshold { get; set; }
            public TimeSpan MonitoringInterval { get; set; }
            public double MinimumQualityThreshold { get; set; }
            public ChangeThresholds ChangeDetectionThreshold { get; set; }
            public FallbackFilterSettings FallbackSettings { get; set; }
        }

        /// <summary>
        /// Ortam analiz sonucu;
        /// </summary>
        public class EnvironmentAnalysis;
        {
            public EnvironmentType EnvironmentType { get; set; }
            public NoiseType NoiseType { get; set; }
            public double DecibelLevel { get; set; }
            public FrequencyAnalysis FrequencyProfile { get; set; }
            public bool EnvironmentChanged { get; set; }
            public bool IsCriticalNoise { get; set; }
        }

        /// <summary>
        /// Hızlı ortam analizi;
        /// </summary>
        public class QuickAnalysis;
        {
            public double DecibelLevel { get; set; }
            public bool HasNoiseSpike { get; set; }
            public bool SignificantChange { get; set; }
            public NoiseType DetectedNoiseType { get; set; }
        }

        /// <summary>
        /// Ortam profili;
        /// </summary>
        public class EnvironmentProfile : ICloneable;
        {
            public EnvironmentType EnvironmentType { get; set; }
            public NoiseType PrimaryNoiseType { get; set; }
            public double AverageDecibelLevel { get; set; }
            public FrequencyAnalysis FrequencyProfile { get; set; }
            public DateTime DetectionTime { get; set; }
            public AudioSample[] NoiseSamples { get; set; }

            public static EnvironmentProfile CreateDefault()
            {
                return new EnvironmentProfile;
                {
                    EnvironmentType = EnvironmentType.Unknown,
                    PrimaryNoiseType = NoiseType.Unknown,
                    AverageDecibelLevel = 30.0,
                    DetectionTime = DateTime.UtcNow,
                    NoiseSamples = Array.Empty<AudioSample>()
                };
            }

            public EnvironmentProfile Clone()
            {
                return new EnvironmentProfile;
                {
                    EnvironmentType = this.EnvironmentType,
                    PrimaryNoiseType = this.PrimaryNoiseType,
                    AverageDecibelLevel = this.AverageDecibelLevel,
                    FrequencyProfile = this.FrequencyProfile?.Clone(),
                    DetectionTime = this.DetectionTime,
                    NoiseSamples = this.NoiseSamples?.ToArray()
                };
            }

            object ICloneable.Clone() => Clone();
        }

        /// <summary>
        /// Kalite doğrulama sonucu;
        /// </summary>
        public class QualityVerification;
        {
            public double SignalToNoiseRatio { get; set; }
            public double NaturalnessScore { get; set; }
            public double DistortionLevel { get; set; }
            public double QualityScore { get; set; }
            public bool IsAcceptable { get; set; }
        }

        /// <summary>
        /// Ortam tarihçesi;
        /// </summary>
        public class EnvironmentHistory;
        {
            public List<EnvironmentProfile> Profiles { get; set; }
            public TimeSpan TimeRange { get; set; }
            public int EnvironmentChanges { get; set; }
            public double AverageNoiseLevel { get; set; }
        }

        /// <summary>
        /// Ortam değişikliği olay argümanları;
        /// </summary>
        public class EnvironmentChangedEventArgs : EventArgs;
        {
            public EnvironmentProfile OldProfile { get; set; }
            public EnvironmentProfile NewProfile { get; set; }
            public DateTime ChangeTime { get; set; }
        }

        /// <summary>
        /// Gürültü eşiği aşım olay argümanları;
        /// </summary>
        public class NoiseThresholdExceededEventArgs : EventArgs;
        {
            public double DecibelLevel { get; set; }
            public double Threshold { get; set; }
            public DateTime Timestamp { get; set; }
            public NoiseType NoiseType { get; set; }
        }

        #endregion;

        #region Enum'lar;

        /// <summary>
        /// Ortam türleri;
        /// </summary>
        public enum EnvironmentType;
        {
            Unknown = 0,
            QuietOffice = 1,
            NoisyOffice = 2,
            PublicSpace = 3,
            Transportation = 4,
            Industrial = 5,
            Residential = 6,
            Outdoor = 7;
        }

        /// <summary>
        /// Gürültü türleri;
        /// </summary>
        public enum NoiseType;
        {
            Unknown = 0,
            WhiteNoise = 1,
            PinkNoise = 2,
            LowFrequencyRumble = 3,
            Impulsive = 4,
            Continuous = 5,
            Periodic = 6,
            Transient = 7;
        }

        /// <summary>
        /// Filtre stratejileri;
        /// </summary>
        public enum FilterStrategy;
        {
            LightFiltering = 0,
            ModerateFiltering = 1,
            AggressiveFiltering = 2,
            AdaptiveMultiBand = 3,
            HeavyDuty = 4,
            Balanced = 5;
        }

        /// <summary>
        /// Kalibrasyon modları;
        /// </summary>
        public enum CalibrationMode;
        {
            AutoCalibration = 0,
            ManualCalibration = 1,
            QuickCalibration = 2,
            ResetToDefaults = 3;
        }

        #endregion;
    }

    /// <summary>
    /// Ortam adaptasyonu için arayüz;
    /// </summary>
    public interface IEnvironmentAdapter : IDisposable
    {
        event EventHandler<EnvironmentAdapter.EnvironmentChangedEventArgs> EnvironmentChanged;
        event EventHandler<EnvironmentAdapter.NoiseThresholdExceededEventArgs> NoiseThresholdExceeded;

        AudioBuffer ProcessAudio(AudioBuffer audioInput);
        void Calibrate(EnvironmentAdapter.CalibrationMode mode, AudioBuffer calibrationSample = null);
        void StartContinuousMonitoring();
        EnvironmentAdapter.EnvironmentProfile GetCurrentProfile();
        EnvironmentAdapter.EnvironmentHistory GetHistory(TimeSpan timeRange);
    }

    /// <summary>
    /// Ortam adaptasyonu özel exception'ları;
    /// </summary>
    public class EnvironmentAdaptationException : Exception
    {
        public EnvironmentAdaptationException(string message) : base(message) { }
        public EnvironmentAdaptationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class CalibrationException : EnvironmentAdaptationException;
    {
        public CalibrationException(string message) : base(message) { }
        public CalibrationException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
