using NEDA.Communication.EmotionalIntelligence.EmotionRecognition;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Interface.ResponseGenerator.EmotionalIntonation;
using NEDA.Interface.ResponseGenerator.MultilingualSupport;
using NEDA.Interface.ResponseGenerator.NaturalVoiceSynthesis;
using System;
using System.Collections.Generic;
using System.Speech.Synthesis;
using System.Threading.Tasks;

namespace NEDA.Interface.ResponseGenerator.TextToSpeech;
{
    /// <summary>
    /// Text-to-Speech Engine for converting text to natural sounding speech;
    /// Supports multiple voices, emotions, languages, and audio formats;
    /// </summary>
    public class TTSEngine : IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly SpeechSynthesizer _speechSynthesizer;
        private readonly VoiceSynthesizer _voiceSynthesizer;
        private readonly EmotionEngine _emotionEngine;
        private readonly MultiLanguage _multiLanguage;
        private readonly TTSConfiguration _configuration;
        private bool _isInitialized;
        private bool _isPlaying;

        public event EventHandler<SpeechStartedEventArgs> SpeechStarted;
        public event EventHandler<SpeechCompletedEventArgs> SpeechCompleted;
        public event EventHandler<SpeechErrorEventArgs> SpeechError;
        public event EventHandler<VoiceChangedEventArgs> VoiceChanged;

        public IReadOnlyList<InstalledVoice> AvailableVoices => _speechSynthesizer.GetInstalledVoices();
        public VoiceInfo CurrentVoice => _speechSynthesizer.Voice;
        public int SpeechRate;
        {
            get => _speechSynthesizer.Rate;
            set => _speechSynthesizer.Rate = Math.Clamp(value, -10, 10);
        }

        public int Volume;
        {
            get => _speechSynthesizer.Volume;
            set => _speechSynthesizer.Volume = Math.Clamp(value, 0, 100);
        }

        #endregion;

        #region Configuration Classes;

        public class TTSConfiguration;
        {
            public string DefaultVoice { get; set; } = "Microsoft David Desktop";
            public int DefaultRate { get; set; } = 0;
            public int DefaultVolume { get; set; } = 80;
            public bool UseNeuralVoices { get; set; } = true;
            public AudioFormat AudioFormat { get; set; } = AudioFormat.Wave;
            public int SampleRate { get; set; } = 44100;
            public int BitDepth { get; set; } = 16;
            public bool EnableEmotionalIntonation { get; set; } = true;
            public bool EnableRealTimeProcessing { get; set; } = true;
            public bool CacheSynthesizedSpeech { get; set; } = true;
            public string CacheDirectory { get; set; } = "AudioCache";
        }

        public enum AudioFormat;
        {
            Wave,
            MP3,
            OGG,
            AAC;
        }

        public enum VoiceGender;
        {
            Male,
            Female,
            Neutral;
        }

        public enum EmotionType;
        {
            Neutral,
            Happy,
            Sad,
            Angry,
            Excited,
            Calm,
            Formal,
            Friendly;
        }

        #endregion;

        #region Event Args Classes;

        public class SpeechStartedEventArgs : EventArgs;
        {
            public string Text { get; }
            public DateTime StartTime { get; }
            public VoiceInfo Voice { get; }

            public SpeechStartedEventArgs(string text, VoiceInfo voice)
            {
                Text = text;
                StartTime = DateTime.UtcNow;
                Voice = voice;
            }
        }

        public class SpeechCompletedEventArgs : EventArgs;
        {
            public string Text { get; }
            public DateTime StartTime { get; }
            public DateTime EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public byte[] AudioData { get; }
            public string AudioFilePath { get; }

            public SpeechCompletedEventArgs(string text, DateTime startTime, DateTime endTime,
                byte[] audioData = null, string audioFilePath = null)
            {
                Text = text;
                StartTime = startTime;
                EndTime = endTime;
                AudioData = audioData;
                AudioFilePath = audioFilePath;
            }
        }

        public class SpeechErrorEventArgs : EventArgs;
        {
            public string Text { get; }
            public Exception Error { get; }
            public string ErrorMessage => Error?.Message;

            public SpeechErrorEventArgs(string text, Exception error)
            {
                Text = text;
                Error = error;
            }
        }

