using NEDA.Animation.Common.Enums;
using NEDA.Animation.Common.Models;
using NEDA.Animation.Interfaces;
using NEDA.Animation.SequenceEditor.KeyframeEditing;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.Animation.SequenceEditor.TimelineManagement;
{
    /// <summary>
    /// Advanced sequence management system for handling complex animation timelines,
    /// scene management, and real-time playback control;
    /// </summary>
    public class SequenceManager : ISequenceManager, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly IAnimationSequenceStorage _sequenceStorage;
        private readonly IKeyframeDataService _keyframeService;
        private readonly object _lockObject = new object();
        private readonly Dictionary<string, AnimationSequence> _loadedSequences;
        private readonly List<SequenceMarker> _globalMarkers;
        private AnimationSequence _currentSequence;
        private PlaybackController _playbackController;
        private bool _disposed = false;
        private bool _isInitialized = false;
        #endregion;

        #region Public Properties;
        public string ManagerName { get; private set; }
        public SequenceManagerState CurrentState { get; private set; }
        public SequenceManagerSettings Settings { get; private set; }
        public AnimationSequence CurrentSequence => _currentSequence;
        public IReadOnlyDictionary<string, AnimationSequence> LoadedSequences => _loadedSequences;
        public IReadOnlyList<SequenceMarker> GlobalMarkers => _globalMarkers.AsReadOnly();
        public PlaybackInfo PlaybackInfo => _playbackController?.CurrentInfo ?? new PlaybackInfo();
        public bool IsPlaying => _playbackController?.IsPlaying ?? false;
        public bool IsPaused => _playbackController?.IsPaused ?? false;
        public event EventHandler<SequenceManagerEventArgs> SequenceManagerEvent;
        public event EventHandler<PlaybackStateChangedEventArgs> PlaybackStateChanged;
        public event EventHandler<SequenceLoadedEventArgs> SequenceLoaded;
        public event EventHandler<TimeChangedEventArgs> TimeChanged;
        #endregion;

        #region Constructors;
        public SequenceManager(ILogger logger, IAnimationSequenceStorage sequenceStorage, IKeyframeDataService keyframeService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sequenceStorage = sequenceStorage ?? throw new ArgumentNullException(nameof(sequenceStorage));
            _keyframeService = keyframeService ?? throw new ArgumentNullException(nameof(keyframeService));

            _loadedSequences = new Dictionary<string, AnimationSequence>();
            _globalMarkers = new List<SequenceMarker>();
            _playbackController = new PlaybackController();

            InitializeManager();
        }

        public SequenceManager(ILogger logger)
            : this(logger, new DefaultAnimationSequenceStorage(), new DefaultKeyframeDataService())
        {
        }
        #endregion;

        #region Initialization;
        private void InitializeManager()
        {
            ManagerName = "Advanced Sequence Manager";
            CurrentState = SequenceManagerState.Stopped;

            Settings = new SequenceManagerSettings;
            {
                AutoSaveInterval = TimeSpan.FromMinutes(5),
                MaxUndoLevels = 50,
                DefaultFrameRate = 30,
                TimeDisplayFormat = TimeDisplayFormat.Timecode,
                LoopPlayback = false,
                SnapToFrames = true,
                ShowAudioWaveform = true,
                EnableAutoKeyframing = false;
            };

            _playbackController.PlaybackStateChanged += OnPlaybackControllerStateChanged;
            _playbackController.TimeChanged += OnPlaybackControllerTimeChanged;

            _isInitialized = true;
            _logger.LogInformation($"SequenceManager initialized: {ManagerName}");
        }

        public async Task InitializeAsync()
        {
            ValidateNotDisposed();

            if (_isInitialized)
            {
                return;
            }

            try
            {
                CurrentState = SequenceManagerState.Initializing;

                // Load default sequences and configuration;
                await LoadGlobalMarkersAsync();
                await LoadRecentSequencesAsync();

                CurrentState = SequenceManagerState.Ready;
                _isInitialized = true;

                _logger.LogInformation("SequenceManager initialized asynchronously");

                OnSequenceManagerEvent(new SequenceManagerEventArgs;
                {
                    EventType = SequenceManagerEventType.Initialized,
                    Message = "Sequence manager initialized successfully"
                });
            }
            catch (Exception ex)
            {
                CurrentState = SequenceManagerState.Error;
                _logger.LogError(ex, "Failed to initialize SequenceManager");
                throw new SequenceManagerException("Initialization failed", ex);
            }
        }
        #endregion;

        #region Sequence Management;
        public async Task<AnimationSequence> CreateSequenceAsync(string sequenceName, double duration, double frameRate)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateSequenceName(sequenceName);
            ValidateDuration(duration);
            ValidateFrameRate(frameRate);

            try
            {
                var sequence = new AnimationSequence;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = sequenceName,
                    Duration = duration,
                    FrameRate = frameRate,
                    CreatedAt = DateTime.UtcNow,
                    ModifiedAt = DateTime.UtcNow,
                    State = SequenceState.Draft;
                };

                lock (_lockObject)
                {
                    if (_loadedSequences.ContainsKey(sequence.Id))
                    {
                        throw new SequenceManagerException($"Sequence with ID {sequence.Id} already exists");
                    }

                    _loadedSequences[sequence.Id] = sequence;
                }

                await _sequenceStorage.SaveSequenceAsync(sequence);

                _logger.LogInformation($"Sequence created: {sequenceName} (ID: {sequence.Id})");

                OnSequenceManagerEvent(new SequenceManagerEventArgs;
                {
                    EventType = SequenceManagerEventType.SequenceCreated,
                    Sequence = sequence,
                    Message = $"Sequence '{sequenceName}' created"
                });

                return sequence;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create sequence: {sequenceName}");
                throw new SequenceManagerException($"Sequence creation failed: {ex.Message}", ex);
            }
        }

        public async Task<AnimationSequence> LoadSequenceAsync(string sequenceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateSequenceId(sequenceId);

            try
            {
                AnimationSequence sequence;

                lock (_lockObject)
                {
                    if (_loadedSequences.TryGetValue(sequenceId, out var loadedSequence))
                    {
                        sequence = loadedSequence;
                    }
                    else;
                    {
                        sequence = _sequenceStorage.LoadSequence(sequenceId);
                        if (sequence == null)
                        {
                            throw new SequenceNotFoundException($"Sequence not found: {sequenceId}");
                        }
                        _loadedSequences[sequenceId] = sequence;
                    }
                }

                // Stop current playback if any;
                if (IsPlaying || IsPaused)
                {
                    await StopPlaybackAsync();
                }

                _currentSequence = sequence;
                _playbackController.SetSequence(sequence);

                _logger.LogInformation($"Sequence loaded: {sequence.Name} (ID: {sequenceId})");

                OnSequenceLoaded(new SequenceLoadedEventArgs;
                {
                    Sequence = sequence,
                    PreviousSequence = _currentSequence,
                    LoadTime = DateTime.UtcNow;
                });

                return sequence;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load sequence: {sequenceId}");
                throw new SequenceManagerException($"Sequence load failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> SaveSequenceAsync(string sequenceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateSequenceId(sequenceId);

            try
            {
                AnimationSequence sequence;
                lock (_lockObject)
                {
                    if (!_loadedSequences.TryGetValue(sequenceId, out sequence))
                    {
                        throw new SequenceNotFoundException($"Sequence not loaded: {sequenceId}");
                    }
                }

                sequence.ModifiedAt = DateTime.UtcNow;
                await _sequenceStorage.SaveSequenceAsync(sequence);

                _logger.LogDebug($"Sequence saved: {sequence.Name} (ID: {sequenceId})");

                OnSequenceManagerEvent(new SequenceManagerEventArgs;
                {
                    EventType = SequenceManagerEventType.SequenceSaved,
                    Sequence = sequence,
                    Message = $"Sequence '{sequence.Name}' saved"
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save sequence: {sequenceId}");
                throw new SequenceManagerException($"Sequence save failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> SaveAllSequencesAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                var saveTasks = new List<Task>();
                List<AnimationSequence> sequences;

                lock (_lockObject)
                {
                    sequences = _loadedSequences.Values.ToList();
                }

                foreach (var sequence in sequences)
                {
                    saveTasks.Add(SaveSequenceAsync(sequence.Id));
                }

                await Task.WhenAll(saveTasks);

                _logger.LogInformation($"All sequences saved: {sequences.Count} sequences");

                OnSequenceManagerEvent(new SequenceManagerEventArgs;
                {
                    EventType = SequenceManagerEventType.AllSequencesSaved,
                    Message = $"All {sequences.Count} sequences saved"
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save all sequences");
                throw new SequenceManagerException($"Save all sequences failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> CloseSequenceAsync(string sequenceId, bool saveChanges = true)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateSequenceId(sequenceId);

            try
            {
                AnimationSequence sequence;
                lock (_lockObject)
                {
                    if (!_loadedSequences.TryGetValue(sequenceId, out sequence))
                    {
                        return false; // Sequence not loaded, nothing to close;
                    }
                }

                // Save if requested;
                if (saveChanges)
                {
                    await SaveSequenceAsync(sequenceId);
                }

                // Stop playback if this is the current sequence;
                if (_currentSequence?.Id == sequenceId)
                {
                    if (IsPlaying || IsPaused)
                    {
                        await StopPlaybackAsync();
                    }
                    _currentSequence = null;
                }

                // Remove from loaded sequences;
                lock (_lockObject)
                {
                    _loadedSequences.Remove(sequenceId);
                }

                _logger.LogInformation($"Sequence closed: {sequence.Name} (ID: {sequenceId})");

                OnSequenceManagerEvent(new SequenceManagerEventArgs;
                {
                    EventType = SequenceManagerEventType.SequenceClosed,
                    Sequence = sequence,
                    Message = $"Sequence '{sequence.Name}' closed"
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to close sequence: {sequenceId}");
                throw new SequenceManagerException($"Sequence close failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> DeleteSequenceAsync(string sequenceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateSequenceId(sequenceId);

            try
            {
                // Close sequence first if loaded;
                if (_loadedSequences.ContainsKey(sequenceId))
                {
                    await CloseSequenceAsync(sequenceId, false);
                }

                // Delete from storage;
                var success = await _sequenceStorage.DeleteSequenceAsync(sequenceId);

                if (success)
                {
                    _logger.LogInformation($"Sequence deleted: {sequenceId}");

                    OnSequenceManagerEvent(new SequenceManagerEventArgs;
                    {
                        EventType = SequenceManagerEventType.SequenceDeleted,
                        Message = $"Sequence deleted: {sequenceId}"
                    });
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete sequence: {sequenceId}");
                throw new SequenceManagerException($"Sequence deletion failed: {ex.Message}", ex);
            }
        }

        public IReadOnlyList<AnimationSequence> GetSequencesByState(SequenceState state)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_lockObject)
            {
                return _loadedSequences.Values;
                    .Where(s => s.State == state)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public IReadOnlyList<AnimationSequence> FindSequencesByName(string namePattern)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(namePattern))
            {
                return new List<AnimationSequence>().AsReadOnly();
            }

            lock (_lockObject)
            {
                return _loadedSequences.Values;
                    .Where(s => s.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                    .AsReadOnly();
            }
        }
        #endregion;

        #region Playback Control;
        public async Task<bool> PlayAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateCurrentSequence();

            try
            {
                if (IsPlaying)
                {
                    return true; // Already playing;
                }

                var success = await _playbackController.PlayAsync();

                if (success)
                {
                    CurrentState = SequenceManagerState.Playing;
                    _logger.LogDebug($"Playback started: {_currentSequence.Name}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start playback");
                throw new SequenceManagerException($"Playback start failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> PauseAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                if (!IsPlaying)
                {
                    return false; // Not playing;
                }

                var success = await _playbackController.PauseAsync();

                if (success)
                {
                    CurrentState = SequenceManagerState.Paused;
                    _logger.LogDebug("Playback paused");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause playback");
                throw new SequenceManagerException($"Playback pause failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> StopAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                if (!IsPlaying && !IsPaused)
                {
                    return true; // Already stopped;
                }

                var success = await _playbackController.StopAsync();

                if (success)
                {
                    CurrentState = SequenceManagerState.Stopped;
                    _logger.LogDebug("Playback stopped");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop playback");
                throw new SequenceManagerException($"Playback stop failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> SeekAsync(double time)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateCurrentSequence();
            ValidateTimeValue(time);

            try
            {
                var success = await _playbackController.SeekAsync(time);

                if (success)
                {
                    _logger.LogDebug($"Seeked to time: {time}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to seek to time: {time}");
                throw new SequenceManagerException($"Seek failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> SetPlaybackSpeedAsync(double speed)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidatePlaybackSpeed(speed);

            try
            {
                var success = await _playbackController.SetPlaybackSpeedAsync(speed);

                if (success)
                {
                    _logger.LogDebug($"Playback speed set to: {speed}x");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set playback speed: {speed}");
                throw new SequenceManagerException($"Playback speed change failed: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Marker Management;
        public SequenceMarker AddGlobalMarker(string name, double time, MarkerType type, string description = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateMarkerName(name);
            ValidateTimeValue(time);

            try
            {
                var marker = new SequenceMarker;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    Time = time,
                    Type = type,
                    Description = description,
                    CreatedAt = DateTime.UtcNow,
                    IsGlobal = true;
                };

                lock (_lockObject)
                {
                    _globalMarkers.Add(marker);
                    _globalMarkers.Sort((a, b) => a.Time.CompareTo(b.Time));
                }

                _logger.LogDebug($"Global marker added: {name} at time {time}");

                OnSequenceManagerEvent(new SequenceManagerEventArgs;
                {
                    EventType = SequenceManagerEventType.MarkerAdded,
                    Marker = marker,
                    Message = $"Global marker '{name}' added at {time}"
                });

                return marker;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add global marker: {name}");
                throw new SequenceManagerException($"Marker addition failed: {ex.Message}", ex);
            }
        }

        public bool RemoveGlobalMarker(string markerId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateMarkerId(markerId);

            try
            {
                SequenceMarker removedMarker = null;

                lock (_lockObject)
                {
                    var marker = _globalMarkers.FirstOrDefault(m => m.Id == markerId);
                    if (marker != null)
                    {
                        removedMarker = marker;
                        _globalMarkers.Remove(marker);
                    }
                }

                if (removedMarker != null)
                {
                    _logger.LogDebug($"Global marker removed: {removedMarker.Name}");

                    OnSequenceManagerEvent(new SequenceManagerEventArgs;
                    {
                        EventType = SequenceManagerEventType.MarkerRemoved,
                        Marker = removedMarker,
                        Message = $"Global marker '{removedMarker.Name}' removed"
                    });

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove global marker: {markerId}");
                throw new SequenceManagerException($"Marker removal failed: {ex.Message}", ex);
            }
        }

        public IReadOnlyList<SequenceMarker> GetMarkersInRange(double startTime, double endTime, bool includeGlobal = true)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTimeRange(startTime, endTime);

            var markers = new List<SequenceMarker>();

            if (includeGlobal)
            {
                lock (_lockObject)
                {
                    markers.AddRange(_globalMarkers.Where(m => m.Time >= startTime && m.Time <= endTime));
                }
            }

            if (_currentSequence != null)
            {
                markers.AddRange(_currentSequence.Markers.Where(m => m.Time >= startTime && m.Time <= endTime));
            }

            return markers.OrderBy(m => m.Time).ToList().AsReadOnly();
        }
        #endregion;

        #region Sequence Analysis;
        public SequenceAnalysisResult AnalyzeSequence(string sequenceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateSequenceId(sequenceId);

            try
            {
                AnimationSequence sequence;
                lock (_lockObject)
                {
                    if (!_loadedSequences.TryGetValue(sequenceId, out sequence))
                    {
                        throw new SequenceNotFoundException($"Sequence not found: {sequenceId}");
                    }
                }

                var keyframes = _keyframeService.GetKeyframesForSequence(sequenceId);
                var analysis = new SequenceAnalysisResult;
                {
                    SequenceId = sequenceId,
                    SequenceName = sequence.Name,
                    TotalKeyframes = keyframes.Count,
                    Duration = sequence.Duration,
                    FrameRate = sequence.FrameRate,
                    KeyframesPerProperty = CalculateKeyframesPerProperty(keyframes),
                    TimeRange = CalculateTimeRange(keyframes),
                    ValueRanges = CalculateValueRanges(keyframes),
                    MarkerCount = sequence.Markers.Count,
                    CreatedAt = sequence.CreatedAt,
                    ModifiedAt = sequence.ModifiedAt;
                };

                _logger.LogDebug($"Sequence analysis completed: {sequence.Name}");
                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to analyze sequence: {sequenceId}");
                throw new SequenceManagerException($"Sequence analysis failed: {ex.Message}", ex);
            }
        }

        public async Task<SequenceStatistics> GetSequenceStatisticsAsync(string sequenceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateSequenceId(sequenceId);

            try
            {
                var analysis = AnalyzeSequence(sequenceId);
                var keyframes = _keyframeService.GetKeyframesForSequence(sequenceId);

                var statistics = new SequenceStatistics;
                {
                    SequenceId = sequenceId,
                    TotalKeyframes = analysis.TotalKeyframes,
                    UniqueProperties = analysis.KeyframesPerProperty.Count,
                    TotalDuration = analysis.Duration,
                    AverageKeyframesPerSecond = analysis.TotalKeyframes / analysis.Duration,
                    MemoryUsage = CalculateMemoryUsage(keyframes),
                    ComplexityScore = CalculateComplexityScore(keyframes),
                    LastActivity = analysis.ModifiedAt;
                };

                return await Task.FromResult(statistics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get sequence statistics: {sequenceId}");
                throw new SequenceManagerException($"Statistics calculation failed: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Utility Methods;
        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SequenceManager));
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new SequenceManagerException("SequenceManager not initialized. Call InitializeAsync first.");
        }

        private void ValidateCurrentSequence()
        {
            if (_currentSequence == null)
                throw new SequenceManagerException("No current sequence loaded");
        }

        private void ValidateSequenceName(string sequenceName)
        {
            if (string.IsNullOrWhiteSpace(sequenceName))
                throw new ArgumentException("Sequence name cannot be null or empty", nameof(sequenceName));

            if (sequenceName.Length > 100)
                throw new ArgumentException("Sequence name too long", nameof(sequenceName));
        }

        private void ValidateSequenceId(string sequenceId)
        {
            if (string.IsNullOrWhiteSpace(sequenceId))
                throw new ArgumentException("Sequence ID cannot be null or empty", nameof(sequenceId));
        }

        private void ValidateDuration(double duration)
        {
            if (duration <= 0)
                throw new ArgumentException("Duration must be positive", nameof(duration));

            if (duration > 24 * 60 * 60) // 24 hours max;
                throw new ArgumentException("Duration too long", nameof(duration));
        }

        private void ValidateFrameRate(double frameRate)
        {
            if (frameRate <= 0)
                throw new ArgumentException("Frame rate must be positive", nameof(frameRate));

            if (frameRate > 1000)
                throw new ArgumentException("Frame rate too high", nameof(frameRate));
        }

        private void ValidateTimeValue(double time)
        {
            if (time < 0)
                throw new ArgumentException("Time cannot be negative", nameof(time));
        }

        private void ValidateTimeRange(double startTime, double endTime)
        {
            if (startTime < 0 || endTime < 0)
                throw new ArgumentException("Time values cannot be negative");

            if (startTime > endTime)
                throw new ArgumentException("Start time cannot be greater than end time");
        }

        private void ValidatePlaybackSpeed(double speed)
        {
            if (speed <= 0)
                throw new ArgumentException("Playback speed must be positive", nameof(speed));

            if (speed > 100)
                throw new ArgumentException("Playback speed too high", nameof(speed));
        }

        private void ValidateMarkerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Marker name cannot be null or empty", nameof(name));
        }

        private void ValidateMarkerId(string markerId)
        {
            if (string.IsNullOrWhiteSpace(markerId))
                throw new ArgumentException("Marker ID cannot be null or empty", nameof(markerId));
        }

        private async Task LoadGlobalMarkersAsync()
        {
            try
            {
                var markers = await _sequenceStorage.LoadGlobalMarkersAsync();
                lock (_lockObject)
                {
                    _globalMarkers.Clear();
                    _globalMarkers.AddRange(markers);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load global markers");
            }
        }

        private async Task LoadRecentSequencesAsync()
        {
            try
            {
                var recentSequences = await _sequenceStorage.GetRecentSequencesAsync(10);
                foreach (var sequence in recentSequences)
                {
                    if (!_loadedSequences.ContainsKey(sequence.Id))
                    {
                        _loadedSequences[sequence.Id] = sequence;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load recent sequences");
            }
        }

        private Dictionary<string, int> CalculateKeyframesPerProperty(IEnumerable<Keyframe> keyframes)
        {
            return keyframes;
                .GroupBy(k => k.PropertyName)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private TimeRange CalculateTimeRange(IEnumerable<Keyframe> keyframes)
        {
            if (!keyframes.Any())
                return new TimeRange(0, 0);

            var minTime = keyframes.Min(k => k.Time);
            var maxTime = keyframes.Max(k => k.Time);
            return new TimeRange(minTime, maxTime);
        }

        private Dictionary<string, ValueRange> CalculateValueRanges(IEnumerable<Keyframe> keyframes)
        {
            return keyframes;
                .GroupBy(k => k.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => new ValueRange(g.Min(k => k.Value), g.Max(k => k.Value))
                );
        }

        private long CalculateMemoryUsage(IEnumerable<Keyframe> keyframes)
        {
            // Rough estimation: each keyframe ~100 bytes;
            return keyframes.Count() * 100L;
        }

        private double CalculateComplexityScore(IEnumerable<Keyframe> keyframes)
        {
            if (!keyframes.Any())
                return 0;

            var propertyCount = keyframes.Select(k => k.PropertyName).Distinct().Count();
            var totalKeyframes = keyframes.Count();
            var timeRange = CalculateTimeRange(keyframes);

            // Simple complexity formula;
            return (propertyCount * totalKeyframes) / timeRange.Duration;
        }
        #endregion;

        #region Event Handlers;
        private void OnPlaybackControllerStateChanged(object sender, PlaybackStateChangedEventArgs e)
        {
            PlaybackStateChanged?.Invoke(this, e);
        }

        private void OnPlaybackControllerTimeChanged(object sender, TimeChangedEventArgs e)
        {
            TimeChanged?.Invoke(this, e);
        }

        protected virtual void OnSequenceManagerEvent(SequenceManagerEventArgs e)
        {
            SequenceManagerEvent?.Invoke(this, e);
        }

        protected virtual void OnSequenceLoaded(SequenceLoadedEventArgs e)
        {
            SequenceLoaded?.Invoke(this, e);
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
                    // Stop playback;
                    if (IsPlaying || IsPaused)
                    {
                        StopAsync().Wait(5000); // Wait up to 5 seconds;
                    }

                    // Save all sequences;
                    if (_loadedSequences.Any())
                    {
                        SaveAllSequencesAsync().Wait(5000);
                    }

                    // Dispose resources;
                    _playbackController.PlaybackStateChanged -= OnPlaybackControllerStateChanged;
                    _playbackController.TimeChanged -= OnPlaybackControllerTimeChanged;

                    if (_playbackController is IDisposable disposablePlayback)
                    {
                        disposablePlayback.Dispose();
                    }

                    if (_sequenceStorage is IDisposable disposableStorage)
                    {
                        disposableStorage.Dispose();
                    }

                    if (_keyframeService is IDisposable disposableKeyframe)
                    {
                        disposableKeyframe.Dispose();
                    }

                    _loadedSequences.Clear();
                    _globalMarkers.Clear();

                    _logger.LogInformation("SequenceManager disposed");
                }

                _disposed = true;
            }
        }

        ~SequenceManager()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes;
    public class SequenceManagerSettings;
    {
        public TimeSpan AutoSaveInterval { get; set; }
        public int MaxUndoLevels { get; set; }
        public double DefaultFrameRate { get; set; }
        public TimeDisplayFormat TimeDisplayFormat { get; set; }
        public bool LoopPlayback { get; set; }
        public bool SnapToFrames { get; set; }
        public bool ShowAudioWaveform { get; set; }
        public bool EnableAutoKeyframing { get; set; }
    }

    public class SequenceAnalysisResult;
    {
        public string SequenceId { get; set; }
        public string SequenceName { get; set; }
        public int TotalKeyframes { get; set; }
        public double Duration { get; set; }
        public double FrameRate { get; set; }
        public Dictionary<string, int> KeyframesPerProperty { get; set; }
        public TimeRange TimeRange { get; set; }
        public Dictionary<string, ValueRange> ValueRanges { get; set; }
        public int MarkerCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }

    public class SequenceStatistics;
    {
        public string SequenceId { get; set; }
        public int TotalKeyframes { get; set; }
        public int UniqueProperties { get; set; }
        public double TotalDuration { get; set; }
        public double AverageKeyframesPerSecond { get; set; }
        public long MemoryUsage { get; set; }
        public double ComplexityScore { get; set; }
        public DateTime LastActivity { get; set; }
    }

    public class PlaybackController;
    {
        public PlaybackInfo CurrentInfo { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public event EventHandler<PlaybackStateChangedEventArgs> PlaybackStateChanged;
        public event EventHandler<TimeChangedEventArgs> TimeChanged;

        public Task<bool> PlayAsync() => Task.FromResult(true);
        public Task<bool> PauseAsync() => Task.FromResult(true);
        public Task<bool> StopAsync() => Task.FromResult(true);
        public Task<bool> SeekAsync(double time) => Task.FromResult(true);
        public Task<bool> SetPlaybackSpeedAsync(double speed) => Task.FromResult(true);
        public void SetSequence(AnimationSequence sequence) { }
    }

    public class PlaybackInfo;
    {
        public double CurrentTime { get; set; }
        public double PlaybackSpeed { get; set; }
        public bool IsLooping { get; set; }
    }
    #endregion;
}
