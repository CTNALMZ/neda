using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NEDA.Animation;
using NEDA.Animation.SequenceEditor.CinematicDirector;
using NEDA.Animation.SequenceEditor.CameraAnimation;
using NEDA.Animation.SequenceEditor.TimelineManagement;
using NEDA.Animation.SequenceEditor.KeyframeEditing;
using NEDA.Common;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Animation.SequenceEditor;
{
    /// <summary>
    /// Sekans editörü - Animasyon zaman çizelgelerini yönetmek için merkezi sınıf;
    /// </summary>
    public class SequenceEditor : ISequenceEditor, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly IEventBus _eventBus;
        private readonly IServiceProvider _serviceProvider;

        private readonly Dictionary<string, AnimationSequence> _sequences;
        private readonly Dictionary<string, Timeline> _timelines;
        private readonly Dictionary<string, CameraShot> _cameraShots;

        private ICinematicDirector _cinematicDirector;
        private ICameraController _cameraController;
        private ITimelineManager _timelineManager;
        private IKeyframeEditor _keyframeEditor;

        private bool _isInitialized;
        private bool _isPlaying;
        private bool _isRecording;
        private float _currentTime;
        private float _playbackSpeed = 1.0f;

        /// <summary>
        /// Sekans editörü olayları;
        /// </summary>
        public event EventHandler<SequenceEventArgs> SequenceStarted;
        public event EventHandler<SequenceEventArgs> SequenceStopped;
        public event EventHandler<SequenceEventArgs> SequencePaused;
        public event EventHandler<KeyframeEventArgs> KeyframeAdded;
        public event EventHandler<KeyframeEventArgs> KeyframeModified;
        public event EventHandler<KeyframeEventArgs> KeyframeRemoved;
        public event EventHandler<TimelineEventArgs> TimelineModified;
        public event EventHandler<PlaybackEventArgs> PlaybackStateChanged;

        /// <summary>
        /// Aktif sekans;
        /// </summary>
        public AnimationSequence ActiveSequence { get; private set; }

        /// <summary>
        /// Aktif zaman çizelgesi;
        /// </summary>
        public Timeline ActiveTimeline { get; private set; }

        /// <summary>
        /// Oynatma durumu;
        /// </summary>
        public PlaybackState PlaybackState { get; private set; }

        /// <summary>
        /// Kayıt durumu;
        /// </summary>
        public bool IsRecording;
        {
            get => _isRecording;
            private set;
            {
                if (_isRecording != value)
                {
                    _isRecording = value;
                    OnRecordingStateChanged(value);
                }
            }
        }

        /// <summary>
        /// Geçerli zaman (saniye)
        /// </summary>
        public float CurrentTime;
        {
            get => _currentTime;
            set;
            {
                if (Math.Abs(_currentTime - value) > 0.001f)
                {
                    _currentTime = Math.Max(0, value);
                    OnTimeChanged(_currentTime);
                }
            }
        }

        /// <summary>
        /// Oynatma hızı (1.0 = normal hız)
        /// </summary>
        public float PlaybackSpeed;
        {
            get => _playbackSpeed;
            set;
            {
                if (Math.Abs(_playbackSpeed - value) > 0.001f)
                {
                    _playbackSpeed = Math.Max(0.1f, Math.Min(10.0f, value));
                    OnPlaybackSpeedChanged(_playbackSpeed);
                }
            }
        }

        /// <summary>
        /// Yapılandırma ayarları;
        /// </summary>
        public SequenceEditorConfig Config { get; private set; }

        /// <summary>
        /// SequenceEditor sınıfı yapıcı metodu;
        /// </summary>
        public SequenceEditor(
            ILogger logger,
            IErrorReporter errorReporter,
            IEventBus eventBus,
            IServiceProvider serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            _sequences = new Dictionary<string, AnimationSequence>();
            _timelines = new Dictionary<string, Timeline>();
            _cameraShots = new Dictionary<string, CameraShot>();

            Config = new SequenceEditorConfig();
            PlaybackState = PlaybackState.Stopped;

            _logger.Info("SequenceEditor initialized");
        }

        /// <summary>
        /// Editörü başlat;
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                _logger.Info("Initializing SequenceEditor...");

                // Alt bileşenleri başlat;
                _cinematicDirector = _serviceProvider.GetService<ICinematicDirector>();
                _cameraController = _serviceProvider.GetService<ICameraController>();
                _timelineManager = _serviceProvider.GetService<ITimelineManager>();
                _keyframeEditor = _serviceProvider.GetService<IKeyframeEditor>();

                // Alt bileşenleri başlat;
                await _cinematicDirector.InitializeAsync();
                await _cameraController.InitializeAsync();
                await _timelineManager.InitializeAsync();
                await _keyframeEditor.InitializeAsync();

                // Olay dinleyicilerini kaydet;
                RegisterEventHandlers();

                // Varsayılan sekans oluştur;
                await CreateDefaultSequenceAsync();

                _isInitialized = true;

                _logger.Info("SequenceEditor initialized successfully");

                // Başlangıç olayını yayınla;
                _eventBus.Publish(new EditorInitializedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    EditorId = GetHashCode().ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize SequenceEditor: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.InitializeAsync");
                throw new SequenceEditorInitializationException(
                    "Failed to initialize SequenceEditor", ex);
            }
        }

        /// <summary>
        /// Yeni animasyon sekansı oluştur;
        /// </summary>
        public async Task<AnimationSequence> CreateSequenceAsync(string sequenceName, SequenceConfig config = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Creating sequence: {sequenceName}");

                if (_sequences.ContainsKey(sequenceName))
                {
                    throw new SequenceAlreadyExistsException($"Sequence '{sequenceName}' already exists");
                }

                var sequence = new AnimationSequence(sequenceName, config ?? new SequenceConfig());

                // Zaman çizelgesi oluştur;
                var timeline = await _timelineManager.CreateTimelineAsync($"{sequenceName}_Timeline");
                sequence.TimelineId = timeline.Id;
                _timelines[timeline.Id] = timeline;

                // Sekansa özel kamera shot'ları oluştur;
                await CreateDefaultCameraShotsForSequenceAsync(sequence);

                _sequences[sequenceName] = sequence;

                _logger.Info($"Sequence '{sequenceName}' created successfully");

                // Olay yayınla;
                OnSequenceCreated(sequence);

                return sequence;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create sequence '{sequenceName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.CreateSequenceAsync");
                throw;
            }
        }

        /// <summary>
        /// Sekansı yükle;
        /// </summary>
        public async Task LoadSequenceAsync(string sequenceName)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Loading sequence: {sequenceName}");

                if (!_sequences.TryGetValue(sequenceName, out var sequence))
                {
                    throw new SequenceNotFoundException($"Sequence '{sequenceName}' not found");
                }

                // Aktif sekansı durdur;
                if (ActiveSequence != null && PlaybackState != PlaybackState.Stopped)
                {
                    await StopPlaybackAsync();
                }

                // Zaman çizelgesini yükle;
                if (!string.IsNullOrEmpty(sequence.TimelineId) &&
                    _timelines.TryGetValue(sequence.TimelineId, out var timeline))
                {
                    ActiveTimeline = timeline;
                    await _timelineManager.LoadTimelineAsync(timeline.Id);
                }

                ActiveSequence = sequence;
                CurrentTime = 0;

                // Kamera shot'larını yükle;
                await LoadCameraShotsForSequenceAsync(sequence);

                _logger.Info($"Sequence '{sequenceName}' loaded successfully");

                // Olay yayınla;
                SequenceStarted?.Invoke(this, new SequenceEventArgs;
                {
                    SequenceName = sequenceName,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load sequence '{sequenceName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.LoadSequenceAsync");
                throw;
            }
        }

        /// <summary>
        /// Sekansı kaydet;
        /// </summary>
        public async Task SaveSequenceAsync(string sequenceName)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Saving sequence: {sequenceName}");

                if (!_sequences.TryGetValue(sequenceName, out var sequence))
                {
                    throw new SequenceNotFoundException($"Sequence '{sequenceName}' not found");
                }

                // Zaman çizelgesini kaydet;
                if (ActiveTimeline != null)
                {
                    await _timelineManager.SaveTimelineAsync(ActiveTimeline.Id);
                }

                // Kamera shot'larını kaydet;
                await SaveCameraShotsForSequenceAsync(sequence);

                // Sekans metadata'sını güncelle;
                sequence.LastModified = DateTime.UtcNow;
                sequence.Version++;

                _logger.Info($"Sequence '{sequenceName}' saved successfully");

                // Olay yayınla;
                OnSequenceSaved(sequence);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save sequence '{sequenceName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.SaveSequenceAsync");
                throw;
            }
        }

        /// <summary>
        /// Sekansı sil;
        /// </summary>
        public async Task DeleteSequenceAsync(string sequenceName)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Deleting sequence: {sequenceName}");

                if (!_sequences.TryGetValue(sequenceName, out var sequence))
                {
                    throw new SequenceNotFoundException($"Sequence '{sequenceName}' not found");
                }

                // Aktif sekans ise durdur;
                if (ActiveSequence == sequence)
                {
                    await StopPlaybackAsync();
                    ActiveSequence = null;
                    ActiveTimeline = null;
                }

                // Zaman çizelgesini sil;
                if (!string.IsNullOrEmpty(sequence.TimelineId))
                {
                    await _timelineManager.DeleteTimelineAsync(sequence.TimelineId);
                    _timelines.Remove(sequence.TimelineId);
                }

                // Kamera shot'larını sil;
                await DeleteCameraShotsForSequenceAsync(sequence);

                // Sekansı kaldır;
                _sequences.Remove(sequenceName);

                _logger.Info($"Sequence '{sequenceName}' deleted successfully");

                // Olay yayınla;
                OnSequenceDeleted(sequenceName);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete sequence '{sequenceName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.DeleteSequenceAsync");
                throw;
            }
        }

        /// <summary>
        /// Oynatmayı başlat;
        /// </summary>
        public async Task StartPlaybackAsync()
        {
            ValidateInitialized();
            ValidateActiveSequence();

            try
            {
                if (PlaybackState == PlaybackState.Playing)
                {
                    _logger.Warning("Playback already started");
                    return;
                }

                _logger.Info("Starting playback");

                PlaybackState = PlaybackState.Playing;
                _isPlaying = true;

                // Zaman çizelgesini başlat;
                if (ActiveTimeline != null)
                {
                    await _timelineManager.StartPlaybackAsync();
                }

                // Sinematik yönetmeni başlat;
                await _cinematicDirector.StartSequenceAsync(ActiveSequence.Name);

                // Kamera animasyonunu başlat;
                await _cameraController.StartCameraAnimationAsync();

                // Oynatma döngüsünü başlat;
                _ = Task.Run(async () => await PlaybackLoopAsync());

                _logger.Info("Playback started successfully");

                // Olay yayınla;
                PlaybackStateChanged?.Invoke(this, new PlaybackEventArgs;
                {
                    State = PlaybackState,
                    Timestamp = DateTime.UtcNow,
                    CurrentTime = CurrentTime;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start playback: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.StartPlaybackAsync");
                throw;
            }
        }

        /// <summary>
        /// Oynatmayı durdur;
        /// </summary>
        public async Task StopPlaybackAsync()
        {
            ValidateInitialized();

            try
            {
                if (PlaybackState == PlaybackState.Stopped)
                {
                    _logger.Warning("Playback already stopped");
                    return;
                }

                _logger.Info("Stopping playback");

                _isPlaying = false;
                PlaybackState = PlaybackState.Stopped;

                // Zaman çizelgesini durdur;
                if (ActiveTimeline != null)
                {
                    await _timelineManager.StopPlaybackAsync();
                }

                // Sinematik yönetmeni durdur;
                await _cinematicDirector.StopSequenceAsync();

                // Kamera animasyonunu durdur;
                await _cameraController.StopCameraAnimationAsync();

                _logger.Info("Playback stopped successfully");

                // Olay yayınla;
                PlaybackStateChanged?.Invoke(this, new PlaybackEventArgs;
                {
                    State = PlaybackState,
                    Timestamp = DateTime.UtcNow,
                    CurrentTime = CurrentTime;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to stop playback: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.StopPlaybackAsync");
                throw;
            }
        }

        /// <summary>
        /// Oynatmayı duraklat;
        /// </summary>
        public async Task PausePlaybackAsync()
        {
            ValidateInitialized();

            try
            {
                if (PlaybackState != PlaybackState.Playing)
                {
                    _logger.Warning("Cannot pause - playback not active");
                    return;
                }

                _logger.Info("Pausing playback");

                PlaybackState = PlaybackState.Paused;
                _isPlaying = false;

                // Zaman çizelgesini duraklat;
                if (ActiveTimeline != null)
                {
                    await _timelineManager.PausePlaybackAsync();
                }

                // Sinematik yönetmeni duraklat;
                await _cinematicDirector.PauseSequenceAsync();

                _logger.Info("Playback paused successfully");

                // Olay yayınla;
                PlaybackStateChanged?.Invoke(this, new PlaybackEventArgs;
                {
                    State = PlaybackState,
                    Timestamp = DateTime.UtcNow,
                    CurrentTime = CurrentTime;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to pause playback: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.PausePlaybackAsync");
                throw;
            }
        }

        /// <summary>
        /// Kayıt modunu başlat/durdur;
        /// </summary>
        public async Task ToggleRecordingAsync()
        {
            ValidateInitialized();
            ValidateActiveSequence();

            try
            {
                if (IsRecording)
                {
                    await StopRecordingAsync();
                }
                else;
                {
                    await StartRecordingAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to toggle recording: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.ToggleRecordingAsync");
                throw;
            }
        }

        /// <summary>
        /// Keyframe ekle;
        /// </summary>
        public async Task AddKeyframeAsync(string trackName, Keyframe keyframe)
        {
            ValidateInitialized();
            ValidateActiveSequence();

            try
            {
                _logger.Info($"Adding keyframe to track '{trackName}' at time {keyframe.Time}");

                if (ActiveTimeline == null)
                {
                    throw new InvalidOperationException("No active timeline");
                }

                // Keyframe'i timeline'a ekle;
                var addedKeyframe = await _timelineManager.AddKeyframeAsync(
                    ActiveTimeline.Id, trackName, keyframe);

                _logger.Info($"Keyframe added successfully to track '{trackName}'");

                // Olay yayınla;
                KeyframeAdded?.Invoke(this, new KeyframeEventArgs;
                {
                    TrackName = trackName,
                    Keyframe = addedKeyframe,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add keyframe: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.AddKeyframeAsync");
                throw;
            }
        }

        /// <summary>
        /// Keyframe düzenle;
        /// </summary>
        public async Task ModifyKeyframeAsync(string trackName, Guid keyframeId, Keyframe newKeyframe)
        {
            ValidateInitialized();
            ValidateActiveSequence();

            try
            {
                _logger.Info($"Modifying keyframe {keyframeId} on track '{trackName}'");

                if (ActiveTimeline == null)
                {
                    throw new InvalidOperationException("No active timeline");
                }

                // Keyframe'i düzenle;
                var modifiedKeyframe = await _timelineManager.ModifyKeyframeAsync(
                    ActiveTimeline.Id, trackName, keyframeId, newKeyframe);

                _logger.Info($"Keyframe {keyframeId} modified successfully");

                // Olay yayınla;
                KeyframeModified?.Invoke(this, new KeyframeEventArgs;
                {
                    TrackName = trackName,
                    Keyframe = modifiedKeyframe,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to modify keyframe: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.ModifyKeyframeAsync");
                throw;
            }
        }

        /// <summary>
        /// Keyframe sil;
        /// </summary>
        public async Task RemoveKeyframeAsync(string trackName, Guid keyframeId)
        {
            ValidateInitialized();
            ValidateActiveSequence();

            try
            {
                _logger.Info($"Removing keyframe {keyframeId} from track '{trackName}'");

                if (ActiveTimeline == null)
                {
                    throw new InvalidOperationException("No active timeline");
                }

                // Keyframe'i sil;
                await _timelineManager.RemoveKeyframeAsync(
                    ActiveTimeline.Id, trackName, keyframeId);

                _logger.Info($"Keyframe {keyframeId} removed successfully");

                // Olay yayınla;
                KeyframeRemoved?.Invoke(this, new KeyframeEventArgs;
                {
                    TrackName = trackName,
                    KeyframeId = keyframeId,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to remove keyframe: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.RemoveKeyframeAsync");
                throw;
            }
        }

        /// <summary>
        /// Kamera shot'ı ekle;
        /// </summary>
        public async Task<CameraShot> AddCameraShotAsync(string shotName, CameraShotConfig config)
        {
            ValidateInitialized();
            ValidateActiveSequence();

            try
            {
                _logger.Info($"Adding camera shot: {shotName}");

                var shot = await _cameraController.CreateCameraShotAsync(shotName, config);
                _cameraShots[shot.Id] = shot;

                // Aktif sekansa ekle;
                if (ActiveSequence != null)
                {
                    ActiveSequence.CameraShots.Add(shot.Id);
                }

                _logger.Info($"Camera shot '{shotName}' added successfully");

                return shot;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add camera shot: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.AddCameraShotAsync");
                throw;
            }
        }

        /// <summary>
        /// Tüm sekansları listele;
        /// </summary>
        public IReadOnlyList<AnimationSequence> GetAllSequences()
        {
            return _sequences.Values.ToList();
        }

        /// <summary>
        /// Sekans ara;
        /// </summary>
        public IReadOnlyList<AnimationSequence> SearchSequences(string searchTerm)
        {
            return _sequences.Values;
                .Where(s => s.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                           s.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Editör yapılandırmasını güncelle;
        /// </summary>
        public async Task UpdateConfigAsync(SequenceEditorConfig config)
        {
            ValidateInitialized();

            try
            {
                _logger.Info("Updating SequenceEditor configuration");

                Config = config ?? throw new ArgumentNullException(nameof(config));

                // Alt bileşenlere config'i uygula;
                if (_timelineManager != null)
                {
                    await _timelineManager.UpdateConfigAsync(config.TimelineConfig);
                }

                if (_keyframeEditor != null)
                {
                    await _keyframeEditor.UpdateConfigAsync(config.KeyframeConfig);
                }

                _logger.Info("SequenceEditor configuration updated successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update configuration: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.UpdateConfigAsync");
                throw;
            }
        }

        /// <summary>
        /// Kaynakları serbest bırak;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                _logger.Info("Disposing SequenceEditor...");

                // Oynatmayı durdur;
                if (PlaybackState != PlaybackState.Stopped)
                {
                    await StopPlaybackAsync();
                }

                // Kaydı durdur;
                if (IsRecording)
                {
                    await StopRecordingAsync();
                }

                // Alt bileşenleri dispose et;
                if (_cinematicDirector is IAsyncDisposable cinematicDisposable)
                {
                    await cinematicDisposable.DisposeAsync();
                }

                if (_cameraController is IAsyncDisposable cameraDisposable)
                {
                    await cameraDisposable.DisposeAsync();
                }

                if (_timelineManager is IAsyncDisposable timelineDisposable)
                {
                    await timelineDisposable.DisposeAsync();
                }

                if (_keyframeEditor is IAsyncDisposable keyframeDisposable)
                {
                    await keyframeDisposable.DisposeAsync();
                }

                // Kaynakları temizle;
                _sequences.Clear();
                _timelines.Clear();
                _cameraShots.Clear();

                _isInitialized = false;

                _logger.Info("SequenceEditor disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error disposing SequenceEditor: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.DisposeAsync");
            }
        }

        #region Private Methods;

        private async Task CreateDefaultSequenceAsync()
        {
            try
            {
                var defaultSequence = await CreateSequenceAsync("Default_Sequence", new SequenceConfig;
                {
                    FrameRate = 30,
                    Duration = 60.0f, // 60 saniye;
                    Loop = false,
                    AutoSave = true;
                });

                await LoadSequenceAsync(defaultSequence.Name);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to create default sequence: {ex.Message}");
                // Default sekans oluşturulamazsa hata verme, sadece logla;
            }
        }

        private async Task CreateDefaultCameraShotsForSequenceAsync(AnimationSequence sequence)
        {
            try
            {
                // Varsayılan kamera shot'ları oluştur;
                var wideShot = await AddCameraShotAsync($"{sequence.Name}_WideShot", new CameraShotConfig;
                {
                    ShotType = CameraShotType.Wide,
                    Duration = 5.0f,
                    Priority = 1;
                });

                var mediumShot = await AddCameraShotAsync($"{sequence.Name}_MediumShot", new CameraShotConfig;
                {
                    ShotType = CameraShotType.Medium,
                    Duration = 3.0f,
                    Priority = 2;
                });

                var closeUpShot = await AddCameraShotAsync($"{sequence.Name}_CloseUp", new CameraShotConfig;
                {
                    ShotType = CameraShotType.CloseUp,
                    Duration = 2.0f,
                    Priority = 3;
                });

                sequence.CameraShots.Add(wideShot.Id);
                sequence.CameraShots.Add(mediumShot.Id);
                sequence.CameraShots.Add(closeUpShot.Id);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to create default camera shots: {ex.Message}");
            }
        }

        private async Task LoadCameraShotsForSequenceAsync(AnimationSequence sequence)
        {
            foreach (var shotId in sequence.CameraShots)
            {
                if (_cameraShots.TryGetValue(shotId, out var shot))
                {
                    await _cameraController.LoadCameraShotAsync(shot);
                }
            }
        }

        private async Task SaveCameraShotsForSequenceAsync(AnimationSequence sequence)
        {
            foreach (var shotId in sequence.CameraShots)
            {
                if (_cameraShots.TryGetValue(shotId, out var shot))
                {
                    await _cameraController.SaveCameraShotAsync(shot);
                }
            }
        }

        private async Task DeleteCameraShotsForSequenceAsync(AnimationSequence sequence)
        {
            foreach (var shotId in sequence.CameraShots)
            {
                if (_cameraShots.TryGetValue(shotId, out var shot))
                {
                    await _cameraController.DeleteCameraShotAsync(shot.Id);
                    _cameraShots.Remove(shotId);
                }
            }
        }

        private async Task StartRecordingAsync()
        {
            _logger.Info("Starting recording");

            IsRecording = true;

            // Kayıt modunu alt bileşenlere bildir;
            if (_timelineManager != null)
            {
                await _timelineManager.StartRecordingAsync();
            }

            if (_keyframeEditor != null)
            {
                await _keyframeEditor.StartRecordingAsync();
            }

            _logger.Info("Recording started");
        }

        private async Task StopRecordingAsync()
        {
            _logger.Info("Stopping recording");

            IsRecording = false;

            // Kayıt modunu durdur;
            if (_timelineManager != null)
            {
                await _timelineManager.StopRecordingAsync();
            }

            if (_keyframeEditor != null)
            {
                await _keyframeEditor.StopRecordingAsync();
            }

            _logger.Info("Recording stopped");
        }

        private async Task PlaybackLoopAsync()
        {
            var lastUpdateTime = DateTime.UtcNow;

            while (_isPlaying && PlaybackState == PlaybackState.Playing)
            {
                try
                {
                    var currentTime = DateTime.UtcNow;
                    var deltaTime = (float)(currentTime - lastUpdateTime).TotalSeconds * PlaybackSpeed;

                    // Zamanı güncelle;
                    CurrentTime += deltaTime;

                    // Zaman çizelgesini güncelle;
                    if (ActiveTimeline != null)
                    {
                        await _timelineManager.UpdateTimelineAsync(CurrentTime);
                    }

                    // Kamera animasyonunu güncelle;
                    await _cameraController.UpdateCameraAnimationAsync(CurrentTime);

                    // Sinematik yönetmeni güncelle;
                    await _cinematicDirector.UpdateSequenceAsync(CurrentTime);

                    // Keyframe kaydı yapılıyorsa;
                    if (IsRecording)
                    {
                        await HandleRecordingAsync(CurrentTime);
                    }

                    // Sekans süresini kontrol et;
                    if (ActiveSequence != null &&
                        CurrentTime >= ActiveSequence.Config.Duration)
                    {
                        if (ActiveSequence.Config.Loop)
                        {
                            CurrentTime = 0;
                        }
                        else;
                        {
                            await StopPlaybackAsync();
                            break;
                        }
                    }

                    lastUpdateTime = currentTime;

                    // Frame rate'e göre bekle;
                    var targetFrameTime = 1.0f / (ActiveSequence?.Config.FrameRate ?? 30);
                    if (deltaTime < targetFrameTime)
                    {
                        var waitTime = (int)((targetFrameTime - deltaTime) * 1000);
                        if (waitTime > 0)
                        {
                            await Task.Delay(waitTime);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in playback loop: {ex.Message}", ex);
                    await _errorReporter.ReportErrorAsync(ex, "SequenceEditor.PlaybackLoopAsync");

                    // Hata durumunda oynatmayı durdur;
                    await StopPlaybackAsync();
                    break;
                }
            }
        }

        private async Task HandleRecordingAsync(float currentTime)
        {
            try
            {
                // Otomatik keyframe kaydı;
                if (Config.AutoKeyframeRecording)
                {
                    await _keyframeEditor.RecordKeyframesAtTimeAsync(currentTime);
                }

                // Kamera kaydı;
                if (Config.RecordCameraMotion)
                {
                    await _cameraController.RecordCameraPositionAsync(currentTime);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error in recording: {ex.Message}");
            }
        }

        private void RegisterEventHandlers()
        {
            // Timeline olaylarını dinle;
            if (_timelineManager != null)
            {
                _timelineManager.TimelineModified += OnTimelineModified;
            }

            // Keyframe editor olaylarını dinle;
            if (_keyframeEditor != null)
            {
                _keyframeEditor.KeyframeAdded += OnKeyframeEditorKeyframeAdded;
                _keyframeEditor.KeyframeModified += OnKeyframeEditorKeyframeModified;
                _keyframeEditor.KeyframeRemoved += OnKeyframeEditorKeyframeRemoved;
            }

            // Camera controller olaylarını dinle;
            if (_cameraController != null)
            {
                _cameraController.CameraShotAdded += OnCameraShotAdded;
                _cameraController.CameraShotModified += OnCameraShotModified;
            }
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new SequenceEditorNotInitializedException(
                    "SequenceEditor must be initialized before use");
            }
        }

        private void ValidateActiveSequence()
        {
            if (ActiveSequence == null)
            {
                throw new NoActiveSequenceException("No active sequence loaded");
            }
        }

        #endregion;

        #region Event Handlers;

        private void OnTimelineModified(object sender, TimelineEventArgs e)
        {
            TimelineModified?.Invoke(this, e);
        }

        private void OnKeyframeEditorKeyframeAdded(object sender, KeyframeEventArgs e)
        {
            KeyframeAdded?.Invoke(this, e);
        }

        private void OnKeyframeEditorKeyframeModified(object sender, KeyframeEventArgs e)
        {
            KeyframeModified?.Invoke(this, e);
        }

        private void OnKeyframeEditorKeyframeRemoved(object sender, KeyframeEventArgs e)
        {
            KeyframeRemoved?.Invoke(this, e);
        }

        private void OnCameraShotAdded(object sender, CameraShotEventArgs e)
        {
            // Kamera shot eklendiğinde sequence'e ekle;
            if (ActiveSequence != null && !ActiveSequence.CameraShots.Contains(e.Shot.Id))
            {
                ActiveSequence.CameraShots.Add(e.Shot.Id);
            }
        }

        private void OnCameraShotModified(object sender, CameraShotEventArgs e)
        {
            // Kamera shot değişikliklerini işle;
        }

        private void OnSequenceCreated(AnimationSequence sequence)
        {
            _eventBus.Publish(new SequenceCreatedEvent;
            {
                SequenceName = sequence.Name,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnSequenceSaved(AnimationSequence sequence)
        {
            _eventBus.Publish(new SequenceSavedEvent;
            {
                SequenceName = sequence.Name,
                Timestamp = DateTime.UtcNow,
                Version = sequence.Version;
            });
        }

        private void OnSequenceDeleted(string sequenceName)
        {
            _eventBus.Publish(new SequenceDeletedEvent;
            {
                SequenceName = sequenceName,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnTimeChanged(float newTime)
        {
            _eventBus.Publish(new TimeChangedEvent;
            {
                CurrentTime = newTime,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnPlaybackSpeedChanged(float newSpeed)
        {
            _eventBus.Publish(new PlaybackSpeedChangedEvent;
            {
                Speed = newSpeed,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnRecordingStateChanged(bool isRecording)
        {
            _eventBus.Publish(new RecordingStateChangedEvent;
            {
                IsRecording = isRecording,
                Timestamp = DateTime.UtcNow;
            });
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            DisposeAsync().AsTask().Wait();
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    /// <summary>
    /// Animasyon sekansı;
    /// </summary>
    public class AnimationSequence;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public SequenceConfig Config { get; set; }
        public string TimelineId { get; set; }
        public List<string> CameraShots { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
        public int Version { get; set; }

        public AnimationSequence(string name, SequenceConfig config)
        {
            Name = name;
            Config = config;
            CameraShots = new List<string>();
            Created = DateTime.UtcNow;
            LastModified = DateTime.UtcNow;
            Version = 1;
        }
    }

    /// <summary>
    /// Sekans yapılandırması;
    /// </summary>
    public class SequenceConfig;
    {
        public int FrameRate { get; set; } = 30;
        public float Duration { get; set; } = 60.0f;
        public bool Loop { get; set; } = false;
        public bool AutoSave { get; set; } = true;
        public float StartTime { get; set; } = 0.0f;
        public float EndTime { get; set; } = 60.0f;
    }

    /// <summary>
    /// SequenceEditor yapılandırması;
    /// </summary>
    public class SequenceEditorConfig;
    {
        public bool AutoKeyframeRecording { get; set; } = true;
        public bool RecordCameraMotion { get; set; } = true;
        public float AutoSaveInterval { get; set; } = 300.0f; // 5 dakika;
        public int MaxUndoLevels { get; set; } = 50;
        public TimelineConfig TimelineConfig { get; set; } = new TimelineConfig();
        public KeyframeConfig KeyframeConfig { get; set; } = new KeyframeConfig();
    }

    /// <summary>
    /// Oynatma durumu;
    /// </summary>
    public enum PlaybackState;
    {
        Stopped,
        Playing,
        Paused,
        Recording;
    }

    /// <summary>
    /// Kamera shot tipi;
    /// </summary>
    public enum CameraShotType;
    {
        Wide,
        Medium,
        CloseUp,
        ExtremeCloseUp,
        PointOfView,
        OverTheShoulder;
    }

    #region Event Classes;

    public class SequenceEventArgs : EventArgs;
    {
        public string SequenceName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class KeyframeEventArgs : EventArgs;
    {
        public string TrackName { get; set; }
        public Keyframe Keyframe { get; set; }
        public Guid KeyframeId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TimelineEventArgs : EventArgs;
    {
        public string TimelineId { get; set; }
        public TimelineOperation Operation { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PlaybackEventArgs : EventArgs;
    {
        public PlaybackState State { get; set; }
        public DateTime Timestamp { get; set; }
        public float CurrentTime { get; set; }
    }

    public class CameraShotEventArgs : EventArgs;
    {
        public CameraShot Shot { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum TimelineOperation;
    {
        Created,
        Modified,
        Deleted,
        Loaded,
        Saved;
    }

    #endregion;

    #region Event Bus Events;

    public class EditorInitializedEvent : IEvent;
    {
        public string EditorId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SequenceCreatedEvent : IEvent;
    {
        public string SequenceName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SequenceSavedEvent : IEvent;
    {
        public string SequenceName { get; set; }
        public DateTime Timestamp { get; set; }
        public int Version { get; set; }
    }

    public class SequenceDeletedEvent : IEvent;
    {
        public string SequenceName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TimeChangedEvent : IEvent;
    {
        public float CurrentTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PlaybackSpeedChangedEvent : IEvent;
    {
        public float Speed { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class RecordingStateChangedEvent : IEvent;
    {
        public bool IsRecording { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class SequenceEditorException : Exception
    {
        public SequenceEditorException(string message) : base(message) { }
        public SequenceEditorException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SequenceEditorInitializationException : SequenceEditorException;
    {
        public SequenceEditorInitializationException(string message) : base(message) { }
        public SequenceEditorInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SequenceEditorNotInitializedException : SequenceEditorException;
    {
        public SequenceEditorNotInitializedException(string message) : base(message) { }
    }

    public class SequenceAlreadyExistsException : SequenceEditorException;
    {
        public SequenceAlreadyExistsException(string message) : base(message) { }
    }

    public class SequenceNotFoundException : SequenceEditorException;
    {
        public SequenceNotFoundException(string message) : base(message) { }
    }

    public class NoActiveSequenceException : SequenceEditorException;
    {
        public NoActiveSequenceException(string message) : base(message) { }
    }

    #endregion;

    #region Interfaces;

    public interface ISequenceEditor : IAsyncDisposable;
    {
        event EventHandler<SequenceEventArgs> SequenceStarted;
        event EventHandler<SequenceEventArgs> SequenceStopped;
        event EventHandler<SequenceEventArgs> SequencePaused;
        event EventHandler<KeyframeEventArgs> KeyframeAdded;
        event EventHandler<KeyframeEventArgs> KeyframeModified;
        event EventHandler<KeyframeEventArgs> KeyframeRemoved;
        event EventHandler<TimelineEventArgs> TimelineModified;
        event EventHandler<PlaybackEventArgs> PlaybackStateChanged;

        AnimationSequence ActiveSequence { get; }
        Timeline ActiveTimeline { get; }
        PlaybackState PlaybackState { get; }
        bool IsRecording { get; }
        float CurrentTime { get; set; }
        float PlaybackSpeed { get; set; }
        SequenceEditorConfig Config { get; }

        Task InitializeAsync();
        Task<AnimationSequence> CreateSequenceAsync(string sequenceName, SequenceConfig config = null);
        Task LoadSequenceAsync(string sequenceName);
        Task SaveSequenceAsync(string sequenceName);
        Task DeleteSequenceAsync(string sequenceName);
        Task StartPlaybackAsync();
        Task StopPlaybackAsync();
        Task PausePlaybackAsync();
        Task ToggleRecordingAsync();
        Task AddKeyframeAsync(string trackName, Keyframe keyframe);
        Task ModifyKeyframeAsync(string trackName, Guid keyframeId, Keyframe newKeyframe);
        Task RemoveKeyframeAsync(string trackName, Guid keyframeId);
        Task<CameraShot> AddCameraShotAsync(string shotName, CameraShotConfig config);
        IReadOnlyList<AnimationSequence> GetAllSequences();
        IReadOnlyList<AnimationSequence> SearchSequences(string searchTerm);
        Task UpdateConfigAsync(SequenceEditorConfig config);
    }

    // Referans için diğer arayüzler (diğer dosyalarda implemente edilecek)
    public interface ICinematicDirector { /* ... */ }
    public interface ICameraController { /* ... */ }
    public interface ITimelineManager { /* ... */ }
    public interface IKeyframeEditor { /* ... */ }
    public class Timeline { /* ... */ }
    public class Keyframe { /* ... */ }
    public class CameraShot { /* ... */ }
    public class CameraShotConfig { /* ... */ }
    public class TimelineConfig { /* ... */ }
    public class KeyframeConfig { /* ... */ }

    #endregion;
}
