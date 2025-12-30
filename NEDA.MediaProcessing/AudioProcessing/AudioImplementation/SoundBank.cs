using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;
using NAudio.Vorbis;
using NVorbis;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Security.Encryption;

namespace NEDA.MediaProcessing.AudioProcessing;
{
    /// <summary>
    /// Ses efektleri, müzik ve ses kaynaklarını yöneten gelişmiş Sound Bank sistemi;
    /// </summary>
    public class SoundBank : ISoundBank, IDisposable;
    {
        #region Fields;
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly Dictionary<string, SoundAsset> _soundAssets;
        private readonly Dictionary<string, SoundCategory> _categories;
        private readonly Dictionary<string, SoundPool> _soundPools;
        private readonly Dictionary<int, SoundChannel> _channels;
        private readonly Dictionary<string, Playlist> _playlists;
        private readonly Dictionary<string, AudioMixer> _mixers;
        private readonly Dictionary<string, StreamedSound> _streamingSounds;
        private readonly List<SoundFilter> _filters;
        private readonly object _lockObject = new object();
        private readonly WasapiOut _audioOutput;
        private readonly MixingSampleProvider _mixerProvider;
        private readonly string _bankPath;
        private bool _isInitialized;
        private bool _isDisposed;
        private int _nextChannelId;
        private int _maxChannels;
        private float _masterVolume;
        private bool _isMuted;
        private MemoryCache _memoryCache;
        private StreamingManager _streamingManager;
        private CompressionEngine _compressionEngine;
        #endregion;

        #region Properties;
        public string BankId { get; private set; }
        public string BankName { get; private set; }
        public SoundBankStatus Status { get; private set; }
        public SoundBankConfiguration Configuration { get; private set; }
        public int TotalSounds => _soundAssets.Count;
        public int LoadedSounds => _soundAssets.Values.Count(s => s.IsLoaded);
        public int ActiveChannels => _channels.Values.Count(c => c.IsPlaying);
        public float UsedMemory => CalculateUsedMemory();
        public long TotalBytesStreamed { get; private set; }
        public int TotalSoundsPlayed { get; private set; }
        public event EventHandler<SoundBankEventArgs> OnSoundEvent;
        public event EventHandler<BankLoadProgressEventArgs> OnLoadProgress;
        #endregion;

