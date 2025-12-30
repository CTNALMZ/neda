using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NEDA.Core.Common.Extensions;
using NEDA.Core.ExceptionHandling.ErrorReporting;
using NEDA.Core.Logging;

namespace NEDA.MediaProcessing.AudioProcessing.AudioImplementation;
{
    /// <summary>
    /// Gelişmiş ses yönetim sistemi - Çoklu ses akışı, 3D ses, efektler ve daha fazlası;
    /// </summary>
    public class AudioManager : IAudioManager, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger<AudioManager> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly Dictionary<string, AudioSource> _audioSources;
        private readonly Dictionary<string, AudioMixer> _mixers;
        private readonly Dictionary<string, AudioOutputDevice> _outputDevices;
        private readonly Dictionary<string, AudioCaptureDevice> _captureDevices;
        private readonly AudioEngineConfiguration _configuration;
        private readonly object _lockObject = new object();
        private readonly MMDeviceEnumerator _deviceEnumerator;
        private IWavePlayer _primaryOutputDevice;
        private MixingSampleProvider _masterMixer;
        private bool _isInitialized;
        private bool _disposed;
        private float _masterVolume = 1.0f;
        private bool _masterMuted = false;
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
                    IsMuted = _masterMuted;
                });
            }
        }

        /// <summary>
        /// Ana ses susturulmuş mu?
        /// </summary>
        public bool MasterMuted;
        {
            get => _masterMuted;
            set;
            {
                _masterMuted = value;
                UpdateMasterVolume();
                OnVolumeChanged(new AudioVolumeChangedEventArgs;
                {
                    Channel = "Master",
                    Volume = _masterVolume,
                    IsMuted = _masterMuted;
                });
            }
        }

        /// <summary>
        /// Ses sistemi başlatıldı mı?
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Kayıt yapılıyor mu?
        /// </summary>
        public bool IsRecording { get; private set; }

        /// <summary>
        /// Çalma listesi çalınıyor mu?
        /// </summary>
        public bool IsPlaylistPlaying { get; private set; }

        /// <summary>
        /// Aktif ses kaynaklarının sayısı;
        /// </summary>
        public int ActiveSourceCount => _audioSources.Values.Count(s => s.IsPlaying);

        /// <summary>
        /// Toplam bellek kullanımı (byte)
        /// </summary>
        public long TotalMemoryUsage { get; private set; }

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
        #endregion;

        #region Events;
        /// <summary>
        /// Ses sistemi başlatıldığında tetiklenir;
        /// </summary>
        public event EventHandler<AudioSystemInitializedEventArgs> SystemInitialized;

        /// <summary>
        /// Ses sistemi durdurulduğunda tetiklenir;
        /// </summary>
        public event EventHandler<AudioSystemShutdownEventArgs> SystemShutdown;

        /// <summary>
        /// Ses kaynağı eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AudioSourceAddedEventArgs> SourceAdded;

        /// <summary>
        /// Ses kaynağı kaldırıldığında tetiklenir;
        /// </summary>
        public event EventHandler<AudioSourceRemovedEventArgs> SourceRemoved;

        /// <summary>
        /// Ses kaynağı durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AudioSourceStateChangedEventArgs> SourceStateChanged;

        /// <summary>
        /// Ses seviyesi değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AudioVolumeChangedEventArgs> VolumeChanged;

        /// <summary>
        /// Ses efekti uygulandığında tetiklenir;
        /// </summary>
        public event EventHandler<AudioEffectAppliedEventArgs> EffectApplied;

        /// <summary>
        /// Ses hatası oluştuğunda tetiklenir;
        /// </summary>
        public event EventHandler<AudioErrorEventArgs> ErrorOccurred;
        #endregion;

        #region Constructors;
        /// <summary>
        /// AudioManager sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="errorReporter">Hata raporlama servisi</param>
        public AudioManager(
            ILogger<AudioManager> logger,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _configuration = new AudioEngineConfiguration();

            _audioSources = new Dictionary<string, AudioSource>();
            _mixers = new Dictionary<string, AudioMixer>();
            _outputDevices = new Dictionary<string, AudioOutputDevice>();
            _captureDevices = new Dictionary<string, AudioCaptureDevice>();

            _deviceEnumerator = new MMDeviceEnumerator();

            InitializeDefaults();

            _logger.LogInformation("AudioManager başlatıldı. Varsayılan konfigürasyon uygulandı.");
        }

        /// <summary>
        /// Özel konfigürasyon ile AudioManager sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public AudioManager(
            ILogger<AudioManager> logger,
            IErrorReporter errorReporter,
            AudioEngineConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _audioSources = new Dictionary<string, AudioSource>();
            _mixers = new Dictionary<string, AudioMixer>();
            _outputDevices = new Dictionary<string, AudioOutputDevice>();
            _captureDevices = new Dictionary<string, AudioCaptureDevice>();

            _deviceEnumerator = new MMDeviceEnumerator();

            InitializeDefaults();

            _logger.LogInformation("AudioManager başlatıldı. Özel konfigürasyon uygulandı.");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Ses sistemini başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("Ses sistemi zaten başlatılmış");
                return;
            }

            try
            {
                _logger.LogInformation("Ses sistemi başlatılıyor...");

                // Ses cihazlarını tara;
                await ScanAudioDevicesAsync(cancellationToken);

                // Ana çıkış cihazını başlat;
                InitializePrimaryOutputDevice();

                // Master mikseri oluştur;
                InitializeMasterMixer();

                // Varsayılan mikserleri oluştur;
                InitializeDefaultMixers();

                _isInitialized = true;

                _logger.LogInformation("Ses sistemi başarıyla başlatıldı. Cihazlar: {OutputCount} çıkış, {InputCount} giriş",
                    _outputDevices.Count, _captureDevices.Count);

                OnSystemInitialized(new AudioSystemInitializedEventArgs;
                {
                    OutputDeviceCount = _outputDevices.Count,
                    InputDeviceCount = _captureDevices.Count,
                    DefaultSampleRate = DefaultSampleRate,
                    DefaultChannels = DefaultChannelCount;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses sistemi başlatılamadı");
                throw new AudioException("Ses sistemi başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Ses sistemini durdurur;
        /// </summary>
        public void Shutdown()
        {
            if (!_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Ses sistemi durduruluyor...");

                // Tüm ses kaynaklarını durdur;
                StopAllSources();

                // Tüm cihazları kapat;
                _primaryOutputDevice?.Stop();
                _primaryOutputDevice?.Dispose();
                _primaryOutputDevice = null;

                // Tüm kaynakları temizle;
                lock (_lockObject)
                {
                    foreach (var source in _audioSources.Values)
                    {
                        source.Dispose();
                    }
                    _audioSources.Clear();

                    foreach (var mixer in _mixers.Values)
                    {
                        mixer.Dispose();
                    }
                    _mixers.Clear();
                }

                _isInitialized = false;

                _logger.LogInformation("Ses sistemi başarıyla durduruldu");

                OnSystemShutdown(new AudioSystemShutdownEventArgs;
                {
                    WasRecording = IsRecording,
                    ActiveSourcesStopped = _audioSources.Count;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses sistemi durdurulamadı");
                throw new AudioException("Ses sistemi durdurulamadı", ex);
            }
        }

        /// <summary>
        /// Ses dosyası yükler;
        /// </summary>
        public async Task<AudioSource> LoadAudioAsync(string filePath, string sourceId = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
                throw new AudioException("Ses sistemi başlatılmamış");

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Dosya yolu boş olamaz", nameof(filePath));

            try
            {
                sourceId ??= GenerateSourceId("file");

                _logger.LogInformation("Ses dosyası yükleniyor: {FilePath}, SourceId={SourceId}",
                    filePath, sourceId);

                var audioFile = new AudioFileReader(filePath);
                var source = new AudioSource(sourceId, audioFile)
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(filePath),
                    FilePath = filePath,
                    Duration = audioFile.TotalTime,
                    SampleRate = audioFile.WaveFormat.SampleRate,
                    Channels = audioFile.WaveFormat.Channels;
                };

                lock (_lockObject)
                {
                    if (_audioSources.ContainsKey(sourceId))
                    {
                        _logger.LogWarning("SourceId zaten mevcut: {SourceId}, üzerine yazılıyor", sourceId);
                        _audioSources[sourceId].Dispose();
                    }

                    _audioSources[sourceId] = source;
                }

                // Bellek kullanımını güncelle;
                UpdateMemoryUsage();

                _logger.LogInformation("Ses dosyası başarıyla yüklendi: {FilePath}, Duration={Duration}, Format={SampleRate}Hz/{Channels}ch",
                    filePath, source.Duration, source.SampleRate, source.Channels);

                OnSourceAdded(new AudioSourceAddedEventArgs;
                {
                    SourceId = sourceId,
                    SourceType = AudioSourceType.File,
                    FilePath = filePath,
                    Duration = source.Duration,
                    SampleRate = source.SampleRate;
                });

                return source;
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
        public AudioSource LoadAudioFromBytes(byte[] audioData, WaveFormat format, string sourceId = null)
        {
            if (!_isInitialized)
                throw new AudioException("Ses sistemi başlatılmamış");

            if (audioData == null || audioData.Length == 0)
                throw new ArgumentException("Ses verisi boş olamaz", nameof(audioData));

            try
            {
                sourceId ??= GenerateSourceId("bytes");

                _logger.LogInformation("Byte dizisinden ses yükleniyor: {Length} bytes, SourceId={SourceId}",
                    audioData.Length, sourceId);

                var memoryStream = new System.IO.MemoryStream(audioData);
                var reader = new RawSourceWaveStream(memoryStream, format);
                var source = new AudioSource(sourceId, reader)
                {
                    Name = $"ByteSource_{sourceId}",
                    Duration = reader.TotalTime,
                    SampleRate = format.SampleRate,
                    Channels = format.Channels;
                };

                lock (_lockObject)
                {
                    if (_audioSources.ContainsKey(sourceId))
                    {
                        _logger.LogWarning("SourceId zaten mevcut: {SourceId}, üzerine yazılıyor", sourceId);
                        _audioSources[sourceId].Dispose();
                    }

                    _audioSources[sourceId] = source;
                }

                UpdateMemoryUsage();

                _logger.LogInformation("Byte dizisinden ses başarıyla yüklendi: {Length} bytes, Duration={Duration}",
                    audioData.Length, source.Duration);

                OnSourceAdded(new AudioSourceAddedEventArgs;
                {
                    SourceId = sourceId,
                    SourceType = AudioSourceType.Memory,
                    Duration = source.Duration,
                    SampleRate = source.SampleRate;
                });

                return source;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Byte dizisinden ses yüklenemedi");
                throw new AudioException("Byte dizisinden ses yüklenemedi", ex);
            }
        }

        /// <summary>
        /// Ses kaynağını çalar;
        /// </summary>
        public void PlaySource(string sourceId, bool loop = false, float volume = 1.0f)
        {
            if (!_isInitialized)
                throw new AudioException("Ses sistemi başlatılmamış");

            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");

            try
            {
                lock (_lockObject)
                {
                    if (source.IsPlaying)
                    {
                        _logger.LogWarning("Ses kaynağı zaten çalınıyor: {SourceId}", sourceId);
                        return;
                    }

                    source.Volume = volume;
                    source.Loop = loop;
                    source.Play();

                    // Master mikserine ekle;
                    _masterMixer.AddMixerInput(source.GetSampleProvider());

                    _logger.LogInformation("Ses kaynağı çalınıyor: {SourceId}, Loop={Loop}, Volume={Volume}",
                        sourceId, loop, volume);
                }

                OnSourceStateChanged(new AudioSourceStateChangedEventArgs;
                {
                    SourceId = sourceId,
                    PreviousState = AudioPlaybackState.Stopped,
                    NewState = AudioPlaybackState.Playing,
                    Position = source.Position;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaynağı çalınamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' ses kaynağı çalınamadı", ex);
            }
        }

        /// <summary>
        /// Ses kaynağını durdurur;
        /// </summary>
        public void StopSource(string sourceId)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                return;

            try
            {
                var previousState = source.PlaybackState;

                lock (_lockObject)
                {
                    source.Stop();

                    // Master mikserinden çıkar;
                    var provider = source.GetSampleProvider();
                    // NAudio MixingSampleProvider RemoveMixerInput desteği yok, bu nedenle farklı yaklaşım gerekebilir;
                }

                _logger.LogInformation("Ses kaynağı durduruldu: {SourceId}", sourceId);

                OnSourceStateChanged(new AudioSourceStateChangedEventArgs;
                {
                    SourceId = sourceId,
                    PreviousState = previousState,
                    NewState = AudioPlaybackState.Stopped,
                    Position = source.Position;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaynağı durdurulamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' ses kaynağı durdurulamadı", ex);
            }
        }

        /// <summary>
        /// Tüm ses kaynaklarını durdurur;
        /// </summary>
        public void StopAllSources()
        {
            lock (_lockObject)
            {
                foreach (var source in _audioSources.Values)
                {
                    try
                    {
                        if (source.IsPlaying)
                        {
                            source.Stop();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ses kaynağı durdurulurken hata: {SourceId}", source.Id);
                    }
                }

                _logger.LogInformation("Tüm ses kaynakları durduruldu: {Count} kaynak", _audioSources.Count);
            }
        }

        /// <summary>
        /// Ses kaynağını duraklatır;
        /// </summary>
        public void PauseSource(string sourceId)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");

            try
            {
                var previousState = source.PlaybackState;

                lock (_lockObject)
                {
                    source.Pause();
                }

                _logger.LogInformation("Ses kaynağı duraklatıldı: {SourceId}", sourceId);

                OnSourceStateChanged(new AudioSourceStateChangedEventArgs;
                {
                    SourceId = sourceId,
                    PreviousState = previousState,
                    NewState = AudioPlaybackState.Paused,
                    Position = source.Position;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaynağı duraklatılamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' ses kaynağı duraklatılamadı", ex);
            }
        }

        /// <summary>
        /// Ses kaynağının ses seviyesini ayarlar;
        /// </summary>
        public void SetSourceVolume(string sourceId, float volume)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");

            try
            {
                volume = Math.Clamp(volume, 0.0f, 1.0f);
                source.Volume = volume;

                _logger.LogDebug("Ses kaynağı seviyesi ayarlandı: {SourceId}, Volume={Volume}", sourceId, volume);

                OnVolumeChanged(new AudioVolumeChangedEventArgs;
                {
                    Channel = sourceId,
                    Volume = volume,
                    IsMuted = false;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaynağı seviyesi ayarlanamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' ses kaynağı seviyesi ayarlanamadı", ex);
            }
        }

        /// <summary>
        /// Ses kaynağına efekt uygular;
        /// </summary>
        public void ApplyEffectToSource(string sourceId, IAudioEffect effect)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");

            try
            {
                source.ApplyEffect(effect);

                _logger.LogInformation("Ses kaynağına efekt uygulandı: {SourceId}, Effect={EffectType}",
                    sourceId, effect.GetType().Name);

                OnEffectApplied(new AudioEffectAppliedEventArgs;
                {
                    SourceId = sourceId,
                    EffectType = effect.GetType().Name,
                    EffectName = effect.Name;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaynağına efekt uygulanamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' ses kaynağına efekt uygulanamadı", ex);
            }
        }

        /// <summary>
        /// Ses kaynağından efekti kaldırır;
        /// </summary>
        public void RemoveEffectFromSource(string sourceId, string effectName)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");

            try
            {
                source.RemoveEffect(effectName);

                _logger.LogInformation("Ses kaynağından efekt kaldırıldı: {SourceId}, Effect={EffectName}",
                    sourceId, effectName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaynağından efekt kaldırılamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' ses kaynağından efekt kaldırılamadı", ex);
            }
        }

        /// <summary>
        /// Mikser oluşturur;
        /// </summary>
        public AudioMixer CreateMixer(string mixerId, MixerConfiguration config = null)
        {
            if (!_isInitialized)
                throw new AudioException("Ses sistemi başlatılmamış");

            try
            {
                config ??= new MixerConfiguration();

                var mixer = new AudioMixer(mixerId, config);

                lock (_lockObject)
                {
                    if (_mixers.ContainsKey(mixerId))
                    {
                        _logger.LogWarning("Mikser zaten mevcut: {MixerId}, üzerine yazılıyor", mixerId);
                        _mixers[mixerId].Dispose();
                    }

                    _mixers[mixerId] = mixer;
                }

                _logger.LogInformation("Mikser oluşturuldu: {MixerId}, Channels={Channels}",
                    mixerId, config.ChannelCount);

                return mixer;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mikser oluşturulamadı: {MixerId}", mixerId);
                throw new AudioException($"'{mixerId}' mikseri oluşturulamadı", ex);
            }
        }

        /// <summary>
        /// Miksere ses kaynağı ekler;
        /// </summary>
        public void AddSourceToMixer(string sourceId, string mixerId, int channel = 0)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");
            if (!_mixers.TryGetValue(mixerId, out var mixer))
                throw new AudioException($"Mikser bulunamadı: {mixerId}");

            try
            {
                mixer.AddSource(source, channel);

                _logger.LogInformation("Ses kaynağı miksere eklendi: {SourceId} -> {MixerId}, Channel={Channel}",
                    sourceId, mixerId, channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaynağı miksere eklenemedi: {SourceId} -> {MixerId}", sourceId, mixerId);
                throw new AudioException($"'{sourceId}' ses kaynağı '{mixerId}' mikserine eklenemedi", ex);
            }
        }

        /// <summary>
        /// Ses kaydı başlatır;
        /// </summary>
        public async Task StartRecordingAsync(string outputFilePath, RecordingConfiguration config = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
                throw new AudioException("Ses sistemi başlatılmamış");

            if (IsRecording)
                throw new AudioException("Zaten kayıt yapılıyor");

            try
            {
                config ??= new RecordingConfiguration();

                _logger.LogInformation("Ses kaydı başlatılıyor: {OutputFile}, Device={DeviceId}, Format={SampleRate}Hz/{Bits}bit",
                    outputFilePath, config.DeviceId, config.SampleRate, config.BitsPerSample);

                // Kayıt cihazını bul;
                var captureDevice = FindCaptureDevice(config.DeviceId);
                if (captureDevice == null)
                    throw new AudioException($"Kayıt cihazı bulunamadı: {config.DeviceId}");

                // Kayıt formatını oluştur;
                var waveFormat = new WaveFormat(config.SampleRate, config.BitsPerSample, config.ChannelCount);

                // Kaydediciyi oluştur;
                var waveRecorder = new WaveFileWriter(outputFilePath, waveFormat);
                var waveIn = new WaveInEvent;
                {
                    DeviceNumber = captureDevice.DeviceIndex,
                    WaveFormat = waveFormat,
                    BufferMilliseconds = config.BufferSize;
                };

                waveIn.DataAvailable += (sender, e) =>
                {
                    try
                    {
                        waveRecorder.Write(e.Buffer, 0, e.BytesRecorded);
                        waveRecorder.Flush();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Kayıt yazma hatası");
                        OnErrorOccurred(new AudioErrorEventArgs;
                        {
                            ErrorType = AudioErrorType.RecordingError,
                            ErrorMessage = "Kayıt yazma hatası",
                            Exception = ex;
                        });
                    }
                };

                waveIn.RecordingStopped += (sender, e) =>
                {
                    try
                    {
                        waveRecorder?.Dispose();
                        waveIn?.Dispose();

                        IsRecording = false;

                        _logger.LogInformation("Ses kaydı tamamlandı: {OutputFile}", outputFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Kayıt tamamlama hatası");
                    }
                };

                waveIn.StartRecording();
                IsRecording = true;

                _logger.LogInformation("Ses kaydı başlatıldı: {OutputFile}", outputFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses kaydı başlatılamadı");
                throw new AudioException("Ses kaydı başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Ses kaydını durdurur;
        /// </summary>
        public void StopRecording()
        {
            if (!IsRecording)
                return;

            // Kayıt durdurma işlemi WaveInEvent üzerinden yapılacak;
            // Bu örnekte basit implementasyon, gerçek implementasyonda kayıt nesnesini saklamak gerekir;
            IsRecording = false;

            _logger.LogInformation("Ses kaydı durduruldu");
        }

        /// <summary>
        /// Çalma listesi oluşturur ve çalar;
        /// </summary>
        public async Task PlayPlaylistAsync(List<string> audioFiles, PlaylistConfiguration config = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
                throw new AudioException("Ses sistemi başlatılmamış");

            try
            {
                config ??= new PlaylistConfiguration();

                _logger.LogInformation("Çalma listesi başlatılıyor: {Count} dosya, Shuffle={Shuffle}, Loop={Loop}",
                    audioFiles.Count, config.Shuffle, config.Loop);

                if (config.Shuffle)
                {
                    audioFiles = audioFiles.OrderBy(x => Guid.NewGuid()).ToList();
                }

                IsPlaylistPlaying = true;
                int currentIndex = 0;

                while (IsPlaylistPlaying && currentIndex < audioFiles.Count)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var file = audioFiles[currentIndex];
                    try
                    {
                        var source = await LoadAudioAsync(file, $"playlist_{currentIndex}", cancellationToken);
                        PlaySource(source.Id, false, config.Volume);

                        // Çalma tamamlanana kadar bekle;
                        while (source.IsPlaying && !cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(100, cancellationToken);
                        }

                        if (!config.Loop || cancellationToken.IsCancellationRequested)
                        {
                            currentIndex++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Çalma listesi öğesi çalınamadı: {File}", file);
                        currentIndex++;
                    }
                }

                IsPlaylistPlaying = false;
                _logger.LogInformation("Çalma listesi tamamlandı");
            }
            catch (Exception ex)
            {
                IsPlaylistPlaying = false;
                _logger.LogError(ex, "Çalma listesi çalınamadı");
                throw new AudioException("Çalma listesi çalınamadı", ex);
            }
        }

        /// <summary>
        /// Ses cihazlarını yeniden tarar;
        /// </summary>
        public async Task RescanDevicesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Ses cihazları yeniden taranıyor...");

                await ScanAudioDevicesAsync(cancellationToken);

                _logger.LogInformation("Ses cihazları yeniden tarandı: {OutputCount} çıkış, {InputCount} giriş",
                    _outputDevices.Count, _captureDevices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses cihazları taranamadı");
                throw new AudioException("Ses cihazları taranamadı", ex);
            }
        }

        /// <summary>
        /// Ses analizi yapar;
        /// </summary>
        public AudioAnalysisResult AnalyzeAudio(string sourceId)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");

            try
            {
                _logger.LogInformation("Ses analizi başlatılıyor: {SourceId}", sourceId);

                var result = new AudioAnalysisResult;
                {
                    SourceId = sourceId,
                    SampleRate = source.SampleRate,
                    Channels = source.Channels,
                    Duration = source.Duration;
                };

                // Basit analiz - gerçek implementasyonda FFT veya benzeri teknikler kullanılır;
                // Bu örnek için temel bilgiler döndürülüyor;

                _logger.LogInformation("Ses analizi tamamlandı: {SourceId}", sourceId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses analizi yapılamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' ses analizi yapılamadı", ex);
            }
        }

        /// <summary>
        /// 3D ses pozisyonunu ayarlar;
        /// </summary>
        public void Set3DPosition(string sourceId, Audio3DPosition position)
        {
            if (!_audioSources.TryGetValue(sourceId, out var source))
                throw new AudioException($"Ses kaynağı bulunamadı: {sourceId}");

            try
            {
                source.Position3D = position;

                // 3D ses efektini uygula;
                var effect = new PanningEffect(position);
                source.ApplyEffect(effect);

                _logger.LogDebug("3D ses pozisyonu ayarlandı: {SourceId}, Position={X},{Y},{Z}",
                    sourceId, position.X, position.Y, position.Z);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "3D ses pozisyonu ayarlanamadı: {SourceId}", sourceId);
                throw new AudioException($"'{sourceId}' 3D ses pozisyonu ayarlanamadı", ex);
            }
        }
        #endregion;

        #region Private Methods;
        private void InitializeDefaults()
        {
            // Varsayılan ses cihazlarını başlat;
            try
            {
                // Varsayılan değerleri konfigürasyondan al;
                if (_configuration != null)
                {
                    _masterVolume = _configuration.DefaultMasterVolume;
                    _masterMuted = _configuration.StartMuted;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varsayılan ayarlar başlatılamadı");
            }
        }

        private async Task ScanAudioDevicesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _outputDevices.Clear();
                _captureDevices.Clear();

                // Çıkış cihazlarını tara (NAudio)
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    var capabilities = WaveOut.GetCapabilities(i);
                    var device = new AudioOutputDevice;
                    {
                        DeviceId = i.ToString(),
                        Name = capabilities.ProductName,
                        Channels = capabilities.Channels,
                        IsDefault = i == 0 // Basit varsayılan tespit;
                    };
                    _outputDevices[device.DeviceId] = device;
                }

                // Giriş cihazlarını tara (NAudio)
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    var capabilities = WaveIn.GetCapabilities(i);
                    var device = new AudioCaptureDevice;
                    {
                        DeviceId = i.ToString(),
                        Name = capabilities.ProductName,
                        Channels = capabilities.Channels,
                        DeviceIndex = i,
                        IsDefault = i == 0 // Basit varsayılan tespit;
                    };
                    _captureDevices[device.DeviceId] = device;
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ses cihazları taranamadı");
                throw;
            }
        }

        private void InitializePrimaryOutputDevice()
        {
            try
            {
                // Varsayılan çıkış cihazını oluştur;
                _primaryOutputDevice = new WaveOutEvent;
                {
                    DesiredLatency = _configuration?.DesiredLatency ?? 100,
                    NumberOfBuffers = _configuration?.NumberOfBuffers ?? 2;
                };

                _logger.LogDebug("Ana çıkış cihazı başlatıldı: Latency={Latency}ms, Buffers={Buffers}",
                    _primaryOutputDevice.DesiredLatency, _primaryOutputDevice.NumberOfBuffers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ana çıkış cihazı başlatılamadı");
                throw;
            }
        }

        private void InitializeMasterMixer()
        {
            try
            {
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(DefaultSampleRate, DefaultChannelCount);
                _masterMixer = new MixingSampleProvider(waveFormat)
                {
                    ReadFully = true;
                };

                // Master mikserini çıkış cihazına bağla;
                _primaryOutputDevice.Init(_masterMixer);
                _primaryOutputDevice.Play();

                _logger.LogDebug("Master mikser başlatıldı: Format={SampleRate}Hz/{Channels}ch",
                    waveFormat.SampleRate, waveFormat.Channels);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Master mikser başlatılamadı");
                throw;
            }
        }

        private void InitializeDefaultMixers()
        {
            try
            {
                // Varsayılan miksers oluştur;
                var musicMixer = CreateMixer("music", new MixerConfiguration { ChannelCount = 2 });
                var sfxMixer = CreateMixer("sfx", new MixerConfiguration { ChannelCount = 2 });
                var voiceMixer = CreateMixer("voice", new MixerConfiguration { ChannelCount = 1 });

                _logger.LogDebug("Varsayılan miksers oluşturuldu: Music, SFX, Voice");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varsayılan miksers oluşturulamadı");
                // Kritik hata değil, devam et;
            }
        }

        private void UpdateMasterVolume()
        {
            try
            {
                if (_masterMixer != null)
                {
                    var volumeProvider = new VolumeSampleProvider(_masterMixer)
                    {
                        Volume = _masterMuted ? 0.0f : _masterVolume;
                    };

                    // Volume güncelleme işlemi;
                    // NAudio'da VolumeSampleProvider kullanarak volume kontrolü;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Master volume güncellenemedi");
            }
        }

        private void UpdateMemoryUsage()
        {
            long total = 0;
            lock (_lockObject)
            {
                foreach (var source in _audioSources.Values)
                {
                    total += source.EstimatedMemoryUsage;
                }
            }
            TotalMemoryUsage = total;
        }

        private string GenerateSourceId(string prefix)
        {
            return $"{prefix}_{Guid.NewGuid():N}";
        }

        private AudioCaptureDevice FindCaptureDevice(string deviceId)
        {
            if (_captureDevices.TryGetValue(deviceId, out var device))
                return device;

            // Varsayılan cihazı döndür;
            return _captureDevices.Values.FirstOrDefault(d => d.IsDefault) ?? _captureDevices.Values.FirstOrDefault();
        }

        private void OnSystemInitialized(AudioSystemInitializedEventArgs e)
        {
            SystemInitialized?.Invoke(this, e);
        }

        private void OnSystemShutdown(AudioSystemShutdownEventArgs e)
        {
            SystemShutdown?.Invoke(this, e);
        }

        private void OnSourceAdded(AudioSourceAddedEventArgs e)
        {
            SourceAdded?.Invoke(this, e);
        }

        private void OnSourceRemoved(AudioSourceRemovedEventArgs e)
        {
            SourceRemoved?.Invoke(this, e);
        }

        private void OnSourceStateChanged(AudioSourceStateChangedEventArgs e)
        {
            SourceStateChanged?.Invoke(this, e);
        }

        private void OnVolumeChanged(AudioVolumeChangedEventArgs e)
        {
            VolumeChanged?.Invoke(this, e);
        }

        private void OnEffectApplied(AudioEffectAppliedEventArgs e)
        {
            EffectApplied?.Invoke(this, e);
        }

        private void OnErrorOccurred(AudioErrorEventArgs e)
        {
            ErrorOccurred?.Invoke(this, e);
        }
        #endregion;

        #region IDisposable Implementation;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Shutdown();

                    _deviceEnumerator?.Dispose();

                    foreach (var source in _audioSources.Values)
                    {
                        source.Dispose();
                    }
                    _audioSources.Clear();

                    foreach (var mixer in _mixers.Values)
                    {
                        mixer.Dispose();
                    }
                    _mixers.Clear();
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
            public bool StartMuted { get; set; } = false;
            public int DesiredLatency { get; set; } = 100; // ms;
            public int NumberOfBuffers { get; set; } = 2;
            public int DefaultBufferSize { get; set; } = 4096;
            public bool EnableEffects { get; set; } = true;
            public bool Enable3DAudio { get; set; } = true;
            public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Ses kaynağı sınıfı;
        /// </summary>
        public class AudioSource : IDisposable
        {
            public string Id { get; }
            public string Name { get; set; }
            public string FilePath { get; set; }
            public TimeSpan Duration { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public float Volume { get; set; } = 1.0f;
            public bool Loop { get; set; }
            public bool IsPlaying => PlaybackState == AudioPlaybackState.Playing;
            public AudioPlaybackState PlaybackState { get; private set; }
            public TimeSpan Position { get; set; }
            public Audio3DPosition Position3D { get; set; }
            public long EstimatedMemoryUsage { get; private set; }
            public List<IAudioEffect> Effects { get; } = new List<IAudioEffect>();

            private readonly IWaveProvider _waveProvider;
            private readonly object _lockObject = new object();

            public AudioSource(string id, IWaveProvider waveProvider)
            {
                Id = id;
                _waveProvider = waveProvider;
                PlaybackState = AudioPlaybackState.Stopped;
            }

            public void Play()
            {
                lock (_lockObject)
                {
                    PlaybackState = AudioPlaybackState.Playing;
                }
            }

            public void Pause()
            {
                lock (_lockObject)
                {
                    PlaybackState = AudioPlaybackState.Paused;
                }
            }

            public void Stop()
            {
                lock (_lockObject)
                {
                    PlaybackState = AudioPlaybackState.Stopped;
                    Position = TimeSpan.Zero;
                }
            }

            public ISampleProvider GetSampleProvider()
            {
                var sampleProvider = _waveProvider.ToSampleProvider();

                // Efektleri uygula;
                foreach (var effect in Effects)
                {
                    sampleProvider = effect.Apply(sampleProvider);
                }

                return sampleProvider;
            }

            public void ApplyEffect(IAudioEffect effect)
            {
                lock (_lockObject)
                {
                    Effects.Add(effect);
                }
            }

            public void RemoveEffect(string effectName)
            {
                lock (_lockObject)
                {
                    Effects.RemoveAll(e => e.Name == effectName);
                }
            }

            public void Dispose()
            {
                if (_waveProvider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        /// <summary>
        /// Ses kaynağı türleri;
        /// </summary>
        public enum AudioSourceType;
        {
            File,
            Stream,
            Memory,
            Generated;
        }

        /// <summary>
        /// Çalma durumu;
        /// </summary>
        public enum AudioPlaybackState;
        {
            Stopped,
            Playing,
            Paused;
        }

        /// <summary>
        /// 3D ses pozisyonu;
        /// </summary>
        public struct Audio3DPosition;
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }

            public Audio3DPosition(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
        #endregion;
    }

    #region Supporting Types and Interfaces;
    /// <summary>
    /// Audio manager interface'i;
    /// </summary>
    public interface IAudioManager : IDisposable
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        void Shutdown();
        Task<AudioSource> LoadAudioAsync(string filePath, string sourceId = null, CancellationToken cancellationToken = default);
        AudioSource LoadAudioFromBytes(byte[] audioData, WaveFormat format, string sourceId = null);
        void PlaySource(string sourceId, bool loop = false, float volume = 1.0f);
        void StopSource(string sourceId);
        void StopAllSources();
        void PauseSource(string sourceId);
        void SetSourceVolume(string sourceId, float volume);
        void ApplyEffectToSource(string sourceId, IAudioEffect effect);
        void RemoveEffectFromSource(string sourceId, string effectName);
        AudioMixer CreateMixer(string mixerId, MixerConfiguration config = null);
        void AddSourceToMixer(string sourceId, string mixerId, int channel = 0);
        Task StartRecordingAsync(string outputFilePath, RecordingConfiguration config = null, CancellationToken cancellationToken = default);
        void StopRecording();
        Task PlayPlaylistAsync(List<string> audioFiles, PlaylistConfiguration config = null, CancellationToken cancellationToken = default);
        Task RescanDevicesAsync(CancellationToken cancellationToken = default);
        AudioAnalysisResult AnalyzeAudio(string sourceId);
        void Set3DPosition(string sourceId, Audio3DPosition position);

        // Properties;
        float MasterVolume { get; set; }
        bool MasterMuted { get; set; }
        bool IsInitialized { get; }
        bool IsRecording { get; }
        bool IsPlaylistPlaying { get; }
        int ActiveSourceCount { get; }
        long TotalMemoryUsage { get; }
    }

    /// <summary>
    /// Ses istisna sınıfı;
    /// </summary>
    public class AudioException : Exception
    {
        public AudioException(string message) : base(message) { }
        public AudioException(string message, Exception innerException) : base(message, innerException) { }

        public string SourceId { get; set; }
        public AudioErrorType ErrorType { get; set; }
    }

    /// <summary>
    /// Hata türleri;
    /// </summary>
    public enum AudioErrorType;
    {
        InitializationError,
        PlaybackError,
        RecordingError,
        DeviceError,
        FileError,
        EffectError,
        Unknown;
    }

    // Diğer yardımcı sınıfların tanımları (kısaltılmış)
    public class AudioOutputDevice { /* ... */ }
    public class AudioCaptureDevice { /* ... */ }
    public class AudioMixer : IDisposable { /* ... */ }
    public class MixerConfiguration { /* ... */ }
    public class RecordingConfiguration { /* ... */ }
    public class PlaylistConfiguration { /* ... */ }
    public class AudioAnalysisResult { /* ... */ }
    public interface IAudioEffect { /* ... */ }
    public class PanningEffect : IAudioEffect { /* ... */ }

    // Olay argümanları sınıfları;
    public class AudioSystemInitializedEventArgs : EventArgs { /* ... */ }
    public class AudioSystemShutdownEventArgs : EventArgs { /* ... */ }
    public class AudioSourceAddedEventArgs : EventArgs { /* ... */ }
    public class AudioSourceRemovedEventArgs : EventArgs { /* ... */ }
    public class AudioSourceStateChangedEventArgs : EventArgs { /* ... */ }
    public class AudioVolumeChangedEventArgs : EventArgs { /* ... */ }
    public class AudioEffectAppliedEventArgs : EventArgs { /* ... */ }
    public class AudioErrorEventArgs : EventArgs { /* ... */ }
    #endregion;
}
