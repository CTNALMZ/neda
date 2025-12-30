using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using NEDA.Core.Common.Extensions;
using NEDA.Core.ExceptionHandling.ErrorReporting;
using NEDA.Core.Logging;

namespace NEDA.MediaProcessing.AudioProcessing.SoundEditing;
{
    /// <summary>
    /// Profesyonel ses düzenleme editörü - Multi-track editing, efektler, mastering araçları;
    /// </summary>
    public class AudioEditor : IAudioEditor, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger<AudioEditor> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly Dictionary<string, AudioTrack> _audioTracks;
        private readonly Dictionary<string, AudioEffect> _effects;
        private readonly Dictionary<string, AudioProject> _projects;
        private readonly object _lockObject = new object();
        private readonly AudioEngineConfiguration _configuration;
        private MixingSampleProvider _masterMixer;
        private IWavePlayer _outputDevice;
        private bool _disposed;
        private float _masterVolume = 1.0f;
        private bool _isPlaying;
        private long _playbackPosition;
        private TimeSpan _totalDuration;
        #endregion;

        #region Properties;
        /// <summary>
        /// Ana ses seviyesi (0.0 - 1.0)
        /// </summary>
        public float MasterVolume;
        {
            get => _masterVolume;
            set;
            {
                _masterVolume = Math.Clamp(value, 0.0f, 1.0f);
                UpdateMasterVolume();
                OnVolumeChanged(new AudioVolumeChangedEventArgs;
                {
                    Channel = "Master",
                    Volume = _masterVolume,
                    IsMuted = false;
                });
            }
        }

        /// <summary>
        /// Çalma durumunda mı?
        /// </summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// Şu anki çalma pozisyonu;
        /// </summary>
        public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(_playbackPosition);

        /// <summary>
        /// Toplam süre;
        /// </summary>
        public TimeSpan TotalDuration => _totalDuration;

        /// <summary>
        /// Aktif proje;
        /// </summary>
        public AudioProject ActiveProject { get; private set; }

        /// <summary>
        /// Varsayılan örnekleme hızı;
        /// </summary>
        public int DefaultSampleRate => 44100;

        /// <summary>
        /// Varsayılan bit derinliği;
        /// </summary>
        public int DefaultBitDepth => 16;

        /// <summary>
        /// Varsayılan kanal sayısı;
        /// </summary>
        public int DefaultChannelCount => 2;

        /// <summary>
        /// Tempo (BPM)
        /// </summary>
        public float Tempo { get; set; } = 120.0f;

        /// <summary>
        /// Pitch ayarı (yarım ton)
        /// </summary>
        public float Pitch { get; set; } = 0.0f;

        /// <summary>
        /// Kayıt durumunda mı?
        /// </summary>
        public bool IsRecording { get; private set; }

        /// <summary>
        /// Döngü aktif mi?
        /// </summary>
        public bool IsLooping { get; set; }
        #endregion;

        #region Events;
        /// <summary>
        /// Çalma başladığında tetiklenir;
        /// </summary>
        public event EventHandler<PlaybackStartedEventArgs> PlaybackStarted;

        /// <summary>
        /// Çalma durduğunda tetiklenir;
        /// </summary>
        public event EventHandler<PlaybackStoppedEventArgs> PlaybackStopped;

        /// <summary>
        /// Çalma pozisyonu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<PlaybackPositionChangedEventArgs> PlaybackPositionChanged;

        /// <summary>
        /// Ses yüklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AudioLoadedEventArgs> AudioLoaded;

        /// <summary>
        /// Ses kaydedildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AudioRecordedEventArgs> AudioRecorded;

        /// <summary>
        /// Efekt uygulandığında tetiklenir;
        /// </summary>
        public event EventHandler<EffectAppliedEventArgs> EffectApplied;

        /// <summary>
        /// Ses düzenlendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AudioEditedEventArgs> AudioEdited;

        /// <summary>
        /// Proje kaydedildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ProjectSavedEventArgs> ProjectSaved;

        /// <summary>
        /// Hata oluştuğunda tetiklenir;
        /// </summary>
        public event EventHandler<AudioErrorEventArgs> ErrorOccurred;
        #endregion;

        #region Constructors;
        /// <summary>
        /// AudioEditor sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="errorReporter">Hata raporlama servisi</param>
        public AudioEditor(
            ILogger<AudioEditor> logger,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _configuration = new AudioEngineConfiguration();

            _audioTracks = new Dictionary<string, AudioTrack>();
            _effects = new Dictionary<string, AudioEffect>();
            _projects = new Dictionary<string, AudioProject>();

            InitializeAudioEngine();
            InitializeBuiltInEffects();

            _logger.LogInformation("AudioEditor başlatıldı. Varsayılan sample rate: {SampleRate}, Channels: {Channels}",
                DefaultSampleRate, DefaultChannelCount);
        }

        /// <summary>
        /// Özel konfigürasyon ile AudioEditor sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public AudioEditor(
            ILogger<AudioEditor> logger,
            IErrorReporter errorReporter,
            AudioEngineConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _audioTracks = new Dictionary<string, AudioTrack>();
            _effects = new Dictionary<string, AudioEffect>();
            _projects = new Dictionary<string, AudioProject>();

            InitializeAudioEngine();
            InitializeBuiltInEffects();