        #region Constructor;
        public SoundBank(
            ILogger logger,
            IErrorReporter errorReporter,
            ICryptoEngine cryptoEngine = null,
            string bankPath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _cryptoEngine = cryptoEngine;

            BankId = Guid.NewGuid().ToString();
            BankName = "DefaultSoundBank";
            Status = SoundBankStatus.Unloaded;

            _soundAssets = new Dictionary<string, SoundAsset>();
            _categories = new Dictionary<string, SoundCategory>();
            _soundPools = new Dictionary<string, SoundPool>();
            _channels = new Dictionary<int, SoundChannel>();
            _playlists = new Dictionary<string, Playlist>();
            _mixers = new Dictionary<string, AudioMixer>();
            _streamingSounds = new Dictionary<string, StreamedSound>();
            _filters = new List<SoundFilter>();

            _bankPath = bankPath ?? "SoundBanks/";
            _masterVolume = 1.0f;
            _maxChannels = 32;
            _nextChannelId = 1;

            // Audio output setup;
            _audioOutput = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            _mixerProvider = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
            {
                ReadFully = true;
            };

            _memoryCache = new MemoryCache();
            _streamingManager = new StreamingManager();
            _compressionEngine = new CompressionEngine();

            _logger.LogInformation($"Sound Bank created with ID: {BankId}");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Sound Bank'ı başlatır ve yapılandırır;
        /// </summary>
        public async Task<OperationResult> InitializeAsync(SoundBankConfiguration configuration = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("Sound Bank is already initialized");
                    return OperationResult.Failure("Sound Bank is already initialized");
                }

                _logger.LogInformation("Initializing Sound Bank...");
                Status = SoundBankStatus.Loading;

                Configuration = configuration ?? GetDefaultConfiguration();

                // Bank dizinini oluştur;
                await CreateBankDirectoryAsync(cancellationToken);

                // Audio output'u başlat;
                _audioOutput.Init(_mixerProvider);
                _audioOutput.Play();

                // Kanalları oluştur;
                InitializeChannels();

                // Filtreleri oluştur;
                InitializeFilters();

                // Mixer'ları oluştur;
                InitializeMixers();

                _isInitialized = true;
                Status = SoundBankStatus.Ready;

                _logger.LogInformation("Sound Bank initialized successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Sound Bank");
                Status = SoundBankStatus.Error;
                return OperationResult.Failure($"Initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sound Bank'ı yükler (ses dosyalarını belleğe alır)
        /// </summary>
        public async Task<OperationResult> LoadBankAsync(string bankName = null, LoadOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (Status == SoundBankStatus.Loading)
                {
                    _logger.LogWarning("Sound Bank is already loading");
                    return OperationResult.Failure("Sound Bank is already loading");
                }

                _logger.LogInformation($"Loading Sound Bank: {bankName ?? "Default"}");
                Status = SoundBankStatus.Loading;

                var loadOptions = options ?? LoadOptions.Default;
                BankName = bankName ?? BankName;

                // Manifest dosyasını yükle;
                var manifest = await LoadManifestAsync(bankName, cancellationToken);
                if (manifest == null)
                {
                    Status = SoundBankStatus.Error;
                    return OperationResult.Failure("Failed to load bank manifest");
                }

                // Kategorileri yükle;
                await LoadCategoriesAsync(manifest, cancellationToken);

                // Ses varlıklarını yükle;
                await LoadSoundAssetsAsync(manifest, loadOptions, cancellationToken);

                // Sound pool'ları oluştur;
                await CreateSoundPoolsAsync(manifest, cancellationToken);

                // Playlist'leri yükle;
                await LoadPlaylistsAsync(manifest, cancellationToken);

                Status = SoundBankStatus.Ready;

                _logger.LogInformation($"Sound Bank loaded successfully: {TotalSounds} sounds, {_categories.Count} categories");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Sound Bank");
                Status = SoundBankStatus.Error;
                return OperationResult.Failure($"Load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sound Bank'ı boşaltır;
        /// </summary>
        public async Task<OperationResult> UnloadBankAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                _logger.LogInformation("Unloading Sound Bank...");
                Status = SoundBankStatus.Unloading;

                // Tüm sesleri durdur;
                StopAllSounds();

                // Tüm ses varlıklarını boşalt;
                await UnloadAllAssetsAsync(cancellationToken);

                // Sound pool'ları temizle;
                _soundPools.Clear();

                // Playlist'leri temizle;
                _playlists.Clear();

                // Streaming sesleri temizle;
                await ClearStreamingSoundsAsync(cancellationToken);

                // Cache'i temizle;
                _memoryCache.Clear();

                Status = SoundBankStatus.Unloaded;

                _logger.LogInformation("Sound Bank unloaded successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unload Sound Bank");
                return OperationResult.Failure($"Unload failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ses dosyasını yükler;
        /// </summary>
        public async Task<SoundLoadResult> LoadSoundAsync(string soundId, string filePath, LoadSoundOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return SoundLoadResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(soundId))
                {
                    return SoundLoadResult.Failure("Sound ID cannot be null or empty");
                }

                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return SoundLoadResult.Failure("File path cannot be null or empty");
                }

                if (!File.Exists(filePath))
                {
                    return SoundLoadResult.Failure($"Sound file not found: {filePath}");
                }

                _logger.LogDebug($"Loading sound: {soundId} from {filePath}");

                lock (_lockObject)
                {
                    if (_soundAssets.ContainsKey(soundId))
                    {
                        return SoundLoadResult.Failure($"Sound already exists: {soundId}");
                    }
                }

                var loadOptions = options ?? LoadSoundOptions.Default;
                var fileInfo = new FileInfo(filePath);

                // Dosya boyutu kontrolü;
                if (fileInfo.Length > Configuration.MaxSoundSize)
                {
                    return SoundLoadResult.Failure($"Sound file too large: {fileInfo.Length} bytes (max: {Configuration.MaxSoundSize} bytes)");
                }

                // Dosya formatını kontrol et;
                var format = GetAudioFormat(filePath);
                if (format == AudioFormat.Unknown)
                {
                    return SoundLoadResult.Failure($"Unsupported audio format: {filePath}");
                }

                SoundAsset soundAsset = null;

                // Yükleme stratejisine göre yükle;
                switch (loadOptions.LoadStrategy)
                {
                    case LoadStrategy.Memory:
                        soundAsset = await LoadToMemoryAsync(soundId, filePath, loadOptions, cancellationToken);
                        break;

                    case LoadStrategy.Streaming:
                        soundAsset = await LoadForStreamingAsync(soundId, filePath, loadOptions, cancellationToken);
                        break;

                    case LoadStrategy.OnDemand:
                        soundAsset = await LoadOnDemandAsync(soundId, filePath, loadOptions, cancellationToken);
                        break;

                    default:
                        return SoundLoadResult.Failure($"Unsupported load strategy: {loadOptions.LoadStrategy}");
                }

                if (soundAsset == null)
                {
                    return SoundLoadResult.Failure("Failed to load sound asset");
                }

                // Metadata ekle;
                soundAsset.Metadata = new Dictionary<string, object>
                {
                    ["FilePath"] = filePath,
                    ["FileSize"] = fileInfo.Length,
                    ["LastModified"] = fileInfo.LastWriteTimeUtc,
                    ["Format"] = format,
                    ["LoadTime"] = DateTime.UtcNow;
                };

                // Kategoriye ekle;
                if (!string.IsNullOrEmpty(loadOptions.Category))
                {
                    AddToCategory(soundAsset, loadOptions.Category);
                }

                lock (_lockObject)
                {
                    _soundAssets[soundId] = soundAsset;
                }

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.SoundLoaded,
                    SoundId = soundId,
                    SoundName = soundAsset.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Sound loaded successfully: {soundId}");

                return SoundLoadResult.Success(soundAsset);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load sound: {soundId}");
                return SoundLoadResult.Failure($"Load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ses dosyasını boşaltır;
        /// </summary>
        public async Task<OperationResult> UnloadSoundAsync(string soundId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(soundId))
                {
                    return OperationResult.Failure("Sound ID cannot be null or empty");
                }

                SoundAsset soundAsset;

                lock (_lockObject)
                {
                    if (!_soundAssets.TryGetValue(soundId, out soundAsset))
                    {
                        return OperationResult.Failure($"Sound not found: {soundId}");
                    }
                }

                // Eğer çalınıyorsa durdur;
                StopSound(soundId);

                // Bellekten boşalt;
                await soundAsset.UnloadAsync(cancellationToken);

                lock (_lockObject)
                {
                    _soundAssets.Remove(soundId);
                }

                // Kategoriden çıkar;
                if (!string.IsNullOrEmpty(soundAsset.Category))
                {
                    RemoveFromCategory(soundAsset, soundAsset.Category);
                }

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.SoundUnloaded,
                    SoundId = soundId,
                    SoundName = soundAsset.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Sound unloaded: {soundId}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to unload sound: {soundId}");
                return OperationResult.Failure($"Unload failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ses çalar;
        /// </summary>
        public async Task<PlaySoundResult> PlaySoundAsync(string soundId, PlayOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return PlaySoundResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(soundId))
                {
                    return PlaySoundResult.Failure("Sound ID cannot be null or empty");
                }

                SoundAsset soundAsset;

                lock (_lockObject)
                {
                    if (!_soundAssets.TryGetValue(soundId, out soundAsset))
                    {
                        return PlaySoundResult.Failure($"Sound not found: {soundId}");
                    }
                }

                // Eğer yüklenmemişse yükle;
                if (!soundAsset.IsLoaded)
                {
                    var loadResult = await soundAsset.LoadAsync(cancellationToken);
                    if (!loadResult.Success)
                    {
                        return PlaySoundResult.Failure($"Failed to load sound: {loadResult.ErrorMessage}");
                    }
                }

                // Boş kanal bul;
                var channel = GetAvailableChannel();
                if (channel == null)
                {
                    return PlaySoundResult.Failure("No available audio channels");
                }

                var playOptions = options ?? PlayOptions.Default;

                // Ses data'sını hazırla;
                var audioData = await PrepareAudioDataAsync(soundAsset, playOptions, cancellationToken);
                if (audioData == null)
                {
                    return PlaySoundResult.Failure("Failed to prepare audio data");
                }

                // Kanalı yapılandır;
                channel.Configure(soundAsset, audioData, playOptions);

                // Oynatmayı başlat;
                var playResult = await channel.PlayAsync(cancellationToken);
                if (!playResult.Success)
                {
                    return PlaySoundResult.Failure($"Failed to play sound: {playResult.ErrorMessage}");
                }

                // İstatistikleri güncelle;
                UpdateStatistics(soundAsset);

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.SoundPlayed,
                    SoundId = soundId,
                    SoundName = soundAsset.Name,
                    ChannelId = channel.ChannelId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Playing sound: {soundId} on channel {channel.ChannelId}");

                return PlaySoundResult.Success(channel.ChannelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to play sound: {soundId}");
                return PlaySoundResult.Failure($"Play failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Birden fazla ses çalar (sound pool'dan)
        /// </summary>
        public async Task<PlaySoundResult> PlayFromPoolAsync(string poolId, PlayOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return PlaySoundResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(poolId))
                {
                    return PlaySoundResult.Failure("Pool ID cannot be null or empty");
                }

                SoundPool soundPool;

                lock (_lockObject)
                {
                    if (!_soundPools.TryGetValue(poolId, out soundPool))
                    {
                        return PlaySoundResult.Failure($"Sound pool not found: {poolId}");
                    }
                }

                // Pool'dan ses seç;
                var soundId = soundPool.GetNextSound();
                if (string.IsNullOrEmpty(soundId))
                {
                    return PlaySoundResult.Failure($"No sounds available in pool: {poolId}");
                }

                // Ses çal;
                return await PlaySoundAsync(soundId, options, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to play from pool: {poolId}");
                return PlaySoundResult.Failure($"Play from pool failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ses durdurur;
        /// </summary>
        public OperationResult StopSound(string soundId, StopOptions options = null)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(soundId))
                {
                    return OperationResult.Failure("Sound ID cannot be null or empty");
                }

                var stopOptions = options ?? StopOptions.Default;
                var channelsToStop = new List<SoundChannel>();

                lock (_lockObject)
                {
                    // Tüm kanallarda bu sesi ara;
                    foreach (var channel in _channels.Values)
                    {
                        if (channel.CurrentSound?.SoundId == soundId && channel.IsPlaying)
                        {
                            channelsToStop.Add(channel);
                        }
                    }
                }

                if (channelsToStop.Count == 0)
                {
                    return OperationResult.Failure($"Sound not playing: {soundId}");
                }

                // Kanalları durdur;
                foreach (var channel in channelsToStop)
                {
                    channel.Stop(stopOptions);
                }

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.SoundStopped,
                    SoundId = soundId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Stopped sound: {soundId} on {channelsToStop.Count} channel(s)");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to stop sound: {soundId}");
                return OperationResult.Failure($"Stop failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanalı durdurur;
        /// </summary>
        public OperationResult StopChannel(int channelId, StopOptions options = null)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                SoundChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }
                }

                if (!channel.IsPlaying)
                {
                    return OperationResult.Failure($"Channel not playing: {channelId}");
                }

                var stopOptions = options ?? StopOptions.Default;
                channel.Stop(stopOptions);

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.ChannelStopped,
                    ChannelId = channelId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Stopped channel: {channelId}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to stop channel: {channelId}");
                return OperationResult.Failure($"Stop failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tüm sesleri durdurur;
        /// </summary>
        public OperationResult StopAllSounds(StopOptions options = null)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                var stopOptions = options ?? StopOptions.Default;
                var channelsStopped = 0;

                lock (_lockObject)
                {
                    foreach (var channel in _channels.Values)
                    {
                        if (channel.IsPlaying)
                        {
                            channel.Stop(stopOptions);
                            channelsStopped++;
                        }
                    }
                }

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.AllSoundsStopped,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Stopped all sounds: {channelsStopped} channel(s) stopped");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop all sounds");
                return OperationResult.Failure($"Stop all failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ses durumunu kontrol eder;
        /// </summary>
        public SoundStatus GetSoundStatus(string soundId)
        {
            try
            {
                if (!_isInitialized)
                {
                    return SoundStatus.Error;
                }

                if (string.IsNullOrWhiteSpace(soundId))
                {
                    return SoundStatus.Error;
                }

                SoundAsset soundAsset;

                lock (_lockObject)
                {
                    if (!_soundAssets.TryGetValue(soundId, out soundAsset))
                    {
                        return SoundStatus.NotFound;
                    }
                }

                // Çalınan kanalları bul;
                var playingChannels = new List<int>();

                lock (_lockObject)
                {
                    foreach (var channel in _channels.Values)
                    {
                        if (channel.CurrentSound?.SoundId == soundId && channel.IsPlaying)
                        {
                            playingChannels.Add(channel.ChannelId);
                        }
                    }
                }

                return new SoundStatus;
                {
                    SoundId = soundId,
                    IsLoaded = soundAsset.IsLoaded,
                    IsPlaying = playingChannels.Count > 0,
                    PlayingChannels = playingChannels,
                    LoadTime = soundAsset.LoadTime,
                    MemoryUsage = soundAsset.MemoryUsage;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get sound status: {soundId}");
                return SoundStatus.Error;
            }
        }

        /// <summary>
        /// Kanal durumunu kontrol eder;
        /// </summary>
        public ChannelStatus GetChannelStatus(int channelId)
        {
            try
            {
                if (!_isInitialized)
                {
                    return ChannelStatus.Error;
                }

                SoundChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return ChannelStatus.NotFound;
                    }
                }

                return new ChannelStatus;
                {
                    ChannelId = channelId,
                    IsPlaying = channel.IsPlaying,
                    IsPaused = channel.IsPaused,
                    CurrentSound = channel.CurrentSound?.SoundId,
                    Volume = channel.Volume,
                    Pan = channel.Pan,
                    PlaybackPosition = channel.PlaybackPosition,
                    PlaybackTime = channel.PlaybackTime;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get channel status: {channelId}");
                return ChannelStatus.Error;
            }
        }

        /// <summary>
        /// Ses düzeyini ayarlar;
        /// </summary>
        public OperationResult SetVolume(string soundId, float volume)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(soundId))
                {
                    return OperationResult.Failure("Sound ID cannot be null or empty");
                }

                if (volume < 0 || volume > 1)
                {
                    return OperationResult.Failure("Volume must be between 0 and 1");
                }

                // Tüm kanallarda bu sesin volume'ünü ayarla;
                var channelsUpdated = 0;

                lock (_lockObject)
                {
                    foreach (var channel in _channels.Values)
                    {
                        if (channel.CurrentSound?.SoundId == soundId)
                        {
                            channel.SetVolume(volume);
                            channelsUpdated++;
                        }
                    }
                }

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.VolumeChanged,
                    SoundId = soundId,
                    Volume = volume,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Set volume for sound {soundId}: {volume} (updated {channelsUpdated} channel(s))");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set volume for sound: {soundId}");
                return OperationResult.Failure($"Set volume failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kanal volume'ünü ayarlar;
        /// </summary>
        public OperationResult SetChannelVolume(int channelId, float volume)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                SoundChannel channel;

                lock (_lockObject)
                {
                    if (!_channels.TryGetValue(channelId, out channel))
                    {
                        return OperationResult.Failure($"Channel not found: {channelId}");
                    }
                }

                channel.SetVolume(volume);

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.ChannelVolumeChanged,
                    ChannelId = channelId,
                    Volume = volume,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Set volume for channel {channelId}: {volume}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set channel volume: {channelId}");
                return OperationResult.Failure($"Set channel volume failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Master volume'ü ayarlar;
        /// </summary>
        public OperationResult SetMasterVolume(float volume)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (volume < 0 || volume > 1)
                {
                    return OperationResult.Failure("Volume must be between 0 and 1");
                }

                _masterVolume = volume;

                // Tüm kanalların volume'ünü güncelle;
                lock (_lockObject)
                {
                    foreach (var channel in _channels.Values)
                    {
                        channel.ApplyMasterVolume(_masterVolume);
                    }
                }

                // Event tetikle;
                OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                {
                    EventType = SoundEventType.MasterVolumeChanged,
                    Volume = volume,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Set master volume: {volume}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set master volume");
                return OperationResult.Failure($"Set master volume failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ses filtresi ekler;
        /// </summary>
        public OperationResult AddFilter(SoundFilter filter)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (filter == null)
                {
                    return OperationResult.Failure("Filter cannot be null");
                }

                lock (_filters)
                {
                    if (_filters.Any(f => f.FilterId == filter.FilterId))
                    {
                        return OperationResult.Failure($"Filter already exists: {filter.FilterId}");
                    }

                    _filters.Add(filter);

                    // Tüm kanallara uygula;
                    foreach (var channel in _channels.Values)
                    {
                        channel.AddFilter(filter);
                    }
                }

                _logger.LogDebug($"Added filter: {filter.FilterId}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add filter");
                return OperationResult.Failure($"Add filter failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kategori oluşturur;
        /// </summary>
        public OperationResult CreateCategory(string categoryId, CategoryConfiguration configuration = null)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(categoryId))
                {
                    return OperationResult.Failure("Category ID cannot be null or empty");
                }

                lock (_lockObject)
                {
                    if (_categories.ContainsKey(categoryId))
                    {
                        return OperationResult.Failure($"Category already exists: {categoryId}");
                    }

                    var categoryConfig = configuration ?? new CategoryConfiguration();
                    var category = new SoundCategory(categoryId, categoryConfig);

                    _categories[categoryId] = category;
                }

                _logger.LogDebug($"Created category: {categoryId}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create category: {categoryId}");
                return OperationResult.Failure($"Create category failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sesleri kategorilere göre filtreler;
        /// </summary>
        public List<SoundAsset> GetSoundsByCategory(string categoryId)
        {
            try
            {
                if (!_isInitialized)
                {
                    return new List<SoundAsset>();
                }

                if (string.IsNullOrWhiteSpace(categoryId))
                {
                    return new List<SoundAsset>();
                }

                SoundCategory category;

                lock (_lockObject)
                {
                    if (!_categories.TryGetValue(categoryId, out category))
                    {
                        return new List<SoundAsset>();
                    }
                }

                var sounds = new List<SoundAsset>();

                foreach (var soundId in category.SoundIds)
                {
                    if (_soundAssets.TryGetValue(soundId, out var sound))
                    {
                        sounds.Add(sound);
                    }
                }

                return sounds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get sounds by category: {categoryId}");
                return new List<SoundAsset>();
            }
        }

        /// <summary>
        /// Sound pool oluşturur;
        /// </summary>
        public OperationResult CreateSoundPool(string poolId, SoundPoolConfiguration configuration)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(poolId))
                {
                    return OperationResult.Failure("Pool ID cannot be null or empty");
                }

                if (configuration == null)
                {
                    return OperationResult.Failure("Configuration cannot be null");
                }

                lock (_lockObject)
                {
                    if (_soundPools.ContainsKey(poolId))
                    {
                        return OperationResult.Failure($"Sound pool already exists: {poolId}");
                    }

                    // Seslerin var olduğunu kontrol et;
                    foreach (var soundId in configuration.SoundIds)
                    {
                        if (!_soundAssets.ContainsKey(soundId))
                        {
                            return OperationResult.Failure($"Sound not found: {soundId}");
                        }
                    }

                    var soundPool = new SoundPool(poolId, configuration);
                    _soundPools[poolId] = soundPool;
                }

                _logger.LogDebug($"Created sound pool: {poolId} with {configuration.SoundIds.Count} sounds");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create sound pool: {poolId}");
                return OperationResult.Failure($"Create sound pool failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Playlist oluşturur;
        /// </summary>
        public OperationResult CreatePlaylist(string playlistId, PlaylistConfiguration configuration)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(playlistId))
                {
                    return OperationResult.Failure("Playlist ID cannot be null or empty");
                }

                if (configuration == null)
                {
                    return OperationResult.Failure("Configuration cannot be null");
                }

                lock (_lockObject)
                {
                    if (_playlists.ContainsKey(playlistId))
                    {
                        return OperationResult.Failure($"Playlist already exists: {playlistId}");
                    }

                    // Seslerin var olduğunu kontrol et;
                    foreach (var soundId in configuration.SoundIds)
                    {
                        if (!_soundAssets.ContainsKey(soundId))
                        {
                            return OperationResult.Failure($"Sound not found: {soundId}");
                        }
                    }

                    var playlist = new Playlist(playlistId, configuration);
                    _playlists[playlistId] = playlist;
                }

                _logger.LogDebug($"Created playlist: {playlistId} with {configuration.SoundIds.Count} sounds");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create playlist: {playlistId}");
                return OperationResult.Failure($"Create playlist failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Playlist çalar;
        /// </summary>
        public async Task<PlaylistResult> PlayPlaylistAsync(string playlistId, PlaylistPlayOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return PlaylistResult.Failure("Sound Bank is not initialized");
                }

                if (string.IsNullOrWhiteSpace(playlistId))
                {
                    return PlaylistResult.Failure("Playlist ID cannot be null or empty");
                }

                Playlist playlist;

                lock (_lockObject)
                {
                    if (!_playlists.TryGetValue(playlistId, out playlist))
                    {
                        return PlaylistResult.Failure($"Playlist not found: {playlistId}");
                    }
                }

                var playOptions = options ?? new PlaylistPlayOptions();
                playlist.StartPlayback(playOptions);

                // Playlist'i çalmaya başla;
                var result = await playlist.PlayNextAsync(async (soundId, opts) =>
                {
                    return await PlaySoundAsync(soundId, opts, cancellationToken);
                }, cancellationToken);

                return new PlaylistResult;
                {
                    Success = result.Success,
                    PlaylistId = playlistId,
                    CurrentIndex = playlist.CurrentIndex,
                    IsPlaying = playlist.IsPlaying,
                    IsLooping = playlist.IsLooping,
                    ErrorMessage = result.ErrorMessage;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to play playlist: {playlistId}");
                return PlaylistResult.Failure($"Play playlist failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sound Bank metriklerini alır;
        /// </summary>
        public SoundBankMetrics GetMetrics()
        {
            return new SoundBankMetrics;
            {
                BankId = BankId,
                BankName = BankName,
                Status = Status,
                TotalSounds = TotalSounds,
                LoadedSounds = LoadedSounds,
                ActiveChannels = ActiveChannels,
                UsedMemory = UsedMemory,
                TotalBytesStreamed = TotalBytesStreamed,
                TotalSoundsPlayed = TotalSoundsPlayed,
                CategoriesCount = _categories.Count,
                PoolsCount = _soundPools.Count,
                PlaylistsCount = _playlists.Count,
                FiltersCount = _filters.Count,
                MasterVolume = _masterVolume,
                IsMuted = _isMuted,
                Uptime = DateTime.UtcNow - _startTime,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Sound Bank'ı kaydeder;
        /// </summary>
        public async Task<OperationResult> SaveBankAsync(string bankName = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return OperationResult.Failure("Sound Bank is not initialized");
                }

                _logger.LogInformation("Saving Sound Bank...");

                var saveBankName = bankName ?? BankName;
                var bankDirectory = Path.Combine(_bankPath, saveBankName);

                if (!Directory.Exists(bankDirectory))
                {
                    Directory.CreateDirectory(bankDirectory);
                }

                // Manifest oluştur;
                var manifest = CreateBankManifest();

                // Manifest'i kaydet;
                var manifestPath = Path.Combine(bankDirectory, "manifest.json");
                var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                });

                await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);

                // Ses dosyalarını kaydet;
                await SaveSoundAssetsAsync(bankDirectory, cancellationToken);

                // Kategorileri kaydet;
                await SaveCategoriesAsync(bankDirectory, cancellationToken);

                // Sound pool'ları kaydet;
                await SaveSoundPoolsAsync(bankDirectory, cancellationToken);

                // Playlist'leri kaydet;
                await SavePlaylistsAsync(bankDirectory, cancellationToken);

                _logger.LogInformation($"Sound Bank saved: {saveBankName}");

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save Sound Bank");
                return OperationResult.Failure($"Save failed: {ex.Message}");
            }
        }
        #endregion;

        #region Private Methods;
        private SoundBankConfiguration GetDefaultConfiguration()
        {
            return new SoundBankConfiguration;
            {
                MaxSoundSize = 100 * 1024 * 1024, // 100MB;
                MaxMemoryUsage = 500 * 1024 * 1024, // 500MB;
                DefaultLoadStrategy = LoadStrategy.Memory,
                EnableStreaming = true,
                StreamingBufferSize = 8192,
                EnableCompression = true,
                CompressionLevel = CompressionLevel.Normal,
                MaxChannels = 32,
                DefaultVolume = 0.8f,
                Enable3DSound = false,
                EnableEffects = true,
                CacheEnabled = true,
                CacheSize = 100 * 1024 * 1024, // 100MB;
                PreloadSounds = false,
                LogLevel = LogLevel.Info;
            };
        }

        private async Task CreateBankDirectoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(_bankPath))
                {
                    Directory.CreateDirectory(_bankPath);
                    _logger.LogDebug($"Created bank directory: {_bankPath}");
                }

                // Alt dizinleri oluştur;
                var subDirectories = new[]
                {
                    "Sounds",
                    "Categories",
                    "Pools",
                    "Playlists",
                    "Cache",
                    "Streaming",
                    "Backup"
                };

                foreach (var subDir in subDirectories)
                {
                    var dirPath = Path.Combine(_bankPath, subDir);
                    if (!Directory.Exists(dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create bank directory");
                throw;
            }
        }

        private void InitializeChannels()
        {
            lock (_lockObject)
            {
                _channels.Clear();

                for (int i = 0; i < Configuration.MaxChannels; i++)
                {
                    var channelId = _nextChannelId++;
                    var channel = new SoundChannel(channelId, _mixerProvider);

                    channel.OnChannelEvent += (sender, args) =>
                    {
                        // Channel event'lerini bank event'ine dönüştür;
                        OnSoundEvent?.Invoke(this, new SoundBankEventArgs;
                        {
                            EventType = MapChannelEvent(args.EventType),
                            ChannelId = channelId,
                            SoundId = args.SoundId,
                            Timestamp = args.Timestamp;
                        });
                    };

                    _channels[channelId] = channel;
                }
            }

            _logger.LogDebug($"Initialized {Configuration.MaxChannels} audio channels");
        }

        private SoundEventType MapChannelEvent(ChannelEventType channelEvent)
        {
            return channelEvent switch;
            {
                ChannelEventType.Started => SoundEventType.ChannelStarted,
                ChannelEventType.Stopped => SoundEventType.ChannelStopped,
                ChannelEventType.Paused => SoundEventType.ChannelPaused,
                ChannelEventType.Resumed => SoundEventType.ChannelResumed,
                ChannelEventType.Completed => SoundEventType.SoundCompleted,
                ChannelEventType.Looped => SoundEventType.SoundLooped,
                _ => SoundEventType.Unknown;
            };
        }

        private void InitializeFilters()
        {
            // Varsayılan filtreleri ekle;
            var filters = new[]
            {
                new SoundFilter("lowpass", FilterType.LowPass, 1000f),
                new SoundFilter("highpass", FilterType.HighPass, 500f),
                new SoundFilter("reverb", FilterType.Reverb, 0.5f),
                new SoundFilter("echo", FilterType.Echo, 0.3f)
            };

            foreach (var filter in filters)
            {
                _filters.Add(filter);
            }

            _logger.LogDebug($"Initialized {_filters.Count} audio filters");
        }

        private void InitializeMixers()
        {
            // Varsayılan mixer'ları oluştur;
            var mixers = new[]
            {
                new AudioMixer("master", "Master Mixer", 1.0f),
                new AudioMixer("sfx", "Sound Effects", 1.0f),
                new AudioMixer("music", "Music", 0.8f),
                new AudioMixer("voice", "Voice", 1.0f),
                new AudioMixer("ambient", "Ambient", 0.6f)
            };

            foreach (var mixer in mixers)
            {
                _mixers[mixer.MixerId] = mixer;
            }

            _logger.LogDebug($"Initialized {_mixers.Count} audio mixers");
        }

        private async Task<BankManifest> LoadManifestAsync(string bankName, CancellationToken cancellationToken)
        {
            try
            {
                var manifestPath = Path.Combine(_bankPath, bankName ?? BankName, "manifest.json");

                if (!File.Exists(manifestPath))
                {
                    _logger.LogWarning($"Manifest not found: {manifestPath}");
                    return CreateDefaultManifest();
                }

                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<BankManifest>(json);

                if (manifest == null)
                {
                    _logger.LogWarning($"Failed to parse manifest: {manifestPath}");
                    return CreateDefaultManifest();
                }

                _logger.LogDebug($"Loaded manifest: {manifest.BankName} (v{manifest.Version})");

                return manifest;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load manifest");
                return CreateDefaultManifest();
            }
        }

        private BankManifest CreateDefaultManifest()
        {
            return new BankManifest;
            {
                BankId = BankId,
                BankName = BankName,
                Version = "1.0.0",
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Sounds = new List<SoundInfo>(),
                Categories = new List<CategoryInfo>(),
                Pools = new List<PoolInfo>(),
                Playlists = new List<PlaylistInfo>(),
                Metadata = new Dictionary<string, object>()
            };
        }

        private async Task LoadCategoriesAsync(BankManifest manifest, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var categoryInfo in manifest.Categories)
                {
                    var category = new SoundCategory(categoryInfo.CategoryId, new CategoryConfiguration;
                    {
                        MaxSounds = categoryInfo.MaxSounds,
                        Volume = categoryInfo.Volume,
                        IsMuted = categoryInfo.IsMuted;
                    });

                    _categories[categoryInfo.CategoryId] = category;

                    // Progress event'i tetikle;
                    OnLoadProgress?.Invoke(this, new BankLoadProgressEventArgs;
                    {
                        Current = manifest.Categories.IndexOf(categoryInfo) + 1,
                        Total = manifest.Categories.Count,
                        ItemType = "Category",
                        ItemName = categoryInfo.CategoryId,
                        Timestamp = DateTime.UtcNow;
                    });

                    await Task.Delay(10, cancellationToken); // Yield;
                }

                _logger.LogDebug($"Loaded {manifest.Categories.Count} categories");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load categories");
                throw;
            }
        }

        private async Task LoadSoundAssetsAsync(BankManifest manifest, LoadOptions options, CancellationToken cancellationToken)
        {
            try
            {
                var totalSounds = manifest.Sounds.Count;
                var loadedCount = 0;

                foreach (var soundInfo in manifest.Sounds)
                {
                    try
                    {
                        var soundPath = Path.Combine(_bankPath, BankName, "Sounds", soundInfo.FileName);

                        if (!File.Exists(soundPath))
                        {
                            _logger.LogWarning($"Sound file not found: {soundPath}");
                            continue;
                        }

                        var loadOptions = new LoadSoundOptions;
                        {
                            LoadStrategy = options.PreloadSounds ? LoadStrategy.Memory : LoadStrategy.OnDemand,
                            Category = soundInfo.Category,
                            Volume = soundInfo.Volume,
                            Priority = soundInfo.Priority;
                        };

                        var result = await LoadSoundAsync(soundInfo.SoundId, soundPath, loadOptions, cancellationToken);

                        if (result.Success)
                        {
                            loadedCount++;
                        }

                        // Progress event'i tetikle;
                        OnLoadProgress?.Invoke(this, new BankLoadProgressEventArgs;
                        {
                            Current = loadedCount,
                            Total = totalSounds,
                            ItemType = "Sound",
                            ItemName = soundInfo.SoundId,
                            Success = result.Success,
                            Timestamp = DateTime.UtcNow;
                        });

                        await Task.Delay(10, cancellationToken); // Yield;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to load sound: {soundInfo.SoundId}");
                    }
                }

                _logger.LogDebug($"Loaded {loadedCount} of {totalSounds} sounds");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load sound assets");
                throw;
            }
        }

        private async Task CreateSoundPoolsAsync(BankManifest manifest, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var poolInfo in manifest.Pools)
                {
                    var config = new SoundPoolConfiguration;
                    {
                        SoundIds = poolInfo.SoundIds,
                        SelectionMode = poolInfo.SelectionMode,
                        Volume = poolInfo.Volume,
                        MaxConcurrent = poolInfo.MaxConcurrent;
                    };

                    var result = CreateSoundPool(poolInfo.PoolId, config);

                    if (result.Success)
                    {
                        _logger.LogDebug($"Created sound pool: {poolInfo.PoolId}");
                    }

                    await Task.Delay(10, cancellationToken); // Yield;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create sound pools");
                throw;
            }
        }

        private async Task LoadPlaylistsAsync(BankManifest manifest, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var playlistInfo in manifest.Playlists)
                {
                    var config = new PlaylistConfiguration;
                    {
                        SoundIds = playlistInfo.SoundIds,
                        PlayOrder = playlistInfo.PlayOrder,
                        Loop = playlistInfo.Loop,
                        Crossfade = playlistInfo.Crossfade,
                        CrossfadeDuration = playlistInfo.CrossfadeDuration;
                    };

                    var result = CreatePlaylist(playlistInfo.PlaylistId, config);

                    if (result.Success)
                    {
                        _logger.LogDebug($"Created playlist: {playlistInfo.PlaylistId}");
                    }

                    await Task.Delay(10, cancellationToken); // Yield;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load playlists");
                throw;
            }
        }

        private async Task UnloadAllAssetsAsync(CancellationToken cancellationToken)
        {
            var soundIds = _soundAssets.Keys.ToList();

            foreach (var soundId in soundIds)
            {
                try
                {
                    await UnloadSoundAsync(soundId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to unload sound: {soundId}");
                }

                await Task.Delay(10, cancellationToken); // Yield;
            }
        }

        private async Task ClearStreamingSoundsAsync(CancellationToken cancellationToken)
        {
            foreach (var streamedSound in _streamingSounds.Values)
            {
                try
                {
                    await streamedSound.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispose streamed sound");
                }
            }

            _streamingSounds.Clear();
        }

        private AudioFormat GetAudioFormat(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch;
            {
                ".wav" => AudioFormat.WAV,
                ".mp3" => AudioFormat.MP3,
                ".ogg" => AudioFormat.OGG,
                ".flac" => AudioFormat.FLAC,
                ".aiff" => AudioFormat.AIFF,
                ".aif" => AudioFormat.AIFF,
                ".m4a" => AudioFormat.M4A,
                ".wma" => AudioFormat.WMA,
                ".aac" => AudioFormat.AAC,
                _ => AudioFormat.Unknown;
            };
        }

        private async Task<SoundAsset> LoadToMemoryAsync(string soundId, string filePath, LoadSoundOptions options, CancellationToken cancellationToken)
        {
            try
            {
                var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);

                // Şifrelenmişse çöz;
                if (options.IsEncrypted && _cryptoEngine != null)
                {
                    fileBytes = await _cryptoEngine.DecryptAsync(fileBytes, options.EncryptionKey, cancellationToken);
                }

                // Sıkıştırılmışsa aç;
                if (options.IsCompressed)
                {
                    fileBytes = await _compressionEngine.DecompressAsync(fileBytes, cancellationToken);
                }

                // Audio format'ını parse et;
                using var memoryStream = new MemoryStream(fileBytes);
                var waveStream = CreateWaveStream(memoryStream, GetAudioFormat(filePath));

                if (waveStream == null)
                {
                    throw new InvalidOperationException($"Failed to create wave stream for: {filePath}");
                }

                // Wave data'sını oku;
                var waveData = new byte[waveStream.Length];
                await waveStream.ReadAsync(waveData, 0, waveData.Length, cancellationToken);

                var soundAsset = new SoundAsset(soundId, options.Name ?? soundId)
                {
                    AudioData = waveData,
                    AudioFormat = GetAudioFormat(filePath),
                    Duration = waveStream.TotalTime,
                    SampleRate = waveStream.WaveFormat.SampleRate,
                    Channels = waveStream.WaveFormat.Channels,
                    BitsPerSample = waveStream.WaveFormat.BitsPerSample,
                    Category = options.Category,
                    DefaultVolume = options.Volume,
                    Priority = options.Priority,
                    LoadStrategy = LoadStrategy.Memory,
                    IsLoaded = true,
                    LoadTime = DateTime.UtcNow,
                    MemoryUsage = waveData.Length;
                };

                waveStream.Dispose();

                return soundAsset;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load sound to memory: {soundId}");
                return null;
            }
        }

        private async Task<SoundAsset> LoadForStreamingAsync(string soundId, string filePath, LoadSoundOptions options, CancellationToken cancellationToken)
        {
            try
            {
                var streamedSound = new StreamedSound(soundId, filePath, Configuration.StreamingBufferSize);
                await streamedSound.InitializeAsync(cancellationToken);

                _streamingSounds[soundId] = streamedSound;

                var soundAsset = new SoundAsset(soundId, options.Name ?? soundId)
                {
                    StreamedSound = streamedSound,
                    AudioFormat = GetAudioFormat(filePath),
                    Duration = streamedSound.Duration,
                    SampleRate = streamedSound.SampleRate,
                    Channels = streamedSound.Channels,
                    BitsPerSample = streamedSound.BitsPerSample,
                    Category = options.Category,
                    DefaultVolume = options.Volume,
                    Priority = options.Priority,
                    LoadStrategy = LoadStrategy.Streaming,
                    IsLoaded = true,
                    LoadTime = DateTime.UtcNow,
                    MemoryUsage = streamedSound.BufferSize;
                };

                return soundAsset;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load sound for streaming: {soundId}");
                return null;
            }
        }

        private async Task<SoundAsset> LoadOnDemandAsync(string soundId, string filePath, LoadSoundOptions options, CancellationToken cancellationToken)
        {
            var soundAsset = new SoundAsset(soundId, options.Name ?? soundId)
            {
                FilePath = filePath,
                AudioFormat = GetAudioFormat(filePath),
                Category = options.Category,
                DefaultVolume = options.Volume,
                Priority = options.Priority,
                LoadStrategy = LoadStrategy.OnDemand,
                IsLoaded = false,
                LoadTime = DateTime.MinValue,
                MemoryUsage = 0;
            };

            // Metadata için temel bilgileri al;
            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var waveStream = CreateWaveStream(fileStream, GetAudioFormat(filePath));

                if (waveStream != null)
                {
                    soundAsset.Duration = waveStream.TotalTime;
                    soundAsset.SampleRate = waveStream.WaveFormat.SampleRate;
                    soundAsset.Channels = waveStream.WaveFormat.Channels;
                    soundAsset.BitsPerSample = waveStream.WaveFormat.BitsPerSample;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to get audio info for: {soundId}");
            }

            return await Task.FromResult(soundAsset);
        }

        private WaveStream CreateWaveStream(Stream stream, AudioFormat format)
        {
            return format switch;
            {
                AudioFormat.WAV => new WaveFileReader(stream),
                AudioFormat.MP3 => new Mp3FileReader(stream),
                AudioFormat.OGG => new VorbisWaveReader(stream),
                AudioFormat.FLAC => new NAudio.Flac.FlacReader(stream),
                _ => throw new NotSupportedException($"Format not supported: {format}")
            };
        }

        private void AddToCategory(SoundAsset soundAsset, string categoryId)
        {
            lock (_lockObject)
            {
                if (!_categories.TryGetValue(categoryId, out var category))
                {
                    // Kategori yoksa oluştur;
                    category = new SoundCategory(categoryId, new CategoryConfiguration());
                    _categories[categoryId] = category;
                }

                category.AddSound(soundAsset.SoundId);
                soundAsset.Category = categoryId;
            }
        }

        private void RemoveFromCategory(SoundAsset soundAsset, string categoryId)
        {
            lock (_lockObject)
            {
                if (_categories.TryGetValue(categoryId, out var category))
                {
                    category.RemoveSound(soundAsset.SoundId);
                    soundAsset.Category = null;
                }
            }
        }

        private SoundChannel GetAvailableChannel()
        {
            lock (_lockObject)
            {
                // Boş kanal ara;
                foreach (var channel in _channels.Values)
                {
                    if (!channel.IsPlaying && !channel.IsReserved)
                    {
                        return channel;
                    }
                }

                // Düşük öncelikli kanal ara;
                var lowestPriorityChannel = _channels.Values;
                    .Where(c => c.IsPlaying)
                    .OrderBy(c => c.Priority)
                    .FirstOrDefault();

                if (lowestPriorityChannel != null)
                {
                    lowestPriorityChannel.Stop(new StopOptions { FadeOut = true });
                    return lowestPriorityChannel;
                }

                return null;
            }
        }

        private async Task<AudioData> PrepareAudioDataAsync(SoundAsset soundAsset, PlayOptions options, CancellationToken cancellationToken)
        {
            try
            {
                AudioData audioData = null;

                switch (soundAsset.LoadStrategy)
                {
                    case LoadStrategy.Memory:
                        audioData = new AudioData;
                        {
                            Data = soundAsset.AudioData,
                            Format = soundAsset.AudioFormat,
                            SampleRate = soundAsset.SampleRate,
                            Channels = soundAsset.Channels,
                            BitsPerSample = soundAsset.BitsPerSample;
                        };
                        break;

                    case LoadStrategy.Streaming:
                        if (soundAsset.StreamedSound != null)
                        {
                            audioData = new AudioData;
                            {
                                Stream = soundAsset.StreamedSound.GetStream(),
                                Format = soundAsset.AudioFormat,
                                SampleRate = soundAsset.SampleRate,
                                Channels = soundAsset.Channels,
                                BitsPerSample = soundAsset.BitsPerSample,
                                IsStreaming = true;
                            };
                        }
                        break;

                    case LoadStrategy.OnDemand:
                        // On-demand loading;
                        var result = await soundAsset.LoadAsync(cancellationToken);
                        if (result.Success && soundAsset.AudioData != null)
                        {
                            audioData = new AudioData;
                            {
                                Data = soundAsset.AudioData,
                                Format = soundAsset.AudioFormat,
                                SampleRate = soundAsset.SampleRate,
                                Channels = soundAsset.Channels,
                                BitsPerSample = soundAsset.BitsPerSample;
                            };
                        }
                        break;
                }

                return audioData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prepare audio data");
                return null;
            }
        }

        private void UpdateStatistics(SoundAsset soundAsset)
        {
            TotalSoundsPlayed++;

            if (soundAsset.LoadStrategy == LoadStrategy.Streaming)
            {
                TotalBytesStreamed += soundAsset.StreamedSound?.BytesStreamed ?? 0;
            }
        }

        private float CalculateUsedMemory()
        {
            float total = 0;

            lock (_lockObject)
            {
                foreach (var sound in _soundAssets.Values)
                {
                    total += sound.MemoryUsage;
                }
            }

            return total;
        }

        private BankManifest CreateBankManifest()
        {
            var manifest = new BankManifest;
            {
                BankId = BankId,
                BankName = BankName,
                Version = "1.0.0",
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Sounds = new List<SoundInfo>(),
                Categories = new List<CategoryInfo>(),
                Pools = new List<PoolInfo>(),
                Playlists = new List<PlaylistInfo>(),
                Metadata = new Dictionary<string, object>
                {
                    ["TotalSounds"] = TotalSounds,
                    ["LoadedSounds"] = LoadedSounds,
                    ["MemoryUsage"] = UsedMemory;
                }
            };

            // Ses bilgilerini ekle;
            foreach (var sound in _soundAssets.Values)
            {
                manifest.Sounds.Add(new SoundInfo;
                {
                    SoundId = sound.SoundId,
                    Name = sound.Name,
                    FileName = Path.GetFileName(sound.FilePath),
                    Category = sound.Category,
                    Volume = sound.DefaultVolume,
                    Priority = sound.Priority,
                    LoadStrategy = sound.LoadStrategy;
                });
            }

            // Kategori bilgilerini ekle;
            foreach (var category in _categories.Values)
            {
                manifest.Categories.Add(new CategoryInfo;
                {
                    CategoryId = category.CategoryId,
                    SoundIds = category.SoundIds.ToList(),
                    Volume = category.Volume,
                    IsMuted = category.IsMuted;
                });
            }

            // Pool bilgilerini ekle;
            foreach (var pool in _soundPools.Values)
            {
                manifest.Pools.Add(new PoolInfo;
                {
                    PoolId = pool.PoolId,
                    SoundIds = pool.SoundIds.ToList(),
                    SelectionMode = pool.SelectionMode,
                    Volume = pool.Volume,
                    MaxConcurrent = pool.MaxConcurrent;
                });
            }

            // Playlist bilgilerini ekle;
            foreach (var playlist in _playlists.Values)
            {
                manifest.Playlists.Add(new PlaylistInfo;
                {
                    PlaylistId = playlist.PlaylistId,
                    SoundIds = playlist.SoundIds.ToList(),
                    PlayOrder = playlist.PlayOrder,
                    Loop = playlist.Loop,
                    Crossfade = playlist.Crossfade,
                    CrossfadeDuration = playlist.CrossfadeDuration;
                });
            }

            return manifest;
        }

        private async Task SaveSoundAssetsAsync(string bankDirectory, CancellationToken cancellationToken)
        {
            var soundsDir = Path.Combine(bankDirectory, "Sounds");

            if (!Directory.Exists(soundsDir))
            {
                Directory.CreateDirectory(soundsDir);
            }

            foreach (var sound in _soundAssets.Values)
            {
                if (!string.IsNullOrEmpty(sound.FilePath))
                {
                    try
                    {
                        var destPath = Path.Combine(soundsDir, Path.GetFileName(sound.FilePath));

                        if (File.Exists(sound.FilePath) && !File.Exists(destPath))
                        {
                            File.Copy(sound.FilePath, destPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to save sound file: {sound.SoundId}");
                    }
                }

                await Task.Delay(10, cancellationToken); // Yield;
            }
        }

        private async Task SaveCategoriesAsync(string bankDirectory, CancellationToken cancellationToken)
        {
            var categoriesDir = Path.Combine(bankDirectory, "Categories");

            if (!Directory.Exists(categoriesDir))
            {
                Directory.CreateDirectory(categoriesDir);
            }

            foreach (var category in _categories.Values)
            {
                try
                {
                    var categoryPath = Path.Combine(categoriesDir, $"{category.CategoryId}.json");
                    var categoryJson = JsonSerializer.Serialize(category, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    });

                    await File.WriteAllTextAsync(categoryPath, categoryJson, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to save category: {category.CategoryId}");
                }

                await Task.Delay(10, cancellationToken); // Yield;
            }
        }

        private async Task SaveSoundPoolsAsync(string bankDirectory, CancellationToken cancellationToken)
        {
            var poolsDir = Path.Combine(bankDirectory, "Pools");

            if (!Directory.Exists(poolsDir))
            {
                Directory.CreateDirectory(poolsDir);
            }

            foreach (var pool in _soundPools.Values)
            {
                try
                {
                    var poolPath = Path.Combine(poolsDir, $"{pool.PoolId}.json");
                    var poolJson = JsonSerializer.Serialize(pool, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    });

                    await File.WriteAllTextAsync(poolPath, poolJson, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to save pool: {pool.PoolId}");
                }

                await Task.Delay(10, cancellationToken); // Yield;
            }
        }

        private async Task SavePlaylistsAsync(string bankDirectory, CancellationToken cancellationToken)
        {
            var playlistsDir = Path.Combine(bankDirectory, "Playlists");

            if (!Directory.Exists(playlistsDir))
            {
                Directory.CreateDirectory(playlistsDir);
            }

            foreach (var playlist in _playlists.Values)
            {
                try
                {
                    var playlistPath = Path.Combine(playlistsDir, $"{playlist.PlaylistId}.json");
                    var playlistJson = JsonSerializer.Serialize(playlist, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    });

                    await File.WriteAllTextAsync(playlistPath, playlistJson, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to save playlist: {playlist.PlaylistId}");
                }

                await Task.Delay(10, cancellationToken); // Yield;
            }
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
                    // Tüm sesleri durdur;
                    StopAllSounds();

                    // Audio output'u kapat;
                    _audioOutput?.Stop();
                    _audioOutput?.Dispose();

                    // Tüm kanalları temizle;
                    foreach (var channel in _channels.Values)
                    {
                        channel.Dispose();
                    }
                    _channels.Clear();

                    // Tüm stream'leri temizle;
                    foreach (var stream in _streamingSounds.Values)
                    {
                        stream.DisposeAsync().GetAwaiter().GetResult();
                    }
                    _streamingSounds.Clear();

                    // Belleği temizle;
                    _memoryCache?.Clear();
                    _memoryCache = null;

                    // Sound Bank'ı boşalt;
                    UnloadBankAsync().GetAwaiter().GetResult();

                    Status = SoundBankStatus.Disposed;

                    _logger.LogInformation("Sound Bank disposed");
                }

                _isDisposed = true;
            }
        }

        ~SoundBank()
        {
            Dispose(false);
        }
        #endregion;

        #region Supporting Classes and Enums;
        public enum SoundBankStatus;
        {
            Unloaded,
            Loading,
            Ready,
            Unloading,
            Error,
            Disposed;
        }

        public enum LoadStrategy;
        {
            Memory,
            Streaming,
            OnDemand;
        }

        public enum AudioFormat;
        {
            Unknown,
            WAV,
            MP3,
            OGG,
            FLAC,
            AIFF,
            M4A,
            WMA,
            AAC;
        }

        public enum CompressionLevel;
        {
            None,
            Fast,
            Normal,
            Maximum;
        }

        public enum FilterType;
        {
            LowPass,
            HighPass,
            BandPass,
            Notch,
            Reverb,
            Echo,
            Chorus,
            Flanger,
            Distortion,
            Compressor,
            Limiter;
        }

        public enum SoundEventType;
        {
            Unknown,
            SoundLoaded,
            SoundUnloaded,
            SoundPlayed,
            SoundStopped,
            SoundCompleted,
            SoundLooped,
            VolumeChanged,
            ChannelStarted,
            ChannelStopped,
            ChannelPaused,
            ChannelResumed,
            ChannelVolumeChanged,
            MasterVolumeChanged,
            AllSoundsStopped,
            BankLoaded,
            BankUnloaded,
            BankSaved;
        }

        public class SoundBankConfiguration;
        {
            public long MaxSoundSize { get; set; }
            public long MaxMemoryUsage { get; set; }
            public LoadStrategy DefaultLoadStrategy { get; set; }
            public bool EnableStreaming { get; set; }
            public int StreamingBufferSize { get; set; }
            public bool EnableCompression { get; set; }
            public CompressionLevel CompressionLevel { get; set; }
            public int MaxChannels { get; set; }
            public float DefaultVolume { get; set; }
            public bool Enable3DSound { get; set; }
            public bool EnableEffects { get; set; }
            public bool CacheEnabled { get; set; }
            public long CacheSize { get; set; }
            public bool PreloadSounds { get; set; }
            public LogLevel LogLevel { get; set; }
        }

        public class LoadSoundOptions;
        {
            public static LoadSoundOptions Default => new LoadSoundOptions();

            public string Name { get; set; }
            public LoadStrategy LoadStrategy { get; set; } = LoadStrategy.Memory;
            public string Category { get; set; }
            public float Volume { get; set; } = 1.0f;
            public int Priority { get; set; } = 100;
            public bool IsEncrypted { get; set; } = false;
            public string EncryptionKey { get; set; }
            public bool IsCompressed { get; set; } = false;
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        public class PlayOptions;
        {
            public static PlayOptions Default => new PlayOptions();

            public float Volume { get; set; } = 1.0f;
            public float Pan { get; set; } = 0.0f;
            public float Pitch { get; set; } = 1.0f;
            public bool Loop { get; set; } = false;
            public int LoopCount { get; set; } = 0; // 0 = infinite;
            public TimeSpan StartTime { get; set; } = TimeSpan.Zero;
            public TimeSpan EndTime { get; set; } = TimeSpan.Zero;
            public bool FadeIn { get; set; } = false;
            public TimeSpan FadeInDuration { get; set; } = TimeSpan.FromSeconds(1);
            public bool FadeOut { get; set; } = false;
            public TimeSpan FadeOutDuration { get; set; } = TimeSpan.FromSeconds(1);
            public int Priority { get; set; } = 100;
            public List<string> Filters { get; set; } = new List<string>();
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        }

        public class StopOptions;
        {
            public static StopOptions Default => new StopOptions();

            public bool Immediate { get; set; } = true;
            public bool FadeOut { get; set; } = false;
            public TimeSpan FadeOutDuration { get; set; } = TimeSpan.FromSeconds(1);
            public bool ReleaseChannel { get; set; } = true;
        }

        public class SoundAsset;
        {
            public string SoundId { get; set; }
            public string Name { get; set; }
            public byte[] AudioData { get; set; }
            public StreamedSound StreamedSound { get; set; }
            public string FilePath { get; set; }
            public AudioFormat AudioFormat { get; set; }
            public TimeSpan Duration { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int BitsPerSample { get; set; }
            public string Category { get; set; }
            public float DefaultVolume { get; set; } = 1.0f;
            public int Priority { get; set; } = 100;
            public LoadStrategy LoadStrategy { get; set; }
            public bool IsLoaded { get; set; }
            public DateTime LoadTime { get; set; }
            public long MemoryUsage { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

            public SoundAsset(string soundId, string name)
            {
                SoundId = soundId;
                Name = name;
            }

            public async Task<OperationResult> LoadAsync(CancellationToken cancellationToken = default)
            {
                try
                {
                    if (IsLoaded)
                    {
                        return OperationResult.Success();
                    }

                    if (string.IsNullOrEmpty(FilePath))
                    {
                        return OperationResult.Failure("No file path specified");
                    }

                    // On-demand loading için;
                    var fileBytes = await File.ReadAllBytesAsync(FilePath, cancellationToken);
                    using var memoryStream = new MemoryStream(fileBytes);
                    using var waveStream = CreateWaveStream(memoryStream, AudioFormat);

                    if (waveStream == null)
                    {
                        return OperationResult.Failure($"Failed to create wave stream for: {FilePath}");
                    }

                    AudioData = new byte[waveStream.Length];
                    await waveStream.ReadAsync(AudioData, 0, AudioData.Length, cancellationToken);

                    Duration = waveStream.TotalTime;
                    SampleRate = waveStream.WaveFormat.SampleRate;
                    Channels = waveStream.WaveFormat.Channels;
                    BitsPerSample = waveStream.WaveFormat.BitsPerSample;

                    IsLoaded = true;
                    LoadTime = DateTime.UtcNow;
                    MemoryUsage = AudioData.Length;

                    return OperationResult.Success();
                }
                catch (Exception ex)
                {
                    return OperationResult.Failure($"Load failed: {ex.Message}");
                }
            }

            public async Task<OperationResult> UnloadAsync(CancellationToken cancellationToken = default)
            {
                try
                {
                    if (!IsLoaded)
                    {
                        return OperationResult.Success();
                    }

                    AudioData = null;
                    StreamedSound?.DisposeAsync().GetAwaiter().GetResult();
                    StreamedSound = null;

                    IsLoaded = false;
                    MemoryUsage = 0;

                    return OperationResult.Success();
                }
                catch (Exception ex)
                {
                    return OperationResult.Failure($"Unload failed: {ex.Message}");
                }
            }

            private WaveStream CreateWaveStream(Stream stream, AudioFormat format)
            {
                // Wave stream oluşturma implementasyonu;
                // Bu, SoundBank sınıfındaki metotla aynı olmalı;
                return null;
            }
        }

        public class SoundCategory;
        {
            public string CategoryId { get; private set; }
            public CategoryConfiguration Configuration { get; private set; }
            public float Volume { get; set; }
            public bool IsMuted { get; set; }
            public HashSet<string> SoundIds { get; private set; }

            public SoundCategory(string categoryId, CategoryConfiguration configuration)
            {
                CategoryId = categoryId;
                Configuration = configuration;
                SoundIds = new HashSet<string>();
                Volume = configuration.Volume;
                IsMuted = configuration.IsMuted;
            }

            public void AddSound(string soundId)
            {
                SoundIds.Add(soundId);

                if (Configuration.MaxSounds > 0 && SoundIds.Count > Configuration.MaxSounds)
                {
                    // En eski sesi çıkar;
                    var oldest = SoundIds.First();
                    SoundIds.Remove(oldest);
                }
            }

            public void RemoveSound(string soundId)
            {
                SoundIds.Remove(soundId);
            }
        }

        public class SoundChannel : IDisposable
        {
            public int ChannelId { get; private set; }
            public bool IsPlaying { get; private set; }
            public bool IsPaused { get; private set; }
            public bool IsReserved { get; private set; }
            public SoundAsset CurrentSound { get; private set; }
            public float Volume { get; private set; }
            public float Pan { get; private set; }
            public TimeSpan PlaybackPosition { get; private set; }
            public TimeSpan PlaybackTime { get; private set; }
            public int Priority { get; private set; }

            private readonly MixingSampleProvider _mixerProvider;
            private readonly List<SoundFilter> _filters;
            private IWaveProvider _waveProvider;
            private bool _isDisposed;

            public event EventHandler<ChannelEventArgs> OnChannelEvent;

            public SoundChannel(int channelId, MixingSampleProvider mixerProvider)
            {
                ChannelId = channelId;
                _mixerProvider = mixerProvider;
                _filters = new List<SoundFilter>();
                IsPlaying = false;
                IsPaused = false;
                IsReserved = false;
            }

            public void Configure(SoundAsset soundAsset, AudioData audioData, PlayOptions options)
            {
                CurrentSound = soundAsset;
                Volume = options.Volume;
                Pan = options.Pan;
                Priority = options.Priority;

                // Wave provider oluştur;
                if (audioData.IsStreaming)
                {
                    _waveProvider = CreateStreamingProvider(audioData);
                }
                else;
                {
                    _waveProvider = CreateMemoryProvider(audioData);
                }

                // Filtreleri uygula;
                ApplyFilters(options.Filters);

                // Mixer'a ekle;
                _mixerProvider.AddMixerInput(_waveProvider);
            }

            public async Task<OperationResult> PlayAsync(CancellationToken cancellationToken = default)
            {
                try
                {
                    IsPlaying = true;
                    IsPaused = false;
                    PlaybackTime = TimeSpan.Zero;

                    OnChannelEvent?.Invoke(this, new ChannelEventArgs;
                    {
                        EventType = ChannelEventType.Started,
                        ChannelId = ChannelId,
                        SoundId = CurrentSound?.SoundId,
                        Timestamp = DateTime.UtcNow;
                    });

                    return OperationResult.Success();
                }
                catch (Exception ex)
                {
                    return OperationResult.Failure($"Play failed: {ex.Message}");
                }
            }

            public void Stop(StopOptions options)
            {
                if (!IsPlaying) return;

                IsPlaying = false;
                IsPaused = false;

                // Mixer'dan çıkar;
                if (_waveProvider != null)
                {
                    // Mixer'dan çıkarma işlemi;
                }

                OnChannelEvent?.Invoke(this, new ChannelEventArgs;
                {
                    EventType = ChannelEventType.Stopped,
                    ChannelId = ChannelId,
                    SoundId = CurrentSound?.SoundId,
                    Timestamp = DateTime.UtcNow;
                });

                CurrentSound = null;
                IsReserved = false;
            }

            public void SetVolume(float volume)
            {
                Volume = Math.Clamp(volume, 0, 1);
                // Volume değişikliğini uygula;
            }

            public void ApplyMasterVolume(float masterVolume)
            {
                // Master volume'ü uygula;
            }

            public void AddFilter(SoundFilter filter)
            {
                _filters.Add(filter);
                // Filter'ı uygula;
            }

            private IWaveProvider CreateMemoryProvider(AudioData audioData)
            {
                // Memory-based wave provider oluştur;
                return null;
            }

            private IWaveProvider CreateStreamingProvider(AudioData audioData)
            {
                // Streaming wave provider oluştur;
                return null;
            }

            private void ApplyFilters(List<string> filterIds)
            {
                // Filtreleri uygula;
            }

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
                        Stop(new StopOptions { Immediate = true });
                        _waveProvider = null;
                        _filters.Clear();
                    }

                    _isDisposed = true;
                }
            }
        }

        public class SoundPool;
        {
            public string PoolId { get; private set; }
            public SoundPoolConfiguration Configuration { get; private set; }
            public List<string> SoundIds { get; private set; }
            public PoolSelectionMode SelectionMode { get; private set; }
            public float Volume { get; set; }
            public int MaxConcurrent { get; set; }

            private readonly Random _random;
            private int _currentIndex;
            private readonly List<int> _activeChannels;

            public SoundPool(string poolId, SoundPoolConfiguration configuration)
            {
                PoolId = poolId;
                Configuration = configuration;
                SoundIds = new List<string>(configuration.SoundIds);
                SelectionMode = configuration.SelectionMode;
                Volume = configuration.Volume;
                MaxConcurrent = configuration.MaxConcurrent;

                _random = new Random();
                _currentIndex = 0;
                _activeChannels = new List<int>();
            }

            public string GetNextSound()
            {
                if (SoundIds.Count == 0)
                {
                    return null;
                }

                string soundId = null;

                switch (SelectionMode)
                {
                    case PoolSelectionMode.Sequential:
                        soundId = SoundIds[_currentIndex];
                        _currentIndex = (_currentIndex + 1) % SoundIds.Count;
                        break;

                    case PoolSelectionMode.Random:
                        var index = _random.Next(SoundIds.Count);
                        soundId = SoundIds[index];
                        break;

                    case PoolSelectionMode.Shuffle:
                        // Shuffle implementasyonu;
                        break;

                    case PoolSelectionMode.Weighted:
                        // Weighted selection implementasyonu;
                        break;
                }

                return soundId;
            }
        }

        public class Playlist;
        {
            public string PlaylistId { get; private set; }
            public PlaylistConfiguration Configuration { get; private set; }
            public List<string> SoundIds { get; private set; }
            public PlayOrder PlayOrder { get; set; }
            public bool Loop { get; set; }
            public bool Crossfade { get; set; }
            public TimeSpan CrossfadeDuration { get; set; }
            public bool IsPlaying { get; private set; }
            public int CurrentIndex { get; private set; }

            private readonly Random _random;
            private List<int> _playOrder;
            private bool _isShuffled;

            public Playlist(string playlistId, PlaylistConfiguration configuration)
            {
                PlaylistId = playlistId;
                Configuration = configuration;
                SoundIds = new List<string>(configuration.SoundIds);
                PlayOrder = configuration.PlayOrder;
                Loop = configuration.Loop;
                Crossfade = configuration.Crossfade;
                CrossfadeDuration = configuration.CrossfadeDuration;

                _random = new Random();
                _playOrder = new List<int>();
                ResetPlayOrder();
            }

            public void StartPlayback(PlaylistPlayOptions options)
            {
                IsPlaying = true;
                CurrentIndex = options.StartIndex;

                if (options.Shuffle)
                {
                    Shuffle();
                }
            }

            public async Task<PlaylistItemResult> PlayNextAsync(Func<string, PlayOptions, Task<PlaySoundResult>> playSoundFunc, CancellationToken cancellationToken = default)
            {
                if (!IsPlaying || SoundIds.Count == 0)
                {
                    return PlaylistItemResult.Failure("Playlist not playing or empty");
                }

                if (CurrentIndex >= SoundIds.Count)
                {
                    if (Loop)
                    {
                        CurrentIndex = 0;
                        if (PlayOrder == PlayOrder.Shuffle)
                        {
                            Shuffle();
                        }
                    }
                    else;
                    {
                        IsPlaying = false;
                        return PlaylistItemResult.Failure("End of playlist");
                    }
                }

                var soundId = SoundIds[_playOrder[CurrentIndex]];
                CurrentIndex++;

                var playOptions = new PlayOptions;
                {
                    Volume = 1.0f,
                    FadeIn = Crossfade,
                    FadeInDuration = CrossfadeDuration;
                };

                var result = await playSoundFunc(soundId, playOptions);

                return new PlaylistItemResult;
                {
                    Success = result.Success,
                    SoundId = soundId,
                    ChannelId = result.ChannelId,
                    ErrorMessage = result.ErrorMessage;
                };
            }

            private void ResetPlayOrder()
            {
                _playOrder.Clear();
                for (int i = 0; i < SoundIds.Count; i++)
                {
                    _playOrder.Add(i);
                }
                _isShuffled = false;
            }

            private void Shuffle()
            {
                if (_isShuffled)
                {
                    ResetPlayOrder();
                }

                // Fisher-Yates shuffle;
                for (int i = _playOrder.Count - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    (_playOrder[i], _playOrder[j]) = (_playOrder[j], _playOrder[i]);
                }

                _isShuffled = true;
            }
        }

        public class AudioMixer;
        {
            public string MixerId { get; private set; }
            public string Name { get; private set; }
            public float Volume { get; set; }
            public bool IsMuted { get; set; }
            public List<string> Channels { get; private set; }
            public Dictionary<string, float> ChannelVolumes { get; private set; }

            public AudioMixer(string mixerId, string name, float volume)
            {
                MixerId = mixerId;
                Name = name;
                Volume = volume;
                IsMuted = false;
                Channels = new List<string>();
                ChannelVolumes = new Dictionary<string, float>();
            }

            public void AddChannel(string channelId, float volume = 1.0f)
            {
                if (!Channels.Contains(channelId))
                {
                    Channels.Add(channelId);
                    ChannelVolumes[channelId] = volume;
                }
            }

            public void RemoveChannel(string channelId)
            {
                Channels.Remove(channelId);
                ChannelVolumes.Remove(channelId);
            }

            public void SetChannelVolume(string channelId, float volume)
            {
                if (Channels.Contains(channelId))
                {
                    ChannelVolumes[channelId] = Math.Clamp(volume, 0, 1);
                }
            }
        }

        public class SoundFilter;
        {
            public string FilterId { get; private set; }
            public FilterType FilterType { get; private set; }
            public float Intensity { get; set; }
            public Dictionary<string, object> Parameters { get; private set; }
            public bool IsEnabled { get; set; }

            public SoundFilter(string filterId, FilterType filterType, float intensity)
            {
                FilterId = filterId;
                FilterType = filterType;
                Intensity = intensity;
                Parameters = new Dictionary<string, object>();
                IsEnabled = true;
            }

            public void SetParameter(string key, object value)
            {
                Parameters[key] = value;
            }
        }

        public class StreamedSound : IAsyncDisposable;
        {
            public string SoundId { get; private set; }
            public string FilePath { get; private set; }
            public int BufferSize { get; private set; }
            public TimeSpan Duration { get; private set; }
            public int SampleRate { get; private set; }
            public int Channels { get; private set; }
            public int BitsPerSample { get; private set; }
            public long BytesStreamed { get; private set; }
            public bool IsStreaming { get; private set; }

            private FileStream _fileStream;
            private WaveStream _waveStream;
            private bool _isDisposed;

            public StreamedSound(string soundId, string filePath, int bufferSize)
            {
                SoundId = soundId;
                FilePath = filePath;
                BufferSize = bufferSize;
                BytesStreamed = 0;
                IsStreaming = false;
            }

            public async Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                try
                {
                    _fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
                    _waveStream = CreateWaveStream(_fileStream, GetAudioFormat(FilePath));

                    if (_waveStream == null)
                    {
                        throw new InvalidOperationException($"Failed to create wave stream for: {FilePath}");
                    }

                    Duration = _waveStream.TotalTime;
                    SampleRate = _waveStream.WaveFormat.SampleRate;
                    Channels = _waveStream.WaveFormat.Channels;
                    BitsPerSample = _waveStream.WaveFormat.BitsPerSample;

                    IsStreaming = true;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to initialize streamed sound: {ex.Message}", ex);
                }
            }

            public Stream GetStream()
            {
                return _waveStream;
            }

            public async Task<byte[]> ReadNextChunkAsync(CancellationToken cancellationToken = default)
            {
                if (_waveStream == null || _waveStream.Position >= _waveStream.Length)
                {
                    return null;
                }

                var chunkSize = Math.Min(BufferSize, _waveStream.Length - _waveStream.Position);
                var buffer = new byte[chunkSize];

                var bytesRead = await _waveStream.ReadAsync(buffer, 0, (int)chunkSize, cancellationToken);
                BytesStreamed += bytesRead;

                if (bytesRead < chunkSize)
                {
                    Array.Resize(ref buffer, bytesRead);
                }

                return buffer;
            }

            public void Reset()
            {
                _waveStream?.Seek(0, SeekOrigin.Begin);
                BytesStreamed = 0;
            }

            private AudioFormat GetAudioFormat(string filePath)
            {
                // Audio format belirleme;
                return AudioFormat.Unknown;
            }

            private WaveStream CreateWaveStream(Stream stream, AudioFormat format)
            {
                // Wave stream oluşturma;
                return null;
            }

            public async ValueTask DisposeAsync()
            {
                if (!_isDisposed)
                {
                    _waveStream?.Dispose();
                    _fileStream?.Dispose();

                    _isDisposed = true;
                }
            }
        }

        public class MemoryCache;
        {
            private readonly Dictionary<string, CacheItem> _cache;
            private readonly long _maxSize;
            private long _currentSize;
            private readonly object _lockObject = new object();

            public MemoryCache(long maxSize = 100 * 1024 * 1024)
            {
                _cache = new Dictionary<string, CacheItem>();
                _maxSize = maxSize;
                _currentSize = 0;
            }

            public void Add(string key, byte[] data, TimeSpan ttl)
            {
                lock (_lockObject)
                {
                    if (_cache.ContainsKey(key))
                    {
                        Remove(key);
                    }

                    // Cache boyutu kontrolü;
                    while (_currentSize + data.Length > _maxSize && _cache.Count > 0)
                    {
                        var oldestKey = _cache.OrderBy(x => x.Value.LastAccessed).First().Key;
                        Remove(oldestKey);
                    }

                    var cacheItem = new CacheItem;
                    {
                        Data = data,
                        Size = data.Length,
                        Created = DateTime.UtcNow,
                        LastAccessed = DateTime.UtcNow,
                        Expires = DateTime.UtcNow.Add(ttl)
                    };

                    _cache[key] = cacheItem;
                    _currentSize += data.Length;
                }
            }

            public byte[] Get(string key)
            {
                lock (_lockObject)
                {
                    if (_cache.TryGetValue(key, out var cacheItem))
                    {
                        if (cacheItem.Expires < DateTime.UtcNow)
                        {
                            Remove(key);
                            return null;
                        }

                        cacheItem.LastAccessed = DateTime.UtcNow;
                        return cacheItem.Data;
                    }

                    return null;
                }
            }

            public void Remove(string key)
            {
                lock (_lockObject)
                {
                    if (_cache.TryGetValue(key, out var cacheItem))
                    {
                        _currentSize -= cacheItem.Size;
                        _cache.Remove(key);
                    }
                }
            }

            public void Clear()
            {
                lock (_lockObject)
                {
                    _cache.Clear();
                    _currentSize = 0;
                }
            }

            private class CacheItem;
            {
                public byte[] Data { get; set; }
                public long Size { get; set; }
                public DateTime Created { get; set; }
                public DateTime LastAccessed { get; set; }
                public DateTime Expires { get; set; }
            }
        }

        public class CompressionEngine;
        {
            public async Task<byte[]> CompressAsync(byte[] data, CompressionLevel level, CancellationToken cancellationToken = default)
            {
                // Compression implementasyonu;
                return await Task.FromResult(data);
            }

            public async Task<byte[]> DecompressAsync(byte[] data, CancellationToken cancellationToken = default)
            {
                // Decompression implementasyonu;
                return await Task.FromResult(data);
            }
        }

        public class StreamingManager;
        {
            // Streaming yönetimi implementasyonu;
        }
        #endregion;
    }

    #region Interfaces;
    public interface ISoundBank : IDisposable
    {
        Task<OperationResult> InitializeAsync(SoundBankConfiguration configuration = null, CancellationToken cancellationToken = default);
        Task<OperationResult> LoadBankAsync(string bankName = null, LoadOptions options = null, CancellationToken cancellationToken = default);
        Task<OperationResult> UnloadBankAsync(CancellationToken cancellationToken = default);
        Task<SoundLoadResult> LoadSoundAsync(string soundId, string filePath, LoadSoundOptions options = null, CancellationToken cancellationToken = default);
        Task<OperationResult> UnloadSoundAsync(string soundId, CancellationToken cancellationToken = default);
        Task<PlaySoundResult> PlaySoundAsync(string soundId, PlayOptions options = null, CancellationToken cancellationToken = default);
        Task<PlaySoundResult> PlayFromPoolAsync(string poolId, PlayOptions options = null, CancellationToken cancellationToken = default);
        OperationResult StopSound(string soundId, StopOptions options = null);
        OperationResult StopChannel(int channelId, StopOptions options = null);
        OperationResult StopAllSounds(StopOptions options = null);
        SoundStatus GetSoundStatus(string soundId);
        ChannelStatus GetChannelStatus(int channelId);
        OperationResult SetVolume(string soundId, float volume);
        OperationResult SetChannelVolume(int channelId, float volume);
        OperationResult SetMasterVolume(float volume);
        OperationResult AddFilter(SoundFilter filter);
        OperationResult CreateCategory(string categoryId, CategoryConfiguration configuration = null);
        List<SoundAsset> GetSoundsByCategory(string categoryId);
        OperationResult CreateSoundPool(string poolId, SoundPoolConfiguration configuration);
        OperationResult CreatePlaylist(string playlistId, PlaylistConfiguration configuration);
        Task<PlaylistResult> PlayPlaylistAsync(string playlistId, PlaylistPlayOptions options = null, CancellationToken cancellationToken = default);
        SoundBankMetrics GetMetrics();
        Task<OperationResult> SaveBankAsync(string bankName = null, CancellationToken cancellationToken = default);

        event EventHandler<SoundBankEventArgs> OnSoundEvent;
        event EventHandler<BankLoadProgressEventArgs> OnLoadProgress;
    }
    #endregion;
}