        public class VoiceChangedEventArgs : EventArgs;
        {
            public VoiceInfo PreviousVoice { get; }
            public VoiceInfo NewVoice { get; }

            public VoiceChangedEventArgs(VoiceInfo previousVoice, VoiceInfo newVoice)
            {
                PreviousVoice = previousVoice;
                NewVoice = newVoice;
            }
        }

        #endregion;

        #region Constructor and Initialization;

        /// <summary>
        /// Initializes a new instance of TTSEngine;
        /// </summary>
        /// <param name="logger">Logger instance for logging</param>
        /// <param name="configuration">TTS configuration (optional)</param>
        public TTSEngine(ILogger logger = null, TTSConfiguration configuration = null)
        {
            _logger = logger ?? new DefaultLogger();
            _configuration = configuration ?? new TTSConfiguration();

            try
            {
                _speechSynthesizer = new SpeechSynthesizer();
                _voiceSynthesizer = new VoiceSynthesizer();
                _emotionEngine = new EmotionEngine();
                _multiLanguage = new MultiLanguage();

                InitializeEngine();
                _isInitialized = true;

                _logger.LogInformation("TTSEngine initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize TTSEngine: {ex.Message}", ex);
                throw new EngineInitializationException("Failed to initialize TTS Engine", ex);
            }
        }

        private void InitializeEngine()
        {
            // Configure speech synthesizer;
            _speechSynthesizer.SetOutputToDefaultAudioDevice();
            _speechSynthesizer.Rate = _configuration.DefaultRate;
            _speechSynthesizer.Volume = _configuration.DefaultVolume;

            // Select default voice;
            SelectVoice(_configuration.DefaultVoice);

            // Subscribe to events;
            _speechSynthesizer.SpeakStarted += OnSpeakStarted;
            _speechSynthesizer.SpeakCompleted += OnSpeakCompleted;
            _speechSynthesizer.SpeakProgress += OnSpeakProgress;

            // Initialize audio cache if enabled;
            if (_configuration.CacheSynthesizedSpeech)
            {
                InitializeAudioCache();
            }
        }

