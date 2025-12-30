using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.Interface.VoiceRecognition.SpeechToText.Configuration;
using NEDA.Interface.VoiceRecognition.SpeechToText.Events;
using NEDA.Interface.VoiceRecognition.SpeechToText.Models;
using NEDA.Interface.VoiceRecognition.SpeechToText.Providers;
using NEDA.Interface.VoiceRecognition.SpeechToText.Exceptions;

namespace NEDA.Interface.VoiceRecognition.SpeechToText;
{
    /// <summary>
    /// Speech recognition engine that converts spoken language to text;
    /// Supports multiple recognition providers with fallback mechanisms;
    /// </summary>
    public class SpeechRecognizer : ISpeechRecognizer, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger<SpeechRecognizer> _logger;
        private readonly SpeechRecognitionConfig _config;
        private readonly IAudioProcessor _audioProcessor;
        private readonly IRecognitionProviderFactory _providerFactory;
        private readonly ConcurrentDictionary<string, RecognitionSession> _activeSessions;
        private readonly SemaphoreSlim _recognitionSemaphore;
        private readonly PriorityQueue<RecognitionRequest, int> _recognitionQueue;
        private readonly CancellationTokenSource _globalCancellationTokenSource;
        private IRecognitionProvider _currentProvider;
        private bool _isInitialized;
        private bool _isDisposed;

        #endregion;

        #region Public Properties;

        /// <summary>
        /// Gets the current state of the speech recognizer;
        /// </summary>
        public SpeechRecognizerState State { get; private set; }

        /// <summary>
        /// Gets the current recognition provider in use;
        /// </summary>
        public string CurrentProvider => _currentProvider?.ProviderName ?? "None";

        /// <summary>
        /// Gets the supported languages;
        /// </summary>
        public IReadOnlyList<LanguageModel> SupportedLanguages => _currentProvider?.SupportedLanguages ?? new List<LanguageModel>();

        /// <summary>
        /// Gets or sets the current language for recognition;
        /// </summary>
        public LanguageModel CurrentLanguage { get; set; }

        /// <summary>
        /// Gets whether real-time recognition is enabled;
        /// </summary>
        public bool IsRealTimeEnabled => _config.EnableRealTimeRecognition;

        /// <summary>
        /// Gets the recognition confidence threshold;
        /// </summary>
        public float ConfidenceThreshold => _config.ConfidenceThreshold;

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when speech recognition starts;
        /// </summary>
        public event EventHandler<RecognitionStartedEventArgs> RecognitionStarted;

        /// <summary>
        /// Event raised when speech is recognized;
        /// </summary>
        public event EventHandler<RecognitionResultEventArgs> RecognitionResult;

        /// <summary>
        /// Event raised when recognition ends;
        /// </summary>
        public event EventHandler<RecognitionEndedEventArgs> RecognitionEnded;

        /// <summary>
        /// Event raised when an error occurs during recognition;
        /// </summary>
        public event EventHandler<RecognitionErrorEventArgs> RecognitionError;

