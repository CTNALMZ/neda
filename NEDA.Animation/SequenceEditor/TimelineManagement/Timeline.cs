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
    /// Advanced timeline control system with multi-track support, real-time playback,
    /// zoom/pan capabilities, and professional editing features;
    /// </summary>
    public class Timeline : ITimeline, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly IKeyframeDataService _keyframeService;
        private readonly ITimelineRenderer _renderer;
        private readonly object _lockObject = new object();
        private readonly List<TimelineTrack> _tracks;
        private readonly List<TimelineLayer> _layers;
        private readonly Dictionary<string, TimelineMarker> _markers;
        private TimelineViewState _viewState;
        private TimelinePlayback _playback;
        private TimelineSelection _selection;
        private bool _disposed = false;
        private bool _isInitialized = false;
        private double _currentTime = 0.0;
        #endregion;

        #region Public Properties;
        public string TimelineName { get; private set; }
        public Guid TimelineId { get; private set; }
        public TimelineState CurrentState { get; private set; }
        public TimelineSettings Settings { get; private set; }
        public TimelineViewState ViewState => _viewState;
        public TimelinePlayback Playback => _playback;
        public TimelineSelection Selection => _selection;
        public double CurrentTime => _currentTime;
        public double Duration { get; private set; }
        public double FrameRate { get; private set; }
        public int TotalTracks => _tracks.Count;
        public int TotalLayers => _layers.Count;
        public int TotalKeyframes => _tracks.Sum(t => t.KeyframeCount);
        public IReadOnlyList<TimelineTrack> Tracks => _tracks.AsReadOnly();
        public IReadOnlyList<TimelineLayer> Layers => _layers.AsReadOnly();
        public IReadOnlyDictionary<string, TimelineMarker> Markers => _markers;
        public event EventHandler<TimelineEventArgs> TimelineEvent;
        public event EventHandler<TimeChangedEventArgs> TimeChanged;
        public event EventHandler<ViewStateChangedEventArgs> ViewStateChanged;
        public event EventHandler<SelectionChangedEventArgs> SelectionChanged;
        public event EventHandler<TrackCollectionChangedEventArgs> TracksChanged;
        #endregion;

        #region Constructors;
        public Timeline(ILogger logger, IKeyframeDataService keyframeService, ITimelineRenderer renderer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _keyframeService = keyframeService ?? throw new ArgumentNullException(nameof(keyframeService));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

            TimelineId = Guid.NewGuid();
            _tracks = new List<TimelineTrack>();
            _layers = new List<TimelineLayer>();
            _markers = new Dictionary<string, TimelineMarker>();
            _viewState = new TimelineViewState();
            _playback = new TimelinePlayback();
            _selection = new TimelineSelection();

            InitializeTimeline();
        }

        public Timeline(ILogger logger)
            : this(logger, new DefaultKeyframeDataService(), new DefaultTimelineRenderer())
        {
        }
        #endregion;

        #region Initialization;
        private void InitializeTimeline()
        {
            TimelineName = "Main Timeline";
            CurrentState = TimelineState.Stopped;
            Duration = 30.0; // 30 seconds default;
            FrameRate = 30.0; // 30 FPS default;

            Settings = new TimelineSettings;
            {
                SnapToFrames = true,
                ShowGrid = true,
                GridSpacing = 1.0,
                ZoomLevel = 1.0,
                TimeDisplayFormat = TimeDisplayFormat.Timecode,
                ShowWaveform = true,
                ShowKeyframes = true,
                AutoScroll = true,
                LoopPlayback = false,
                PlaybackSpeed = 1.0;
            };

            _playback.StateChanged += OnPlaybackStateChanged;
            _selection.SelectionChanged += OnSelectionChanged;

            _isInitialized = true;
            _logger.LogInformation($"Timeline initialized: {TimelineName} (ID: {TimelineId})");
        }

        public async Task InitializeAsync(double duration, double frameRate)
        {
            ValidateNotDisposed();

            if (_isInitialized)
            {
                await ResetAsync();
            }

            try
            {
                CurrentState = TimelineState.Initializing;
                Duration = duration;
                FrameRate = frameRate;

                // Initialize default tracks;
                await CreateDefaultTracksAsync();

                CurrentState = TimelineState.Ready;
                _isInitialized = true;

                _logger.LogInformation($"Timeline initialized with duration: {duration}s, frame rate: {frameRate}fps");

                OnTimelineEvent(new TimelineEventArgs;
                {
                    EventType = TimelineEventType.Initialized,
                    TimelineId = TimelineId,
                    Message = "Timeline initialized successfully"
                });
            }
            catch (Exception ex)
            {
                CurrentState = TimelineState.Error;
                _logger.LogError(ex, "Failed to initialize timeline");
                throw new TimelineException("Timeline initialization failed", ex);
            }
        }
        #endregion;

        #region Track Management;
        public TimelineTrack AddTrack(string trackName, TrackType trackType, int insertIndex = -1)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTrackName(trackName);

            try
            {
                var track = new TimelineTrack;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = trackName,
                    Type = trackType,
                    CreatedAt = DateTime.UtcNow,
                    IsVisible = true,
                    IsLocked = false,
                    Height = GetDefaultTrackHeight(trackType)
                };

                lock (_lockObject)
                {
                    if (insertIndex >= 0 && insertIndex <= _tracks.Count)
                    {
                        _tracks.Insert(insertIndex, track);
                    }
                    else;
                    {
                        _tracks.Add(track);
                    }
                }

                _logger.LogDebug($"Track added: {trackName} (Type: {trackType})");

                OnTracksChanged(new TrackCollectionChangedEventArgs;
                {
                    Action = TrackCollectionAction.Add,
                    Track = track,
                    TotalTracks = _tracks.Count;
                });

                return track;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add track: {trackName}");
                throw new TimelineException($"Track addition failed: {ex.Message}", ex);
            }
        }

        public bool RemoveTrack(string trackId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTrackId(trackId);

            try
            {
                TimelineTrack removedTrack = null;

                lock (_lockObject)
                {
                    var track = _tracks.FirstOrDefault(t => t.Id == trackId);
                    if (track != null)
                    {
                        removedTrack = track;
                        _tracks.Remove(track);

                        // Remove track from selection;
                        _selection.DeselectTrack(track);
                    }
                }

                if (removedTrack != null)
                {
                    _logger.LogDebug($"Track removed: {removedTrack.Name}");

                    OnTracksChanged(new TrackCollectionChangedEventArgs;
                    {
                        Action = TrackCollectionAction.Remove,
                        Track = removedTrack,
                        TotalTracks = _tracks.Count;
                    });

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove track: {trackId}");
                throw new TimelineException($"Track removal failed: {ex.Message}", ex);
            }
        }

        public TimelineTrack GetTrack(string trackId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTrackId(trackId);

            lock (_lockObject)
            {
                return _tracks.FirstOrDefault(t => t.Id == trackId);
            }
        }

        public IReadOnlyList<TimelineTrack> GetTracksByType(TrackType trackType)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_lockObject)
            {
                return _tracks.Where(t => t.Type == trackType).ToList().AsReadOnly();
            }
        }

        public bool MoveTrack(string trackId, int newIndex)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTrackId(trackId);

            if (newIndex < 0 || newIndex >= _tracks.Count)
            {
                throw new ArgumentException("Invalid track index", nameof(newIndex));
            }

            try
            {
                lock (_lockObject)
                {
                    var track = _tracks.FirstOrDefault(t => t.Id == trackId);
                    if (track == null)
                    {
                        return false;
                    }

                    var currentIndex = _tracks.IndexOf(track);
                    if (currentIndex == newIndex)
                    {
                        return true; // Already at desired position;
                    }

                    _tracks.RemoveAt(currentIndex);
                    _tracks.Insert(newIndex, track);

                    _logger.LogDebug($"Track moved: {track.Name} from index {currentIndex} to {newIndex}");

                    OnTracksChanged(new TrackCollectionChangedEventArgs;
                    {
                        Action = TrackCollectionAction.Move,
                        Track = track,
                        TotalTracks = _tracks.Count;
                    });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to move track: {trackId}");
                throw new TimelineException($"Track move failed: {ex.Message}", ex);
            }
        }

        public void SetTrackVisibility(string trackId, bool isVisible)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTrackId(trackId);

            try
            {
                var track = GetTrack(trackId);
                if (track != null && track.IsVisible != isVisible)
                {
                    track.IsVisible = isVisible;

                    _logger.LogDebug($"Track visibility changed: {track.Name} -> {isVisible}");

                    OnTimelineEvent(new TimelineEventArgs;
                    {
                        EventType = TimelineEventType.TrackVisibilityChanged,
                        TimelineId = TimelineId,
                        Track = track,
                        Message = $"Track '{track.Name}' visibility set to {isVisible}"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set track visibility: {trackId}");
                throw new TimelineException($"Track visibility change failed: {ex.Message}", ex);
            }
        }

        public void SetTrackLocked(string trackId, bool isLocked)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTrackId(trackId);

            try
            {
                var track = GetTrack(trackId);
                if (track != null && track.IsLocked != isLocked)
                {
                    track.IsLocked = isLocked;

                    _logger.LogDebug($"Track lock state changed: {track.Name} -> {isLocked}");

                    OnTimelineEvent(new TimelineEventArgs;
                    {
                        EventType = TimelineEventType.TrackLockChanged,
                        TimelineId = TimelineId,
                        Track = track,
                        Message = $"Track '{track.Name}' lock set to {isLocked}"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set track locked: {trackId}");
                throw new TimelineException($"Track lock change failed: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Time Management;
        public void SetCurrentTime(double time, bool snapToFrames = true)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTimeValue(time);

            try
            {
                var newTime = time;

                if (snapToFrames && Settings.SnapToFrames)
                {
                    newTime = SnapToFrame(time);
                }

                if (Math.Abs(_currentTime - newTime) > double.Epsilon)
                {
                    var oldTime = _currentTime;
                    _currentTime = Math.Max(0, Math.Min(newTime, Duration));

                    _logger.LogDebug($"Time changed: {oldTime} -> {_currentTime}");

                    OnTimeChanged(new TimeChangedEventArgs;
                    {
                        OldTime = oldTime,
                        NewTime = _currentTime,
                        TimelineId = TimelineId;
                    });

                    // Auto-scroll if enabled;
                    if (Settings.AutoScroll && _playback.IsPlaying)
                    {
                        ScrollToTime(_currentTime);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set current time: {time}");
                throw new TimelineException($"Time change failed: {ex.Message}", ex);
            }
        }

        public void SetDuration(double duration)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateDuration(duration);

            try
            {
                var oldDuration = Duration;
                Duration = duration;

                // Ensure current time is within new duration;
                if (_currentTime > Duration)
                {
                    SetCurrentTime(Duration);
                }

                _logger.LogDebug($"Duration changed: {oldDuration} -> {Duration}");

                OnTimelineEvent(new TimelineEventArgs;
                {
                    EventType = TimelineEventType.DurationChanged,
                    TimelineId = TimelineId,
                    Message = $"Duration changed from {oldDuration} to {Duration}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set duration: {duration}");
                throw new TimelineException($"Duration change failed: {ex.Message}", ex);
            }
        }

        public void SetFrameRate(double frameRate)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateFrameRate(frameRate);

            try
            {
                var oldFrameRate = FrameRate;
                FrameRate = frameRate;

                _logger.LogDebug($"Frame rate changed: {oldFrameRate} -> {FrameRate}");

                OnTimelineEvent(new TimelineEventArgs;
                {
                    EventType = TimelineEventType.FrameRateChanged,
                    TimelineId = TimelineId,
                    Message = $"Frame rate changed from {oldFrameRate} to {FrameRate}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set frame rate: {frameRate}");
                throw new TimelineException($"Frame rate change failed: {ex.Message}", ex);
            }
        }

        public double SnapToFrame(double time)
        {
            var frameDuration = 1.0 / FrameRate;
            return Math.Round(time / frameDuration) * frameDuration;
        }

        public double TimeToPixels(double time)
        {
            return time * _viewState.PixelsPerSecond * Settings.ZoomLevel;
        }

        public double PixelsToTime(double pixels)
        {
            return pixels / (_viewState.PixelsPerSecond * Settings.ZoomLevel);
        }

        public int TimeToFrame(double time)
        {
            return (int)Math.Round(time * FrameRate);
        }

        public double FrameToTime(int frame)
        {
            return frame / FrameRate;
        }
        #endregion;

        #region View Management;
        public void Zoom(double zoomLevel, double zoomCenter = 0.5)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateZoomLevel(zoomLevel);

            try
            {
                var oldZoom = Settings.ZoomLevel;
                Settings.ZoomLevel = Math.Max(0.1, Math.Min(zoomLevel, 100.0));

                // Adjust view position to maintain zoom center;
                var centerTime = _viewState.StartTime + (_viewState.VisibleDuration * zoomCenter);
                var visibleDuration = _viewState.VisibleDuration * (oldZoom / Settings.ZoomLevel);
                _viewState.StartTime = centerTime - (visibleDuration * zoomCenter);
                _viewState.VisibleDuration = visibleDuration;

                _logger.LogDebug($"Zoom level changed: {oldZoom} -> {Settings.ZoomLevel}");

                OnViewStateChanged(new ViewStateChangedEventArgs;
                {
                    OldViewState = new TimelineViewState { ZoomLevel = oldZoom },
                    NewViewState = _viewState,
                    TimelineId = TimelineId;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to zoom: {zoomLevel}");
                throw new TimelineException($"Zoom operation failed: {ex.Message}", ex);
            }
        }

        public void ZoomToFit()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                var oldZoom = Settings.ZoomLevel;
                var oldStartTime = _viewState.StartTime;

                // Calculate zoom to fit entire duration in view;
                var requiredZoom = _viewState.ViewportWidth / (Duration * _viewState.PixelsPerSecond);
                Settings.ZoomLevel = Math.Max(0.1, Math.Min(requiredZoom, 100.0));

                _viewState.StartTime = 0;
                _viewState.VisibleDuration = Duration;

                _logger.LogDebug("Zoom to fit executed");

                OnViewStateChanged(new ViewStateChangedEventArgs;
                {
                    OldViewState = new TimelineViewState { ZoomLevel = oldZoom, StartTime = oldStartTime },
                    NewViewState = _viewState,
                    TimelineId = TimelineId;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to zoom to fit");
                throw new TimelineException($"Zoom to fit failed: {ex.Message}", ex);
            }
        }

        public void Pan(double timeDelta)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                var oldStartTime = _viewState.StartTime;
                _viewState.StartTime = Math.Max(0, _viewState.StartTime + timeDelta);

                if (Math.Abs(oldStartTime - _viewState.StartTime) > double.Epsilon)
                {
                    _logger.LogDebug($"Timeline panned: {oldStartTime} -> {_viewState.StartTime}");

                    OnViewStateChanged(new ViewStateChangedEventArgs;
                    {
                        OldViewState = new TimelineViewState { StartTime = oldStartTime },
                        NewViewState = _viewState,
                        TimelineId = TimelineId;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to pan: {timeDelta}");
                throw new TimelineException($"Pan operation failed: {ex.Message}", ex);
            }
        }

        public void ScrollToTime(double time)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTimeValue(time);

            try
            {
                var oldStartTime = _viewState.StartTime;

                if (time < _viewState.StartTime || time > _viewState.StartTime + _viewState.VisibleDuration)
                {
                    _viewState.StartTime = Math.Max(0, time - (_viewState.VisibleDuration * 0.1));

                    _logger.LogDebug($"Scrolled to time: {time}");

                    OnViewStateChanged(new ViewStateChangedEventArgs;
                    {
                        OldViewState = new TimelineViewState { StartTime = oldStartTime },
                        NewViewState = _viewState,
                        TimelineId = TimelineId;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scroll to time: {time}");
                throw new TimelineException($"Scroll to time failed: {ex.Message}", ex);
            }
        }

        public void SetViewportSize(double width, double height)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Viewport dimensions must be positive");
            }

            try
            {
                var oldViewState = _viewState.Clone();
                _viewState.ViewportWidth = width;
                _viewState.ViewportHeight = height;
                _viewState.VisibleDuration = width / (_viewState.PixelsPerSecond * Settings.ZoomLevel);

                OnViewStateChanged(new ViewStateChangedEventArgs;
                {
                    OldViewState = oldViewState,
                    NewViewState = _viewState,
                    TimelineId = TimelineId;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set viewport size: {width}x{height}");
                throw new TimelineException($"Viewport size change failed: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Marker Management;
        public TimelineMarker AddMarker(string name, double time, MarkerType type, string description = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateMarkerName(name);
            ValidateTimeValue(time);

            try
            {
                var marker = new TimelineMarker;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    Time = time,
                    Type = type,
                    Description = description,
                    Color = GetDefaultMarkerColor(type),
                    CreatedAt = DateTime.UtcNow;
                };

                lock (_lockObject)
                {
                    _markers[marker.Id] = marker;
                }

                _logger.LogDebug($"Marker added: {name} at time {time}");

                OnTimelineEvent(new TimelineEventArgs;
                {
                    EventType = TimelineEventType.MarkerAdded,
                    TimelineId = TimelineId,
                    Marker = marker,
                    Message = $"Marker '{name}' added at {time}"
                });

                return marker;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add marker: {name}");
                throw new TimelineException($"Marker addition failed: {ex.Message}", ex);
            }
        }

        public bool RemoveMarker(string markerId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateMarkerId(markerId);

            try
            {
                TimelineMarker removedMarker = null;

                lock (_lockObject)
                {
                    if (_markers.TryGetValue(markerId, out var marker))
                    {
                        removedMarker = marker;
                        _markers.Remove(markerId);
                    }
                }

                if (removedMarker != null)
                {
                    _logger.LogDebug($"Marker removed: {removedMarker.Name}");

                    OnTimelineEvent(new TimelineEventArgs;
                    {
                        EventType = TimelineEventType.MarkerRemoved,
                        TimelineId = TimelineId,
                        Marker = removedMarker,
                        Message = $"Marker '{removedMarker.Name}' removed"
                    });

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove marker: {markerId}");
                throw new TimelineException($"Marker removal failed: {ex.Message}", ex);
            }
        }

        public IReadOnlyList<TimelineMarker> GetMarkersInRange(double startTime, double endTime)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateTimeRange(startTime, endTime);

            lock (_lockObject)
            {
                return _markers.Values;
                    .Where(m => m.Time >= startTime && m.Time <= endTime)
                    .OrderBy(m => m.Time)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public void GoToMarker(string markerId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateMarkerId(markerId);

            try
            {
                if (_markers.TryGetValue(markerId, out var marker))
                {
                    SetCurrentTime(marker.Time);
                    _logger.LogDebug($"Jumped to marker: {marker.Name}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to go to marker: {markerId}");
                throw new TimelineException($"Go to marker failed: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Playback Control;
        public async Task PlayAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                await _playback.PlayAsync();
                CurrentState = TimelineState.Playing;

                _logger.LogDebug("Playback started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start playback");
                throw new TimelineException($"Playback start failed: {ex.Message}", ex);
            }
        }

        public async Task PauseAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                await _playback.PauseAsync();
                CurrentState = TimelineState.Paused;

                _logger.LogDebug("Playback paused");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause playback");
                throw new TimelineException($"Playback pause failed: {ex.Message}", ex);
            }
        }

        public async Task StopAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                await _playback.StopAsync();
                CurrentState = TimelineState.Stopped;
                SetCurrentTime(0);

                _logger.LogDebug("Playback stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop playback");
                throw new TimelineException($"Playback stop failed: {ex.Message}", ex);
            }
        }

        public void SetPlaybackSpeed(double speed)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidatePlaybackSpeed(speed);

            try
            {
                _playback.SetSpeed(speed);
                Settings.PlaybackSpeed = speed;

                _logger.LogDebug($"Playback speed set to: {speed}x");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set playback speed: {speed}");
                throw new TimelineException($"Playback speed change failed: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Utility Methods;
        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Timeline));
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new TimelineException("Timeline not initialized");
        }

        private void ValidateTrackName(string trackName)
        {
            if (string.IsNullOrWhiteSpace(trackName))
                throw new ArgumentException("Track name cannot be null or empty", nameof(trackName));
        }

        private void ValidateTrackId(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                throw new ArgumentException("Track ID cannot be null or empty", nameof(trackId));
        }

        private void ValidateTimeValue(double time)
        {
            if (time < 0)
                throw new ArgumentException("Time cannot be negative", nameof(time));
        }

        private void ValidateDuration(double duration)
        {
            if (duration <= 0)
                throw new ArgumentException("Duration must be positive", nameof(duration));
        }

        private void ValidateFrameRate(double frameRate)
        {
            if (frameRate <= 0)
                throw new ArgumentException("Frame rate must be positive", nameof(frameRate));
        }

        private void ValidateZoomLevel(double zoomLevel)
        {
            if (zoomLevel <= 0)
                throw new ArgumentException("Zoom level must be positive", nameof(zoomLevel));
        }

        private void ValidateTimeRange(double startTime, double endTime)
        {
            if (startTime < 0 || endTime < 0)
                throw new ArgumentException("Time values cannot be negative");

            if (startTime > endTime)
                throw new ArgumentException("Start time cannot be greater than end time");
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

        private void ValidatePlaybackSpeed(double speed)
        {
            if (speed <= 0)
                throw new ArgumentException("Playback speed must be positive", nameof(speed));
        }

        private double GetDefaultTrackHeight(TrackType trackType)
        {
            return trackType switch;
            {
                TrackType.Animation => 80.0,
                TrackType.Audio => 60.0,
                TrackType.Effect => 40.0,
                TrackType.Camera => 100.0,
                _ => 60.0;
            };
        }

        private string GetDefaultMarkerColor(MarkerType markerType)
        {
            return markerType switch;
            {
                MarkerType.Annotation => "#FFFF00", // Yellow;
                MarkerType.Bookmark => "#00FF00",   // Green;
                MarkerType.Important => "#FF0000",  // Red;
                MarkerType.Warning => "#FFA500",    // Orange;
                _ => "#FFFFFF"                      // White;
            };
        }

        private async Task CreateDefaultTracksAsync()
        {
            // Create default tracks for common animation workflows;
            var defaultTracks = new[]
            {
                new { Name = "Camera", Type = TrackType.Camera },
                new { Name = "Character", Type = TrackType.Animation },
                new { Name = "Audio", Type = TrackType.Audio },
                new { Name = "Effects", Type = TrackType.Effect }
            };

            foreach (var trackInfo in defaultTracks)
            {
                AddTrack(trackInfo.Name, trackInfo.Type);
            }

            await Task.CompletedTask;
        }

        private async Task ResetAsync()
        {
            lock (_lockObject)
            {
                _tracks.Clear();
                _layers.Clear();
                _markers.Clear();
                _selection.Clear();
                _currentTime = 0;
            }

            await Task.CompletedTask;
        }
        #endregion;

        #region Event Handlers;
        private void OnPlaybackStateChanged(object sender, PlaybackStateChangedEventArgs e)
        {
            // Forward playback state changes;
            TimelineEvent?.Invoke(this, new TimelineEventArgs;
            {
                EventType = TimelineEventType.PlaybackStateChanged,
                TimelineId = TimelineId,
                Message = $"Playback state changed: {e.NewState}"
            });
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectionChanged?.Invoke(this, e);
        }

        protected virtual void OnTimelineEvent(TimelineEventArgs e)
        {
            TimelineEvent?.Invoke(this, e);
        }

        protected virtual void OnTimeChanged(TimeChangedEventArgs e)
        {
            TimeChanged?.Invoke(this, e);
        }

        protected virtual void OnViewStateChanged(ViewStateChangedEventArgs e)
        {
            ViewStateChanged?.Invoke(this, e);
        }

        protected virtual void OnTracksChanged(TrackCollectionChangedEventArgs e)
        {
            TracksChanged?.Invoke(this, e);
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
                    if (_playback.IsPlaying)
                    {
                        StopAsync().Wait(5000);
                    }

                    // Clean up event handlers;
                    _playback.StateChanged -= OnPlaybackStateChanged;
                    _selection.SelectionChanged -= OnSelectionChanged;

                    // Dispose resources;
                    if (_playback is IDisposable disposablePlayback)
                    {
                        disposablePlayback.Dispose();
                    }

                    if (_renderer is IDisposable disposableRenderer)
                    {
                        disposableRenderer.Dispose();
                    }

                    if (_keyframeService is IDisposable disposableKeyframe)
                    {
                        disposableKeyframe.Dispose();
                    }

                    _tracks.Clear();
                    _layers.Clear();
                    _markers.Clear();

                    _logger.LogInformation("Timeline disposed");
                }

                _disposed = true;
            }
        }

        ~Timeline()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes;
    public class TimelineSettings;
    {
        public bool SnapToFrames { get; set; }
        public bool ShowGrid { get; set; }
        public double GridSpacing { get; set; }
        public double ZoomLevel { get; set; }
        public TimeDisplayFormat TimeDisplayFormat { get; set; }
        public bool ShowWaveform { get; set; }
        public bool ShowKeyframes { get; set; }
        public bool AutoScroll { get; set; }
        public bool LoopPlayback { get; set; }
        public double PlaybackSpeed { get; set; }
    }

    public class TimelineViewState;
    {
        public double StartTime { get; set; }
        public double VisibleDuration { get; set; }
        public double ViewportWidth { get; set; }
        public double ViewportHeight { get; set; }
        public double PixelsPerSecond { get; set; } = 100.0; // Default value;
        public double ZoomLevel { get; set; } = 1.0;

        public TimelineViewState Clone()
        {
            return new TimelineViewState;
            {
                StartTime = StartTime,
                VisibleDuration = VisibleDuration,
                ViewportWidth = ViewportWidth,
                ViewportHeight = ViewportHeight,
                PixelsPerSecond = PixelsPerSecond,
                ZoomLevel = ZoomLevel;
            };
        }
    }

    public class TimelinePlayback;
    {
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public double Speed { get; private set; } = 1.0;
        public event EventHandler<PlaybackStateChangedEventArgs> StateChanged;

        public Task PlayAsync()
        {
            IsPlaying = true;
            IsPaused = false;
            StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs;
            {
                NewState = PlaybackState.Playing;
            });
            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            IsPaused = true;
            StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs;
            {
                NewState = PlaybackState.Paused;
            });
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsPlaying = false;
            IsPaused = false;
            StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs;
            {
                NewState = PlaybackState.Stopped;
            });
            return Task.CompletedTask;
        }

        public void SetSpeed(double speed)
        {
            Speed = speed;
        }
    }

    public class TimelineSelection;
    {
        private readonly HashSet<object> _selectedItems;

        public int Count => _selectedItems.Count;
        public bool HasSelection => _selectedItems.Count > 0;
        public event EventHandler<SelectionChangedEventArgs> SelectionChanged;

        public TimelineSelection()
        {
            _selectedItems = new HashSet<object>();
        }

        public void Select(object item)
        {
            if (_selectedItems.Add(item))
            {
                SelectionChanged?.Invoke(this, new SelectionChangedEventArgs;
                {
                    Action = SelectionAction.Add,
                    SelectedItems = new[] { item }
                });
            }
        }

        public void Deselect(object item)
        {
            if (_selectedItems.Remove(item))
            {
                SelectionChanged?.Invoke(this, new SelectionChangedEventArgs;
                {
                    Action = SelectionAction.Remove,
                    SelectedItems = new[] { item }
                });
            }
        }

        public void DeselectTrack(TimelineTrack track)
        {
            // Remove all keyframes from this track from selection;
            var itemsToRemove = _selectedItems.OfType<Keyframe>()
                .Where(k => k.TrackId == track.Id)
                .Cast<object>()
                .ToList();

            foreach (var item in itemsToRemove)
            {
                Deselect(item);
            }
        }

        public void Clear()
        {
            if (_selectedItems.Count > 0)
            {
                var oldItems = _selectedItems.ToList();
                _selectedItems.Clear();
                SelectionChanged?.Invoke(this, new SelectionChangedEventArgs;
                {
                    Action = SelectionAction.Clear,
                    PreviousItems = oldItems;
                });
            }
        }

        public bool IsSelected(object item) => _selectedItems.Contains(item);
    }
    #endregion;
}
