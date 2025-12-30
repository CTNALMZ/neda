using Microsoft.Extensions.Logging;
using NEDA.Animation.SequenceEditor.TimelineManagement;
using NEDA.API.Middleware;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NEDA.Animation.SequenceEditor.TimelineManagement;
{
    /// <summary>
    /// Timeline kontrolünü yöneten ana sınıf;
    /// Animasyon, video ve ses timeline'ları için merkezi kontrol sağlar;
    /// </summary>
    public interface ITimelineController : IDisposable
    {
        /// <summary>
        /// Timeline ID;
        /// </summary>
        Guid TimelineId { get; }

        /// <summary>
        /// Timeline adı;
        /// </summary>
        string TimelineName { get; set; }

        /// <summary>
        /// Timeline uzunluğu (saniye)
        /// </summary>
        double Duration { get; set; }

        /// <summary>
        /// Geçerli zaman pozisyonu (saniye)
        /// </summary>
        double CurrentTime { get; }

        /// <summary>
        /// Timeline'ın oynatma durumu;
        /// </summary>
        TimelinePlayState PlayState { get; }

        /// <summary>
        /// Timeline kayıt modunda mı?
        /// </summary>
        bool IsRecording { get; }

        /// <summary>
        /// Timeline'daki tüm track'lar;
        /// </summary>
        IReadOnlyList<ITimelineTrack> Tracks { get; }

        /// <summary>
        /// Timeline'daki tüm marker'lar;
        /// </summary>
        IReadOnlyList<ITimelineMarker> Markers { get; }

        /// <summary>
        /// Timeline'ı başlat;
        /// </summary>
        Task<bool> InitializeAsync(TimelineConfiguration configuration);

        /// <summary>
        /// Timeline'ı oynat;
        /// </summary>
        Task PlayAsync();

        /// <summary>
        /// Timeline'ı duraklat;
        /// </summary>
        Task PauseAsync();

        /// <summary>
        /// Timeline'ı durdur ve başa sar;
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Belirli bir zamana atla;
        /// </summary>
        Task SeekAsync(double timeInSeconds);

        /// <summary>
        /// Kayıt modunu başlat/durdur;
        /// </summary>
        Task ToggleRecordingAsync();

        /// <summary>
        /// Timeline'a yeni track ekle;
        /// </summary>
        Task<ITimelineTrack> AddTrackAsync(TrackType trackType, string trackName);

        /// <summary>
        /// Track'ı timeline'dan kaldır;
        /// </summary>
        Task<bool> RemoveTrackAsync(Guid trackId);

        /// <summary>
        /// Timeline'a marker ekle;
        /// </summary>
        Task<ITimelineMarker> AddMarkerAsync(double timeInSeconds, string markerName, MarkerType type);

        /// <summary>
        /// Timeline'dan marker kaldır;
        /// </summary>
        Task<bool> RemoveMarkerAsync(Guid markerId);

        /// <summary>
        /// Timeline'ı kaydet;
        /// </summary>
        Task<bool> SaveTimelineAsync(string filePath);

        /// <summary>
        /// Timeline'ı yükle;
        /// </summary>
        Task<bool> LoadTimelineAsync(string filePath);

        /// <summary>
        /// Timeline'ı export et;
        /// </summary>
        Task<bool> ExportTimelineAsync(ExportFormat format, string outputPath);

        /// <summary>
        /// Timeline playback hızını ayarla;
        /// </summary>
        Task SetPlaybackSpeedAsync(double speed);

        /// <summary>
        /// Timeline loop modunu ayarla;
        /// </summary>
        Task SetLoopModeAsync(LoopMode loopMode);

        /// <summary>
        /// Timeline olayına abone ol;
        /// </summary>
        void SubscribeToTimelineEvent(TimelineEventType eventType, Action<TimelineEventArgs> handler);

        /// <summary>
        /// Timeline olayından abonelikten çık;
        /// </summary>
        void UnsubscribeFromTimelineEvent(TimelineEventType eventType, Action<TimelineEventArgs> handler);
    }

    /// <summary>
    /// Timeline track interface;
    /// </summary>
    public interface ITimelineTrack;
    {
        Guid TrackId { get; }
        string TrackName { get; set; }
        TrackType Type { get; }
        bool IsMuted { get; set; }
        bool IsLocked { get; set; }
        double Volume { get; set; }
        IReadOnlyList<ITimelineClip> Clips { get; }

        Task<ITimelineClip> AddClipAsync(ClipType clipType, double startTime, double duration, object clipData);
        Task<bool> RemoveClipAsync(Guid clipId);
        Task<bool> MoveClipAsync(Guid clipId, double newStartTime);
        Task<bool> TrimClipAsync(Guid clipId, double newStartTime, double newDuration);
    }

    /// <summary>
    /// Timeline clip interface;
    /// </summary>
    public interface ITimelineClip;
    {
        Guid ClipId { get; }
        ClipType Type { get; }
        double StartTime { get; }
        double Duration { get; }
        double EndTime { get; }
        bool IsSelected { get; set; }
        object ClipData { get; }

        Task<bool> UpdateClipDataAsync(object newData);
        Task<bool> SplitClipAsync(double splitTime);
    }

    /// <summary>
    /// Timeline marker interface;
    /// </summary>
    public interface ITimelineMarker;
    {
        Guid MarkerId { get; }
        string MarkerName { get; set; }
        double Time { get; }
        MarkerType Type { get; }
        string Notes { get; set; }
    }

    /// <summary>
    /// TimelineController implementasyonu;
    /// </summary>
    public class TimelineController : ITimelineController;
    {
        private readonly ILogger<TimelineController> _logger;
        private readonly IEventBus _eventBus;
        private readonly IErrorReporter _errorReporter;
        private readonly object _syncLock = new object();

        private Guid _timelineId;
        private string _timelineName;
        private double _duration;
        private double _currentTime;
        private TimelinePlayState _playState;
        private bool _isRecording;
        private double _playbackSpeed = 1.0;
        private LoopMode _loopMode = LoopMode.None;
        private TimelineConfiguration _configuration;

        private readonly List<ITimelineTrack> _tracks = new List<ITimelineTrack>();
        private readonly List<ITimelineMarker> _markers = new List<ITimelineMarker>();
        private readonly Dictionary<TimelineEventType, List<Action<TimelineEventArgs>>> _eventHandlers =
            new Dictionary<TimelineEventType, List<Action<TimelineEventArgs>>>();

        private Task _playbackTask;
        private CancellationTokenSource _playbackCancellationTokenSource;
        private bool _isDisposed;

        /// <summary>
        /// TimelineController constructor;
        /// </summary>
        public TimelineController(
            ILogger<TimelineController> logger,
            IEventBus eventBus,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            _timelineId = Guid.NewGuid();
            _timelineName = $"Timeline_{_timelineId.ToString("N").Substring(0, 8)}";
            _duration = 60.0; // Default 60 seconds;
            _currentTime = 0.0;
            _playState = TimelinePlayState.Stopped;
            _isRecording = false;

            _logger.LogInformation("TimelineController initialized with ID: {TimelineId}", _timelineId);
        }

        public Guid TimelineId => _timelineId;

        public string TimelineName;
        {
            get => _timelineName;
            set;
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Timeline name cannot be null or empty", nameof(value));

                lock (_syncLock)
                {
                    var oldName = _timelineName;
                    _timelineName = value;
                    _logger.LogDebug("Timeline name changed from '{OldName}' to '{NewName}'", oldName, value);

                    RaiseTimelineEvent(TimelineEventType.TimelineRenamed,
                        new TimelineEventArgs;
                        {
                            TimelineId = _timelineId,
                            OldName = oldName,
                            NewName = value;
                        });
                }
            }
        }

        public double Duration;
        {
            get => _duration;
            set;
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Duration must be greater than zero");

                lock (_syncLock)
                {
                    var oldDuration = _duration;
                    _duration = value;

                    // Eğer current time yeni duration'dan büyükse, sona ayarla;
                    if (_currentTime > _duration)
                    {
                        _currentTime = _duration;
                    }

                    _logger.LogDebug("Timeline duration changed from {OldDuration} to {NewDuration}",
                        oldDuration, value);

                    RaiseTimelineEvent(TimelineEventType.DurationChanged,
                        new TimelineEventArgs;
                        {
                            TimelineId = _timelineId,
                            OldDuration = oldDuration,
                            NewDuration = value;
                        });
                }
            }
        }

        public double CurrentTime => _currentTime;

        public TimelinePlayState PlayState => _playState;

        public bool IsRecording => _isRecording;

        public IReadOnlyList<ITimelineTrack> Tracks;
        {
            get;
            {
                lock (_syncLock)
                {
                    return _tracks.AsReadOnly();
                }
            }
        }

        public IReadOnlyList<ITimelineMarker> Markers;
        {
            get;
            {
                lock (_syncLock)
                {
                    return _markers.AsReadOnly();
                }
            }
        }

        public async Task<bool> InitializeAsync(TimelineConfiguration configuration)
        {
            try
            {
                _logger.LogInformation("Initializing timeline with configuration");

                if (configuration == null)
                    throw new ArgumentNullException(nameof(configuration));

                _configuration = configuration;

                // Timeline özelliklerini ayarla;
                _timelineName = configuration.TimelineName;
                _duration = configuration.Duration;

                // Varsayılan track'ları oluştur;
                if (configuration.CreateDefaultTracks)
                {
                    await CreateDefaultTracksAsync();
                }

                // Event bus'a kayıt ol;
                await _eventBus.SubscribeAsync<TimelineEvent>(HandleTimelineEvent);

                _logger.LogInformation("Timeline initialized successfully. Duration: {Duration}s, Tracks: {TrackCount}",
                    _duration, _tracks.Count);

                RaiseTimelineEvent(TimelineEventType.TimelineInitialized,
                    new TimelineEventArgs { TimelineId = _timelineId });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize timeline");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TimelineInitializationFailed);
                throw new TimelineException("Failed to initialize timeline", ex, ErrorCodes.TimelineInitializationFailed);
            }
        }

        public async Task PlayAsync()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_playState == TimelinePlayState.Playing)
                    {
                        _logger.LogWarning("Timeline is already playing");
                        return;
                    }

                    if (_isRecording)
                    {
                        _logger.LogWarning("Cannot play while recording");
                        throw new TimelineException("Cannot play while recording", ErrorCodes.TimelineInvalidOperation);
                    }

                    _playState = TimelinePlayState.Playing;
                    _playbackCancellationTokenSource = new CancellationTokenSource();
                }

                _logger.LogDebug("Starting timeline playback from {CurrentTime}s", _currentTime);

                // Playback task'ını başlat;
                _playbackTask = Task.Run(() => PlaybackLoopAsync(_playbackCancellationTokenSource.Token));

                RaiseTimelineEvent(TimelineEventType.PlaybackStarted,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        CurrentTime = _currentTime;
                    });

                await _eventBus.PublishAsync(new TimelineEvent;
                {
                    EventType = TimelineEventType.PlaybackStarted.ToString(),
                    TimelineId = _timelineId,
                    Timestamp = DateTime.UtcNow,
                    Data = new { CurrentTime = _currentTime }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start playback");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.PlaybackFailed);
                throw new TimelineException("Failed to start playback", ex, ErrorCodes.PlaybackFailed);
            }
        }

        public async Task PauseAsync()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_playState != TimelinePlayState.Playing)
                    {
                        _logger.LogWarning("Timeline is not playing");
                        return;
                    }

                    _playState = TimelinePlayState.Paused;

                    // Playback task'ını durdur;
                    _playbackCancellationTokenSource?.Cancel();
                }

                _logger.LogDebug("Timeline paused at {CurrentTime}s", _currentTime);

                // Task'ın tamamlanmasını bekle;
                if (_playbackTask != null && !_playbackTask.IsCompleted)
                {
                    await _playbackTask;
                }

                RaiseTimelineEvent(TimelineEventType.PlaybackPaused,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        CurrentTime = _currentTime;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause timeline");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.PlaybackFailed);
                throw new TimelineException("Failed to pause timeline", ex, ErrorCodes.PlaybackFailed);
            }
        }

        public async Task StopAsync()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_playState == TimelinePlayState.Stopped)
                    {
                        return;
                    }

                    var previousState = _playState;
                    _playState = TimelinePlayState.Stopped;
                    _currentTime = 0.0;

                    // Playback task'ını durdur;
                    _playbackCancellationTokenSource?.Cancel();
                }

                _logger.LogDebug("Timeline stopped");

                // Task'ın tamamlanmasını bekle;
                if (_playbackTask != null && !_playbackTask.IsCompleted)
                {
                    await _playbackTask;
                }

                RaiseTimelineEvent(TimelineEventType.PlaybackStopped,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        PreviousState = _playState.ToString(),
                        CurrentTime = _currentTime;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop timeline");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.PlaybackFailed);
                throw new TimelineException("Failed to stop timeline", ex, ErrorCodes.PlaybackFailed);
            }
        }

        public async Task SeekAsync(double timeInSeconds)
        {
            try
            {
                if (timeInSeconds < 0 || timeInSeconds > _duration)
                {
                    throw new ArgumentOutOfRangeException(nameof(timeInSeconds),
                        $"Time must be between 0 and {_duration}");
                }

                lock (_syncLock)
                {
                    var previousTime = _currentTime;
                    _currentTime = timeInSeconds;

                    _logger.LogDebug("Seeking from {PreviousTime}s to {NewTime}s", previousTime, timeInSeconds);

                    // Eğer playing state'teysek ve seek yapıyorsak, playback'ı güncelle;
                    if (_playState == TimelinePlayState.Playing)
                    {
                        // Playback loop'unun yeni zamanı görmesi için event gönder;
                    }
                }

                // Tüm track'lara seek event'ini bildir;
                foreach (var track in _tracks)
                {
                    if (track is TimelineTrack timelineTrack)
                    {
                        await timelineTrack.HandleSeekAsync(_currentTime);
                    }
                }

                RaiseTimelineEvent(TimelineEventType.SeekPerformed,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        PreviousTime = _currentTime,
                        NewTime = timeInSeconds;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seek to {Time}s", timeInSeconds);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.SeekFailed);
                throw new TimelineException("Failed to perform seek", ex, ErrorCodes.SeekFailed);
            }
        }

        public async Task ToggleRecordingAsync()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_playState == TimelinePlayState.Playing)
                    {
                        throw new TimelineException("Cannot toggle recording while playing",
                            ErrorCodes.TimelineInvalidOperation);
                    }

                    _isRecording = !_isRecording;
                }

                _logger.LogInformation("Recording toggled: {IsRecording}", _isRecording);

                RaiseTimelineEvent(_isRecording ? TimelineEventType.RecordingStarted : TimelineEventType.RecordingStopped,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        IsRecording = _isRecording;
                    });

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle recording");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.RecordingFailed);
                throw new TimelineException("Failed to toggle recording", ex, ErrorCodes.RecordingFailed);
            }
        }

        public async Task<ITimelineTrack> AddTrackAsync(TrackType trackType, string trackName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(trackName))
                {
                    throw new ArgumentException("Track name cannot be null or empty", nameof(trackName));
                }

                lock (_syncLock)
                {
                    // Track ismi benzersiz olmalı;
                    if (_tracks.Any(t => t.TrackName.Equals(trackName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new TimelineException($"Track with name '{trackName}' already exists",
                            ErrorCodes.TrackAlreadyExists);
                    }

                    var track = new TimelineTrack(trackType, trackName, _logger);
                    _tracks.Add(track);

                    _logger.LogInformation("Added track: {TrackName} ({TrackType})", trackName, trackType);

                    RaiseTimelineEvent(TimelineEventType.TrackAdded,
                        new TimelineEventArgs;
                        {
                            TimelineId = _timelineId,
                            TrackId = track.TrackId,
                            TrackName = trackName,
                            TrackType = trackType;
                        });

                    return track;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add track: {TrackName}", trackName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TrackOperationFailed);
                throw new TimelineException("Failed to add track", ex, ErrorCodes.TrackOperationFailed);
            }
        }

        public async Task<bool> RemoveTrackAsync(Guid trackId)
        {
            try
            {
                lock (_syncLock)
                {
                    var track = _tracks.FirstOrDefault(t => t.TrackId == trackId);
                    if (track == null)
                    {
                        _logger.LogWarning("Track not found: {TrackId}", trackId);
                        return false;
                    }

                    if (track.IsLocked)
                    {
                        throw new TimelineException("Cannot remove locked track", ErrorCodes.TrackLocked);
                    }

                    _tracks.Remove(track);

                    _logger.LogInformation("Removed track: {TrackName} ({TrackId})", track.TrackName, trackId);

                    RaiseTimelineEvent(TimelineEventType.TrackRemoved,
                        new TimelineEventArgs;
                        {
                            TimelineId = _timelineId,
                            TrackId = trackId,
                            TrackName = track.TrackName;
                        });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove track: {TrackId}", trackId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TrackOperationFailed);
                throw new TimelineException("Failed to remove track", ex, ErrorCodes.TrackOperationFailed);
            }
        }

        public async Task<ITimelineMarker> AddMarkerAsync(double timeInSeconds, string markerName, MarkerType type)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(markerName))
                {
                    throw new ArgumentException("Marker name cannot be null or empty", nameof(markerName));
                }

                if (timeInSeconds < 0 || timeInSeconds > _duration)
                {
                    throw new ArgumentOutOfRangeException(nameof(timeInSeconds),
                        $"Time must be between 0 and {_duration}");
                }

                lock (_syncLock)
                {
                    // Marker ismi benzersiz olmalı;
                    if (_markers.Any(m => m.MarkerName.Equals(markerName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new TimelineException($"Marker with name '{markerName}' already exists",
                            ErrorCodes.MarkerAlreadyExists);
                    }

                    var marker = new TimelineMarker(markerName, timeInSeconds, type);
                    _markers.Add(marker);

                    // Marker'ları zamana göre sırala;
                    _markers = _markers.OrderBy(m => m.Time).ToList();

                    _logger.LogInformation("Added marker: {MarkerName} at {Time}s", markerName, timeInSeconds);

                    RaiseTimelineEvent(TimelineEventType.MarkerAdded,
                        new TimelineEventArgs;
                        {
                            TimelineId = _timelineId,
                            MarkerId = marker.MarkerId,
                            MarkerName = markerName,
                            MarkerTime = timeInSeconds,
                            MarkerType = type;
                        });

                    return marker;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add marker: {MarkerName} at {Time}s", markerName, timeInSeconds);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MarkerOperationFailed);
                throw new TimelineException("Failed to add marker", ex, ErrorCodes.MarkerOperationFailed);
            }
        }

        public async Task<bool> RemoveMarkerAsync(Guid markerId)
        {
            try
            {
                lock (_syncLock)
                {
                    var marker = _markers.FirstOrDefault(m => m.MarkerId == markerId);
                    if (marker == null)
                    {
                        _logger.LogWarning("Marker not found: {MarkerId}", markerId);
                        return false;
                    }

                    _markers.Remove(marker);

                    _logger.LogInformation("Removed marker: {MarkerName} ({MarkerId})", marker.MarkerName, markerId);

                    RaiseTimelineEvent(TimelineEventType.MarkerRemoved,
                        new TimelineEventArgs;
                        {
                            TimelineId = _timelineId,
                            MarkerId = markerId,
                            MarkerName = marker.MarkerName;
                        });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove marker: {MarkerId}", markerId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MarkerOperationFailed);
                throw new TimelineException("Failed to remove marker", ex, ErrorCodes.MarkerOperationFailed);
            }
        }

        public async Task<bool> SaveTimelineAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
                }

                _logger.LogInformation("Saving timeline to: {FilePath}", filePath);

                var timelineData = new TimelineData;
                {
                    TimelineId = _timelineId,
                    TimelineName = _timelineName,
                    Duration = _duration,
                    Configuration = _configuration,
                    Tracks = _tracks.Cast<TimelineTrack>().Select(t => t.GetTrackData()).ToList(),
                    Markers = _markers.Cast<TimelineMarker>().Select(m => m.GetMarkerData()).ToList(),
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow;
                };

                // JSON serialization (gerçek implementasyonda System.Text.Json veya Newtonsoft.Json kullan)
                // Bu örnekte basit bir simülasyon;
                var serializedData = SerializeTimelineData(timelineData);
                await System.IO.File.WriteAllTextAsync(filePath, serializedData);

                _logger.LogInformation("Timeline saved successfully");

                RaiseTimelineEvent(TimelineEventType.TimelineSaved,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        FilePath = filePath;
                    });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save timeline to: {FilePath}", filePath);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TimelineSaveFailed);
                throw new TimelineException("Failed to save timeline", ex, ErrorCodes.TimelineSaveFailed);
            }
        }

        public async Task<bool> LoadTimelineAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
                }

                if (!System.IO.File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Timeline file not found: {filePath}");
                }

                _logger.LogInformation("Loading timeline from: {FilePath}", filePath);

                // Timeline'ı durdur;
                await StopAsync();

                lock (_syncLock)
                {
                    // Mevcut verileri temizle;
                    _tracks.Clear();
                    _markers.Clear();

                    // Dosyayı oku ve deserialize et;
                    var fileContent = System.IO.File.ReadAllText(filePath);
                    var timelineData = DeserializeTimelineData(fileContent);

                    // Timeline özelliklerini yükle;
                    _timelineId = timelineData.TimelineId;
                    _timelineName = timelineData.TimelineName;
                    _duration = timelineData.Duration;
                    _configuration = timelineData.Configuration;

                    // Track'ları yeniden oluştur;
                    foreach (var trackData in timelineData.Tracks)
                    {
                        var track = TimelineTrack.FromTrackData(trackData, _logger);
                        _tracks.Add(track);
                    }

                    // Marker'ları yeniden oluştur;
                    foreach (var markerData in timelineData.Markers)
                    {
                        var marker = TimelineMarker.FromMarkerData(markerData);
                        _markers.Add(marker);
                    }
                }

                _logger.LogInformation("Timeline loaded successfully. Tracks: {TrackCount}, Markers: {MarkerCount}",
                    _tracks.Count, _markers.Count);

                RaiseTimelineEvent(TimelineEventType.TimelineLoaded,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        FilePath = filePath;
                    });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load timeline from: {FilePath}", filePath);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TimelineLoadFailed);
                throw new TimelineException("Failed to load timeline", ex, ErrorCodes.TimelineLoadFailed);
            }
        }

        public async Task<bool> ExportTimelineAsync(ExportFormat format, string outputPath)
        {
            try
            {
                _logger.LogInformation("Exporting timeline to {Format} format", format);

                switch (format)
                {
                    case ExportFormat.JSON:
                        await ExportToJsonAsync(outputPath);
                        break;
                    case ExportFormat.XML:
                        await ExportToXmlAsync(outputPath);
                        break;
                    case ExportFormat.EDL:
                        await ExportToEdlAsync(outputPath);
                        break;
                    case ExportFormat.FCPXML:
                        await ExportToFcpXmlAsync(outputPath);
                        break;
                    default:
                        throw new NotSupportedException($"Export format {format} is not supported");
                }

                _logger.LogInformation("Timeline exported successfully to: {OutputPath}", outputPath);

                RaiseTimelineEvent(TimelineEventType.TimelineExported,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        ExportFormat = format,
                        OutputPath = outputPath;
                    });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export timeline");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TimelineExportFailed);
                throw new TimelineException("Failed to export timeline", ex, ErrorCodes.TimelineExportFailed);
            }
        }

        public async Task SetPlaybackSpeedAsync(double speed)
        {
            try
            {
                if (speed <= 0 || speed > 10) // 10x maksimum hız;
                {
                    throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be between 0.1 and 10.0");
                }

                lock (_syncLock)
                {
                    var oldSpeed = _playbackSpeed;
                    _playbackSpeed = speed;

                    _logger.LogDebug("Playback speed changed from {OldSpeed}x to {NewSpeed}x", oldSpeed, speed);
                }

                RaiseTimelineEvent(TimelineEventType.PlaybackSpeedChanged,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        PlaybackSpeed = speed;
                    });

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set playback speed to {Speed}x", speed);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.PlaybackSpeedChangeFailed);
                throw new TimelineException("Failed to set playback speed", ex, ErrorCodes.PlaybackSpeedChangeFailed);
            }
        }

        public async Task SetLoopModeAsync(LoopMode loopMode)
        {
            try
            {
                lock (_syncLock)
                {
                    var oldMode = _loopMode;
                    _loopMode = loopMode;

                    _logger.LogDebug("Loop mode changed from {OldMode} to {NewMode}", oldMode, loopMode);
                }

                RaiseTimelineEvent(TimelineEventType.LoopModeChanged,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        LoopMode = loopMode;
                    });

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set loop mode to {LoopMode}", loopMode);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.LoopModeChangeFailed);
                throw new TimelineException("Failed to set loop mode", ex, ErrorCodes.LoopModeChangeFailed);
            }
        }

        public void SubscribeToTimelineEvent(TimelineEventType eventType, Action<TimelineEventArgs> handler)
        {
            lock (_syncLock)
            {
                if (!_eventHandlers.ContainsKey(eventType))
                {
                    _eventHandlers[eventType] = new List<Action<TimelineEventArgs>>();
                }

                _eventHandlers[eventType].Add(handler);
                _logger.LogDebug("Subscribed to timeline event: {EventType}", eventType);
            }
        }

        public void UnsubscribeFromTimelineEvent(TimelineEventType eventType, Action<TimelineEventArgs> handler)
        {
            lock (_syncLock)
            {
                if (_eventHandlers.ContainsKey(eventType))
                {
                    _eventHandlers[eventType].Remove(handler);
                    _logger.LogDebug("Unsubscribed from timeline event: {EventType}", eventType);
                }
            }
        }

        private async Task PlaybackLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("Starting playback loop");

                var lastUpdateTime = DateTime.UtcNow;

                while (!cancellationToken.IsCancellationRequested)
                {
                    lock (_syncLock)
                    {
                        if (_playState != TimelinePlayState.Playing)
                        {
                            break;
                        }
                    }

                    var currentTime = DateTime.UtcNow;
                    var elapsed = (currentTime - lastUpdateTime).TotalSeconds;
                    lastUpdateTime = currentTime;

                    // Timeline zamanını güncelle;
                    lock (_syncLock)
                    {
                        _currentTime += elapsed * _playbackSpeed;

                        // Loop kontrolü;
                        if (_currentTime >= _duration)
                        {
                            switch (_loopMode)
                            {
                                case LoopMode.None:
                                    _currentTime = _duration;
                                    _playState = TimelinePlayState.Stopped;
                                    break;
                                case LoopMode.Loop:
                                    _currentTime = 0;
                                    break;
                                case LoopMode.PingPong:
                                    // Ping-pong loop implementasyonu;
                                    _playbackSpeed = -_playbackSpeed;
                                    break;
                            }
                        }

                        // Marker kontrolü;
                        CheckMarkers();
                    }

                    // Frame rate'e göre uyu (60 FPS için ~16ms)
                    await Task.Delay(16, cancellationToken);
                }

                _logger.LogDebug("Playback loop ended");
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Playback loop cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in playback loop");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.PlaybackLoopError);
            }
        }

        private void CheckMarkers()
        {
            var currentMarkers = _markers.Where(m => Math.Abs(m.Time - _currentTime) < 0.05).ToList();

            foreach (var marker in currentMarkers)
            {
                RaiseTimelineEvent(TimelineEventType.MarkerReached,
                    new TimelineEventArgs;
                    {
                        TimelineId = _timelineId,
                        MarkerId = marker.MarkerId,
                        MarkerName = marker.MarkerName,
                        MarkerTime = marker.Time;
                    });
            }
        }

        private void RaiseTimelineEvent(TimelineEventType eventType, TimelineEventArgs args)
        {
            lock (_syncLock)
            {
                if (_eventHandlers.ContainsKey(eventType))
                {
                    foreach (var handler in _eventHandlers[eventType].ToList())
                    {
                        try
                        {
                            handler?.Invoke(args);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in timeline event handler for {EventType}", eventType);
                        }
                    }
                }
            }
        }

        private async Task CreateDefaultTracksAsync()
        {
            // Varsayılan track'ları oluştur;
            await AddTrackAsync(TrackType.Video, "Video Track 1");
            await AddTrackAsync(TrackType.Audio, "Audio Track 1");
            await AddTrackAsync(TrackType.Audio, "Audio Track 2");
            await AddTrackAsync(TrackType.Effect, "Effects Track");

            _logger.LogDebug("Created default tracks");
        }

        private async Task HandleTimelineEvent(TimelineEvent @event)
        {
            // Event bus'tan gelen timeline event'lerini işle;
            // Burada external event'leri handle edebiliriz;
            await Task.CompletedTask;
        }

        private string SerializeTimelineData(TimelineData data)
        {
            // Gerçek implementasyonda JSON serialization kullan;
            return System.Text.Json.JsonSerializer.Serialize(data);
        }

        private TimelineData DeserializeTimelineData(string json)
        {
            // Gerçek implementasyonda JSON deserialization kullan;
            return System.Text.Json.JsonSerializer.Deserialize<TimelineData>(json);
        }

        private async Task ExportToJsonAsync(string outputPath)
        {
            var exportData = new;
            {
                TimelineId = _timelineId,
                TimelineName = _timelineName,
                Duration = _duration,
                Tracks = _tracks,
                Markers = _markers,
                ExportDate = DateTime.UtcNow;
            };

            var json = System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions;
            {
                WriteIndented = true;
            });

            await System.IO.File.WriteAllTextAsync(outputPath, json);
        }

        private async Task ExportToXmlAsync(string outputPath)
        {
            // XML export implementasyonu;
            using var writer = System.Xml.XmlWriter.Create(outputPath, new System.Xml.XmlWriterSettings;
            {
                Indent = true,
                Async = true;
            });

            await writer.WriteStartDocumentAsync();
            await writer.WriteStartElementAsync(null, "Timeline", null);
            await writer.WriteAttributeStringAsync(null, "Id", null, _timelineId.ToString());
            await writer.WriteAttributeStringAsync(null, "Name", null, _timelineName);
            await writer.WriteAttributeStringAsync(null, "Duration", null, _duration.ToString());

            // Track'ları yaz;
            await writer.WriteStartElementAsync(null, "Tracks", null);
            foreach (var track in _tracks)
            {
                await writer.WriteStartElementAsync(null, "Track", null);
                await writer.WriteAttributeStringAsync(null, "Id", null, track.TrackId.ToString());
                await writer.WriteAttributeStringAsync(null, "Name", null, track.TrackName);
                await writer.WriteAttributeStringAsync(null, "Type", null, track.Type.ToString());
                await writer.WriteEndElementAsync();
            }
            await writer.WriteEndElementAsync();

            await writer.WriteEndElementAsync();
            await writer.WriteEndDocumentAsync();
        }

        private async Task ExportToEdlAsync(string outputPath)
        {
            // EDL (Edit Decision List) export implementasyonu;
            var edlContent = new System.Text.StringBuilder();
            edlContent.AppendLine("TITLE: " + _timelineName);
            edlContent.AppendLine("FCM: NON-DROP FRAME");
            edlContent.AppendLine();

            int eventNumber = 1;
            foreach (var track in _tracks)
            {
                foreach (var clip in track.Clips)
                {
                    edlContent.AppendLine($"{eventNumber:000}  AX  V  C        {TimeToEdlTime(clip.StartTime)} {TimeToEdlTime(clip.EndTime)} {TimeToEdlTime(0)} {TimeToEdlTime(clip.Duration)}");
                    eventNumber++;
                }
            }

            await System.IO.File.WriteAllTextAsync(outputPath, edlContent.ToString());
        }

        private async Task ExportToFcpXmlAsync(string outputPath)
        {
            // Final Cut Pro XML export implementasyonu;
            // Basitleştirilmiş versiyon;
            var xmlContent = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE fcpxml>
<fcpxml version=""1.9"">
    <resources>
        <format id=""r1"" name=""FFVideoFormat1080p30"" frameDuration=""100/3000s"" width=""1920"" height=""1080""/>
    </resources>
    <library location=""file:///" >
        <event name=""" + _timelineName + @""" uid=""" + _timelineId.ToString() + @""">
            <project name=""" + _timelineName + @""" uid=""" + Guid.NewGuid().ToString() + @""">
                <sequence format=""r1"" duration=""" + TimeToRationalTime(_duration) + @""">
                    <spine>";
            
            // Track ve clip'leri ekle;
            xmlContent += "</spine></sequence></project></event></library></fcpxml>";

        await System.IO.File.WriteAllTextAsync(outputPath, xmlContent);
        }
        
        private string TimeToEdlTime(double timeInSeconds)
        {
            var timeSpan = TimeSpan.FromSeconds(timeInSeconds);
            return $"{timeSpan.Hours:00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}:{(int)(timeSpan.Milliseconds * 0.024)}";
        }

        private string TimeToRationalTime(double timeInSeconds)
        {
            var frames = (int)(timeInSeconds * 30); // 30 FPS varsay;
            return $"{(frames * 100)}/3000s";
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                _logger.LogInformation("Disposing TimelineController: {TimelineName}", _timelineName);

                // Playback'ı durdur;
                StopAsync().GetAwaiter().GetResult();

                // CancellationTokenSource'u dispose et;
                _playbackCancellationTokenSource?.Dispose();

                // Event handler'ları temizle;
                _eventHandlers.Clear();

                // Track'ları temizle;
                _tracks.Clear();

                // Marker'ları temizle;
                _markers.Clear();

                _isDisposed = true;

                _logger.LogInformation("TimelineController disposed: {TimelineName}", _timelineName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing TimelineController");
            }
        }
    }

    /// <summary>
    /// Timeline track implementasyonu;
    /// </summary>
    internal class TimelineTrack : ITimelineTrack;
{
    private readonly ILogger _logger;
    private readonly List<ITimelineClip> _clips = new List<ITimelineClip>();

