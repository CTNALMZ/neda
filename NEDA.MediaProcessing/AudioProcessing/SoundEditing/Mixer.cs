using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Dsp;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;

namespace NEDA.MediaProcessing.AudioProcessing;
{
    /// <summary>
    /// Profesyonel ses mikseri - çoklu kanal, efekt, EQ ve mastering özellikleri;
    /// </summary>
    public class Mixer : IMixer, IDisposable;
    {
        #region Fields;
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly Dictionary<string, MixerChannel> _channels;
        private readonly Dictionary<string, AudioEffect> _effects;
        private readonly Dictionary<string, AudioBus> _buses;
        private readonly Dictionary<string, AudioGroup> _groups;
        private readonly List<AudioSend> _sends;
        private readonly List<AudioReturn> _returns;
        private readonly object _lockObject = new object();
        private readonly WasapiOut _audioOutput;
        private readonly MixingSampleProvider _masterMixer;
        private readonly BufferedWaveProvider _outputBuffer;
        private readonly Equalizer _equalizer;
        private readonly Compressor _compressor;
        private readonly Limiter _limiter;
        private readonly Reverb _reverb;
        private readonly Delay _delay;
        private readonly Chorus _chorus;
        private readonly Flanger _flanger;
        private readonly Distortion _distortion;
        private bool _isInitialized;
        private bool _isDisposed;
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isMuted;
        private bool _isSolo;
        private float _masterVolume;
        private float _masterPan;
        private int _sampleRate;
        private int _channels;
        private int _bitsPerSample;
        private Task _mixingTask;
        private CancellationTokenSource _mixingCancellationTokenSource;
        private DateTime _startTime;
        private long _totalSamplesMixed;
        private float[] _masterPeakLevels;
        private float[] _masterRMSLevels;
        private RingBuffer<float[]> _meterBuffer;
        private Dictionary<string, MeterData> _channelMeters;
        #endregion;

        #region Properties;
        public string MixerId { get; private set; }
        public string MixerName { get; private set; }
        public MixerStatus Status { get; private set; }
        public MixerConfiguration Configuration { get; private set; }
        public int ChannelCount => _channels.Count;
        public int EffectCount => _effects.Count;
        public int BusCount => _buses.Count;
        public int GroupCount => _groups.Count;
        public bool IsPlaying => _isPlaying;
        public bool IsPaused => _isPaused;
        public bool IsMuted => _isMuted;
        public bool IsSolo => _isSolo;
        public float MasterVolume => _masterVolume;
        public float MasterPan => _masterPan;
        public float[] MasterPeakLevels => _masterPeakLevels?.ToArray() ?? new float[2];
        public float[] MasterRMSLevels => _masterRMSLevels?.ToArray() ?? new float[2];
        public int Latency { get; private set; }
        public long TotalSamplesProcessed => _totalSamplesMixed;
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;
        public event EventHandler<MixerEventArgs> OnMixerEvent;
        public event EventHandler<MeterDataEventArgs> OnMeterUpdate;
        public event EventHandler<PeakEventArgs> OnPeakDetected;
        #endregion;

        #region Constructor;
        public Mixer(
            ILogger logger,
            IErrorReporter errorReporter,
            string mixerName = "Master Mixer")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            MixerId = Guid.NewGuid().ToString();
            MixerName = mixerName;
            Status = MixerStatus.Stopped;

            _channels = new Dictionary<string, MixerChannel>();
            _effects = new Dictionary<string, AudioEffect>();
            _buses = new Dictionary<string, AudioBus>();
            _groups = new Dictionary<string, AudioGroup>();
            _sends = new List<AudioSend>();
            _returns = new List<AudioReturn>();

            _sampleRate = 44100;
            _channels = 2;
            _bitsPerSample = 16;
            _masterVolume = 1.0f;
            _masterPan = 0.0f;
            _isMuted = false;
            _isSolo = false;

            _masterPeakLevels = new float[2];
            _masterRMSLevels = new float[2];
            _channelMeters = new Dictionary<string, MeterData>();
            _meterBuffer = new RingBuffer<float[]>(100); // 100 frame buffer;

            // Master mixer oluştur;
            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
            _masterMixer = new MixingSampleProvider(waveFormat)
            {
                ReadFully = true;
            };

            // Output buffer oluştur;
            _outputBuffer = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = false;
            };