            _logger.LogInformation("AudioEditor başlatıldı. Özel konfigürasyon uygulandı.");
        }
        #endregion;

        #region Public Methods - Project Management;
        /// <summary>
        /// Yeni ses projesi oluşturur;
        /// </summary>
        public AudioProject CreateProject(string projectName, ProjectSettings settings = null)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                throw new ArgumentException("Proje adı boş olamaz", nameof(projectName));

            try
            {
                settings ??= new ProjectSettings;
                {
                    SampleRate = DefaultSampleRate,
                    BitDepth = DefaultBitDepth,
                    ChannelCount = DefaultChannelCount,
                    Tempo = 120.0f;
                };

                var project = new AudioProject;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = projectName,
                    CreatedDate = DateTime.UtcNow,
                    Settings = settings,
                    Tracks = new List<AudioTrack>(),
                    Markers = new List<AudioMarker>()
                };

                lock (_lockObject)
                {
                    _projects[project.Id] = project;
                    ActiveProject = project;
                }

                _logger.LogInformation("Yeni proje oluşturuldu: {ProjectName}, ID: {ProjectId}",
                    projectName, project.Id);

                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proje oluşturulamadı: {ProjectName}", projectName);
                throw new AudioException($"'{projectName}' projesi oluşturulamadı", ex);
            }
        }

        /// <summary>
        /// Projeyi yükler;
        /// </summary>
        public async Task<AudioProject> LoadProjectAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Dosya yolu boş olamaz", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Proje dosyası bulunamadı: {filePath}", filePath);

            try
            {
                _logger.LogInformation("Proje yükleniyor: {FilePath}", filePath);

                string jsonContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                var project = System.Text.Json.JsonSerializer.Deserialize<AudioProject>(
                    jsonContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (project == null)
                    throw new AudioException($"Proje dosyası geçersiz: {filePath}");

                // Ses dosyalarını yeniden yükle;
                foreach (var track in project.Tracks)
                {
                    if (!string.IsNullOrEmpty(track.SourceFilePath) && File.Exists(track.SourceFilePath))
                    {
                        await LoadAudioFileAsync(track.SourceFilePath, track.Id, cancellationToken);
                    }
                }

                lock (_lockObject)
                {
                    _projects[project.Id] = project;
                    ActiveProject = project;
                }

                UpdateTotalDuration();

                _logger.LogInformation("Proje yüklendi: {ProjectName}, Tracks: {TrackCount}",
                    project.Name, project.Tracks.Count);

                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proje yüklenemedi: {FilePath}", filePath);
                throw new AudioException($"'{filePath}' projesi yüklenemedi", ex);
            }
        }

        /// <summary>
        /// Projeyi kaydeder;
        /// </summary>
        public async Task SaveProjectAsync(string filePath = null, CancellationToken cancellationToken = default)
        {
            if (ActiveProject == null)
                throw new AudioException("Aktif proje bulunamadı");

            try
            {
                filePath ??= ActiveProject.FilePath;
                if (string.IsNullOrWhiteSpace(filePath))
                    throw new ArgumentException("Dosya yolu belirtilmelidir", nameof(filePath));

                _logger.LogInformation("Proje kaydediliyor: {ProjectName} -> {FilePath}",
                    ActiveProject.Name, filePath);

                // Proje durumunu güncelle;
                ActiveProject.ModifiedDate = DateTime.UtcNow;
                ActiveProject.FilePath = filePath;

                // JSON olarak kaydet;
                var options = new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                };

                string jsonContent = System.Text.Json.JsonSerializer.Serialize(ActiveProject, options);
                await File.WriteAllTextAsync(filePath, jsonContent, cancellationToken);

                _logger.LogInformation("Proje kaydedildi: {FilePath}", filePath);

                OnProjectSaved(new ProjectSavedEventArgs;
                {
                    ProjectId = ActiveProject.Id,
                    FilePath = filePath,
                    TrackCount = ActiveProject.Tracks.Count;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proje kaydedilemedi");
                throw new AudioException("Proje kaydedilemedi", ex);
            }
        }

        /// <summary>
        /// Projeyi kapatır;
        /// </summary>
        public void CloseProject()
        {
            try
            {
                StopPlayback();

                lock (_lockObject)
                {
                    // Tüm track'leri temizle;
                    foreach (var track in _audioTracks.Values)
                    {
                        track.Dispose();
                    }
                    _audioTracks.Clear();

                    ActiveProject = null;
                }

                _logger.LogInformation("Proje kapatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proje kapatılamadı");
                throw new AudioException("Proje kapatılamadı", ex);
            }
        }
        #endregion;

        #region Public Methods - Audio Loading;
        /// <summary>
        /// Ses dosyasını yükler;
        /// </summary>
        public async Task<AudioTrack> LoadAudioFileAsync(string filePath, string trackId = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Dosya yolu boş olamaz", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Ses dosyası bulunamadı: {filePath}", filePath);

            try
            {
                trackId ??= Guid.NewGuid().ToString();

                _logger.LogInformation("Ses dosyası yükleniyor: {FilePath}, TrackID: {TrackId}",
                    filePath, trackId);

                AudioFileReader audioFile;
                try
                {
                    audioFile = new AudioFileReader(filePath);
                }
                catch
                {
                    // Farklı format denemesi;
                    using var reader = new MediaFoundationReader(filePath);
                    var provider = reader.ToSampleProvider();
                    audioFile = new AudioFileReader(filePath); // Tekrar deneme;
                }

                var track = new AudioTrack(trackId, audioFile)
                {
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    SourceFilePath = filePath,
                    StartTime = TimeSpan.Zero,
                    Duration = audioFile.TotalTime,
                    SampleRate = audioFile.WaveFormat.SampleRate,
                    Channels = audioFile.WaveFormat.Channels,
                    Volume = 1.0f,
                    Pan = 0.0f,
                    IsMuted = false,
                    IsSolo = false;
                };

                lock (_lockObject)
                {
                    if (_audioTracks.ContainsKey(trackId))
                    {
                        _logger.LogWarning("Track ID zaten mevcut: {TrackId}, üzerine yazılıyor", trackId);
                        _audioTracks[trackId].Dispose();
                    }

                    _audioTracks[trackId] = track;

                    // Aktif projeye ekle;
                    if (ActiveProject != null)
                    {
                        ActiveProject.Tracks.Add(track);
                        ActiveProject.ModifiedDate = DateTime.UtcNow;
                    }
                }

                UpdateTotalDuration();

                _logger.LogInformation("Ses dosyası yüklendi: {FilePath}, Duration: {Duration}, Format: {SampleRate}Hz/{Channels}ch",
                    filePath, track.Duration, track.SampleRate, track.Channels);

                OnAudioLoaded(new AudioLoadedEventArgs;
                {
                    TrackId = trackId,
                    FilePath = filePath,
                    Duration = track.Duration,
                    SampleRate = track.SampleRate,
                    Channels = track.Channels;
                });

                return track;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses dosyası yüklenemedi: {FilePath}", filePath);
                throw new AudioException($"'{filePath}' ses dosyası yüklenemedi", ex);
            }
        }

        /// <summary>
        /// Byte dizisinden ses yükler;
        /// </summary>
        public AudioTrack LoadAudioFromBytes(byte[] audioData, WaveFormat format, string trackId = null)
        {
            if (audioData == null || audioData.Length == 0)
                throw new ArgumentException("Ses verisi boş olamaz", nameof(audioData));

            try
            {
                trackId ??= Guid.NewGuid().ToString();

                _logger.LogInformation("Byte dizisinden ses yükleniyor: {Length} bytes, TrackID: {TrackId}",
                    audioData.Length, trackId);

                var memoryStream = new MemoryStream(audioData);
                var reader = new RawSourceWaveStream(memoryStream, format);
                var track = new AudioTrack(trackId, reader)
                {
                    Name = $"ByteAudio_{trackId}",
                    StartTime = TimeSpan.Zero,
                    Duration = reader.TotalTime,
                    SampleRate = format.SampleRate,
                    Channels = format.Channels,
                    Volume = 1.0f,
                    Pan = 0.0f;
                };

                lock (_lockObject)
                {
                    _audioTracks[trackId] = track;

                    if (ActiveProject != null)
                    {
                        ActiveProject.Tracks.Add(track);
                        ActiveProject.ModifiedDate = DateTime.UtcNow;
                    }
                }

                UpdateTotalDuration();

                _logger.LogInformation("Byte dizisinden ses yüklendi: {Length} bytes, Duration: {Duration}",
                    audioData.Length, track.Duration);

                return track;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Byte dizisinden ses yüklenemedi");
                throw new AudioException("Byte dizisinden ses yüklenemedi", ex);
            }
        }

        /// <summary>
        /// Mikrofon veya line-in'den ses kaydeder;
        /// </summary>
        public async Task<AudioTrack> RecordAudioAsync(TimeSpan duration, RecordingSettings settings = null, CancellationToken cancellationToken = default)
        {
            if (duration <= TimeSpan.Zero)
                throw new ArgumentException("Kayıt süresi pozitif olmalıdır", nameof(duration));

            try
            {
                settings ??= new RecordingSettings();

                _logger.LogInformation("Ses kaydı başlatılıyor: Duration: {Duration}, Device: {DeviceName}",
                    duration, settings.DeviceName);

                var trackId = Guid.NewGuid().ToString();
                string tempFilePath = Path.GetTempFileName() + ".wav";

                using var waveIn = new WaveInEvent;
                {
                    DeviceNumber = settings.DeviceIndex,
                    WaveFormat = new WaveFormat(settings.SampleRate, settings.BitDepth, settings.Channels),
                    BufferMilliseconds = settings.BufferSize;
                };

                using var writer = new WaveFileWriter(tempFilePath, waveIn.WaveFormat);

                var tcs = new TaskCompletionSource<bool>();
                var stopRecording = false;

                waveIn.DataAvailable += (sender, e) =>
                {
                    if (stopRecording || cancellationToken.IsCancellationRequested)
                        return;

                    writer.Write(e.Buffer, 0, e.BytesRecorded);
                    writer.Flush();
                };

                waveIn.RecordingStopped += (sender, e) =>
                {
                    writer.Dispose();
                    tcs.SetResult(true);
                };

                waveIn.StartRecording();
                IsRecording = true;

                // Belirtilen süre kadar bekle;
                await Task.Delay(duration, cancellationToken);

                stopRecording = true;
                waveIn.StopRecording();
                IsRecording = false;

                await tcs.Task;

                // Kaydedilen sesi yükle;
                var track = await LoadAudioFileAsync(tempFilePath, trackId, cancellationToken);
                track.Name = $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}";

                // Geçici dosyayı sil;
                try
                {
                    File.Delete(tempFilePath);
                }
                catch { /* Ignore */ }

                _logger.LogInformation("Ses kaydı tamamlandı: Duration: {Duration}, File: {FilePath}",
                    duration, track.SourceFilePath);

                OnAudioRecorded(new AudioRecordedEventArgs;
                {
                    TrackId = trackId,
                    Duration = duration,
                    SampleRate = settings.SampleRate,
                    Channels = settings.Channels;
                });

                return track;
            }
            catch (Exception ex)
            {
                IsRecording = false;
                _logger.LogError(ex, "Ses kaydı başarısız");
                throw new AudioException("Ses kaydı başarısız", ex);
            }
        }
        #endregion;

        #region Public Methods - Playback Control;
        /// <summary>
        /// Çalmayı başlatır;
        /// </summary>
        public void StartPlayback()
        {
            if (_isPlaying)
                return;

            try
            {
                lock (_lockObject)
                {
                    if (_outputDevice == null)
                    {
                        _outputDevice = new WaveOutEvent;
                        {
                            DesiredLatency = 100,
                            NumberOfBuffers = 2;
                        };

                        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(DefaultSampleRate, DefaultChannelCount);
                        _masterMixer = new MixingSampleProvider(waveFormat)
                        {
                            ReadFully = true;
                        };

                        _outputDevice.Init(_masterMixer);
                    }

                    // Tüm track'leri mixere ekle;
                    foreach (var track in _audioTracks.Values)
                    {
                        if (!track.IsMuted && (track.IsSolo || !_audioTracks.Values.Any(t => t.IsSolo)))
                        {
                            var provider = track.GetSampleProvider();
                            if (provider != null)
                            {
                                _masterMixer.AddMixerInput(provider);
                            }
                        }
                    }

                    _outputDevice.Play();
                    _isPlaying = true;

                    // Çalma pozisyonu takibi;
                    Task.Run(async () =>
                    {
                        while (_isPlaying)
                        {
                            await Task.Delay(50);
                            _playbackPosition += 50;

                            OnPlaybackPositionChanged(new PlaybackPositionChangedEventArgs;
                            {
                                CurrentPosition = CurrentPosition,
                                TotalDuration = TotalDuration;
                            });

                            // Döngü kontrolü;
                            if (IsLooping && CurrentPosition >= TotalDuration)
                            {
                                SeekToPosition(TimeSpan.Zero);
                            }

                            if (CurrentPosition >= TotalDuration && !IsLooping)
                            {
                                StopPlayback();
                                break;
                            }
                        }
                    });
                }

                _logger.LogInformation("Çalma başlatıldı");

                OnPlaybackStarted(new PlaybackStartedEventArgs;
                {
                    StartPosition = CurrentPosition,
                    TotalDuration = TotalDuration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Çalma başlatılamadı");
                throw new AudioException("Çalma başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Çalmayı durdurur;
        /// </summary>
        public void StopPlayback()
        {
            if (!_isPlaying)
                return;

            try
            {
                lock (_lockObject)
                {
                    _outputDevice?.Stop();
                    _isPlaying = false;
                    _playbackPosition = 0;
                }

                _logger.LogInformation("Çalma durduruldu");

                OnPlaybackStopped(new PlaybackStoppedEventArgs;
                {
                    StopPosition = CurrentPosition,
                    TotalDuration = TotalDuration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Çalma durdurulamadı");
                throw new AudioException("Çalma durdurulamadı", ex);
            }
        }

        /// <summary>
        /// Çalmayı duraklatır;
        /// </summary>
        public void PausePlayback()
        {
            if (!_isPlaying)
                return;

            try
            {
                lock (_lockObject)
                {
                    _outputDevice?.Pause();
                    _isPlaying = false;
                }

                _logger.LogInformation("Çalma duraklatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Çalma duraklatılamadı");
                throw new AudioException("Çalma duraklatılamadı", ex);
            }
        }

        /// <summary>
        /// Belirli bir pozisyona atlar;
        /// </summary>
        public void SeekToPosition(TimeSpan position)
        {
            try
            {
                var wasPlaying = _isPlaying;

                if (_isPlaying)
                {
                    PausePlayback();
                }

                lock (_lockObject)
                {
                    _playbackPosition = (long)position.TotalMilliseconds;

                    // Tüm track'lerin pozisyonunu güncelle;
                    foreach (var track in _audioTracks.Values)
                    {
                        track.Seek(position);
                    }
                }

                _logger.LogDebug("Pozisyon atlandı: {Position}", position);

                if (wasPlaying)
                {
                    StartPlayback();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pozisyon atlanamadı: {Position}", position);
                throw new AudioException($"'{position}' pozisyonuna atlanamadı", ex);
            }
        }
        #endregion;

        #region Public Methods - Editing Operations;
        /// <summary>
        /// Ses parçasını keser;
        /// </summary>
        public AudioClip Cut(string trackId, TimeSpan startTime, TimeSpan duration)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogInformation("Ses kesiliyor: Track: {TrackId}, Start: {StartTime}, Duration: {Duration}",
                    trackId, startTime, duration);

                var clip = track.Cut(startTime, duration);

                UpdateTotalDuration();

                _logger.LogInformation("Ses kesildi: ClipID: {ClipId}, Duration: {Duration}",
                    clip.Id, clip.Duration);

                OnAudioEdited(new AudioEditedEventArgs;
                {
                    TrackId = trackId,
                    Operation = EditOperation.Cut,
                    StartTime = startTime,
                    Duration = duration;
                });

                return clip;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kesilemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inden ses kesilemedi", ex);
            }
        }

        /// <summary>
        /// Ses parçasını kopyalar;
        /// </summary>
        public AudioClip Copy(string trackId, TimeSpan startTime, TimeSpan duration)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Ses kopyalanıyor: Track: {TrackId}, Start: {StartTime}, Duration: {Duration}",
                    trackId, startTime, duration);

                var clip = track.Copy(startTime, duration);

                _logger.LogDebug("Ses kopyalandı: ClipID: {ClipId}, Duration: {Duration}",
                    clip.Id, clip.Duration);

                return clip;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kopyalanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inden ses kopyalanamadı", ex);
            }
        }

        /// <summary>
        /// Ses parçasını yapıştırır;
        /// </summary>
        public void Paste(string trackId, AudioClip clip, TimeSpan position)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            if (clip == null)
                throw new ArgumentNullException(nameof(clip));

            try
            {
                _logger.LogInformation("Ses yapıştırılıyor: Track: {TrackId}, Position: {Position}, Clip: {ClipId}",
                    trackId, position, clip.Id);

                track.Paste(clip, position);

                UpdateTotalDuration();

                _logger.LogInformation("Ses yapıştırıldı: Track: {TrackId}, Position: {Position}",
                    trackId, position);

                OnAudioEdited(new AudioEditedEventArgs;
                {
                    TrackId = trackId,
                    Operation = EditOperation.Paste,
                    StartTime = position,
                    Duration = clip.Duration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses yapıştırılamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine ses yapıştırılamadı", ex);
            }
        }

        /// <summary>
        /// Ses parçasını siler;
        /// </summary>
        public void Delete(string trackId, TimeSpan startTime, TimeSpan duration)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogInformation("Ses siliniyor: Track: {TrackId}, Start: {StartTime}, Duration: {Duration}",
                    trackId, startTime, duration);

                track.Delete(startTime, duration);

                UpdateTotalDuration();

                _logger.LogInformation("Ses silindi: Track: {TrackId}, Duration: {Duration}",
                    trackId, duration);

                OnAudioEdited(new AudioEditedEventArgs;
                {
                    TrackId = trackId,
                    Operation = EditOperation.Delete,
                    StartTime = startTime,
                    Duration = duration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses silinemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inden ses silinemedi", ex);
            }
        }

        /// <summary>
        /// Sesi trim eder (başlangıç ve bitiş arasını alır)
        /// </summary>
        public void Trim(string trackId, TimeSpan startTime, TimeSpan endTime)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogInformation("Ses trim ediliyor: Track: {TrackId}, Start: {StartTime}, End: {EndTime}",
                    trackId, startTime, endTime);

                var duration = endTime - startTime;
                if (duration <= TimeSpan.Zero)
                    throw new ArgumentException("Başlangıç zamanı bitiş zamanından önce olmalıdır");

                // Kes ve yeni track oluştur;
                var clip = track.Cut(startTime, duration);
                track.Clear();
                track.Paste(clip, TimeSpan.Zero);

                UpdateTotalDuration();

                _logger.LogInformation("Ses trim edildi: Track: {TrackId}, Duration: {Duration}",
                    trackId, duration);

                OnAudioEdited(new AudioEditedEventArgs;
                {
                    TrackId = trackId,
                    Operation = EditOperation.Trim,
                    StartTime = startTime,
                    Duration = duration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses trim edilemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i trim edilemedi", ex);
            }
        }

        /// <summary>
        /// Sessizlikleri temizler;
        /// </summary>
        public void RemoveSilence(string trackId, float threshold = 0.01f, TimeSpan minSilenceDuration = default)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                minSilenceDuration = minSilenceDuration == default ? TimeSpan.FromMilliseconds(100) : minSilenceDuration;

                _logger.LogInformation("Sessizlik temizleniyor: Track: {TrackId}, Threshold: {Threshold}, MinSilence: {MinSilence}",
                    trackId, threshold, minSilenceDuration);

                track.RemoveSilence(threshold, minSilenceDuration);

                UpdateTotalDuration();

                _logger.LogInformation("Sessizlik temizlendi: Track: {TrackId}", trackId);

                OnAudioEdited(new AudioEditedEventArgs;
                {
                    TrackId = trackId,
                    Operation = EditOperation.RemoveSilence,
                    StartTime = TimeSpan.Zero,
                    Duration = track.Duration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sessizlik temizlenemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'indeki sessizlikler temizlenemedi", ex);
            }
        }

        /// <summary>
        /// Sesi ters çevirir;
        /// </summary>
        public void Reverse(string trackId)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogInformation("Ses ters çevriliyor: Track: {TrackId}", trackId);

                track.Reverse();

                _logger.LogInformation("Ses ters çevrildi: Track: {TrackId}", trackId);

                OnAudioEdited(new AudioEditedEventArgs;
                {
                    TrackId = trackId,
                    Operation = EditOperation.Reverse,
                    StartTime = TimeSpan.Zero,
                    Duration = track.Duration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses ters çevrilemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i ters çevrilemedi", ex);
            }
        }

        /// <summary>
        /// İki ses parçasının yerini değiştirir;
        /// </summary>
        public void Swap(string trackId, TimeSpan firstStart, TimeSpan firstDuration, TimeSpan secondStart, TimeSpan secondDuration)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogInformation("Ses parçaları değiştiriliyor: Track: {TrackId}", trackId);

                track.Swap(firstStart, firstDuration, secondStart, secondDuration);

                _logger.LogInformation("Ses parçaları değiştirildi: Track: {TrackId}", trackId);

                OnAudioEdited(new AudioEditedEventArgs;
                {
                    TrackId = trackId,
                    Operation = EditOperation.Swap,
                    StartTime = firstStart,
                    Duration = firstDuration + secondDuration;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses parçaları değiştirilemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'indeki parçalar değiştirilemedi", ex);
            }
        }
        #endregion;

        #region Public Methods - Effects;
        /// <summary>
        /// Fade in efekti uygular;
        /// </summary>
        public void ApplyFadeIn(string trackId, TimeSpan fadeDuration)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Fade in uygulanıyor: Track: {TrackId}, Duration: {FadeDuration}",
                    trackId, fadeDuration);

                track.ApplyFadeIn(fadeDuration);

                _logger.LogDebug("Fade in uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "FadeIn",
                    EffectName = $"FadeIn_{fadeDuration}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fade in uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine fade in uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Fade out efekti uygular;
        /// </summary>
        public void ApplyFadeOut(string trackId, TimeSpan fadeDuration)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Fade out uygulanıyor: Track: {TrackId}, Duration: {FadeDuration}",
                    trackId, fadeDuration);

                track.ApplyFadeOut(fadeDuration);

                _logger.LogDebug("Fade out uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "FadeOut",
                    EffectName = $"FadeOut_{fadeDuration}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fade out uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine fade out uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Normalizasyon uygular;
        /// </summary>
        public void Normalize(string trackId, float targetLevel = 0.0f)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Normalizasyon uygulanıyor: Track: {TrackId}, TargetLevel: {TargetLevel}",
                    trackId, targetLevel);

                track.Normalize(targetLevel);

                _logger.LogDebug("Normalizasyon uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "Normalize",
                    EffectName = $"Normalize_{targetLevel}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Normalizasyon uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i normalize edilemedi", ex);
            }
        }

        /// <summary>
        /// Equalizer uygular;
        /// </summary>
        public void ApplyEqualizer(string trackId, EqualizerBand[] bands)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            if (bands == null || bands.Length == 0)
                throw new ArgumentException("Equalizer bantları boş olamaz", nameof(bands));

            try
            {
                _logger.LogDebug("Equalizer uygulanıyor: Track: {TrackId}, Bands: {BandCount}",
                    trackId, bands.Length);

                track.ApplyEqualizer(bands);

                _logger.LogDebug("Equalizer uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "Equalizer",
                    EffectName = $"Equalizer_{bands.Length}bands"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Equalizer uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine equalizer uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Reverb efekti uygular;
        /// </summary>
        public void ApplyReverb(string trackId, ReverbSettings settings = null)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                settings ??= new ReverbSettings();

                _logger.LogDebug("Reverb uygulanıyor: Track: {TrackId}, Mix: {Mix}, Decay: {Decay}",
                    trackId, settings.Mix, settings.Decay);

                track.ApplyReverb(settings);

                _logger.LogDebug("Reverb uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "Reverb",
                    EffectName = $"Reverb_{settings.Mix}_{settings.Decay}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reverb uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine reverb uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Delay efekti uygular;
        /// </summary>
        public void ApplyDelay(string trackId, DelaySettings settings = null)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                settings ??= new DelaySettings();

                _logger.LogDebug("Delay uygulanıyor: Track: {TrackId}, Time: {DelayTime}, Feedback: {Feedback}",
                    trackId, settings.DelayTime, settings.Feedback);

                track.ApplyDelay(settings);

                _logger.LogDebug("Delay uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "Delay",
                    EffectName = $"Delay_{settings.DelayTime}_{settings.Feedback}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delay uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine delay uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Chorus efekti uygular;
        /// </summary>
        public void ApplyChorus(string trackId, ChorusSettings settings = null)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                settings ??= new ChorusSettings();

                _logger.LogDebug("Chorus uygulanıyor: Track: {TrackId}, Rate: {Rate}, Depth: {Depth}",
                    trackId, settings.Rate, settings.Depth);

                track.ApplyChorus(settings);

                _logger.LogDebug("Chorus uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "Chorus",
                    EffectName = $"Chorus_{settings.Rate}_{settings.Depth}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chorus uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine chorus uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Kompresör uygular;
        /// </summary>
        public void ApplyCompressor(string trackId, CompressorSettings settings = null)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                settings ??= new CompressorSettings();

                _logger.LogDebug("Kompresör uygulanıyor: Track: {TrackId}, Threshold: {Threshold}, Ratio: {Ratio}",
                    trackId, settings.Threshold, settings.Ratio);

                track.ApplyCompressor(settings);

                _logger.LogDebug("Kompresör uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "Compressor",
                    EffectName = $"Compressor_{settings.Threshold}_{settings.Ratio}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kompresör uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine kompresör uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Pitch değişikliği uygular;
        /// </summary>
        public void ChangePitch(string trackId, float semitones)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Pitch değiştiriliyor: Track: {TrackId}, Semitones: {Semitones}",
                    trackId, semitones);

                track.ChangePitch(semitones);

                _logger.LogDebug("Pitch değiştirildi: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "PitchShift",
                    EffectName = $"PitchShift_{semitones}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pitch değiştirilemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inin pitch'i değiştirilemedi", ex);
            }
        }

        /// <summary>
        /// Tempo değişikliği uygular;
        /// </summary>
        public void ChangeTempo(string trackId, float tempoRatio)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Tempo değiştiriliyor: Track: {TrackId}, Ratio: {TempoRatio}",
                    trackId, tempoRatio);

                track.ChangeTempo(tempoRatio);

                _logger.LogDebug("Tempo değiştirildi: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "TempoChange",
                    EffectName = $"TempoChange_{tempoRatio}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tempo değiştirilemedi: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inin tempo'su değiştirilemedi", ex);
            }
        }

        /// <summary>
        /// Özel efekt uygular;
        /// </summary>
        public void ApplyCustomEffect(string trackId, AudioEffect effect)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            if (effect == null)
                throw new ArgumentNullException(nameof(effect));

            try
            {
                _logger.LogDebug("Özel efekt uygulanıyor: Track: {TrackId}, Effect: {EffectName}",
                    trackId, effect.Name);

                track.ApplyEffect(effect);

                _logger.LogDebug("Özel efekt uygulandı: Track: {TrackId}", trackId);

                OnEffectApplied(new EffectAppliedEventArgs;
                {
                    TrackId = trackId,
                    EffectType = "Custom",
                    EffectName = effect.Name;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Özel efekt uygulanamadı: Track: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'ine özel efekt uygulanamadı", ex);
            }
        }
        #endregion;

        #region Public Methods - Track Management;
        /// <summary>
        /// Track ekler;
        /// </summary>
        public AudioTrack AddTrack(string name = null)
        {
            try
            {
                var trackId = Guid.NewGuid().ToString();
                name ??= $"Track_{_audioTracks.Count + 1}";

                var track = new AudioTrack(trackId, null)
                {
                    Name = name,
                    Volume = 1.0f,
                    Pan = 0.0f,
                    IsMuted = false,
                    IsSolo = false;
                };

                lock (_lockObject)
                {
                    _audioTracks[trackId] = track;

                    if (ActiveProject != null)
                    {
                        ActiveProject.Tracks.Add(track);
                        ActiveProject.ModifiedDate = DateTime.UtcNow;
                    }
                }

                _logger.LogInformation("Track eklendi: {TrackName}, ID: {TrackId}", name, trackId);

                return track;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track eklenemedi");
                throw new AudioException("Track eklenemedi", ex);
            }
        }

        /// <summary>
        /// Track'i kaldırır;
        /// </summary>
        public void RemoveTrack(string trackId)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                return;

            try
            {
                lock (_lockObject)
                {
                    track.Dispose();
                    _audioTracks.Remove(trackId);

                    if (ActiveProject != null)
                    {
                        ActiveProject.Tracks.Remove(track);
                        ActiveProject.ModifiedDate = DateTime.UtcNow;
                    }
                }

                UpdateTotalDuration();

                _logger.LogInformation("Track kaldırıldı: {TrackId}", trackId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track kaldırılamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i kaldırılamadı", ex);
            }
        }

        /// <summary>
        /// Track'in ses seviyesini ayarlar;
        /// </summary>
        public void SetTrackVolume(string trackId, float volume)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                volume = Math.Clamp(volume, 0.0f, 1.0f);
                track.Volume = volume;

                _logger.LogDebug("Track ses seviyesi ayarlandı: {TrackId}, Volume: {Volume}",
                    trackId, volume);

                OnVolumeChanged(new AudioVolumeChangedEventArgs;
                {
                    Channel = trackId,
                    Volume = volume,
                    IsMuted = track.IsMuted;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track ses seviyesi ayarlanamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inin ses seviyesi ayarlanamadı", ex);
            }
        }

        /// <summary>
        /// Track'in pan ayarını yapar;
        /// </summary>
        public void SetTrackPan(string trackId, float pan)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                pan = Math.Clamp(pan, -1.0f, 1.0f);
                track.Pan = pan;

                _logger.LogDebug("Track pan ayarlandı: {TrackId}, Pan: {Pan}", trackId, pan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track pan ayarlanamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inin pan'i ayarlanamadı", ex);
            }
        }

        /// <summary>
        /// Track'i susturur/açarlar;
        /// </summary>
        public void MuteTrack(string trackId, bool mute)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                track.IsMuted = mute;

                _logger.LogDebug("Track mute ayarlandı: {TrackId}, Mute: {Mute}", trackId, mute);

                OnVolumeChanged(new AudioVolumeChangedEventArgs;
                {
                    Channel = trackId,
                    Volume = track.Volume,
                    IsMuted = mute;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track mute ayarlanamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i mute edilemedi/açılamadı", ex);
            }
        }

        /// <summary>
        /// Track'i solo yapar;
        /// </summary>
        public void SoloTrack(string trackId, bool solo)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                track.IsSolo = solo;

                _logger.LogDebug("Track solo ayarlandı: {TrackId}, Solo: {Solo}", trackId, solo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track solo ayarlanamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i solo yapılamadı/çıkarılamadı", ex);
            }
        }

        /// <summary>
        /// Track'i taşır;
        /// </summary>
        public void MoveTrack(string trackId, TimeSpan newStartTime)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                track.StartTime = newStartTime;

                UpdateTotalDuration();

                _logger.LogDebug("Track taşındı: {TrackId}, NewStartTime: {NewStartTime}",
                    trackId, newStartTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track taşınamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i taşınamadı", ex);
            }
        }
        #endregion;

        #region Public Methods - Export;
        /// <summary>
        /// Ses projesini dışa aktarır;
        /// </summary>
        public async Task ExportAsync(string outputFilePath, ExportSettings settings = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("Çıktı dosya yolu boş olamaz", nameof(outputFilePath));

            try
            {
                settings ??= new ExportSettings();

                _logger.LogInformation("Ses dışa aktarılıyor: {OutputFilePath}, Format: {Format}, SampleRate: {SampleRate}",
                    outputFilePath, settings.Format, settings.SampleRate);

                // Tüm track'leri mix et;
                var mixedAudio = MixAllTracks();

                // Format'a göre kaydet;
                switch (settings.Format)
                {
                    case AudioFormat.WAV:
                        await ExportAsWavAsync(mixedAudio, outputFilePath, settings, cancellationToken);
                        break;
                    case AudioFormat.MP3:
                        await ExportAsMp3Async(mixedAudio, outputFilePath, settings, cancellationToken);
                        break;
                    case AudioFormat.FLAC:
                        await ExportAsFlacAsync(mixedAudio, outputFilePath, settings, cancellationToken);
                        break;
                    case AudioFormat.OGG:
                        await ExportAsOggAsync(mixedAudio, outputFilePath, settings, cancellationToken);
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen format: {settings.Format}");
                }

                _logger.LogInformation("Ses dışa aktarıldı: {OutputFilePath}, Size: {FileSize}",
                    outputFilePath, new FileInfo(outputFilePath).Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses dışa aktarılamadı: {OutputFilePath}", outputFilePath);
                throw new AudioException($"Ses '{outputFilePath}' dosyasına aktarılamadı", ex);
            }
        }

        /// <summary>
        /// Belirli bir track'i dışa aktarır;
        /// </summary>
        public async Task ExportTrackAsync(string trackId, string outputFilePath, ExportSettings settings = null, CancellationToken cancellationToken = default)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("Çıktı dosya yolu boş olamaz", nameof(outputFilePath));

            try
            {
                settings ??= new ExportSettings();

                _logger.LogInformation("Track dışa aktarılıyor: {TrackId} -> {OutputFilePath}",
                    trackId, outputFilePath);

                var audioData = track.GetAudioData();

                // Format'a göre kaydet;
                switch (settings.Format)
                {
                    case AudioFormat.WAV:
                        await ExportAsWavAsync(audioData, outputFilePath, settings, cancellationToken);
                        break;
                    case AudioFormat.MP3:
                        await ExportAsMp3Async(audioData, outputFilePath, settings, cancellationToken);
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen format: {settings.Format}");
                }

                _logger.LogInformation("Track dışa aktarıldı: {TrackId} -> {OutputFilePath}",
                    trackId, outputFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track dışa aktarılamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'i dışa aktarılamadı", ex);
            }
        }

        /// <summary>
        /// Ses parçasını dışa aktarır;
        /// </summary>
        public async Task ExportClipAsync(AudioClip clip, string outputFilePath, ExportSettings settings = null, CancellationToken cancellationToken = default)
        {
            if (clip == null)
                throw new ArgumentNullException(nameof(clip));

            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("Çıktı dosya yolu boş olamaz", nameof(outputFilePath));

            try
            {
                settings ??= new ExportSettings();

                _logger.LogInformation("Clip dışa aktarılıyor: {ClipId} -> {OutputFilePath}",
                    clip.Id, outputFilePath);

                // Clip'i WAV olarak kaydet;
                using var writer = new WaveFileWriter(outputFilePath, clip.WaveFormat);
                writer.Write(clip.AudioData, 0, clip.AudioData.Length);
                await writer.FlushAsync(cancellationToken);

                _logger.LogInformation("Clip dışa aktarıldı: {ClipId} -> {OutputFilePath}",
                    clip.Id, outputFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Clip dışa aktarılamadı: {ClipId}", clip.Id);
                throw new AudioException($"'{clip.Id}' clip'i dışa aktarılamadı", ex);
            }
        }
        #endregion;

        #region Public Methods - Analysis;
        /// <summary>
        /// Ses analizi yapar;
        /// </summary>
        public AudioAnalysis AnalyzeAudio(string trackId)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Ses analizi yapılıyor: {TrackId}", trackId);

                var analysis = track.Analyze();

                _logger.LogDebug("Ses analizi tamamlandı: {TrackId}, RMS: {RMS}, Peak: {Peak}",
                    trackId, analysis.RMS, analysis.PeakLevel);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses analizi yapılamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inin analizi yapılamadı", ex);
            }
        }

        /// <summary>
        /// Spektogram analizi yapar;
        /// </summary>
        public SpectrumAnalysis AnalyzeSpectrum(string trackId, int fftSize = 2048)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Spektogram analizi yapılıyor: {TrackId}, FFT Size: {FftSize}",
                    trackId, fftSize);

                var analysis = track.AnalyzeSpectrum(fftSize);

                _logger.LogDebug("Spektogram analizi tamamlandı: {TrackId}, Bins: {BinCount}",
                    trackId, analysis.FrequencyBins?.Length ?? 0);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spektogram analizi yapılamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inin spektogram analizi yapılamadı", ex);
            }
        }

        /// <summary>
        /// Ses dalga formunu alır;
        /// </summary>
        public float[] GetWaveform(string trackId, int sampleCount = 1000)
        {
            if (!_audioTracks.TryGetValue(trackId, out var track))
                throw new AudioException($"Track bulunamadı: {trackId}");

            try
            {
                _logger.LogDebug("Dalga formu alınıyor: {TrackId}, SampleCount: {SampleCount}",
                    trackId, sampleCount);

                var waveform = track.GetWaveform(sampleCount);

                _logger.LogDebug("Dalga formu alındı: {TrackId}, Points: {PointCount}",
                    trackId, waveform.Length);

                return waveform;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dalga formu alınamadı: {TrackId}", trackId);
                throw new AudioException($"'{trackId}' track'inin dalga formu alınamadı", ex);
            }
        }
        #endregion;

        #region Private Methods;
        private void InitializeAudioEngine()
        {
            try
            {
                // Varsayılan değerleri konfigürasyondan al;
                if (_configuration != null)
                {
                    _masterVolume = _configuration.DefaultMasterVolume;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses motoru başlatılamadı");
                throw new AudioException("Ses motoru başlatılamadı", ex);
            }
        }

        private void InitializeBuiltInEffects()
        {
            try
            {
                // Built-in efektleri yükle;
                var effects = new List<AudioEffect>
                {
                    new AudioEffect { Name = "FadeIn", Type = EffectType.Fade, Category = EffectCategory.Volume },
                    new AudioEffect { Name = "FadeOut", Type = EffectType.Fade, Category = EffectCategory.Volume },
                    new AudioEffect { Name = "Normalize", Type = EffectType.Normalize, Category = EffectCategory.Volume },
                    new AudioEffect { Name = "Equalizer", Type = EffectType.Equalizer, Category = EffectCategory.Frequency },
                    new AudioEffect { Name = "Reverb", Type = EffectType.Reverb, Category = EffectCategory.Space },
                    new AudioEffect { Name = "Delay", Type = EffectType.Delay, Category = EffectCategory.Time },
                    new AudioEffect { Name = "Chorus", Type = EffectType.Chorus, Category = EffectCategory.Modulation },
                    new AudioEffect { Name = "Compressor", Type = EffectType.Compressor, Category = EffectCategory.Dynamics },
                    new AudioEffect { Name = "PitchShift", Type = EffectType.PitchShift, Category = EffectCategory.Pitch },
                    new AudioEffect { Name = "TempoChange", Type = EffectType.TempoChange, Category = EffectCategory.Time }
                };

                foreach (var effect in effects)
                {
                    _effects[effect.Name] = effect;
                }

                _logger.LogDebug("Built-in efektler yüklendi: {EffectCount} efekt", _effects.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Built-in efektler yüklenemedi");
                // Kritik hata değil, devam et;
            }
        }

        private void UpdateMasterVolume()
        {
            try
            {
                if (_masterMixer != null)
                {
                    // Volume güncelleme işlemi;
                    // Gerçek implementasyonda VolumeSampleProvider kullanılabilir;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Master volume güncellenemedi");
            }
        }

        private void UpdateTotalDuration()
        {
            try
            {
                lock (_lockObject)
                {
                    _totalDuration = _audioTracks.Values;
                        .Select(t => t.StartTime + t.Duration)
                        .DefaultIfEmpty(TimeSpan.Zero)
                        .Max();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Toplam süre güncellenemedi");
            }
        }

        private ISampleProvider MixAllTracks()
        {
            try
            {
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(DefaultSampleRate, DefaultChannelCount);
                var mixer = new MixingSampleProvider(waveFormat) { ReadFully = true };

                foreach (var track in _audioTracks.Values)
                {
                    if (!track.IsMuted)
                    {
                        var provider = track.GetSampleProvider();
                        if (provider != null)
                        {
                            mixer.AddMixerInput(provider);
                        }
                    }
                }

                return mixer;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Track'ler mixlenemedi");
                throw new AudioException("Track'ler mixlenemedi", ex);
            }
        }

        private async Task ExportAsWavAsync(ISampleProvider audio, string filePath, ExportSettings settings, CancellationToken cancellationToken)
        {
            try
            {
                using var writer = new WaveFileWriter(filePath, audio.WaveFormat);

                var buffer = new float[settings.SampleRate * audio.WaveFormat.Channels];
                int samplesRead;

                do;
                {
                    samplesRead = audio.Read(buffer, 0, buffer.Length);
                    if (samplesRead > 0)
                    {
                        writer.WriteSamples(buffer, 0, samplesRead);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        break;

                } while (samplesRead > 0);

                await writer.FlushAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WAV export başarısız: {FilePath}", filePath);
                throw;
            }
        }

        private async Task ExportAsMp3Async(ISampleProvider audio, string filePath, ExportSettings settings, CancellationToken cancellationToken)
        {
            // MP3 export için Lame veya benzeri encoder gerekli;
            // Bu örnekte basit bir placeholder;
            _logger.LogWarning("MP3 export henüz implemente edilmedi, WAV olarak kaydediliyor");
            await ExportAsWavAsync(audio, filePath, settings, cancellationToken);
        }

        private async Task ExportAsFlacAsync(ISampleProvider audio, string filePath, ExportSettings settings, CancellationToken cancellationToken)
        {
            // FLAC export için NAudio.Flac veya benzeri gerekli;
            _logger.LogWarning("FLAC export henüz implemente edilmedi, WAV olarak kaydediliyor");
            await ExportAsWavAsync(audio, filePath, settings, cancellationToken);
        }

        private async Task ExportAsOggAsync(ISampleProvider audio, string filePath, ExportSettings settings, CancellationToken cancellationToken)
        {
            // OGG export için NVorbis veya benzeri gerekli;
            _logger.LogWarning("OGG export henüz implemente edilmedi, WAV olarak kaydediliyor");
            await ExportAsWavAsync(audio, filePath, settings, cancellationToken);
        }

        private void OnPlaybackStarted(PlaybackStartedEventArgs e)
        {
            PlaybackStarted?.Invoke(this, e);
        }

        private void OnPlaybackStopped(PlaybackStoppedEventArgs e)
        {
            PlaybackStopped?.Invoke(this, e);
        }

        private void OnPlaybackPositionChanged(PlaybackPositionChangedEventArgs e)
        {
            PlaybackPositionChanged?.Invoke(this, e);
        }

        private void OnAudioLoaded(AudioLoadedEventArgs e)
        {
            AudioLoaded?.Invoke(this, e);
        }

        private void OnAudioRecorded(AudioRecordedEventArgs e)
        {
            AudioRecorded?.Invoke(this, e);
        }

        private void OnEffectApplied(EffectAppliedEventArgs e)
        {
            EffectApplied?.Invoke(this, e);
        }

        private void OnAudioEdited(AudioEditedEventArgs e)
        {
            AudioEdited?.Invoke(this, e);
        }

        private void OnProjectSaved(ProjectSavedEventArgs e)
        {
            ProjectSaved?.Invoke(this, e);
        }

        private void OnErrorOccurred(AudioErrorEventArgs e)
        {
            ErrorOccurred?.Invoke(this, e);
        }

        private void OnVolumeChanged(AudioVolumeChangedEventArgs e)
        {
            VolumeChanged?.Invoke(this, e);
        }
        #endregion;

        #region IDisposable Implementation;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopPlayback();
                    CloseProject();

                    _outputDevice?.Dispose();

                    foreach (var track in _audioTracks.Values)
                    {
                        track.Dispose();
                    }
                    _audioTracks.Clear();

                    foreach (var project in _projects.Values)
                    {
                        project.Dispose();
                    }
                    _projects.Clear();
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

        #region Nested Types;
        /// <summary>
        /// Ses motoru konfigürasyonu;
        /// </summary>
        public class AudioEngineConfiguration;
        {
            public float DefaultMasterVolume { get; set; } = 1.0f;
            public int DefaultBufferSize { get; set; } = 4096;
            public int DefaultLatency { get; set; } = 100; // ms;
            public bool EnableEffects { get; set; } = true;
            public bool EnableRecording { get; set; } = true;
            public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Ses projesi;
        /// </summary>
        public class AudioProject : IDisposable
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string FilePath { get; set; }
            public DateTime CreatedDate { get; set; }
            public DateTime ModifiedDate { get; set; }
            public ProjectSettings Settings { get; set; }
            public List<AudioTrack> Tracks { get; set; }
            public List<AudioMarker> Markers { get; set; }
            public TimeSpan Duration => Tracks?.Select(t => t.StartTime + t.Duration).DefaultIfEmpty(TimeSpan.Zero).Max() ?? TimeSpan.Zero;

            public void Dispose()
            {
                if (Tracks != null)
                {
                    foreach (var track in Tracks)
                    {
                        track.Dispose();
                    }
                    Tracks.Clear();
                }
            }
        }

        /// <summary>
        /// Proje ayarları;
        /// </summary>
        public class ProjectSettings;
        {
            public int SampleRate { get; set; } = 44100;
            public int BitDepth { get; set; } = 16;
            public int ChannelCount { get; set; } = 2;
            public float Tempo { get; set; } = 120.0f;
            public TimeSignature TimeSignature { get; set; } = new TimeSignature(4, 4);
            public Key Key { get; set; } = Key.CMajor;
        }

        /// <summary>
        /// Ses track'i;
        /// </summary>
        public class AudioTrack : IDisposable
        {
            public string Id { get; }
            public string Name { get; set; }
            public string SourceFilePath { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan Duration { get; private set; }
            public int SampleRate { get; private set; }
            public int Channels { get; private set; }
            public float Volume { get; set; } = 1.0f;
            public float Pan { get; set; } = 0.0f;
            public bool IsMuted { get; set; }
            public bool IsSolo { get; set; }
            public List<AudioEffect> Effects { get; } = new List<AudioEffect>();

            private IWaveProvider _waveProvider;
            private List<AudioClip> _clips = new List<AudioClip>();
            private readonly object _lockObject = new object();

            public AudioTrack(string id, IWaveProvider waveProvider)
            {
                Id = id;
                _waveProvider = waveProvider;

                if (waveProvider != null)
                {
                    Duration = waveProvider.TotalTime;
                    SampleRate = waveProvider.WaveFormat.SampleRate;
                    Channels = waveProvider.WaveFormat.Channels;
                }
            }

            public ISampleProvider GetSampleProvider()
            {
                lock (_lockObject)
                {
                    if (_waveProvider == null)
                        return null;

                    var sampleProvider = _waveProvider.ToSampleProvider();

                    // Efektleri uygula;
                    foreach (var effect in Effects)
                    {
                        sampleProvider = effect.Apply(sampleProvider);
                    }

                    // Volume ve pan uygula;
                    if (Volume != 1.0f)
                    {
                        sampleProvider = new VolumeSampleProvider(sampleProvider) { Volume = Volume };
                    }

                    if (Pan != 0.0f)
                    {
                        sampleProvider = new PanningSampleProvider(sampleProvider) { Pan = Pan };
                    }

                    return sampleProvider;
                }
            }

            public byte[] GetAudioData()
            {
                lock (_lockObject)
                {
                    if (_waveProvider == null)
                        return Array.Empty<byte>();

                    using var memoryStream = new MemoryStream();
                    using var writer = new WaveFileWriter(memoryStream, _waveProvider.WaveFormat);

                    var buffer = new byte[_waveProvider.WaveFormat.AverageBytesPerSecond];
                    int bytesRead;

                    while ((bytesRead = _waveProvider.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                    }

                    writer.Flush();
                    return memoryStream.ToArray();
                }
            }

            public AudioClip Cut(TimeSpan startTime, TimeSpan duration)
            {
                lock (_lockObject)
                {
                    // Ses kesme işlemi;
                    var clip = new AudioClip;
                    {
                        Id = Guid.NewGuid().ToString(),
                        StartTime = startTime,
                        Duration = duration,
                        WaveFormat = _waveProvider?.WaveFormat;
                    };

                    // Gerçek implementasyonda ses verisini kesme işlemi;
                    return clip;
                }
            }

            public AudioClip Copy(TimeSpan startTime, TimeSpan duration)
            {
                return Cut(startTime, duration); // Copy, cut gibi çalışır ama orijinali silmez;
            }

            public void Paste(AudioClip clip, TimeSpan position)
            {
                lock (_lockObject)
                {
                    // Ses yapıştırma işlemi;
                    // Gerçek implementasyonda ses verisini birleştirme;
                }
            }

            public void Delete(TimeSpan startTime, TimeSpan duration)
            {
                lock (_lockObject)
                {
                    // Ses silme işlemi;
                }
            }

            public void Clear()
            {
                lock (_lockObject)
                {
                    _waveProvider = null;
                    _clips.Clear();
                    Duration = TimeSpan.Zero;
                }
            }

            public void Seek(TimeSpan position)
            {
                lock (_lockObject)
                {
                    // Pozisyon atlama;
                    if (_waveProvider is AudioFileReader audioFile)
                    {
                        audioFile.CurrentTime = position;
                    }
                }
            }

            public void RemoveSilence(float threshold, TimeSpan minSilenceDuration)
            {
                lock (_lockObject)
                {
                    // Sessizlik temizleme algoritması;
                }
            }

            public void Reverse()
            {
                lock (_lockObject)
                {
                    // Ters çevirme işlemi;
                }
            }

            public void Swap(TimeSpan firstStart, TimeSpan firstDuration, TimeSpan secondStart, TimeSpan secondDuration)
            {
                lock (_lockObject)
                {
                    // Yer değiştirme işlemi;
                }
            }

            public void ApplyFadeIn(TimeSpan fadeDuration)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "FadeIn",
                    Type = EffectType.Fade,
                    Parameters = new Dictionary<string, object> { { "Duration", fadeDuration } }
                });
            }

            public void ApplyFadeOut(TimeSpan fadeDuration)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "FadeOut",
                    Type = EffectType.Fade,
                    Parameters = new Dictionary<string, object> { { "Duration", fadeDuration } }
                });
            }

            public void Normalize(float targetLevel)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "Normalize",
                    Type = EffectType.Normalize,
                    Parameters = new Dictionary<string, object> { { "TargetLevel", targetLevel } }
                });
            }

            public void ApplyEqualizer(EqualizerBand[] bands)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "Equalizer",
                    Type = EffectType.Equalizer,
                    Parameters = new Dictionary<string, object> { { "Bands", bands } }
                });
            }

            public void ApplyReverb(ReverbSettings settings)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "Reverb",
                    Type = EffectType.Reverb,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Mix", settings.Mix },
                        { "Decay", settings.Decay }
                    }
                });
            }

            public void ApplyDelay(DelaySettings settings)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "Delay",
                    Type = EffectType.Delay,
                    Parameters = new Dictionary<string, object>
                    {
                        { "DelayTime", settings.DelayTime },
                        { "Feedback", settings.Feedback }
                    }
                });
            }

            public void ApplyChorus(ChorusSettings settings)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "Chorus",
                    Type = EffectType.Chorus,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Rate", settings.Rate },
                        { "Depth", settings.Depth }
                    }
                });
            }

            public void ApplyCompressor(CompressorSettings settings)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "Compressor",
                    Type = EffectType.Compressor,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Threshold", settings.Threshold },
                        { "Ratio", settings.Ratio }
                    }
                });
            }

            public void ChangePitch(float semitones)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "PitchShift",
                    Type = EffectType.PitchShift,
                    Parameters = new Dictionary<string, object> { { "Semitones", semitones } }
                });
            }

            public void ChangeTempo(float tempoRatio)
            {
                Effects.Add(new AudioEffect;
                {
                    Name = "TempoChange",
                    Type = EffectType.TempoChange,
                    Parameters = new Dictionary<string, object> { { "TempoRatio", tempoRatio } }
                });
            }

            public void ApplyEffect(AudioEffect effect)
            {
                Effects.Add(effect);
            }

            public AudioAnalysis Analyze()
            {
                return new AudioAnalysis;
                {
                    TrackId = Id,
                    Duration = Duration,
                    SampleRate = SampleRate,
                    Channels = Channels;
                };
            }

            public SpectrumAnalysis AnalyzeSpectrum(int fftSize)
            {
                return new SpectrumAnalysis;
                {
                    TrackId = Id,
                    FftSize = fftSize;
                };
            }

            public float[] GetWaveform(int sampleCount)
            {
                // Dalga formu hesaplama;
                return new float[sampleCount];
            }

            public void Dispose()
            {
                if (_waveProvider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                _waveProvider = null;
                _clips.Clear();
            }
        }

        /// <summary>
        /// Ses efekti;
        /// </summary>
        public class AudioEffect;
        {
            public string Name { get; set; }
            public EffectType Type { get; set; }
            public EffectCategory Category { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

            public ISampleProvider Apply(ISampleProvider input)
            {
                // Efekt uygulama işlemi;
                // Gerçek implementasyonda efekt tipine göre işlem yapılır;
                return input;
            }
        }

        /// <summary>
        /// Efekt tipleri;
        /// </summary>
        public enum EffectType;
        {
            Fade,
            Normalize,
            Equalizer,
            Reverb,
            Delay,
            Chorus,
            Compressor,
            PitchShift,
            TempoChange,
            Custom;
        }

        /// <summary>
        /// Efekt kategorileri;
        /// </summary>
        public enum EffectCategory;
        {
            Volume,
            Frequency,
            Time,
            Space,
            Modulation,
            Dynamics,
            Pitch;
        }

        /// <summary>
        /// Ses klibi;
        /// </summary>
        public class AudioClip;
        {
            public string Id { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan Duration { get; set; }
            public WaveFormat WaveFormat { get; set; }
            public byte[] AudioData { get; set; }
        }

        /// <summary>
        /// Ses marker'ı;
        /// </summary>
        public class AudioMarker;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public TimeSpan Position { get; set; }
            public MarkerType Type { get; set; }
            public string Color { get; set; }
            public string Notes { get; set; }
        }

        /// <summary>
        /// Marker tipleri;
        /// </summary>
        public enum MarkerType;
        {
            General,
            Verse,
            Chorus,
            Bridge,
            Drop,
            Breakdown,
            Custom;
        }

        /// <summary>
        /// Kayıt ayarları;
        /// </summary>
        public class RecordingSettings;
        {
            public string DeviceName { get; set; } = "Default";
            public int DeviceIndex { get; set; } = 0;
            public int SampleRate { get; set; } = 44100;
            public int BitDepth { get; set; } = 16;
            public int Channels { get; set; } = 2;
            public int BufferSize { get; set; } = 4096;
        }

        /// <summary>
        /// Dışa aktarma ayarları;
        /// </summary>
        public class ExportSettings;
        {
            public AudioFormat Format { get; set; } = AudioFormat.WAV;
            public int SampleRate { get; set; } = 44100;
            public int BitDepth { get; set; } = 16;
            public int Channels { get; set; } = 2;
            public bool Normalize { get; set; } = true;
            public float TargetLevel { get; set; } = -1.0f; // dB;
            public bool ApplyDithering { get; set; } = true;
            public bool IncludeMarkers { get; set; } = false;
        }

        /// <summary>
        /// Ses formatları;
        /// </summary>
        public enum AudioFormat;
        {
            WAV,
            MP3,
            FLAC,
            OGG,
            AIFF,
            M4A;
        }

        /// <summary>
        /// Equalizer band'ı;
        /// </summary>
        public class EqualizerBand;
        {
            public float Frequency { get; set; }
            public float Gain { get; set; } // dB;
            public float Bandwidth { get; set; } // Octaves;
        }

        /// <summary>
        /// Reverb ayarları;
        /// </summary>
        public class ReverbSettings;
        {
            public float Mix { get; set; } = 0.5f; // 0.0 - 1.0;
            public float Decay { get; set; } = 2.0f; // seconds;
            public float PreDelay { get; set; } = 0.0f; // ms;
            public float RoomSize { get; set; } = 0.7f; // 0.0 - 1.0;
        }

        /// <summary>
        /// Delay ayarları;
        /// </summary>
        public class DelaySettings;
        {
            public TimeSpan DelayTime { get; set; } = TimeSpan.FromMilliseconds(500);
            public float Feedback { get; set; } = 0.5f; // 0.0 - 1.0;
            public float Mix { get; set; } = 0.3f; // 0.0 - 1.0;
            public bool PingPong { get; set; } = false;
        }

        /// <summary>
        /// Chorus ayarları;
        /// </summary>
        public class ChorusSettings;
        {
            public float Rate { get; set; } = 1.0f; // Hz;
            public float Depth { get; set; } = 0.5f; // 0.0 - 1.0;
            public float Mix { get; set; } = 0.5f; // 0.0 - 1.0;
            public float Delay { get; set; } = 10.0f; // ms;
        }

        /// <summary>
        /// Kompresör ayarları;
        /// </summary>
        public class CompressorSettings;
        {
            public float Threshold { get; set; } = -20.0f; // dB;
            public float Ratio { get; set; } = 4.0f; // :1;
            public float Attack { get; set; } = 10.0f; // ms;
            public float Release { get; set; } = 100.0f; // ms;
            public float MakeupGain { get; set; } = 0.0f; // dB;
        }

        /// <summary>
        /// Ses analizi;
        /// </summary>
        public class AudioAnalysis;
        {
            public string TrackId { get; set; }
            public TimeSpan Duration { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public float RMS { get; set; } // Root Mean Square;
            public float PeakLevel { get; set; } // dB;
            public float DynamicRange { get; set; } // dB;
            public float CrestFactor { get; set; } // Peak/RMS ratio;
            public List<float> Spectrum { get; set; }
            public List<TimeSpan> Transients { get; set; }
        }

        /// <summary>
        /// Spektrum analizi;
        /// </summary>
        public class SpectrumAnalysis;
        {
            public string TrackId { get; set; }
            public int FftSize { get; set; }
            public float[] FrequencyBins { get; set; }
            public float[] Magnitudes { get; set; }
            public float[] Phases { get; set; }
            public float SpectralCentroid { get; set; }
            public float SpectralFlatness { get; set; }
            public float SpectralRolloff { get; set; }
        }

        /// <summary>
        /// Düzenleme operasyonları;
        /// </summary>
        public enum EditOperation;
        {
            Cut,
            Copy,
            Paste,
            Delete,
            Trim,
            RemoveSilence,
            Reverse,
            Swap,
            FadeIn,
            FadeOut,
            Normalize;
        }

        // Olay argümanları sınıfları;
        public class PlaybackStartedEventArgs : EventArgs;
        {
            public TimeSpan StartPosition { get; set; }
            public TimeSpan TotalDuration { get; set; }
            public DateTime StartTime { get; } = DateTime.UtcNow;
        }

        public class PlaybackStoppedEventArgs : EventArgs;
        {
            public TimeSpan StopPosition { get; set; }
            public TimeSpan TotalDuration { get; set; }
            public DateTime StopTime { get; } = DateTime.UtcNow;
        }

        public class PlaybackPositionChangedEventArgs : EventArgs;
        {
            public TimeSpan CurrentPosition { get; set; }
            public TimeSpan TotalDuration { get; set; }
            public DateTime UpdateTime { get; } = DateTime.UtcNow;
        }

        public class AudioLoadedEventArgs : EventArgs;
        {
            public string TrackId { get; set; }
            public string FilePath { get; set; }
            public TimeSpan Duration { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public DateTime LoadTime { get; } = DateTime.UtcNow;
        }

        public class AudioRecordedEventArgs : EventArgs;
        {
            public string TrackId { get; set; }
            public TimeSpan Duration { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public DateTime RecordTime { get; } = DateTime.UtcNow;
        }

        public class EffectAppliedEventArgs : EventArgs;
        {
            public string TrackId { get; set; }
            public string EffectType { get; set; }
            public string EffectName { get; set; }
            public DateTime ApplyTime { get; } = DateTime.UtcNow;
        }

        public class AudioEditedEventArgs : EventArgs;
        {
            public string TrackId { get; set; }
            public EditOperation Operation { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan Duration { get; set; }
            public DateTime EditTime { get; } = DateTime.UtcNow;
        }

        public class ProjectSavedEventArgs : EventArgs;
        {
            public string ProjectId { get; set; }
            public string FilePath { get; set; }
            public int TrackCount { get; set; }
            public DateTime SaveTime { get; } = DateTime.UtcNow;
        }

        public class AudioErrorEventArgs : EventArgs;
        {
            public string ErrorType { get; set; }
            public string ErrorMessage { get; set; }
            public Exception Exception { get; set; }
            public DateTime ErrorTime { get; } = DateTime.UtcNow;
        }

        public class AudioVolumeChangedEventArgs : EventArgs;
        {
            public string Channel { get; set; }
            public float Volume { get; set; }
            public bool IsMuted { get; set; }
            public DateTime ChangeTime { get; } = DateTime.UtcNow;
        }
        #endregion;
    }

    #region Supporting Types and Interfaces;
    /// <summary>
    /// Audio editor interface'i;
    /// </summary>
    public interface IAudioEditor : IDisposable
    {
        // Proje yönetimi;
        AudioProject CreateProject(string projectName, ProjectSettings settings = null);
        Task<AudioProject> LoadProjectAsync(string filePath, CancellationToken cancellationToken = default);
        Task SaveProjectAsync(string filePath = null, CancellationToken cancellationToken = default);
        void CloseProject();

        // Ses yükleme;
        Task<AudioTrack> LoadAudioFileAsync(string filePath, string trackId = null, CancellationToken cancellationToken = default);
        AudioTrack LoadAudioFromBytes(byte[] audioData, WaveFormat format, string trackId = null);
        Task<AudioTrack> RecordAudioAsync(TimeSpan duration, RecordingSettings settings = null, CancellationToken cancellationToken = default);

        // Çalma kontrolü;
        void StartPlayback();
        void StopPlayback();
        void PausePlayback();
        void SeekToPosition(TimeSpan position);

        // Düzenleme operasyonları;
        AudioClip Cut(string trackId, TimeSpan startTime, TimeSpan duration);
        AudioClip Copy(string trackId, TimeSpan startTime, TimeSpan duration);
        void Paste(string trackId, AudioClip clip, TimeSpan position);
        void Delete(string trackId, TimeSpan startTime, TimeSpan duration);
        void Trim(string trackId, TimeSpan startTime, TimeSpan endTime);
        void RemoveSilence(string trackId, float threshold = 0.01f, TimeSpan minSilenceDuration = default);
        void Reverse(string trackId);
        void Swap(string trackId, TimeSpan firstStart, TimeSpan firstDuration, TimeSpan secondStart, TimeSpan secondDuration);

        // Efektler;
        void ApplyFadeIn(string trackId, TimeSpan fadeDuration);
        void ApplyFadeOut(string trackId, TimeSpan fadeDuration);
        void Normalize(string trackId, float targetLevel = 0.0f);
        void ApplyEqualizer(string trackId, EqualizerBand[] bands);
        void ApplyReverb(string trackId, ReverbSettings settings = null);
        void ApplyDelay(string trackId, DelaySettings settings = null);
        void ApplyChorus(string trackId, ChorusSettings settings = null);
        void ApplyCompressor(string trackId, CompressorSettings settings = null);
        void ChangePitch(string trackId, float semitones);
        void ChangeTempo(string trackId, float tempoRatio);
        void ApplyCustomEffect(string trackId, AudioEffect effect);

        // Track yönetimi;
        AudioTrack AddTrack(string name = null);
        void RemoveTrack(string trackId);
        void SetTrackVolume(string trackId, float volume);
        void SetTrackPan(string trackId, float pan);
        void MuteTrack(string trackId, bool mute);
        void SoloTrack(string trackId, bool solo);
        void MoveTrack(string trackId, TimeSpan newStartTime);

        // Dışa aktarma;
        Task ExportAsync(string outputFilePath, ExportSettings settings = null, CancellationToken cancellationToken = default);
        Task ExportTrackAsync(string trackId, string outputFilePath, ExportSettings settings = null, CancellationToken cancellationToken = default);
        Task ExportClipAsync(AudioClip clip, string outputFilePath, ExportSettings settings = null, CancellationToken cancellationToken = default);

        // Analiz;
        AudioAnalysis AnalyzeAudio(string trackId);
        SpectrumAnalysis AnalyzeSpectrum(string trackId, int fftSize = 2048);
        float[] GetWaveform(string trackId, int sampleCount = 1000);

        // Properties;
        float MasterVolume { get; set; }
        bool IsPlaying { get; }
        TimeSpan CurrentPosition { get; }
        TimeSpan TotalDuration { get; }
        AudioProject ActiveProject { get; }
        bool IsRecording { get; }
        bool IsLooping { get; set; }
    }

    /// <summary>
    /// Ses istisna sınıfı;
    /// </summary>
    public class AudioException : Exception
    {
        public AudioException(string message) : base(message) { }
        public AudioException(string message, Exception innerException) : base(message, innerException) { }

        public string TrackId { get; set; }
        public string Operation { get; set; }
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
    }

    /// <summary>
    /// Ton (key)
    /// </summary>
    public class Key;
    {
        public string Name { get; set; }
        public bool IsMajor { get; set; }

        public static Key CMajor => new Key { Name = "C", IsMajor = true };
        public static Key AMinor => new Key { Name = "A", IsMajor = false };
    }

    /// <summary>
    /// AudioEditor için extension metotlar;
    /// </summary>
    public static class AudioEditorExtensions;
    {
        /// <summary>
        /// Tüm track'leri susturur;
        /// </summary>
        public static void MuteAllTracks(this IAudioEditor editor)
        {
            // Tüm track'leri mute etme işlemi;
        }

        /// <summary>
        /// Tüm track'lerin ses seviyesini ayarlar;
        /// </summary>
        public static void SetAllTracksVolume(this IAudioEditor editor, float volume)
        {
            // Tüm track'lerin volume'unu ayarlama;
        }

        /// <summary>
        /// Crossfade uygular;
        /// </summary>
        public static void ApplyCrossfade(this IAudioEditor editor, string trackId, TimeSpan fadeInDuration, TimeSpan fadeOutDuration)
        {
            // Crossfade efekti;
        }

        /// <summary>
        /// Beat detection yapar;
        /// </summary>
        public static List<TimeSpan> DetectBeats(this IAudioEditor editor, string trackId, float sensitivity = 0.5f)
        {
            // Beat tespiti;
            return new List<TimeSpan>();
        }

        /// <summary>
        /// Otomatik tuning yapar;
        /// </summary>
        public static void AutoTune(this IAudioEditor editor, string trackId, float strength = 0.7f)
        {
            // Auto-tune efekti;
        }
    }
    #endregion;
}
