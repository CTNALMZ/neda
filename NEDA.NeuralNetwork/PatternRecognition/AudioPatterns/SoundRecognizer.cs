using Accord.Audio;
using Accord.Audio.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NEDA.Biometrics.VoiceIdentification;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.CharacterSystems.CharacterCreator.AppearanceCustomization;
using NEDA.Communication.MultiModalCommunication.VoiceGestures;
using NEDA.ContentCreation.AssetPipeline.FormatConverters;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Interface.VoiceRecognition.AccentAdaptation;
using NEDA.MediaProcessing.AudioProcessing;
using NEDA.MediaProcessing.AudioProcessing.AudioImplementation;
using NEDA.MotionTracking;
using NEDA.NeuralNetwork.Common;
using NEDA.NeuralNetwork.DeepLearning.TransferLearning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.MediaProcessing.AudioProcessing.AudioImplementation.AudioManager;
using static NEDA.MediaProcessing.AudioProcessing.VoiceProcessing.SpeechProcessor;

namespace NEDA.NeuralNetwork.PatternRecognition.AudioPatterns;
{
    /// <summary>
    /// Ses tanıma motoru - Akustik desen tanıma, ses sınıflandırma ve gerçek zamanlı analiz;
    /// Profesyonel ses işleme ve makine öğrenimi tekniklerini birleştirir;
    /// </summary>
    public class SoundRecognizer : ISoundRecognizer, IDisposable;
    {
        private readonly ILogger<SoundRecognizer> _logger;
        private readonly SoundRecognitionConfig _config;
        private readonly IAudioFeatureExtractor _featureExtractor;
        private readonly IAudioModel _recognitionModel;
        private readonly IAudioPreprocessor _preprocessor;
        private readonly IAudioClassifier _classifier;
        private readonly WaveFormat _targetWaveFormat;
        private readonly List<SoundPattern> _registeredPatterns;
        private readonly Dictionary<string, SoundProfile> _soundProfiles;
        private readonly object _recognitionLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        private CancellationTokenSource _recognitionCancellationSource;
        private RealTimeRecognitionEngine _realTimeEngine;
        private SoundDatabase _soundDatabase;

        /// <summary>
        /// Ses tanıyıcıyı başlatır;
        /// </summary>
        public SoundRecognizer(
            ILogger<SoundRecognizer> logger,
            IOptions<SoundRecognitionConfig> config,
            IAudioFeatureExtractor featureExtractor = null,
            IAudioModel recognitionModel = null,
            IAudioPreprocessor preprocessor = null,
            IAudioClassifier classifier = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? SoundRecognitionConfig.Default;

            _featureExtractor = featureExtractor ?? new AudioFeatureExtractor();
            _preprocessor = preprocessor ?? new AudioPreprocessor();
            _classifier = classifier ?? new AudioClassifier();
            _recognitionModel = recognitionModel ?? LoadDefaultModel();

            _targetWaveFormat = new WaveFormat(
                _config.SampleRate,
                _config.BitsPerSample,
                _config.Channels);

            _registeredPatterns = new List<SoundPattern>();
            _soundProfiles = new Dictionary<string, SoundProfile>();
            _soundDatabase = new SoundDatabase();

            _logger.LogInformation("SoundRecognizer initialized. Sample Rate: {SampleRate}, Channels: {Channels}",
                _config.SampleRate, _config.Channels);
        }

        /// <summary>
        /// Asenkron başlatma işlemi;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                await ValidateConfigurationAsync();
                await LoadSoundDatabaseAsync();
                await InitializeRecognitionModelAsync();
                await InitializeRealTimeEngineAsync();

