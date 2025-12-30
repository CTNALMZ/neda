using Microsoft.Extensions.DependencyInjection;
using NEDA.Biometrics.VoiceIdentification;
using NEDA.Interface.VoiceRecognition.VoiceActivityDetection;
using NEDA.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.Interface.VoiceRecognition.SpeechToText;
{
    /// <summary>
    /// Advanced voice recognition engine supporting multiple speech recognition providers;
    /// with real-time processing and adaptive noise cancellation;
    /// </summary>
    public class VoiceEngine : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IVoiceRecognitionProvider _primaryProvider;
        private readonly List<IVoiceRecognitionProvider> _fallbackProviders;
        private readonly NoiseCancellationEngine _noiseCancellationEngine;
        private readonly VoiceActivityDetector _voiceActivityDetector;

        private bool _isInitialized;
        private bool _isListening;
        private string _currentLanguage;
        private RecognitionMode _currentMode;

        // Audio buffers;
        private readonly CircularBuffer<byte> _audioBuffer;
        private const int BUFFER_SIZE = 44100 * 10; // 10 seconds at 44.1kHz;

        // Events;
        public event EventHandler<SpeechRecognitionEventArgs> SpeechRecognized;
        public event EventHandler<SpeechErrorEventArgs> RecognitionError;
        public event EventHandler<VoiceActivityEventArgs> VoiceActivityChanged;
        public event EventHandler<EngineStateChangedEventArgs> EngineStateChanged;

        /// <summary>
        /// Recognition modes supported by the engine;
        /// </summary>
        public enum RecognitionMode;
        {
            Continuous,
            Command,
            Dictation,
            Interactive;
        }

        /// <summary>
        /// Engine configuration;
        /// </summary>
        public class EngineConfiguration;
        {
            public string PrimaryProvider { get; set; } = "MicrosoftCognitive";
            public List<string> FallbackProviders { get; set; } = new List<string>();
            public bool EnableNoiseCancellation { get; set; } = true;
            public bool EnableVoiceActivityDetection { get; set; } = true;
            public int SampleRate { get; set; } = 44100;
            public int Channels { get; set; } = 1;
            public float ConfidenceThreshold { get; set; } = 0.7f;
            public TimeSpan EndpointTimeout { get; set; } = TimeSpan.FromSeconds(2);
            public TimeSpan InitialSilenceTimeout { get; set; } = TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Initialize a new VoiceEngine instance;
        /// </summary>
        public VoiceEngine(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Initialize components;
            _primaryProvider = CreateRecognitionProvider("MicrosoftCognitive");
            _fallbackProviders = new List<IVoiceRecognitionProvider>();
            _noiseCancellationEngine = new NoiseCancellationEngine();
            _voiceActivityDetector = new VoiceActivityDetector();
            _audioBuffer = new CircularBuffer<byte>(BUFFER_SIZE);

            _logger.LogInformation("VoiceEngine initialized");
        }

        /// <summary>
        /// Initialize the voice recognition engine with configuration;
        /// </summary>
        public async Task<bool> InitializeAsync(EngineConfiguration configuration, string languageCode = "en-US")
        {
            try
            {
                _logger.LogInformation($"Initializing VoiceEngine with language: {languageCode}");

                // Initialize primary provider;
                var initResult = await _primaryProvider.InitializeAsync(new RecognitionProviderConfig;
                {
                    LanguageCode = languageCode,
                    SampleRate = configuration.SampleRate,
                    Channels = configuration.Channels,
                    RecognitionMode = MapRecognitionMode(_currentMode)
                });

                if (!initResult)
                {
                    _logger.LogError("Failed to initialize primary recognition provider");
                    return false;
                }

                // Initialize fallback providers;
                foreach (var providerName in configuration.FallbackProviders)
                {
                    var provider = CreateRecognitionProvider(providerName);
                    if (await provider.InitializeAsync(new RecognitionProviderConfig;
                    {
                        LanguageCode = languageCode,
                        SampleRate = configuration.SampleRate,
                        Channels = configuration.Channels;
                    }))
                    {
                        _fallbackProviders.Add(provider);
                    }
                }

                // Initialize noise cancellation if enabled;
                if (configuration.EnableNoiseCancellation)
                {
                    await _noiseCancellationEngine.InitializeAsync(configuration.SampleRate, configuration.Channels);
                }

                // Initialize voice activity detection if enabled;
                if (configuration.EnableVoiceActivityDetection)
                {
                    _voiceActivityDetector.VoiceActivityChanged += OnVoiceActivityChanged;
                    await _voiceActivityDetector.InitializeAsync(configuration.SampleRate);
                }

                _currentLanguage = languageCode;
                _isInitialized = true;

                _logger.LogInformation("VoiceEngine initialized successfully");
                EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs(EngineState.Initialized));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize VoiceEngine");
                return false;
            }
        }

        /// <summary>
        /// Start continuous voice recognition;
        /// </summary>
        public async Task<bool> StartRecognitionAsync(RecognitionMode mode = RecognitionMode.Continuous)
        {
            if (!_isInitialized)
            {
                _logger.LogWarning("VoiceEngine must be initialized before starting recognition");
                return false;
            }

            if (_isListening)
            {
                _logger.LogWarning("Recognition is already running");
                return true;
            }

            try
            {
                _logger.LogInformation($"Starting voice recognition in {mode} mode");

                _currentMode = mode;

                // Configure provider for new mode;
                await _primaryProvider.ConfigureAsync(new ProviderConfiguration;
                {
                    Mode = MapRecognitionMode(mode),
                    EndpointTimeout = _currentMode == RecognitionMode.Command ?
                        TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2),
                    InitialSilenceTimeout = TimeSpan.FromSeconds(3)
                });

                // Start recognition session;
                await _primaryProvider.StartRecognitionAsync();

                _isListening = true;

                EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs(EngineState.Listening));
                _logger.LogInformation("Voice recognition started successfully");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start voice recognition");
                return false;
            }
        }

        /// <summary>
        /// Stop voice recognition;
        /// </summary>
        public async Task<bool> StopRecognitionAsync()
        {
            if (!_isListening)
            {
                return true;
            }

            try
            {
                _logger.LogInformation("Stopping voice recognition");

                await _primaryProvider.StopRecognitionAsync();

                // Stop all fallback providers;
                foreach (var provider in _fallbackProviders)
                {
                    await provider.StopRecognitionAsync();
                }

                _isListening = false;

                EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs(EngineState.Stopped));
                _logger.LogInformation("Voice recognition stopped");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping voice recognition");
                return false;
            }
        }

        /// <summary>
        /// Process audio data for recognition;
        /// </summary>
        public async Task ProcessAudioDataAsync(byte[] audioData, int offset, int count)
        {
            if (!_isListening) return;

            try
            {
                // Apply noise cancellation if enabled;
                byte[] processedAudio = audioData;
                if (_noiseCancellationEngine.IsInitialized)
                {
                    processedAudio = await _noiseCancellationEngine.ProcessAsync(audioData, offset, count);
                }

                // Update voice activity detector;
                _voiceActivityDetector.ProcessAudio(processedAudio, 0, processedAudio.Length);

                // Send to primary recognition provider;
                var result = await _primaryProvider.ProcessAudioAsync(processedAudio, 0, processedAudio.Length);

                if (result != null && result.Confidence > 0.7f)
                {
                    OnSpeechRecognized(result);
                }
                else if (_fallbackProviders.Any())
                {
                    // Try fallback providers;
                    await TryFallbackRecognitionAsync(processedAudio);
                }

                // Store in buffer for potential reprocessing;
                _audioBuffer.Write(processedAudio, 0, processedAudio.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing audio data");
                RecognitionError?.Invoke(this, new SpeechErrorEventArgs(ex.Message, ex));
            }
        }

        /// <summary>
        /// Perform one-time recognition from audio file;
        /// </summary>
        public async Task<RecognitionResult> RecognizeFromFileAsync(string filePath, RecognitionMode mode = RecognitionMode.Dictation)
        {
            try
            {
                _logger.LogInformation($"Recognizing speech from file: {filePath}");

                // Read and process audio file;
                var audioFile = await AudioFile.LoadAsync(filePath);
                var audioData = await audioFile.GetAudioDataAsync();

                // Configure provider for one-time recognition;
                await _primaryProvider.ConfigureAsync(new ProviderConfiguration;
                {
                    Mode = MapRecognitionMode(mode),
                    IsSingleUtterance = true;
                });

                // Process audio;
                return await _primaryProvider.ProcessAudioAsync(audioData, 0, audioData.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error recognizing speech from file: {filePath}");
                throw new VoiceRecognitionException("File recognition failed", ex);
            }
        }

        /// <summary>
        /// Change recognition language dynamically;
        /// </summary>
        public async Task<bool> ChangeLanguageAsync(string languageCode)
        {
            try
            {
                _logger.LogInformation($"Changing recognition language to: {languageCode}");

                var wasListening = _isListening;
                if (wasListening)
                {
                    await StopRecognitionAsync();
                }

                // Reinitialize with new language;
                var config = new EngineConfiguration();
                var result = await InitializeAsync(config, languageCode);

                if (result && wasListening)
                {
                    await StartRecognitionAsync(_currentMode);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to change language to: {languageCode}");
                return false;
            }
        }

        /// <summary>
        /// Get current engine statistics;
        /// </summary>
        public EngineStatistics GetStatistics()
        {
            return new EngineStatistics;
            {
                IsListening = _isListening,
                CurrentLanguage = _currentLanguage,
                CurrentMode = _currentMode,
                AudioBufferUsage = (float)_audioBuffer.Count / _audioBuffer.Capacity,
                ProviderName = _primaryProvider.GetType().Name,
                FallbackProviderCount = _fallbackProviders.Count;
            };
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            try
            {
                StopRecognitionAsync().Wait();

                _primaryProvider?.Dispose();
                foreach (var provider in _fallbackProviders)
                {
                    provider?.Dispose();
                }

                _noiseCancellationEngine?.Dispose();
                _voiceActivityDetector?.Dispose();

                _logger.LogInformation("VoiceEngine disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing VoiceEngine");
            }
        }

        #region Private Methods;

        private IVoiceRecognitionProvider CreateRecognitionProvider(string providerName)
        {
            return providerName switch;
            {
                "MicrosoftCognitive" => _serviceProvider.GetRequiredService<MicrosoftCognitiveSpeechProvider>(),
                "GoogleCloud" => _serviceProvider.GetRequiredService<GoogleCloudSpeechProvider>(),
                "AmazonTranscribe" => _serviceProvider.GetRequiredService<AmazonTranscribeProvider>(),
                "SystemSpeech" => _serviceProvider.GetRequiredService<SystemSpeechProvider>(),
                _ => throw new ArgumentException($"Unknown provider: {providerName}")
            };
        }

        private RecognitionProviderMode MapRecognitionMode(RecognitionMode mode)
        {
            return mode switch;
            {
                RecognitionMode.Continuous => RecognitionProviderMode.Continuous,
                RecognitionMode.Command => RecognitionProviderMode.Command,
                RecognitionMode.Dictation => RecognitionProviderMode.Dictation,
                RecognitionMode.Interactive => RecognitionProviderMode.Interactive,
                _ => RecognitionProviderMode.Continuous;
            };
        }

        private async Task TryFallbackRecognitionAsync(byte[] audioData)
        {
            foreach (var provider in _fallbackProviders)
            {
                try
                {
                    var result = await provider.ProcessAudioAsync(audioData, 0, audioData.Length);
                    if (result != null && result.Confidence > 0.6f)
                    {
                        OnSpeechRecognized(result);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Fallback provider {provider.GetType().Name} failed");
                }
            }
        }

        private void OnSpeechRecognized(RecognitionResult result)
        {
            var args = new SpeechRecognitionEventArgs;
            {
                Text = result.Text,
                Confidence = result.Confidence,
                IsFinal = result.IsFinal,
                Language = _currentLanguage,
                Timestamp = DateTime.UtcNow;
            };

            SpeechRecognized?.Invoke(this, args);
            _logger.LogDebug($"Speech recognized: {result.Text} (Confidence: {result.Confidence:P0})");
        }

        private void OnVoiceActivityChanged(object sender, VoiceActivityEventArgs e)
        {
            VoiceActivityChanged?.Invoke(this, e);
        }

        #endregion;

        #region Nested Types;

        public class SpeechRecognitionEventArgs : EventArgs;
        {
            public string Text { get; set; }
            public float Confidence { get; set; }
            public bool IsFinal { get; set; }
            public string Language { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class SpeechErrorEventArgs : EventArgs;
        {
            public string ErrorMessage { get; }
            public Exception Exception { get; }

            public SpeechErrorEventArgs(string message, Exception ex = null)
            {
                ErrorMessage = message;
                Exception = ex;
            }
        }

        public class VoiceActivityEventArgs : EventArgs;
        {
            public bool IsVoiceActive { get; set; }
            public float ActivityLevel { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class EngineStateChangedEventArgs : EventArgs;
        {
            public EngineState State { get; }

            public EngineStateChangedEventArgs(EngineState state)
            {
                State = state;
            }
        }

        public enum EngineState;
        {
            Uninitialized,
            Initialized,
            Listening,
            Stopped,
            Error;
        }

        public class EngineStatistics;
        {
            public bool IsListening { get; set; }
            public string CurrentLanguage { get; set; }
            public RecognitionMode CurrentMode { get; set; }
            public float AudioBufferUsage { get; set; }
            public string ProviderName { get; set; }
            public int FallbackProviderCount { get; set; }
        }

        public class VoiceRecognitionException : Exception
        {
            public VoiceRecognitionException(string message) : base(message) { }
            public VoiceRecognitionException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }

    /// <summary>
    /// Voice recognition provider interface for abstraction;
    /// </summary>
    public interface IVoiceRecognitionProvider : IDisposable
    {
        Task<bool> InitializeAsync(RecognitionProviderConfig config);
        Task ConfigureAsync(ProviderConfiguration config);
        Task StartRecognitionAsync();
        Task StopRecognitionAsync();
        Task<RecognitionResult> ProcessAudioAsync(byte[] audioData, int offset, int count);
    }

    /// <summary>
    /// Recognition provider configuration;
    /// </summary>
    public class RecognitionProviderConfig;
    {
        public string LanguageCode { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public RecognitionProviderMode RecognitionMode { get; set; }
    }

    /// <summary>
    /// Provider configuration for dynamic changes;
    /// </summary>
    public class ProviderConfiguration;
    {
        public RecognitionProviderMode Mode { get; set; }
        public bool IsSingleUtterance { get; set; }
        public TimeSpan EndpointTimeout { get; set; }
        public TimeSpan InitialSilenceTimeout { get; set; }
    }

    /// <summary>
    /// Recognition result from provider;
    /// </summary>
    public class RecognitionResult;
    {
        public string Text { get; set; }
        public float Confidence { get; set; }
        public bool IsFinal { get; set; }
        public IReadOnlyList<RecognitionAlternative> Alternatives { get; set; }
    }

    /// <summary>
    /// Recognition alternatives;
    /// </summary>
    public class RecognitionAlternative;
    {
        public string Text { get; set; }
        public float Confidence { get; set; }
    }

    /// <summary>
    /// Recognition provider modes;
    /// </summary>
    public enum RecognitionProviderMode;
    {
        Continuous,
        Command,
        Dictation,
        Interactive;
    }
}