            // Audio output oluştur;
            _audioOutput = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);

            // Efektler oluştur;
            _equalizer = new Equalizer(_sampleRate);
            _compressor = new Compressor(_sampleRate);
            _limiter = new Limiter(_sampleRate);
            _reverb = new Reverb(_sampleRate);
            _delay = new Delay(_sampleRate);
            _chorus = new Chorus(_sampleRate);
            _flanger = new Flanger(_sampleRate);
            _distortion = new Distortion(_sampleRate);

            _logger.LogInformation($"Mixer created: {MixerName} (ID: {MixerId})");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Mikseri başlatır ve yapılandırır;
        /// </summary>
        public async Task<OperationResult> InitializeAsync(MixerConfiguration configuration = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("Mixer is already initialized");
                    return OperationResult.Failure("Mixer is already initialized");
                }

                _logger.LogInformation($"Initializing mixer: {MixerName}");
                Status = MixerStatus.Initializing;

                Configuration = configuration ?? GetDefaultConfiguration();

                // Ses formatını ayarla;
                _sampleRate = Configuration.SampleRate;
                _channels = Configuration.Channels;
                _bitsPerSample = Configuration.BitsPerSample;

                // Master mixer formatını güncelle;
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
                _masterMixer.WaveFormat = waveFormat;
                _outputBuffer.WaveFormat = waveFormat;

                // Efektleri başlat;
                await InitializeEffectsAsync(cancellationToken);

                // Varsayılan kanalları oluştur;
                await CreateDefaultChannelsAsync(cancellationToken);

                // Bus'ları oluştur;
                await CreateDefaultBusesAsync(cancellationToken);

                // Grupları oluştur;
                await CreateDefaultGroupsAsync(cancellationToken);

                // Metre buffer'ını ayarla;
                _meterBuffer = new RingBuffer<float[]>(Configuration.MeterBufferSize);

                // Audio output'u başlat;
                _audioOutput.Init(_outputBuffer);

                _isInitialized = true;
                Status = MixerStatus.Stopped;
                _startTime = DateTime.UtcNow;

                _logger.LogInformation($"Mixer initialized successfully: {_channels.Count} channels, {_buses.Count} buses");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize mixer");
                Status = MixerStatus.Error;
                return OperationResult.Failure($"Initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mikseri başlatır (oynatmaya başlar)
        /// </summary>
        public async Task<OperationResult> StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (_isPlaying)
                {
                    _logger.LogWarning("Mixer is already playing");
                    return OperationResult.Failure("Mixer is already playing");
                }

                _logger.LogInformation("Starting mixer...");
                Status = MixerStatus.Starting;

                // Tüm kanalları başlat;
                await StartAllChannelsAsync(cancellationToken);

                // Mixing task'ını başlat;
                StartMixingTask();

                // Audio output'u başlat;
                _audioOutput.Play();

                _isPlaying = true;
                _isPaused = false;
                Status = MixerStatus.Playing;

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.Started,
                    MixerId = MixerId,
                    MixerName = MixerName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Mixer started successfully");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start mixer");
                Status = MixerStatus.Error;
                return OperationResult.Failure($"Start failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mikseri durdurur;
        /// </summary>
        public async Task<OperationResult> StopAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (!_isPlaying)
                {
                    return OperationResult.Failure("Mixer is not playing");
                }

                _logger.LogInformation("Stopping mixer...");
                Status = MixerStatus.Stopping;

                // Audio output'u durdur;
                _audioOutput.Stop();

                // Mixing task'ını durdur;
                StopMixingTask();

                // Tüm kanalları durdur;
                await StopAllChannelsAsync(cancellationToken);

                _isPlaying = false;
                _isPaused = false;
                Status = MixerStatus.Stopped;

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.Stopped,
                    MixerId = MixerId,
                    MixerName = MixerName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Mixer stopped successfully");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop mixer");
                return OperationResult.Failure($"Stop failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mikseri duraklatır;
        /// </summary>
        public async Task<OperationResult> PauseAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (!_isPlaying)
                {
                    return OperationResult.Failure("Mixer is not playing");
                }

                if (_isPaused)
                {
                    return OperationResult.Failure("Mixer is already paused");
                }

                _logger.LogInformation("Pausing mixer...");

                _audioOutput.Pause();
                _isPaused = true;
                Status = MixerStatus.Paused;

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.Paused,
                    MixerId = MixerId,
                    MixerName = MixerName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Mixer paused");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause mixer");
                return OperationResult.Failure($"Pause failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mikseri duraklamadan çıkarır;
        /// </summary>
        public async Task<OperationResult> ResumeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (!_isPlaying)
                {
                    return OperationResult.Failure("Mixer is not playing");
                }

                if (!_isPaused)
                {
                    return OperationResult.Failure("Mixer is not paused");
                }

                _logger.LogInformation("Resuming mixer...");

                _audioOutput.Play();
                _isPaused = false;
                Status = MixerStatus.Playing;

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.Resumed,
                    MixerId = MixerId,
                    MixerName = MixerName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Mixer resumed");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume mixer");
                return OperationResult.Failure($"Resume failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Yeni kanal ekler;
        /// </summary>
        public async Task<OperationResult> AddChannelAsync(string channelId, MixerChannel channel, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                if (channel == null)
                {
                    return OperationResult.Failure("Channel cannot be null");
                }

                lock (_lockObject)
                {
                    if (_channels.ContainsKey(channelId))
                    {
                        return OperationResult.Failure($"Channel already exists: {channelId}");
                    }

                    // Kanalı yapılandır;
                    channel.Initialize(_sampleRate, _channels);

                    // Miksere bağla;
                    channel.OnAudioProcessed += HandleChannelAudioProcessed;
                    channel.OnMeterUpdate += HandleChannelMeterUpdate;
                    channel.OnPeakDetected += HandleChannelPeakDetected;

                    _channels[channelId] = channel;

                    // Master mixer'a ekle;
                    _masterMixer.AddMixerInput(channel.GetSampleProvider());
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelAdded,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    ChannelName = channel.ChannelName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Channel added: {channelId} ({channel.ChannelName})");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add channel: {channelId}");
                return OperationResult.Failure($"Add channel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanalı kaldırır;
        /// </summary>
        public async Task<OperationResult> RemoveChannelAsync(string channelId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                MixerChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    // Kanalı durdur;
                    channel.Stop();

                    // Event handler'larını kaldır;
                    channel.OnAudioProcessed -= HandleChannelAudioProcessed;
                    channel.OnMeterUpdate -= HandleChannelMeterUpdate;
                    channel.OnPeakDetected -= HandleChannelPeakDetected;

                    // Master mixer'dan çıkar;
                    // NAudio MixingSampleProvider RemoveMixerInput desteği yok, bu yüzden yeniden oluşturmamız gerekebilir;
                    // Basitçe kanalı devre dışı bırakıyoruz;
                    channel.SetVolume(0);

                    _channels.Remove(channelId);
                    _channelMeters.Remove(channelId);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelRemoved,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    ChannelName = channel.ChannelName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Channel removed: {channelId}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove channel: {channelId}");
                return OperationResult.Failure($"Remove channel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanal ses seviyesini ayarlar;
        /// </summary>
        public OperationResult SetChannelVolume(string channelId, float volume)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                if (volume < 0 || volume > 1)
                {
                    return OperationResult.Failure("Volume must be between 0 and 1");
                }

                MixerChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    channel.SetVolume(volume);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelVolumeChanged,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    Volume = volume,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set channel volume: {channelId}");
                return OperationResult.Failure($"Set channel volume failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanal pan'ını ayarlar;
        /// </summary>
        public OperationResult SetChannelPan(string channelId, float pan)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                if (pan < -1 || pan > 1)
                {
                    return OperationResult.Failure("Pan must be between -1 and 1");
                }

                MixerChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    channel.SetPan(pan);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelPanChanged,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    Pan = pan,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set channel pan: {channelId}");
                return OperationResult.Failure($"Set channel pan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanalı solo moduna alır;
        /// </summary>
        public OperationResult SetChannelSolo(string channelId, bool solo)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                MixerChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    channel.SetSolo(solo);

                    // Eğer herhangi bir kanal solo ise, master solo'yu aktif et;
                    _isSolo = _channels.Values.Any(c => c.IsSolo);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelSoloChanged,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    IsSolo = solo,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set channel solo: {channelId}");
                return OperationResult.Failure($"Set channel solo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanalı mute eder;
        /// </summary>
        public OperationResult SetChannelMute(string channelId, bool mute)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                MixerChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    channel.SetMute(mute);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelMuteChanged,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    IsMuted = mute,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set channel mute: {channelId}");
                return OperationResult.Failure($"Set channel mute failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Master ses seviyesini ayarlar;
        /// </summary>
        public OperationResult SetMasterVolume(float volume)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (volume < 0 || volume > 1)
                {
                    return OperationResult.Failure("Volume must be between 0 and 1");
                }

                _masterVolume = volume;

                // Tüm kanallara master volume'ü uygula;
                lock (_lockObject)
                {
                    foreach (var channel in _channels.Values)
                    {
                        channel.ApplyMasterVolume(_masterVolume);
                    }
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.MasterVolumeChanged,
                    MixerId = MixerId,
                    Volume = volume,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set master volume");
                return OperationResult.Failure($"Set master volume failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Master pan'ını ayarlar;
        /// </summary>
        public OperationResult SetMasterPan(float pan)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (pan < -1 || pan > 1)
                {
                    return OperationResult.Failure("Pan must be between -1 and 1");
                }

                _masterPan = pan;

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.MasterPanChanged,
                    MixerId = MixerId,
                    Pan = pan,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set master pan");
                return OperationResult.Failure($"Set master pan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Master'ı mute eder;
        /// </summary>
        public OperationResult SetMasterMute(bool mute)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                _isMuted = mute;

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.MasterMuteChanged,
                    MixerId = MixerId,
                    IsMuted = mute,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set master mute");
                return OperationResult.Failure($"Set master mute failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Efekt ekler;
        /// </summary>
        public OperationResult AddEffect(string effectId, AudioEffect effect)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(effectId))
                {
                    return OperationResult.Failure("Effect ID cannot be null or empty");
                }

                if (effect == null)
                {
                    return OperationResult.Failure("Effect cannot be null");
                }

                lock (_lockObject)
                {
                    if (_effects.ContainsKey(effectId))
                    {
                        return OperationResult.Failure($"Effect already exists: {effectId}");
                    }

                    _effects[effectId] = effect;
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.EffectAdded,
                    MixerId = MixerId,
                    EffectId = effectId,
                    EffectType = effect.EffectType,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Effect added: {effectId} ({effect.EffectType})");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add effect: {effectId}");
                return OperationResult.Failure($"Add effect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanal'a efekt uygular;
        /// </summary>
        public OperationResult ApplyEffectToChannel(string channelId, string effectId, EffectParameters parameters)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                if (string.IsNullOrWhiteSpace(effectId))
                {
                    return OperationResult.Failure("Effect ID cannot be null or empty");
                }

                MixerChannel channel;
                AudioEffect effect;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    if (!_effects.TryGetValue(effectId, out effect))
                    {
                        return OperationResult.Failure($"Effect not found: {effectId}");
                    }

                    channel.AddEffect(effect, parameters);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.EffectApplied,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    EffectId = effectId,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply effect {effectId} to channel {channelId}");
                return OperationResult.Failure($"Apply effect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Bus ekler;
        /// </summary>
        public OperationResult AddBus(string busId, AudioBus bus)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(busId))
                {
                    return OperationResult.Failure("Bus ID cannot be null or empty");
                }

                if (bus == null)
                {
                    return OperationResult.Failure("Bus cannot be null");
                }

                lock (_lockObject)
                {
                    if (_buses.ContainsKey(busId))
                    {
                        return OperationResult.Failure($"Bus already exists: {busId}");
                    }

                    _buses[busId] = bus;

                    // Bus'ı master mixer'a bağla;
                    _masterMixer.AddMixerInput(bus.GetSampleProvider());
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.BusAdded,
                    MixerId = MixerId,
                    BusId = busId,
                    BusName = bus.BusName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Bus added: {busId} ({bus.BusName})");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add bus: {busId}");
                return OperationResult.Failure($"Add bus failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanal'ı bus'a yönlendirir;
        /// </summary>
        public OperationResult RouteChannelToBus(string channelId, string busId, float sendLevel)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                if (string.IsNullOrWhiteSpace(busId))
                {
                    return OperationResult.Failure("Bus ID cannot be null or empty");
                }

                MixerChannel channel;
                AudioBus bus;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    if (!_buses.TryGetValue(busId, out bus))
                    {
                        return OperationResult.Failure($"Bus not found: {busId}");
                    }

                    channel.AddBusSend(bus, sendLevel);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelRouted,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    BusId = busId,
                    SendLevel = sendLevel,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to route channel {channelId} to bus {busId}");
                return OperationResult.Failure($"Route channel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Grup ekler;
        /// </summary>
        public OperationResult AddGroup(string groupId, AudioGroup group)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(groupId))
                {
                    return OperationResult.Failure("Group ID cannot be null or empty");
                }

                if (group == null)
                {
                    return OperationResult.Failure("Group cannot be null");
                }

                lock (_lockObject)
                {
                    if (_groups.ContainsKey(groupId))
                    {
                        return OperationResult.Failure($"Group already exists: {groupId}");
                    }

                    _groups[groupId] = group;
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.GroupAdded,
                    MixerId = MixerId,
                    GroupId = groupId,
                    GroupName = group.GroupName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Group added: {groupId} ({group.GroupName})");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add group: {groupId}");
                return OperationResult.Failure($"Add group failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanal'ı gruba ekler;
        /// </summary>
        public OperationResult AddChannelToGroup(string channelId, string groupId)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return OperationResult.Failure("Channel ID cannot be null or empty");
                }

                if (string.IsNullOrWhiteSpace(groupId))
                {
                    return OperationResult.Failure("Group ID cannot be null or empty");
                }

                MixerChannel channel;
                AudioGroup group;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }

                    if (!_groups.TryGetValue(groupId, out group))
                    {
                        return OperationResult.Failure($"Group not found: {groupId}");
                    }

                    group.AddChannel(channel);
                    channel.SetGroup(group);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ChannelGrouped,
                    MixerId = MixerId,
                    ChannelId = channelId,
                    GroupId = groupId,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add channel {channelId} to group {groupId}");
                return OperationResult.Failure($"Add channel to group failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Send (FX send) ekler;
        /// </summary>
        public OperationResult AddSend(AudioSend send)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (send == null)
                {
                    return OperationResult.Failure("Send cannot be null");
                }

                lock (_lockObject)
                {
                    _sends.Add(send);

                    // Send'i master mixer'a bağla;
                    _masterMixer.AddMixerInput(send.GetSampleProvider());
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.SendAdded,
                    MixerId = MixerId,
                    SendId = send.SendId,
                    SendName = send.SendName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Send added: {send.SendName}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add send");
                return OperationResult.Failure($"Add send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Return (FX return) ekler;
        /// </summary>
        public OperationResult AddReturn(AudioReturn audioReturn)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (audioReturn == null)
                {
                    return OperationResult.Failure("Return cannot be null");
                }

                lock (_lockObject)
                {
                    _returns.Add(audioReturn);
                }

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.ReturnAdded,
                    MixerId = MixerId,
                    ReturnId = audioReturn.ReturnId,
                    ReturnName = audioReturn.ReturnName,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Return added: {audioReturn.ReturnName}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add return");
                return OperationResult.Failure($"Add return failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Equalizer ayarlarını yapılandırır;
        /// </summary>
        public OperationResult ConfigureEqualizer(EqualizerSettings settings)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (settings == null)
                {
                    return OperationResult.Failure("Settings cannot be null");
                }

                _equalizer.Configure(settings);

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.EqualizerConfigured,
                    MixerId = MixerId,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure equalizer");
                return OperationResult.Failure($"Configure equalizer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Compressor ayarlarını yapılandırır;
        /// </summary>
        public OperationResult ConfigureCompressor(CompressorSettings settings)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (settings == null)
                {
                    return OperationResult.Failure("Settings cannot be null");
                }

                _compressor.Configure(settings);

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.CompressorConfigured,
                    MixerId = MixerId,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure compressor");
                return OperationResult.Failure($"Configure compressor failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Limiter ayarlarını yapılandırır;
        /// </summary>
        public OperationResult ConfigureLimiter(LimiterSettings settings)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (settings == null)
                {
                    return OperationResult.Failure("Settings cannot be null");
                }

                _limiter.Configure(settings);

                // Event tetikle;
                OnMixerEvent?.Invoke(this, new MixerEventArgs;
                {
                    EventType = MixerEventType.LimiterConfigured,
                    MixerId = MixerId,
                    Timestamp = DateTime.UtcNow;
                });

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure limiter");
                return OperationResult.Failure($"Configure limiter failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanal durumunu alır;
        /// </summary>
        public ChannelStatus GetChannelStatus(string channelId)
        {
            try
            {
                if (!_isInitialized)
                {
                    return ChannelStatus.Error;
                }

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    return ChannelStatus.Error;
                }

                MixerChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return ChannelStatus.NotFound;
                    }
                }

                MeterData meterData;
                _channelMeters.TryGetValue(channelId, out meterData);

                return new ChannelStatus;
                {
                    ChannelId = channelId,
                    ChannelName = channel.ChannelName,
                    IsActive = channel.IsActive,
                    IsMuted = channel.IsMuted,
                    IsSolo = channel.IsSolo,
                    Volume = channel.Volume,
                    Pan = channel.Pan,
                    PeakLevels = meterData?.PeakLevels ?? new float[2],
                    RMSLevels = meterData?.RMSLevels ?? new float[2],
                    ClipCount = meterData?.ClipCount ?? 0,
                    EffectCount = channel.EffectCount,
                    BusCount = channel.BusCount;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get channel status: {channelId}");
                return ChannelStatus.Error;
            }
        }

        /// <summary>
        /// Tüm kanalların durumunu alır;
        /// </summary>
        public List<ChannelStatus> GetAllChannelStatuses()
        {
            try
            {
                if (!_isInitialized)
                {
                    return new List<ChannelStatus>();
                }

                var statuses = new List<ChannelStatus>();

                lock (_lockObject)
                {
                    foreach (var channelId in _channels.Keys)
                    {
                        statuses.Add(GetChannelStatus(channelId));
                    }
                }

                return statuses;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all channel statuses");
                return new List<ChannelStatus>();
            }
        }

        /// <summary>
        /// Bus durumunu alır;
        /// </summary>
        public BusStatus GetBusStatus(string busId)
        {
            try
            {
                if (!_isInitialized)
                {
                    return BusStatus.Error;
                }

                if (string.IsNullOrWhiteSpace(busId))
                {
                    return BusStatus.Error;
                }

                AudioBus bus;

                lock (_lockObject)
                {
                    if (!_buses.TryGetValue(busId, out bus))
                    {
                        return BusStatus.NotFound;
                    }
                }

                return new BusStatus;
                {
                    BusId = busId,
                    BusName = bus.BusName,
                    ChannelCount = bus.ChannelCount,
                    Volume = bus.Volume,
                    IsActive = bus.IsActive,
                    Effects = bus.Effects;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get bus status: {busId}");
                return BusStatus.Error;
            }
        }

        /// <summary>
        /// Grup durumunu alır;
        /// </summary>
        public GroupStatus GetGroupStatus(string groupId)
        {
            try
            {
                if (!_isInitialized)
                {
                    return GroupStatus.Error;
                }

                if (string.IsNullOrWhiteSpace(groupId))
                {
                    return GroupStatus.Error;
                }

                AudioGroup group;

                lock (_lockObject)
                {
                    if (!_groups.TryGetValue(groupId, out group))
                    {
                        return GroupStatus.NotFound;
                    }
                }

                return new GroupStatus;
                {
                    GroupId = groupId,
                    GroupName = group.GroupName,
                    ChannelCount = group.ChannelCount,
                    Volume = group.Volume,
                    IsMuted = group.IsMuted,
                    IsSolo = group.IsSolo;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get group status: {groupId}");
                return GroupStatus.Error;
            }
        }

        /// <summary>
        /// Mikser metriklerini alır;
        /// </summary>
        public MixerMetrics GetMetrics()
        {
            return new MixerMetrics;
            {
                MixerId = MixerId,
                MixerName = MixerName,
                Status = Status,
                ChannelCount = ChannelCount,
                EffectCount = EffectCount,
                BusCount = BusCount,
                GroupCount = GroupCount,
                SendCount = _sends.Count,
                ReturnCount = _returns.Count,
                IsPlaying = IsPlaying,
                IsPaused = IsPaused,
                IsMuted = IsMuted,
                IsSolo = IsSolo,
                MasterVolume = MasterVolume,
                MasterPan = MasterPan,
                MasterPeakLeft = MasterPeakLevels[0],
                MasterPeakRight = MasterPeakLevels[1],
                MasterRMSLeft = MasterRMSLevels[0],
                MasterRMSRight = MasterRMSLevels[1],
                SampleRate = _sampleRate,
                Channels = _channels,
                BitsPerSample = _bitsPerSample,
                TotalSamplesProcessed = TotalSamplesProcessed,
                Latency = Latency,
                Uptime = Uptime,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Mikser durumunu kaydeder;
        /// </summary>
        public async Task<OperationResult> SaveStateAsync(string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                _logger.LogInformation($"Saving mixer state to: {filePath}");

                var state = new MixerState;
                {
                    MixerId = MixerId,
                    MixerName = MixerName,
                    Configuration = Configuration,
                    MasterVolume = _masterVolume,
                    MasterPan = _masterPan,
                    IsMuted = _isMuted,
                    Channels = new Dictionary<string, ChannelState>(),
                    Buses = new Dictionary<string, BusState>(),
                    Groups = new Dictionary<string, GroupState>(),
                    Effects = new Dictionary<string, EffectState>(),
                    Saves = new List<SendState>(),
                    Returns = new List<ReturnState>(),
                    EqualizerSettings = _equalizer.GetSettings(),
                    CompressorSettings = _compressor.GetSettings(),
                    LimiterSettings = _limiter.GetSettings(),
                    SaveTime = DateTime.UtcNow;
                };

                // Kanal durumlarını kaydet;
                lock (_lockObject)
                {
                    foreach (var kvp in _channels)
                    {
                        state.Channels[kvp.Key] = kvp.Value.GetState();
                    }

                    foreach (var kvp in _buses)
                    {
                        state.Buses[kvp.Key] = kvp.Value.GetState();
                    }

                    foreach (var kvp in _groups)
                    {
                        state.Groups[kvp.Key] = kvp.Value.GetState();
                    }

                    foreach (var kvp in _effects)
                    {
                        state.Effects[kvp.Key] = kvp.Value.GetState();
                    }

                    foreach (var send in _sends)
                    {
                        state.Saves.Add(send.GetState());
                    }

                    foreach (var audioReturn in _returns)
                    {
                        state.Returns.Add(audioReturn.GetState());
                    }
                }

                // JSON olarak kaydet;
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                });

                await File.WriteAllTextAsync(filePath, json, cancellationToken);

                _logger.LogInformation($"Mixer state saved: {filePath}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save mixer state");
                return OperationResult.Failure($"Save state failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mikser durumunu yükler;
        /// </summary>
        public async Task<OperationResult> LoadStateAsync(string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                if (!File.Exists(filePath))
                {
                    return OperationResult.Failure($"State file not found: {filePath}");
                }

                _logger.LogInformation($"Loading mixer state from: {filePath}");

                // Mevcut durumu temizle;
                await ClearStateAsync(cancellationToken);

                // JSON'u yükle;
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var state = JsonSerializer.Deserialize<MixerState>(json);

                if (state == null)
                {
                    return OperationResult.Failure("Failed to parse mixer state");
                }

                // Konfigürasyonu yükle;
                Configuration = state.Configuration;

                // Master ayarlarını yükle;
                _masterVolume = state.MasterVolume;
                _masterPan = state.MasterPan;
                _isMuted = state.IsMuted;

                // Efekt ayarlarını yükle;
                if (state.EqualizerSettings != null)
                    _equalizer.Configure(state.EqualizerSettings);

                if (state.CompressorSettings != null)
                    _compressor.Configure(state.CompressorSettings);

                if (state.LimiterSettings != null)
                    _limiter.Configure(state.LimiterSettings);

                // Kanalları yükle;
                lock (_lockObject)
                {
                    foreach (var kvp in state.Channels)
                    {
                        var channel = new MixerChannel(kvp.Key, kvp.Value.ChannelName);
                        channel.LoadState(kvp.Value);
                        _channels[kvp.Key] = channel;
                    }

                    // Bus'ları yükle;
                    foreach (var kvp in state.Buses)
                    {
                        var bus = new AudioBus(kvp.Key, kvp.Value.BusName);
                        bus.LoadState(kvp.Value);
                        _buses[kvp.Key] = bus;
                    }

                    // Grupları yükle;
                    foreach (var kvp in state.Groups)
                    {
                        var group = new AudioGroup(kvp.Key, kvp.Value.GroupName);
                        group.LoadState(kvp.Value);
                        _groups[kvp.Key] = group;
                    }

                    // Efektleri yükle;
                    foreach (var kvp in state.Effects)
                    {
                        var effect = AudioEffectFactory.CreateEffect(kvp.Value.EffectType);
                        effect.LoadState(kvp.Value);
                        _effects[kvp.Key] = effect;
                    }
                }

                _logger.LogInformation($"Mixer state loaded: {_channels.Count} channels, {_buses.Count} buses");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load mixer state");
                return OperationResult.Failure($"Load state failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mikser durumunu temizler;
        /// </summary>
        public async Task<OperationResult> ClearStateAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Mixer is not initialized");
                }

                _logger.LogInformation("Clearing mixer state...");

                // Mikseri durdur;
                if (_isPlaying)
                {
                    await StopAsync(cancellationToken);
                }

                // Tüm kanalları kaldır;
                lock (_lockObject)
                {
                    var channelIds = _channels.Keys.ToList();
                    foreach (var channelId in channelIds)
                    {
                        await RemoveChannelAsync(channelId, cancellationToken);
                    }

                    _buses.Clear();
                    _groups.Clear();
                    _effects.Clear();
                    _sends.Clear();
                    _returns.Clear();
                    _channelMeters.Clear();
                }

                // Master ayarlarını sıfırla;
                _masterVolume = 1.0f;
                _masterPan = 0.0f;
                _isMuted = false;
                _isSolo = false;

                // Efekt ayarlarını sıfırla;
                _equalizer.Reset();
                _compressor.Reset();
                _limiter.Reset();

                _logger.LogInformation("Mixer state cleared");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear mixer state");
                return OperationResult.Failure($"Clear state failed: {ex.Message}");
            }
        }
        #endregion;

        #region Private Methods;
        private MixerConfiguration GetDefaultConfiguration()
        {
            return new MixerConfiguration;
            {
                SampleRate = 44100,
                Channels = 2,
                BitsPerSample = 16,
                BufferSize = 4096,
                MeterUpdateRate = 30, // Hz;
                MeterBufferSize = 100,
                EnableEqualizer = true,
                EnableCompressor = true,
                EnableLimiter = true,
                EnableReverb = false,
                EnableDelay = false,
                EnableChorus = false,
                EnableFlanger = false,
                EnableDistortion = false,
                MaxChannels = 32,
                MaxBuses = 8,
                MaxGroups = 4,
                MaxEffects = 16,
                DefaultVolume = 0.8f,
                LogLevel = LogLevel.Info;
            };
        }

        private async Task InitializeEffectsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Varsayılan efektleri yapılandır;
                if (Configuration.EnableEqualizer)
                {
                    var eqSettings = new EqualizerSettings;
                    {
                        Bands = new List<EqualizerBand>
                        {
                            new EqualizerBand { Frequency = 32, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 64, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 125, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 250, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 500, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 1000, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 2000, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 4000, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 8000, Gain = 0, Bandwidth = 1 },
                            new EqualizerBand { Frequency = 16000, Gain = 0, Bandwidth = 1 }
                        },
                        IsEnabled = true;
                    };

                    _equalizer.Configure(eqSettings);
                }

                if (Configuration.EnableCompressor)
                {
                    var compressorSettings = new CompressorSettings;
                    {
                        Threshold = -20,
                        Ratio = 4,
                        Attack = 10,
                        Release = 100,
                        MakeupGain = 0,
                        IsEnabled = true;
                    };

                    _compressor.Configure(compressorSettings);
                }

                if (Configuration.EnableLimiter)
                {
                    var limiterSettings = new LimiterSettings;
                    {
                        Threshold = -1,
                        Attack = 1,
                        Release = 50,
                        IsEnabled = true;
                    };

                    _limiter.Configure(limiterSettings);
                }

                _logger.LogDebug("Effects initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize effects");
                throw;
            }
        }

        private async Task CreateDefaultChannelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Varsayılan kanalları oluştur;
                var defaultChannels = new[]
                {
                    new MixerChannel("ch1", "Channel 1"),
                    new MixerChannel("ch2", "Channel 2"),
                    new MixerChannel("ch3", "Channel 3"),
                    new MixerChannel("ch4", "Channel 4"),
                    new MixerChannel("ch5", "Channel 5"),
                    new MixerChannel("ch6", "Channel 6"),
                    new MixerChannel("ch7", "Channel 7"),
                    new MixerChannel("ch8", "Channel 8")
                };

                foreach (var channel in defaultChannels)
                {
                    await AddChannelAsync(channel.ChannelId, channel, cancellationToken);
                }

                _logger.LogDebug($"Created {defaultChannels.Length} default channels");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create default channels");
                throw;
            }
        }

        private async Task CreateDefaultBusesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Varsayılan bus'ları oluştur;
                var defaultBuses = new[]
                {
                    new AudioBus("bus1", "FX Bus 1"),
                    new AudioBus("bus2", "FX Bus 2"),
                    new AudioBus("bus3", "Submix 1"),
                    new AudioBus("bus4", "Submix 2")
                };

                foreach (var bus in defaultBuses)
                {
                    AddBus(bus.BusId, bus);
                }

                _logger.LogDebug($"Created {defaultBuses.Length} default buses");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create default buses");
                throw;
            }
        }

        private async Task CreateDefaultGroupsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Varsayılan grupları oluştur;
                var defaultGroups = new[]
                {
                    new AudioGroup("group1", "Drums"),
                    new AudioGroup("group2", "Vocals"),
                    new AudioGroup("group3", "Guitars"),
                    new AudioGroup("group4", "Keys")
                };

                foreach (var group in defaultGroups)
                {
                    AddGroup(group.GroupId, group);
                }

                _logger.LogDebug($"Created {defaultGroups.Length} default groups");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create default groups");
                throw;
            }
        }

        private void StartMixingTask()
        {
            _mixingCancellationTokenSource = new CancellationTokenSource();
            _mixingTask = Task.Run(async () => await ProcessMixingAsync(_mixingCancellationTokenSource.Token));

            _logger.LogDebug("Mixing task started");
        }

        private void StopMixingTask()
        {
            _mixingCancellationTokenSource?.Cancel();

            try
            {
                _mixingTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex, "Error stopping mixing task");
            }

            _mixingCancellationTokenSource?.Dispose();
            _mixingCancellationTokenSource = null;

            _logger.LogDebug("Mixing task stopped");
        }

        private async Task ProcessMixingAsync(CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new float[Configuration.BufferSize * _channels];
                var meterUpdateInterval = 1000 / Configuration.MeterUpdateRate;
                var lastMeterUpdate = DateTime.UtcNow;

                while (!cancellationToken.IsCancellationRequested && _isPlaying)
                {
                    try
                    {
                        // Master mixer'dan audio okuma;
                        int samplesRead = _masterMixer.Read(buffer, 0, buffer.Length);

                        if (samplesRead > 0)
                        {
                            // Efektleri uygula;
                            ProcessEffects(buffer, samplesRead);

                            // Master volume ve pan uygula;
                            ProcessMaster(buffer, samplesRead);

                            // Output buffer'a yaz;
                            WriteToOutputBuffer(buffer, samplesRead);

                            // Metreleri güncelle;
                            UpdateMeters(buffer, samplesRead);

                            // İstatistikleri güncelle;
                            _totalSamplesMixed += samplesRead / _channels;
                        }

                        // Metre güncelleme hızını kontrol et;
                        if ((DateTime.UtcNow - lastMeterUpdate).TotalMilliseconds >= meterUpdateInterval)
                        {
                            UpdateMasterMeters();
                            lastMeterUpdate = DateTime.UtcNow;
                        }

                        await Task.Delay(1, cancellationToken); // Yield;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in mixing loop");
                        await Task.Delay(100, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mixing task error");
            }
        }

        private void ProcessEffects(float[] buffer, int samplesRead)
        {
            try
            {
                // Equalizer;
                if (Configuration.EnableEqualizer && _equalizer.IsEnabled)
                {
                    _equalizer.Process(buffer, samplesRead);
                }

                // Compressor;
                if (Configuration.EnableCompressor && _compressor.IsEnabled)
                {
                    _compressor.Process(buffer, samplesRead);
                }

                // Limiter;
                if (Configuration.EnableLimiter && _limiter.IsEnabled)
                {
                    _limiter.Process(buffer, samplesRead);
                }

                // Reverb;
                if (Configuration.EnableReverb && _reverb.IsEnabled)
                {
                    _reverb.Process(buffer, samplesRead);
                }

                // Delay;
                if (Configuration.EnableDelay && _delay.IsEnabled)
                {
                    _delay.Process(buffer, samplesRead);
                }

                // Chorus;
                if (Configuration.EnableChorus && _chorus.IsEnabled)
                {
                    _chorus.Process(buffer, samplesRead);
                }

                // Flanger;
                if (Configuration.EnableFlanger && _flanger.IsEnabled)
                {
                    _flanger.Process(buffer, samplesRead);
                }

                // Distortion;
                if (Configuration.EnableDistortion && _distortion.IsEnabled)
                {
                    _distortion.Process(buffer, samplesRead);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing effects");
            }
        }

        private void ProcessMaster(float[] buffer, int samplesRead)
        {
            try
            {
                // Master volume uygula;
                if (!_isMuted && _masterVolume != 1.0f)
                {
                    for (int i = 0; i < samplesRead; i++)
                    {
                        buffer[i] *= _masterVolume;
                    }
                }

                // Mute kontrolü;
                if (_isMuted)
                {
                    Array.Clear(buffer, 0, samplesRead);
                }

                // Master pan uygula;
                if (_masterPan != 0.0f)
                {
                    ApplyPan(buffer, samplesRead, _masterPan);
                }

                // Solo kontrolü;
                if (_isSolo)
                {
                    // Solo modunda sadece solo kanalları duyulur;
                    // Bu implementasyon kanal seviyesinde yapılıyor;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing master");
            }
        }

        private void ApplyPan(float[] buffer, int samplesRead, float pan)
        {
            // Basit pan uygulama (stereo için)
            if (_channels == 2)
            {
                for (int i = 0; i < samplesRead; i += 2)
                {
                    float left = buffer[i];
                    float right = buffer[i + 1];

                    // Pan law: -6dB center;
                    float leftGain = (float)Math.Cos((pan + 1) * Math.PI / 4);
                    float rightGain = (float)Math.Sin((pan + 1) * Math.PI / 4);

                    buffer[i] = left * leftGain;
                    buffer[i + 1] = right * rightGain;
                }
            }
        }

        private void WriteToOutputBuffer(float[] buffer, int samplesRead)
        {
            try
            {
                // Float'tan byte'a dönüştür;
                byte[] byteBuffer = new byte[samplesRead * sizeof(float)];
                Buffer.BlockCopy(buffer, 0, byteBuffer, 0, byteBuffer.Length);

                // Output buffer'a yaz;
                _outputBuffer.AddSamples(byteBuffer, 0, byteBuffer.Length);

                // Gecikmeyi hesapla;
                Latency = (int)(_outputBuffer.BufferedBytes / (_sampleRate * _channels * sizeof(float)) * 1000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing to output buffer");
            }
        }

        private void UpdateMeters(float[] buffer, int samplesRead)
        {
            try
            {
                // Buffer'ı metre buffer'ına ekle;
                var frame = new float[samplesRead];
                Array.Copy(buffer, frame, samplesRead);
                _meterBuffer.Write(frame);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating meters");
            }
        }

        private void UpdateMasterMeters()
        {
            try
            {
                // Son 100 frame'i al;
                var frames = _meterBuffer.ReadAll();
                if (frames.Count == 0) return;

                float peakLeft = float.MinValue;
                float peakRight = float.MinValue;
                float sumLeft = 0;
                float sumRight = 0;
                int sampleCount = 0;

                foreach (var frame in frames)
                {
                    for (int i = 0; i < frame.Length; i += _channels)
                    {
                        float left = frame[i];
                        float right = (_channels > 1) ? frame[i + 1] : left;

                        // Peak değerleri;
                        peakLeft = Math.Max(peakLeft, Math.Abs(left));
                        peakRight = Math.Max(peakRight, Math.Abs(right));

                        // RMS için kareler toplamı;
                        sumLeft += left * left;
                        sumRight += right * right;

                        sampleCount++;
                    }
                }

                // RMS hesapla;
                float rmsLeft = (float)Math.Sqrt(sumLeft / sampleCount);
                float rmsRight = (float)Math.Sqrt(sumRight / sampleCount);

                // Master metreleri güncelle;
                _masterPeakLevels[0] = peakLeft;
                _masterPeakLevels[1] = peakRight;
                _masterRMSLevels[0] = rmsLeft;
                _masterRMSLevels[1] = rmsRight;

                // Event tetikle;
                OnMeterUpdate?.Invoke(this, new MeterDataEventArgs;
                {
                    MixerId = MixerId,
                    PeakLeft = peakLeft,
                    PeakRight = peakRight,
                    RMSLeft = rmsLeft,
                    RMSRight = rmsRight,
                    Timestamp = DateTime.UtcNow;
                });

                // Clip detection;
                if (peakLeft >= 0.99f || peakRight >= 0.99f)
                {
                    OnPeakDetected?.Invoke(this, new PeakEventArgs;
                    {
                        MixerId = MixerId,
                        PeakLeft = peakLeft,
                        PeakRight = peakRight,
                        IsClipping = true,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating master meters");
            }
        }

        private async Task StartAllChannelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var tasks = new List<Task>();

                lock (_lockObject)
                {
                    foreach (var channel in _channels.Values)
                    {
                        tasks.Add(channel.StartAsync(cancellationToken));
                    }

                    foreach (var bus in _buses.Values)
                    {
                        bus.Start();
                    }

                    foreach (var group in _groups.Values)
                    {
                        group.Start();
                    }

                    foreach (var send in _sends)
                    {
                        send.Start();
                    }
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting all channels");
                throw;
            }
        }

        private async Task StopAllChannelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var tasks = new List<Task>();

                lock (_lockObject)
                {
                    foreach (var channel in _channels.Values)
                    {
                        tasks.Add(channel.StopAsync(cancellationToken));
                    }

                    foreach (var bus in _buses.Values)
                    {
                        bus.Stop();
                    }

                    foreach (var group in _groups.Values)
                    {
                        group.Stop();
                    }

                    foreach (var send in _sends)
                    {
                        send.Stop();
                    }
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping all channels");
                throw;
            }
        }

        private void HandleChannelAudioProcessed(object sender, ChannelAudioEventArgs e)
        {
            // Kanal audio event'lerini işle;
            // Burada kanal seviyesinde işlemler yapılabilir;
        }

        private void HandleChannelMeterUpdate(object sender, ChannelMeterEventArgs e)
        {
            try
            {
                // Kanal metre verilerini güncelle;
                var meterData = new MeterData;
                {
                    ChannelId = e.ChannelId,
                    PeakLevels = e.PeakLevels,
                    RMSLevels = e.RMSLevels,
                    ClipCount = e.ClipCount,
                    Timestamp = e.Timestamp;
                };

                lock (_lockObject)
                {
                    _channelMeters[e.ChannelId] = meterData;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling channel meter update");
            }
        }

        private void HandleChannelPeakDetected(object sender, ChannelPeakEventArgs e)
        {
            // Kanal peak event'lerini işle;
            // Event'i yukarıya ilet;
            OnPeakDetected?.Invoke(this, new PeakEventArgs;
            {
                MixerId = MixerId,
                ChannelId = e.ChannelId,
                PeakLeft = e.PeakLeft,
                PeakRight = e.PeakRight,
                IsClipping = e.IsClipping,
                Timestamp = e.Timestamp;
            });
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
                    // Kaynakları serbest bırak;
                    StopAsync().Wait(TimeSpan.FromSeconds(5));

                    _mixingCancellationTokenSource?.Cancel();
                    _mixingCancellationTokenSource?.Dispose();

                    _audioOutput?.Stop();
                    _audioOutput?.Dispose();

                    // Tüm kanalları temizle;
                    lock (_lockObject)
                    {
                        foreach (var channel in _channels.Values)
                        {
                            channel.Dispose();
                        }
                        _channels.Clear();

                        foreach (var bus in _buses.Values)
                        {
                            bus.Dispose();
                        }
                        _buses.Clear();

                        foreach (var group in _groups.Values)
                        {
                            group.Dispose();
                        }
                        _groups.Clear();

                        foreach (var effect in _effects.Values)
                        {
                            effect.Dispose();
                        }
                        _effects.Clear();

                        foreach (var send in _sends)
                        {
                            send.Dispose();
                        }
                        _sends.Clear();

                        foreach (var audioReturn in _returns)
                        {
                            audioReturn.Dispose();
                        }
                        _returns.Clear();
                    }

                    // Efektleri temizle;
                    _equalizer?.Dispose();
                    _compressor?.Dispose();
                    _limiter?.Dispose();
                    _reverb?.Dispose();
                    _delay?.Dispose();
                    _chorus?.Dispose();
                    _flanger?.Dispose();
                    _distortion?.Dispose();

                    Status = MixerStatus.Disposed;

                    _logger.LogInformation("Mixer disposed");
                }

                _isDisposed = true;
            }
        }

        ~Mixer()
        {
            Dispose(false);
        }
        #endregion;

        #region Supporting Classes and Enums;
        public enum MixerStatus;
        {
            Stopped,
            Initializing,
            Starting,
            Playing,
            Paused,
            Stopping,
            Error,
            Disposed;
        }

        public enum MixerEventType;
        {
            Started,
            Stopped,
            Paused,
            Resumed,
            ChannelAdded,
            ChannelRemoved,
            ChannelVolumeChanged,
            ChannelPanChanged,
            ChannelSoloChanged,
            ChannelMuteChanged,
            ChannelRouted,
            ChannelGrouped,
            EffectAdded,
            EffectApplied,
            BusAdded,
            GroupAdded,
            SendAdded,
            ReturnAdded,
            MasterVolumeChanged,
            MasterPanChanged,
            MasterMuteChanged,
            EqualizerConfigured,
            CompressorConfigured,
            LimiterConfigured;
        }

        public class MixerConfiguration;
        {
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int BitsPerSample { get; set; }
            public int BufferSize { get; set; }
            public int MeterUpdateRate { get; set; }
            public int MeterBufferSize { get; set; }
            public bool EnableEqualizer { get; set; }
            public bool EnableCompressor { get; set; }
            public bool EnableLimiter { get; set; }
            public bool EnableReverb { get; set; }
            public bool EnableDelay { get; set; }
            public bool EnableChorus { get; set; }
            public bool EnableFlanger { get; set; }
            public bool EnableDistortion { get; set; }
            public int MaxChannels { get; set; }
            public int MaxBuses { get; set; }
            public int MaxGroups { get; set; }
            public int MaxEffects { get; set; }
            public float DefaultVolume { get; set; }
            public LogLevel LogLevel { get; set; }
        }

        public class ChannelStatus;
        {
            public string ChannelId { get; set; }
            public string ChannelName { get; set; }
            public bool IsActive { get; set; }
            public bool IsMuted { get; set; }
            public bool IsSolo { get; set; }
            public float Volume { get; set; }
            public float Pan { get; set; }
            public float[] PeakLevels { get; set; }
            public float[] RMSLevels { get; set; }
            public int ClipCount { get; set; }
            public int EffectCount { get; set; }
            public int BusCount { get; set; }
            public static ChannelStatus Error => new ChannelStatus { ChannelName = "Error" };
            public static ChannelStatus NotFound => new ChannelStatus { ChannelName = "Not Found" };
        }

        public class BusStatus;
        {
            public string BusId { get; set; }
            public string BusName { get; set; }
            public int ChannelCount { get; set; }
            public float Volume { get; set; }
            public bool IsActive { get; set; }
            public List<string> Effects { get; set; }
            public static BusStatus Error => new BusStatus { BusName = "Error" };
            public static BusStatus NotFound => new BusStatus { BusName = "Not Found" };
        }

        public class GroupStatus;
        {
            public string GroupId { get; set; }
            public string GroupName { get; set; }
            public int ChannelCount { get; set; }
            public float Volume { get; set; }
            public bool IsMuted { get; set; }
            public bool IsSolo { get; set; }
            public static GroupStatus Error => new GroupStatus { GroupName = "Error" };
            public static GroupStatus NotFound => new GroupStatus { GroupName = "Not Found" };
        }

        public class MixerMetrics;
        {
            public string MixerId { get; set; }
            public string MixerName { get; set; }
            public MixerStatus Status { get; set; }
            public int ChannelCount { get; set; }
            public int EffectCount { get; set; }
            public int BusCount { get; set; }
            public int GroupCount { get; set; }
            public int SendCount { get; set; }
            public int ReturnCount { get; set; }
            public bool IsPlaying { get; set; }
            public bool IsPaused { get; set; }
            public bool IsMuted { get; set; }
            public bool IsSolo { get; set; }
            public float MasterVolume { get; set; }
            public float MasterPan { get; set; }
            public float MasterPeakLeft { get; set; }
            public float MasterPeakRight { get; set; }
            public float MasterRMSLeft { get; set; }
            public float MasterRMSRight { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int BitsPerSample { get; set; }
            public long TotalSamplesProcessed { get; set; }
            public int Latency { get; set; }
            public TimeSpan Uptime { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class MixerState;
        {
            public string MixerId { get; set; }
            public string MixerName { get; set; }
            public MixerConfiguration Configuration { get; set; }
            public float MasterVolume { get; set; }
            public float MasterPan { get; set; }
            public bool IsMuted { get; set; }
            public Dictionary<string, ChannelState> Channels { get; set; }
            public Dictionary<string, BusState> Buses { get; set; }
            public Dictionary<string, GroupState> Groups { get; set; }
            public Dictionary<string, EffectState> Effects { get; set; }
            public List<SendState> Saves { get; set; }
            public List<ReturnState> Returns { get; set; }
            public EqualizerSettings EqualizerSettings { get; set; }
            public CompressorSettings CompressorSettings { get; set; }
            public LimiterSettings LimiterSettings { get; set; }
            public DateTime SaveTime { get; set; }
        }

        public class MeterData;
        {
            public string ChannelId { get; set; }
            public float[] PeakLevels { get; set; }
            public float[] RMSLevels { get; set; }
            public int ClipCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class RingBuffer<T>
        {
            private readonly T[] _buffer;
            private readonly int _capacity;
            private int _head;
            private int _tail;
            private int _count;
            private readonly object _lockObject = new object();

            public RingBuffer(int capacity)
            {
                _capacity = capacity;
                _buffer = new T[capacity];
                _head = 0;
                _tail = 0;
                _count = 0;
            }

            public void Write(T item)
            {
                lock (_lockObject)
                {
                    _buffer[_head] = item;
                    _head = (_head + 1) % _capacity;

                    if (_count == _capacity)
                    {
                        _tail = (_tail + 1) % _capacity;
                    }
                    else;
                    {
                        _count++;
                    }
                }
            }

            public List<T> ReadAll()
            {
                lock (_lockObject)
                {
                    var items = new List<T>(_count);

                    for (int i = 0; i < _count; i++)
                    {
                        int index = (_tail + i) % _capacity;
                        items.Add(_buffer[index]);
                    }

                    return items;
                }
            }

            public void Clear()
            {
                lock (_lockObject)
                {
                    _head = 0;
                    _tail = 0;
                    _count = 0;
                    Array.Clear(_buffer, 0, _buffer.Length);
                }
            }
        }
        #endregion;
    }

    #region Interfaces;
    public interface IMixer : IDisposable
    {
        Task<OperationResult> InitializeAsync(MixerConfiguration configuration = null, CancellationToken cancellationToken = default);
        Task<OperationResult> StartAsync(CancellationToken cancellationToken = default);
        Task<OperationResult> StopAsync(CancellationToken cancellationToken = default);
        Task<OperationResult> PauseAsync(CancellationToken cancellationToken = default);
        Task<OperationResult> ResumeAsync(CancellationToken cancellationToken = default);
        Task<OperationResult> AddChannelAsync(string channelId, MixerChannel channel, CancellationToken cancellationToken = default);
        Task<OperationResult> RemoveChannelAsync(string channelId, CancellationToken cancellationToken = default);
        OperationResult SetChannelVolume(string channelId, float volume);
        OperationResult SetChannelPan(string channelId, float pan);
        OperationResult SetChannelSolo(string channelId, bool solo);
        OperationResult SetChannelMute(string channelId, bool mute);
        OperationResult SetMasterVolume(float volume);
        OperationResult SetMasterPan(float pan);
        OperationResult SetMasterMute(bool mute);
        OperationResult AddEffect(string effectId, AudioEffect effect);
        OperationResult ApplyEffectToChannel(string channelId, string effectId, EffectParameters parameters);
        OperationResult AddBus(string busId, AudioBus bus);
        OperationResult RouteChannelToBus(string channelId, string busId, float sendLevel);
        OperationResult AddGroup(string groupId, AudioGroup group);
        OperationResult AddChannelToGroup(string channelId, string groupId);
        OperationResult AddSend(AudioSend send);
        OperationResult AddReturn(AudioReturn audioReturn);
        OperationResult ConfigureEqualizer(EqualizerSettings settings);
        OperationResult ConfigureCompressor(CompressorSettings settings);
        OperationResult ConfigureLimiter(LimiterSettings settings);
        ChannelStatus GetChannelStatus(string channelId);
        List<ChannelStatus> GetAllChannelStatuses();
        BusStatus GetBusStatus(string busId);
        GroupStatus GetGroupStatus(string groupId);
        MixerMetrics GetMetrics();
        Task<OperationResult> SaveStateAsync(string filePath, CancellationToken cancellationToken = default);
        Task<OperationResult> LoadStateAsync(string filePath, CancellationToken cancellationToken = default);
        Task<OperationResult> ClearStateAsync(CancellationToken cancellationToken = default);

        event EventHandler<MixerEventArgs> OnMixerEvent;
        event EventHandler<MeterDataEventArgs> OnMeterUpdate;
        event EventHandler<PeakEventArgs> OnPeakDetected;
    }
    #endregion;
}