                _isInitialized = true;
                _logger.LogInformation("SoundRecognizer initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SoundRecognizer");
                throw new SoundRecognizerInitializationException(
                    "SoundRecognizer initialization failed", ex);
            }
        }

        /// <summary>
        /// Ses dosyasından desen tanıma yapar;
        /// </summary>
        public async Task<SoundRecognitionResult> RecognizeFromFileAsync(
            string filePath,
            RecognitionOptions options = null)
        {
            ValidateFile(filePath);
            options ??= RecognitionOptions.Default;

            using var audioData = await LoadAudioFileAsync(filePath);
            return await RecognizeAudioDataAsync(audioData, options);
        }

        /// <summary>
        /// Ham ses verisinden tanıma yapar;
        /// </summary>
        public async Task<SoundRecognitionResult> RecognizeFromBytesAsync(
            byte[] audioBytes,
            AudioFormat format,
            RecognitionOptions options = null)
        {
            ValidateAudioBytes(audioBytes);
            options ??= RecognitionOptions.Default;

            using var audioData = await ConvertBytesToAudioDataAsync(audioBytes, format);
            return await RecognizeAudioDataAsync(audioData, options);
        }

        /// <summary>
        /// Gerçek zamanlı ses akışından tanıma başlatır;
        /// </summary>
        public async Task StartRealTimeRecognitionAsync(
            IAudioStream audioStream,
            RealTimeRecognitionOptions options)
        {
            ValidateRealTimeOptions(options);

            if (_realTimeEngine?.IsRunning == true)
                throw new InvalidOperationException("Real-time recognition is already running");

            _recognitionCancellationSource = new CancellationTokenSource();

            _realTimeEngine = new RealTimeRecognitionEngine(
                audioStream,
                options,
                _featureExtractor,
                _classifier,
                _logger);

            _realTimeEngine.SoundRecognized += OnSoundRecognized;
            _realTimeEngine.RecognitionError += OnRecognitionError;

            await _realTimeEngine.StartAsync(_recognitionCancellationSource.Token);

            _logger.LogInformation("Real-time sound recognition started");
        }

        /// <summary>
        /// Gerçek zamanlı tanımayı durdurur;
        /// </summary>
        public async Task StopRealTimeRecognitionAsync()
        {
            if (_realTimeEngine == null || !_realTimeEngine.IsRunning)
                return;

            _recognitionCancellationSource?.Cancel();
            await _realTimeEngine.StopAsync();

            _realTimeEngine.SoundRecognized -= OnSoundRecognized;
            _realTimeEngine.RecognitionError -= OnRecognitionError;

            _logger.LogInformation("Real-time sound recognition stopped");
        }

        /// <summary>
        /// Yeni ses deseni kaydeder;
        /// </summary>
        public async Task<SoundPattern> RegisterSoundPatternAsync(
            string patternName,
            IEnumerable<AudioSample> trainingSamples,
            SoundPatternMetadata metadata = null)
        {
            ValidatePatternRegistration(patternName, trainingSamples);

            var samples = trainingSamples.ToArray();
            var features = await ExtractTrainingFeaturesAsync(samples);
            var pattern = CreateSoundPattern(patternName, features, metadata);

            lock (_recognitionLock)
            {
                _registeredPatterns.Add(pattern);
                _soundDatabase.AddPattern(pattern);
            }

            await TrainClassifierWithNewPatternAsync(pattern);

            _logger.LogInformation("Sound pattern registered: {PatternName} with {SampleCount} samples",
                patternName, samples.Length);

            return pattern;
        }

        /// <summary>
        /// Ses desenini siler;
        /// </summary>
        public async Task<bool> RemoveSoundPatternAsync(string patternId)
        {
            lock (_recognitionLock)
            {
                var pattern = _registeredPatterns.FirstOrDefault(p => p.Id == patternId);
                if (pattern == null)
                    return false;

                _registeredPatterns.Remove(pattern);
                _soundDatabase.RemovePattern(patternId);
            }

            await RetrainClassifierAfterRemovalAsync(patternId);

            _logger.LogInformation("Sound pattern removed: {PatternId}", patternId);
            return true;
        }

        /// <summary>
        /// Ses sınıflandırma modelini eğitir;
        /// </summary>
        public async Task<TrainingResult> TrainModelAsync(
            IEnumerable<SoundTrainingSample> trainingData,
            ModelTrainingOptions options = null)
        {
            ValidateTrainingData(trainingData);
            options ??= ModelTrainingOptions.Default;

            var trainingSamples = trainingData.ToArray();
            var validationSet = SplitValidationSet(trainingSamples, options.ValidationSplit);

            var trainingContext = new TrainingContext;
            {
                TrainingSamples = trainingSamples,
                ValidationSamples = validationSet,
                Options = options;
            };

            var result = await PerformTrainingAsync(trainingContext);

            _logger.LogInformation("Model training completed. Accuracy: {Accuracy:F2}%, Loss: {Loss:F4}",
                result.Accuracy * 100, result.Loss);

            return result;
        }

        /// <summary>
        /// Transfer learning ile modeli geliştirir;
        /// </summary>
        public async Task<TransferLearningResult> ApplyTransferLearningAsync(
            IEnumerable<SoundTrainingSample> newSamples,
            TransferLearningOptions options)
        {
            ValidateTransferLearning(newSamples, options);

            var result = await PerformTransferLearningAsync(newSamples.ToArray(), options);

            _logger.LogInformation("Transfer learning applied. Improvement: {Improvement:F2}%",
                result.Improvement * 100);

            return result;
        }

        /// <summary>
        /// Ses benzerlik analizi yapar;
        /// </summary>
        public async Task<SimilarityAnalysisResult> AnalyzeSimilarityAsync(
            AudioSample sample1,
            AudioSample sample2,
            SimilarityMetric metric = SimilarityMetric.Cosine)
        {
            var features1 = await ExtractFeaturesAsync(sample1);
            var features2 = await ExtractFeaturesAsync(sample2);

            var similarity = CalculateSimilarity(features1, features2, metric);
            var confidence = CalculateSimilarityConfidence(similarity, features1, features2);

            return new SimilarityAnalysisResult;
            {
                SimilarityScore = similarity,
                Confidence = confidence,
                FeatureCorrelation = CalculateFeatureCorrelation(features1, features2),
                IsMatch = similarity >= _config.SimilarityThreshold;
            };
        }

        /// <summary>
        /// Ses kalitesi analizi yapar;
        /// </summary>
        public async Task<AudioQualityResult> AnalyzeAudioQualityAsync(
            AudioSample audioSample,
            QualityMetrics metrics = QualityMetrics.All)
        {
            var signal = await ConvertToSignalAsync(audioSample);
            var result = new AudioQualityResult();

            if (metrics.HasFlag(QualityMetrics.SNR))
                result.SignalToNoiseRatio = CalculateSNR(signal);

            if (metrics.HasFlag(QualityMetrics.Distortion))
                result.DistortionLevel = CalculateDistortion(signal);

            if (metrics.HasFlag(QualityMetrics.DynamicRange))
                result.DynamicRange = CalculateDynamicRange(signal);

            if (metrics.HasFlag(QualityMetrics.FrequencyResponse))
                result.FrequencyResponse = AnalyzeFrequencyResponse(signal);

            result.OverallQuality = CalculateOverallQuality(result);

            return result;
        }

        /// <summary>
        /// Çevresel ses analizi yapar;
        /// </summary>
        public async Task<EnvironmentalAnalysisResult> AnalyzeEnvironmentAsync(
            AudioSample environmentSample,
            EnvironmentAnalysisOptions options = null)
        {
            options ??= EnvironmentAnalysisOptions.Default;

            var features = await ExtractEnvironmentalFeaturesAsync(environmentSample);
            var classification = await ClassifyEnvironmentAsync(features);
            var noiseProfile = AnalyzeNoiseProfile(environmentSample);

            return new EnvironmentalAnalysisResult;
            {
                EnvironmentType = classification,
                NoiseLevel = noiseProfile.NoiseLevel,
                BackgroundSounds = noiseProfile.BackgroundSounds,
                AcousticCharacteristics = features.AcousticFeatures,
                Recommendations = GenerateEnvironmentRecommendations(classification, noiseProfile)
            };
        }

        /// <summary>
        /// Anomali tespiti yapar;
        /// </summary>
        public async Task<AnomalyDetectionResult> DetectAnomaliesAsync(
            AudioSample audioSample,
            AnomalyDetectionOptions options = null)
        {
            options ??= AnomalyDetectionOptions.Default;

            var features = await ExtractFeaturesAsync(audioSample);
            var reconstructionError = await CalculateReconstructionErrorAsync(features);

            var isAnomaly = reconstructionError > options.AnomalyThreshold;
            var anomalyScore = CalculateAnomalyScore(reconstructionError, features);

            var result = new AnomalyDetectionResult;
            {
                IsAnomaly = isAnomaly,
                AnomalyScore = anomalyScore,
                ReconstructionError = reconstructionError,
                DetectedFeatures = features,
                Timestamp = DateTime.UtcNow;
            };

            if (isAnomaly && options.StoreAnomalies)
                await StoreAnomalyAsync(result);

            return result;
        }

        /// <summary>
        /// Ses sentezi için model oluşturur;
        /// </summary>
        public async Task<SynthesisModel> CreateSynthesisModelAsync(
            IEnumerable<AudioSample> voiceSamples,
            SynthesisOptions options = null)
        {
            ValidateSynthesisSamples(voiceSamples);
            options ??= SynthesisOptions.Default;

            var samples = voiceSamples.ToArray();
            var voiceFeatures = await ExtractVoiceFeaturesAsync(samples);
            var model = await TrainSynthesisModelAsync(voiceFeatures, options);

            _logger.LogInformation("Synthesis model created with {SampleCount} samples", samples.Length);

            return model;
        }

        #region Private Implementation;

        private async Task<SoundRecognitionResult> RecognizeAudioDataAsync(
            AudioData audioData,
            RecognitionOptions options)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Ön işleme;
            var processedAudio = await _preprocessor.ProcessAsync(audioData, options.Preprocessing);

            // Özellik çıkarımı;
            var features = await _featureExtractor.ExtractAsync(processedAudio, options.FeatureExtraction);

            // Sınıflandırma;
            var classification = await _classifier.ClassifyAsync(features, options.Classification);

            // Güven skoru hesaplama;
            var confidence = CalculateRecognitionConfidence(classification, features);

            stopwatch.Stop();

            var result = new SoundRecognitionResult;
            {
                RecognizedSound = classification.TopCategory,
                Confidence = confidence,
                AllProbabilities = classification.Probabilities,
                Features = features,
                ProcessingTime = stopwatch.Elapsed,
                Timestamp = DateTime.UtcNow,
                Metadata = new RecognitionMetadata;
                {
                    AudioDuration = audioData.Duration,
                    SampleRate = audioData.Format.SampleRate,
                    ChannelCount = audioData.Format.Channels;
                }
            };

            // Sonuç iyileştirme;
            if (options.ApplyPostProcessing)
                result = ApplyPostProcessing(result);

            _logger.LogDebug("Recognition completed in {ProcessingTime}ms with confidence: {Confidence:F2}",
                result.ProcessingTime.TotalMilliseconds, result.Confidence);

            return result;
        }

        private async Task<AudioData> LoadAudioFileAsync(string filePath)
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                var audioData = new AudioData;
                {
                    Format = reader.WaveFormat,
                    Samples = await ReadAllSamplesAsync(reader),
                    Duration = reader.TotalTime,
                    Source = AudioSource.File,
                    FilePath = filePath;
                };

                // Gerekirse format dönüşümü;
                if (!IsCompatibleFormat(audioData.Format))
                {
                    audioData = await ConvertAudioFormatAsync(audioData, _targetWaveFormat);
                }

                return audioData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load audio file: {FilePath}", filePath);
                throw new AudioLoadException($"Failed to load audio file: {filePath}", ex);
            }
        }

        private async Task<float[]> ReadAllSamplesAsync(AudioFileReader reader)
        {
            var sampleCount = (int)(reader.Length / (reader.WaveFormat.BitsPerSample / 8));
            var samples = new float[sampleCount];

            await Task.Run(() => reader.Read(samples, 0, sampleCount));

            return samples;
        }

        private async Task<AudioData> ConvertBytesToAudioDataAsync(
            byte[] audioBytes,
            AudioFormat format)
        {
            using var memoryStream = new MemoryStream(audioBytes);
            return await ConvertStreamToAudioDataAsync(memoryStream, format);
        }

        private async Task<FeatureSet> ExtractTrainingFeaturesAsync(AudioSample[] samples)
        {
            var tasks = samples.Select(sample =>
                _featureExtractor.ExtractAsync(sample, new FeatureExtractionOptions;
                {
                    ExtractMFCC = true,
                    ExtractSpectrogram = true,
                    ExtractChromagram = true,
                    ExtractZeroCrossingRate = true,
                    ExtractSpectralContrast = true;
                }));

            var featureSets = await Task.WhenAll(tasks);

            return FeatureSet.Combine(featureSets);
        }

        private SoundPattern CreateSoundPattern(
            string name,
            FeatureSet features,
            SoundPatternMetadata metadata)
        {
            return new SoundPattern;
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Features = features,
                FeatureVector = features.ToVector(),
                CreatedAt = DateTime.UtcNow,
                Metadata = metadata ?? new SoundPatternMetadata(),
                Statistics = CalculatePatternStatistics(features)
            };
        }

        private async Task InitializeRecognitionModelAsync()
        {
            if (_recognitionModel == null)
                throw new InvalidOperationException("Recognition model is not set");

            await _recognitionModel.InitializeAsync();

            if (_config.UsePreTrainedModel && !string.IsNullOrEmpty(_config.ModelPath))
            {
                await _recognitionModel.LoadAsync(_config.ModelPath);
                _logger.LogDebug("Pre-trained model loaded from: {ModelPath}", _config.ModelPath);
            }
        }

        private async Task InitializeRealTimeEngineAsync()
        {
            _realTimeEngine = new RealTimeRecognitionEngine(
                _config.RealTimeBufferSize,
                _config.RealTimeOverlap,
                _featureExtractor,
                _classifier,
                _logger);
        }

        private void OnSoundRecognized(object sender, SoundRecognizedEventArgs e)
        {
            // Ses tanıma olayını işle;
            var recognitionEvent = new SoundRecognitionEvent;
            {
                SoundType = e.RecognitionResult.RecognizedSound,
                Confidence = e.RecognitionResult.Confidence,
                Timestamp = e.Timestamp,
                AudioSegment = e.AudioSegment,
                Context = e.Context;
            };

            // Event'i yayınla;
            SoundRecognized?.Invoke(this, recognitionEvent);

            // Gerekirse kaydet;
            if (_config.LogRecognitionEvents)
            {
                _logger.LogInformation("Sound recognized: {SoundType} (Confidence: {Confidence:F2})",
                    recognitionEvent.SoundType, recognitionEvent.Confidence);
            }
        }

        private void OnRecognitionError(object sender, RecognitionErrorEventArgs e)
        {
            _logger.LogError(e.Exception, "Recognition error occurred: {ErrorMessage}", e.ErrorMessage);
            RecognitionError?.Invoke(this, e);
        }

        private double CalculateRecognitionConfidence(
            ClassificationResult classification,
            FeatureSet features)
        {
            var baseConfidence = classification.Confidence;

            // Özellik kalitesine göre ayarlama;
            var featureQuality = CalculateFeatureQuality(features);
            var adjustedConfidence = baseConfidence * featureQuality;

            // Sınıflandırma belirsizliğini hesaba kat;
            var uncertainty = CalculateUncertainty(classification.Probabilities);
            adjustedConfidence *= (1 - uncertainty);

            return Math.Clamp(adjustedConfidence, 0, 1);
        }

        private double CalculateSimilarity(
            FeatureSet features1,
            FeatureSet features2,
            SimilarityMetric metric)
        {
            return metric switch;
            {
                SimilarityMetric.Cosine => CalculateCosineSimilarity(features1.Vector, features2.Vector),
                SimilarityMetric.Euclidean => 1 / (1 + CalculateEuclideanDistance(features1.Vector, features2.Vector)),
                SimilarityMetric.Pearson => CalculatePearsonCorrelation(features1.Vector, features2.Vector),
                _ => CalculateCosineSimilarity(features1.Vector, features2.Vector)
            };
        }

        private async Task<double> CalculateReconstructionErrorAsync(FeatureSet features)
        {
            if (_recognitionModel is IReconstructiveModel reconstructiveModel)
            {
                var reconstructed = await reconstructiveModel.ReconstructAsync(features.Vector);
                return CalculateReconstructionError(features.Vector, reconstructed);
            }

            return await CalculateStatisticalReconstructionErrorAsync(features);
        }

        #endregion;

        #region Validation Methods;

        private async Task ValidateConfigurationAsync()
        {
            if (_config.SampleRate < 8000 || _config.SampleRate > 192000)
                throw new InvalidConfigurationException("Sample rate must be between 8000 and 192000 Hz");

            if (_config.Channels < 1 || _config.Channels > 8)
                throw new InvalidConfigurationException("Channel count must be between 1 and 8");

            if (_config.MinimumAudioDuration < TimeSpan.FromMilliseconds(100))
                throw new InvalidConfigurationException("Minimum audio duration must be at least 100ms");

            await Task.CompletedTask;
        }

        private void ValidateFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Audio file not found: {filePath}");

            var extension = Path.GetExtension(filePath).ToLower();
            if (!_config.SupportedFormats.Contains(extension))
                throw new UnsupportedFormatException($"Unsupported audio format: {extension}");
        }

        private void ValidateAudioBytes(byte[] audioBytes)
        {
            if (audioBytes == null || audioBytes.Length == 0)
                throw new ArgumentException("Audio bytes cannot be null or empty", nameof(audioBytes));

            if (audioBytes.Length < _config.MinimumAudioSize)
                throw new InsufficientAudioDataException(
                    $"Audio data too small. Minimum size: {_config.MinimumAudioSize} bytes");
        }

        #endregion;

        #region Events;

        /// <summary>
        /// Ses tanındığında tetiklenen olay;
        /// </summary>
        public event EventHandler<SoundRecognitionEvent> SoundRecognized;

        /// <summary>
        /// Tanıma hatası oluştuğunda tetiklenen olay;
        /// </summary>
        public event EventHandler<RecognitionErrorEventArgs> RecognitionError;

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _recognitionCancellationSource?.Dispose();
                    _realTimeEngine?.Dispose();
                    _soundDatabase?.Dispose();
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Ses tanıma yapılandırması;
    /// </summary>
    public class SoundRecognitionConfig;
    {
        public int SampleRate { get; set; } = 44100;
        public int BitsPerSample { get; set; } = 16;
        public int Channels { get; set; } = 1;
        public int RealTimeBufferSize { get; set; } = 4096;
        public int RealTimeOverlap { get; set; } = 1024;
        public float SimilarityThreshold { get; set; } = 0.7f;
        public int MinimumAudioSize { get; set; } = 1024;
        public TimeSpan MinimumAudioDuration { get; set; } = TimeSpan.FromMilliseconds(500);
        public string[] SupportedFormats { get; set; } = { ".wav", ".mp3", ".flac", ".ogg", ".m4a" };
        public string ModelPath { get; set; }
        public bool UsePreTrainedModel { get; set; } = true;
        public bool LogRecognitionEvents { get; set; } = true;
        public int MaxRegisteredPatterns { get; set; } = 1000;

        public static SoundRecognitionConfig Default => new SoundRecognitionConfig();
    }

    /// <summary>
    /// Ses tanıma sonucu;
    /// </summary>
    public class SoundRecognitionResult;
    {
        public string RecognizedSound { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> AllProbabilities { get; set; }
        public FeatureSet Features { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public RecognitionMetadata Metadata { get; set; }
        public bool IsAmbiguous => Confidence < 0.7;
    }

    /// <summary>
    /// Gerçek zamanlı tanıma seçenekleri;
    /// </summary>
    public class RealTimeRecognitionOptions;
    {
        public int BufferDurationMs { get; set; } = 100;
        public int OverlapPercentage { get; set; } = 50;
        public double MinimumConfidence { get; set; } = 0.5;
        public bool ContinuousRecognition { get; set; } = true;
        public string[] SoundsToDetect { get; set; }
        public TimeSpan MaxSilenceDuration { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Ses deseni;
    /// </summary>
    public class SoundPattern;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public FeatureSet Features { get; set; }
        public double[] FeatureVector { get; set; }
        public DateTime CreatedAt { get; set; }
        public SoundPatternMetadata Metadata { get; set; }
        public PatternStatistics Statistics { get; set; }
    }

    /// <summary>
    /// Eğitim sonucu;
    /// </summary>
    public class TrainingResult;
    {
        public double Accuracy { get; set; }
        public double Loss { get; set; }
        public double ValidationAccuracy { get; set; }
        public TimeSpan TrainingDuration { get; set; }
        public int Epochs { get; set; }
        public Dictionary<string, double> ClassMetrics { get; set; }
        public string ModelPath { get; set; }
    }

    /// <summary>
    /// Benzerlik analiz sonucu;
    /// </summary>
    public class SimilarityAnalysisResult;
    {
        public double SimilarityScore { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> FeatureCorrelation { get; set; }
        public bool IsMatch { get; set; }
        public SimilarityLevel SimilarityLevel { get; set; }
    }

    /// <summary>
    /// Ses kalitesi sonucu;
    /// </summary>
    public class AudioQualityResult;
    {
        public double SignalToNoiseRatio { get; set; }
        public double DistortionLevel { get; set; }
        public double DynamicRange { get; set; }
        public FrequencyResponse FrequencyResponse { get; set; }
        public double OverallQuality { get; set; }
        public QualityGrade Grade { get; set; }
    }

    /// <summary>
    /// Çevresel analiz sonucu;
    /// </summary>
    public class EnvironmentalAnalysisResult;
    {
        public EnvironmentType EnvironmentType { get; set; }
        public double NoiseLevel { get; set; }
        public string[] BackgroundSounds { get; set; }
        public AcousticFeatures AcousticCharacteristics { get; set; }
        public string[] Recommendations { get; set; }
    }

    /// <summary>
    /// Anomali tespit sonucu;
    /// </summary>
    public class AnomalyDetectionResult;
    {
        public bool IsAnomaly { get; set; }
        public double AnomalyScore { get; set; }
        public double ReconstructionError { get; set; }
        public FeatureSet DetectedFeatures { get; set; }
        public DateTime Timestamp { get; set; }
        public string AnomalyType { get; set; }
    }

    #endregion;
}
