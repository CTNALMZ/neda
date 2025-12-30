using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.Interface.VoiceRecognition.VoiceActivityDetection.Configuration;
using NEDA.Interface.VoiceRecognition.VoiceActivityDetection.Events;
using NEDA.Interface.VoiceRecognition.VoiceActivityDetection.Models;
using NEDA.Interface.VoiceRecognition.VoiceActivityDetection.Analyzers;
using NEDA.Interface.VoiceRecognition.VoiceActivityDetection.Exceptions;

namespace NEDA.Interface.VoiceRecognition.VoiceActivityDetection;
{
    /// <summary>
    Advanced silence detection engine with adaptive thresholding and noise profiling;
    /// Supports multiple detection algorithms and real-time audio stream analysis;
    /// </summary>
    public class SilenceDetector : ISilenceDetector, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger<SilenceDetector> _logger;
        private readonly SilenceDetectionConfig _config;
        private readonly IAudioAnalyzer _audioAnalyzer;
        private readonly ConcurrentQueue<AudioSample> _sampleBuffer;
        private readonly CircularBuffer<float> _energyBuffer;
        private readonly List<NoiseProfile> _noiseProfiles;
        private readonly SemaphoreSlim _processingSemaphore;
        private readonly CancellationTokenSource _globalCancellationTokenSource;

        private NoiseProfile _currentNoiseProfile;
        private AdaptiveThreshold _adaptiveThreshold;
        private DetectionState _currentState;
        private DateTime _lastVoiceActivity;
        private DateTime _silenceStartTime;
        private float _currentEnergyLevel;
        private float _backgroundNoiseLevel;
        private int _consecutiveSilentFrames;
        private int _consecutiveActiveFrames;
        private bool _isInitialized;
        private bool _isDisposed;
        private bool _isNoiseProfilingComplete;
        private readonly object _stateLock = new object();

        #endregion;

        #region Public Properties;

        /// <summary>
        /// Gets the current detection state;
        /// </summary>
        public DetectionState State;
        {
            get;
            {
                lock (_stateLock)
                {
                    return _currentState;
                }
            }
            private set;
            {
                lock (_stateLock)
                {
                    _currentState = value;
                }
            }
        }

        /// <summary>
        /// Gets whether the detector is currently profiling noise;
        /// </summary>
        public bool IsNoiseProfiling => !_isNoiseProfilingComplete;

        /// <summary>
        /// Gets the current adaptive threshold value;
        /// </summary>
        public float CurrentThreshold => _adaptiveThreshold?.CurrentValue ?? _config.InitialThreshold;

        /// <summary>
        /// Gets the current background noise level;
        /// </summary>
        public float BackgroundNoiseLevel => _backgroundNoiseLevel;

        /// <summary>
        /// Gets the current audio energy level;
        /// </summary>
        public float CurrentEnergyLevel => _currentEnergyLevel;

        /// <summary>
        /// Gets the duration of current silence period;
        /// </summary>
        public TimeSpan CurrentSilenceDuration =>
            State == DetectionState.Silence ? DateTime.UtcNow - _silenceStartTime : TimeSpan.Zero;

        /// <summary>
        /// Gets the time since last voice activity;
        /// </summary>
        public TimeSpan TimeSinceLastVoiceActivity => DateTime.UtcNow - _lastVoiceActivity;

        /// <summary>
        /// Gets the total number of processed audio samples;
        /// </summary>
        public long TotalProcessedSamples { get; private set; }

        /// <summary>
        /// Gets the detection statistics;
        /// </summary>
        public DetectionStatistics Statistics { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when voice activity is detected;
        /// </summary>
        public event EventHandler<VoiceActivityDetectedEventArgs> VoiceActivityDetected;

        /// <summary>
        /// Event raised when silence is detected;
        /// </summary>
        public event EventHandler<SilenceDetectedEventArgs> SilenceDetected;

        /// <summary>
        /// Event raised when noise profiling is completed;
        /// </summary>
        public event EventHandler<NoiseProfileCompletedEventArgs> NoiseProfileCompleted;

        /// <summary>
        /// Event raised when the detection threshold is adjusted;
        /// </summary>
        public event EventHandler<ThresholdAdjustedEventArgs> ThresholdAdjusted;

        /// <summary>
        /// Event raised when an error occurs during detection;
        /// </summary>
        public event EventHandler<DetectionErrorEventArgs> DetectionError;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of the SilenceDetector;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="config">Silence detection configuration</param>
        /// <param name="audioAnalyzer">Audio analyzer service</param>
        public SilenceDetector(
            ILogger<SilenceDetector> logger,
            IOptions<SilenceDetectionConfig> config,
            IAudioAnalyzer audioAnalyzer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _audioAnalyzer = audioAnalyzer ?? throw new ArgumentNullException(nameof(audioAnalyzer));

            _sampleBuffer = new ConcurrentQueue<AudioSample>();
            _energyBuffer = new CircularBuffer<float>(_config.EnergyHistorySize);
            _noiseProfiles = new List<NoiseProfile>();
            _processingSemaphore = new SemaphoreSlim(1, 1);
            _globalCancellationTokenSource = new CancellationTokenSource();

            State = DetectionState.Idle;
            _currentState = DetectionState.Idle;
            Statistics = new DetectionStatistics();

            _logger.LogInformation("SilenceDetector initialized with buffer size: {BufferSize}",
                _config.EnergyHistorySize);
        }

        #endregion;

        #region Initialization;

        /// <summary>
        /// Initializes the silence detector with audio stream parameters;
        /// </summary>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="channels">Number of audio channels</param>
        /// <param name="bitsPerSample">Bits per sample</param>
        public async Task InitializeAsync(int sampleRate, int channels, int bitsPerSample)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("SilenceDetector is already initialized");
                return;
            }