        /// <summary>
        /// Event raised when the recognizer state changes;
        /// </summary>
        public event EventHandler<RecognitionStateChangedEventArgs> StateChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of the SpeechRecognizer;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="config">Speech recognition configuration</param>
        /// <param name="audioProcessor">Audio processing service</param>
        /// <param name="providerFactory">Recognition provider factory</param>
        public SpeechRecognizer(
            ILogger<SpeechRecognizer> logger,
            IOptions<SpeechRecognitionConfig> config,
            IAudioProcessor audioProcessor,
            IRecognitionProviderFactory providerFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _audioProcessor = audioProcessor ?? throw new ArgumentNullException(nameof(audioProcessor));
            _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));

            _activeSessions = new ConcurrentDictionary<string, RecognitionSession>();
            _recognitionSemaphore = new SemaphoreSlim(_config.MaxConcurrentRecognitions);
            _recognitionQueue = new PriorityQueue<RecognitionRequest, int>();
            _globalCancellationTokenSource = new CancellationTokenSource();

            State = SpeechRecognizerState.Stopped;
            CurrentLanguage = _config.DefaultLanguage;

            _logger.LogInformation("SpeechRecognizer initialized with {ProviderCount} available providers",
                _providerFactory.AvailableProviders.Count);
        }

        #endregion;

        #region Initialization;

        /// <summary>
        /// Initializes the speech recognizer and selects the best available provider;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("SpeechRecognizer is already initialized");
                return;
            }

            try
            {
                UpdateState(SpeechRecognizerState.Initializing);

                _logger.LogInformation("Initializing SpeechRecognizer...");

                // Initialize audio processor;
                await _audioProcessor.InitializeAsync(cancellationToken);

                // Select and initialize the best recognition provider;
                _currentProvider = await SelectBestProviderAsync(cancellationToken);

                if (_currentProvider == null)
                {
                    throw new SpeechRecognitionException("No suitable recognition provider available");
                }

                await _currentProvider.InitializeAsync(cancellationToken);

                _isInitialized = true;
                UpdateState(SpeechRecognizerState.Ready);

                _logger.LogInformation("SpeechRecognizer initialized successfully with provider: {Provider}",
                    _currentProvider.ProviderName);
            }
            catch (Exception ex)
            {
                UpdateState(SpeechRecognizerState.Error);
                _logger.LogError(ex, "Failed to initialize SpeechRecognizer");
                throw new SpeechRecognitionException("Failed to initialize speech recognizer", ex);
            }
        }

        /// <summary>
        /// Selects the best available recognition provider based on configuration and capabilities;
        /// </summary>
        private async Task<IRecognitionProvider> SelectBestProviderAsync(CancellationToken cancellationToken)
        {
            var availableProviders = _providerFactory.AvailableProviders;

            if (availableProviders.Count == 0)
            {
                throw new SpeechRecognitionException("No recognition providers are available");
            }

            // Use configured provider if specified;
            if (!string.IsNullOrEmpty(_config.PreferredProvider))
            {
                var preferredProvider = _providerFactory.GetProvider(_config.PreferredProvider);
                if (preferredProvider != null)
                {
                    _logger.LogDebug("Using configured preferred provider: {Provider}", _config.PreferredProvider);
                    return preferredProvider;
                }

                _logger.LogWarning("Configured preferred provider '{Provider}' not found, falling back to automatic selection",
                    _config.PreferredProvider);
            }

            // Evaluate and select best provider;
            var providerScores = new Dictionary<IRecognitionProvider, int>();

            foreach (var provider in availableProviders)
            {
                try
                {
                    var capabilities = await provider.GetCapabilitiesAsync(cancellationToken);
                    var score = CalculateProviderScore(capabilities);
                    providerScores[provider] = score;

                    _logger.LogDebug("Provider {Provider} scored {Score}", provider.ProviderName, score);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to evaluate provider {Provider}", provider.ProviderName);
                    providerScores[provider] = 0;
                }
            }

            var bestProvider = providerScores.OrderByDescending(p => p.Value).FirstOrDefault();

            if (bestProvider.Key == null || bestProvider.Value < _config.MinimumProviderScore)
            {
                throw new SpeechRecognitionException("No suitable recognition provider meets minimum requirements");
            }

            _logger.LogInformation("Selected provider: {Provider} with score {Score}",
                bestProvider.Key.ProviderName, bestProvider.Value);

            return bestProvider.Key;
        }

        /// <summary>
        /// Calculates a score for a recognition provider based on its capabilities;
        /// </summary>
        private int CalculateProviderScore(RecognitionCapabilities capabilities)
        {
            int score = 0;

            // Language support;
            if (capabilities.SupportedLanguages.Any(l => l.Code == CurrentLanguage.Code))
            {
                score += 50;
            }

            // Real-time support;
            if (capabilities.SupportsRealTime && _config.EnableRealTimeRecognition)
            {
                score += 30;
            }

            // Accuracy rating;
            score += (int)(capabilities.EstimatedAccuracy * 20);

            // Latency performance;
            if (capabilities.AverageLatency < TimeSpan.FromMilliseconds(500))
            {
                score += 20;
            }
            else if (capabilities.AverageLatency < TimeSpan.FromMilliseconds(1000))
            {
                score += 10;
            }

            // Offline support;
            if (capabilities.SupportsOffline && _config.PreferOffline)
            {
                score += 25;
            }

            return score;
        }

        #endregion;

        #region Recognition Methods;

        /// <summary>
        /// Starts continuous speech recognition;
        /// </summary>
        /// <param name="sessionId">Optional session identifier</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task StartContinuousRecognitionAsync(string sessionId = null, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (State == SpeechRecognizerState.Listening)
            {
                throw new SpeechRecognitionException("Recognition is already in progress");
            }

            var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _globalCancellationTokenSource.Token;
            ).Token;

            try
            {
                sessionId ??= Guid.NewGuid().ToString();

                _logger.LogInformation("Starting continuous recognition for session: {SessionId}", sessionId);

                var session = new RecognitionSession(sessionId, combinedToken);

                if (!_activeSessions.TryAdd(sessionId, session))
                {
                    throw new SpeechRecognitionException($"Session {sessionId} already exists");
                }

                UpdateState(SpeechRecognizerState.Listening);
                OnRecognitionStarted(new RecognitionStartedEventArgs(sessionId));

                // Start audio capture;
                await _audioProcessor.StartCaptureAsync(combinedToken);

                // Start recognition loop;
                _ = Task.Run(async () => await RecognitionLoopAsync(session, combinedToken), combinedToken);

                _logger.LogDebug("Continuous recognition started for session: {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                UpdateState(SpeechRecognizerState.Error);
                _logger.LogError(ex, "Failed to start continuous recognition");
                throw new SpeechRecognitionException("Failed to start continuous recognition", ex);
            }
        }

        /// <summary>
        /// Stops continuous speech recognition;
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        public async Task StopContinuousRecognitionAsync(string sessionId)
        {
            ValidateInitialized();

            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                _logger.LogWarning("Attempted to stop non-existent session: {SessionId}", sessionId);
                return;
            }

            try
            {
                _logger.LogInformation("Stopping continuous recognition for session: {SessionId}", sessionId);

                session.Cancel();

                await _audioProcessor.StopCaptureAsync();

                _activeSessions.TryRemove(sessionId, out _);

                if (_activeSessions.IsEmpty)
                {
                    UpdateState(SpeechRecognizerState.Ready);
                }

                OnRecognitionEnded(new RecognitionEndedEventArgs(sessionId, RecognitionEndReason.ManualStop));

                _logger.LogDebug("Continuous recognition stopped for session: {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping continuous recognition for session: {SessionId}", sessionId);
                throw new SpeechRecognitionException($"Error stopping recognition session {sessionId}", ex);
            }
        }

        /// <summary>
        /// Recognizes speech from an audio file;
        /// </summary>
        /// <param name="audioFilePath">Path to the audio file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<RecognitionResult> RecognizeFromFileAsync(string audioFilePath, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(audioFilePath))
            {
                throw new ArgumentException("Audio file path cannot be null or empty", nameof(audioFilePath));
            }

            if (!File.Exists(audioFilePath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
            }

            try
            {
                _logger.LogInformation("Starting file recognition: {FilePath}", audioFilePath);

                await _recognitionSemaphore.WaitAsync(cancellationToken);

                var sessionId = Guid.NewGuid().ToString();
                OnRecognitionStarted(new RecognitionStartedEventArgs(sessionId));

                // Read and process audio file;
                var audioData = await _audioProcessor.ProcessAudioFileAsync(audioFilePath, cancellationToken);

                // Perform recognition;
                var result = await _currentProvider.RecognizeAsync(audioData, CurrentLanguage, cancellationToken);

                // Apply confidence filter;
                if (result.Confidence < _config.ConfidenceThreshold)
                {
                    _logger.LogDebug("Recognition confidence {Confidence} below threshold {Threshold}",
                        result.Confidence, _config.ConfidenceThreshold);

                    result = new RecognitionResult;
                    {
                        Text = result.Text,
                        Confidence = result.Confidence,
                        IsFiltered = true,
                        Alternatives = result.Alternatives;
                    };
                }

                OnRecognitionResult(new RecognitionResultEventArgs(sessionId, result));
                OnRecognitionEnded(new RecognitionEndedEventArgs(sessionId, RecognitionEndReason.Success));

                _logger.LogDebug("File recognition completed: {FilePath}, Confidence: {Confidence}",
                    audioFilePath, result.Confidence);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Recognition from file was cancelled: {FilePath}", audioFilePath);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recognizing speech from file: {FilePath}", audioFilePath);
                OnRecognitionError(new RecognitionErrorEventArgs(
                    audioFilePath,
                    "FileRecognitionError",
                    ex.Message));

                throw new SpeechRecognitionException($"Failed to recognize speech from file: {audioFilePath}", ex);
            }
            finally
            {
                _recognitionSemaphore.Release();
            }
        }

        /// <summary>
        /// Recognizes speech from raw audio data;
        /// </summary>
        /// <param name="audioData">Raw audio data</param>
        /// <param name="audioFormat">Audio format information</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<RecognitionResult> RecognizeFromAudioDataAsync(
            byte[] audioData,
            AudioFormat audioFormat,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (audioData == null || audioData.Length == 0)
            {
                throw new ArgumentException("Audio data cannot be null or empty", nameof(audioData));
            }

            if (audioFormat == null)
            {
                throw new ArgumentNullException(nameof(audioFormat));
            }

            try
            {
                _logger.LogDebug("Starting recognition from audio data ({Length} bytes)", audioData.Length);

                await _recognitionSemaphore.WaitAsync(cancellationToken);

                var sessionId = Guid.NewGuid().ToString();
                OnRecognitionStarted(new RecognitionStartedEventArgs(sessionId));

                // Process audio data;
                var processedAudio = await _audioProcessor.ProcessRawAudioAsync(audioData, audioFormat, cancellationToken);

                // Perform recognition;
                var result = await _currentProvider.RecognizeAsync(processedAudio, CurrentLanguage, cancellationToken);

                // Apply confidence filter;
                if (result.Confidence < _config.ConfidenceThreshold)
                {
                    result.IsFiltered = true;
                }

                OnRecognitionResult(new RecognitionResultEventArgs(sessionId, result));
                OnRecognitionEnded(new RecognitionEndedEventArgs(sessionId, RecognitionEndReason.Success));

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Recognition from audio data was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recognizing speech from audio data");
                OnRecognitionError(new RecognitionErrorEventArgs(
                    "AudioDataRecognition",
                    "AudioDataRecognitionError",
                    ex.Message));

                throw new SpeechRecognitionException("Failed to recognize speech from audio data", ex);
            }
            finally
            {
                _recognitionSemaphore.Release();
            }
        }

        /// <summary>
        /// Main recognition loop for continuous recognition;
        /// </summary>
        private async Task RecognitionLoopAsync(RecognitionSession session, CancellationToken cancellationToken)
        {
            try
            {
                var audioBuffer = new byte[_config.AudioBufferSize];

                while (!cancellationToken.IsCancellationRequested && !session.IsCancelled)
                {
                    try
                    {
                        // Read audio data from capture device;
                        var audioData = await _audioProcessor.ReadAudioBufferAsync(audioBuffer, cancellationToken);

                        if (audioData.Length == 0)
                        {
                            await Task.Delay(10, cancellationToken);
                            continue;
                        }

                        // Check for speech activity;
                        var hasSpeech = await _audioProcessor.DetectSpeechActivityAsync(audioData, cancellationToken);

                        if (!hasSpeech)
                        {
                            continue;
                        }

                        // Process and recognize the audio chunk;
                        var processedAudio = await _audioProcessor.ProcessAudioChunkAsync(audioData, cancellationToken);

                        var recognitionTask = _currentProvider.RecognizeAsync(
                            processedAudio,
                            CurrentLanguage,
                            cancellationToken);

                        // Queue the recognition task with priority;
                        var request = new RecognitionRequest(
                            session.SessionId,
                            recognitionTask,
                            DateTime.UtcNow,
                            _config.RecognitionPriority);

                        _recognitionQueue.Enqueue(request, request.Priority);

                        // Process queued recognition tasks;
                        await ProcessRecognitionQueueAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in recognition loop for session: {SessionId}", session.SessionId);

                        if (_config.ContinueOnRecognitionError)
                        {
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }

                        break;
                    }
                }
            }
            finally
            {
                _activeSessions.TryRemove(session.SessionId, out _);

                if (_activeSessions.IsEmpty)
                {
                    UpdateState(SpeechRecognizerState.Ready);
                }
            }
        }

        /// <summary>
        /// Processes the recognition task queue;
        /// </summary>
        private async Task ProcessRecognitionQueueAsync(CancellationToken cancellationToken)
        {
            while (_recognitionQueue.TryDequeue(out var request, out _))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var result = await request.RecognitionTask;

                    if (result.Confidence >= _config.ConfidenceThreshold)
                    {
                        OnRecognitionResult(new RecognitionResultEventArgs(
                            request.SessionId,
                            result,
                            request.Timestamp));
                    }
                    else;
                    {
                        _logger.LogDebug("Recognition result filtered due to low confidence: {Confidence}",
                            result.Confidence);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing recognition task for session: {SessionId}",
                        request.SessionId);
                }
            }
        }

        #endregion;

        #region Configuration Methods;

        /// <summary>
        /// Updates the recognition configuration;
        /// </summary>
        /// <param name="configUpdates">Configuration updates</param>
        public void UpdateConfiguration(Action<SpeechRecognitionConfig> configUpdates)
        {
            if (configUpdates == null)
            {
                throw new ArgumentNullException(nameof(configUpdates));
            }

            try
            {
                _logger.LogInformation("Updating speech recognition configuration");

                configUpdates(_config);

                // Reinitialize if necessary;
                if (State != SpeechRecognizerState.Stopped && State != SpeechRecognizerState.Error)
                {
                    _ = ReinitializeIfNeededAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating speech recognition configuration");
                throw new SpeechRecognitionException("Failed to update configuration", ex);
            }
        }

        /// <summary>
        /// Sets the recognition language;
        /// </summary>
        /// <param name="languageCode">Language code (e.g., "en-US")</param>
        public async Task SetLanguageAsync(string languageCode)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(languageCode))
            {
                throw new ArgumentException("Language code cannot be null or empty", nameof(languageCode));
            }

            var language = SupportedLanguages.FirstOrDefault(l => l.Code == languageCode);

            if (language == null)
            {
                throw new SpeechRecognitionException($"Language '{languageCode}' is not supported by the current provider");
            }

            if (CurrentLanguage.Code == languageCode)
            {
                _logger.LogDebug("Language is already set to {Language}", languageCode);
                return;
            }

            try
            {
                _logger.LogInformation("Changing recognition language from {OldLanguage} to {NewLanguage}",
                    CurrentLanguage.Name, language.Name);

                // Check if current provider supports the new language;
                if (!_currentProvider.SupportedLanguages.Any(l => l.Code == languageCode))
                {
                    // Try to switch to a provider that supports the language;
                    var newProvider = _providerFactory.GetProviderForLanguage(languageCode);

                    if (newProvider == null)
                    {
                        throw new SpeechRecognitionException($"No available provider supports language '{languageCode}'");
                    }

                    await SwitchProviderAsync(newProvider);
                }

                CurrentLanguage = language;

                // Update provider language;
                await _currentProvider.SetLanguageAsync(languageCode);

                _logger.LogInformation("Recognition language changed to {Language}", language.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to change recognition language to {Language}", languageCode);
                throw new SpeechRecognitionException($"Failed to set language to '{languageCode}'", ex);
            }
        }

        /// <summary>
        /// Switches to a different recognition provider;
        /// </summary>
        /// <param name="providerName">Name of the provider to switch to</param>
        public async Task SwitchProviderAsync(string providerName)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new ArgumentException("Provider name cannot be null or empty", nameof(providerName));
            }

            var newProvider = _providerFactory.GetProvider(providerName);

            if (newProvider == null)
            {
                throw new SpeechRecognitionException($"Provider '{providerName}' not found");
            }

            await SwitchProviderAsync(newProvider);
        }

        /// <summary>
        /// Switches to a different recognition provider;
        /// </summary>
        /// <param name="newProvider">New provider instance</param>
        private async Task SwitchProviderAsync(IRecognitionProvider newProvider)
        {
            if (newProvider == _currentProvider)
            {
                _logger.LogDebug("Provider is already {Provider}", newProvider.ProviderName);
                return;
            }

            try
            {
                _logger.LogInformation("Switching recognition provider from {OldProvider} to {NewProvider}",
                    _currentProvider.ProviderName, newProvider.ProviderName);

                // Stop current recognition if active;
                var wasListening = State == SpeechRecognizerState.Listening;
                var activeSessions = _activeSessions.Keys.ToList();

                if (wasListening)
                {
                    foreach (var sessionId in activeSessions)
                    {
                        await StopContinuousRecognitionAsync(sessionId);
                    }
                }

                // Initialize new provider;
                await newProvider.InitializeAsync();

                // Set language on new provider;
                await newProvider.SetLanguageAsync(CurrentLanguage.Code);

                // Switch provider;
                _currentProvider = newProvider;

                // Restart recognition if it was active;
                if (wasListening)
                {
                    foreach (var sessionId in activeSessions)
                    {
                        await StartContinuousRecognitionAsync(sessionId);
                    }
                }

                _logger.LogInformation("Successfully switched to provider: {Provider}", newProvider.ProviderName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to switch recognition provider to {Provider}",
                    newProvider.ProviderName);
                throw new SpeechRecognitionException($"Failed to switch to provider '{newProvider.ProviderName}'", ex);
            }
        }

        /// <summary>
        /// Reinitializes the recognizer if configuration changes require it;
        /// </summary>
        private async Task ReinitializeIfNeededAsync()
        {
            try
            {
                if (!_isInitialized || State == SpeechRecognizerState.Error)
                {
                    await InitializeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reinitialize speech recognizer");
            }
        }

        #endregion;

        #region Helper Methods;

        /// <summary>
        /// Validates that the recognizer is initialized;
        /// </summary>
        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new SpeechRecognitionException("Speech recognizer is not initialized. Call InitializeAsync first.");
            }

            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(SpeechRecognizer));
            }
        }

        /// <summary>
        /// Updates the recognizer state and raises the StateChanged event;
        /// </summary>
        private void UpdateState(SpeechRecognizerState newState)
        {
            if (State == newState)
            {
                return;
            }

            var oldState = State;
            State = newState;

            _logger.LogDebug("SpeechRecognizer state changed: {OldState} -> {NewState}", oldState, newState);

            OnStateChanged(new RecognitionStateChangedEventArgs(oldState, newState));
        }

        /// <summary>
        /// Gets diagnostic information about the recognizer;
        /// </summary>
        public async Task<RecognitionDiagnostics> GetDiagnosticsAsync()
        {
            ValidateInitialized();

            return new RecognitionDiagnostics;
            {
                State = State,
                CurrentProvider = CurrentProvider,
                CurrentLanguage = CurrentLanguage,
                IsInitialized = _isInitialized,
                ActiveSessions = _activeSessions.Count,
                ProviderCapabilities = await _currentProvider.GetCapabilitiesAsync(),
                AudioProcessorStatus = _audioProcessor.GetStatus(),
                MemoryUsage = GC.GetTotalMemory(false),
                Timestamp = DateTime.UtcNow;
            };
        }

        #endregion;

        #region Event Handlers;

        /// <summary>
        /// Raises the RecognitionStarted event;
        /// </summary>
        protected virtual void OnRecognitionStarted(RecognitionStartedEventArgs e)
        {
            RecognitionStarted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the RecognitionResult event;
        /// </summary>
        protected virtual void OnRecognitionResult(RecognitionResultEventArgs e)
        {
            RecognitionResult?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the RecognitionEnded event;
        /// </summary>
        protected virtual void OnRecognitionEnded(RecognitionEndedEventArgs e)
        {
            RecognitionEnded?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the RecognitionError event;
        /// </summary>
        protected virtual void OnRecognitionError(RecognitionErrorEventArgs e)
        {
            RecognitionError?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the StateChanged event;
        /// </summary>
        protected virtual void OnStateChanged(RecognitionStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Disposes the speech recognizer and releases all resources;
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
            {
                return;
            }

            if (disposing)
            {
                _logger.LogInformation("Disposing SpeechRecognizer...");

                try
                {
                    // Cancel all ongoing operations;
                    _globalCancellationTokenSource.Cancel();

                    // Stop all active sessions;
                    foreach (var sessionId in _activeSessions.Keys.ToList())
                    {
                        try
                        {
                            StopContinuousRecognitionAsync(sessionId).Wait(TimeSpan.FromSeconds(5));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error stopping session during disposal: {SessionId}", sessionId);
                        }
                    }

                    // Dispose resources;
                    _currentProvider?.Dispose();
                    _audioProcessor.Dispose();
                    _recognitionSemaphore.Dispose();
                    _globalCancellationTokenSource.Dispose();

                    _activeSessions.Clear();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during SpeechRecognizer disposal");
                }

                UpdateState(SpeechRecognizerState.Stopped);
                _isDisposed = true;

                _logger.LogInformation("SpeechRecognizer disposed successfully");
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~SpeechRecognizer()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Interface for speech recognition capabilities;
    /// </summary>
    public interface ISpeechRecognizer;
    {
        /// <summary>
        /// Gets the current state of the speech recognizer;
        /// </summary>
        SpeechRecognizerState State { get; }

        /// <summary>
        /// Gets the current recognition provider in use;
        /// </summary>
        string CurrentProvider { get; }

        /// <summary>
        /// Gets the supported languages;
        /// </summary>
        IReadOnlyList<LanguageModel> SupportedLanguages { get; }

        /// <summary>
        /// Gets or sets the current language for recognition;
        /// </summary>
        LanguageModel CurrentLanguage { get; set; }

        /// <summary>
        /// Gets whether real-time recognition is enabled;
        /// </summary>
        bool IsRealTimeEnabled { get; }

        /// <summary>
        /// Gets the recognition confidence threshold;
        /// </summary>
        float ConfidenceThreshold { get; }

        // Events;
        event EventHandler<RecognitionStartedEventArgs> RecognitionStarted;
        event EventHandler<RecognitionResultEventArgs> RecognitionResult;
        event EventHandler<RecognitionEndedEventArgs> RecognitionEnded;
        event EventHandler<RecognitionErrorEventArgs> RecognitionError;
        event EventHandler<RecognitionStateChangedEventArgs> StateChanged;

        // Methods;
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task StartContinuousRecognitionAsync(string sessionId = null, CancellationToken cancellationToken = default);
        Task StopContinuousRecognitionAsync(string sessionId);
        Task<RecognitionResult> RecognizeFromFileAsync(string audioFilePath, CancellationToken cancellationToken = default);
        Task<RecognitionResult> RecognizeFromAudioDataAsync(byte[] audioData, AudioFormat audioFormat, CancellationToken cancellationToken = default);
        Task SetLanguageAsync(string languageCode);
        Task SwitchProviderAsync(string providerName);
        Task<RecognitionDiagnostics> GetDiagnosticsAsync();
        void UpdateConfiguration(Action<SpeechRecognitionConfig> configUpdates);
    }

    /// <summary>
    /// Speech recognizer state enumeration;
    /// </summary>
    public enum SpeechRecognizerState;
    {
        Stopped,
        Initializing,
        Ready,
        Listening,
        Processing,
        Error;
    }

    /// <summary>
    /// Recognition session for continuous recognition;
    /// </summary>
    internal class RecognitionSession;
    {
        public string SessionId { get; }
        public CancellationToken CancellationToken { get; }
        public DateTime StartTime { get; }
        public bool IsCancelled { get; private set; }

        private readonly CancellationTokenSource _cancellationTokenSource;

        public RecognitionSession(string sessionId, CancellationToken cancellationToken)
        {
            SessionId = sessionId;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken = _cancellationTokenSource.Token;
            StartTime = DateTime.UtcNow;
        }

        public void Cancel()
        {
            if (!IsCancelled)
            {
                IsCancelled = true;
                _cancellationTokenSource.Cancel();
            }
        }
    }

    /// <summary>
    /// Recognition request for queued processing;
    /// </summary>
    internal class RecognitionRequest;
    {
        public string SessionId { get; }
        public Task<RecognitionResult> RecognitionTask { get; }
        public DateTime Timestamp { get; }
        public int Priority { get; }

        public RecognitionRequest(string sessionId, Task<RecognitionResult> recognitionTask, DateTime timestamp, int priority)
        {
            SessionId = sessionId;
            RecognitionTask = recognitionTask;
            Timestamp = timestamp;
            Priority = priority;
        }
    }

    #endregion;
}
