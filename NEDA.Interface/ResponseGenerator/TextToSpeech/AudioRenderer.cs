using MaterialDesignThemes.Wpf;
using NAudio.Wave;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Interface.ResponseGenerator.TextToSpeech.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.ResponseGenerator.TextToSpeech;
{
    /// <summary>
    /// Ses verilerini oynatan, yöneten ve işleyen profesyonel ses render motoru;
    /// Thread-safe ve yüksek performanslı implementasyon;
    /// </summary>
    public class AudioRenderer : IAudioRenderer, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly WaveOutEvent _waveOut;
        private AudioFileReader _currentAudioFile;
        private MemoryStream _currentAudioStream;
        private readonly object _lockObject = new object();
        private AudioRenderState _currentState = AudioRenderState.Stopped;
        private float _volume = 1.0f;
        private float _playbackRate = 1.0f;
        private bool _isDisposed;

        // Ses cihazı yönetimi;
        private List<AudioDeviceInfo> _availableDevices;
        private int _selectedDeviceId = -1; // -1 = default device;

        // Olaylar (events)
        public event EventHandler<AudioRenderEventArgs> PlaybackStarted;
        public event EventHandler<AudioRenderEventArgs> PlaybackStopped;
        public event EventHandler<AudioRenderEventArgs> PlaybackPaused;
        public event EventHandler<AudioRenderEventArgs> PlaybackCompleted;
        public event EventHandler<AudioRenderErrorEventArgs> PlaybackError;
        public event EventHandler<AudioPositionChangedEventArgs> PositionChanged;

        /// <summary>
        /// AudioRenderer constructor;
        /// </summary>
        public AudioRenderer(ILogger logger = null)
        {
            _logger = logger ?? LogManager.GetLogger(typeof(AudioRenderer));

            try
            {
                // WaveOutEvent'i başlat;
                _waveOut = new WaveOutEvent;
                {
                    DesiredLatency = 100,
                    NumberOfBuffers = 3;
                };

                _waveOut.PlaybackStopped += OnPlaybackStopped;

                // Kullanılabilir ses cihazlarını tara;
                InitializeAudioDevices();

                _logger.Info("AudioRenderer başarıyla başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.Error($"AudioRenderer başlatma hatası: {ex.Message}", ex);
                throw new AudioRenderException("Ses render motoru başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Constructor with configuration;
        /// </summary>
        public AudioRenderer(AudioRenderConfig config, ILogger logger = null)
            : this(logger)
        {
            if (config != null)
            {
                Configure(config);
            }
        }

        /// <summary>
        /// Ses oynatma durumu;
        /// </summary>
        public AudioRenderState CurrentState;
        {
            get;
            {
                lock (_lockObject)
                {
                    return _currentState;
                }
            }
        }

        /// <summary>
        /// Ses seviyesi (0.0 - 1.0)
        /// </summary>
        public float Volume;
        {
            get => _volume;
            set;
            {
                lock (_lockObject)
                {
                    _volume = Math.Max(0.0f, Math.Min(1.0f, value));
                    if (_currentAudioFile != null)
                    {
                        _currentAudioFile.Volume = _volume;
                    }
                }
            }
        }

        /// <summary>
        /// Oynatma hızı (0.5 - 2.0)
        /// </summary>
        public float PlaybackRate;
        {
            get => _playbackRate;
            set;
            {
                lock (_lockObject)
                {
                    _playbackRate = Math.Max(0.5f, Math.Min(2.0f, value));
                    // Hız değişikliği için audio file'ı yeniden oluştur;
                    UpdatePlaybackRate();
                }
            }
        }

        /// <summary>
        /// Geçerli pozisyon (milisaniye)
        /// </summary>
        public TimeSpan CurrentPosition;
        {
            get;
            {
                lock (_lockObject)
                {
                    return _currentAudioFile?.CurrentTime ?? TimeSpan.Zero;
                }
            }
            set;
            {
                lock (_lockObject)
                {
                    if (_currentAudioFile != null && _currentState == AudioRenderState.Playing)
                    {
                        _currentAudioFile.CurrentTime = value;
                    }
                }
            }
        }

        /// <summary>
        /// Toplam süre (milisaniye)
        /// </summary>
        public TimeSpan TotalDuration;
        {
            get;
            {
                lock (_lockObject)
                {
                    return _currentAudioFile?.TotalTime ?? TimeSpan.Zero;
                }
            }
        }

        /// <summary>
        /// Kullanılabilir ses cihazları;
        /// </summary>
        public IReadOnlyList<AudioDeviceInfo> AvailableDevices => _availableDevices.AsReadOnly();

        /// <summary>
        /// Seçili ses cihazı;
        /// </summary>
        public AudioDeviceInfo SelectedDevice;
        {
            get;
            {
                if (_selectedDeviceId == -1 || _availableDevices == null)
                    return null;

                return _availableDevices.FirstOrDefault(d => d.DeviceId == _selectedDeviceId);
            }
        }

        #region Public Methods;

        /// <summary>
        /// Ses dosyasını yükler ve hazırlar;
        /// </summary>
        public async Task<bool> LoadAudioAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));

            try
            {
                _logger.Debug($"Ses dosyası yükleniyor: {filePath}");

                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Ses dosyası bulunamadı: {filePath}");
                }

                await Task.Run(() =>
                {
                    lock (_lockObject)
                    {
                        // Eski kaynakları temizle;
                        CleanupCurrentAudio();

                        // Yeni audio file oluştur;
                        _currentAudioFile = new AudioFileReader(filePath);
                        _currentAudioFile.Volume = _volume;

                        // WaveOut'a bağla;
                        _waveOut.Init(_currentAudioFile);

                        _currentState = AudioRenderState.Stopped;
                    }
                }, cancellationToken);

                _logger.Info($"Ses dosyası başarıyla yüklendi: {filePath}");
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.Warn("Ses yükleme işlemi iptal edildi");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses dosyası yükleme hatası: {ex.Message}", ex);
                OnPlaybackError(new AudioRenderErrorEventArgs(
                    AudioRenderErrorType.LoadFailed,
                    "Ses dosyası yüklenemedi",
                    ex));
                return false;
            }
        }

        /// <summary>
        /// Ses verisini byte array'den yükler;
        /// </summary>
        public async Task<bool> LoadAudioAsync(byte[] audioData, AudioFormat format, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(audioData, nameof(audioData));

            try
            {
                _logger.Debug($"Ses verisi yükleniyor ({audioData.Length} bytes)");

                await Task.Run(() =>
                {
                    lock (_lockObject)
                    {
                        // Eski kaynakları temizle;
                        CleanupCurrentAudio();

                        // Memory stream oluştur;
                        _currentAudioStream = new MemoryStream(audioData);

                        // Format'a göre reader oluştur;
                        switch (format)
                        {
                            case AudioFormat.Wav:
                                _currentAudioFile = new AudioFileReader(_currentAudioStream);
                                break;
                            case AudioFormat.Mp3:
                                var mp3Reader = new Mp3FileReader(_currentAudioStream);
                                _currentAudioFile = new AudioFileReader(mp3Reader);
                                break;
                            default:
                                throw new NotSupportedException($"Desteklenmeyen ses formatı: {format}");
                        }

                        _currentAudioFile.Volume = _volume;
                        _waveOut.Init(_currentAudioFile);
                        _currentState = AudioRenderState.Stopped;
                    }
                }, cancellationToken);

                _logger.Info("Ses verisi başarıyla yüklendi");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses verisi yükleme hatası: {ex.Message}", ex);
                OnPlaybackError(new AudioRenderErrorEventArgs(
                    AudioRenderErrorType.LoadFailed,
                    "Ses verisi yüklenemedi",
                    ex));
                return false;
            }
        }

        /// <summary>
        /// Ses oynatmayı başlat;
        /// </summary>
        public void Play()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_currentAudioFile == null)
                    {
                        throw new InvalidOperationException("Oynatılacak ses dosyası yüklenmemiş");
                    }

                    if (_currentState == AudioRenderState.Playing)
                    {
                        _logger.Warn("Ses zaten oynatılıyor");
                        return;
                    }

                    _waveOut.Play();
                    _currentState = AudioRenderState.Playing;

                    // Pozisyon takibi başlat;
                    StartPositionTracking();

                    _logger.Debug("Ses oynatma başlatıldı");

                    // Olay tetikle;
                    OnPlaybackStarted(new AudioRenderEventArgs;
                    {
                        Timestamp = DateTime.UtcNow,
                        AudioLength = _currentAudioFile.TotalTime,
                        CurrentPosition = _currentAudioFile.CurrentTime;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses oynatma hatası: {ex.Message}", ex);
                OnPlaybackError(new AudioRenderErrorEventArgs(
                    AudioRenderErrorType.PlaybackFailed,
                    "Ses oynatılamadı",
                    ex));
                throw new AudioRenderException("Ses oynatma başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Asenkron ses oynatma;
        /// </summary>
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                Play();

                // Oynatma tamamlanana kadar bekle;
                while (_currentState == AudioRenderState.Playing && !cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Ses oynatmayı duraklat;
        /// </summary>
        public void Pause()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_currentState != AudioRenderState.Playing)
                    {
                        _logger.Warn("Oynatılan ses bulunamadı");
                        return;
                    }

                    _waveOut.Pause();
                    _currentState = AudioRenderState.Paused;

                    // Pozisyon takibini durdur;
                    StopPositionTracking();

                    _logger.Debug("Ses oynatma duraklatıldı");

                    OnPlaybackPaused(new AudioRenderEventArgs;
                    {
                        Timestamp = DateTime.UtcNow,
                        AudioLength = _currentAudioFile.TotalTime,
                        CurrentPosition = _currentAudioFile.CurrentTime;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses duraklatma hatası: {ex.Message}", ex);
                OnPlaybackError(new AudioRenderErrorEventArgs(
                    AudioRenderErrorType.PlaybackFailed,
                    "Ses duraklatılamadı",
                    ex));
            }
        }

        /// <summary>
        /// Ses oynatmayı durdur;
        /// </summary>
        public void Stop()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_currentState == AudioRenderState.Stopped)
                    {
                        return;
                    }

                    _waveOut.Stop();
                    _currentState = AudioRenderState.Stopped;

                    // Pozisyon takibini durdur;
                    StopPositionTracking();

                    // Pozisyonu başa al;
                    if (_currentAudioFile != null)
                    {
                        _currentAudioFile.CurrentTime = TimeSpan.Zero;
                    }

                    _logger.Debug("Ses oynatma durduruldu");

                    OnPlaybackStopped(new AudioRenderEventArgs;
                    {
                        Timestamp = DateTime.UtcNow,
                        AudioLength = _currentAudioFile?.TotalTime ?? TimeSpan.Zero,
                        CurrentPosition = TimeSpan.Zero;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses durdurma hatası: {ex.Message}", ex);
                OnPlaybackError(new AudioRenderErrorEventArgs(
                    AudioRenderErrorType.PlaybackFailed,
                    "Ses durdurulamadı",
                    ex));
            }
        }

        /// <summary>
        /// Belirli bir pozisyondan oynatmaya başla;
        /// </summary>
        public void Seek(TimeSpan position)
        {
            try
            {
                lock (_lockObject)
                {
                    if (_currentAudioFile == null)
                    {
                        throw new InvalidOperationException("Ses dosyası yüklenmemiş");
                    }

                    // Pozisyonu sınırla;
                    if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                    if (position > _currentAudioFile.TotalTime) position = _currentAudioFile.TotalTime;

                    _currentAudioFile.CurrentTime = position;

                    // Eğer oynatılıyorsa, pozisyon değişikliğini bildir;
                    if (_currentState == AudioRenderState.Playing)
                    {
                        OnPositionChanged(new AudioPositionChangedEventArgs;
                        {
                            OldPosition = position,
                            NewPosition = position,
                            Timestamp = DateTime.UtcNow;
                        });
                    }

                    _logger.Debug($"Ses pozisyonu değiştirildi: {position}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses pozisyonu değiştirme hatası: {ex.Message}", ex);
                OnPlaybackError(new AudioRenderErrorEventArgs(
                    AudioRenderErrorType.SeekFailed,
                    "Ses pozisyonu değiştirilemedi",
                    ex));
            }
        }

        /// <summary>
        /// Ses cihazını değiştir;
        /// </summary>
        public bool ChangeAudioDevice(int deviceId)
        {
            try
            {
                lock (_lockObject)
                {
                    // Geçerli cihazı kontrol et;
                    if (!_availableDevices.Any(d => d.DeviceId == deviceId))
                    {
                        _logger.Error($"Geçersiz ses cihazı ID: {deviceId}");
                        return false;
                    }

                    // Eğer oynatılıyorsa durdur;
                    var wasPlaying = _currentState == AudioRenderState.Playing;
                    if (wasPlaying)
                    {
                        Stop();
                    }

                    // Yeni cihazı ayarla;
                    _selectedDeviceId = deviceId;
                    _waveOut.DeviceNumber = deviceId;

                    // Oynatılıyorsa yeniden başlat;
                    if (wasPlaying && _currentAudioFile != null)
                    {
                        _waveOut.Init(_currentAudioFile);
                        Play();
                    }

                    _logger.Info($"Ses cihazı değiştirildi: {deviceId}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses cihazı değiştirme hatası: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Ses cihazını değiştir (isimle)
        /// </summary>
        public bool ChangeAudioDevice(string deviceName)
        {
            var device = _availableDevices.FirstOrDefault(d =>
                d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                _logger.Error($"Ses cihazı bulunamadı: {deviceName}");
                return false;
            }

            return ChangeAudioDevice(device.DeviceId);
        }

        /// <summary>
        /// Ses renderer'ı yapılandır;
        /// </summary>
        public void Configure(AudioRenderConfig config)
        {
            Guard.ArgumentNotNull(config, nameof(config));

            lock (_lockObject)
            {
                try
                {
                    // Ses seviyesi;
                    if (config.Volume.HasValue)
                    {
                        Volume = config.Volume.Value;
                    }

                    // Oynatma hızı;
                    if (config.PlaybackRate.HasValue)
                    {
                        PlaybackRate = config.PlaybackRate.Value;
                    }

                    // Ses cihazı;
                    if (!string.IsNullOrEmpty(config.AudioDeviceName))
                    {
                        ChangeAudioDevice(config.AudioDeviceName);
                    }
                    else if (config.AudioDeviceId.HasValue)
                    {
                        ChangeAudioDevice(config.AudioDeviceId.Value);
                    }

                    // Latency ayarları;
                    if (config.DesiredLatency.HasValue)
                    {
                        _waveOut.DesiredLatency = config.DesiredLatency.Value;
                    }

                    if (config.NumberOfBuffers.HasValue)
                    {
                        _waveOut.NumberOfBuffers = config.NumberOfBuffers.Value;
                    }

                    _logger.Info("AudioRenderer yapılandırma tamamlandı");
                }
                catch (Exception ex)
                {
                    _logger.Error($"AudioRenderer yapılandırma hatası: {ex.Message}", ex);
                    throw new AudioRenderException("Ses renderer yapılandırılamadı", ex);
                }
            }
        }

        /// <summary>
        /// Ses durumunu kontrol et;
        /// </summary>
        public AudioRenderStatus GetStatus()
        {
            lock (_lockObject)
            {
                return new AudioRenderStatus;
                {
                    State = _currentState,
                    CurrentPosition = _currentAudioFile?.CurrentTime ?? TimeSpan.Zero,
                    TotalDuration = _currentAudioFile?.TotalTime ?? TimeSpan.Zero,
                    Volume = _volume,
                    PlaybackRate = _playbackRate,
                    SelectedDevice = SelectedDevice,
                    IsAudioLoaded = _currentAudioFile != null;
                };
            }
        }

        /// <summary>
        /// Ses verisini dosyaya kaydet;
        /// </summary>
        public async Task SaveToFileAsync(string filePath, AudioFormat format, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));

            if (_currentAudioFile == null)
            {
                throw new InvalidOperationException("Kaydedilecek ses verisi bulunamadı");
            }

            try
            {
                await Task.Run(() =>
                {
                    lock (_lockObject)
                    {
                        // Geçerli pozisyonu kaydet;
                        var originalPosition = _currentAudioFile.CurrentTime;

                        // Başa dön;
                        _currentAudioFile.Position = 0;

                        // Format'a göre kaydet;
                        using (var writer = CreateAudioWriter(filePath, format))
                        {
                            byte[] buffer = new byte[4096];
                            int bytesRead;

                            while ((bytesRead = _currentAudioFile.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    break;
                                }

                                writer.Write(buffer, 0, bytesRead);
                            }
                        }

                        // Orijinal pozisyona dön;
                        _currentAudioFile.CurrentTime = originalPosition;

                        _logger.Info($"Ses dosyası kaydedildi: {filePath}");
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses dosyası kaydetme hatası: {ex.Message}", ex);
                throw new AudioRenderException("Ses dosyası kaydedilemedi", ex);
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Ses cihazlarını başlat;
        /// </summary>
        private void InitializeAudioDevices()
        {
            _availableDevices = new List<AudioDeviceInfo>();

            try
            {
                for (int i = -1; i < WaveOut.DeviceCount; i++)
                {
                    var capabilities = WaveOut.GetCapabilities(i);
                    _availableDevices.Add(new AudioDeviceInfo;
                    {
                        DeviceId = i,
                        DeviceName = capabilities.ProductName,
                        Channels = capabilities.Channels,
                        SupportsPlayback = true,
                        IsDefault = i == -1;
                    });
                }

                _logger.Debug($"{_availableDevices.Count} ses cihazı bulundu");
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses cihazı tarama hatası: {ex.Message}", ex);
                // Default cihazı ekle;
                _availableDevices.Add(new AudioDeviceInfo;
                {
                    DeviceId = -1,
                    DeviceName = "Default Audio Device",
                    Channels = 2,
                    SupportsPlayback = true,
                    IsDefault = true;
                });
            }
        }

        /// <summary>
        /// Oynatma hızını güncelle;
        /// </summary>
        private void UpdatePlaybackRate()
        {
            if (_currentAudioFile != null && Math.Abs(_playbackRate - 1.0f) > 0.01f)
            {
                // Hız değişikliği için yeni bir audio file oluştur;
                // Not: Bu basit bir implementasyon, gerçek uygulamada daha gelişmiş bir çözüm gerekebilir;
                var originalPosition = _currentAudioFile.CurrentTime;

                // SpeedProvider kullanarak hızı değiştir;
                var speedProvider = new MediaFoundationResampler(_currentAudioFile,
                    new WaveFormat((int)(_currentAudioFile.WaveFormat.SampleRate * _playbackRate),
                    _currentAudioFile.WaveFormat.BitsPerSample,
                    _currentAudioFile.WaveFormat.Channels));

                // Eski audio file'ı temizle;
                _currentAudioFile.Dispose();
                _currentAudioFile = new AudioFileReader(speedProvider);
                _currentAudioFile.Volume = _volume;
                _currentAudioFile.CurrentTime = originalPosition;

                _waveOut.Init(_currentAudioFile);
            }
        }

        /// <summary>
        /// Mevcut ses kaynaklarını temizle;
        /// </summary>
        private void CleanupCurrentAudio()
        {
            try
            {
                if (_currentAudioFile != null)
                {
                    _waveOut.Stop();
                    _currentAudioFile.Dispose();
                    _currentAudioFile = null;
                }

                if (_currentAudioStream != null)
                {
                    _currentAudioStream.Dispose();
                    _currentAudioStream = null;
                }

                _currentState = AudioRenderState.Stopped;
            }
            catch (Exception ex)
            {
                _logger.Error($"Ses kaynakları temizleme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Pozisyon takibini başlat;
        /// </summary>
        private void StartPositionTracking()
        {
            // Timer ile pozisyon değişikliklerini takip et;
            // Not: Gerçek implementasyonda daha optimize bir çözüm kullanılabilir;
            Task.Run(async () =>
            {
                TimeSpan lastPosition = TimeSpan.Zero;

                while (_currentState == AudioRenderState.Playing)
                {
                    try
                    {
                        lock (_lockObject)
                        {
                            if (_currentAudioFile != null)
                            {
                                var currentPosition = _currentAudioFile.CurrentTime;

                                // Pozisyon değiştiyse event tetikle;
                                if (currentPosition != lastPosition)
                                {
                                    OnPositionChanged(new AudioPositionChangedEventArgs;
                                    {
                                        OldPosition = lastPosition,
                                        NewPosition = currentPosition,
                                        Timestamp = DateTime.UtcNow;
                                    });
                                    lastPosition = currentPosition;
                                }

                                // Oynatma tamamlandı mı kontrol et;
                                if (currentPosition >= _currentAudioFile.TotalTime)
                                {
                                    OnPlaybackCompleted(new AudioRenderEventArgs;
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        AudioLength = _currentAudioFile.TotalTime,
                                        CurrentPosition = currentPosition;
                                    });

                                    Stop();
                                    break;
                                }
                            }
                        }

                        await Task.Delay(50); // 50ms interval;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Pozisyon takip hatası: {ex.Message}", ex);
                        break;
                    }
                }
            });
        }

        /// <summary>
        /// Pozisyon takibini durdur;
        /// </summary>
        private void StopPositionTracking()
        {
            // Tracking thread'i otomatik olarak duracak;
        }

        /// <summary>
        /// Ses yazıcı oluştur;
        /// </summary>
        private WaveFileWriter CreateAudioWriter(string filePath, AudioFormat format)
        {
            switch (format)
            {
                case AudioFormat.Wav:
                    return new WaveFileWriter(filePath, _currentAudioFile.WaveFormat);

                case AudioFormat.Mp3:
                    // NAudio için MP3 yazma daha karmaşık;
                    // Bu örnek için WAV formatına dönüştürülebilir;
                    throw new NotSupportedException("MP3 formatı şu anda desteklenmiyor");

                default:
                    throw new NotSupportedException($"Desteklenmeyen format: {format}");
            }
        }

        #endregion;

        #region Event Handlers;

        /// <summary>
        /// WaveOut oynatma durdu event handler;
        /// </summary>
        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            try
            {
                lock (_lockObject)
                {
                    if (_currentState != AudioRenderState.Stopped)
                    {
                        _currentState = AudioRenderState.Stopped;
                        StopPositionTracking();

                        // Hata varsa bildir;
                        if (e.Exception != null)
                        {
                            OnPlaybackError(new AudioRenderErrorEventArgs(
                                AudioRenderErrorType.PlaybackFailed,
                                "Oynatma sırasında hata oluştu",
                                e.Exception));
                        }

                        OnPlaybackStopped(new AudioRenderEventArgs;
                        {
                            Timestamp = DateTime.UtcNow,
                            AudioLength = _currentAudioFile?.TotalTime ?? TimeSpan.Zero,
                            CurrentPosition = TimeSpan.Zero;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Oynatma durdurma event handler hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Oynatma başladı event tetikleyici;
        /// </summary>
        protected virtual void OnPlaybackStarted(AudioRenderEventArgs e)
        {
            PlaybackStarted?.Invoke(this, e);
        }

        /// <summary>
        /// Oynatma durduruldu event tetikleyici;
        /// </summary>
        protected virtual void OnPlaybackStopped(AudioRenderEventArgs e)
        {
            PlaybackStopped?.Invoke(this, e);
        }

        /// <summary>
        /// Oynatma duraklatıldı event tetikleyici;
        /// </summary>
        protected virtual void OnPlaybackPaused(AudioRenderEventArgs e)
        {
            PlaybackPaused?.Invoke(this, e);
        }

        /// <summary>
        /// Oynatma tamamlandı event tetikleyici;
        /// </summary>
        protected virtual void OnPlaybackCompleted(AudioRenderEventArgs e)
        {
            PlaybackCompleted?.Invoke(this, e);
        }

        /// <summary>
        /// Oynatma hatası event tetikleyici;
        /// </summary>
        protected virtual void OnPlaybackError(AudioRenderErrorEventArgs e)
        {
            PlaybackError?.Invoke(this, e);
        }

        /// <summary>
        /// Pozisyon değişti event tetikleyici;
        /// </summary>
        protected virtual void OnPositionChanged(AudioPositionChangedEventArgs e)
        {
            PositionChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Dispose pattern implementation;
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
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Yönetilen kaynakları temizle;
                    try
                    {
                        if (_waveOut != null)
                        {
                            _waveOut.PlaybackStopped -= OnPlaybackStopped;
                            _waveOut.Dispose();
                        }

                        CleanupCurrentAudio();

                        _logger.Info("AudioRenderer kaynakları temizlendi");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Dispose hatası: {ex.Message}", ex);
                    }
                }

                _isDisposed = true;
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~AudioRenderer()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Ses render durumu enum'u;
    /// </summary>
    public enum AudioRenderState;
    {
        Stopped,
        Playing,
        Paused,
        Buffering;
    }

    /// <summary>
    /// Ses formatları;
    /// </summary>
    public enum AudioFormat;
    {
        Wav,
        Mp3,
        Aac,
        Ogg;
    }

    /// <summary>
    /// Ses render hata türleri;
    /// </summary>
    public enum AudioRenderErrorType;
    {
        LoadFailed,
        PlaybackFailed,
        DeviceError,
        SeekFailed,
        SaveFailed;
    }

    /// <summary>
    /// Ses render yapılandırması;
    /// </summary>
    public class AudioRenderConfig;
    {
        public float? Volume { get; set; }
        public float? PlaybackRate { get; set; }
        public string AudioDeviceName { get; set; }
        public int? AudioDeviceId { get; set; }
        public int? DesiredLatency { get; set; }
        public int? NumberOfBuffers { get; set; }
    }

    /// <summary>
    /// Ses cihazı bilgisi;
    /// </summary>
    public class AudioDeviceInfo;
    {
        public int DeviceId { get; set; }
        public string DeviceName { get; set; }
        public int Channels { get; set; }
        public bool SupportsPlayback { get; set; }
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Ses render durumu;
    /// </summary>
    public class AudioRenderStatus;
    {
        public AudioRenderState State { get; set; }
        public TimeSpan CurrentPosition { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public float Volume { get; set; }
        public float PlaybackRate { get; set; }
        public AudioDeviceInfo SelectedDevice { get; set; }
        public bool IsAudioLoaded { get; set; }

        public double ProgressPercentage =>
            TotalDuration.TotalMilliseconds > 0 ?
            (CurrentPosition.TotalMilliseconds / TotalDuration.TotalMilliseconds) * 100 : 0;
    }

    /// <summary>
    /// Ses render event args;
    /// </summary>
    public class AudioRenderEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan AudioLength { get; set; }
        public TimeSpan CurrentPosition { get; set; }
    }

    /// <summary>
    /// Ses pozisyon değişikliği event args;
    /// </summary>
    public class AudioPositionChangedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan OldPosition { get; set; }
        public TimeSpan NewPosition { get; set; }
    }

    /// <summary>
    /// Ses render hata event args;
    /// </summary>
    public class AudioRenderErrorEventArgs : EventArgs;
    {
        public AudioRenderErrorType ErrorType { get; }
        public string Message { get; }
        public Exception Exception { get; }

        public AudioRenderErrorEventArgs(AudioRenderErrorType errorType, string message, Exception exception = null)
        {
            ErrorType = errorType;
            Message = message;
            Exception = exception;
        }
    }

    /// <summary>
    /// Audio render interface;
    /// </summary>
    public interface IAudioRenderer : IDisposable
    {
        event EventHandler<AudioRenderEventArgs> PlaybackStarted;
        event EventHandler<AudioRenderEventArgs> PlaybackStopped;
        event EventHandler<AudioRenderEventArgs> PlaybackPaused;
        event EventHandler<AudioRenderEventArgs> PlaybackCompleted;
        event EventHandler<AudioRenderErrorEventArgs> PlaybackError;
        event EventHandler<AudioPositionChangedEventArgs> PositionChanged;

        AudioRenderState CurrentState { get; }
        float Volume { get; set; }
        float PlaybackRate { get; set; }
        TimeSpan CurrentPosition { get; set; }
        TimeSpan TotalDuration { get; }

        Task<bool> LoadAudioAsync(string filePath, CancellationToken cancellationToken = default);
        Task<bool> LoadAudioAsync(byte[] audioData, AudioFormat format, CancellationToken cancellationToken = default);
        void Play();
        Task PlayAsync(CancellationToken cancellationToken = default);
        void Pause();
        void Stop();
        void Seek(TimeSpan position);
        bool ChangeAudioDevice(int deviceId);
        bool ChangeAudioDevice(string deviceName);
        void Configure(AudioRenderConfig config);
        AudioRenderStatus GetStatus();
        Task SaveToFileAsync(string filePath, AudioFormat format, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Audio render exception;
    /// </summary>
    public class AudioRenderException : Exception
    {
        public AudioRenderException(string message) : base(message) { }
        public AudioRenderException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
