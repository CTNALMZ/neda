using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Linq;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Monitoring.Diagnostics;

namespace NEDA.Interface.VoiceRecognition.VoiceActivityDetection;
{
    /// <summary>
    /// Advanced Voice Activity Detection (VAD) engine with adaptive thresholds;
    /// Detects speech segments, silence periods, and voice boundaries with high accuracy;
    /// </summary>
    public class VADetector : IVADetector, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly AppConfig _config;
        private readonly IDiagnosticTool _diagnosticTool;

        private WaveInEvent _waveIn;
        private CancellationTokenSource _processingCancellationTokenSource;
        private readonly List<VoiceActivity> _activityHistory;
        private readonly CircularBuffer<float> _audioBuffer;
        private readonly object _syncLock = new object();

        private volatile bool _isInitialized;
        private volatile bool _isMonitoring;
        private volatile bool _isSpeechActive;
        private volatile float _currentEnergy;

        private VADConfiguration _currentConfig;
        private AdaptiveThreshold _adaptiveThreshold;
        private NoiseProfile _noiseProfile;
        private EnergyCalculator _energyCalculator;
        private SpeechSegment _currentSegment;

        private DateTime _lastActivityTime;
        private DateTime _lastSpeechStart;
        private TimeSpan _totalSpeechTime;
        private TimeSpan _totalSilenceTime;

        /// <summary>
        /// Current detection status;
        /// </summary>
        public VADStatus Status { get; private set; }

        /// <summary>
        /// Current voice activity state;
        /// </summary>
        public VoiceActivityState CurrentState { get; private set; }

        /// <summary>
        /// Real-time detection statistics;
        /// </summary>
        public VADStatistics Statistics { get; private set; }

        /// <summary>
        /// Current configuration settings;
        /// </summary>
        public VADConfiguration CurrentConfiguration => _currentConfig;

        /// <summary>
        /// Current noise floor estimation;
        /// </summary>
        public float NoiseFloor { get; private set; }

        /// <summary>
        /// Current signal-to-noise ratio (SNR)
        /// </summary>
        public float SignalToNoiseRatio { get; private set; }

        /// <summary>
        /// Is currently detecting speech;
        /// </summary>
        public bool IsSpeechDetected => _isSpeechActive;

        #endregion;

        #region Events;

        /// <summary>
        /// Event triggered when voice activity starts;
        /// </summary>
        public event EventHandler<VoiceActivityStartedEventArgs> VoiceActivityStarted;

        /// <summary>
        /// Event triggered when voice activity ends;
        /// </summary>
        public event EventHandler<VoiceActivityEndedEventArgs> VoiceActivityEnded;

        /// <summary>
        /// Event triggered when speech segment is detected;
        /// </summary>
        public event EventHandler<SpeechSegmentDetectedEventArgs> SpeechSegmentDetected;

        /// <summary>
        /// Event triggered when silence period is detected;
        /// </summary>
        public event EventHandler<SilenceDetectedEventArgs> SilenceDetected;

        /// <summary>
        /// Event triggered when real-time audio energy is calculated;
        /// </summary>
        public event EventHandler<EnergyLevelUpdatedEventArgs> EnergyLevelUpdated;

        /// <summary>
        /// Event triggered when detection parameters are adapted;
        /// </summary>
        public event EventHandler<ParametersAdaptedEventArgs> ParametersAdapted;

