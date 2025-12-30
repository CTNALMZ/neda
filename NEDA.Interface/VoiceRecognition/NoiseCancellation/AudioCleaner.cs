using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NEDA.Interface.VoiceRecognition.SpeechToText;
{
    /// <summary>
    /// Provides advanced audio cleaning and noise reduction capabilities for speech processing.
    /// Implements multiple noise reduction algorithms and adaptive filtering techniques.
    /// </summary>
    public class AudioCleaner : IAudioCleaner, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly AudioCleanerConfig _config;
        private bool _isDisposed;

        // Audio processing buffers;
        private float[] _noiseProfile;
        private Queue<float[]> _audioBuffer;
        private readonly object _processingLock = new object();

        /// <summary>
        /// Gets the current cleaning algorithm in use;
        /// </summary>
        public NoiseReductionAlgorithm CurrentAlgorithm { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the cleaner is currently processing audio;
        /// </summary>
        public bool IsProcessing { get; private set; }

        /// <summary>
        /// Event raised when audio processing is completed;
        /// </summary>
        public event EventHandler<AudioProcessingEventArgs> ProcessingCompleted;

        /// <summary>
        /// Event raised when an error occurs during audio processing;
        /// </summary>
        public event EventHandler<AudioProcessingErrorEventArgs> ProcessingError;

        /// <summary>
        /// Initializes a new instance of the AudioCleaner class with default configuration;
        /// </summary>
        /// <param name="logger">Logger instance for recording processing events</param>
        public AudioCleaner(ILogger logger) : this(logger, AudioCleanerConfig.Default)
        {
        }

        /// <summary>
        /// Initializes a new instance of the AudioCleaner class with custom configuration;
        /// </summary>
        /// <param name="logger">Logger instance for recording processing events</param>
        /// <param name="config">Configuration for audio cleaning behavior</param>
        public AudioCleaner(ILogger logger, AudioCleanerConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            Initialize();

            _logger.LogInformation($"AudioCleaner initialized with algorithm: {config.NoiseReductionAlgorithm}",
                component: nameof(AudioCleaner));
        }

        private void Initialize()
        {
            CurrentAlgorithm = _config.NoiseReductionAlgorithm;
            _audioBuffer = new Queue<float[]>(_config.BufferSize);

            if (_config.EnableAdaptiveFiltering)
            {
                InitializeAdaptiveFilter();
            }
        }

        private void InitializeAdaptiveFilter()
        {
            // Initialize adaptive filter coefficients based on configuration;
            _noiseProfile = new float[_config.NoiseProfileLength];

            _logger.LogDebug("Adaptive filter initialized",
                component: nameof(AudioCleaner),
                metadata: new Dictionary<string, object>
                {
                    { "ProfileLength", _config.NoiseProfileLength },
                    { "LearningRate", _config.AdaptiveLearningRate }
                });
        }

        /// <summary>
        /// Cleans audio data using the configured noise reduction algorithm;
        /// </summary>
        /// <param name="audioData">Raw audio data to clean</param>
        /// <param name="sampleRate">Sample rate of the audio data</param>
        /// <returns>Cleaned audio data</returns>
        public float[] CleanAudio(float[] audioData, int sampleRate)
        {
            ValidateNotDisposed();

            if (audioData == null || audioData.Length == 0)
            {
                throw new AudioProcessingException(
                    ErrorCodes.AudioProcessing.InvalidAudioData,
                    "Audio data cannot be null or empty");
            }

            if (sampleRate <= 0)
            {
                throw new AudioProcessingException(
                    ErrorCodes.AudioProcessing.InvalidSampleRate,
                    $"Invalid sample rate: {sampleRate}");
            }

            try
            {
                _logger.LogDebug("Starting audio cleaning process",
                    component: nameof(AudioCleaner),
                    metadata: new Dictionary<string, object>
                    {
                        { "DataLength", audioData.Length },
                        { "SampleRate", sampleRate },
                        { "Algorithm", CurrentAlgorithm.ToString() }
                    });

                IsProcessing = true;

                // Apply selected cleaning algorithm;
                float[] cleanedAudio = CurrentAlgorithm switch;
                {
                    NoiseReductionAlgorithm.SpectralSubtraction => ApplySpectralSubtraction(audioData, sampleRate),
                    NoiseReductionAlgorithm.WienerFilter => ApplyWienerFilter(audioData, sampleRate),
                    NoiseReductionAlgorithm.AdaptiveNoiseCancellation => ApplyAdaptiveNoiseCancellation(audioData, sampleRate),
                    NoiseReductionAlgorithm.WaveletDenoising => ApplyWaveletDenoising(audioData, sampleRate),
                    _ => ApplyDefaultCleaning(audioData, sampleRate)
                };

                // Apply additional processing if enabled;
                if (_config.EnableNormalization)
                {
                    cleanedAudio = NormalizeAudio(cleanedAudio);
                }

                if (_config.EnableDCBiasRemoval)
                {
                    cleanedAudio = RemoveDCBias(cleanedAudio);
                }

                // Update noise profile for adaptive filtering;
                if (_config.EnableAdaptiveFiltering && _config.UpdateNoiseProfile)
                {
                    UpdateNoiseProfile(cleanedAudio);
                }

                _logger.LogInformation("Audio cleaning completed successfully",
                    component: nameof(AudioCleaner),
                    metadata: new Dictionary<string, object>
                    {
                        { "OriginalLength", audioData.Length },
                        { "CleanedLength", cleanedAudio.Length },
                        { "ProcessingTime", DateTime.UtcNow }
                    });

                OnProcessingCompleted(new AudioProcessingEventArgs;
                {
                    OriginalData = audioData,
                    CleanedData = cleanedAudio,
                    SampleRate = sampleRate,
                    AlgorithmUsed = CurrentAlgorithm;
                });

                return cleanedAudio;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during audio cleaning",
                    ex,
                    component: nameof(AudioCleaner),
                    metadata: new Dictionary<string, object>
                    {
                        { "Algorithm", CurrentAlgorithm.ToString() },
                        { "SampleRate", sampleRate }
                    });

                OnProcessingError(new AudioProcessingErrorEventArgs;
                {
                    AudioData = audioData,
                    SampleRate = sampleRate,
                    Error = ex,
                    ErrorCode = ErrorCodes.AudioProcessing.CleaningFailed;
                });

                throw new AudioProcessingException(
                    ErrorCodes.AudioProcessing.CleaningFailed,
                    "Failed to clean audio data",
                    ex);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Cleans audio data in chunks for streaming applications;
        /// </summary>
        /// <param name="audioChunk">Chunk of audio data to clean</param>
        /// <param name="sampleRate">Sample rate of the audio data</param>
        /// <param name="isFinalChunk">Indicates if this is the final chunk</param>
        /// <returns>Cleaned audio chunk</returns>
        public float[] CleanAudioChunk(float[] audioChunk, int sampleRate, bool isFinalChunk = false)
        {
            ValidateNotDisposed();

            lock (_processingLock)
            {
                _audioBuffer.Enqueue(audioChunk);

                if (_audioBuffer.Count >= _config.BufferSize || isFinalChunk)
                {
                    var bufferArray = _audioBuffer.ToArray();
                    var combinedAudio = bufferArray.SelectMany(x => x).ToArray();
                    _audioBuffer.Clear();

                    return CleanAudio(combinedAudio, sampleRate);
                }

                return new float[0]; // Return empty array until buffer is full;
            }
        }

        /// <summary>
        /// Sets a custom noise profile for noise cancellation;
        /// </summary>
        /// <param name="noiseProfile">Noise profile data</param>
        public void SetNoiseProfile(float[] noiseProfile)
        {
            ValidateNotDisposed();

            if (noiseProfile == null || noiseProfile.Length == 0)
            {
                throw new ArgumentException("Noise profile cannot be null or empty", nameof(noiseProfile));
            }

            _noiseProfile = noiseProfile;
            _logger.LogInformation("Noise profile updated",
                component: nameof(AudioCleaner),
                metadata: new Dictionary<string, object>
                {
                    { "ProfileLength", noiseProfile.Length }
                });
        }

        /// <summary>
        /// Captures a noise profile from silent or noise-only audio;
        /// </summary>
        /// <param name="noiseAudio">Audio containing only noise</param>
        /// <param name="sampleRate">Sample rate of the noise audio</param>
        public void LearnNoiseProfile(float[] noiseAudio, int sampleRate)
        {
            ValidateNotDisposed();

            if (noiseAudio == null || noiseAudio.Length == 0)
            {
                throw new ArgumentException("Noise audio cannot be null or empty", nameof(noiseAudio));
            }

            try
            {
                // Calculate noise profile using spectral analysis;
                var spectrum = CalculateSpectrum(noiseAudio);
                _noiseProfile = CalculateNoiseProfile(spectrum);

                _logger.LogInformation("Noise profile learned from audio",
                    component: nameof(AudioCleaner),
                    metadata: new Dictionary<string, object>
                    {
                        { "NoiseAudioLength", noiseAudio.Length },
                        { "SampleRate", sampleRate },
                        { "ProfileLength", _noiseProfile.Length }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to learn noise profile",
                    ex,
                    component: nameof(AudioCleaner));

                throw new AudioProcessingException(
                    ErrorCodes.AudioProcessing.NoiseProfileLearningFailed,
                    "Failed to learn noise profile",
                    ex);
            }
        }

        /// <summary>
        /// Changes the active noise reduction algorithm;
        /// </summary>
        /// <param name="algorithm">Algorithm to use for noise reduction</param>
        public void SetNoiseReductionAlgorithm(NoiseReductionAlgorithm algorithm)
        {
            ValidateNotDisposed();

            CurrentAlgorithm = algorithm;

            _logger.LogInformation($"Noise reduction algorithm changed to: {algorithm}",
                component: nameof(AudioCleaner));
        }

        /// <summary>
        /// Resets the audio cleaner to its initial state;
        /// </summary>
        public void Reset()
        {
            ValidateNotDisposed();

            lock (_processingLock)
            {
                _audioBuffer.Clear();
                _noiseProfile = null;

                if (_config.EnableAdaptiveFiltering)
                {
                    InitializeAdaptiveFilter();
                }
            }

            _logger.LogInformation("Audio cleaner reset to initial state",
                component: nameof(AudioCleaner));
        }

        #region Processing Algorithms;

        private float[] ApplySpectralSubtraction(float[] audioData, int sampleRate)
        {
            // Implement spectral subtraction algorithm;
            var spectrum = CalculateSpectrum(audioData);
            var noiseSpectrum = _noiseProfile ?? CalculateNoiseProfile(spectrum);

            // Subtract noise spectrum from audio spectrum;
            var cleanedSpectrum = new float[spectrum.Length];
            for (int i = 0; i < spectrum.Length; i++)
            {
                cleanedSpectrum[i] = Math.Max(0, spectrum[i] - noiseSpectrum[i % noiseSpectrum.Length] * _config.NoiseReductionFactor);
            }

            return InverseSpectrum(cleanedSpectrum, audioData.Length);
        }

        private float[] ApplyWienerFilter(float[] audioData, int sampleRate)
        {
            // Implement Wiener filter for optimal noise reduction;
            var spectrum = CalculateSpectrum(audioData);
            var noiseSpectrum = _noiseProfile ?? CalculateNoiseProfile(spectrum);

            var cleanedSpectrum = new float[spectrum.Length];
            for (int i = 0; i < spectrum.Length; i++)
            {
                var signalPower = spectrum[i] * spectrum[i];
                var noisePower = noiseSpectrum[i % noiseSpectrum.Length] * noiseSpectrum[i % noiseSpectrum.Length];
                var snr = signalPower / (noisePower + float.Epsilon);

                // Wiener filter transfer function;
                var filterGain = snr / (snr + 1);
                cleanedSpectrum[i] = spectrum[i] * filterGain;
            }

            return InverseSpectrum(cleanedSpectrum, audioData.Length);
        }

        private float[] ApplyAdaptiveNoiseCancellation(float[] audioData, int sampleRate)
        {
            // Implement adaptive LMS filter for noise cancellation;
            var result = new float[audioData.Length];
            var filterCoefficients = new float[_config.AdaptiveFilterOrder];

            for (int i = 0; i < audioData.Length; i++)
            {
                // Adaptive filter implementation;
                var estimatedNoise = 0f;
                for (int j = 0; j < filterCoefficients.Length && i - j >= 0; j++)
                {
                    estimatedNoise += filterCoefficients[j] * audioData[i - j];
                }

                result[i] = audioData[i] - estimatedNoise;

                // Update filter coefficients using LMS algorithm;
                if (i >= filterCoefficients.Length)
                {
                    for (int j = 0; j < filterCoefficients.Length; j++)
                    {
                        filterCoefficients[j] += _config.AdaptiveLearningRate * result[i] * audioData[i - j];
                    }
                }
            }

            return result;
        }

        private float[] ApplyWaveletDenoising(float[] audioData, int sampleRate)
        {
            // Implement wavelet-based denoising;
            // This is a simplified implementation - production would use proper wavelet transform;
            var result = new float[audioData.Length];
            Array.Copy(audioData, result, audioData.Length);

            // Simple thresholding for demonstration;
            var threshold = CalculateNoiseThreshold(result);
            for (int i = 0; i < result.Length; i++)
            {
                if (Math.Abs(result[i]) < threshold)
                {
                    result[i] = 0;
                }
            }

            return result;
        }

        private float[] ApplyDefaultCleaning(float[] audioData, int sampleRate)
        {
            // Default cleaning: simple high-pass filter to remove low-frequency noise;
            var result = new float[audioData.Length];
            var alpha = 0.95f; // Filter coefficient;

            result[0] = audioData[0];
            for (int i = 1; i < audioData.Length; i++)
            {
                result[i] = alpha * (result[i - 1] + audioData[i] - audioData[i - 1]);
            }

            return result;
        }

        #endregion;

        #region Audio Processing Utilities;

        private float[] NormalizeAudio(float[] audioData)
        {
            if (audioData.Length == 0) return audioData;

            var maxAmplitude = audioData.Max(x => Math.Abs(x));
            if (maxAmplitude < float.Epsilon) return audioData;

            var normalizationFactor = _config.TargetAmplitude / maxAmplitude;
            var normalized = new float[audioData.Length];

            for (int i = 0; i < audioData.Length; i++)
            {
                normalized[i] = audioData[i] * normalizationFactor;
            }

            return normalized;
        }

        private float[] RemoveDCBias(float[] audioData)
        {
            if (audioData.Length == 0) return audioData;

            var mean = audioData.Average();
            var result = new float[audioData.Length];

            for (int i = 0; i < audioData.Length; i++)
            {
                result[i] = audioData[i] - mean;
            }

            return result;
        }

        private float[] CalculateSpectrum(float[] audioData)
        {
            // Simplified FFT implementation - production would use optimized FFT;
            var n = audioData.Length;
            var spectrum = new float[n];

            // For production, use a proper FFT library like MathNet.Numerics;
            // This is a placeholder implementation;
            Array.Copy(audioData, spectrum, n);

            return spectrum;
        }

        private float[] InverseSpectrum(float[] spectrum, int targetLength)
        {
            // Inverse of CalculateSpectrum - placeholder implementation;
            var result = new float[targetLength];
            var minLength = Math.Min(spectrum.Length, targetLength);
            Array.Copy(spectrum, result, minLength);

            return result;
        }

        private float[] CalculateNoiseProfile(float[] spectrum)
        {
            // Calculate average noise profile from spectrum;
            var profileLength = Math.Min(spectrum.Length, _config.NoiseProfileLength);
            var profile = new float[profileLength];

            var segmentSize = spectrum.Length / profileLength;
            for (int i = 0; i < profileLength; i++)
            {
                var start = i * segmentSize;
                var end = Math.Min(start + segmentSize, spectrum.Length);
                var sum = 0f;

                for (int j = start; j < end; j++)
                {
                    sum += Math.Abs(spectrum[j]);
                }

                profile[i] = sum / (end - start);
            }

            return profile;
        }

        private float CalculateNoiseThreshold(float[] audioData)
        {
            // Calculate noise threshold using median absolute deviation;
            var deviations = audioData.Select(x => Math.Abs(x)).ToArray();
            Array.Sort(deviations);

            var median = deviations[deviations.Length / 2];
            return median * _config.WaveletThresholdFactor;
        }

        private void UpdateNoiseProfile(float[] audioData)
        {
            if (_noiseProfile == null || audioData.Length == 0) return;

            var spectrum = CalculateSpectrum(audioData);
            var newProfile = CalculateNoiseProfile(spectrum);

            // Exponential moving average update;
            for (int i = 0; i < Math.Min(_noiseProfile.Length, newProfile.Length); i++)
            {
                _noiseProfile[i] = _config.NoiseProfileUpdateAlpha * newProfile[i] +
                                 (1 - _config.NoiseProfileUpdateAlpha) * _noiseProfile[i];
            }
        }

        #endregion;

        #region Event Handling;

        protected virtual void OnProcessingCompleted(AudioProcessingEventArgs e)
        {
            ProcessingCompleted?.Invoke(this, e);
        }

        protected virtual void OnProcessingError(AudioProcessingErrorEventArgs e)
        {
            ProcessingError?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(AudioCleaner));
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
                    // Clean up managed resources;
                    _audioBuffer?.Clear();
                    _noiseProfile = null;
                }

                _isDisposed = true;

                _logger.LogInformation("AudioCleaner disposed",
                    component: nameof(AudioCleaner));
            }
        }

        ~AudioCleaner()
        {
            Dispose(false);
        }

        #endregion;
    }

    /// <summary>
    /// Configuration for AudioCleaner behavior;
    /// </summary>
    public class AudioCleanerConfig;
    {
        public NoiseReductionAlgorithm NoiseReductionAlgorithm { get; set; } = NoiseReductionAlgorithm.WienerFilter;
        public float NoiseReductionFactor { get; set; } = 1.2f;
        public int BufferSize { get; set; } = 10;
        public bool EnableAdaptiveFiltering { get; set; } = true;
        public bool UpdateNoiseProfile { get; set; } = true;
        public int NoiseProfileLength { get; set; } = 256;
        public float AdaptiveLearningRate { get; set; } = 0.01f;
        public int AdaptiveFilterOrder { get; set; } = 32;
        public bool EnableNormalization { get; set; } = true;
        public float TargetAmplitude { get; set; } = 0.8f;
        public bool EnableDCBiasRemoval { get; set; } = true;
        public float WaveletThresholdFactor { get; set; } = 0.5f;
        public float NoiseProfileUpdateAlpha { get; set; } = 0.1f;

        public static AudioCleanerConfig Default => new AudioCleanerConfig();
    }

    /// <summary>
    /// Noise reduction algorithms supported by AudioCleaner;
    /// </summary>
    public enum NoiseReductionAlgorithm;
    {
        SpectralSubtraction,
        WienerFilter,
        AdaptiveNoiseCancellation,
        WaveletDenoising,
        Default;
    }

    /// <summary>
    /// Event arguments for audio processing completion;
    /// </summary>
    public class AudioProcessingEventArgs : EventArgs;
    {
        public float[] OriginalData { get; set; }
        public float[] CleanedData { get; set; }
        public int SampleRate { get; set; }
        public NoiseReductionAlgorithm AlgorithmUsed { get; set; }
        public DateTime ProcessingTimestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Event arguments for audio processing errors;
    /// </summary>
    public class AudioProcessingErrorEventArgs : EventArgs;
    {
        public float[] AudioData { get; set; }
        public int SampleRate { get; set; }
        public Exception Error { get; set; }
        public string ErrorCode { get; set; }
        public DateTime ErrorTimestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Interface for audio cleaning functionality;
    /// </summary>
    public interface IAudioCleaner;
    {
        float[] CleanAudio(float[] audioData, int sampleRate);
        float[] CleanAudioChunk(float[] audioChunk, int sampleRate, bool isFinalChunk);
        void SetNoiseProfile(float[] noiseProfile);
        void LearnNoiseProfile(float[] noiseAudio, int sampleRate);
        void SetNoiseReductionAlgorithm(NoiseReductionAlgorithm algorithm);
        void Reset();

        event EventHandler<AudioProcessingEventArgs> ProcessingCompleted;
        event EventHandler<AudioProcessingErrorEventArgs> ProcessingError;
    }

    /// <summary>
    /// Custom exception for audio processing errors;
    /// </summary>
    public class AudioProcessingException : Exception
    {
        public string ErrorCode { get; }

        public AudioProcessingException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public AudioProcessingException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}