    public Guid TrackId { get; }
    public string TrackName { get; set; }
    public TrackType Type { get; }
    public bool IsMuted { get; set; }
    public bool IsLocked { get; set; }
    public double Volume { get; set; } = 1.0;
    public IReadOnlyList<ITimelineClip> Clips => _clips.AsReadOnly();

    public TimelineTrack(TrackType type, string name, ILogger logger)
    {
        TrackId = Guid.NewGuid();
        Type = type;
        TrackName = name;
        _logger = logger;
        Volume = 1.0;
        IsMuted = false;
        IsLocked = false;

        _logger.LogDebug("Created track: {TrackName} ({TrackType})", name, type);
    }

    public async Task<ITimelineClip> AddClipAsync(ClipType clipType, double startTime, double duration, object clipData)
    {
        try
        {
            var clip = new TimelineClip(clipType, startTime, duration, clipData);
            _clips.Add(clip);

            // Clip'leri start time'a göre sırala;
            _clips.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            _logger.LogDebug("Added clip to track {TrackName}: {ClipType} at {StartTime}s",
                TrackName, clipType, startTime);

            return clip;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add clip to track {TrackName}", TrackName);
            throw;
        }
    }

    public async Task<bool> RemoveClipAsync(Guid clipId)
    {
        try
        {
            var clip = _clips.FirstOrDefault(c => (c as TimelineClip)?.ClipId == clipId);
            if (clip == null)
                return false;

            _clips.Remove(clip);
            _logger.LogDebug("Removed clip {ClipId} from track {TrackName}", clipId, TrackName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove clip {ClipId} from track {TrackName}", clipId, TrackName);
            throw;
        }
    }

    public async Task<bool> MoveClipAsync(Guid clipId, double newStartTime)
    {
        try
        {
            var clip = _clips.FirstOrDefault(c => (c as TimelineClip)?.ClipId == clipId) as TimelineClip;
            if (clip == null)
                return false;

            clip.StartTime = newStartTime;

            // Clip'leri yeniden sırala;
            _clips.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            _logger.LogDebug("Moved clip {ClipId} to {NewStartTime}s", clipId, newStartTime);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move clip {ClipId}", clipId);
            throw;
        }
    }

    public async Task<bool> TrimClipAsync(Guid clipId, double newStartTime, double newDuration)
    {
        try
        {
            var clip = _clips.FirstOrDefault(c => (c as TimelineClip)?.ClipId == clipId) as TimelineClip;
            if (clip == null)
                return false;

            clip.StartTime = newStartTime;
            clip.Duration = newDuration;

            _logger.LogDebug("Trimmed clip {ClipId}: Start={StartTime}s, Duration={Duration}s",
                clipId, newStartTime, newDuration);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trim clip {ClipId}", clipId);
            throw;
        }
    }

    public async Task HandleSeekAsync(double time)
    {
        // Seek event'ini tüm clip'lere iletebiliriz;
        await Task.CompletedTask;
    }

    internal TrackData GetTrackData()
    {
        return new TrackData;
        {
            TrackId = TrackId,
            TrackName = TrackName,
            TrackType = Type,
            Volume = Volume,
            IsMuted = IsMuted,
            IsLocked = IsLocked,
            Clips = _clips.Cast<TimelineClip>().Select(c => c.GetClipData()).ToList()
        };
    }

    internal static TimelineTrack FromTrackData(TrackData data, ILogger logger)
    {
        var track = new TimelineTrack(data.TrackType, data.TrackName, logger)
        {
            Volume = data.Volume,
            IsMuted = data.IsMuted,
            IsLocked = data.IsLocked;
        };

        // Clip'leri ekle;
        foreach (var clipData in data.Clips)
        {
            var clip = TimelineClip.FromClipData(clipData);
            track._clips.Add(clip);
        }

        return track;
    }
}

/// <summary>
/// Timeline clip implementasyonu;
/// </summary>
internal class TimelineClip : ITimelineClip;
{
    public Guid ClipId { get; }
    public ClipType Type { get; }
    public double StartTime { get; set; }
    public double Duration { get; set; }
    public double EndTime => StartTime + Duration;
    public bool IsSelected { get; set; }
    public object ClipData { get; private set; }