        /// <summary>
        /// Event triggered when detection error occurs;
        /// </summary>
        public event EventHandler<VADErrorEventArgs> DetectionError;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of VADetector;
        /// </summary>
        public VADetector(ILogger logger, IExceptionHandler exceptionHandler, AppConfig config, IDiagnosticTool diagnosticTool)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));

            _activityHistory = new List<VoiceActivity>();
            _audioBuffer = new CircularBuffer<float>(48000 * 10); // 10 seconds buffer at 48kHz;

            // Default configuration;
            _currentConfig = VADConfiguration.Default;
            _adaptiveThreshold = new AdaptiveThreshold(_currentConfig);
            _noiseProfile = new NoiseProfile();
            _energyCalculator = new EnergyCalculator();

            Statistics = new VADStatistics();
            CurrentState = VoiceActivityState.Silence;

            _isInitialized = false;
            _isMonitoring = false;
            _isSpeechActive = false;
            _currentEnergy = 0.0f;

            NoiseFloor = -60.0f; // Default -60dB;
            SignalToNoiseRatio = 0.0f;

            _lastActivityTime = DateTime.UtcNow;
            _lastSpeechStart = DateTime.UtcNow;
            _totalSpeechTime = TimeSpan.Zero;
            _totalSilenceTime = TimeSpan.Zero;

            _logger.LogInformation("VADetector initialized");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the VAD engine with specified configuration;
        /// </summary>
        public async Task InitializeAsync(VADConfiguration configuration = null)
        {
            if (_isInitialized)
            {
                await ShutdownAsync();
            }

            try
            {
                await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    _logger.LogInformation("Initializing VADetector...");

                    // Apply configuration;
                    if (configuration != null)
                    {
                        _currentConfig = configuration;
                        _adaptiveThreshold = new AdaptiveThreshold(configuration);
                    }

                    // Initialize audio capture;
                    InitializeAudioCapture();

                    // Initialize noise profiling;
                    await InitializeNoiseProfilingAsync();

                    // Reset statistics;
                    ResetStatistics();

                    _isInitialized = true;
                    Status = VADStatus.Ready;

                    _logger.LogInformation($"VADetector initialized with sensitivity: {_currentConfig.Sensitivity}");

                    // Start background processing if auto-start is enabled;
                    if (_currentConfig.AutoStartMonitoring)
                    {
                        await StartMonitoringAsync();
                    }
                }, "VADetector initialization failed");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize VADetector: {ex.Message}");
                Status = VADStatus.Error;
                throw;
            }
        }

        /// <summary>
        /// Starts monitoring audio for voice activity;
        /// </summary>
        public async Task StartMonitoringAsync()
        {
            ValidateInitialization();

            if (_isMonitoring)
            {
                _logger.LogWarning("VADetector is already monitoring");
                return;
            }

            try
            {
                await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    Status = VADStatus.Monitoring;
                    _isMonitoring = true;

                    _processingCancellationTokenSource = new CancellationTokenSource();

                    // Start audio capture;
                    _waveIn.StartRecording();

                    // Start background processing;
                    StartBackgroundProcessing(_processingCancellationTokenSource.Token);

                    _logger.LogInformation("Voice activity monitoring started");
                }, "Failed to start monitoring");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error starting monitoring: {ex.Message}");
                Status = VADStatus.Error;
                throw;
            }
        }

        /// <summary>
        /// Stops monitoring for voice activity;
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (!_isMonitoring)
            {
                return;
            }

            try
            {
                await _exceptionHandler.ExecuteWithHandlingAsync(() =>
                {
                    _processingCancellationTokenSource?.Cancel();

                    if (_waveIn != null && _waveIn.CaptureState == CaptureState.Capturing)
                    {
                        _waveIn.StopRecording();
                    }

                    _isMonitoring = false;
                    Status = VADStatus.Ready;

                    // Finalize current segment if active;
                    if (_isSpeechActive)
                    {
                        FinalizeCurrentSegment();
                    }

                    _logger.LogInformation("Voice activity monitoring stopped");

                    return Task.CompletedTask;
                }, "Failed to stop monitoring");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping monitoring: {ex.Message}");
                Status = VADStatus.Error;
                throw;
            }
        }

        /// <summary>
        /// Analyzes audio buffer for voice activity;
        /// </summary>
        public VoiceActivityResult AnalyzeBuffer(float[] audioBuffer, int offset, int count)
        {
            ValidateInitialization();

            if (audioBuffer == null)
            {
                throw new ArgumentNullException(nameof(audioBuffer));
            }

            if (offset < 0 || offset >= audioBuffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count <= 0 || offset + count > audioBuffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            try
            {
                return _exceptionHandler.ExecuteWithHandling(() =>
                {
                    // Extract buffer segment;
                    var segment = new float[count];
                    Array.Copy(audioBuffer, offset, segment, 0, count);

                    // Calculate energy;
                    var energy = _energyCalculator.CalculateRMS(segment);
                    _currentEnergy = energy;

                    // Update noise profile;
                    _noiseProfile.Update(energy);

                    // Calculate SNR;
                    SignalToNoiseRatio = CalculateSNR(energy, NoiseFloor);

                    // Detect voice activity;
                    var isSpeech = DetectSpeech(energy);

                    // Create result;
                    var result = new VoiceActivityResult;
                    {
                        Timestamp = DateTime.UtcNow,
                        Energy = energy,
                        IsSpeech = isSpeech,
                        NoiseFloor = NoiseFloor,
                        SignalToNoiseRatio = SignalToNoiseRatio,
                        Confidence = CalculateConfidence(energy, isSpeech),
                        SegmentData = segment;
                    };

                    // Update state machine;
                    UpdateStateMachine(result);

                    // Fire energy update event;
                    EnergyLevelUpdated?.Invoke(this, new EnergyLevelUpdatedEventArgs;
                    {
                        Energy = energy,
                        IsSpeech = isSpeech,
                        Timestamp = DateTime.UtcNow;
                    });

                    return result;
                }, "Buffer analysis failed");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error analyzing buffer: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Analyzes audio file for voice activity;
        /// </summary>
        public async Task<VADAnalysisResult> AnalyzeFileAsync(string filePath, AnalysisOptions options = null)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
            }

            if (!System.IO.File.Exists(filePath))
            {
                throw new System.IO.FileNotFoundException($"Audio file not found: {filePath}");
            }

            try
            {
                Status = VADStatus.Analyzing;

                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    _logger.LogInformation($"Starting VAD analysis for file: {filePath}");

                    using (var audioFile = new AudioFileReader(filePath))
                    {
                        var result = new VADAnalysisResult;
                        {
                            FilePath = filePath,
                            StartTime = DateTime.UtcNow,
                            SampleRate = audioFile.WaveFormat.SampleRate,
                            Channels = audioFile.WaveFormat.Channels;
                        };

                        var buffer = new float[audioFile.WaveFormat.SampleRate / 10]; // 100ms buffer;
                        int samplesRead;

                        while ((samplesRead = await audioFile.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            var vadResult = AnalyzeBuffer(buffer, 0, samplesRead);
                            result.Segments.Add(vadResult);

                            // Update overall statistics;
                            if (vadResult.IsSpeech)
                            {
                                result.TotalSpeechDuration = result.TotalSpeechDuration.Add(
                                    TimeSpan.FromSeconds((double)samplesRead / audioFile.WaveFormat.SampleRate));
                            }
                        }

                        result.EndTime = DateTime.UtcNow;
                        result.TotalDuration = audioFile.TotalTime;
                        result.SilenceDuration = result.TotalDuration - result.TotalSpeechDuration;
                        result.SpeechPercentage = (float)(result.TotalSpeechDuration.TotalSeconds /
                            result.TotalDuration.TotalSeconds * 100.0);

                        _logger.LogInformation($"VAD analysis completed: {result.SpeechPercentage:F1}% speech");

                        return result;
                    }
                }, "File analysis failed");
            }
            finally
            {
                Status = VADStatus.Ready;
            }
        }

        /// <summary>
        /// Performs noise calibration to establish baseline;
        /// </summary>
        public async Task CalibrateNoiseAsync(CalibrationOptions options = null)
        {
            ValidateInitialization();

            if (_isSpeechActive)
            {
                throw new InvalidOperationException("Cannot calibrate while speech is active");
            }

            try
            {
                Status = VADStatus.Calibrating;

                await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    _logger.LogInformation("Starting noise calibration...");

                    var calibrationDuration = options?.Duration ?? TimeSpan.FromSeconds(3);
                    var startTime = DateTime.UtcNow;
                    var calibrationData = new List<float>();

                    // Subscribe to energy updates during calibration;
                    EventHandler<EnergyLevelUpdatedEventArgs> calibrationHandler = null;
                    calibrationHandler = (sender, args) =>
                    {
                        calibrationData.Add(args.Energy);
                    };

                    EnergyLevelUpdated += calibrationHandler;

                    try
                    {
                        // Wait for calibration duration;
                        await Task.Delay(calibrationDuration);

                        // Calculate noise profile;
                        if (calibrationData.Count > 0)
                        {
                            var mean = calibrationData.Average();
                            var variance = calibrationData.Select(x => (x - mean) * (x - mean)).Average();
                            var stdDev = (float)Math.Sqrt(variance);

                            NoiseFloor = mean;
                            _noiseProfile = new NoiseProfile(mean, stdDev);

                            _adaptiveThreshold.UpdateNoiseFloor(NoiseFloor);

                            _logger.LogInformation($"Noise calibration completed: Floor={NoiseFloor:F2}dB, StdDev={stdDev:F2}");

                            ParametersAdapted?.Invoke(this, new ParametersAdaptedEventArgs;
                            {
                                NoiseFloor = NoiseFloor,
                                Timestamp = DateTime.UtcNow,
                                Reason = "Calibration completed"
                            });
                        }
                    }
                    finally
                    {
                        EnergyLevelUpdated -= calibrationHandler;
                    }
                }, "Noise calibration failed");
            }
            finally
            {
                Status = VADStatus.Ready;
            }
        }

        /// <summary>
        /// Updates detection configuration;
        /// </summary>
        public void UpdateConfiguration(VADConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            lock (_syncLock)
            {
                _currentConfig = configuration;
                _adaptiveThreshold.UpdateConfiguration(configuration);

                _logger.LogInformation($"VAD configuration updated: Sensitivity={configuration.Sensitivity}");

                ParametersAdapted?.Invoke(this, new ParametersAdaptedEventArgs;
                {
                    Configuration = configuration,
                    Timestamp = DateTime.UtcNow,
                    Reason = "Manual configuration update"
                });
            }
        }

        /// <summary>
        /// Gets voice activity history;
        /// </summary>
        public IReadOnlyList<VoiceActivity> GetActivityHistory()
        {
            lock (_syncLock)
            {
                return new List<VoiceActivity>(_activityHistory);
            }
        }

        /// <summary>
        /// Clears activity history;
        /// </summary>
        public void ClearHistory()
        {
            lock (_syncLock)
            {
                _activityHistory.Clear();
                ResetStatistics();

                _logger.LogInformation("Voice activity history cleared");
            }
        }

        /// <summary>
        /// Gets current audio buffer contents;
        /// </summary>
        public float[] GetAudioBuffer(int maxSamples = 0)
        {
            lock (_syncLock)
            {
                if (maxSamples <= 0 || maxSamples > _audioBuffer.Count)
                {
                    maxSamples = _audioBuffer.Count;
                }

                return _audioBuffer.GetLatest(maxSamples);
            }
        }

        /// <summary>
        /// Exports detection data;
        /// </summary>
        public VADExportData ExportData()
        {
            lock (_syncLock)
            {
                return new VADExportData;
                {
                    Configuration = _currentConfig,
                    Statistics = Statistics,
                    ActivityHistory = new List<VoiceActivity>(_activityHistory),
                    NoiseProfile = _noiseProfile,
                    ExportTime = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Shuts down the VAD engine;
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                if (_isMonitoring)
                {
                    await StopMonitoringAsync();
                }

                if (_waveIn != null)
                {
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                _isInitialized = false;
                Status = VADStatus.Shutdown;

                _logger.LogInformation("VADetector shut down");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during shutdown: {ex.Message}");
                throw;
            }
        }

        #endregion;

        #region Private Methods;

        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("VADetector not initialized. Call InitializeAsync first.");
            }
        }

        private void InitializeAudioCapture()
        {
            try
            {
                _waveIn = new WaveInEvent;
                {
                    WaveFormat = new WaveFormat(_currentConfig.SampleRate, 16, 1), // Mono, 16-bit;
                    BufferMilliseconds = _currentConfig.FrameDuration,
                    NumberOfBuffers = 3;
                };

                _waveIn.DataAvailable += OnAudioDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;

                _logger.LogDebug($"Audio capture initialized: {_currentConfig.SampleRate}Hz, {_currentConfig.FrameDuration}ms frames");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize audio capture: {ex.Message}");
                throw;
            }
        }

        private async Task InitializeNoiseProfilingAsync()
        {
            try
            {
                // Initialize noise profile with default values;
                NoiseFloor = -60.0f;
                _noiseProfile = new NoiseProfile(NoiseFloor, 5.0f); // Default std dev 5dB;

                _logger.LogDebug("Noise profiling initialized with defaults");
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not initialize noise profiling: {ex.Message}");
                // Continue with defaults;
            }
        }

        private void StartBackgroundProcessing(CancellationToken cancellationToken)
        {
            Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await ProcessAudioBufferAsync(cancellationToken);
                        await Task.Delay(10, cancellationToken); // Process every 10ms;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Background processing error: {ex.Message}");

                    DetectionError?.Invoke(this, new VADErrorEventArgs;
                    {
                        ErrorMessage = ex.Message,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }, cancellationToken);
        }

        private async Task ProcessAudioBufferAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!_isMonitoring || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // Get latest audio data from buffer;
                float[] audioData;
                lock (_syncLock)
                {
                    audioData = _audioBuffer.GetLatest(_currentConfig.SampleRate / 10); // 100ms of audio;
                }

                if (audioData.Length > 0)
                {
                    // Analyze for voice activity;
                    var result = AnalyzeBuffer(audioData, 0, audioData.Length);

                    // Update statistics;
                    UpdateStatistics(result);

                    // Adaptive threshold adjustment;
                    _adaptiveThreshold.Adjust(result);

                    await Task.CompletedTask;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Audio buffer processing error: {ex.Message}");
            }
        }

        private bool DetectSpeech(float energy)
        {
            // Convert to dB;
            var energyDb = EnergyToDb(energy);

            // Check against adaptive thresholds;
            var speechThreshold = _adaptiveThreshold.SpeechThreshold;
            var silenceThreshold = _adaptiveThreshold.SilenceThreshold;

            // Apply hysteresis;
            if (_isSpeechActive)
            {
                // Currently in speech state, check for silence;
                return energyDb > silenceThreshold;
            }
            else;
            {
                // Currently in silence state, check for speech;
                return energyDb > speechThreshold;
            }
        }

        private float CalculateSNR(float signalEnergy, float noiseFloor)
        {
            if (noiseFloor <= 0)
            {
                return 0.0f;
            }

            var signalDb = EnergyToDb(signalEnergy);
            return signalDb - noiseFloor;
        }

        private float CalculateConfidence(float energy, bool isSpeech)
        {
            var energyDb = EnergyToDb(energy);
            var threshold = isSpeech ? _adaptiveThreshold.SpeechThreshold : _adaptiveThreshold.SilenceThreshold;

            var distance = Math.Abs(energyDb - threshold);
            var maxDistance = 30.0f; // Maximum expected distance;

            // Normalize to 0-1 range;
            var normalized = Math.Min(distance / maxDistance, 1.0f);

            // Convert to confidence (closer to threshold = lower confidence)
            return 1.0f - normalized;
        }

        private void UpdateStateMachine(VoiceActivityResult result)
        {
            var previousState = CurrentState;
            var isSpeech = result.IsSpeech;

            if (isSpeech && !_isSpeechActive)
            {
                // Speech start detected;
                _isSpeechActive = true;
                CurrentState = VoiceActivityState.Speech;
                _lastSpeechStart = DateTime.UtcNow;

                // Create new speech segment;
                _currentSegment = new SpeechSegment;
                {
                    StartTime = DateTime.UtcNow,
                    StartEnergy = result.Energy,
                    Confidence = result.Confidence;
                };

                // Fire speech start event;
                VoiceActivityStarted?.Invoke(this, new VoiceActivityStartedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    Energy = result.Energy,
                    Confidence = result.Confidence,
                    SignalToNoiseRatio = result.SignalToNoiseRatio;
                });

                _logger.LogDebug($"Speech started at {_lastSpeechStart:HH:mm:ss.fff}");
            }
            else if (!isSpeech && _isSpeechActive)
            {
                // Speech end detected;
                _isSpeechActive = false;
                CurrentState = VoiceActivityState.Silence;

                // Finalize current segment;
                if (_currentSegment != null)
                {
                    _currentSegment.EndTime = DateTime.UtcNow;
                    _currentSegment.Duration = _currentSegment.EndTime - _currentSegment.StartTime;
                    _currentSegment.EndEnergy = result.Energy;

                    // Fire speech end and segment detected events;
                    VoiceActivityEnded?.Invoke(this, new VoiceActivityEndedEventArgs;
                    {
                        Timestamp = DateTime.UtcNow,
                        Segment = _currentSegment,
                        SignalToNoiseRatio = result.SignalToNoiseRatio;
                    });

                    SpeechSegmentDetected?.Invoke(this, new SpeechSegmentDetectedEventArgs;
                    {
                        Segment = _currentSegment,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Add to history;
                    lock (_syncLock)
                    {
                        _activityHistory.Add(new VoiceActivity;
                        {
                            Type = VoiceActivityType.Speech,
                            StartTime = _currentSegment.StartTime,
                            EndTime = _currentSegment.EndTime,
                            Duration = _currentSegment.Duration,
                            AverageEnergy = (_currentSegment.StartEnergy + _currentSegment.EndEnergy) / 2,
                            Confidence = _currentSegment.Confidence;
                        });
                    }

                    _logger.LogDebug($"Speech ended. Duration: {_currentSegment.Duration.TotalMilliseconds:F0}ms");

                    _currentSegment = null;
                }

                // Fire silence detected event;
                SilenceDetected?.Invoke(this, new SilenceDetectedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - _lastSpeechStart,
                    Energy = result.Energy;
                });
            }

            _lastActivityTime = DateTime.UtcNow;
        }

        private void FinalizeCurrentSegment()
        {
            if (_currentSegment != null)
            {
                _currentSegment.EndTime = DateTime.UtcNow;
                _currentSegment.Duration = _currentSegment.EndTime - _currentSegment.StartTime;
                _currentSegment.EndEnergy = _currentEnergy;

                // Add to history;
                lock (_syncLock)
                {
                    _activityHistory.Add(new VoiceActivity;
                    {
                        Type = VoiceActivityType.Speech,
                        StartTime = _currentSegment.StartTime,
                        EndTime = _currentSegment.EndTime,
                        Duration = _currentSegment.Duration,
                        AverageEnergy = (_currentSegment.StartEnergy + _currentSegment.EndEnergy) / 2,
                        Confidence = _currentSegment.Confidence;
                    });
                }

                _currentSegment = null;
            }

            _isSpeechActive = false;
            CurrentState = VoiceActivityState.Silence;
        }

        private void UpdateStatistics(VoiceActivityResult result)
        {
            lock (_syncLock)
            {
                Statistics.TotalFrames++;

                if (result.IsSpeech)
                {
                    Statistics.SpeechFrames++;
                    _totalSpeechTime = _totalSpeechTime.Add(
                        TimeSpan.FromSeconds((double)result.SegmentData.Length / _currentConfig.SampleRate));
                }
                else;
                {
                    Statistics.SilenceFrames++;
                    _totalSilenceTime = _totalSilenceTime.Add(
                        TimeSpan.FromSeconds((double)result.SegmentData.Length / _currentConfig.SampleRate));
                }

                Statistics.AverageEnergy = (Statistics.AverageEnergy * (Statistics.TotalFrames - 1) +
                    result.Energy) / Statistics.TotalFrames;

                Statistics.AverageSNR = (Statistics.AverageSNR * (Statistics.TotalFrames - 1) +
                    result.SignalToNoiseRatio) / Statistics.TotalFrames;

                Statistics.SpeechRatio = (float)Statistics.SpeechFrames / Statistics.TotalFrames;
                Statistics.LastUpdateTime = DateTime.UtcNow;
                Statistics.TotalSpeechDuration = _totalSpeechTime;
                Statistics.TotalSilenceDuration = _totalSilenceTime;
            }
        }

        private void ResetStatistics()
        {
            lock (_syncLock)
            {
                Statistics = new VADStatistics();
                _totalSpeechTime = TimeSpan.Zero;
                _totalSilenceTime = TimeSpan.Zero;
            }
        }

        private float EnergyToDb(float energy)
        {
            if (energy <= 0)
            {
                return -120.0f; // Minimum dB value;
            }

            return 20.0f * (float)Math.Log10(energy);
        }

        #endregion;

        #region Event Handlers;

        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            try
            {
                if (!_isMonitoring)
                {
                    return;
                }

                // Convert byte array to float array;
                var floatBuffer = new float[e.BytesRecorded / 2]; // 16-bit samples;
                for (int i = 0; i < floatBuffer.Length; i++)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                    floatBuffer[i] = sample / 32768.0f; // Normalize to [-1, 1]
                }

                // Add to circular buffer;
                lock (_syncLock)
                {
                    _audioBuffer.Add(floatBuffer);
                }

                // Process immediately if not using background processing;
                if (!_currentConfig.UseBackgroundProcessing)
                {
                    var result = AnalyzeBuffer(floatBuffer, 0, floatBuffer.Length);
                    UpdateStatistics(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing audio data: {ex.Message}");
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            try
            {
                if (e.Exception != null)
                {
                    _logger.LogError($"Recording stopped with error: {e.Exception.Message}");

                    DetectionError?.Invoke(this, new VADErrorEventArgs;
                    {
                        ErrorMessage = e.Exception.Message,
                        Timestamp = DateTime.UtcNow;
                    });
                }
                else;
                {
                    _logger.LogDebug("Recording stopped normally");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in recording stopped handler: {ex.Message}");
            }
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
                    // Dispose managed resources;
                    _processingCancellationTokenSource?.Dispose();
                    _waveIn?.Dispose();
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

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Interface for Voice Activity Detector;
    /// </summary>
    public interface IVADetector : IDisposable
    {
        /// <summary>
        /// Current detection status;
        /// </summary>
        VADStatus Status { get; }

        /// <summary>
        /// Current voice activity state;
        /// </summary>
        VoiceActivityState CurrentState { get; }

        /// <summary>
        /// Real-time detection statistics;
        /// </summary>
        VADStatistics Statistics { get; }

        /// <summary>
        /// Current configuration settings;
        /// </summary>
        VADConfiguration CurrentConfiguration { get; }

        /// <summary>
        /// Current noise floor estimation;
        /// </summary>
        float NoiseFloor { get; }

        /// <summary>
        /// Current signal-to-noise ratio;
        /// </summary>
        float SignalToNoiseRatio { get; }

        /// <summary>
        /// Is currently detecting speech;
        /// </summary>
        bool IsSpeechDetected { get; }

        /// <summary>
        /// Initializes the VAD engine;
        /// </summary>
        Task InitializeAsync(VADConfiguration configuration = null);

        /// <summary>
        /// Starts monitoring audio for voice activity;
        /// </summary>
        Task StartMonitoringAsync();

        /// <summary>
        /// Stops monitoring for voice activity;
        /// </summary>
        Task StopMonitoringAsync();

        /// <summary>
        /// Analyzes audio buffer for voice activity;
        /// </summary>
        VoiceActivityResult AnalyzeBuffer(float[] audioBuffer, int offset, int count);

        /// <summary>
        /// Analyzes audio file for voice activity;
        /// </summary>
        Task<VADAnalysisResult> AnalyzeFileAsync(string filePath, AnalysisOptions options = null);

        /// <summary>
        /// Performs noise calibration;
        /// </summary>
        Task CalibrateNoiseAsync(CalibrationOptions options = null);

        /// <summary>
        /// Updates detection configuration;
        /// </summary>
        void UpdateConfiguration(VADConfiguration configuration);

        /// <summary>
        /// Gets voice activity history;
        /// </summary>
        IReadOnlyList<VoiceActivity> GetActivityHistory();

        /// <summary>
        /// Clears activity history;
        /// </summary>
        void ClearHistory();

        /// <summary>
        /// Gets current audio buffer contents;
        /// </summary>
        float[] GetAudioBuffer(int maxSamples = 0);

        /// <summary>
        /// Exports detection data;
        /// </summary>
        VADExportData ExportData();

        /// <summary>
        /// Shuts down the VAD engine;
        /// </summary>
        Task ShutdownAsync();
    }

    /// <summary>
    /// Voice activity detection configuration;
    /// </summary>
    public class VADConfiguration;
    {
        /// <summary>
        /// Default configuration;
        /// </summary>
        public static VADConfiguration Default => new VADConfiguration;
        {
            Sensitivity = VADSensitivity.Medium,
            SampleRate = 16000,
            FrameDuration = 30,
            SpeechThresholdDb = -30.0f,
            SilenceThresholdDb = -40.0f,
            MinimumSpeechDuration = TimeSpan.FromMilliseconds(100),
            MaximumSilenceDuration = TimeSpan.FromMilliseconds(500),
            UseAdaptiveThreshold = true,
            UseBackgroundProcessing = true,
            AutoStartMonitoring = false;
        };

        /// <summary>
        /// Detection sensitivity level;
        /// </summary>
        public VADSensitivity Sensitivity { get; set; }

        /// <summary>
        /// Audio sample rate (Hz)
        /// </summary>
        public int SampleRate { get; set; }

        /// <summary>
        /// Frame duration in milliseconds;
        /// </summary>
        public int FrameDuration { get; set; }

        /// <summary>
        /// Speech detection threshold in dB;
        /// </summary>
        public float SpeechThresholdDb { get; set; }

        /// <summary>
        /// Silence detection threshold in dB;
        /// </summary>
        public float SilenceThresholdDb { get; set; }

        /// <summary>
        /// Minimum speech duration to be considered valid;
        /// </summary>
        public TimeSpan MinimumSpeechDuration { get; set; }

        /// <summary>
        /// Maximum silence duration before ending speech segment;
        /// </summary>
        public TimeSpan MaximumSilenceDuration { get; set; }

        /// <summary>
        /// Use adaptive thresholds based on noise floor;
        /// </summary>
        public bool UseAdaptiveThreshold { get; set; }

        /// <summary>
        /// Use background processing for real-time analysis;
        /// </summary>
        public bool UseBackgroundProcessing { get; set; }

        /// <summary>
        /// Auto-start monitoring after initialization;
        /// </summary>
        public bool AutoStartMonitoring { get; set; }
    }

    /// <summary>
    /// Voice activity detection sensitivity;
    /// </summary>
    public enum VADSensitivity;
    {
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh;
    }

    /// <summary>
    /// VAD status enumeration;
    /// </summary>
    public enum VADStatus;
    {
        Idle,
        Initializing,
        Ready,
        Monitoring,
        Analyzing,
        Calibrating,
        Error,
        Shutdown;
    }

    /// <summary>
    /// Voice activity state;
    /// </summary>
    public enum VoiceActivityState;
    {
        Silence,
        Speech,
        Unknown;
    }

    /// <summary>
    /// Voice activity type;
    /// </summary>
    public enum VoiceActivityType;
    {
        Speech,
        Silence,
        Noise;
    }

    /// <summary>
    /// Voice activity result;
    /// </summary>
    public class VoiceActivityResult;
    {
        public DateTime Timestamp { get; set; }
        public float Energy { get; set; }
        public bool IsSpeech { get; set; }
        public float NoiseFloor { get; set; }
        public float SignalToNoiseRatio { get; set; }
        public float Confidence { get; set; }
        public float[] SegmentData { get; set; }
    }

    /// <summary>
    /// Voice activity segment;
    /// </summary>
    public class VoiceActivity;
    {
        public VoiceActivityType Type { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public float AverageEnergy { get; set; }
        public float Confidence { get; set; }
        public float MaximumEnergy { get; set; }
        public float MinimumEnergy { get; set; }
    }

    /// <summary>
    /// Speech segment information;
    /// </summary>
    public class SpeechSegment;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public float StartEnergy { get; set; }
        public float EndEnergy { get; set; }
        public float MaximumEnergy { get; set; }
        public float MinimumEnergy { get; set; }
        public float AverageEnergy { get; set; }
        public float Confidence { get; set; }
        public List<float> EnergyProfile { get; set; } = new List<float>();
    }

    /// <summary>
    /// VAD analysis result for files;
    /// </summary>
    public class VADAnalysisResult;
    {
        public string FilePath { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public TimeSpan TotalSpeechDuration { get; set; }
        public TimeSpan SilenceDuration { get; set; }
        public float SpeechPercentage { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public List<VoiceActivityResult> Segments { get; set; } = new List<VoiceActivityResult>();
    }

    /// <summary>
    /// VAD statistics;
    /// </summary>
    public class VADStatistics;
    {
        public long TotalFrames { get; set; }
        public long SpeechFrames { get; set; }
        public long SilenceFrames { get; set; }
        public float SpeechRatio { get; set; }
        public float AverageEnergy { get; set; }
        public float AverageSNR { get; set; }
        public TimeSpan TotalSpeechDuration { get; set; }
        public TimeSpan TotalSilenceDuration { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public float PeakEnergy { get; set; }
        public float MinimumEnergy { get; set; }
    }

    /// <summary>
    /// Analysis options;
    /// </summary>
    public class AnalysisOptions;
    {
        public bool ExportSegments { get; set; }
        public float MinimumSegmentDuration { get; set; } = 0.1f; // seconds;
        public bool IncludeEnergyProfile { get; set; } = true;
    }

    /// <summary>
    /// Calibration options;
    /// </summary>
    public class CalibrationOptions;
    {
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(3);
        public bool UpdateThresholds { get; set; } = true;
        public float TargetSNR { get; set; } = 20.0f; // dB;
    }

    /// <summary>
    /// VAD export data;
    /// </summary>
    public class VADExportData;
    {
        public VADConfiguration Configuration { get; set; }
        public VADStatistics Statistics { get; set; }
        public List<VoiceActivity> ActivityHistory { get; set; }
        public NoiseProfile NoiseProfile { get; set; }
        public DateTime ExportTime { get; set; }
    }

    #endregion;

    #region Helper Classes;

    /// <summary>
    /// Adaptive threshold calculator;
    /// </summary>
    internal class AdaptiveThreshold;
    {
        private readonly VADConfiguration _config;
        private float _noiseFloor;
        private float _speechThreshold;
        private float _silenceThreshold;

        public float SpeechThreshold => _speechThreshold;
        public float SilenceThreshold => _silenceThreshold;

        public AdaptiveThreshold(VADConfiguration config)
        {
            _config = config;
            _noiseFloor = -60.0f;
            UpdateThresholds();
        }

        public void UpdateConfiguration(VADConfiguration config)
        {
            _config = config;
            UpdateThresholds();
        }

        public void UpdateNoiseFloor(float noiseFloor)
        {
            _noiseFloor = noiseFloor;
            UpdateThresholds();
        }

        public void Adjust(VoiceActivityResult result)
        {
            if (!_config.UseAdaptiveThreshold)
            {
                return;
            }

            // Simple adaptive adjustment based on recent activity;
            var adjustment = 0.0f;

            if (result.IsSpeech)
            {
                // If speech is detected but confidence is low, lower threshold;
                if (result.Confidence < 0.5f)
                {
                    adjustment = -2.0f;
                }
            }
            else;
            {
                // If silence is detected but energy is high, raise threshold;
                if (EnergyToDb(result.Energy) > _silenceThreshold + 5.0f)
                {
                    adjustment = 1.0f;
                }
            }

            if (Math.Abs(adjustment) > 0.01f)
            {
                _speechThreshold += adjustment;
                _silenceThreshold += adjustment * 0.8f; // Adjust silence threshold less;
            }
        }

        private void UpdateThresholds()
        {
            var sensitivityMultiplier = GetSensitivityMultiplier(_config.Sensitivity);

            _speechThreshold = _noiseFloor + _config.SpeechThresholdDb * sensitivityMultiplier;
            _silenceThreshold = _noiseFloor + _config.SilenceThresholdDb * sensitivityMultiplier;

            // Ensure hysteresis (speech threshold > silence threshold)
            if (_speechThreshold <= _silenceThreshold)
            {
                _speechThreshold = _silenceThreshold + 2.0f;
            }
        }

        private float GetSensitivityMultiplier(VADSensitivity sensitivity)
        {
            return sensitivity switch;
            {
                VADSensitivity.VeryLow => 0.5f,
                VADSensitivity.Low => 0.75f,
                VADSensitivity.Medium => 1.0f,
                VADSensitivity.High => 1.25f,
                VADSensitivity.VeryHigh => 1.5f,
                _ => 1.0f;
            };
        }

        private float EnergyToDb(float energy)
        {
            if (energy <= 0)
            {
                return -120.0f;
            }

            return 20.0f * (float)Math.Log10(energy);
        }
    }

    /// <summary>
    /// Noise profile for adaptive detection;
    /// </summary>
    internal class NoiseProfile;
    {
        public float Mean { get; private set; }
        public float StandardDeviation { get; private set; }
        public float Variance => StandardDeviation * StandardDeviation;
        public int SampleCount { get; private set; }

        public NoiseProfile()
        {
            Mean = -60.0f;
            StandardDeviation = 5.0f;
            SampleCount = 0;
        }

        public NoiseProfile(float mean, float stdDev)
        {
            Mean = mean;
            StandardDeviation = stdDev;
            SampleCount = 1;
        }

        public void Update(float energyDb)
        {
            // Online update of mean and variance;
            SampleCount++;

            var delta = energyDb - Mean;
            Mean += delta / SampleCount;

            var delta2 = energyDb - Mean;
            StandardDeviation = (float)Math.Sqrt(
                (StandardDeviation * StandardDeviation * (SampleCount - 1) + delta * delta2) / SampleCount);
        }

        public bool IsLikelyNoise(float energyDb)
        {
            // Check if energy is within 2 standard deviations of noise mean;
            var zScore = Math.Abs(energyDb - Mean) / StandardDeviation;
            return zScore < 2.0f;
        }
    }

    /// <summary>
    /// Energy calculation utilities;
    /// </summary>
    internal class EnergyCalculator;
    {
        public float CalculateRMS(float[] samples)
        {
            if (samples == null || samples.Length == 0)
            {
                return 0.0f;
            }

            double sum = 0.0;
            foreach (var sample in samples)
            {
                sum += sample * sample;
            }

            return (float)Math.Sqrt(sum / samples.Length);
        }

        public float CalculatePeak(float[] samples)
        {
            if (samples == null || samples.Length == 0)
            {
                return 0.0f;
            }

            float peak = 0.0f;
            foreach (var sample in samples)
            {
                var absSample = Math.Abs(sample);
                if (absSample > peak)
                {
                    peak = absSample;
                }
            }

            return peak;
        }

        public float CalculateZeroCrossingRate(float[] samples)
        {
            if (samples == null || samples.Length < 2)
            {
                return 0.0f;
            }

            int crossings = 0;
            for (int i = 1; i < samples.Length; i++)
            {
                if (samples[i] * samples[i - 1] < 0)
                {
                    crossings++;
                }
            }

            return (float)crossings / (samples.Length - 1);
        }
    }

    /// <summary>
    /// Circular buffer for audio data;
    /// </summary>
    internal class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _tail;
        private int _count;
        private readonly object _lock = new object();

        public int Capacity => _buffer.Length;
        public int Count => _count;

        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                _buffer[_head] = item;
                _head = (_head + 1) % _buffer.Length;

                if (_count == _buffer.Length)
                {
                    _tail = (_tail + 1) % _buffer.Length;
                }
                else;
                {
                    _count++;
                }
            }
        }

        public void Add(T[] items)
        {
            if (items == null)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var item in items)
                {
                    Add(item);
                }
            }
        }

        public T[] GetLatest(int count)
        {
            lock (_lock)
            {
                if (count <= 0 || _count == 0)
                {
                    return Array.Empty<T>();
                }

                count = Math.Min(count, _count);
                var result = new T[count];

                int startIndex = (_head - count + _buffer.Length) % _buffer.Length;

                for (int i = 0; i < count; i++)
                {
                    int index = (startIndex + i) % _buffer.Length;
                    result[i] = _buffer[index];
                }

                return result;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
                Array.Clear(_buffer, 0, _buffer.Length);
            }
        }
    }

    #endregion;

    #region Event Arguments;

    /// <summary>
    /// Event arguments for voice activity started;
    /// </summary>
    public class VoiceActivityStartedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public float Energy { get; set; }
        public float Confidence { get; set; }
        public float SignalToNoiseRatio { get; set; }
    }

    /// <summary>
    /// Event arguments for voice activity ended;
    /// </summary>
    public class VoiceActivityEndedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public SpeechSegment Segment { get; set; }
        public float SignalToNoiseRatio { get; set; }
    }

    /// <summary>
    /// Event arguments for speech segment detected;
    /// </summary>
    public class SpeechSegmentDetectedEventArgs : EventArgs;
    {
        public SpeechSegment Segment { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for silence detected;
    /// </summary>
    public class SilenceDetectedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public float Energy { get; set; }
    }

    /// <summary>
    /// Event arguments for energy level updated;
    /// </summary>
    public class EnergyLevelUpdatedEventArgs : EventArgs;
    {
        public float Energy { get; set; }
        public bool IsSpeech { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for parameters adapted;
    /// </summary>
    public class ParametersAdaptedEventArgs : EventArgs;
    {
        public VADConfiguration Configuration { get; set; }
        public float NoiseFloor { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Event arguments for VAD error;
    /// </summary>
    public class VADErrorEventArgs : EventArgs;
    {
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// VAD exception;
    /// </summary>
    public class VADException : Exception
    {
        public VADException() { }
        public VADException(string message) : base(message) { }
        public VADException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
