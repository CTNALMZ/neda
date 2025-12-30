using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NEDA.Core.Common;
using NEDA.Core.Common.Constants;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.SystemControl;
using NEDA.Core.Monitoring;

namespace NEDA.MediaProcessing.AudioProcessing.MusicComposition;
{
    /// <summary>
    /// Advanced MIDI Engine for music composition, sequencing, and real-time MIDI processing.
    /// Supports MIDI file I/O, real-time playback, recording, and extensive MIDI manipulation.
    /// </summary>
    public class MIDIEngine : IMIDIEngine, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger<MIDIEngine> _logger;
        private readonly ISecurityManager _securityManager;
        private readonly ISystemManager _systemManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly MIDIConfiguration _configuration;

        private bool _isInitialized;
        private bool _isDisposed;
        private readonly object _syncLock = new object();

        private readonly Dictionary<string, MIDIFile> _loadedSequences;
        private readonly ConcurrentDictionary<int, MIDIChannel> _channels;
        private readonly Dictionary<string, MIDIPort> _midiPorts;
        private readonly MIDIEngineState _engineState;
        private readonly SemaphoreSlim _operationSemaphore;

        private Thread _midiProcessingThread;
        private Thread _midiClockThread;
        private CancellationTokenSource _processingCancellationTokenSource;
        private Stopwatch _playbackStopwatch;

        private IMIDIOutputDevice _outputDevice;
        private IMIDIInputDevice _inputDevice;
        private MIDISequencer _sequencer;

        private const int MAX_CONCURRENT_OPERATIONS = 15;
        private const int DEFAULT_TEMPO = 120; // BPM;
        private const int DEFAULT_PPQ = 480; // Pulses per quarter note;
        private const int DEFAULT_MIDI_CHANNELS = 16;
        private const int MIDI_CLOCK_TICKS_PER_QUARTER = 24;

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when MIDI file is loaded;
        /// </summary>
        public event EventHandler<MIDIFileLoadedEventArgs> FileLoaded;

        /// <summary>
        /// Event raised when MIDI playback starts;
        /// </summary>
        public event EventHandler<MIDIPlaybackStartedEventArgs> PlaybackStarted;

        /// <summary>
        /// Event raised when MIDI playback stops;
        /// </summary>
        public event EventHandler<MIDIPlaybackStoppedEventArgs> PlaybackStopped;

        /// <summary>
        /// Event raised when MIDI playback position changes;
        /// </summary>
        public event EventHandler<MIDIPlaybackPositionChangedEventArgs> PlaybackPositionChanged;

        /// <summary>
        /// Event raised when MIDI event is received;
        /// </summary>
        public event EventHandler<MIDIEventReceivedEventArgs> EventReceived;

        /// <summary>
        /// Event raised when MIDI event is sent;
        /// </summary>
        public event EventHandler<MIDIEventSentEventArgs> EventSent;

        /// <summary>
        /// Event raised when MIDI recording starts;
        /// </summary>
        public event EventHandler<MIDIRecordingStartedEventArgs> RecordingStarted;

        /// <summary>
        /// Event raised when MIDI recording stops;
        /// </summary>
        public event EventHandler<MIDIRecordingStoppedEventArgs> RecordingStopped;

        /// <summary>
        /// Event raised when MIDI error occurs;
        /// </summary>
        public event EventHandler<MIDIErrorEventArgs> MIDIError;

        /// <summary>
        /// Event raised when MIDI engine state changes;
        /// </summary>
        public event EventHandler<MIDIEngineStateChangedEventArgs> EngineStateChanged;

        /// <summary>
        /// Event raised when tempo changes;
        /// </summary>
        public event EventHandler<TempoChangedEventArgs> TempoChanged;

        /// <summary>
        /// Event raised when time signature changes;
        /// </summary>
        public event EventHandler<TimeSignatureChangedEventArgs> TimeSignatureChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of MIDIEngine;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="securityManager">Security manager instance</param>
        /// <param name="systemManager">System manager instance</param>
        /// <param name="performanceMonitor">Performance monitor instance</param>
        /// <param name="configuration">MIDI engine configuration</param>
        public MIDIEngine(
            ILogger<MIDIEngine> logger,
            ISecurityManager securityManager,
            ISystemManager systemManager,
            IPerformanceMonitor performanceMonitor,
            MIDIConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            _configuration = configuration ?? new MIDIConfiguration();
            _loadedSequences = new Dictionary<string, MIDIFile>(StringComparer.OrdinalIgnoreCase);
            _channels = new ConcurrentDictionary<int, MIDIChannel>();
            _midiPorts = new Dictionary<string, MIDIPort>(StringComparer.OrdinalIgnoreCase);
            _engineState = new MIDIEngineState();
            _operationSemaphore = new SemaphoreSlim(MAX_CONCURRENT_OPERATIONS, MAX_CONCURRENT_OPERATIONS);
            _playbackStopwatch = new Stopwatch();

            InitializeChannels();

            _logger.LogInformation(LogEvents.MIDIEngineCreated,
                "MIDI Engine initialized with configuration: {@Configuration}",
                _configuration);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the MIDI engine with specified devices;
        /// </summary>
        /// <param name="outputDeviceName">MIDI output device name</param>
        /// <param name="inputDeviceName">MIDI input device name</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the initialization operation</returns>
        public async Task InitializeAsync(
            string outputDeviceName = null,
            string inputDeviceName = null,
            CancellationToken cancellationToken = default)
        {
            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIEngineInitialize);

            lock (_syncLock)
            {
                if (_isInitialized)
                    throw new InvalidOperationException("MIDI engine is already initialized");

                _engineState.Status = MIDIEngineStatus.Initializing;
            }

            var operationId = Guid.NewGuid().ToString();

            _logger.LogInformation(LogEvents.MIDIEngineInitializing,
                "Initializing MIDI engine: Output: {OutputDevice}, Input: {InputDevice}, Operation: {OperationId}",
                outputDeviceName, inputDeviceName, operationId);

            try
            {
                OnEngineStateChanged(new MIDIEngineStateChangedEventArgs(
                    _engineState.Status,
                    "Initializing MIDI engine",
                    operationId));

                // Initialize MIDI output device;
                if (!string.IsNullOrEmpty(outputDeviceName))
                {
                    _outputDevice = await CreateMIDIOutputDeviceAsync(outputDeviceName, cancellationToken);
                    await _outputDevice.InitializeAsync(cancellationToken);

                    _engineState.OutputDeviceName = outputDeviceName;
                    _engineState.OutputDeviceAvailable = true;

                    _logger.LogInformation(LogEvents.MIDIDeviceInitialized,
                        "MIDI output device initialized: {DeviceName}", outputDeviceName);
                }

                // Initialize MIDI input device;
                if (!string.IsNullOrEmpty(inputDeviceName))
                {
                    _inputDevice = await CreateMIDIInputDeviceAsync(inputDeviceName, cancellationToken);
                    await _inputDevice.InitializeAsync(cancellationToken);

                    // Subscribe to input events;
                    _inputDevice.EventReceived += OnInputDeviceEventReceived;

                    _engineState.InputDeviceName = inputDeviceName;
                    _engineState.InputDeviceAvailable = true;

                    _logger.LogInformation(LogEvents.MIDIDeviceInitialized,
                        "MIDI input device initialized: {DeviceName}", inputDeviceName);
                }

                // Initialize sequencer;
                _sequencer = new MIDISequencer(_logger, _configuration);
                _sequencer.PlaybackStarted += OnSequencerPlaybackStarted;
                _sequencer.PlaybackStopped += OnSequencerPlaybackStopped;
                _sequencer.PositionChanged += OnSequencerPositionChanged;
                _sequencer.TempoChanged += OnSequencerTempoChanged;
                _sequencer.TimeSignatureChanged += OnSequencerTimeSignatureChanged;
                _sequencer.EventPlayed += OnSequencerEventPlayed;

                // Start processing threads;
                StartMIDIProcessingThread();
                StartMIDIClockThread();

                lock (_syncLock)
                {
                    _isInitialized = true;
                    _engineState.Status = MIDIEngineStatus.Ready;
                }

                OnEngineStateChanged(new MIDIEngineStateChangedEventArgs(
                    _engineState.Status,
                    "MIDI engine initialized successfully",
                    operationId));

                _logger.LogInformation(LogEvents.MIDIEngineInitialized,
                    "MIDI engine initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIEngineInitializationFailed, ex,
                    "MIDI engine initialization failed: Operation {OperationId}",
                    operationId);

                lock (_syncLock)
                {
                    _engineState.Status = MIDIEngineStatus.Error;
                    _engineState.Error = ex.Message;
                }

                OnEngineStateChanged(new MIDIEngineStateChangedEventArgs(
                    MIDIEngineStatus.Error,
                    $"Initialization failed: {ex.Message}",
                    operationId));

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.InitializationError,
                    $"MIDI engine initialization failed: {ex.Message}",
                    ex,
                    operationId));