    public TimelineClip(ClipType type, double startTime, double duration, object clipData)
    {
        ClipId = Guid.NewGuid();
        Type = type;
        StartTime = startTime;
        Duration = duration;
        ClipData = clipData;
        IsSelected = false;
    }

    public async Task<bool> UpdateClipDataAsync(object newData)
    {
        try
        {
            ClipData = newData;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> SplitClipAsync(double splitTime)
    {
        try
        {
            if (splitTime <= StartTime || splitTime >= EndTime)
                return false;

            // Bu implementasyon split işlemini track seviyesinde yapmalı;
            // Burada sadece interface'i implemente ediyoruz;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal ClipData GetClipData()
    {
        return new ClipData;
        {
            ClipId = ClipId,
            ClipType = Type,
            StartTime = StartTime,
            Duration = Duration,
            ClipData = ClipData;
        };
    }

    internal static TimelineClip FromClipData(ClipData data)
    {
        return new TimelineClip(data.ClipType, data.StartTime, data.Duration, data.ClipData)
        {
            ClipId = data.ClipId;
        };
    }
}

/// <summary>
/// Timeline marker implementasyonu;
/// </summary>
internal class TimelineMarker : ITimelineMarker;
{
    public Guid MarkerId { get; }
    public string MarkerName { get; set; }
    public double Time { get; }
    public MarkerType Type { get; }
    public string Notes { get; set; }

    public TimelineMarker(string name, double time, MarkerType type)
    {
        MarkerId = Guid.NewGuid();
        MarkerName = name;
        Time = time;
        Type = type;
        Notes = string.Empty;
    }

    internal MarkerData GetMarkerData()
    {
        return new MarkerData;
        {
            MarkerId = MarkerId,
            MarkerName = MarkerName,
            Time = Time,
            MarkerType = Type,
            Notes = Notes;
        };
    }

    internal static TimelineMarker FromMarkerData(MarkerData data)
    {
        return new TimelineMarker(data.MarkerName, data.Time, data.MarkerType)
        {
            MarkerId = data.MarkerId,
            Notes = data.Notes;
        };
    }
}

// Enum ve Data Class tanımlamaları;

public enum TimelinePlayState;
{
    Stopped,
    Playing,
    Paused;
}

public enum TrackType;
{
    Video,
    Audio,
    Effect,
    Text,
    Adjustment;
}

public enum ClipType;
{
    VideoClip,
    AudioClip,
    ImageClip,
    TextClip,
    EffectClip,
    TransitionClip;
}

public enum MarkerType;
{
    Standard,
    Comment,
    Chapter,
    Beat,
    Cue;
}

public enum ExportFormat;
{
    JSON,
    XML,
    EDL,
    FCPXML,
    AAF;
}

public enum LoopMode;
{
    None,
    Loop,
    PingPong;
}

public enum TimelineEventType;
{
    TimelineInitialized,
    TimelineLoaded,
    TimelineSaved,
    TimelineExported,
    TimelineRenamed,
    PlaybackStarted,
    PlaybackPaused,
    PlaybackStopped,
    PlaybackSpeedChanged,
    RecordingStarted,
    RecordingStopped,
    TrackAdded,
    TrackRemoved,
    TrackMuted,
    TrackUnmuted,
    ClipAdded,
    ClipRemoved,
    ClipMoved,
    MarkerAdded,
    MarkerRemoved,
    MarkerReached,
    SeekPerformed,
    DurationChanged,
    LoopModeChanged,
    ErrorOccurred;
}

public class TimelineEventArgs : EventArgs;
{
    public Guid TimelineId { get; set; }
    public string TimelineName { get; set; }
    public double CurrentTime { get; set; }
    public double PreviousTime { get; set; }
    public double NewTime { get; set; }
    public double OldDuration { get; set; }
    public double NewDuration { get; set; }
    public Guid TrackId { get; set; }
    public string TrackName { get; set; }
    public TrackType TrackType { get; set; }
    public Guid ClipId { get; set; }
    public Guid MarkerId { get; set; }
    public string MarkerName { get; set; }
    public double MarkerTime { get; set; }
    public MarkerType MarkerType { get; set; }
    public string FilePath { get; set; }
    public string OldName { get; set; }
    public string NewName { get; set; }
    public bool IsRecording { get; set; }
    public double PlaybackSpeed { get; set; }
    public LoopMode LoopMode { get; set; }
    public ExportFormat ExportFormat { get; set; }
    public string OutputPath { get; set; }
    public string PreviousState { get; set; }
    public string ErrorMessage { get; set; }
}

public class TimelineConfiguration;
{
    public string TimelineName { get; set; } = "New Timeline";
    public double Duration { get; set; } = 60.0;
    public bool CreateDefaultTracks { get; set; } = true;
    public double FrameRate { get; set; } = 30.0;
    public string TimecodeFormat { get; set; } = "NDF";
    public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
}

internal class TimelineData;
{
    public Guid TimelineId { get; set; }
    public string TimelineName { get; set; }
    public double Duration { get; set; }
    public TimelineConfiguration Configuration { get; set; }
    public List<TrackData> Tracks { get; set; }
    public List<MarkerData> Markers { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime ModifiedDate { get; set; }
}

internal class TrackData;
{
    public Guid TrackId { get; set; }
    public string TrackName { get; set; }
    public TrackType TrackType { get; set; }
    public double Volume { get; set; }
    public bool IsMuted { get; set; }
    public bool IsLocked { get; set; }
    public List<ClipData> Clips { get; set; }
}

internal class ClipData;
{
    public Guid ClipId { get; set; }
    public ClipType ClipType { get; set; }
    public double StartTime { get; set; }
    public double Duration { get; set; }
    public object ClipData { get; set; }
}

internal class MarkerData;
{
    public Guid MarkerId { get; set; }
    public string MarkerName { get; set; }
    public double Time { get; set; }
    public MarkerType MarkerType { get; set; }
    public string Notes { get; set; }
}

public class TimelineEvent : IEvent;
{
    public string EventType { get; set; }
    public Guid TimelineId { get; set; }
    public DateTime Timestamp { get; set; }
    public object Data { get; set; }
}

public class TimelineException : Exception
{
    public string ErrorCode { get; }

    public TimelineException(string message) : base(message)
    {
        ErrorCode = ErrorCodes.TimelineGenericError;
    }

    public TimelineException(string message, string errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }

    public TimelineException(string message, Exception innerException, string errorCode)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public static class ErrorCodes;
{
    public const string TimelineGenericError = "TIMELINE_001";
    public const string TimelineInitializationFailed = "TIMELINE_002";
    public const string TimelineInvalidOperation = "TIMELINE_003";
    public const string PlaybackFailed = "TIMELINE_004";
    public const string SeekFailed = "TIMELINE_005";
    public const string RecordingFailed = "TIMELINE_006";
    public const string TrackOperationFailed = "TIMELINE_007";
    public const string TrackAlreadyExists = "TIMELINE_008";
    public const string TrackLocked = "TIMELINE_009";
    public const string MarkerOperationFailed = "TIMELINE_010";
    public const string MarkerAlreadyExists = "TIMELINE_011";
    public const string TimelineSaveFailed = "TIMELINE_012";
    public const string TimelineLoadFailed = "TIMELINE_013";
    public const string TimelineExportFailed = "TIMELINE_014";
    public const string PlaybackSpeedChangeFailed = "TIMELINE_015";
    public const string LoopModeChangeFailed = "TIMELINE_016";
    public const string PlaybackLoopError = "TIMELINE_017";
}
}