        private void InitializeAudioCache()
        {
            try
            {
                if (!System.IO.Directory.Exists(_configuration.CacheDirectory))
                {
                    System.IO.Directory.CreateDirectory(_configuration.CacheDirectory);
                }
                _logger.LogDebug($"Audio cache initialized at: {_configuration.CacheDirectory}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to initialize audio cache: {ex.Message}");
            }
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Converts text to speech and plays it immediately;
        /// </summary>
        /// <param name="text">Text to speak</param>
        /// <param name="emotion">Emotion to apply to speech</param>
        /// <param name="voiceName">Specific voice to use</param>
        public async Task SpeakAsync(string text, EmotionType emotion = EmotionType.Neutral, string voiceName = null)
        {
            ValidateEngineState();

            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            try
            {
                _isPlaying = true;

                // Apply emotion modulation if enabled;
                if (_configuration.EnableEmotionalIntonation && emotion != EmotionType.Neutral)
                {
                    text = await _emotionEngine.ApplyEmotionToText(text, emotion);
                    AdjustVoiceForEmotion(emotion);
                }

                // Select voice if specified;
                if (!string.IsNullOrEmpty(voiceName))
                {
                    SelectVoice(voiceName);
                }

                // Start speech synthesis;
                await Task.Run(() => _speechSynthesizer.SpeakAsync(text));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in SpeakAsync: {ex.Message}", ex);
                SpeechError?.Invoke(this, new SpeechErrorEventArgs(text, ex));
                throw new SpeechSynthesisException("Failed to synthesize speech", ex);
            }
        }

        /// <summary>
        /// Converts text to speech and saves to file;
        /// </summary>
        /// <param name="text">Text to synthesize</param>
        /// <param name="outputPath">Output file path</param>
        /// <param name="emotion">Emotion to apply</param>
        /// <param name="voiceName">Specific voice to use</param>
        public async Task SynthesizeToFileAsync(string text, string outputPath,
            EmotionType emotion = EmotionType.Neutral, string voiceName = null)
        {
            ValidateEngineState();

            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path cannot be null or empty", nameof(outputPath));

            try
            {
                // Check cache first;
                string cacheKey = GenerateCacheKey(text, voiceName, emotion);
                string cachedFile = GetCachedAudioPath(cacheKey);

                if (_configuration.CacheSynthesizedSpeech && System.IO.File.Exists(cachedFile))
                {
                    System.IO.File.Copy(cachedFile, outputPath, true);
                    _logger.LogDebug($"Using cached audio for: {text.Substring(0, Math.Min(50, text.Length))}...");
                    return;
                }

                // Apply emotion and select voice;
                if (_configuration.EnableEmotionalIntonation && emotion != EmotionType.Neutral)
                {
                    text = await _emotionEngine.ApplyEmotionToText(text, emotion);
                    AdjustVoiceForEmotion(emotion);
                }

                if (!string.IsNullOrEmpty(voiceName))
                {
                    SelectVoice(voiceName);
                }

                // Configure output to file;
                _speechSynthesizer.SetOutputToWaveFile(outputPath);

                await Task.Run(() => _speechSynthesizer.Speak(text));

                // Reset to default audio device;
                _speechSynthesizer.SetOutputToDefaultAudioDevice();

                // Cache the file if enabled;
                if (_configuration.CacheSynthesizedSpeech)
                {
                    CacheAudioFile(cacheKey, outputPath);
                }

                _logger.LogInformation($"Speech synthesized to file: {outputPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in SynthesizeToFileAsync: {ex.Message}", ex);
                throw new SpeechSynthesisException("Failed to synthesize speech to file", ex);
            }
        }

        /// <summary>
        /// Converts text to speech and returns audio data;
        /// </summary>
        /// <param name="text">Text to synthesize</param>
        /// <param name="emotion">Emotion to apply</param>
        /// <param name="voiceName">Specific voice to use</param>
        /// <returns>Audio data in specified format</returns>
        public async Task<byte[]> SynthesizeToMemoryAsync(string text,
            EmotionType emotion = EmotionType.Neutral, string voiceName = null)
        {
            ValidateEngineState();

            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            try
            {
                // Check cache first;
                string cacheKey = GenerateCacheKey(text, voiceName, emotion);
                byte[] cachedData = GetCachedAudioData(cacheKey);

                if (cachedData != null)
                {
                    _logger.LogDebug($"Using cached audio data for: {text.Substring(0, Math.Min(50, text.Length))}...");
                    return cachedData;
                }

                // Apply emotion and select voice;
                if (_configuration.EnableEmotionalIntonation && emotion != EmotionType.Neutral)
                {
                    text = await _emotionEngine.ApplyEmotionToText(text, emotion);
                    AdjustVoiceForEmotion(emotion);
                }

                if (!string.IsNullOrEmpty(voiceName))
                {
                    SelectVoice(voiceName);
                }

                // Synthesize to memory stream;
                using (var memoryStream = new System.IO.MemoryStream())
                {
                    _speechSynthesizer.SetOutputToWaveStream(memoryStream);
                    await Task.Run(() => _speechSynthesizer.Speak(text));
                    _speechSynthesizer.SetOutputToDefaultAudioDevice();

                    byte[] audioData = memoryStream.ToArray();

                    // Cache the data if enabled;
                    if (_configuration.CacheSynthesizedSpeech)
                    {
                        CacheAudioData(cacheKey, audioData);
                    }

                    return audioData;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in SynthesizeToMemoryAsync: {ex.Message}", ex);
                throw new SpeechSynthesisException("Failed to synthesize speech to memory", ex);
            }
        }

        /// <summary>
        /// Selects a specific voice by name;
        /// </summary>
        /// <param name="voiceName">Name of the voice to select</param>
        public void SelectVoice(string voiceName)
        {
            ValidateEngineState();

            try
            {
                var previousVoice = _speechSynthesizer.Voice;

                // Try to set the specified voice;
                _speechSynthesizer.SelectVoice(voiceName);

                // If neural voices are preferred, try to find neural version;
                if (_configuration.UseNeuralVoices && !voiceName.Contains("Neural"))
                {
                    var neuralVoice = FindNeuralVoice(voiceName);
                    if (neuralVoice != null)
                    {
                        _speechSynthesizer.SelectVoice(neuralVoice.VoiceInfo.Name);
                    }
                }

                VoiceChanged?.Invoke(this, new VoiceChangedEventArgs(previousVoice, _speechSynthesizer.Voice));
                _logger.LogDebug($"Voice changed to: {_speechSynthesizer.Voice.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to select voice '{voiceName}': {ex.Message}");
                throw new VoiceSelectionException($"Voice '{voiceName}' not found or unavailable", ex);
            }
        }

        /// <summary>
        /// Finds a voice by gender and language;
        /// </summary>
        /// <param name="gender">Desired voice gender</param>
        /// <param name="languageCode">Language code (e.g., "en-US")</param>
        /// <returns>Voice info or null if not found</returns>
        public VoiceInfo FindVoiceByCriteria(VoiceGender gender, string languageCode = "en-US")
        {
            ValidateEngineState();

            foreach (var voice in AvailableVoices)
            {
                if (voice.Enabled &&
                    voice.VoiceInfo.Culture.Name.Equals(languageCode, StringComparison.OrdinalIgnoreCase))
                {
                    if (gender == VoiceGender.Neutral ||
                        voice.VoiceInfo.Gender.ToString().Equals(gender.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return voice.VoiceInfo;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Pauses speech playback;
        /// </summary>
        public void Pause()
        {
            if (_isPlaying && _speechSynthesizer.State == SynthesizerState.Speaking)
            {
                _speechSynthesizer.Pause();
                _logger.LogDebug("Speech paused");
            }
        }

        /// <summary>
        /// Resumes paused speech playback;
        /// </summary>
        public void Resume()
        {
            if (_speechSynthesizer.State == SynthesizerState.Paused)
            {
                _speechSynthesizer.Resume();
                _logger.LogDebug("Speech resumed");
            }
        }

        /// <summary>
        /// Stops speech playback;
        /// </summary>
        public void Stop()
        {
            if (_isPlaying)
            {
                _speechSynthesizer.SpeakAsyncCancelAll();
                _isPlaying = false;
                _logger.LogDebug("Speech stopped");
            }
        }

        /// <summary>
        /// Preloads text for real-time synthesis;
        /// </summary>
        /// <param name="text">Text to preload</param>
        /// <param name="emotion">Emotion to apply</param>
        public async Task PreloadAsync(string text, EmotionType emotion = EmotionType.Neutral)
        {
            if (_configuration.EnableRealTimeProcessing)
            {
                try
                {
                    // Synthesize and cache in memory for quick playback;
                    await SynthesizeToMemoryAsync(text, emotion);
                    _logger.LogDebug($"Preloaded text: {text.Substring(0, Math.Min(30, text.Length))}...");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to preload text: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets engine status information;
        /// </summary>
        public EngineStatus GetStatus()
        {
            return new EngineStatus;
            {
                IsInitialized = _isInitialized,
                IsPlaying = _isPlaying,
                CurrentVoice = CurrentVoice?.Name,
                AvailableVoicesCount = AvailableVoices.Count,
                SpeechRate = SpeechRate,
                Volume = Volume,
                State = _speechSynthesizer.State.ToString()
            };
        }

        #endregion;

        #region Private Methods;

        private void ValidateEngineState()
        {
            if (!_isInitialized)
                throw new EngineNotInitializedException("TTSEngine is not initialized");

            if (_speechSynthesizer == null)
                throw new InvalidOperationException("Speech synthesizer is not available");
        }

        private void AdjustVoiceForEmotion(EmotionType emotion)
        {
            switch (emotion)
            {
                case EmotionType.Happy:
                    _speechSynthesizer.Rate = Math.Min(10, _speechSynthesizer.Rate + 2);
                    _speechSynthesizer.Volume = Math.Min(100, _speechSynthesizer.Volume + 10);
                    break;

                case EmotionType.Sad:
                    _speechSynthesizer.Rate = Math.Max(-10, _speechSynthesizer.Rate - 2);
                    _speechSynthesizer.Volume = Math.Max(0, _speechSynthesizer.Volume - 5);
                    break;

                case EmotionType.Angry:
                    _speechSynthesizer.Rate = Math.Min(10, _speechSynthesizer.Rate + 3);
                    _speechSynthesizer.Volume = Math.Min(100, _speechSynthesizer.Volume + 15);
                    break;

                case EmotionType.Excited:
                    _speechSynthesizer.Rate = Math.Min(10, _speechSynthesizer.Rate + 4);
                    break;

                case EmotionType.Calm:
                    _speechSynthesizer.Rate = Math.Max(-10, _speechSynthesizer.Rate - 1);
                    break;
            }
        }

        private InstalledVoice FindNeuralVoice(string baseVoiceName)
        {
            foreach (var voice in AvailableVoices)
            {
                if (voice.Enabled &&
                    voice.VoiceInfo.Name.Contains("Neural") &&
                    voice.VoiceInfo.Name.Contains(baseVoiceName.Split()[0]))
                {
                    return voice;
                }
            }
            return null;
        }

        private string GenerateCacheKey(string text, string voiceName, EmotionType emotion)
        {
            string voice = voiceName ?? CurrentVoice?.Name ?? "default";
            string normalizedText = text.Trim().ToLowerInvariant();

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                string input = $"{normalizedText}|{voice}|{emotion}";
                byte[] hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private string GetCachedAudioPath(string cacheKey)
        {
            return System.IO.Path.Combine(_configuration.CacheDirectory, $"{cacheKey}.wav");
        }

        private byte[] GetCachedAudioData(string cacheKey)
        {
            try
            {
                string cachePath = GetCachedAudioPath(cacheKey);
                if (System.IO.File.Exists(cachePath))
                {
                    return System.IO.File.ReadAllBytes(cachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to read cached audio: {ex.Message}");
            }
            return null;
        }

        private void CacheAudioFile(string cacheKey, string sourceFile)
        {
            try
            {
                string cachePath = GetCachedAudioPath(cacheKey);
                System.IO.File.Copy(sourceFile, cachePath, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to cache audio file: {ex.Message}");
            }
        }

        private void CacheAudioData(string cacheKey, byte[] audioData)
        {
            try
            {
                string cachePath = GetCachedAudioPath(cacheKey);
                System.IO.File.WriteAllBytes(cachePath, audioData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to cache audio data: {ex.Message}");
            }
        }

        #endregion;

        #region Event Handlers;

        private void OnSpeakStarted(object sender, SpeakStartedEventArgs e)
        {
            SpeechStarted?.Invoke(this, new SpeechStartedEventArgs(e.Voice, CurrentVoice));
            _logger.LogDebug($"Speech started: {e.Voice}");
        }

        private void OnSpeakCompleted(object sender, SpeakCompletedEventArgs e)
        {
            _isPlaying = false;
            SpeechCompleted?.Invoke(this, new SpeechCompletedEventArgs(
                e.Voice,
                DateTime.UtcNow.AddMilliseconds(-e.AudioPosition.TotalMilliseconds),
                DateTime.UtcNow;
            ));
            _logger.LogDebug($"Speech completed: {e.Voice}");
        }

        private void OnSpeakProgress(object sender, SpeakProgressEventArgs e)
        {
            // Optional: Implement progress tracking for long texts;
            // Could be used for visual feedback or synchronization;
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
                    _speechSynthesizer?.Dispose();
                    _voiceSynthesizer?.Dispose();
                    _emotionEngine?.Dispose();
                    _multiLanguage?.Dispose();
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

        #region Helper Classes;

        public class EngineStatus;
        {
            public bool IsInitialized { get; set; }
            public bool IsPlaying { get; set; }
            public string CurrentVoice { get; set; }
            public int AvailableVoicesCount { get; set; }
            public int SpeechRate { get; set; }
            public int Volume { get; set; }
            public string State { get; set; }
        }

        #endregion;
    }

    #region Custom Exceptions;

    public class EngineInitializationException : Exception
    {
        public EngineInitializationException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class EngineNotInitializedException : InvalidOperationException;
    {
        public EngineNotInitializedException(string message)
            : base(message) { }
    }

    public class SpeechSynthesisException : Exception
    {
        public SpeechSynthesisException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class VoiceSelectionException : Exception
    {
        public VoiceSelectionException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    #endregion;

    #region Default Implementations (for dependencies)

    internal class DefaultLogger : ILogger;
    {
        public void LogInformation(string message) => Console.WriteLine($"[INFO] {message}");
        public void LogDebug(string message) => Console.WriteLine($"[DEBUG] {message}");
        public void LogWarning(string message) => Console.WriteLine($"[WARNING] {message}");
        public void LogError(string message, Exception ex = null) => Console.WriteLine($"[ERROR] {message}: {ex?.Message}");
    }

    #endregion;
}