            try
            {
                _logger.LogInformation("Initializing SilenceDetector with sample rate: {SampleRate}, channels: {Channels}, bits: {Bits}",
                    sampleRate, channels, bitsPerSample);

                // Validate parameters;
                if (sampleRate <= 0)
                    throw new ArgumentException("Sample rate must be greater than 0", nameof(sampleRate));
                if (channels <= 0)
                    throw new ArgumentException("Channels must be greater than 0", nameof(channels));
                if (bitsPerSample != 16 && bitsPerSample != 32)
                    throw new ArgumentException("Bits per sample must be 16 or 32", nameof(bitsPerSample));

                // Initialize audio analyzer;
                await _audioAnalyzer.InitializeAsync(sampleRate, channels, bitsPerSample);

                // Initialize adaptive threshold;
                _adaptiveThreshold = new AdaptiveThreshold(_config.InitialThreshold)
                {
                    AdjustmentRate = _config.ThresholdAdjustmentRate,
                    MinimumThreshold = _config.MinimumThreshold,
                    MaximumThreshold = _config.MaximumThreshold,
                    DecayRate = _config.ThresholdDecayRate;
                };

                // Reset state;
                _energyBuffer.Clear();
                _noiseProfiles.Clear();
                _currentNoiseProfile = null;
                _backgroundNoiseLevel = _config.InitialThreshold;
                _consecutiveSilentFrames = 0;
                _consecutiveActiveFrames = 0;
                _isNoiseProfilingComplete = !_config.EnableNoiseProfiling;
                _lastVoiceActivity = DateTime.UtcNow;

                Statistics = new DetectionStatistics;
                {
                    SampleRate = sampleRate,
                    Channels = channels,
                    BitsPerSample = bitsPerSample,
                    StartTime = DateTime.UtcNow;
                };

                _isInitialized = true;
                State = DetectionState.Ready;

                _logger.LogInformation("SilenceDetector initialized successfully");

                // Start noise profiling if enabled;
                if (_config.EnableNoiseProfiling)
                {
                    _ = Task.Run(async () => await PerformNoiseProfilingAsync());
                }
            }
            catch (Exception ex)
            {
                State = DetectionState.Error;
                _logger.LogError(ex, "Failed to initialize SilenceDetector");
                throw new SilenceDetectionException("Failed to initialize silence detector", ex);
            }
        }

        /// <summary>
        /// Performs noise profiling to establish baseline noise levels;
        /// </summary>
        private async Task PerformNoiseProfilingAsync()
        {
            try
            {
                _logger.LogInformation("Starting noise profiling...");

                var profileSamples = new List<float>();
                var startTime = DateTime.UtcNow;
                var samplesCollected = 0;

                while (samplesCollected < _config.NoiseProfileSamples &&
                       !_globalCancellationTokenSource.IsCancellationRequested)
                {
                    if (_sampleBuffer.TryDequeue(out var sample))
                    {
                        var energy = await _audioAnalyzer.CalculateEnergyAsync(sample.Data);
                        profileSamples.Add(energy);
                        samplesCollected++;

                        // Update progress periodically;
                        if (samplesCollected % 100 == 0)
                        {
                            var progress = (float)samplesCollected / _config.NoiseProfileSamples * 100;
                            _logger.LogDebug("Noise profiling progress: {Progress:F1}%", progress);
                        }
                    }
                    else;
                    {
                        await Task.Delay(10, _globalCancellationTokenSource.Token);
                    }
                }

                if (profileSamples.Count >= _config.MinimumProfileSamples)
                {
                    // Calculate noise profile;
                    var meanEnergy = profileSamples.Average();
                    var variance = profileSamples.Select(e => (e - meanEnergy) * (e - meanEnergy)).Average();
                    var stdDev = (float)Math.Sqrt(variance);

                    _currentNoiseProfile = new NoiseProfile;
                    {
                        MeanEnergy = meanEnergy,
                        StandardDeviation = stdDev,
                        MinimumEnergy = profileSamples.Min(),
                        MaximumEnergy = profileSamples.Max(),
                        SampleCount = profileSamples.Count,
                        ProfileDuration = DateTime.UtcNow - startTime,
                        Timestamp = DateTime.UtcNow;
                    };

                    _noiseProfiles.Add(_currentNoiseProfile);

                    // Set initial threshold based on noise profile;
                    var initialThreshold = meanEnergy + (_config.NoiseMarginFactor * stdDev);
                    _adaptiveThreshold.Reset(initialThreshold);
                    _backgroundNoiseLevel = meanEnergy;

                    _isNoiseProfilingComplete = true;

                    _logger.LogInformation("Noise profiling completed: Mean={Mean:F3}, StdDev={StdDev:F3}, Threshold={Threshold:F3}",
                        meanEnergy, stdDev, initialThreshold);

                    OnNoiseProfileCompleted(new NoiseProfileCompletedEventArgs(_currentNoiseProfile));
                }
                else;
                {
                    _logger.LogWarning("Insufficient samples for noise profiling: {Samples}/{Required}",
                        profileSamples.Count, _config.MinimumProfileSamples);
                    _isNoiseProfilingComplete = true; // Continue without profiling;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during noise profiling");
                _isNoiseProfilingComplete = true; // Continue anyway;
            }
        }

        #endregion;

        #region Detection Methods;

        /// <summary>
        /// Processes an audio sample for silence detection;
        /// </summary>
        /// <param name="sample">Audio sample to process</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<DetectionResult> ProcessSampleAsync(AudioSample sample, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (sample == null)
                throw new ArgumentNullException(nameof(sample));

            if (sample.Data == null || sample.Data.Length == 0)
                throw new ArgumentException("Audio sample data cannot be empty", nameof(sample));

            try
            {
                await _processingSemaphore.WaitAsync(cancellationToken);

                var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _globalCancellationTokenSource.Token;
                ).Token;

                // Add to buffer for processing;
                _sampleBuffer.Enqueue(sample);
                TotalProcessedSamples++;

                // Calculate audio features;
                var features = await AnalyzeAudioSampleAsync(sample, combinedToken);

                // Update energy buffer;
                _energyBuffer.PushBack(features.Energy);
                _currentEnergyLevel = features.Energy;

                // Update statistics;
                UpdateStatistics(features);

                // Perform detection;
                var detectionResult = await PerformDetectionAsync(features, combinedToken);

                // Update state machine;
                UpdateDetectionState(detectionResult);

                // Adjust threshold if adaptive mode is enabled;
                if (_config.UseAdaptiveThreshold && _isNoiseProfilingComplete)
                {
                    AdjustThreshold(features, detectionResult);
                }

                // Raise appropriate events;
                RaiseDetectionEvents(detectionResult, features);

                return detectionResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Silence detection processing was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing audio sample");
                OnDetectionError(new DetectionErrorEventArgs("SampleProcessing", ex.Message, sample.Timestamp));
                throw new SilenceDetectionException("Failed to process audio sample", ex);
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }

        /// <summary>
        /// Processes a batch of audio samples;
        /// </summary>
        /// <param name="samples">Collection of audio samples</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<IReadOnlyList<DetectionResult>> ProcessBatchAsync(
            IEnumerable<AudioSample> samples,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (samples == null)
                throw new ArgumentNullException(nameof(samples));

            var results = new List<DetectionResult>();

            foreach (var sample in samples)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var result = await ProcessSampleAsync(sample, cancellationToken);
                    results.Add(result);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing sample in batch");
                }
            }

            return results.AsReadOnly();
        }

        /// <summary>
        /// Analyzes an audio sample to extract features;
        /// </summary>
        private async Task<AudioFeatures> AnalyzeAudioSampleAsync(AudioSample sample, CancellationToken cancellationToken)
        {
            var features = new AudioFeatures;
            {
                Timestamp = sample.Timestamp,
                SampleDuration = sample.Duration,
                SampleSize = sample.Data.Length;
            };

            // Calculate energy;
            features.Energy = await _audioAnalyzer.CalculateEnergyAsync(sample.Data);

            // Calculate zero-crossing rate if configured;
            if (_config.UseZeroCrossingRate)
            {
                features.ZeroCrossingRate = await _audioAnalyzer.CalculateZeroCrossingRateAsync(sample.Data);
            }

            // Calculate spectral features if configured;
            if (_config.UseSpectralFeatures)
            {
                var spectralFeatures = await _audioAnalyzer.CalculateSpectralFeaturesAsync(sample.Data);
                features.SpectralCentroid = spectralFeatures.Centroid;
                features.SpectralFlux = spectralFeatures.Flux;
                features.SpectralRolloff = spectralFeatures.Rolloff;
            }

            // Calculate additional features;
            features.PeakAmplitude = await _audioAnalyzer.CalculatePeakAmplitudeAsync(sample.Data);
            features.RMS = await _audioAnalyzer.CalculateRMSAsync(sample.Data);

            return features;
        }

        /// <summary>
        /// Performs silence/voice detection based on audio features;
        /// </summary>
        private async Task<DetectionResult> PerformDetectionAsync(AudioFeatures features, CancellationToken cancellationToken)
        {
            var result = new DetectionResult;
            {
                Timestamp = features.Timestamp,
                Energy = features.Energy,
                IsSilence = false,
                Confidence = 0.0f;
            };

            // Get current threshold;
            var threshold = CurrentThreshold;

            // Apply hysteresis if configured;
            if (_config.UseHysteresis)
            {
                threshold = ApplyHysteresis(threshold);
            }

            // Basic energy-based detection;
            if (features.Energy < threshold)
            {
                result.IsSilence = true;
                result.Confidence = CalculateSilenceConfidence(features, threshold);
                _consecutiveSilentFrames++;
                _consecutiveActiveFrames = 0;
            }
            else;
            {
                result.IsSilence = false;
                result.Confidence = CalculateVoiceConfidence(features, threshold);
                _consecutiveSilentFrames = 0;
                _consecutiveActiveFrames++;
                _lastVoiceActivity = DateTime.UtcNow;
            }

            // Apply frame consistency checks;
            if (_config.RequireConsecutiveFrames > 1)
            {
                result.IsSilence = CheckFrameConsistency(result.IsSilence);
            }

            // Apply advanced detection algorithms;
            if (_config.UseAdvancedDetection)
            {
                result = await ApplyAdvancedDetectionAsync(result, features, cancellationToken);
            }

            // Update result metadata;
            result.Threshold = threshold;
            result.ConsecutiveFrames = result.IsSilence ? _consecutiveSilentFrames : _consecutiveActiveFrames;
            result.BackgroundNoiseLevel = _backgroundNoiseLevel;

            return result;
        }

        /// <summary>
        /// Applies hysteresis to the detection threshold;
        /// </summary>
        private float ApplyHysteresis(float baseThreshold)
        {
            if (State == DetectionState.Silence)
            {
                // Higher threshold for leaving silence (prevents false positives)
                return baseThreshold * _config.HysteresisHighFactor;
            }
            else if (State == DetectionState.Voice)
            {
                // Lower threshold for entering silence (prevents false negatives)
                return baseThreshold * _config.HysteresisLowFactor;
            }

            return baseThreshold;
        }

        /// <summary>
        /// Checks frame consistency for more reliable detection;
        /// </summary>
        private bool CheckFrameConsistency(bool currentDetection)
        {
            if (currentDetection)
            {
                // Check if we have enough consecutive silent frames;
                return _consecutiveSilentFrames >= _config.RequireConsecutiveFrames;
            }
            else;
            {
                // Check if we have enough consecutive active frames;
                return _consecutiveActiveFrames >= _config.RequireConsecutiveFrames;
            }
        }

        /// <summary>
        /// Applies advanced detection algorithms;
        /// </summary>
        private async Task<DetectionResult> ApplyAdvancedDetectionAsync(
            DetectionResult preliminaryResult,
            AudioFeatures features,
            CancellationToken cancellationToken)
        {
            var result = preliminaryResult;

            // Use multiple features for decision;
            var detectionScore = 0f;

            // Energy-based score;
            var energyScore = (features.Energy - _backgroundNoiseLevel) / (CurrentThreshold - _backgroundNoiseLevel);
            detectionScore += energyScore * _config.EnergyWeight;

            // Zero-crossing rate score;
            if (_config.UseZeroCrossingRate)
            {
                var zcrScore = CalculateZCRScore(features.ZeroCrossingRate);
                detectionScore += zcrScore * _config.ZCRWeight;
            }

            // Spectral feature scores;
            if (_config.UseSpectralFeatures)
            {
                var spectralScore = CalculateSpectralScore(features);
                detectionScore += spectralScore * _config.SpectralWeight;
            }

            // Apply decision threshold;
            result.IsSilence = detectionScore < _config.AdvancedDecisionThreshold;
            result.Confidence = Math.Abs(detectionScore - _config.AdvancedDecisionThreshold);

            return result;
        }

        /// <summary>
        /// Calculates confidence score for silence detection;
        /// </summary>
        private float CalculateSilenceConfidence(AudioFeatures features, float threshold)
        {
            var normalizedDistance = (threshold - features.Energy) / threshold;
            return Math.Clamp(normalizedDistance, 0.0f, 1.0f);
        }

        /// <summary>
        /// Calculates confidence score for voice detection;
        /// </summary>
        private float CalculateVoiceConfidence(AudioFeatures features, float threshold)
        {
            var normalizedDistance = (features.Energy - threshold) / threshold;
            return Math.Clamp(normalizedDistance, 0.0f, 1.0f);
        }

        /// <summary>
        /// Calculates score based on zero-crossing rate;
        /// </summary>
        private float CalculateZCRScore(float zeroCrossingRate)
        {
            // Voice typically has lower ZCR than unvoiced sounds or noise;
            var typicalVoiceZCR = 0.1f; // Adjust based on your audio characteristics;
            var score = 1.0f - (zeroCrossingRate / typicalVoiceZCR);
            return Math.Clamp(score, 0.0f, 1.0f);
        }

        /// <summary>
        /// Calculates score based on spectral features;
        /// </summary>
        private float CalculateSpectralScore(AudioFeatures features)
        {
            var score = 0.0f;

            // Spectral centroid: voice typically has higher centroid than silence;
            if (features.SpectralCentroid > 0)
            {
                var centroidScore = Math.Clamp(features.SpectralCentroid / 2000.0f, 0.0f, 1.0f);
                score += centroidScore * 0.4f;
            }

            // Spectral flux: voice has higher flux than steady noise;
            if (features.SpectralFlux > 0)
            {
                var fluxScore = Math.Clamp(features.SpectralFlux / 10.0f, 0.0f, 1.0f);
                score += fluxScore * 0.3f;
            }

            return Math.Clamp(score, 0.0f, 1.0f);
        }

        /// <summary>
        /// Updates the detection state based on current result;
        /// </summary>
        private void UpdateDetectionState(DetectionResult result)
        {
            var previousState = State;

            if (result.IsSilence)
            {
                if (State != DetectionState.Silence)
                {
                    State = DetectionState.Silence;
                    _silenceStartTime = DateTime.UtcNow;
                }
            }
            else;
            {
                if (State != DetectionState.Voice)
                {
                    State = DetectionState.Voice;
                    _silenceStartTime = DateTime.MinValue;
                }
            }

            // Update state duration;
            Statistics.UpdateStateDuration(State, result.Timestamp);
        }

        /// <summary>
        /// Adjusts the detection threshold based on current conditions;
        /// </summary>
        private void AdjustThreshold(AudioFeatures features, DetectionResult result)
        {
            var oldThreshold = CurrentThreshold;

            if (result.IsSilence)
            {
                // Gradually lower threshold during silence (become more sensitive)
                _adaptiveThreshold.Adjust(-_config.ThresholdDecayRate);

                // Update background noise level;
                _backgroundNoiseLevel = ExponentialMovingAverage(
                    _backgroundNoiseLevel,
                    features.Energy,
                    _config.NoiseUpdateRate);
            }
            else;
            {
                // Check if this is likely noise or actual voice;
                if (features.Energy < oldThreshold * _config.NoiseMarginFactor)
                {
                    // Probably noise, adjust threshold upward;
                    _adaptiveThreshold.Adjust(_config.ThresholdAdjustmentRate);
                }
            }

            var newThreshold = CurrentThreshold;

            // Raise event if threshold changed significantly;
            if (Math.Abs(newThreshold - oldThreshold) > _config.ThresholdChangeThreshold)
            {
                OnThresholdAdjusted(new ThresholdAdjustedEventArgs(oldThreshold, newThreshold, features.Timestamp));
            }
        }

        /// <summary>
        /// Raises appropriate detection events;
        /// </summary>
        private void RaiseDetectionEvents(DetectionResult result, AudioFeatures features)
        {
            if (result.IsSilence && _consecutiveSilentFrames == _config.RequireConsecutiveFrames)
            {
                // First frame of confirmed silence;
                OnSilenceDetected(new SilenceDetectedEventArgs(
                    result.Timestamp,
                    result.Energy,
                    result.Confidence,
                    CurrentSilenceDuration,
                    features));
            }
            else if (!result.IsSilence && _consecutiveActiveFrames == _config.RequireConsecutiveFrames)
            {
                // First frame of confirmed voice activity;
                OnVoiceActivityDetected(new VoiceActivityDetectedEventArgs(
                    result.Timestamp,
                    result.Energy,
                    result.Confidence,
                    TimeSinceLastVoiceActivity,
                    features));
            }
        }

        /// <summary>
        /// Updates detection statistics;
        /// </summary>
        private void UpdateStatistics(AudioFeatures features)
        {
            Statistics.TotalSamples++;
            Statistics.TotalEnergy += features.Energy;

            if (features.Energy > Statistics.MaxEnergy)
                Statistics.MaxEnergy = features.Energy;
            if (features.Energy < Statistics.MinEnergy)
                Statistics.MinEnergy = features.Energy;

            Statistics.AverageEnergy = Statistics.TotalEnergy / Statistics.TotalSamples;

            // Update energy histogram;
            var binIndex = (int)(features.Energy / _config.EnergyHistogramBinSize);
            if (binIndex < Statistics.EnergyHistogram.Length)
            {
                Statistics.EnergyHistogram[binIndex]++;
            }
        }

        /// <summary>
        /// Calculates exponential moving average;
        /// </summary>
        private float ExponentialMovingAverage(float current, float newValue, float alpha)
        {
            return (alpha * newValue) + ((1 - alpha) * current);
        }

        #endregion;

        #region Control Methods;

        /// <summary>
        /// Resets the silence detector to initial state;
        /// </summary>
        /// <param name="resetNoiseProfile">Whether to reset noise profiling</param>
        public async Task ResetAsync(bool resetNoiseProfile = false)
        {
            ValidateInitialized();

            try
            {
                _logger.LogInformation("Resetting SilenceDetector...");

                await _processingSemaphore.WaitAsync();

                // Clear buffers;
                while (_sampleBuffer.TryDequeue(out _)) { }
                _energyBuffer.Clear();

                // Reset counters;
                _consecutiveSilentFrames = 0;
                _consecutiveActiveFrames = 0;
                TotalProcessedSamples = 0;
                _lastVoiceActivity = DateTime.UtcNow;

                // Reset statistics;
                Statistics = new DetectionStatistics;
                {
                    SampleRate = Statistics.SampleRate,
                    Channels = Statistics.Channels,
                    BitsPerSample = Statistics.BitsPerSample,
                    StartTime = DateTime.UtcNow;
                };

                // Reset noise profiling if requested;
                if (resetNoiseProfile)
                {
                    _noiseProfiles.Clear();
                    _currentNoiseProfile = null;
                    _isNoiseProfilingComplete = !_config.EnableNoiseProfiling;

                    if (_config.EnableNoiseProfiling)
                    {
                        _ = Task.Run(async () => await PerformNoiseProfilingAsync());
                    }
                }

                // Reset threshold;
                if (_adaptiveThreshold != null)
                {
                    _adaptiveThreshold.Reset(_config.InitialThreshold);
                }

                State = DetectionState.Ready;

                _logger.LogInformation("SilenceDetector reset successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting SilenceDetector");
                throw new SilenceDetectionException("Failed to reset silence detector", ex);
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }

        /// <summary>
        /// Forces a specific detection state;
        /// </summary>
        /// <param name="state">State to force</param>
        public void ForceState(DetectionState state)
        {
            ValidateInitialized();

            var previousState = State;
            State = state;

            _logger.LogInformation("Forced state change: {PreviousState} -> {NewState}",
                previousState, state);

            // Reset counters based on new state;
            if (state == DetectionState.Silence)
            {
                _consecutiveSilentFrames = _config.RequireConsecutiveFrames;
                _consecutiveActiveFrames = 0;
                _silenceStartTime = DateTime.UtcNow;
            }
            else if (state == DetectionState.Voice)
            {
                _consecutiveSilentFrames = 0;
                _consecutiveActiveFrames = _config.RequireConsecutiveFrames;
                _lastVoiceActivity = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Updates the detection configuration;
        /// </summary>
        /// <param name="configUpdates">Configuration updates</param>
        public void UpdateConfiguration(Action<SilenceDetectionConfig> configUpdates)
        {
            if (configUpdates == null)
                throw new ArgumentNullException(nameof(configUpdates));

            try
            {
                _logger.LogInformation("Updating silence detection configuration");

                configUpdates(_config);

                // Update adaptive threshold parameters if it exists;
                if (_adaptiveThreshold != null)
                {
                    _adaptiveThreshold.AdjustmentRate = _config.ThresholdAdjustmentRate;
                    _adaptiveThreshold.MinimumThreshold = _config.MinimumThreshold;
                    _adaptiveThreshold.MaximumThreshold = _config.MaximumThreshold;
                    _adaptiveThreshold.DecayRate = _config.ThresholdDecayRate;
                }

                // Resize energy buffer if needed;
                if (_energyBuffer.Capacity != _config.EnergyHistorySize)
                {
                    _energyBuffer.Resize(_config.EnergyHistorySize);
                }

                _logger.LogDebug("Configuration updated successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating silence detection configuration");
                throw new SilenceDetectionException("Failed to update configuration", ex);
            }
        }

        /// <summary>
        /// Gets detailed diagnostics about the silence detector;
        /// </summary>
        public async Task<SilenceDetectorDiagnostics> GetDiagnosticsAsync()
        {
            ValidateInitialized();

            var energyHistory = _energyBuffer.ToArray();
            var avgEnergy = energyHistory.Length > 0 ? energyHistory.Average() : 0;
            var energyVariance = energyHistory.Length > 0 ?
                energyHistory.Select(e => (e - avgEnergy) * (e - avgEnergy)).Average() : 0;

            return new SilenceDetectorDiagnostics;
            {
                State = State,
                IsInitialized = _isInitialized,
                IsNoiseProfilingComplete = _isNoiseProfilingComplete,
                CurrentEnergy = _currentEnergyLevel,
                BackgroundNoiseLevel = _backgroundNoiseLevel,
                CurrentThreshold = CurrentThreshold,
                ConsecutiveSilentFrames = _consecutiveSilentFrames,
                ConsecutiveActiveFrames = _consecutiveActiveFrames,
                CurrentSilenceDuration = CurrentSilenceDuration,
                TimeSinceLastVoiceActivity = TimeSinceLastVoiceActivity,
                TotalProcessedSamples = TotalProcessedSamples,
                BufferUsage = _sampleBuffer.Count,
                EnergyHistorySize = _energyBuffer.Count,
                AverageEnergy = avgEnergy,
                EnergyVariance = energyVariance,
                NoiseProfile = _currentNoiseProfile,
                Statistics = Statistics.Clone(),
                Timestamp = DateTime.UtcNow;
            };
        }

        #endregion;

        #region Helper Methods;

        /// <summary>
        /// Validates that the detector is initialized;
        /// </summary>
        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new SilenceDetectionException("Silence detector is not initialized. Call InitializeAsync first.");
            }

            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(SilenceDetector));
            }
        }

        /// <summary>
        /// Calculates the current voice activity probability;
        /// </summary>
        public float CalculateVoiceProbability()
        {
            if (_energyBuffer.Count < 10)
                return 0.5f; // Neutral probability with insufficient data;

            var recentEnergies = _energyBuffer.ToArray();
            var avgRecentEnergy = recentEnergies.Average();
            var threshold = CurrentThreshold;

            // Simple logistic function for probability;
            var x = (avgRecentEnergy - threshold) / threshold;
            var probability = 1.0f / (1.0f + (float)Math.Exp(-x * 5));

            return probability;
        }

        /// <summary>
        /// Checks if the current audio represents prolonged silence;
        /// </summary>
        /// <param name="minimumDuration">Minimum duration to consider as prolonged silence</param>
        public bool IsProlongedSilence(TimeSpan minimumDuration)
        {
            return State == DetectionState.Silence && CurrentSilenceDuration >= minimumDuration;
        }

        #endregion;

        #region Event Handlers;

        /// <summary>
        /// Raises the VoiceActivityDetected event;
        /// </summary>
        protected virtual void OnVoiceActivityDetected(VoiceActivityDetectedEventArgs e)
        {
            VoiceActivityDetected?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the SilenceDetected event;
        /// </summary>
        protected virtual void OnSilenceDetected(SilenceDetectedEventArgs e)
        {
            SilenceDetected?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the NoiseProfileCompleted event;
        /// </summary>
        protected virtual void OnNoiseProfileCompleted(NoiseProfileCompletedEventArgs e)
        {
            NoiseProfileCompleted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the ThresholdAdjusted event;
        /// </summary>
        protected virtual void OnThresholdAdjusted(ThresholdAdjustedEventArgs e)
        {
            ThresholdAdjusted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the DetectionError event;
        /// </summary>
        protected virtual void OnDetectionError(DetectionErrorEventArgs e)
        {
            DetectionError?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Disposes the silence detector and releases all resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _logger.LogInformation("Disposing SilenceDetector...");

                try
                {
                    // Cancel all ongoing operations;
                    _globalCancellationTokenSource.Cancel();

                    // Wait for processing to complete;
                    _processingSemaphore.Wait(TimeSpan.FromSeconds(2));

                    // Dispose resources;
                    _audioAnalyzer.Dispose();
                    _processingSemaphore.Dispose();
                    _globalCancellationTokenSource.Dispose();

                    // Clear buffers;
                    _sampleBuffer.Clear();

                    _isDisposed = true;
                    State = DetectionState.Stopped;

                    _logger.LogInformation("SilenceDetector disposed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during SilenceDetector disposal");
                }
                finally
                {
                    _processingSemaphore.Release();
                }
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~SilenceDetector()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Interface for silence detection capabilities;
    /// </summary>
    public interface ISilenceDetector : IDisposable
    {
        // Properties;
        DetectionState State { get; }
        bool IsNoiseProfiling { get; }
        float CurrentThreshold { get; }
        float BackgroundNoiseLevel { get; }
        float CurrentEnergyLevel { get; }
        TimeSpan CurrentSilenceDuration { get; }
        TimeSpan TimeSinceLastVoiceActivity { get; }
        long TotalProcessedSamples { get; }
        DetectionStatistics Statistics { get; }

        // Events;
        event EventHandler<VoiceActivityDetectedEventArgs> VoiceActivityDetected;
        event EventHandler<SilenceDetectedEventArgs> SilenceDetected;
        event EventHandler<NoiseProfileCompletedEventArgs> NoiseProfileCompleted;
        event EventHandler<ThresholdAdjustedEventArgs> ThresholdAdjusted;
        event EventHandler<DetectionErrorEventArgs> DetectionError;

        // Methods;
        Task InitializeAsync(int sampleRate, int channels, int bitsPerSample);
        Task<DetectionResult> ProcessSampleAsync(AudioSample sample, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<DetectionResult>> ProcessBatchAsync(IEnumerable<AudioSample> samples, CancellationToken cancellationToken = default);
        Task ResetAsync(bool resetNoiseProfile = false);
        void ForceState(DetectionState state);
        void UpdateConfiguration(Action<SilenceDetectionConfig> configUpdates);
        Task<SilenceDetectorDiagnostics> GetDiagnosticsAsync();
        float CalculateVoiceProbability();
        bool IsProlongedSilence(TimeSpan minimumDuration);
    }

    /// <summary>
    /// Silence detection state enumeration;
    /// </summary>
    public enum DetectionState;
    {
        Idle,
        Initializing,
        Ready,
        Silence,
        Voice,
        Error,
        Stopped;
    }

    /// <summary>
    /// Adaptive threshold for dynamic silence detection;
    /// </summary>
    internal class AdaptiveThreshold;
    {
        private float _currentValue;
        private float _smoothedValue;

        public float CurrentValue => _currentValue;
        public float SmoothedValue => _smoothedValue;
        public float AdjustmentRate { get; set; } = 0.1f;
        public float DecayRate { get; set; } = 0.01f;
        public float MinimumThreshold { get; set; } = 0.001f;
        public float MaximumThreshold { get; set; } = 1.0f;
        public float SmoothingFactor { get; set; } = 0.1f;

        public AdaptiveThreshold(float initialValue)
        {
            _currentValue = initialValue;
            _smoothedValue = initialValue;
        }

        public void Adjust(float adjustment)
        {
            _currentValue = Math.Clamp(_currentValue + adjustment, MinimumThreshold, MaximumThreshold);
            _smoothedValue = (_smoothingFactor * _currentValue) + ((1 - _smoothingFactor) * _smoothedValue);
        }

        public void Reset(float newValue)
        {
            _currentValue = Math.Clamp(newValue, MinimumThreshold, MaximumThreshold);
            _smoothedValue = _currentValue;
        }
    }

    /// <summary>
    /// Circular buffer for efficient energy history storage;
    /// </summary>
    internal class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _tail;
        private int _count;

        public int Capacity => _buffer.Length;
        public int Count => _count;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be greater than 0", nameof(capacity));

            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        public void PushBack(T item)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % Capacity;

            if (_count == Capacity)
            {
                _tail = (_tail + 1) % Capacity;
            }
            else;
            {
                _count++;
            }
        }

        public void Clear()
        {
            _head = 0;
            _tail = 0;
            _count = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public T[] ToArray()
        {
            var result = new T[_count];

            if (_count == 0)
                return result;

            if (_head > _tail)
            {
                Array.Copy(_buffer, _tail, result, 0, _count);
            }
            else;
            {
                var firstPart = Capacity - _tail;
                Array.Copy(_buffer, _tail, result, 0, firstPart);
                Array.Copy(_buffer, 0, result, firstPart, _head);
            }

            return result;
        }

        public void Resize(int newCapacity)
        {
            if (newCapacity <= 0)
                throw new ArgumentException("New capacity must be greater than 0", nameof(newCapacity));

            var currentItems = ToArray();
            var newBuffer = new T[newCapacity];

            var itemsToCopy = Math.Min(currentItems.Length, newCapacity);
            Array.Copy(currentItems, Math.Max(0, currentItems.Length - itemsToCopy), newBuffer, 0, itemsToCopy);

            // Update internal array;
            Array.Resize(ref _buffer, newCapacity);
            Array.Copy(newBuffer, _buffer, newBuffer.Length);

            _head = itemsToCopy % newCapacity;
            _tail = 0;
            _count = itemsToCopy;
        }
    }

    #endregion;
}