                throw new MIDIEngineInitializationException(
                    $"Failed to initialize MIDI engine",
                    ex,
                    operationId);
            }
        }

        /// <summary>
        /// Loads a MIDI file;
        /// </summary>
        /// <param name="filePath">Path to MIDI file</param>
        /// <param name="sequenceId">Unique identifier for the sequence</param>
        /// <param name="loadOptions">MIDI file loading options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Loaded MIDI file</returns>
        public async Task<MIDIFile> LoadMIDIFileAsync(
            string filePath,
            string sequenceId = null,
            MIDILoadOptions loadOptions = null,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();
            ValidateFilePath(filePath);

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIFileLoad);

            var operationId = Guid.NewGuid().ToString();
            var options = loadOptions ?? new MIDILoadOptions();
            var id = sequenceId ?? Path.GetFileNameWithoutExtension(filePath);

            _logger.LogInformation(LogEvents.MIDIFileLoading,
                "Loading MIDI file: {FilePath}, SequenceId: {SequenceId}, Operation: {OperationId}",
                filePath, id, operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                lock (_syncLock)
                {
                    if (_loadedSequences.ContainsKey(id))
                        throw new MIDIFileException($"MIDI sequence with ID '{id}' is already loaded", id);
                }

                // Check file existence and permissions;
                if (!File.Exists(filePath))
                    throw new FileNotFoundException($"MIDI file not found: {filePath}");

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > (_configuration.MaxMIDIFileSize ?? 10 * 1024 * 1024)) // 10MB default;
                    throw new MIDIFileException($"MIDI file too large: {fileInfo.Length} bytes", id);

                // Read MIDI file;
                var midiData = await File.ReadAllBytesAsync(filePath, cancellationToken);

                // Parse MIDI file;
                var midiFile = await ParseMIDIFileAsync(midiData, options, cancellationToken);

                // Configure sequence;
                midiFile.Id = id;
                midiFile.FilePath = filePath;
                midiFile.Name = Path.GetFileNameWithoutExtension(filePath);
                midiFile.FileSize = fileInfo.Length;
                midiFile.LoadOptions = options;
                midiFile.Metadata = new Dictionary<string, object>
                {
                    { "OriginalFilePath", filePath },
                    { "LoadTime", DateTime.UtcNow },
                    { "OperationId", operationId }
                };

                // Apply post-processing if specified;
                if (options.NormalizeNoteVelocities)
                {
                    await NormalizeNoteVelocitiesAsync(midiFile);
                }

                if (options.QuantizeNotes)
                {
                    await QuantizeNotesAsync(midiFile, options.QuantizeValue);
                }

                if (options.RemoveOverlappingNotes)
                {
                    await RemoveOverlappingNotesAsync(midiFile);
                }

                // Store sequence;
                lock (_syncLock)
                {
                    _loadedSequences[id] = midiFile;
                }

                // Update engine state;
                _engineState.LoadedSequencesCount++;

                OnFileLoaded(new MIDIFileLoadedEventArgs(
                    midiFile,
                    operationId,
                    fileInfo.Length,
                    midiFile.Format,
                    midiFile.TrackCount));

                _logger.LogInformation(LogEvents.MIDIFileLoaded,
                    "MIDI file loaded: {SequenceId}, Format: {Format}, Tracks: {TrackCount}, Duration: {Duration}",
                    id, midiFile.Format, midiFile.TrackCount, midiFile.Duration);

                return midiFile;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI file loading cancelled: {FilePath}, Operation: {OperationId}",
                    filePath, operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIFileLoadFailed, ex,
                    "Failed to load MIDI file: {FilePath}, Operation: {OperationId}",
                    filePath, operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.FileLoadError,
                    $"Failed to load MIDI file: {filePath}",
                    ex,
                    operationId));

                throw new MIDIFileException(
                    $"Failed to load MIDI file from {filePath}",
                    ex,
                    id,
                    filePath,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Saves a MIDI file;
        /// </summary>
        /// <param name="midiFile">MIDI file to save</param>
        /// <param name="filePath">Output file path</param>
        /// <param name="saveOptions">MIDI file saving options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the save operation</returns>
        public async Task SaveMIDIFileAsync(
            MIDIFile midiFile,
            string filePath,
            MIDISaveOptions saveOptions = null,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            if (midiFile == null)
                throw new ArgumentNullException(nameof(midiFile));

            ValidateFilePath(filePath);

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIFileSave);

            var operationId = Guid.NewGuid().ToString();
            var options = saveOptions ?? new MIDISaveOptions();

            _logger.LogInformation(LogEvents.MIDIFileSaving,
                "Saving MIDI file: {FilePath}, SequenceId: {SequenceId}, Operation: {OperationId}",
                filePath, midiFile.Id, operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                // Ensure output directory exists;
                var outputDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // Apply pre-save transformations if specified;
                if (options.CompressTracks)
                {
                    await CompressTracksAsync(midiFile);
                }

                if (options.OptimizeEvents)
                {
                    await OptimizeEventsAsync(midiFile);
                }

                // Serialize MIDI file to bytes;
                var midiData = await SerializeMIDIFileAsync(midiFile, options, cancellationToken);

                // Write to file;
                await File.WriteAllBytesAsync(filePath, midiData, cancellationToken);

                _logger.LogInformation(LogEvents.MIDIFileSaved,
                    "MIDI file saved: {FilePath}, SequenceId: {SequenceId}, Size: {Size} bytes",
                    filePath, midiFile.Id, midiData.Length);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI file saving cancelled: {FilePath}, Operation: {OperationId}",
                    filePath, operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIFileSaveFailed, ex,
                    "Failed to save MIDI file: {FilePath}, Operation: {OperationId}",
                    filePath, operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.FileSaveError,
                    $"Failed to save MIDI file: {filePath}",
                    ex,
                    operationId));

                throw new MIDIFileException(
                    $"Failed to save MIDI file to {filePath}",
                    ex,
                    midiFile.Id,
                    filePath,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Plays a MIDI sequence;
        /// </summary>
        /// <param name="sequenceId">MIDI sequence identifier</param>
        /// <param name="playOptions">Playback options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the playback operation</returns>
        public async Task PlaySequenceAsync(
            string sequenceId,
            MIDIPlayOptions playOptions = null,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();
            ValidateSequenceId(sequenceId);

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIPlayback);

            var operationId = Guid.NewGuid().ToString();
            var options = playOptions ?? new MIDIPlayOptions();

            _logger.LogInformation(LogEvents.MIDIPlaybackStarting,
                "Starting MIDI playback: SequenceId: {SequenceId}, Operation: {OperationId}",
                sequenceId, operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                // Get MIDI file;
                MIDIFile midiFile;
                lock (_syncLock)
                {
                    if (!_loadedSequences.TryGetValue(sequenceId, out midiFile))
                        throw new MIDIFileException($"MIDI sequence '{sequenceId}' is not loaded", sequenceId);
                }

                // Configure sequencer;
                _sequencer.LoadSequence(midiFile);
                _sequencer.Loop = options.Loop;
                _sequencer.LoopStart = options.LoopStart;
                _sequencer.LoopEnd = options.LoopEnd;
                _sequencer.TempoScale = options.TempoScale ?? 1.0;

                // Set playback position;
                if (options.StartPosition.HasValue)
                {
                    _sequencer.SetPosition(options.StartPosition.Value);
                }

                // Start playback;
                _sequencer.Start();

                // Update engine state;
                _engineState.IsPlaying = true;
                _engineState.CurrentSequenceId = sequenceId;
                _engineState.PlaybackStartTime = DateTime.UtcNow;

                _logger.LogInformation(LogEvents.MIDIPlaybackStarted,
                    "MIDI playback started: SequenceId: {SequenceId}, Tempo: {Tempo}, Position: {Position}",
                    sequenceId, _sequencer.Tempo, _sequencer.Position);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI playback start cancelled: SequenceId: {SequenceId}, Operation: {OperationId}",
                    sequenceId, operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIPlaybackStartFailed, ex,
                    "Failed to start MIDI playback: SequenceId: {SequenceId}, Operation: {OperationId}",
                    sequenceId, operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.PlaybackError,
                    $"Failed to start MIDI playback for sequence '{sequenceId}'",
                    ex,
                    operationId));

                throw new MIDIPlaybackException(
                    $"Failed to start MIDI playback for sequence '{sequenceId}'",
                    ex,
                    sequenceId,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Stops MIDI playback;
        /// </summary>
        /// <param name="stopOptions">Stop options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the stop operation</returns>
        public async Task StopPlaybackAsync(
            MIDIStopOptions stopOptions = null,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIPlayback);

            var operationId = Guid.NewGuid().ToString();
            var options = stopOptions ?? new MIDIStopOptions();

            _logger.LogDebug(LogEvents.MIDIPlaybackStopping,
                "Stopping MIDI playback: Operation: {OperationId}", operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                if (!_engineState.IsPlaying)
                {
                    _logger.LogWarning(LogEvents.MIDIPlaybackNotActive,
                        "No active MIDI playback to stop");
                    return;
                }

                // Stop sequencer;
                _sequencer.Stop();

                // Update engine state;
                _engineState.IsPlaying = false;
                _engineState.PlaybackEndTime = DateTime.UtcNow;
                _engineState.PlaybackDuration = _engineState.PlaybackEndTime - _engineState.PlaybackStartTime;

                _logger.LogInformation(LogEvents.MIDIPlaybackStopped,
                    "MIDI playback stopped: SequenceId: {SequenceId}, Duration: {Duration}",
                    _engineState.CurrentSequenceId, _engineState.PlaybackDuration);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI playback stop cancelled: Operation: {OperationId}", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIPlaybackStopFailed, ex,
                    "Failed to stop MIDI playback: Operation: {OperationId}", operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.PlaybackError,
                    "Failed to stop MIDI playback",
                    ex,
                    operationId));

                throw new MIDIPlaybackException(
                    "Failed to stop MIDI playback",
                    ex,
                    null,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Pauses MIDI playback;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the pause operation</returns>
        public async Task PausePlaybackAsync(CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIPlayback);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogDebug(LogEvents.MIDIPlaybackPausing,
                "Pausing MIDI playback: Operation: {OperationId}", operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                if (!_engineState.IsPlaying)
                {
                    _logger.LogWarning(LogEvents.MIDIPlaybackNotActive,
                        "No active MIDI playback to pause");
                    return;
                }

                // Pause sequencer;
                _sequencer.Pause();

                // Update engine state;
                _engineState.IsPlaying = false;
                _engineState.IsPaused = true;

                _logger.LogInformation(LogEvents.MIDIPlaybackPaused,
                    "MIDI playback paused: SequenceId: {SequenceId}",
                    _engineState.CurrentSequenceId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI playback pause cancelled: Operation: {OperationId}", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIPlaybackPauseFailed, ex,
                    "Failed to pause MIDI playback: Operation: {OperationId}", operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.PlaybackError,
                    "Failed to pause MIDI playback",
                    ex,
                    operationId));

                throw new MIDIPlaybackException(
                    "Failed to pause MIDI playback",
                    ex,
                    null,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Resumes MIDI playback;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the resume operation</returns>
        public async Task ResumePlaybackAsync(CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIPlayback);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogDebug(LogEvents.MIDIPlaybackResuming,
                "Resuming MIDI playback: Operation: {OperationId}", operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                if (!_engineState.IsPaused)
                {
                    _logger.LogWarning(LogEvents.MIDIPlaybackNotPaused,
                        "MIDI playback is not paused");
                    return;
                }

                // Resume sequencer;
                _sequencer.Resume();

                // Update engine state;
                _engineState.IsPlaying = true;
                _engineState.IsPaused = false;

                _logger.LogInformation(LogEvents.MIDIPlaybackResumed,
                    "MIDI playback resumed: SequenceId: {SequenceId}",
                    _engineState.CurrentSequenceId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI playback resume cancelled: Operation: {OperationId}", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIPlaybackResumeFailed, ex,
                    "Failed to resume MIDI playback: Operation: {OperationId}", operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.PlaybackError,
                    "Failed to resume MIDI playback",
                    ex,
                    operationId));

                throw new MIDIPlaybackException(
                    "Failed to resume MIDI playback",
                    ex,
                    null,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Starts MIDI recording;
        /// </summary>
        /// <param name="recordOptions">Recording options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the recording start operation</returns>
        public async Task StartRecordingAsync(
            MIDIRecordOptions recordOptions = null,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            if (!_engineState.InputDeviceAvailable)
                throw new MIDIDeviceException("MIDI input device is not available");

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIRecording);

            var operationId = Guid.NewGuid().ToString();
            var options = recordOptions ?? new MIDIRecordOptions();

            _logger.LogInformation(LogEvents.MIDIRecordingStarting,
                "Starting MIDI recording: Operation: {OperationId}", operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                if (_engineState.IsRecording)
                    throw new MIDIRecordingException("Recording is already in progress");

                // Create new recording track;
                var recordingTrack = new MIDITrack;
                {
                    Id = $"recording_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                    Name = $"Recording {DateTime.UtcNow:HH:mm:ss}",
                    Channel = options.RecordChannel ?? -1, // -1 for all channels;
                    Events = new List<MIDIEvent>()
                };

                // Configure recording;
                _engineState.IsRecording = true;
                _engineState.RecordingStartTime = DateTime.UtcNow;
                _engineState.RecordingTrack = recordingTrack;
                _engineState.RecordOptions = options;

                // Start recording clock;
                _playbackStopwatch.Restart();

                // Enable recording on input device;
                await _inputDevice.StartRecordingAsync(cancellationToken);

                OnRecordingStarted(new MIDIRecordingStartedEventArgs(
                    recordingTrack,
                    operationId,
                    options));

                _logger.LogInformation(LogEvents.MIDIRecordingStarted,
                    "MIDI recording started: TrackId: {TrackId}, Channel: {Channel}",
                    recordingTrack.Id, options.RecordChannel);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI recording start cancelled: Operation: {OperationId}", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIRecordingStartFailed, ex,
                    "Failed to start MIDI recording: Operation: {OperationId}", operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.RecordingError,
                    "Failed to start MIDI recording",
                    ex,
                    operationId));

                throw new MIDIRecordingException(
                    "Failed to start MIDI recording",
                    ex,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Stops MIDI recording;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Recorded MIDI track</returns>
        public async Task<MIDITrack> StopRecordingAsync(CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIRecording);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogDebug(LogEvents.MIDIRecordingStopping,
                "Stopping MIDI recording: Operation: {OperationId}", operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                if (!_engineState.IsRecording)
                {
                    _logger.LogWarning(LogEvents.MIDIRecordingNotActive,
                        "No active MIDI recording to stop");
                    return null;
                }

                // Stop recording on input device;
                await _inputDevice.StopRecordingAsync(cancellationToken);

                // Stop recording clock;
                _playbackStopwatch.Stop();

                // Get recorded track;
                var recordedTrack = _engineState.RecordingTrack;

                // Update recording metadata;
                _engineState.IsRecording = false;
                _engineState.RecordingEndTime = DateTime.UtcNow;
                _engineState.RecordingDuration = _engineState.RecordingEndTime - _engineState.RecordingStartTime;

                // Post-process recorded track;
                if (_engineState.RecordOptions?.QuantizeRecordedNotes == true)
                {
                    await QuantizeTrackAsync(recordedTrack, _engineState.RecordOptions.QuantizeValue);
                }

                if (_engineState.RecordOptions?.RemoveNoise == true)
                {
                    await RemoveNoiseFromTrackAsync(recordedTrack);
                }

                // Clear recording state;
                _engineState.RecordingTrack = null;
                _engineState.RecordOptions = null;

                OnRecordingStopped(new MIDIRecordingStoppedEventArgs(
                    recordedTrack,
                    operationId,
                    _engineState.RecordingDuration,
                    recordedTrack.Events.Count));

                _logger.LogInformation(LogEvents.MIDIRecordingStopped,
                    "MIDI recording stopped: TrackId: {TrackId}, Duration: {Duration}, Events: {EventCount}",
                    recordedTrack.Id, _engineState.RecordingDuration, recordedTrack.Events.Count);

                return recordedTrack;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI recording stop cancelled: Operation: {OperationId}", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIRecordingStopFailed, ex,
                    "Failed to stop MIDI recording: Operation: {OperationId}", operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.RecordingError,
                    "Failed to stop MIDI recording",
                    ex,
                    operationId));

                throw new MIDIRecordingException(
                    "Failed to stop MIDI recording",
                    ex,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sends a MIDI event;
        /// </summary>
        /// <param name="midiEvent">MIDI event to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendMIDIEventAsync(
            MIDIEvent midiEvent,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            if (midiEvent == null)
                throw new ArgumentNullException(nameof(midiEvent));

            if (!_engineState.OutputDeviceAvailable)
                throw new MIDIDeviceException("MIDI output device is not available");

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIEventSend);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogDebug(LogEvents.MIDIEventSending,
                "Sending MIDI event: Type: {EventType}, Channel: {Channel}, Operation: {OperationId}",
                midiEvent.Type, midiEvent.Channel, operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                // Update channel state if applicable;
                UpdateChannelState(midiEvent);

                // Send via output device;
                var eventData = EncodeMIDIEvent(midiEvent);
                await _outputDevice.SendEventAsync(eventData, cancellationToken);

                OnEventSent(new MIDIEventSentEventArgs(
                    midiEvent,
                    operationId,
                    DateTime.UtcNow));

                _logger.LogTrace(LogEvents.MIDIEventSent,
                    "MIDI event sent: Type: {EventType}, Channel: {Channel}, Data: {Data}",
                    midiEvent.Type, midiEvent.Channel, BitConverter.ToString(eventData));
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI event send cancelled: Operation: {OperationId}", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIEventSendFailed, ex,
                    "Failed to send MIDI event: Type: {EventType}, Operation: {OperationId}",
                    midiEvent.Type, operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.EventError,
                    $"Failed to send MIDI event: {midiEvent.Type}",
                    ex,
                    operationId));

                throw new MIDIEventException(
                    $"Failed to send MIDI event: {midiEvent.Type}",
                    ex,
                    midiEvent,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sends a MIDI note on event;
        /// </summary>
        /// <param name="channel">MIDI channel (0-15)</param>
        /// <param name="note">Note number (0-127)</param>
        /// <param name="velocity">Velocity (0-127)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendNoteOnAsync(
            int channel,
            int note,
            int velocity,
            CancellationToken cancellationToken = default)
        {
            ValidateChannel(channel);
            ValidateNote(note);
            ValidateVelocity(velocity);

            var midiEvent = new MIDIEvent;
            {
                Type = MIDIEventType.NoteOn,
                Channel = channel,
                NoteNumber = note,
                Velocity = velocity,
                Timestamp = DateTime.UtcNow;
            };

            await SendMIDIEventAsync(midiEvent, cancellationToken);
        }

        /// <summary>
        /// Sends a MIDI note off event;
        /// </summary>
        /// <param name="channel">MIDI channel (0-15)</param>
        /// <param name="note">Note number (0-127)</param>
        /// <param name="velocity">Velocity (0-127)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendNoteOffAsync(
            int channel,
            int note,
            int velocity = 0,
            CancellationToken cancellationToken = default)
        {
            ValidateChannel(channel);
            ValidateNote(note);
            ValidateVelocity(velocity);

            var midiEvent = new MIDIEvent;
            {
                Type = MIDIEventType.NoteOff,
                Channel = channel,
                NoteNumber = note,
                Velocity = velocity,
                Timestamp = DateTime.UtcNow;
            };

            await SendMIDIEventAsync(midiEvent, cancellationToken);
        }

        /// <summary>
        /// Sends a MIDI control change event;
        /// </summary>
        /// <param name="channel">MIDI channel (0-15)</param>
        /// <param name="controller">Controller number (0-127)</param>
        /// <param name="value">Controller value (0-127)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendControlChangeAsync(
            int channel,
            int controller,
            int value,
            CancellationToken cancellationToken = default)
        {
            ValidateChannel(channel);
            ValidateController(controller);
            ValidateValue(value);

            var midiEvent = new MIDIEvent;
            {
                Type = MIDIEventType.ControlChange,
                Channel = channel,
                ControllerNumber = controller,
                ControllerValue = value,
                Timestamp = DateTime.UtcNow;
            };

            await SendMIDIEventAsync(midiEvent, cancellationToken);
        }

        /// <summary>
        /// Sends a MIDI program change event;
        /// </summary>
        /// <param name="channel">MIDI channel (0-15)</param>
        /// <param name="program">Program number (0-127)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendProgramChangeAsync(
            int channel,
            int program,
            CancellationToken cancellationToken = default)
        {
            ValidateChannel(channel);
            ValidateProgram(program);

            var midiEvent = new MIDIEvent;
            {
                Type = MIDIEventType.ProgramChange,
                Channel = channel,
                ProgramNumber = program,
                Timestamp = DateTime.UtcNow;
            };

            await SendMIDIEventAsync(midiEvent, cancellationToken);
        }

        /// <summary>
        /// Sends a MIDI pitch bend event;
        /// </summary>
        /// <param name="channel">MIDI channel (0-15)</param>
        /// <param name="value">Pitch bend value (-8192 to 8191)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendPitchBendAsync(
            int channel,
            int value,
            CancellationToken cancellationToken = default)
        {
            ValidateChannel(channel);
            ValidatePitchBend(value);

            var midiEvent = new MIDIEvent;
            {
                Type = MIDIEventType.PitchBend,
                Channel = channel,
                PitchBendValue = value,
                Timestamp = DateTime.UtcNow;
            };

            await SendMIDIEventAsync(midiEvent, cancellationToken);
        }

        /// <summary>
        /// Sends a MIDI system exclusive message;
        /// </summary>
        /// <param name="data">System exclusive data</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendSystemExclusiveAsync(
            byte[] data,
            CancellationToken cancellationToken = default)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("System exclusive data cannot be null or empty", nameof(data));

            var midiEvent = new MIDIEvent;
            {
                Type = MIDIEventType.SystemExclusive,
                Data = data,
                Timestamp = DateTime.UtcNow;
            };

            await SendMIDIEventAsync(midiEvent, cancellationToken);
        }

        /// <summary>
        /// Sends an all notes off message to all channels;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendAllNotesOffAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug(LogEvents.MIDIAllNotesOff,
                "Sending all notes off to all channels");

            for (int channel = 0; channel < 16; channel++)
            {
                try
                {
                    // Send control change 123 (All Notes Off)
                    await SendControlChangeAsync(channel, 123, 0, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(LogEvents.MIDIAllNotesOffFailed, ex,
                        "Failed to send all notes off to channel {Channel}", channel);
                }
            }
        }

        /// <summary>
        /// Sends a reset all controllers message to all channels;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the send operation</returns>
        public async Task SendResetAllControllersAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug(LogEvents.MIDIResetAllControllers,
                "Sending reset all controllers to all channels");

            for (int channel = 0; channel < 16; channel++)
            {
                try
                {
                    // Send control change 121 (Reset All Controllers)
                    await SendControlChangeAsync(channel, 121, 0, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(LogEvents.MIDIResetAllControllersFailed, ex,
                        "Failed to send reset all controllers to channel {Channel}", channel);
                }
            }
        }

        /// <summary>
        /// Gets MIDI engine statistics;
        /// </summary>
        /// <returns>MIDI engine statistics</returns>
        public MIDIEngineStatistics GetStatistics()
        {
            lock (_syncLock)
            {
                return new MIDIEngineStatistics;
                {
                    Status = _engineState.Status,
                    IsPlaying = _engineState.IsPlaying,
                    IsRecording = _engineState.IsRecording,
                    IsPaused = _engineState.IsPaused,
                    LoadedSequencesCount = _engineState.LoadedSequencesCount,
                    CurrentSequenceId = _engineState.CurrentSequenceId,
                    CurrentPosition = _sequencer?.Position ?? TimeSpan.Zero,
                    Tempo = _sequencer?.Tempo ?? DEFAULT_TEMPO,
                    TimeSignature = _sequencer?.TimeSignature ?? new TimeSignature(4, 4),
                    OutputDeviceAvailable = _engineState.OutputDeviceAvailable,
                    InputDeviceAvailable = _engineState.InputDeviceAvailable,
                    OutputDeviceName = _engineState.OutputDeviceName,
                    InputDeviceName = _engineState.InputDeviceName,
                    Channels = _channels.Values.ToList(),
                    ActiveNotes = GetActiveNotes(),
                    PerformanceMetrics = _engineState.PerformanceMetrics,
                    Uptime = DateTime.UtcNow - _engineState.StartTime,
                    Error = _engineState.Error;
                };
            }
        }

        /// <summary>
        /// Gets MIDI channel information;
        /// </summary>
        /// <param name="channel">Channel number (0-15)</param>
        /// <returns>MIDI channel information</returns>
        public MIDIChannel GetChannelInfo(int channel)
        {
            ValidateChannel(channel);

            if (_channels.TryGetValue(channel, out var channelInfo))
            {
                return channelInfo;
            }

            return new MIDIChannel;
            {
                ChannelNumber = channel,
                Program = 0,
                Volume = 100,
                Pan = 64,
                Expression = 127,
                PitchBend = 8192,
                Modulation = 0,
                ActiveNotes = new List<MIDINote>()
            };
        }

        /// <summary>
        /// Gets active MIDI notes across all channels;
        /// </summary>
        /// <returns>Collection of active notes</returns>
        public IEnumerable<MIDINote> GetActiveNotes()
        {
            var activeNotes = new List<MIDINote>();

            foreach (var channel in _channels.Values)
            {
                activeNotes.AddRange(channel.ActiveNotes);
            }

            return activeNotes;
        }

        /// <summary>
        /// Gets available MIDI input devices;
        /// </summary>
        /// <returns>Collection of MIDI input devices</returns>
        public async Task<IEnumerable<MIDIDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // This would typically query the system for available MIDI devices;
                    // For now, return a placeholder list;
                    return new List<MIDIDeviceInfo>
                    {
                        new MIDIDeviceInfo;
                        {
                            Id = "virtual_input_1",
                            Name = "Virtual MIDI Input 1",
                            Type = MIDIDeviceType.Input,
                            IsAvailable = true;
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(LogEvents.MIDIDeviceQueryFailed, ex,
                        "Failed to query MIDI input devices");
                    return Enumerable.Empty<MIDIDeviceInfo>();
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Gets available MIDI output devices;
        /// </summary>
        /// <returns>Collection of MIDI output devices</returns>
        public async Task<IEnumerable<MIDIDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // This would typically query the system for available MIDI devices;
                    // For now, return a placeholder list;
                    return new List<MIDIDeviceInfo>
                    {
                        new MIDIDeviceInfo;
                        {
                            Id = "virtual_output_1",
                            Name = "Virtual MIDI Output 1",
                            Type = MIDIDeviceType.Output,
                            IsAvailable = true;
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(LogEvents.MIDIDeviceQueryFailed, ex,
                        "Failed to query MIDI output devices");
                    return Enumerable.Empty<MIDIDeviceInfo>();
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Transposes a MIDI sequence;
        /// </summary>
        /// <param name="sequenceId">MIDI sequence identifier</param>
        /// <param name="semitones">Number of semitones to transpose</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the transpose operation</returns>
        public async Task TransposeSequenceAsync(
            string sequenceId,
            int semitones,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();
            ValidateSequenceId(sequenceId);

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDISequenceEdit);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogInformation(LogEvents.MIDITransposing,
                "Transposing MIDI sequence: SequenceId: {SequenceId}, Semitones: {Semitones}, Operation: {OperationId}",
                sequenceId, semitones, operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                // Get MIDI file;
                MIDIFile midiFile;
                lock (_syncLock)
                {
                    if (!_loadedSequences.TryGetValue(sequenceId, out midiFile))
                        throw new MIDIFileException($"MIDI sequence '{sequenceId}' is not loaded", sequenceId);
                }

                // Transpose all tracks;
                foreach (var track in midiFile.Tracks)
                {
                    await TransposeTrackAsync(track, semitones, cancellationToken);
                }

                // Update metadata;
                midiFile.Metadata["Transposed"] = true;
                midiFile.Metadata["TranspositionSemitones"] = semitones;
                midiFile.Metadata["TranspositionTime"] = DateTime.UtcNow;

                _logger.LogInformation(LogEvents.MIDITransposed,
                    "MIDI sequence transposed: SequenceId: {SequenceId}, Semitones: {Semitones}",
                    sequenceId, semitones);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI transpose operation cancelled: SequenceId: {SequenceId}, Operation: {OperationId}",
                    sequenceId, operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDITransposeFailed, ex,
                    "Failed to transpose MIDI sequence: SequenceId: {SequenceId}, Operation: {OperationId}",
                    sequenceId, operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.SequenceError,
                    $"Failed to transpose MIDI sequence '{sequenceId}'",
                    ex,
                    operationId));

                throw new MIDISequenceException(
                    $"Failed to transpose MIDI sequence '{sequenceId}'",
                    ex,
                    sequenceId,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Changes the tempo of a MIDI sequence;
        /// </summary>
        /// <param name="sequenceId">MIDI sequence identifier</param>
        /// <param name="tempo">New tempo in BPM</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the tempo change operation</returns>
        public async Task ChangeSequenceTempoAsync(
            string sequenceId,
            int tempo,
            CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();
            ValidateSequenceId(sequenceId);
            ValidateTempo(tempo);

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDISequenceEdit);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogInformation(LogEvents.MIDITempoChanging,
                "Changing MIDI sequence tempo: SequenceId: {SequenceId}, Tempo: {Tempo}, Operation: {OperationId}",
                sequenceId, tempo, operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                // Get MIDI file;
                MIDIFile midiFile;
                lock (_syncLock)
                {
                    if (!_loadedSequences.TryGetValue(sequenceId, out midiFile))
                        throw new MIDIFileException($"MIDI sequence '{sequenceId}' is not loaded", sequenceId);
                }

                // Update tempo events;
                await UpdateTempoEventsAsync(midiFile, tempo, cancellationToken);

                // Update metadata;
                midiFile.Metadata["TempoChanged"] = true;
                midiFile.Metadata["OriginalTempo"] = midiFile.OriginalTempo;
                midiFile.Metadata["NewTempo"] = tempo;
                midiFile.Metadata["TempoChangeTime"] = DateTime.UtcNow;

                // Update current tempo if this is the active sequence;
                if (_engineState.CurrentSequenceId == sequenceId && _sequencer != null)
                {
                    _sequencer.Tempo = tempo;
                }

                _logger.LogInformation(LogEvents.MIDITempoChanged,
                    "MIDI sequence tempo changed: SequenceId: {SequenceId}, Tempo: {Tempo}",
                    sequenceId, tempo);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI tempo change operation cancelled: SequenceId: {SequenceId}, Operation: {OperationId}",
                    sequenceId, operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDITempoChangeFailed, ex,
                    "Failed to change MIDI sequence tempo: SequenceId: {SequenceId}, Operation: {OperationId}",
                    sequenceId, operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.SequenceError,
                    $"Failed to change tempo for MIDI sequence '{sequenceId}'",
                    ex,
                    operationId));

                throw new MIDISequenceException(
                    $"Failed to change tempo for MIDI sequence '{sequenceId}'",
                    ex,
                    sequenceId,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Clears all MIDI sequences and resets the engine;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the clear operation</returns>
        public async Task ClearAllAsync(CancellationToken cancellationToken = default)
        {
            ValidateEngineInitialized();

            await _securityManager.ValidateOperationAsync(SecurityOperation.MIDIEngineClear);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogInformation(LogEvents.MIDIEngineClearing,
                "Clearing all MIDI sequences and resetting engine: Operation: {OperationId}", operationId);

            try
            {
                await _operationSemaphore.WaitAsync(cancellationToken);

                // Stop playback if active;
                if (_engineState.IsPlaying)
                {
                    try
                    {
                        await StopPlaybackAsync(new MIDIStopOptions;
                        {
                            Immediate = true;
                        }, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(LogEvents.MIDIPlaybackStopFailed, ex,
                            "Failed to stop playback during clear");
                    }
                }

                // Stop recording if active;
                if (_engineState.IsRecording)
                {
                    try
                    {
                        await StopRecordingAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(LogEvents.MIDIRecordingStopFailed, ex,
                            "Failed to stop recording during clear");
                    }
                }

                // Clear all sequences;
                lock (_syncLock)
                {
                    _loadedSequences.Clear();
                }

                // Reset sequencer;
                _sequencer?.Reset();

                // Reset channels;
                ResetChannels();

                // Reset engine state;
                lock (_syncLock)
                {
                    _engineState.LoadedSequencesCount = 0;
                    _engineState.CurrentSequenceId = null;
                    _engineState.IsPlaying = false;
                    _engineState.IsRecording = false;
                    _engineState.IsPaused = false;
                    _engineState.PlaybackStartTime = null;
                    _engineState.PlaybackEndTime = null;
                    _engineState.PlaybackDuration = null;
                    _engineState.RecordingStartTime = null;
                    _engineState.RecordingEndTime = null;
                    _engineState.RecordingDuration = null;
                    _engineState.RecordingTrack = null;
                    _engineState.RecordOptions = null;
                }

                // Send all notes off;
                await SendAllNotesOffAsync(cancellationToken);

                _logger.LogInformation(LogEvents.MIDIEngineCleared,
                    "All MIDI sequences cleared and engine reset: Operation: {OperationId}", operationId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.MIDIOperationCancelled,
                    "MIDI clear operation cancelled: Operation: {OperationId}", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.MIDIEngineClearFailed, ex,
                    "Failed to clear MIDI engine: Operation: {OperationId}", operationId);

                OnMIDIError(new MIDIErrorEventArgs(
                    MIDIErrorType.EngineError,
                    "Failed to clear MIDI engine",
                    ex,
                    operationId));

                throw new MIDIEngineException(
                    "Failed to clear MIDI engine",
                    ex,
                    operationId);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        #endregion;

        #region Private Methods;

        private void InitializeChannels()
        {
            for (int i = 0; i < DEFAULT_MIDI_CHANNELS; i++)
            {
                _channels[i] = new MIDIChannel;
                {
                    ChannelNumber = i,
                    Program = 0,
                    Volume = 100,
                    Pan = 64,
                    Expression = 127,
                    PitchBend = 8192,
                    Modulation = 0,
                    SustainPedal = false,
                    ActiveNotes = new List<MIDINote>(),
                    LastActivity = DateTime.UtcNow;
                };
            }

            _logger.LogDebug(LogEvents.MIDIChannelsInitialized,
                "MIDI channels initialized: {ChannelCount} channels", DEFAULT_MIDI_CHANNELS);
        }

        private void ResetChannels()
        {
            foreach (var channel in _channels.Values)
            {
                channel.Program = 0;
                channel.Volume = 100;
                channel.Pan = 64;
                channel.Expression = 127;
                channel.PitchBend = 8192;
                channel.Modulation = 0;
                channel.SustainPedal = false;
                channel.ActiveNotes.Clear();
                channel.LastActivity = DateTime.UtcNow;
            }

            _logger.LogDebug(LogEvents.MIDIChannelsReset,
                "MIDI channels reset");
        }

        private async Task<IMIDIOutputDevice> CreateMIDIOutputDeviceAsync(string deviceName, CancellationToken cancellationToken)
        {
            // This would create the appropriate output device based on the name;
            // For now, return a virtual device;
            return new VirtualMIDIOutputDevice(_logger, deviceName);
        }

        private async Task<IMIDIInputDevice> CreateMIDIInputDeviceAsync(string deviceName, CancellationToken cancellationToken)
        {
            // This would create the appropriate input device based on the name;
            // For now, return a virtual device;
            return new VirtualMIDIInputDevice(_logger, deviceName);
        }

        private void StartMIDIProcessingThread()
        {
            _processingCancellationTokenSource = new CancellationTokenSource();

            _midiProcessingThread = new Thread(async () =>
            {
                _logger.LogDebug(LogEvents.MIDIProcessingThreadStarting,
                    "MIDI processing thread starting");

                var processingInterval = TimeSpan.FromMilliseconds(_configuration.ProcessingIntervalMs ?? 1);

                while (!_processingCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();

                        // Process MIDI events;
                        await ProcessMIDIEventsAsync(_processingCancellationTokenSource.Token);

                        // Process recording if active;
                        if (_engineState.IsRecording)
                        {
                            await ProcessRecordingAsync(_processingCancellationTokenSource.Token);
                        }

                        // Update performance metrics;
                        UpdatePerformanceMetrics(stopwatch.Elapsed);

                        // Sleep to maintain processing rate;
                        if (stopwatch.Elapsed < processingInterval)
                        {
                            var sleepTime = processingInterval - stopwatch.Elapsed;
                            Thread.Sleep(sleepTime);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(LogEvents.MIDIProcessingError, ex,
                            "Error in MIDI processing thread");
                        Thread.Sleep(100);
                    }
                }

                _logger.LogDebug(LogEvents.MIDIProcessingThreadStopping,
                    "MIDI processing thread stopping");
            })
            {
                Name = "MIDIProcessingThread",
                Priority = ThreadPriority.AboveNormal;
            };

            _midiProcessingThread.Start();
        }

        private void StartMIDIClockThread()
        {
            var clockCancellationTokenSource = new CancellationTokenSource();

            _midiClockThread = new Thread(() =>
            {
                _logger.LogDebug(LogEvents.MIDIClockThreadStarting,
                    "MIDI clock thread starting");

                // MIDI clock runs at 24 pulses per quarter note;
                // Calculate interval based on current tempo;
                while (!clockCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (_engineState.IsPlaying && _configuration.SendMIDIClock)
                        {
                            // Calculate clock interval based on tempo;
                            var tempo = _sequencer?.Tempo ?? DEFAULT_TEMPO;
                            var clockInterval = CalculateClockInterval(tempo);

                            // Send MIDI clock ticks;
                            SendMIDIClockTick();

                            Thread.Sleep(clockInterval);
                        }
                        else;
                        {
                            Thread.Sleep(10);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(LogEvents.MIDIClockError, ex,
                            "Error in MIDI clock thread");
                        Thread.Sleep(100);
                    }
                }

                _logger.LogDebug(LogEvents.MIDIClockThreadStopping,
                    "MIDI clock thread stopping");
            })
            {
                Name = "MIDIClockThread",
                Priority = ThreadPriority.Highest;
            };

            _midiClockThread.Start();
        }

        private int CalculateClockInterval(int tempo)
        {
            // Calculate microseconds per quarter note;
            var microsecondsPerQuarter = 60000000 / tempo;

            // Calculate milliseconds per MIDI clock tick (24 ticks per quarter)
            var microsecondsPerTick = microsecondsPerQuarter / MIDI_CLOCK_TICKS_PER_QUARTER;
            var millisecondsPerTick = microsecondsPerTick / 1000;

            return Math.Max(1, millisecondsPerTick);
        }

        private async Task ProcessMIDIEventsAsync(CancellationToken cancellationToken)
        {
            // Process any pending MIDI events;
            // This could include event buffering, filtering, or transformation;

            if (_sequencer != null && _engineState.IsPlaying)
            {
                // Let sequencer process its events;
                _sequencer.Process();
            }
        }

        private async Task ProcessRecordingAsync(CancellationToken cancellationToken)
        {
            // Process any pending recorded events from input device;
            if (_inputDevice != null && _inputDevice.HasEvents)
            {
                var events = await _inputDevice.GetEventsAsync(cancellationToken);

                foreach (var eventData in events)
                {
                    try
                    {
                        var midiEvent = DecodeMIDIEvent(eventData);
                        midiEvent.Timestamp = DateTime.UtcNow;
                        midiEvent.RecordingTime = _playbackStopwatch.Elapsed;

                        // Add to recording track;
                        _engineState.RecordingTrack?.Events?.Add(midiEvent);

                        // Update channel state;
                        UpdateChannelState(midiEvent);

                        // Raise event received event;
                        OnEventReceived(new MIDIEventReceivedEventArgs(
                            midiEvent,
                            Guid.NewGuid().ToString(),
                            true)); // From recording;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(LogEvents.MIDIEventDecodeFailed, ex,
                            "Failed to decode recorded MIDI event");
                    }
                }
            }
        }

        private void UpdatePerformanceMetrics(TimeSpan processingTime)
        {
            var metrics = new MIDIPerformanceMetrics;
            {
                ProcessingTime = processingTime.TotalMilliseconds,
                ActiveNotesCount = GetActiveNotes().Count(),
                EventsProcessedPerSecond = CalculateEventsPerSecond(),
                CpuUsage = CalculateCpuUsage(),
                MemoryUsage = CalculateMemoryUsage(),
                Latency = CalculateLatency()
            };

            _engineState.PerformanceMetrics = metrics;
        }

        private double CalculateEventsPerSecond()
        {
            // Calculate events processed per second;
            // This would track event counts over time;
            return 0;
        }

        private float CalculateCpuUsage()
        {
            // Calculate CPU usage for MIDI processing;
            // This would be system-specific;
            return 0;
        }

        private long CalculateMemoryUsage()
        {
            // Calculate memory usage;
            long totalMemory = 0;

            lock (_syncLock)
            {
                foreach (var sequence in _loadedSequences.Values)
                {
                    totalMemory += sequence.FileSize;
                }
            }

            return totalMemory;
        }

        private float CalculateLatency()
        {
            // Calculate MIDI latency;
            // This would measure time from event generation to processing;
            return 0;
        }

        private async Task<MIDIFile> ParseMIDIFileAsync(byte[] midiData, MIDILoadOptions options, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Parse standard MIDI file format;
                // This is a simplified implementation;

                if (midiData.Length < 14) // Minimum MIDI header size;
                    throw new MIDIFileException("Invalid MIDI file: too small");

                var reader = new BinaryReader(new MemoryStream(midiData));

                // Read header;
                var headerChunk = ReadChunk(reader);
                if (headerChunk.Id != "MThd")
                    throw new MIDIFileException("Invalid MIDI file: missing MThd header");

                var format = reader.ReadInt16();
                var trackCount = reader.ReadInt16();
                var division = reader.ReadInt16();

                var midiFile = new MIDIFile;
                {
                    Format = (MIDIFormat)format,
                    TrackCount = trackCount,
                    Division = division,
                    OriginalTempo = DEFAULT_TEMPO,
                    Tracks = new List<MIDITrack>(),
                    Metadata = new Dictionary<string, object>()
                };

                // Read tracks;
                for (int i = 0; i < trackCount; i++)
                {
                    var trackChunk = ReadChunk(reader);
                    if (trackChunk.Id != "MTrk")
                        throw new MIDIFileException($"Invalid MIDI file: expected MTrk chunk at position {i}");

                    var track = ParseTrack(trackChunk.Data, i, division);
                    midiFile.Tracks.Add(track);
                }

                // Calculate duration;
                midiFile.Duration = CalculateMIDIDuration(midiFile);

                return midiFile;
            }, cancellationToken);
        }

        private (string Id, byte[] Data) ReadChunk(BinaryReader reader)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = ReadVariableLengthQuantity(reader);
            var data = reader.ReadBytes(length);

            return (id, data);
        }

        private int ReadVariableLengthQuantity(BinaryReader reader)
        {
            int value = 0;
            byte b;

            do;
            {
                b = reader.ReadByte();
                value = (value << 7) | (b & 0x7F);
            } while ((b & 0x80) != 0);

            return value;
        }

        private MIDITrack ParseTrack(byte[] trackData, int trackNumber, int division)
        {
            var track = new MIDITrack;
            {
                Id = $"track_{trackNumber}",
                Name = $"Track {trackNumber + 1}",
                Events = new List<MIDIEvent>(),
                Channel = -1 // Unknown until we parse events;
            };

            using (var stream = new MemoryStream(trackData))
            using (var reader = new BinaryReader(stream))
            {
                long absoluteTime = 0;

                while (stream.Position < stream.Length)
                {
                    var deltaTime = ReadVariableLengthQuantity(reader);
                    absoluteTime += deltaTime;

                    var eventByte = reader.ReadByte();

                    if (eventByte == 0xFF) // Meta event;
                    {
                        var metaType = reader.ReadByte();
                        var length = ReadVariableLengthQuantity(reader);
                        var data = reader.ReadBytes(length);

                        var midiEvent = ParseMetaEvent(metaType, data, absoluteTime);
                        track.Events.Add(midiEvent);
                    }
                    else if (eventByte == 0xF0 || eventByte == 0xF7) // System exclusive;
                    {
                        var length = ReadVariableLengthQuantity(reader);
                        var data = reader.ReadBytes(length);

                        var midiEvent = new MIDIEvent;
                        {
                            Type = MIDIEventType.SystemExclusive,
                            Data = data,
                            AbsoluteTime = absoluteTime;
                        };
                        track.Events.Add(midiEvent);
                    }
                    else // MIDI event;
                    {
                        stream.Position--; // Go back to read the status byte;
                        var midiEvent = ParseMIDIEvent(reader, absoluteTime);
                        track.Events.Add(midiEvent);

                        // Update track channel if not set;
                        if (track.Channel == -1 && midiEvent.Channel >= 0)
                        {
                            track.Channel = midiEvent.Channel;
                        }
                    }
                }
            }

            return track;
        }

        private MIDIEvent ParseMetaEvent(byte metaType, byte[] data, long absoluteTime)
        {
            var midiEvent = new MIDIEvent;
            {
                AbsoluteTime = absoluteTime;
            };

            switch (metaType)
            {
                case 0x51: // Tempo;
                    midiEvent.Type = MIDIEventType.Tempo;
                    midiEvent.Tempo = (data[0] << 16) | (data[1] << 8) | data[2];
                    break;

                case 0x58: // Time signature;
                    midiEvent.Type = MIDIEventType.TimeSignature;
                    midiEvent.TimeSignatureNumerator = data[0];
                    midiEvent.TimeSignatureDenominator = (int)Math.Pow(2, data[1]);
                    break;

                case 0x59: // Key signature;
                    midiEvent.Type = MIDIEventType.KeySignature;
                    midiEvent.KeySignature = data[0];
                    midiEvent.IsMinor = data[1] == 1;
                    break;

                case 0x01: // Text;
                case 0x02: // Copyright;
                case 0x03: // Track name;
                case 0x04: // Instrument name;
                case 0x05: // Lyric;
                case 0x06: // Marker;
                case 0x07: // Cue point;
                    midiEvent.Type = MIDIEventType.Text;
                    midiEvent.Text = Encoding.ASCII.GetString(data);
                    break;

                case 0x2F: // End of track;
                    midiEvent.Type = MIDIEventType.EndOfTrack;
                    break;

                default:
                    midiEvent.Type = MIDIEventType.Meta;
                    midiEvent.Data = data;
                    break;
            }

            return midiEvent;
        }

        private MIDIEvent ParseMIDIEvent(BinaryReader reader, long absoluteTime)
        {
            var statusByte = reader.ReadByte();
            var eventType = (statusByte >> 4) & 0x0F;
            var channel = statusByte & 0x0F;

            var midiEvent = new MIDIEvent;
            {
                Channel = channel,
                AbsoluteTime = absoluteTime;
            };

            switch (eventType)
            {
                case 0x8: // Note off;
                    midiEvent.Type = MIDIEventType.NoteOff;
                    midiEvent.NoteNumber = reader.ReadByte();
                    midiEvent.Velocity = reader.ReadByte();
                    break;

                case 0x9: // Note on;
                    midiEvent.Type = MIDIEventType.NoteOn;
                    midiEvent.NoteNumber = reader.ReadByte();
                    midiEvent.Velocity = reader.ReadByte();
                    break;

                case 0xA: // Polyphonic aftertouch;
                    midiEvent.Type = MIDIEventType.Aftertouch;
                    midiEvent.NoteNumber = reader.ReadByte();
                    midiEvent.AftertouchValue = reader.ReadByte();
                    break;

                case 0xB: // Control change;
                    midiEvent.Type = MIDIEventType.ControlChange;
                    midiEvent.ControllerNumber = reader.ReadByte();
                    midiEvent.ControllerValue = reader.ReadByte();
                    break;

                case 0xC: // Program change;
                    midiEvent.Type = MIDIEventType.ProgramChange;
                    midiEvent.ProgramNumber = reader.ReadByte();
                    break;

                case 0xD: // Channel aftertouch;
                    midiEvent.Type = MIDIEventType.ChannelAftertouch;
                    midiEvent.AftertouchValue = reader.ReadByte();
                    break;

                case 0xE: // Pitch bend;
                    midiEvent.Type = MIDIEventType.PitchBend;
                    var lsb = reader.ReadByte();
                    var msb = reader.ReadByte();
                    midiEvent.PitchBendValue = ((msb << 7) | lsb) - 8192;
                    break;

                default:
                    throw new MIDIFileException($"Unknown MIDI event type: 0x{eventType:X}");
            }

            return midiEvent;
        }

        private TimeSpan CalculateMIDIDuration(MIDIFile midiFile)
        {
            // Calculate total duration based on events and tempo;
            // This is a simplified calculation;
            long maxTime = 0;

            foreach (var track in midiFile.Tracks)
            {
                foreach (var evt in track.Events)
                {
                    if (evt.AbsoluteTime > maxTime)
                    {
                        maxTime = evt.AbsoluteTime;
                    }
                }
            }

            // Convert ticks to time based on tempo and division;
            var tempo = midiFile.OriginalTempo;
            var microsecondsPerQuarter = 60000000 / tempo;
            var ticksPerSecond = (1000000.0 / microsecondsPerQuarter) * midiFile.Division;
            var seconds = maxTime / ticksPerSecond;

            return TimeSpan.FromSeconds(seconds);
        }

        private async Task<byte[]> SerializeMIDIFileAsync(MIDIFile midiFile, MIDISaveOptions options, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    // Write header;
                    writer.Write(Encoding.ASCII.GetBytes("MThd"));
                    writer.Write(ReverseBytes(BitConverter.GetBytes((uint)6))); // Header length;
                    writer.Write(ReverseBytes(BitConverter.GetBytes((ushort)midiFile.Format)));
                    writer.Write(ReverseBytes(BitConverter.GetBytes((ushort)midiFile.Tracks.Count)));
                    writer.Write(ReverseBytes(BitConverter.GetBytes((ushort)midiFile.Division)));

                    // Write tracks;
                    foreach (var track in midiFile.Tracks)
                    {
                        WriteTrack(writer, track);
                    }

                    return stream.ToArray();
                }
            }, cancellationToken);
        }

        private void WriteTrack(BinaryWriter writer, MIDITrack track)
        {
            // Collect track data;
            var trackData = new MemoryStream();
            using (var trackWriter = new BinaryWriter(trackData))
            {
                long previousTime = 0;
                var sortedEvents = track.Events.OrderBy(e => e.AbsoluteTime).ToList();

                foreach (var evt in sortedEvents)
                {
                    // Write delta time;
                    var deltaTime = evt.AbsoluteTime - previousTime;
                    WriteVariableLengthQuantity(trackWriter, deltaTime);
                    previousTime = evt.AbsoluteTime;

                    // Write event;
                    WriteMIDIEvent(trackWriter, evt);
                }

                // Write end of track;
                WriteVariableLengthQuantity(trackWriter, 0);
                trackWriter.Write((byte)0xFF);
                trackWriter.Write((byte)0x2F);
                trackWriter.Write((byte)0x00);
            }

            // Write track header;
            writer.Write(Encoding.ASCII.GetBytes("MTrk"));
            writer.Write(ReverseBytes(BitConverter.GetBytes((uint)trackData.Length)));
            writer.Write(trackData.ToArray());
        }

        private void WriteMIDIEvent(BinaryWriter writer, MIDIEvent evt)
        {
            switch (evt.Type)
            {
                case MIDIEventType.NoteOn:
                    writer.Write((byte)(0x90 | (evt.Channel & 0x0F)));
                    writer.Write((byte)evt.NoteNumber);
                    writer.Write((byte)evt.Velocity);
                    break;

                case MIDIEventType.NoteOff:
                    writer.Write((byte)(0x80 | (evt.Channel & 0x0F)));
                    writer.Write((byte)evt.NoteNumber);
                    writer.Write((byte)evt.Velocity);
                    break;

                case MIDIEventType.ControlChange:
                    writer.Write((byte)(0xB0 | (evt.Channel & 0x0F)));
                    writer.Write((byte)evt.ControllerNumber);
                    writer.Write((byte)evt.ControllerValue);
                    break;

                case MIDIEventType.ProgramChange:
                    writer.Write((byte)(0xC0 | (evt.Channel & 0x0F)));
                    writer.Write((byte)evt.ProgramNumber);
                    break;

                case MIDIEventType.PitchBend:
                    writer.Write((byte)(0xE0 | (evt.Channel & 0x0F)));
                    var bendValue = evt.PitchBendValue + 8192;
                    writer.Write((byte)(bendValue & 0x7F));
                    writer.Write((byte)((bendValue >> 7) & 0x7F));
                    break;

                case MIDIEventType.Tempo:
                    writer.Write((byte)0xFF);
                    writer.Write((byte)0x51);
                    writer.Write((byte)0x03);
                    writer.Write((byte)((evt.Tempo >> 16) & 0xFF));
                    writer.Write((byte)((evt.Tempo >> 8) & 0xFF));
                    writer.Write((byte)(evt.Tempo & 0xFF));
                    break;

                case MIDIEventType.TimeSignature:
                    writer.Write((byte)0xFF);
                    writer.Write((byte)0x58);
                    writer.Write((byte)0x04);
                    writer.Write((byte)evt.TimeSignatureNumerator);
                    writer.Write((byte)(Math.Log(evt.TimeSignatureDenominator) / Math.Log(2)));
                    writer.Write((byte)24); // MIDI clocks per metronome click;
                    writer.Write((byte)8); // 32nd notes per 24 MIDI clocks;
                    break;

                case MIDIEventType.Text:
                    writer.Write((byte)0xFF);
                    writer.Write((byte)0x01);
                    WriteVariableLengthQuantity(writer, evt.Text.Length);
                    writer.Write(Encoding.ASCII.GetBytes(evt.Text));
                    break;

                case MIDIEventType.EndOfTrack:
                    writer.Write((byte)0xFF);
                    writer.Write((byte)0x2F);
                    writer.Write((byte)0x00);
                    break;

                case MIDIEventType.SystemExclusive:
                    writer.Write((byte)0xF0);
                    WriteVariableLengthQuantity(writer, evt.Data.Length);
                    writer.Write(evt.Data);
                    break;
            }
        }

        private void WriteVariableLengthQuantity(BinaryWriter writer, long value)
        {
            var buffer = new byte[4];
            int index = 3;

            buffer[index] = (byte)(value & 0x7F);
            value >>= 7;

            while (value > 0)
            {
                index--;
                buffer[index] = (byte)((value & 0x7F) | 0x80);
                value >>= 7;
            }

            for (int i = index; i < 4; i++)
            {
                writer.Write(buffer[i]);
            }
        }

        private byte[] ReverseBytes(byte[] bytes)
        {
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return bytes;
        }

        private async Task NormalizeNoteVelocitiesAsync(MIDIFile midiFile)
        {
            await Task.Run(() =>
            {
                // Find maximum velocity;
                int maxVelocity = 0;

                foreach (var track in midiFile.Tracks)
                {
                    foreach (var evt in track.Events)
                    {
                        if ((evt.Type == MIDIEventType.NoteOn || evt.Type == MIDIEventType.NoteOff) && evt.Velocity > maxVelocity)
                        {
                            maxVelocity = evt.Velocity;
                        }
                    }
                }

                if (maxVelocity == 0 || maxVelocity == 127)
                    return; // Nothing to normalize;

                // Scale all velocities;
                var scaleFactor = 127.0 / maxVelocity;

                foreach (var track in midiFile.Tracks)
                {
                    foreach (var evt in track.Events)
                    {
                        if (evt.Type == MIDIEventType.NoteOn || evt.Type == MIDIEventType.NoteOff)
                        {
                            evt.Velocity = (int)Math.Round(evt.Velocity * scaleFactor);
                            evt.Velocity = Math.Clamp(evt.Velocity, 0, 127);
                        }
                    }
                }
            });
        }

        private async Task QuantizeNotesAsync(MIDIFile midiFile, QuantizeValue quantizeValue)
        {
            await Task.Run(() =>
            {
                var quantizationGrid = GetQuantizationGrid(quantizeValue);

                foreach (var track in midiFile.Tracks)
                {
                    var noteOnEvents = track.Events;
                        .Where(e => e.Type == MIDIEventType.NoteOn)
                        .ToList();

                    foreach (var noteOn in noteOnEvents)
                    {
                        // Find corresponding note off;
                        var noteOff = track.Events;
                            .Where(e => e.Type == MIDIEventType.NoteOff &&
                                       e.NoteNumber == noteOn.NoteNumber &&
                                       e.Channel == noteOn.Channel &&
                                       e.AbsoluteTime > noteOn.AbsoluteTime)
                            .OrderBy(e => e.AbsoluteTime)
                            .FirstOrDefault();

                        if (noteOff != null)
                        {
                            // Quantize start time;
                            var quantizedStart = QuantizeTime(noteOn.AbsoluteTime, quantizationGrid);
                            noteOn.AbsoluteTime = quantizedStart;

                            // Quantize end time (maintain duration or quantize to grid)
                            var duration = noteOff.AbsoluteTime - noteOn.AbsoluteTime;
                            var quantizedDuration = QuantizeDuration(duration, quantizationGrid);
                            noteOff.AbsoluteTime = noteOn.AbsoluteTime + quantizedDuration;
                        }
                    }

                    // Re-sort events by absolute time;
                    track.Events = track.Events.OrderBy(e => e.AbsoluteTime).ToList();
                }
            });
        }

        private async Task QuantizeTrackAsync(MIDITrack track, QuantizeValue quantizeValue)
        {
            await Task.Run(() =>
            {
                var quantizationGrid = GetQuantizationGrid(quantizeValue);

                var noteOnEvents = track.Events;
                    .Where(e => e.Type == MIDIEventType.NoteOn)
                    .ToList();

                foreach (var noteOn in noteOnEvents)
                {
                    // Quantize based on recording time;
                    if (noteOn.RecordingTime.HasValue)
                    {
                        var ticks = (long)(noteOn.RecordingTime.Value.TotalSeconds * _configuration.DefaultPPQ * (_sequencer?.Tempo ?? DEFAULT_TEMPO) / 60);
                        var quantizedTicks = QuantizeTime(ticks, quantizationGrid);

                        // Convert back to recording time;
                        noteOn.RecordingTime = TimeSpan.FromSeconds(quantizedTicks * 60.0 / (_configuration.DefaultPPQ * (_sequencer?.Tempo ?? DEFAULT_TEMPO)));
                    }
                }
            });
        }

        private long GetQuantizationGrid(QuantizeValue quantizeValue)
        {
            // Return ticks per grid division based on PPQ;
            var ppq = _configuration.DefaultPPQ ?? DEFAULT_PPQ;

            return quantizeValue switch;
            {
                QuantizeValue.Whole => ppq * 4,
                QuantizeValue.Half => ppq * 2,
                QuantizeValue.Quarter => ppq,
                QuantizeValue.Eighth => ppq / 2,
                QuantizeValue.Sixteenth => ppq / 4,
                QuantizeValue.ThirtySecond => ppq / 8,
                QuantizeValue.SixtyFourth => ppq / 16,
                _ => ppq / 4 // Default to sixteenth notes;
            };
        }

        private long QuantizeTime(long time, long grid)
        {
            if (grid == 0) return time;

            var remainder = time % grid;
            if (remainder < grid / 2)
            {
                return time - remainder;
            }
            else;
            {
                return time + (grid - remainder);
            }
        }

        private long QuantizeDuration(long duration, long grid)
        {
            // Quantize duration to nearest grid multiple;
            if (grid == 0) return duration;

            var quantized = QuantizeTime(duration, grid);
            return Math.Max(grid, quantized); // Ensure minimum duration;
        }

        private async Task RemoveOverlappingNotesAsync(MIDIFile midiFile)
        {
            await Task.Run(() =>
            {
                foreach (var track in midiFile.Tracks)
                {
                    RemoveOverlappingNotesInTrack(track);
                }
            });
        }

        private void RemoveOverlappingNotesInTrack(MIDITrack track)
        {
            var activeNotes = new Dictionary<int, MIDIEvent>(); // key: (channel << 8) | note;

            foreach (var evt in track.Events.ToList())
            {
                if (evt.Type == MIDIEventType.NoteOn)
                {
                    var key = (evt.Channel << 8) | evt.NoteNumber;

                    if (activeNotes.ContainsKey(key))
                    {
                        // Found overlapping note - insert note off before new note on;
                        var previousNoteOn = activeNotes[key];
                        var noteOffIndex = track.Events.IndexOf(evt);

                        var noteOff = new MIDIEvent;
                        {
                            Type = MIDIEventType.NoteOff,
                            Channel = previousNoteOn.Channel,
                            NoteNumber = previousNoteOn.NoteNumber,
                            Velocity = 0,
                            AbsoluteTime = evt.AbsoluteTime - 1 // Just before new note;
                        };

                        track.Events.Insert(noteOffIndex, noteOff);
                    }

                    activeNotes[key] = evt;
                }
                else if (evt.Type == MIDIEventType.NoteOff)
                {
                    var key = (evt.Channel << 8) | evt.NoteNumber;
                    activeNotes.Remove(key);
                }
            }

            // Sort events again;
            track.Events = track.Events.OrderBy(e => e.AbsoluteTime).ToList();
        }

        private async Task RemoveNoiseFromTrackAsync(MIDITrack track)
        {
            await Task.Run(() =>
            {
                // Remove very short notes (potential noise)
                var minNoteDuration = TimeSpan.FromMilliseconds(_configuration.MinNoteDurationMs ?? 50);

                var noteOnEvents = track.Events;
                    .Where(e => e.Type == MIDIEventType.NoteOn)
                    .ToList();

                foreach (var noteOn in noteOnEvents)
                {
                    var noteOff = track.Events;
                        .Where(e => e.Type == MIDIEventType.NoteOff &&
                                   e.NoteNumber == noteOn.NoteNumber &&
                                   e.Channel == noteOn.Channel)
                        .OrderBy(e => e.AbsoluteTime)
                        .FirstOrDefault();

                    if (noteOff != null)
                    {
                        var duration = noteOff.AbsoluteTime - noteOn.AbsoluteTime;
                        var durationTime = TimeSpan.FromSeconds(duration * 60.0 / (_configuration.DefaultPPQ * (_sequencer?.Tempo ?? DEFAULT_TEMPO)));

                        if (durationTime < minNoteDuration)
                        {
                            // Remove this note (both on and off events)
                            track.Events.Remove(noteOn);
                            track.Events.Remove(noteOff);
                        }
                    }
                }
            });
        }

        private async Task CompressTracksAsync(MIDIFile midiFile)
        {
            await Task.Run(() =>
            {
                // Merge tracks with same channel;
                var tracksByChannel = midiFile.Tracks;
                    .GroupBy(t => t.Channel)
                    .ToList();

                var compressedTracks = new List<MIDITrack>();

                foreach (var group in tracksByChannel)
                {
                    if (group.Count() > 1)
                    {
                        // Merge tracks;
                        var mergedTrack = new MIDITrack;
                        {
                            Id = $"merged_channel_{group.Key}",
                            Name = $"Merged Channel {group.Key + 1}",
                            Channel = group.Key,
                            Events = new List<MIDIEvent>()
                        };

                        foreach (var track in group)
                        {
                            mergedTrack.Events.AddRange(track.Events);
                        }

                        // Sort events by absolute time;
                        mergedTrack.Events = mergedTrack.Events;
                            .OrderBy(e => e.AbsoluteTime)
                            .ToList();

                        compressedTracks.Add(mergedTrack);
                    }
                    else;
                    {
                        compressedTracks.Add(group.First());
                    }
                }

                midiFile.Tracks = compressedTracks;
                midiFile.TrackCount = compressedTracks.Count;
            });
        }

        private async Task OptimizeEventsAsync(MIDIFile midiFile)
        {
            await Task.Run(() =>
            {
                // Remove redundant events and optimize delta times;
                foreach (var track in midiFile.Tracks)
                {
                    OptimizeTrackEvents(track);
                }
            });
        }

        private void OptimizeTrackEvents(MIDITrack track)
        {
            // Remove consecutive identical control changes;
            var eventsToRemove = new List<MIDIEvent>();
            MIDIEvent previousControlChange = null;

            foreach (var evt in track.Events)
            {
                if (evt.Type == MIDIEventType.ControlChange)
                {
                    if (previousControlChange != null &&
                        previousControlChange.Channel == evt.Channel &&
                        previousControlChange.ControllerNumber == evt.ControllerNumber &&
                        previousControlChange.ControllerValue == evt.ControllerValue)
                    {
                        eventsToRemove.Add(evt);
                    }
                    else;
                    {
                        previousControlChange = evt;
                    }
                }
                else;
                {
                    previousControlChange = null;
                }
            }

            foreach (var evt in eventsToRemove)
            {
                track.Events.Remove(evt);
            }

            // Optimize delta times (run-length encoding for delta times of 0)
            // This would be implemented in the serialization phase;
        }

        private async Task TransposeTrackAsync(MIDITrack track, int semitones, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                foreach (var evt in track.Events)
                {
                    if (evt.Type == MIDIEventType.NoteOn || evt.Type == MIDIEventType.NoteOff)
                    {
                        evt.NoteNumber += semitones;
                        evt.NoteNumber = Math.Clamp(evt.NoteNumber, 0, 127);
                    }
                }
            }, cancellationToken);
        }

        private async Task UpdateTempoEventsAsync(MIDIFile midiFile, int newTempo, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Update all tempo events in all tracks;
                foreach (var track in midiFile.Tracks)
                {
                    foreach (var evt in track.Events)
                    {
                        if (evt.Type == MIDIEventType.Tempo)
                        {
                            evt.Tempo = 60000000 / newTempo; // microseconds per quarter;
                        }
                    }
                }

                midiFile.OriginalTempo = newTempo;
            }, cancellationToken);
        }

        private void UpdateChannelState(MIDIEvent midiEvent)
        {
            if (midiEvent.Channel < 0 || midiEvent.Channel >= DEFAULT_MIDI_CHANNELS)
                return;

            if (!_channels.TryGetValue(midiEvent.Channel, out var channel))
                return;

            channel.LastActivity = DateTime.UtcNow;

            switch (midiEvent.Type)
            {
                case MIDIEventType.NoteOn:
                    if (midiEvent.Velocity > 0)
                    {
                        // Add to active notes;
                        channel.ActiveNotes.Add(new MIDINote;
                        {
                            Channel = midiEvent.Channel,
                            NoteNumber = midiEvent.NoteNumber,
                            Velocity = midiEvent.Velocity,
                            StartTime = DateTime.UtcNow;
                        });
                    }
                    else;
                    {
                        // Note on with velocity 0 is treated as note off;
                        RemoveNoteFromChannel(channel, midiEvent.NoteNumber);
                    }
                    break;

                case MIDIEventType.NoteOff:
                    RemoveNoteFromChannel(channel, midiEvent.NoteNumber);
                    break;

                case MIDIEventType.ControlChange:
                    switch (midiEvent.ControllerNumber)
                    {
                        case 7: // Volume;
                            channel.Volume = midiEvent.ControllerValue;
                            break;

                        case 10: // Pan;
                            channel.Pan = midiEvent.ControllerValue;
                            break;

                        case 11: // Expression;
                            channel.Expression = midiEvent.ControllerValue;
                            break;

                        case 1: // Modulation;
                            channel.Modulation = midiEvent.ControllerValue;
                            break;

                        case 64: // Sustain pedal;
                            channel.SustainPedal = midiEvent.ControllerValue >= 64;
                            break;

                        case 121: // Reset all controllers;
                            channel.Volume = 100;
                            channel.Pan = 64;
                            channel.Expression = 127;
                            channel.Modulation = 0;
                            channel.PitchBend = 8192;
                            channel.SustainPedal = false;
                            break;

                        case 123: // All notes off;
                            channel.ActiveNotes.Clear();
                            break;
                    }
                    break;

                case MIDIEventType.ProgramChange:
                    channel.Program = midiEvent.ProgramNumber;
                    break;

                case MIDIEventType.PitchBend:
                    channel.PitchBend = midiEvent.PitchBendValue + 8192;
                    break;
            }
        }

        private void RemoveNoteFromChannel(MIDIChannel channel, int noteNumber)
        {
            var noteToRemove = channel.ActiveNotes;
                .FirstOrDefault(n => n.NoteNumber == noteNumber);

            if (noteToRemove != null)
            {
                noteToRemove.EndTime = DateTime.UtcNow;
                channel.ActiveNotes.Remove(noteToRemove);
            }
        }

        private byte[] EncodeMIDIEvent(MIDIEvent midiEvent)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                switch (midiEvent.Type)
                {
                    case MIDIEventType.NoteOn:
                        writer.Write((byte)(0x90 | (midiEvent.Channel & 0x0F)));
                        writer.Write((byte)midiEvent.NoteNumber);
                        writer.Write((byte)midiEvent.Velocity);
                        break;

                    case MIDIEventType.NoteOff:
                        writer.Write((byte)(0x80 | (midiEvent.Channel & 0x0F)));
                        writer.Write((byte)midiEvent.NoteNumber);
                        writer.Write((byte)midiEvent.Velocity);
                        break;

                    case MIDIEventType.ControlChange:
                        writer.Write((byte)(0xB0 | (midiEvent.Channel & 0x0F)));
                        writer.Write((byte)midiEvent.ControllerNumber);
                        writer.Write((byte)midiEvent.ControllerValue);
                        break;

                    case MIDIEventType.ProgramChange:
                        writer.Write((byte)(0xC0 | (midiEvent.Channel & 0x0F)));
                        writer.Write((byte)midiEvent.ProgramNumber);
                        break;

                    case MIDIEventType.PitchBend:
                        writer.Write((byte)(0xE0 | (midiEvent.Channel & 0x0F)));
                        var bendValue = midiEvent.PitchBendValue + 8192;
                        writer.Write((byte)(bendValue & 0x7F));
                        writer.Write((byte)((bendValue >> 7) & 0x7F));
                        break;

                    case MIDIEventType.SystemExclusive:
                        writer.Write((byte)0xF0);
                        writer.Write(midiEvent.Data);
                        writer.Write((byte)0xF7);
                        break;

                    case MIDIEventType.MIDIClock:
                        writer.Write((byte)0xF8);
                        break;

                    case MIDIEventType.MIDIStart:
                        writer.Write((byte)0xFA);
                        break;

                    case MIDIEventType.MIDIStop:
                        writer.Write((byte)0xFC);
                        break;

                    case MIDIEventType.MIDIContinue:
                        writer.Write((byte)0xFB);
                        break;
                }

                return stream.ToArray();
            }
        }

        private MIDIEvent DecodeMIDIEvent(byte[] eventData)
        {
            if (eventData == null || eventData.Length == 0)
                throw new ArgumentException("Event data cannot be null or empty", nameof(eventData));

            var midiEvent = new MIDIEvent();
            var statusByte = eventData[0];

            if (statusByte >= 0x80 && statusByte < 0xF0)
            {
                // Channel message;
                var eventType = (statusByte >> 4) & 0x0F;
                var channel = statusByte & 0x0F;

                midiEvent.Channel = channel;

                switch (eventType)
                {
                    case 0x8:
                        midiEvent.Type = MIDIEventType.NoteOff;
                        if (eventData.Length >= 3)
                        {
                            midiEvent.NoteNumber = eventData[1];
                            midiEvent.Velocity = eventData[2];
                        }
                        break;

                    case 0x9:
                        midiEvent.Type = MIDIEventType.NoteOn;
                        if (eventData.Length >= 3)
                        {
                            midiEvent.NoteNumber = eventData[1];
                            midiEvent.Velocity = eventData[2];
                        }
                        break;

                    case 0xB:
                        midiEvent.Type = MIDIEventType.ControlChange;
                        if (eventData.Length >= 3)
                        {
                            midiEvent.ControllerNumber = eventData[1];
                            midiEvent.ControllerValue = eventData[2];
                        }
                        break;

                    case 0xC:
                        midiEvent.Type = MIDIEventType.ProgramChange;
                        if (eventData.Length >= 2)
                        {
                            midiEvent.ProgramNumber = eventData[1];
                        }
                        break;

                    case 0xE:
                        midiEvent.Type = MIDIEventType.PitchBend;
                        if (eventData.Length >= 3)
                        {
                            var lsb = eventData[1];
                            var msb = eventData[2];
                            midiEvent.PitchBendValue = ((msb << 7) | lsb) - 8192;
                        }
                        break;
                }
            }
            else if (statusByte >= 0xF0)
            {
                // System message;
                switch (statusByte)
                {
                    case 0xF0:
                        midiEvent.Type = MIDIEventType.SystemExclusive;
                        midiEvent.Data = eventData.Skip(1).ToArray();
                        break;

                    case 0xF8:
                        midiEvent.Type = MIDIEventType.MIDIClock;
                        break;

                    case 0xFA:
                        midiEvent.Type = MIDIEventType.MIDIStart;
                        break;

                    case 0xFC:
                        midiEvent.Type = MIDIEventType.MIDIStop;
                        break;

                    case 0xFB:
                        midiEvent.Type = MIDIEventType.MIDIContinue;
                        break;
                }
            }

            return midiEvent;
        }

        private void SendMIDIClockTick()
        {
            if (_outputDevice != null && _configuration.SendMIDIClock)
            {
                try
                {
                    var clockEvent = new MIDIEvent;
                    {
                        Type = MIDIEventType.MIDIClock,
                        Timestamp = DateTime.UtcNow;
                    };

                    var eventData = EncodeMIDIEvent(clockEvent);
                    _outputDevice.SendEventAsync(eventData, CancellationToken.None).Wait(10);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(LogEvents.MIDIClockSendFailed, ex,
                        "Failed to send MIDI clock tick");
                }
            }
        }

        private void ValidateEngineInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("MIDI engine is not initialized");
        }

        private void ValidateSequenceId(string sequenceId)
        {
            if (string.IsNullOrWhiteSpace(sequenceId))
                throw new ArgumentException("Sequence ID cannot be null or empty", nameof(sequenceId));
        }

        private void ValidateFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            try
            {
                var fullPath = Path.GetFullPath(filePath);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid file path", nameof(filePath), ex);
            }
        }

        private void ValidateChannel(int channel)
        {
            if (channel < 0 || channel >= DEFAULT_MIDI_CHANNELS)
                throw new ArgumentException($"Channel must be between 0 and {DEFAULT_MIDI_CHANNELS - 1}", nameof(channel));
        }

        private void ValidateNote(int note)
        {
            if (note < 0 || note > 127)
                throw new ArgumentException("Note must be between 0 and 127", nameof(note));
        }

        private void ValidateVelocity(int velocity)
        {
            if (velocity < 0 || velocity > 127)
                throw new ArgumentException("Velocity must be between 0 and 127", nameof(velocity));
        }

        private void ValidateController(int controller)
        {
            if (controller < 0 || controller > 127)
                throw new ArgumentException("Controller must be between 0 and 127", nameof(controller));
        }

        private void ValidateValue(int value)
        {
            if (value < 0 || value > 127)
                throw new ArgumentException("Value must be between 0 and 127", nameof(value));
        }

        private void ValidateProgram(int program)
        {
            if (program < 0 || program > 127)
                throw new ArgumentException("Program must be between 0 and 127", nameof(program));
        }

        private void ValidatePitchBend(int pitchBend)
        {
            if (pitchBend < -8192 || pitchBend > 8191)
                throw new ArgumentException("Pitch bend must be between -8192 and 8191", nameof(pitchBend));
        }

        private void ValidateTempo(int tempo)
        {
            if (tempo <= 0 || tempo > 500)
                throw new ArgumentException("Tempo must be between 1 and 500 BPM", nameof(tempo));
        }

        #endregion;

        #region Event Handlers;

        private void OnInputDeviceEventReceived(object sender, MIDIEventReceivedEventArgs e)
        {
            // Forward the event;
            OnEventReceived(e);

            // If recording, add to recording track;
            if (_engineState.IsRecording && _engineState.RecordingTrack != null)
            {
                var recordedEvent = e.Event.Clone();
                recordedEvent.RecordingTime = _playbackStopwatch.Elapsed;
                _engineState.RecordingTrack.Events.Add(recordedEvent);
            }
        }

        private void OnSequencerPlaybackStarted(object sender, EventArgs e)
        {
            var operationId = Guid.NewGuid().ToString();

            OnPlaybackStarted(new MIDIPlaybackStartedEventArgs(
                _engineState.CurrentSequenceId,
                operationId,
                _sequencer.Position,
                _sequencer.Tempo));
        }

        private void OnSequencerPlaybackStopped(object sender, EventArgs e)
        {
            var operationId = Guid.NewGuid().ToString();

            OnPlaybackStopped(new MIDIPlaybackStoppedEventArgs(
                _engineState.CurrentSequenceId,
                operationId,
                _sequencer.Position,
                MIDIStopReason.Completed));
        }

        private void OnSequencerPositionChanged(object sender, EventArgs e)
        {
            var operationId = Guid.NewGuid().ToString();

            OnPlaybackPositionChanged(new MIDIPlaybackPositionChangedEventArgs(
                _engineState.CurrentSequenceId,
                operationId,
                _sequencer.Position,
                CalculatePlaybackProgress()));
        }

        private void OnSequencerTempoChanged(object sender, EventArgs e)
        {
            var operationId = Guid.NewGuid().ToString();

            OnTempoChanged(new TempoChangedEventArgs(
                _engineState.CurrentSequenceId,
                operationId,
                _sequencer.Tempo,
                _sequencer.Position));
        }

        private void OnSequencerTimeSignatureChanged(object sender, EventArgs e)
        {
            var operationId = Guid.NewGuid().ToString();

            OnTimeSignatureChanged(new TimeSignatureChangedEventArgs(
                _engineState.CurrentSequenceId,
                operationId,
                _sequencer.TimeSignature,
                _sequencer.Position));
        }

        private void OnSequencerEventPlayed(object sender, MIDIEventPlayedEventArgs e)
        {
            // Send the event to output device;
            if (_outputDevice != null && _engineState.OutputDeviceAvailable)
            {
                try
                {
                    var eventData = EncodeMIDIEvent(e.Event);
                    _outputDevice.SendEventAsync(eventData, CancellationToken.None).Wait(10);

                    // Update channel state;
                    UpdateChannelState(e.Event);

                    // Raise event sent event;
                    OnEventSent(new MIDIEventSentEventArgs(
                        e.Event,
                        Guid.NewGuid().ToString(),
                        DateTime.UtcNow));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(LogEvents.MIDIEventSendFailed, ex,
                        "Failed to send sequencer event: Type: {EventType}", e.Event.Type);
                }
            }
        }

        private double CalculatePlaybackProgress()
        {
            if (_sequencer == null || !_engineState.IsPlaying)
                return 0;

            // Get current sequence;
            MIDIFile currentSequence = null;
            lock (_syncLock)
            {
                if (!string.IsNullOrEmpty(_engineState.CurrentSequenceId))
                {
                    _loadedSequences.TryGetValue(_engineState.CurrentSequenceId, out currentSequence);
                }
            }

            if (currentSequence == null || currentSequence.Duration == TimeSpan.Zero)
                return 0;

            var progress = _sequencer.Position.TotalMilliseconds / currentSequence.Duration.TotalMilliseconds;
            return Math.Clamp(progress, 0, 1);
        }

        #endregion;

        #region Event Methods;

        protected virtual void OnFileLoaded(MIDIFileLoadedEventArgs e)
        {
            FileLoaded?.Invoke(this, e);
        }

        protected virtual void OnPlaybackStarted(MIDIPlaybackStartedEventArgs e)
        {
            PlaybackStarted?.Invoke(this, e);
        }

        protected virtual void OnPlaybackStopped(MIDIPlaybackStoppedEventArgs e)
        {
            PlaybackStopped?.Invoke(this, e);
        }

        protected virtual void OnPlaybackPositionChanged(MIDIPlaybackPositionChangedEventArgs e)
        {
            PlaybackPositionChanged?.Invoke(this, e);
        }

        protected virtual void OnEventReceived(MIDIEventReceivedEventArgs e)
        {
            EventReceived?.Invoke(this, e);
        }

        protected virtual void OnEventSent(MIDIEventSentEventArgs e)
        {
            EventSent?.Invoke(this, e);
        }

        protected virtual void OnRecordingStarted(MIDIRecordingStartedEventArgs e)
        {
            RecordingStarted?.Invoke(this, e);
        }

        protected virtual void OnRecordingStopped(MIDIRecordingStoppedEventArgs e)
        {
            RecordingStopped?.Invoke(this, e);
        }

        protected virtual void OnMIDIError(MIDIErrorEventArgs e)
        {
            MIDIError?.Invoke(this, e);
        }

        protected virtual void OnEngineStateChanged(MIDIEngineStateChangedEventArgs e)
        {
            EngineStateChanged?.Invoke(this, e);
        }

        protected virtual void OnTempoChanged(TempoChangedEventArgs e)
        {
            TempoChanged?.Invoke(this, e);
        }

        protected virtual void OnTimeSignatureChanged(TimeSignatureChangedEventArgs e)
        {
            TimeSignatureChanged?.Invoke(this, e);
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
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Stop processing threads;
                    _processingCancellationTokenSource?.Cancel();
                    _midiProcessingThread?.Join(TimeSpan.FromSeconds(5));

                    // Clear all sequences;
                    try
                    {
                        ClearAllAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(LogEvents.MIDIEngineDisposeError, ex,
                            "Error during MIDI engine disposal");
                    }

                    // Dispose devices;
                    _outputDevice?.Dispose();
                    _inputDevice?.Dispose();

                    // Dispose sequencer;
                    _sequencer?.Dispose();

                    // Dispose semaphore;
                    _operationSemaphore?.Dispose();
                }

                _isDisposed = true;
            }
        }

        ~MIDIEngine()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes;

        private class MIDIEngineState;
        {
            public MIDIEngineStatus Status { get; set; } = MIDIEngineStatus.Uninitialized;
            public bool IsPlaying { get; set; }
            public bool IsRecording { get; set; }
            public bool IsPaused { get; set; }
            public int LoadedSequencesCount { get; set; }
            public string CurrentSequenceId { get; set; }
            public DateTime? PlaybackStartTime { get; set; }
            public DateTime? PlaybackEndTime { get; set; }
            public TimeSpan? PlaybackDuration { get; set; }
            public DateTime? RecordingStartTime { get; set; }
            public DateTime? RecordingEndTime { get; set; }
            public TimeSpan? RecordingDuration { get; set; }
            public MIDITrack RecordingTrack { get; set; }
            public MIDIRecordOptions RecordOptions { get; set; }
            public bool OutputDeviceAvailable { get; set; }
            public bool InputDeviceAvailable { get; set; }
            public string OutputDeviceName { get; set; }
            public string InputDeviceName { get; set; }
            public MIDIPerformanceMetrics PerformanceMetrics { get; set; }
            public DateTime StartTime { get; set; } = DateTime.UtcNow;
            public string Error { get; set; }
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Interface for MIDI engine functionality;
    /// </summary>
    public interface IMIDIEngine : IDisposable
    {
        /// <summary>
        /// Initializes the MIDI engine with specified devices;
        /// </summary>
        Task InitializeAsync(
            string outputDeviceName = null,
            string inputDeviceName = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads a MIDI file;
        /// </summary>
        Task<MIDIFile> LoadMIDIFileAsync(
            string filePath,
            string sequenceId = null,
            MIDILoadOptions loadOptions = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves a MIDI file;
        /// </summary>
        Task SaveMIDIFileAsync(
            MIDIFile midiFile,
            string filePath,
            MIDISaveOptions saveOptions = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Plays a MIDI sequence;
        /// </summary>
        Task PlaySequenceAsync(
            string sequenceId,
            MIDIPlayOptions playOptions = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops MIDI playback;
        /// </summary>
        Task StopPlaybackAsync(
            MIDIStopOptions stopOptions = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Pauses MIDI playback;
        /// </summary>
        Task PausePlaybackAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Resumes MIDI playback;
        /// </summary>
        Task ResumePlaybackAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts MIDI recording;
        /// </summary>
        Task StartRecordingAsync(
            MIDIRecordOptions recordOptions = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops MIDI recording;
        /// </summary>
        Task<MIDITrack> StopRecordingAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a MIDI event;
        /// </summary>
        Task SendMIDIEventAsync(
            MIDIEvent midiEvent,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a MIDI note on event;
        /// </summary>
        Task SendNoteOnAsync(
            int channel,
            int note,
            int velocity,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a MIDI note off event;
        /// </summary>
        Task SendNoteOffAsync(
            int channel,
            int note,
            int velocity = 0,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a MIDI control change event;
        /// </summary>
        Task SendControlChangeAsync(
            int channel,
            int controller,
            int value,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a MIDI program change event;
        /// </summary>
        Task SendProgramChangeAsync(
            int channel,
            int program,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a MIDI pitch bend event;
        /// </summary>
        Task SendPitchBendAsync(
            int channel,
            int value,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a MIDI system exclusive message;
        /// </summary>
        Task SendSystemExclusiveAsync(
            byte[] data,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends an all notes off message to all channels;
        /// </summary>
        Task SendAllNotesOffAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a reset all controllers message to all channels;
        /// </summary>
        Task SendResetAllControllersAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets MIDI engine statistics;
        /// </summary>
        MIDIEngineStatistics GetStatistics();

        /// <summary>
        /// Gets MIDI channel information;
        /// </summary>
        MIDIChannel GetChannelInfo(int channel);

        /// <summary>
        /// Gets active MIDI notes across all channels;
        /// </summary>
        IEnumerable<MIDINote> GetActiveNotes();

        /// <summary>
        /// Gets available MIDI input devices;
        /// </summary>
        Task<IEnumerable<MIDIDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets available MIDI output devices;
        /// </summary>
        Task<IEnumerable<MIDIDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Transposes a MIDI sequence;
        /// </summary>
        Task TransposeSequenceAsync(
            string sequenceId,
            int semitones,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Changes the tempo of a MIDI sequence;
        /// </summary>
        Task ChangeSequenceTempoAsync(
            string sequenceId,
            int tempo,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears all MIDI sequences and resets the engine;
        /// </summary>
        Task ClearAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Event raised when MIDI file is loaded;
        /// </summary>
        event EventHandler<MIDIFileLoadedEventArgs> FileLoaded;

        /// <summary>
        /// Event raised when MIDI playback starts;
        /// </summary>
        event EventHandler<MIDIPlaybackStartedEventArgs> PlaybackStarted;

        /// <summary>
        /// Event raised when MIDI playback stops;
        /// </summary>
        event EventHandler<MIDIPlaybackStoppedEventArgs> PlaybackStopped;

        /// <summary>
        /// Event raised when MIDI playback position changes;
        /// </summary>
        event EventHandler<MIDIPlaybackPositionChangedEventArgs> PlaybackPositionChanged;

        /// <summary>
        /// Event raised when MIDI event is received;
        /// </summary>
        event EventHandler<MIDIEventReceivedEventArgs> EventReceived;

        /// <summary>
        /// Event raised when MIDI event is sent;
        /// </summary>
        event EventHandler<MIDIEventSentEventArgs> EventSent;

        /// <summary>
        /// Event raised when MIDI recording starts;
        /// </summary>
        event EventHandler<MIDIRecordingStartedEventArgs> RecordingStarted;

        /// <summary>
        /// Event raised when MIDI recording stops;
        /// </summary>
        event EventHandler<MIDIRecordingStoppedEventArgs> RecordingStopped;

        /// <summary>
        /// Event raised when MIDI error occurs;
        /// </summary>
        event EventHandler<MIDIErrorEventArgs> MIDIError;

        /// <summary>
        /// Event raised when MIDI engine state changes;
        /// </summary>
        event EventHandler<MIDIEngineStateChangedEventArgs> EngineStateChanged;

        /// <summary>
        /// Event raised when tempo changes;
        /// </summary>
        event EventHandler<TempoChangedEventArgs> TempoChanged;

        /// <summary>
        /// Event raised when time signature changes;
        /// </summary>
        event EventHandler<TimeSignatureChangedEventArgs> TimeSignatureChanged;
    }

    /// <summary>
    /// Interface for MIDI output devices;
    /// </summary>
    public interface IMIDIOutputDevice : IDisposable
    {
        /// <summary>
        /// Initializes the output device;
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Sends a MIDI event;
        /// </summary>
        Task SendEventAsync(byte[] eventData, CancellationToken cancellationToken);

        /// <summary>
        /// Sends multiple MIDI events;
        /// </summary>
        Task SendEventsAsync(IEnumerable<byte[]> eventsData, CancellationToken cancellationToken);

        /// <summary>
        /// Gets device information;
        /// </summary>
        MIDIDeviceInfo GetDeviceInfo();
    }

    /// <summary>
    /// Interface for MIDI input devices;
    /// </summary>
    public interface IMIDIInputDevice : IDisposable
    {
        /// <summary>
        /// Initializes the input device;
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Starts recording MIDI events;
        /// </summary>
        Task StartRecordingAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Stops recording MIDI events;
        /// </summary>
        Task StopRecordingAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Gets recorded events;
        /// </summary>
        Task<IEnumerable<byte[]>> GetEventsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Checks if there are pending events;
        /// </summary>
        bool HasEvents { get; }

        /// <summary>
        /// Gets device information;
        /// </summary>
        MIDIDeviceInfo GetDeviceInfo();

        /// <summary>
        /// Event raised when MIDI event is received;
        /// </summary>
        event EventHandler<MIDIEventReceivedEventArgs> EventReceived;
    }

    /// <summary>
    /// MIDI engine configuration;
    /// </summary>
    public class MIDIConfiguration;
    {
        public int? DefaultPPQ { get; set; } = 480;
        public int? DefaultTempo { get; set; } = 120;
        public bool SendMIDIClock { get; set; } = true;
        public int? ProcessingIntervalMs { get; set; } = 1;
        public int? MaxMIDIFileSize { get; set; } = 10 * 1024 * 1024; // 10MB;
        public int? MinNoteDurationMs { get; set; } = 50;
        public bool AutoConnectDevices { get; set; } = true;
        public bool EnableMIDIThru { get; set; } = false;
        public int MIDIThruChannel { get; set; } = -1; // -1 for all channels;
        public bool LogMIDIEvents { get; set; } = false;
    }

    /// <summary>
    /// MIDI file;
    /// </summary>
    public class MIDIFile;
    {
        public string Id { get; set; }
        public string FilePath { get; set; }
        public string Name { get; set; }
        public MIDIFormat Format { get; set; }
        public int TrackCount { get; set; }
        public int Division { get; set; }
        public int OriginalTempo { get; set; }
        public TimeSpan Duration { get; set; }
        public List<MIDITrack> Tracks { get; set; }
        public long FileSize { get; set; }
        public MIDILoadOptions LoadOptions { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// MIDI format;
    /// </summary>
    public enum MIDIFormat;
    {
        SingleTrack = 0,
        MultipleTracks = 1,
        MultipleSequences = 2;
    }

    /// <summary>
    /// MIDI track;
    /// </summary>
    public class MIDITrack;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Channel { get; set; } = -1;
        public List<MIDIEvent> Events { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// MIDI event;
    /// </summary>
    public class MIDIEvent : ICloneable;
    {
        public MIDIEventType Type { get; set; }
        public int Channel { get; set; } = -1;
        public long AbsoluteTime { get; set; }
        public int NoteNumber { get; set; }
        public int Velocity { get; set; }
        public int ControllerNumber { get; set; }
        public int ControllerValue { get; set; }
        public int ProgramNumber { get; set; }
        public int PitchBendValue { get; set; }
        public int AftertouchValue { get; set; }
        public int Tempo { get; set; }
        public int TimeSignatureNumerator { get; set; }
        public int TimeSignatureDenominator { get; set; }
        public int KeySignature { get; set; }
        public bool IsMinor { get; set; }
        public string Text { get; set; }
        public byte[] Data { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan? RecordingTime { get; set; }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    /// <summary>
    /// MIDI event type;
    /// </summary>
    public enum MIDIEventType;
    {
        NoteOn,
        NoteOff,
        ControlChange,
        ProgramChange,
        PitchBend,
        Aftertouch,
        ChannelAftertouch,
        SystemExclusive,
        Tempo,
        TimeSignature,
        KeySignature,
        Text,
        Meta,
        EndOfTrack,
        MIDIClock,
        MIDIStart,
        MIDIStop,
        MIDIContinue;
    }

    /// <summary>
    /// MIDI load options;
    /// </summary>
    public class MIDILoadOptions;
    {
        public bool NormalizeNoteVelocities { get; set; }
        public bool QuantizeNotes { get; set; }
        public QuantizeValue QuantizeValue { get; set; } = QuantizeValue.Sixteenth;
        public bool RemoveOverlappingNotes { get; set; }
        public bool MergeTracks { get; set; }
        public bool LoadOnlyFirstTrack { get; set; }
        public int? MaxTrackCount { get; set; }
    }

    /// <summary>
    /// MIDI save options;
    /// </summary>
    public class MIDISaveOptions;
    {
        public bool CompressTracks { get; set; } = true;
        public bool OptimizeEvents { get; set; } = true;
        public bool IncludeMetaEvents { get; set; } = true;
        public MIDIFormat Format { get; set; } = MIDIFormat.MultipleTracks;
        public int? PPQ { get; set; }
    }

    /// <summary>
    /// MIDI play options;
    /// </summary>
    public class MIDIPlayOptions;
    {
        public bool Loop { get; set; }
        public TimeSpan? LoopStart { get; set; }
        public TimeSpan? LoopEnd { get; set; }
        public TimeSpan? StartPosition { get; set; }
        public double? TempoScale { get; set; }
        public bool SendMIDIClock { get; set; } = true;
        public int? TransposeSemitones { get; set; }
    }

    /// <summary>
    /// MIDI stop options;
    /// </summary>
    public class MIDIStopOptions;
    {
        public MIDIStopReason StopReason { get; set; } = MIDIStopReason.UserRequest;
        public bool Immediate { get; set; }
        public TimeSpan? FadeOut { get; set; }
    }

    /// <summary>
    /// MIDI stop reason;
    /// </summary>
    public enum MIDIStopReason;
    {
        UserRequest,
        Completed,
        Error,
        Cleared,
        EndOfTrack;
    }

    /// <summary>
    /// MIDI record options;
    /// </summary>
    public class MIDIRecordOptions;
    {
        public int? RecordChannel { get; set; }
        public bool QuantizeRecordedNotes { get; set; }
        public QuantizeValue QuantizeValue { get; set; } = QuantizeValue.Sixteenth;
        public bool RemoveNoise { get; set; } = true;
        public TimeSpan? PreRoll { get; set; }
        public TimeSpan? PostRoll { get; set; }
        public bool MergeWithExisting { get; set; }
        public string TrackName { get; set; }
    }

    /// <summary>
    /// Quantize value;
    /// </summary>
    public enum QuantizeValue;
    {
        Whole,
        Half,
        Quarter,
        Eighth,
        Sixteenth,
        ThirtySecond,
        SixtyFourth;
    }

    /// <summary>
    /// Time signature;
    /// </summary>
    public class TimeSignature;
    {
        public int Numerator { get; set; }
        public int Denominator { get; set; }

        public TimeSignature(int numerator, int denominator)
        {
            Numerator = numerator;
            Denominator = denominator;
        }

        public override string ToString() => $"{Numerator}/{Denominator}";
    }

    /// <summary>
    /// MIDI engine statistics;
    /// </summary>
    public class MIDIEngineStatistics;
    {
        public MIDIEngineStatus Status { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsRecording { get; set; }
        public bool IsPaused { get; set; }
        public int LoadedSequencesCount { get; set; }
        public string CurrentSequenceId { get; set; }
        public TimeSpan CurrentPosition { get; set; }
        public int Tempo { get; set; }
        public TimeSignature TimeSignature { get; set; }
        public bool OutputDeviceAvailable { get; set; }
        public bool InputDeviceAvailable { get; set; }
        public string OutputDeviceName { get; set; }
        public string InputDeviceName { get; set; }
        public List<MIDIChannel> Channels { get; set; }
        public IEnumerable<MIDINote> ActiveNotes { get; set; }
        public MIDIPerformanceMetrics PerformanceMetrics { get; set; }
        public TimeSpan Uptime { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// MIDI engine status;
    /// </summary>
    public enum MIDIEngineStatus;
    {
        Uninitialized,
        Initializing,
        Ready,
        Playing,
        Recording,
        Paused,
        Error,
        ShuttingDown;
    }

    /// <summary>
    /// MIDI channel;
    /// </summary>
    public class MIDIChannel;
    {
        public int ChannelNumber { get; set; }
        public int Program { get; set; }
        public int Volume { get; set; }
        public int Pan { get; set; }
        public int Expression { get; set; }
        public int PitchBend { get; set; }
        public int Modulation { get; set; }
        public bool SustainPedal { get; set; }
        public List<MIDINote> ActiveNotes { get; set; }
        public DateTime LastActivity { get; set; }
    }

    /// <summary>
    /// MIDI note;
    /// </summary>
    public class MIDINote;
    {
        public int Channel { get; set; }
        public int NoteNumber { get; set; }
        public int Velocity { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string NoteName => GetNoteName(NoteNumber);

        private string GetNoteName(int noteNumber)
        {
            var noteNames = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            var octave = noteNumber / 12 - 1;
            var noteIndex = noteNumber % 12;
            return $"{noteNames[noteIndex]}{octave}";
        }
    }

    /// <summary>
    /// MIDI performance metrics;
    /// </summary>
    public class MIDIPerformanceMetrics;
    {
        public double ProcessingTime { get; set; }
        public int ActiveNotesCount { get; set; }
        public double EventsProcessedPerSecond { get; set; }
        public float CpuUsage { get; set; }
        public long MemoryUsage { get; set; }
        public float Latency { get; set; }
    }

    /// <summary>
    /// MIDI device information;
    /// </summary>
    public class MIDIDeviceInfo;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MIDIDeviceType Type { get; set; }
        public bool IsAvailable { get; set; }
        public string Manufacturer { get; set; }
        public string Version { get; set; }
    }

    /// <summary>
    /// MIDI device type;
    /// </summary>
    public enum MIDIDeviceType;
    {
        Input,
        Output,
        Bidirectional;
    }

    /// <summary>
    /// MIDI port;
    /// </summary>
    public class MIDIPort;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MIDIDeviceType Type { get; set; }
        public bool IsOpen { get; set; }
        public DateTime OpenTime { get; set; }
    }

    /// <summary>
    /// Event arguments for MIDI file loaded;
    /// </summary>
    public class MIDIFileLoadedEventArgs : EventArgs;
    {
        public MIDIFile File { get; }
        public string OperationId { get; }
        public long FileSize { get; }
        public MIDIFormat Format { get; }
        public int TrackCount { get; }

        public MIDIFileLoadedEventArgs(
            MIDIFile file,
            string operationId,
            long fileSize,
            MIDIFormat format,
            int trackCount)
        {
            File = file;
            OperationId = operationId;
            FileSize = fileSize;
            Format = format;
            TrackCount = trackCount;
        }
    }

    /// <summary>
    /// Event arguments for MIDI playback started;
    /// </summary>
    public class MIDIPlaybackStartedEventArgs : EventArgs;
    {
        public string SequenceId { get; }
        public string OperationId { get; }
        public TimeSpan Position { get; }
        public int Tempo { get; }

        public MIDIPlaybackStartedEventArgs(
            string sequenceId,
            string operationId,
            TimeSpan position,
            int tempo)
        {
            SequenceId = sequenceId;
            OperationId = operationId;
            Position = position;
            Tempo = tempo;
        }
    }

    /// <summary>
    /// Event arguments for MIDI playback stopped;
    /// </summary>
    public class MIDIPlaybackStoppedEventArgs : EventArgs;
    {
        public string SequenceId { get; }
        public string OperationId { get; }
        public TimeSpan Position { get; }
        public MIDIStopReason Reason { get; }

        public MIDIPlaybackStoppedEventArgs(
            string sequenceId,
            string operationId,
            TimeSpan position,
            MIDIStopReason reason)
        {
            SequenceId = sequenceId;
            OperationId = operationId;
            Position = position;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event arguments for MIDI playback position changed;
    /// </summary>
    public class MIDIPlaybackPositionChangedEventArgs : EventArgs;
    {
        public string SequenceId { get; }
        public string OperationId { get; }
        public TimeSpan Position { get; }
        public double Progress { get; }

        public MIDIPlaybackPositionChangedEventArgs(
            string sequenceId,
            string operationId,
            TimeSpan position,
            double progress)
        {
            SequenceId = sequenceId;
            OperationId = operationId;
            Position = position;
            Progress = progress;
        }
    }

    /// <summary>
    /// Event arguments for MIDI event received;
    /// </summary>
    public class MIDIEventReceivedEventArgs : EventArgs;
    {
        public MIDIEvent Event { get; }
        public string OperationId { get; }
        public bool IsRecording { get; }

        public MIDIEventReceivedEventArgs(
            MIDIEvent midiEvent,
            string operationId,
            bool isRecording = false)
        {
            Event = midiEvent;
            OperationId = operationId;
            IsRecording = isRecording;
        }
    }

    /// <summary>
    /// Event arguments for MIDI event sent;
    /// </summary>
    public class MIDIEventSentEventArgs : EventArgs;
    {
        public MIDIEvent Event { get; }
        public string OperationId { get; }
        public DateTime SentTime { get; }

        public MIDIEventSentEventArgs(
            MIDIEvent midiEvent,
            string operationId,
            DateTime sentTime)
        {
            Event = midiEvent;
            OperationId = operationId;
            SentTime = sentTime;
        }
    }

    /// <summary>
    /// Event arguments for MIDI recording started;
    /// </summary>
    public class MIDIRecordingStartedEventArgs : EventArgs;
    {
        public MIDITrack Track { get; }
        public string OperationId { get; }
        public MIDIRecordOptions Options { get; }

        public MIDIRecordingStartedEventArgs(
            MIDITrack track,
            string operationId,
            MIDIRecordOptions options)
        {
            Track = track;
            OperationId = operationId;
            Options = options;
        }
    }

    /// <summary>
    /// Event arguments for MIDI recording stopped;
    /// </summary>
    public class MIDIRecordingStoppedEventArgs : EventArgs;
    {
        public MIDITrack Track { get; }
        public string OperationId { get; }
        public TimeSpan Duration { get; }
        public int EventCount { get; }

        public MIDIRecordingStoppedEventArgs(
            MIDITrack track,
            string operationId,
            TimeSpan duration,
            int eventCount)
        {
            Track = track;
            OperationId = operationId;
            Duration = duration;
            EventCount = eventCount;
        }
    }

    /// <summary>
    /// Event arguments for MIDI error;
    /// </summary>
    public class MIDIErrorEventArgs : EventArgs;
    {
        public MIDIErrorType ErrorType { get; }
        public string Message { get; }
        public Exception Exception { get; }
        public string OperationId { get; }

        public MIDIErrorEventArgs(
            MIDIErrorType errorType,
            string message,
            Exception exception,
            string operationId)
        {
            ErrorType = errorType;
            Message = message;
            Exception = exception;
            OperationId = operationId;
        }
    }

    /// <summary>
    /// MIDI error type;
    /// </summary>
    public enum MIDIErrorType;
    {
        InitializationError,
        FileLoadError,
        FileSaveError,
        PlaybackError,
        RecordingError,
        EventError,
        SequenceError,
        DeviceError,
        EngineError;
    }

    /// <summary>
    /// Event arguments for MIDI engine state changed;
    /// </summary>
    public class MIDIEngineStateChangedEventArgs : EventArgs;
    {
        public MIDIEngineStatus Status { get; }
        public string Message { get; }
        public string OperationId { get; }

        public MIDIEngineStateChangedEventArgs(
            MIDIEngineStatus status,
            string message,
            string operationId)
        {
            Status = status;
            Message = message;
            OperationId = operationId;
        }
    }

    /// <summary>
    /// Event arguments for tempo changed;
    /// </summary>
    public class TempoChangedEventArgs : EventArgs;
    {
        public string SequenceId { get; }
        public string OperationId { get; }
        public int Tempo { get; }
        public TimeSpan Position { get; }

        public TempoChangedEventArgs(
            string sequenceId,
            string operationId,
            int tempo,
            TimeSpan position)
        {
            SequenceId = sequenceId;
            OperationId = operationId;
            Tempo = tempo;
            Position = position;
        }
    }

    /// <summary>
    /// Event arguments for time signature changed;
    /// </summary>
    public class TimeSignatureChangedEventArgs : EventArgs;
    {
        public string SequenceId { get; }
        public string OperationId { get; }
        public TimeSignature TimeSignature { get; }
        public TimeSpan Position { get; }

        public TimeSignatureChangedEventArgs(
            string sequenceId,
            string operationId,
            TimeSignature timeSignature,
            TimeSpan position)
        {
            SequenceId = sequenceId;
            OperationId = operationId;
            TimeSignature = timeSignature;
            Position = position;
        }
    }

    /// <summary>
    /// Event arguments for MIDI event played by sequencer;
    /// </summary>
    public class MIDIEventPlayedEventArgs : EventArgs;
    {
        public MIDIEvent Event { get; }
        public string OperationId { get; }

        public MIDIEventPlayedEventArgs(
            MIDIEvent midiEvent,
            string operationId)
        {
            Event = midiEvent;
            OperationId = operationId;
        }
    }

    /// <summary>
    /// Custom exception for MIDI engine initialization errors;
    /// </summary>
    public class MIDIEngineInitializationException : Exception
    {
        public string OperationId { get; }

        public MIDIEngineInitializationException(
            string message,
            Exception innerException,
            string operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    /// <summary>
    /// Custom exception for MIDI file errors;
    /// </summary>
    public class MIDIFileException : Exception
    {
        public string SequenceId { get; }
        public string FilePath { get; }
        public string OperationId { get; }

        public MIDIFileException(
            string message,
            string sequenceId)
            : base(message)
        {
            SequenceId = sequenceId;
        }

        public MIDIFileException(
            string message,
            Exception innerException,
            string sequenceId,
            string filePath,
            string operationId)
            : base(message, innerException)
        {
            SequenceId = sequenceId;
            FilePath = filePath;
            OperationId = operationId;
        }
    }

    /// <summary>
    /// Custom exception for MIDI playback errors;
    /// </summary>
    public class MIDIPlaybackException : Exception
    {
        public string SequenceId { get; }
        public string OperationId { get; }

        public MIDIPlaybackException(
            string message,
            Exception innerException,
            string sequenceId,
            string operationId)
            : base(message, innerException)
        {
            SequenceId = sequenceId;
            OperationId = operationId;
        }
    }

    /// <summary>
    /// Custom exception for MIDI recording errors;
    /// </summary>
    public class MIDIRecordingException : Exception
    {
        public string OperationId { get; }

        public MIDIRecordingException(
            string message,
            Exception innerException,
            string operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    /// <summary>
    /// Custom exception for MIDI event errors;
    /// </summary>
    public class MIDIEventException : Exception
    {
        public MIDIEvent Event { get; }
        public string OperationId { get; }

        public MIDIEventException(
            string message,
            Exception innerException,
            MIDI;
