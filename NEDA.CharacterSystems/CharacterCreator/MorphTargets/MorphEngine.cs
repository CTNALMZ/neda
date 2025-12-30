using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Numerics;
using System.Threading;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.Configuration;
using NEDA.CharacterSystems.CharacterCreator.AppearanceCustomization;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.EngineIntegration.RenderManager;
using NEDA.EngineIntegration.Unreal;

namespace NEDA.CharacterSystems.CharacterCreator.MorphTargets;
{
    /// <summary>
    /// Morph Target (Blend Shape) yönetim motoru;
    /// Karakter yüz ifadeleri, şekil deformasyonları ve detaylı morfoloji kontrolü sağlar;
    /// </summary>
    public class MorphEngine : IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private readonly RenderEngine _renderEngine;
        private readonly BoneManager _boneManager;
        private readonly IKSystem _ikSystem;

        private Dictionary<string, MorphTarget> _morphTargets;
        private Dictionary<string, MorphChannel> _morphChannels;
        private Dictionary<string, MorphPreset> _morphPresets;
        private Dictionary<string, MorphComposition> _activeCompositions;
        private Dictionary<string, MorphDriver> _morphDrivers;

        private MorphConfiguration _configuration;
        private MorphPerformanceSettings _performanceSettings;
        private MorphCache _morphCache;

        private bool _isInitialized;
        private bool _isProcessing;
        private int _activeMorphCount;
        private readonly object _lockObject = new object();
        private readonly Random _random = new Random();

        private CancellationTokenSource _processingCancellation;
        private Task _morphUpdateTask;

        // Performance tracking;
        private MorphPerformanceMetrics _performanceMetrics;
        private DateTime _lastMetricsUpdate;

        /// <summary>
        Toplam yüklü morph target sayısı;
        /// </summary>
        public int TotalMorphTargets => _morphTargets?.Count ?? 0;

        /// <summary>
        /// Aktif morph kanalı sayısı;
        /// </summary>
        public int ActiveChannelCount => _morphChannels?.Values.Count(c => c.IsActive) ?? 0;

        /// <summary>
        /// Morph işleme motorunun aktif olup olmadığı;
        /// </summary>
        public bool IsProcessing => _isProcessing;

        /// <summary>
        /// Performans metrikleri;
        /// </summary>
        public MorphPerformanceMetrics PerformanceMetrics => _performanceMetrics;

        /// <summary>
        /// Cache istatistikleri;
        /// </summary>
        public MorphCacheStatistics CacheStatistics => _morphCache?.Statistics;

        #endregion;

        #region Constructors;

        /// <summary>
        /// MorphEngine yapıcı metodu;
        /// </summary>
        public MorphEngine(ILogger logger, AppConfig config, RenderEngine renderEngine,
                          BoneManager boneManager, IKSystem ikSystem)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _renderEngine = renderEngine ?? throw new ArgumentNullException(nameof(renderEngine));
            _boneManager = boneManager ?? throw new ArgumentNullException(nameof(boneManager));
            _ikSystem = ikSystem ?? throw new ArgumentNullException(nameof(ikSystem));

            InitializeCollections();
            _configuration = new MorphConfiguration();
            _performanceSettings = new MorphPerformanceSettings();
            _performanceMetrics = new MorphPerformanceMetrics();
            _isInitialized = false;
            _isProcessing = false;
            _activeMorphCount = 0;
            _lastMetricsUpdate = DateTime.UtcNow;
        }

        private void InitializeCollections()
        {
            _morphTargets = new Dictionary<string, MorphTarget>();
            _morphChannels = new Dictionary<string, MorphChannel>();
            _morphPresets = new Dictionary<string, MorphPreset>();
            _activeCompositions = new Dictionary<string, MorphComposition>();
            _morphDrivers = new Dictionary<string, MorphDriver>();
            _morphCache = new MorphCache();
        }

        #endregion;

        #region Public Methods - Initialization;

        /// <summary>
        /// MorphEngine'ı başlatır ve yapılandırır;
        /// </summary>
        public async Task InitializeAsync(MorphConfiguration configuration = null)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("MorphEngine zaten başlatılmış.");
                return;
            }

            try
            {
                _logger.LogInformation("MorphEngine başlatılıyor...");

                // Yapılandırmayı uygula;
                if (configuration != null)
                {
                    _configuration = configuration;
                }
                else;
                {
                    await LoadConfigurationAsync();
                }

                // Performans ayarlarını yükle;
                await LoadPerformanceSettingsAsync();

                // Cache sistemini başlat;
                await _morphCache.InitializeAsync(_configuration.CacheSettings);

                // Morph target'ları yükle;
                await LoadMorphTargetsAsync();

                // Morph preset'lerini yükle;
                await LoadMorphPresetsAsync();

                // Varsayılan morph kanallarını oluştur;
                InitializeDefaultChannels();

                // Morph driver'larını başlat;
                InitializeMorphDrivers();

                // Performans izleme başlat;
                StartPerformanceMonitoring();

                _isInitialized = true;

                _logger.LogInformation($"MorphEngine başarıyla başlatıldı. {_morphTargets.Count} morph target, {_morphPresets.Count} preset yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"MorphEngine başlatma hatası: {ex.Message}", ex);
                throw new MorphEngineException("MorphEngine başlatma hatası", ex);
            }
        }

        /// <summary>
        /// MorphEngine'ı durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                // İşlemeyi durdur;
                await StopProcessingAsync();

                // Performans izlemeyi durdur;
                StopPerformanceMonitoring();

                // Cache'i temizle;
                await _morphCache.ClearAsync();

                // Kaynakları serbest bırak;
                await ReleaseResourcesAsync();

                _isInitialized = false;

                _logger.LogInformation("MorphEngine durduruldu.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"MorphEngine kapatma hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Morph işlemeyi başlatır;
        /// </summary>
        public void StartProcessing()
        {
            if (!_isInitialized)
                throw new MorphEngineException("MorphEngine başlatılmamış. Önce InitializeAsync çağrılmalı.");

            if (_isProcessing)
            {
                _logger.LogWarning("Morph işleme zaten çalışıyor.");
                return;
            }

            _processingCancellation = new CancellationTokenSource();
            _isProcessing = true;

            // Morph update task'ını başlat;
            _morphUpdateTask = Task.Run(async () => await MorphUpdateLoopAsync(_processingCancellation.Token));

            _logger.LogInformation("Morph işleme başlatıldı.");
        }

        /// <summary>
        /// Morph işlemeyi durdurur;
        /// </summary>
        public async Task StopProcessingAsync()
        {
            if (!_isProcessing)
                return;

            _isProcessing = false;

            // İptal token'ını tetikle;
            _processingCancellation?.Cancel();

            if (_morphUpdateTask != null && !_morphUpdateTask.IsCompleted)
            {
                await _morphUpdateTask;
            }

            _processingCancellation?.Dispose();
            _processingCancellation = null;

            _logger.LogInformation("Morph işleme durduruldu.");
        }

        #endregion;

        #region Public Methods - Morph Target Management;

        /// <summary>
        /// Yeni bir morph target oluşturur;
        /// </summary>
        public async Task<MorphTarget> CreateMorphTargetAsync(string targetId, MorphTargetData data,
            MorphTargetOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            options ??= new MorphTargetOptions();

            try
            {
                _logger.LogDebug($"Yeni morph target oluşturuluyor: {targetId}");

                // Vertex verilerini doğrula;
                ValidateMorphData(data);

                // Morph target oluştur;
                var morphTarget = new MorphTarget(targetId, data)
                {
                    Name = options.Name ?? targetId,
                    Category = options.Category,
                    Region = options.Region,
                    InfluenceRange = options.InfluenceRange,
                    SymmetryMode = options.SymmetryMode,
                    CreatedDate = DateTime.UtcNow,
                    Version = 1;
                };

                // Quality seviyesine göre optimizasyon;
                if (options.OptimizeForPerformance)
                {
                    morphTarget = await OptimizeMorphTargetAsync(morphTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Symmetry uygula;
                if (options.SymmetryMode != MorphSymmetry.None)
                {
                    morphTarget = await ApplySymmetryAsync(morphTarget, options.SymmetryMode);
                }

                // Cache'e kaydet;
                if (options.CacheResult)
                {
                    await _morphCache.StoreMorphTargetAsync(morphTarget);
                }

                lock (_lockObject)
                {
                    _morphTargets[targetId] = morphTarget;
                }

                _logger.LogInformation($"Morph target oluşturuldu: {targetId} ({morphTarget.VertexCount} vertex)");

                return morphTarget.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target oluşturma hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target oluşturma hatası", ex);
            }
        }

        /// <summary>
        /// Mevcut bir morph target'ı getirir;
        /// </summary>
        public async Task<MorphTarget> GetMorphTargetAsync(string targetId, bool forceReload = false)
        {
            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            // Önce cache'den kontrol et;
            if (!forceReload)
            {
                var cachedTarget = await _morphCache.GetMorphTargetAsync(targetId);
                if (cachedTarget != null)
                {
                    _logger.LogDebug($"Morph target cache'ten getirildi: {targetId}");
                    return cachedTarget;
                }
            }

            lock (_lockObject)
            {
                if (!_morphTargets.TryGetValue(targetId, out var morphTarget))
                {
                    throw new MorphEngineException($"Morph target '{targetId}' bulunamadı.");
                }

                return morphTarget.Clone();
            }
        }

        /// <summary>
        /// Morph target'ı günceller;
        /// </summary>
        public async Task UpdateMorphTargetAsync(string targetId, MorphTargetUpdate update)
        {
            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            if (update == null)
                throw new ArgumentNullException(nameof(update));

            try
            {
                MorphTarget morphTarget;
                lock (_lockObject)
                {
                    if (!_morphTargets.TryGetValue(targetId, out morphTarget))
                    {
                        throw new MorphEngineException($"Morph target '{targetId}' bulunamadı.");
                    }
                }

                // Güncelleme işlemleri;
                if (update.Data != null)
                {
                    morphTarget.Data = update.Data;
                }

                if (!string.IsNullOrEmpty(update.Name))
                {
                    morphTarget.Name = update.Name;
                }

                if (update.Category.HasValue)
                {
                    morphTarget.Category = update.Category.Value;
                }

                if (update.Region.HasValue)
                {
                    morphTarget.Region = update.Region.Value;
                }

                if (update.InfluenceRange.HasValue)
                {
                    morphTarget.InfluenceRange = update.InfluenceRange.Value;
                }

                if (update.SymmetryMode.HasValue)
                {
                    morphTarget.SymmetryMode = update.SymmetryMode.Value;
                }

                morphTarget.LastModified = DateTime.UtcNow;
                morphTarget.Version++;

                // Yeniden optimize et;
                if (update.Optimize)
                {
                    morphTarget = await OptimizeMorphTargetAsync(morphTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Cache'i güncelle;
                if (update.UpdateCache)
                {
                    await _morphCache.StoreMorphTargetAsync(morphTarget);
                }

                lock (_lockObject)
                {
                    _morphTargets[targetId] = morphTarget;
                }

                // Bu morph target'ı kullanan kanalları güncelle;
                await UpdateChannelsUsingTargetAsync(targetId);

                _logger.LogDebug($"Morph target güncellendi: {targetId} (v{morphTarget.Version})");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target güncelleme hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target güncelleme hatası", ex);
            }
        }

        /// <summary>
        /// Morph target'ı siler;
        /// </summary>
        public async Task<bool> DeleteMorphTargetAsync(string targetId)
        {
            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            try
            {
                bool removed;
                lock (_lockObject)
                {
                    removed = _morphTargets.Remove(targetId);
                }

                if (removed)
                {
                    // Cache'den sil;
                    await _morphCache.RemoveMorphTargetAsync(targetId);

                    // Bu target'ı kullanan kanallardan kaldır;
                    await RemoveTargetFromChannelsAsync(targetId);

                    _logger.LogDebug($"Morph target silindi: {targetId}");
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target silme hatası: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Birden fazla morph target'ı birleştirir;
        /// </summary>
        public async Task<MorphTarget> CombineMorphTargetsAsync(List<string> targetIds,
            string combinedId = null, CombineOptions options = null)
        {
            if (targetIds == null || targetIds.Count == 0)
                throw new ArgumentException("En az bir target ID gereklidir.", nameof(targetIds));

            options ??= new CombineOptions();

            try
            {
                _logger.LogDebug($"{targetIds.Count} morph target birleştiriliyor...");

                // Target'ları yükle;
                var morphTargets = new List<MorphTarget>();
                foreach (var targetId in targetIds)
                {
                    var target = await GetMorphTargetAsync(targetId);
                    morphTargets.Add(target);
                }

                // Base mesh kontrolü;
                if (morphTargets.Count == 0 || morphTargets[0].Data.BaseVertices == null)
                {
                    throw new MorphEngineException("Geçerli base mesh verisi bulunamadı.");
                }

                // Birleştirme işlemi;
                var combinedData = await CombineMorphDataAsync(morphTargets, options);

                // Yeni target oluştur;
                combinedId ??= $"combined_{string.Join("_", targetIds)}";

                var combinedTarget = new MorphTarget(combinedId, combinedData)
                {
                    Name = options.Name ?? $"Combined: {string.Join(", ", morphTargets.Select(t => t.Name))}",
                    Category = options.Category ?? MorphCategory.Combined,
                    Region = MorphRegion.FullFace,
                    InfluenceRange = options.InfluenceRange,
                    SymmetryMode = options.SymmetryMode ?? MorphSymmetry.None,
                    CreatedDate = DateTime.UtcNow,
                    Version = 1;
                };

                // Optimizasyon;
                if (options.Optimize)
                {
                    combinedTarget = await OptimizeMorphTargetAsync(combinedTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Kaydet;
                lock (_lockObject)
                {
                    _morphTargets[combinedId] = combinedTarget;
                }

                // Cache'e ekle;
                if (options.CacheResult)
                {
                    await _morphCache.StoreMorphTargetAsync(combinedTarget);
                }

                _logger.LogInformation($"{targetIds.Count} morph target birleştirildi: {combinedId}");

                return combinedTarget.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target birleştirme hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target birleştirme hatası", ex);
            }
        }

        /// <summary>
        /// Morph target'ı klonlar;
        /// </summary>
        public async Task<MorphTarget> CloneMorphTargetAsync(string sourceId, string newTargetId,
            CloneOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source ID boş olamaz.", nameof(sourceId));

            if (string.IsNullOrWhiteSpace(newTargetId))
                throw new ArgumentException("New target ID boş olamaz.", nameof(newTargetId));

            options ??= new CloneOptions();

            try
            {
                // Source target'ı getir;
                var sourceTarget = await GetMorphTargetAsync(sourceId);

                // Klon oluştur;
                var clonedTarget = sourceTarget.Clone();
                clonedTarget.Id = newTargetId;
                clonedTarget.Name = options.NewName ?? $"{sourceTarget.Name} (Clone)";
                clonedTarget.CreatedDate = DateTime.UtcNow;
                clonedTarget.LastModified = DateTime.UtcNow;
                clonedTarget.Version = 1;

                // Modifikasyonlar uygula;
                if (options.Modifications != null && options.Modifications.Count > 0)
                {
                    clonedTarget = await ApplyModificationsAsync(clonedTarget, options.Modifications);
                }

                // Symmetry uygula;
                if (options.ApplySymmetry != MorphSymmetry.None)
                {
                    clonedTarget = await ApplySymmetryAsync(clonedTarget, options.ApplySymmetry);
                }

                // Optimizasyon;
                if (options.Optimize)
                {
                    clonedTarget = await OptimizeMorphTargetAsync(clonedTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Kaydet;
                lock (_lockObject)
                {
                    _morphTargets[newTargetId] = clonedTarget;
                }

                // Cache'e ekle;
                if (options.CacheResult)
                {
                    await _morphCache.StoreMorphTargetAsync(clonedTarget);
                }

                _logger.LogDebug($"Morph target klonlandı: {sourceId} -> {newTargetId}");

                return clonedTarget.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target klonlama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target klonlama hatası", ex);
            }
        }

        #endregion;

        #region Public Methods - Morph Channel Management;

        /// <summary>
        /// Yeni bir morph kanalı oluşturur;
        /// </summary>
        public MorphChannel CreateMorphChannel(string channelId, MorphChannelOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Channel ID boş olamaz.", nameof(channelId));

            options ??= new MorphChannelOptions();

            lock (_lockObject)
            {
                if (_morphChannels.ContainsKey(channelId))
                {
                    throw new MorphEngineException($"Morph kanalı '{channelId}' zaten mevcut.");
                }

                var channel = new MorphChannel(channelId)
                {
                    Name = options.Name ?? channelId,
                    Category = options.Category,
                    Region = options.Region,
                    BlendMode = options.BlendMode,
                    Influence = options.Influence,
                    MinValue = options.MinValue,
                    MaxValue = options.MaxValue,
                    DefaultValue = options.DefaultValue,
                    IsActive = options.IsActive,
                    IsLocked = options.IsLocked,
                    CreatedDate = DateTime.UtcNow;
                };

                _morphChannels.Add(channelId, channel);

                _logger.LogDebug($"Yeni morph kanalı oluşturuldu: {channelId}");

                return channel.Clone();
            }
        }

        /// <summary>
        /// Morph kanalına target ekler;
        /// </summary>
        public async Task AddTargetToChannelAsync(string channelId, string targetId,
            float weight = 1.0f, bool normalize = true)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Channel ID boş olamaz.", nameof(channelId));

            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            try
            {
                MorphChannel channel;
                lock (_lockObject)
                {
                    if (!_morphChannels.TryGetValue(channelId, out channel))
                    {
                        throw new MorphEngineException($"Morph kanalı '{channelId}' bulunamadı.");
                    }
                }

                // Target'ı yükle;
                var target = await GetMorphTargetAsync(targetId);

                // Kanal region kontrolü;
                if (channel.Region != MorphRegion.Any && target.Region != MorphRegion.Any &&
                    channel.Region != target.Region)
                {
                    _logger.LogWarning($"Region uyumsuzluğu: Kanal={channel.Region}, Target={target.Region}");
                }

                // Kanal'a target ekle;
                var channelTarget = new MorphChannelTarget(target, weight);

                lock (_lockObject)
                {
                    channel.AddTarget(channelTarget);
                }

                // Normalize et;
                if (normalize)
                {
                    await NormalizeChannelWeightsAsync(channelId);
                }

                _logger.LogDebug($"Morph target kanala eklendi: {targetId} -> {channelId} (Weight: {weight})");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target kanala ekleme hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target kanala ekleme hatası", ex);
            }
        }

        /// <summary>
        /// Morph kanalı değerini ayarlar;
        /// </summary>
        public async Task SetChannelValueAsync(string channelId, float value,
            bool applyImmediately = true, bool updateDrivers = true)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Channel ID boş olamaz.", nameof(channelId));

            try
            {
                MorphChannel channel;
                lock (_lockObject)
                {
                    if (!_morphChannels.TryGetValue(channelId, out channel))
                    {
                        throw new MorphEngineException($"Morph kanalı '{channelId}' bulunamadı.");
                    }
                }

                // Değeri sınırla;
                value = Math.Clamp(value, channel.MinValue, channel.MaxValue);

                // Değeri güncelle;
                channel.CurrentValue = value;
                channel.LastUpdate = DateTime.UtcNow;

                // Hemen uygula;
                if (applyImmediately)
                {
                    await ApplyChannelAsync(channelId);
                }

                // Driver'ları güncelle;
                if (updateDrivers)
                {
                    await UpdateDriversForChannelAsync(channelId, value);
                }

                _logger.LogTrace($"Morph kanalı değeri güncellendi: {channelId} = {value}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph kanalı değeri ayarlama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph kanalı değeri ayarlama hatası", ex);
            }
        }

        /// <summary>
        /// Morph kanalını animasyonla değiştirir;
        /// </summary>
        public async Task AnimateChannelAsync(string channelId, float targetValue,
            float duration = 1.0f, EasingFunction easing = EasingFunction.Linear,
            bool waitForCompletion = false)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Channel ID boş olamaz.", nameof(channelId));

            if (duration <= 0)
                throw new ArgumentException("Duration pozitif olmalıdır.", nameof(duration));

            try
            {
                _logger.LogDebug($"Morph kanalı animasyonu başlatılıyor: {channelId} -> {targetValue} ({duration}s)");

                var channel = GetChannel(channelId);
                var startValue = channel.CurrentValue;
                var startTime = DateTime.UtcNow;

                // Animasyon task'ını başlat;
                var animationTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        var progress = (float)(elapsed / duration);

                        if (progress >= 1.0f)
                        {
                            // Animasyon tamamlandı;
                            await SetChannelValueAsync(channelId, targetValue);
                            break;
                        }

                        // Easing fonksiyonunu uygula;
                        var easedProgress = ApplyEasing(progress, easing);
                        var currentValue = startValue + (targetValue - startValue) * easedProgress;

                        await SetChannelValueAsync(channelId, currentValue, updateDrivers: false);

                        await Task.Delay(16); // ~60 FPS;
                    }
                });

                if (waitForCompletion)
                {
                    await animationTask;
                }

                _logger.LogDebug($"Morph kanalı animasyonu tamamlandı: {channelId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph kanalı animasyon hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph kanalı animasyon hatası", ex);
            }
        }

        /// <summary>
        /// Morph kanalını uygular;
        /// </summary>
        public async Task ApplyChannelAsync(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Channel ID boş olamaz.", nameof(channelId));

            try
            {
                MorphChannel channel;
                lock (_lockObject)
                {
                    if (!_morphChannels.TryGetValue(channelId, out channel))
                    {
                        throw new MorphEngineException($"Morph kanalı '{channelId}' bulunamadı.");
                    }
                }

                if (!channel.IsActive || channel.CurrentValue == 0)
                {
                    return;
                }

                // Channel vertex offset'lerini hesapla;
                var vertexOffsets = await CalculateChannelOffsetsAsync(channel);

                if (vertexOffsets == null || vertexOffsets.Length == 0)
                {
                    return;
                }

                // Render engine'a gönder;
                await _renderEngine.ApplyMorphOffsetsAsync(channelId, vertexOffsets, channel.BlendMode);

                // Performans metriklerini güncelle;
                _performanceMetrics.ChannelsApplied++;
                _performanceMetrics.TotalVerticesAffected += vertexOffsets.Length / 3; // Her offset 3 float (x, y, z)

                _logger.LogTrace($"Morph kanalı uygulandı: {channelId} ({vertexOffsets.Length / 3} vertex)");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph kanalı uygulama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph kanalı uygulama hatası", ex);
            }
        }

        /// <summary>
        /// Tüm aktif morph kanallarını uygular;
        /// </summary>
        public async Task ApplyAllChannelsAsync()
        {
            try
            {
                var activeChannels = GetActiveChannels();

                if (activeChannels.Count == 0)
                {
                    return;
                }

                _logger.LogDebug($"{activeChannels.Count} aktif morph kanalı uygulanıyor...");

                // Tüm kanalların kombine offset'lerini hesapla;
                var combinedOffsets = await CalculateCombinedOffsetsAsync(activeChannels);

                if (combinedOffsets == null || combinedOffsets.Length == 0)
                {
                    return;
                }

                // Render engine'a gönder;
                await _renderEngine.ApplyMorphOffsetsAsync("all_channels", combinedOffsets, BlendMode.Additive);

                // Performans metriklerini güncelle;
                _performanceMetrics.FullApplyCalls++;
                _performanceMetrics.TotalVerticesAffected += combinedOffsets.Length / 3;

                _logger.LogTrace($"Tüm morph kanalları uygulandı ({combinedOffsets.Length / 3} vertex)");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Tüm morph kanallarını uygulama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Tüm morph kanallarını uygulama hatası", ex);
            }
        }

        /// <summary>
        /// Morph kanalını sıfırlar;
        /// </summary>
        public async Task ResetChannelAsync(string channelId, bool applyImmediately = true)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Channel ID boş olamaz.", nameof(channelId));

            try
            {
                await SetChannelValueAsync(channelId, 0.0f, applyImmediately);

                _logger.LogDebug($"Morph kanalı sıfırlandı: {channelId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph kanalı sıfırlama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph kanalı sıfırlama hatası", ex);
            }
        }

        /// <summary>
        /// Tüm morph kanallarını sıfırlar;
        /// </summary>
        public async Task ResetAllChannelsAsync(bool applyImmediately = true)
        {
            try
            {
                var channels = GetAllChannels();

                foreach (var channel in channels)
                {
                    channel.CurrentValue = 0.0f;
                }

                if (applyImmediately)
                {
                    await ApplyAllChannelsAsync();
                }

                _logger.LogDebug("Tüm morph kanalları sıfırlandı.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Tüm morph kanallarını sıfırlama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Tüm morph kanallarını sıfırlama hatası", ex);
            }
        }

        #endregion;

        #region Public Methods - Morph Composition;

        /// <summary>
        /// Yeni bir morph kompozisyonu oluşturur;
        /// </summary>
        public MorphComposition CreateComposition(string compositionId, CompositionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(compositionId))
                throw new ArgumentException("Composition ID boş olamaz.", nameof(compositionId));

            options ??= new CompositionOptions();

            lock (_lockObject)
            {
                if (_activeCompositions.ContainsKey(compositionId))
                {
                    throw new MorphEngineException($"Morph kompozisyonu '{compositionId}' zaten mevcut.");
                }

                var composition = new MorphComposition(compositionId)
                {
                    Name = options.Name ?? compositionId,
                    Description = options.Description,
                    Category = options.Category,
                    BlendMode = options.BlendMode,
                    CreatedDate = DateTime.UtcNow;
                };

                _activeCompositions.Add(compositionId, composition);

                _logger.LogDebug($"Yeni morph kompozisyonu oluşturuldu: {compositionId}");

                return composition.Clone();
            }
        }

        /// <summary>
        /// Morph kompozisyonuna kanal ekler;
        /// </summary>
        public void AddChannelToComposition(string compositionId, string channelId, float weight = 1.0f)
        {
            if (string.IsNullOrWhiteSpace(compositionId))
                throw new ArgumentException("Composition ID boş olamaz.", nameof(compositionId));

            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Channel ID boş olamaz.", nameof(channelId));

            lock (_lockObject)
            {
                if (!_activeCompositions.TryGetValue(compositionId, out var composition))
                {
                    throw new MorphEngineException($"Morph kompozisyonu '{compositionId}' bulunamadı.");
                }

                if (!_morphChannels.TryGetValue(channelId, out var channel))
                {
                    throw new MorphEngineException($"Morph kanalı '{channelId}' bulunamadı.");
                }

                composition.AddChannel(channel, weight);

                _logger.LogDebug($"Morph kanalı kompozisyona eklendi: {channelId} -> {compositionId}");
            }
        }

        /// <summary>
        /// Morph kompozisyonunu uygular;
        /// </summary>
        public async Task ApplyCompositionAsync(string compositionId, float intensity = 1.0f)
        {
            if (string.IsNullOrWhiteSpace(compositionId))
                throw new ArgumentException("Composition ID boş olamaz.", nameof(compositionId));

            try
            {
                MorphComposition composition;
                lock (_lockObject)
                {
                    if (!_activeCompositions.TryGetValue(compositionId, out composition))
                    {
                        throw new MorphEngineException($"Morph kompozisyonu '{compositionId}' bulunamadı.");
                    }
                }

                _logger.LogDebug($"Morph kompozisyonu uygulanıyor: {compositionId} (Intensity: {intensity})");

                // Tüm kanalları geçici olarak değiştir ve uygula;
                var originalValues = new Dictionary<string, float>();

                foreach (var channelEntry in composition.Channels)
                {
                    var channel = channelEntry.Channel;
                    var weight = channelEntry.Weight;

                    // Orijinal değeri kaydet;
                    originalValues[channel.Id] = channel.CurrentValue;

                    // Yeni değeri hesapla ve uygula;
                    var newValue = weight * intensity;
                    await SetChannelValueAsync(channel.Id, newValue, applyImmediately: false);
                }

                // Tüm değişiklikleri uygula;
                await ApplyAllChannelsAsync();

                _logger.LogDebug($"Morph kompozisyonu uygulandı: {compositionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph kompozisyonu uygulama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph kompozisyonu uygulama hatası", ex);
            }
        }

        /// <summary>
        /// Morph kompozisyonunu preset olarak kaydeder;
        /// </summary>
        public async Task SaveCompositionAsPresetAsync(string compositionId, string presetId = null,
            PresetSaveOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(compositionId))
                throw new ArgumentException("Composition ID boş olamaz.", nameof(compositionId));

            options ??= new PresetSaveOptions();

            try
            {
                MorphComposition composition;
                lock (_lockObject)
                {
                    if (!_activeCompositions.TryGetValue(compositionId, out composition))
                    {
                        throw new MorphEngineException($"Morph kompozisyonu '{compositionId}' bulunamadı.");
                    }
                }

                presetId ??= $"preset_{compositionId}_{Guid.NewGuid():N}";

                var preset = new MorphPreset(presetId)
                {
                    Name = options.Name ?? composition.Name,
                    Description = options.Description ?? composition.Description,
                    Category = options.Category ?? composition.Category,
                    CreatedDate = DateTime.UtcNow;
                };

                // Kanal değerlerini kaydet;
                foreach (var channelEntry in composition.Channels)
                {
                    preset.ChannelValues[channelEntry.Channel.Id] = channelEntry.Weight;
                }

                // Preset'i kaydet;
                lock (_lockObject)
                {
                    _morphPresets[presetId] = preset;
                }

                // Dosyaya kaydet;
                if (options.SaveToFile)
                {
                    await SavePresetToFileAsync(preset, options.FilePath);
                }

                _logger.LogDebug($"Morph kompozisyonu preset olarak kaydedildi: {compositionId} -> {presetId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph kompozisyonu preset kaydetme hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph kompozisyonu preset kaydetme hatası", ex);
            }
        }

        #endregion;

        #region Public Methods - Morph Presets;

        /// <summary>
        /// Morph preset'ini uygular;
        /// </summary>
        public async Task ApplyPresetAsync(string presetId, float intensity = 1.0f,
            bool blendWithCurrent = false, float blendFactor = 0.5f)
        {
            if (string.IsNullOrWhiteSpace(presetId))
                throw new ArgumentException("Preset ID boş olamaz.", nameof(presetId));

            try
            {
                MorphPreset preset;
                lock (_lockObject)
                {
                    if (!_morphPresets.TryGetValue(presetId, out preset))
                    {
                        throw new MorphEngineException($"Morph preset '{presetId}' bulunamadı.");
                    }
                }

                _logger.LogDebug($"Morph preset uygulanıyor: {presetId} (Intensity: {intensity})");

                // Mevcut değerleri kaydet (blending için)
                Dictionary<string, float> currentValues = null;
                if (blendWithCurrent)
                {
                    currentValues = new Dictionary<string, float>();
                    foreach (var channel in _morphChannels.Values)
                    {
                        currentValues[channel.Id] = channel.CurrentValue;
                    }
                }

                // Preset değerlerini uygula;
                foreach (var kvp in preset.ChannelValues)
                {
                    var channelId = kvp.Key;
                    var presetValue = kvp.Value * intensity;

                    if (_morphChannels.TryGetValue(channelId, out var channel))
                    {
                        float finalValue;

                        if (blendWithCurrent && currentValues.TryGetValue(channelId, out var currentValue))
                        {
                            // Blending uygula;
                            finalValue = currentValue + (presetValue - currentValue) * blendFactor;
                        }
                        else;
                        {
                            finalValue = presetValue;
                        }

                        await SetChannelValueAsync(channelId, finalValue, applyImmediately: false);
                    }
                }

                // Tüm değişiklikleri uygula;
                await ApplyAllChannelsAsync();

                _logger.LogDebug($"Morph preset uygulandı: {presetId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph preset uygulama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph preset uygulama hatası", ex);
            }
        }

        /// <summary>
        /// Expression (yüz ifadesi) preset'ini uygular;
        /// </summary>
        public async Task ApplyExpressionAsync(ExpressionType expression, float intensity = 1.0f,
            ExpressionOptions options = null)
        {
            options ??= new ExpressionOptions();

            try
            {
                _logger.LogDebug($"Expression uygulanıyor: {expression} (Intensity: {intensity})");

                // Expression'a özgü preset ID'si;
                var presetId = $"expression_{expression}";

                if (!_morphPresets.ContainsKey(presetId))
                {
                    // Dynamic expression oluştur;
                    await CreateDynamicExpressionAsync(expression, presetId, options);
                }

                // Preset'i uygula;
                await ApplyPresetAsync(presetId, intensity, options.BlendWithCurrent, options.BlendFactor);

                // Animation uygula;
                if (options.Animate)
                {
                    await AnimateExpressionAsync(expression, intensity, options.AnimationDuration, options.Easing);
                }

                _logger.LogDebug($"Expression uygulandı: {expression}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Expression uygulama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Expression uygulama hatası", ex);
            }
        }

        /// <summary>
        /// Phoneme (ses birimi) preset'ini uygular;
        /// </summary>
        public async Task ApplyPhonemeAsync(PhonemeType phoneme, float intensity = 1.0f,
            PhonemeOptions options = null)
        {
            options ??= new PhonemeOptions();

            try
            {
                _logger.LogDebug($"Phoneme uygulanıyor: {phoneme} (Intensity: {intensity})");

                // Phoneme preset ID'si;
                var presetId = $"phoneme_{phoneme}";

                if (!_morphPresets.ContainsKey(presetId))
                {
                    // Dynamic phoneme oluştur;
                    await CreateDynamicPhonemeAsync(phoneme, presetId, options);
                }

                // Preset'i uygula;
                await ApplyPresetAsync(presetId, intensity, options.BlendWithCurrent, options.BlendFactor);

                // Lip sync için özel işlemler;
                if (options.LipSyncEnabled)
                {
                    await ApplyLipSyncCorrectionsAsync(phoneme, intensity, options);
                }

                _logger.LogDebug($"Phoneme uygulandı: {phoneme}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Phoneme uygulama hatası: {ex.Message}", ex);
                throw new MorphEngineException("Phoneme uygulama hatası", ex);
            }
        }

        #endregion;

        #region Public Methods - Advanced Morph Operations;

        /// <summary>
        /// Morph target'lar arasında interpolation yapar;
        /// </summary>
        public async Task<MorphTarget> InterpolateBetweenTargetsAsync(string targetId1, string targetId2,
            float factor, string resultId = null, InterpolationOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(targetId1))
                throw new ArgumentException("Target ID 1 boş olamaz.", nameof(targetId1));

            if (string.IsNullOrWhiteSpace(targetId2))
                throw new ArgumentException("Target ID 2 boş olamaz.", nameof(targetId2));

            options ??= new InterpolationOptions();

            try
            {
                // Target'ları yükle;
                var target1 = await GetMorphTargetAsync(targetId1);
                var target2 = await GetMorphTargetAsync(targetId2);

                // Veri uyumluluğunu kontrol et;
                if (target1.Data.BaseVertices.Length != target2.Data.BaseVertices.Length)
                {
                    throw new MorphEngineException("Morph target'ların vertex sayıları uyuşmuyor.");
                }

                // Interpolation işlemi;
                var interpolatedData = await InterpolateMorphDataAsync(target1.Data, target2.Data, factor, options);

                // Yeni target oluştur;
                resultId ??= $"interpolated_{targetId1}_{targetId2}_{factor}";

                var resultTarget = new MorphTarget(resultId, interpolatedData)
                {
                    Name = options.Name ?? $"Interpolated: {target1.Name} -> {target2.Name} ({factor:P0})",
                    Category = options.Category ?? MorphCategory.Interpolated,
                    Region = options.Region ?? MorphRegion.Any,
                    InfluenceRange = options.InfluenceRange,
                    SymmetryMode = options.SymmetryMode ?? MorphSymmetry.None,
                    CreatedDate = DateTime.UtcNow,
                    Version = 1;
                };

                // Optimizasyon;
                if (options.Optimize)
                {
                    resultTarget = await OptimizeMorphTargetAsync(resultTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Kaydet;
                lock (_lockObject)
                {
                    _morphTargets[resultId] = resultTarget;
                }

                // Cache'e ekle;
                if (options.CacheResult)
                {
                    await _morphCache.StoreMorphTargetAsync(resultTarget);
                }

                _logger.LogDebug($"Morph target'lar interpolated: {targetId1} + {targetId2} -> {resultId}");

                return resultTarget.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target interpolation hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target interpolation hatası", ex);
            }
        }

        /// <summary>
        /// Morph target'ı mirror (ayna) yapar;
        /// </summary>
        public async Task<MorphTarget> MirrorMorphTargetAsync(string sourceId, string mirroredId = null,
            MirrorAxis axis = MirrorAxis.X, MirrorOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source ID boş olamaz.", nameof(sourceId));

            options ??= new MirrorOptions();

            try
            {
                // Source target'ı yükle;
                var sourceTarget = await GetMorphTargetAsync(sourceId);

                // Mirror işlemi;
                var mirroredData = await MirrorMorphDataAsync(sourceTarget.Data, axis, options);

                // Yeni target oluştur;
                mirroredId ??= $"mirrored_{sourceId}_{axis}";

                var mirroredTarget = new MorphTarget(mirroredId, mirroredData)
                {
                    Name = options.Name ?? $"{sourceTarget.Name} (Mirrored {axis})",
                    Category = options.Category ?? sourceTarget.Category,
                    Region = options.Region ?? sourceTarget.Region,
                    InfluenceRange = options.InfluenceRange ?? sourceTarget.InfluenceRange,
                    SymmetryMode = GetMirroredSymmetry(sourceTarget.SymmetryMode),
                    CreatedDate = DateTime.UtcNow,
                    Version = 1;
                };

                // Optimizasyon;
                if (options.Optimize)
                {
                    mirroredTarget = await OptimizeMorphTargetAsync(mirroredTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Kaydet;
                lock (_lockObject)
                {
                    _morphTargets[mirroredId] = mirroredTarget;
                }

                // Cache'e ekle;
                if (options.CacheResult)
                {
                    await _morphCache.StoreMorphTargetAsync(mirroredTarget);
                }

                _logger.LogDebug($"Morph target mirrored: {sourceId} -> {mirroredId} (Axis: {axis})");

                return mirroredTarget.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target mirror hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target mirror hatası", ex);
            }
        }

        /// <summary>
        /// Morph target'ı smooth (yumuşat) yapar;
        /// </summary>
        public async Task<MorphTarget> SmoothMorphTargetAsync(string sourceId, string smoothedId = null,
            float smoothFactor = 0.5f, SmoothOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source ID boş olamaz.", nameof(sourceId));

            options ??= new SmoothOptions();

            try
            {
                // Source target'ı yükle;
                var sourceTarget = await GetMorphTargetAsync(sourceId);

                // Smooth işlemi;
                var smoothedData = await SmoothMorphDataAsync(sourceTarget.Data, smoothFactor, options);

                // Yeni target oluştur;
                smoothedId ??= $"smoothed_{sourceId}_{smoothFactor}";

                var smoothedTarget = new MorphTarget(smoothedId, smoothedData)
                {
                    Name = options.Name ?? $"{sourceTarget.Name} (Smoothed)",
                    Category = options.Category ?? sourceTarget.Category,
                    Region = options.Region ?? sourceTarget.Region,
                    InfluenceRange = options.InfluenceRange ?? sourceTarget.InfluenceRange,
                    SymmetryMode = options.SymmetryMode ?? sourceTarget.SymmetryMode,
                    CreatedDate = DateTime.UtcNow,
                    Version = 1;
                };

                // Optimizasyon;
                if (options.Optimize)
                {
                    smoothedTarget = await OptimizeMorphTargetAsync(smoothedTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Kaydet;
                lock (_lockObject)
                {
                    _morphTargets[smoothedId] = smoothedTarget;
                }

                // Cache'e ekle;
                if (options.CacheResult)
                {
                    await _morphCache.StoreMorphTargetAsync(smoothedTarget);
                }

                _logger.LogDebug($"Morph target smoothed: {sourceId} -> {smoothedId} (Factor: {smoothFactor})");

                return smoothedTarget.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target smooth hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target smooth hatası", ex);
            }
        }

        /// <summary>
        /// Morph target'ı normalize eder;
        /// </summary>
        public async Task<MorphTarget> NormalizeMorphTargetAsync(string sourceId, string normalizedId = null,
            NormalizeOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source ID boş olamaz.", nameof(sourceId));

            options ??= new NormalizeOptions();

            try
            {
                // Source target'ı yükle;
                var sourceTarget = await GetMorphTargetAsync(sourceId);

                // Normalize işlemi;
                var normalizedData = await NormalizeMorphDataAsync(sourceTarget.Data, options);

                // Yeni target oluştur;
                normalizedId ??= $"normalized_{sourceId}";

                var normalizedTarget = new MorphTarget(normalizedId, normalizedData)
                {
                    Name = options.Name ?? $"{sourceTarget.Name} (Normalized)",
                    Category = options.Category ?? sourceTarget.Category,
                    Region = options.Region ?? sourceTarget.Region,
                    InfluenceRange = options.InfluenceRange ?? sourceTarget.InfluenceRange,
                    SymmetryMode = options.SymmetryMode ?? sourceTarget.SymmetryMode,
                    CreatedDate = DateTime.UtcNow,
                    Version = 1;
                };

                // Optimizasyon;
                if (options.Optimize)
                {
                    normalizedTarget = await OptimizeMorphTargetAsync(normalizedTarget, _performanceSettings.TargetOptimizationLevel);
                }

                // Kaydet;
                lock (_lockObject)
                {
                    _morphTargets[normalizedId] = normalizedTarget;
                }

                // Cache'e ekle;
                if (options.CacheResult)
                {
                    await _morphCache.StoreMorphTargetAsync(normalizedTarget);
                }

                _logger.LogDebug($"Morph target normalized: {sourceId} -> {normalizedId}");

                return normalizedTarget.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target normalize hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target normalize hatası", ex);
            }
        }

        #endregion;

        #region Public Methods - Displacement and Normal Generation;

        /// <summary>
        /// Morph target'tan displacement map oluşturur;
        /// </summary>
        public async Task<byte[]> GenerateDisplacementMapAsync(string targetId, int textureSize = 1024,
            DisplacementOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            options ??= new DisplacementOptions();

            try
            {
                // Target'ı yükle;
                var target = await GetMorphTargetAsync(targetId);

                // Displacement map oluştur;
                var displacementData = await GenerateDisplacementDataAsync(target.Data, textureSize, options);

                _logger.LogDebug($"Displacement map oluşturuldu: {targetId} ({textureSize}x{textureSize})");

                return displacementData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Displacement map oluşturma hatası: {ex.Message}", ex);
                throw new MorphEngineException("Displacement map oluşturma hatası", ex);
            }
        }

        /// <summary>
        /// Morph target'tan normal map oluşturur;
        /// </summary>
        public async Task<byte[]> GenerateNormalMapAsync(string targetId, int textureSize = 1024,
            NormalMapOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            options ??= new NormalMapOptions();

            try
            {
                // Target'ı yükle;
                var target = await GetMorphTargetAsync(targetId);

                // Normal map oluştur;
                var normalData = await GenerateNormalDataAsync(target.Data, textureSize, options);

                _logger.LogDebug($"Normal map oluşturuldu: {targetId} ({textureSize}x{textureSize})");

                return normalData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Normal map oluşturma hatası: {ex.Message}", ex);
                throw new MorphEngineException("Normal map oluşturma hatası", ex);
            }
        }

        /// <summary>
        /// Birden fazla morph target'tan displacement data oluşturur;
        /// </summary>
        public async Task<byte[]> GenerateDisplacementDataAsync(List<MorphTarget> targets, int textureSize)
        {
            if (targets == null || targets.Count == 0)
                throw new ArgumentException("En az bir morph target gereklidir.", nameof(targets));

            try
            {
                // Tüm target'ların kombine displacement'ını hesapla;
                var combinedDisplacement = await CalculateCombinedDisplacementAsync(targets, textureSize);

                return combinedDisplacement;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Combined displacement data oluşturma hatası: {ex.Message}", ex);
                throw new MorphEngineException("Combined displacement data oluşturma hatası", ex);
            }
        }

        #endregion;

        #region Public Methods - Export and Import;

        /// <summary>
        /// Morph target'ı dışa aktarır;
        /// </summary>
        public async Task<byte[]> ExportMorphTargetAsync(string targetId, ExportFormat format = ExportFormat.NEDA)
        {
            if (string.IsNullOrWhiteSpace(targetId))
                throw new ArgumentException("Target ID boş olamaz.", nameof(targetId));

            try
            {
                var target = await GetMorphTargetAsync(targetId);

                _logger.LogDebug($"Morph target dışa aktarılıyor: {targetId} (Format: {format})");

                byte[] exportData;

                switch (format)
                {
                    case ExportFormat.NEDA:
                        exportData = ExportToNedaFormat(target);
                        break;

                    case ExportFormat.FBX:
                        exportData = await ExportToFbxFormatAsync(target);
                        break;

                    case ExportFormat.OBJ:
                        exportData = await ExportToObjFormatAsync(target);
                        break;

                    case ExportFormat.BlendShape:
                        exportData = await ExportToBlendShapeFormatAsync(target);
                        break;

                    case ExportFormat.JSON:
                        exportData = ExportToJsonFormat(target);
                        break;

                    default:
                        throw new MorphEngineException($"Desteklenmeyen export formatı: {format}");
                }

                _logger.LogDebug($"Morph target dışa aktarıldı: {targetId} ({exportData.Length} bytes)");

                return exportData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target export hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target export hatası", ex);
            }
        }

        /// <summary>
        /// Morph target'ı içe aktarır;
        /// </summary>
        public async Task<MorphTarget> ImportMorphTargetAsync(byte[] importData, string targetId = null,
            ImportFormat format = ImportFormat.AutoDetect)
        {
            if (importData == null || importData.Length == 0)
                throw new ArgumentException("Import verisi boş olamaz.", nameof(importData));

            try
            {
                _logger.LogDebug("Morph target içe aktarılıyor...");

                MorphTarget target;
                ImportFormat detectedFormat = format;

                // Format tespiti;
                if (format == ImportFormat.AutoDetect)
                {
                    detectedFormat = DetectImportFormat(importData);
                }

                // Format'a göre içe aktarım;
                switch (detectedFormat)
                {
                    case ImportFormat.NEDA:
                        target = ImportFromNedaFormat(importData);
                        break;

                    case ImportFormat.FBX:
                        target = await ImportFromFbxFormatAsync(importData);
                        break;

                    case ImportFormat.OBJ:
                        target = await ImportFromObjFormatAsync(importData);
                        break;

                    case ImportFormat.BlendShape:
                        target = await ImportFromBlendShapeFormatAsync(importData);
                        break;

                    case ImportFormat.JSON:
                        target = ImportFromJsonFormat(importData);
                        break;

                    default:
                        throw new MorphEngineException($"Desteklenmeyen import formatı: {detectedFormat}");
                }

                // Target ID ataması;
                if (!string.IsNullOrWhiteSpace(targetId))
                {
                    target.Id = targetId;
                }

                // Kaydet;
                lock (_lockObject)
                {
                    _morphTargets[target.Id] = target;
                }

                // Cache'e ekle;
                await _morphCache.StoreMorphTargetAsync(target);

                _logger.LogDebug($"Morph target içe aktarıldı: {target.Id}");

                return target.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target import hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph target import hatası", ex);
            }
        }

        /// <summary>
        /// Tüm morph kanallarını dışa aktarır;
        /// </summary>
        public async Task<byte[]> ExportAllChannelsAsync(ExportFormat format = ExportFormat.NEDA)
        {
            try
            {
                var channels = GetAllChannels();

                var exportData = new ChannelsExport;
                {
                    Channels = channels,
                    ExportDate = DateTime.UtcNow,
                    Version = "1.0"
                };

                byte[] data;

                switch (format)
                {
                    case ExportFormat.NEDA:
                    case ExportFormat.JSON:
                        data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        break;

                    default:
                        throw new MorphEngineException($"Desteklenmeyen export formatı: {format}");
                }

                _logger.LogDebug($"Tüm morph kanalları dışa aktarıldı: {channels.Count} kanal");

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph kanalları export hatası: {ex.Message}", ex);
                throw new MorphEngineException("Morph kanalları export hatası", ex);
            }
        }

        #endregion;

        #region Public Methods - Performance and Diagnostics;

        /// <summary>
        /// Morph engine performansını optimize eder;
        /// </summary>
        public async Task OptimizePerformanceAsync(OptimizationOptions options = null)
        {
            options ??= new OptimizationOptions();

            try
            {
                _logger.LogInformation("Morph engine performans optimizasyonu başlatılıyor...");

                // Cache optimizasyonu;
                if (options.OptimizeCache)
                {
                    await _morphCache.OptimizeAsync(options.CacheOptimizationLevel);
                }

                // Morph target optimizasyonu;
                if (options.OptimizeTargets)
                {
                    await OptimizeAllMorphTargetsAsync(options.TargetOptimizationLevel);
                }

                // Channel optimizasyonu;
                if (options.OptimizeChannels)
                {
                    await OptimizeAllChannelsAsync(options.ChannelOptimizationLevel);
                }

                // Memory cleanup;
                if (options.CleanupMemory)
                {
                    await CleanupMemoryAsync();
                }

                _logger.LogInformation("Morph engine performans optimizasyonu tamamlandı.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Performans optimizasyonu hatası: {ex.Message}", ex);
                throw new MorphEngineException("Performans optimizasyonu hatası", ex);
            }
        }

        /// <summary>
        /// Morph engine durum raporu oluşturur;
        /// </summary>
        public MorphEngineStatus GetStatusReport()
        {
            lock (_lockObject)
            {
                var report = new MorphEngineStatus;
                {
                    ReportTime = DateTime.UtcNow,
                    IsInitialized = _isInitialized,
                    IsProcessing = _isProcessing,
                    TotalMorphTargets = _morphTargets.Count,
                    ActiveChannels = _morphChannels.Values.Count(c => c.IsActive),
                    TotalChannels = _morphChannels.Count,
                    ActiveCompositions = _activeCompositions.Count,
                    TotalPresets = _morphPresets.Count,
                    CacheStatistics = _morphCache.Statistics,
                    PerformanceMetrics = _performanceMetrics,
                    MemoryUsage = GetMemoryUsage(),
                    AverageFrameTime = _performanceMetrics.AverageFrameTime,
                    MaxFrameTime = _performanceMetrics.MaxFrameTime;
                };

                // Region dağılımı;
                foreach (var region in Enum.GetValues(typeof(MorphRegion)).Cast<MorphRegion>())
                {
                    var count = _morphTargets.Values.Count(t => t.Region == region);
                    if (count > 0)
                    {
                        report.RegionDistribution[region] = count;
                    }
                }

                // Category dağılımı;
                foreach (var category in Enum.GetValues(typeof(MorphCategory)).Cast<MorphCategory>())
                {
                    var count = _morphTargets.Values.Count(t => t.Category == category);
                    if (count > 0)
                    {
                        report.CategoryDistribution[category] = count;
                    }
                }

                return report;
            }
        }

        /// <summary>
        /// Morph engine diagnostic test'ini çalıştırır;
        /// </summary>
        public async Task<DiagnosticResult> RunDiagnosticAsync(DiagnosticOptions options = null)
        {
            options ??= new DiagnosticOptions();

            try
            {
                _logger.LogInformation("Morph engine diagnostic test başlatılıyor...");

                var result = new DiagnosticResult;
                {
                    StartTime = DateTime.UtcNow,
                    Tests = new List<DiagnosticTestResult>()
                };

                // 1. Initialization test;
                if (options.TestInitialization)
                {
                    var testResult = await TestInitializationAsync();
                    result.Tests.Add(testResult);
                }

                // 2. Morph target operations test;
                if (options.TestMorphTargets)
                {
                    var testResult = await TestMorphTargetOperationsAsync();
                    result.Tests.Add(testResult);
                }

                // 3. Channel operations test;
                if (options.TestChannels)
                {
                    var testResult = await TestChannelOperationsAsync();
                    result.Tests.Add(testResult);
                }

                // 4. Performance test;
                if (options.TestPerformance)
                {
                    var testResult = await TestPerformanceAsync();
                    result.Tests.Add(testResult);
                }

                // 5. Memory test;
                if (options.TestMemory)
                {
                    var testResult = await TestMemoryAsync();
                    result.Tests.Add(testResult);
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.Passed = result.Tests.All(t => t.Passed);

                _logger.LogInformation($"Diagnostic test tamamlandı: {result.Passed}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Diagnostic test hatası: {ex.Message}", ex);
                throw new MorphEngineException("Diagnostic test hatası", ex);
            }
        }

        #endregion;

        #region Private Methods - Core Processing;

        /// <summary>
        /// Morph update ana döngüsü;
        /// </summary>
        private async Task MorphUpdateLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Morph update döngüsü başlatıldı.");

            var frameTimes = new List<float>();
            var lastFrameTime = DateTime.UtcNow;

            while (_isProcessing && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var frameStart = DateTime.UtcNow;

                    // Frame interval hesapla;
                    var frameInterval = (float)(frameStart - lastFrameTime).TotalSeconds;
                    lastFrameTime = frameStart;

                    // Aktif kanalları güncelle;
                    await UpdateActiveChannelsAsync();

                    // Driver'ları güncelle;
                    await UpdateAllDriversAsync();

                    // Tüm değişiklikleri uygula;
                    await ApplyAllChannelsAsync();

                    // Frame time hesapla;
                    var frameTime = (float)(DateTime.UtcNow - frameStart).TotalSeconds;
                    frameTimes.Add(frameTime);

                    // Performans metriklerini güncelle;
                    UpdatePerformanceMetrics(frameTime, frameTimes);

                    // Bekleme süresi (target FPS)
                    var targetFrameTime = 1.0f / _configuration.TargetFPS;
                    var elapsed = (float)(DateTime.UtcNow - frameStart).TotalSeconds;

                    if (elapsed < targetFrameTime)
                    {
                        var waitTime = (int)((targetFrameTime - elapsed) * 1000);
                        await Task.Delay(Math.Max(1, waitTime), cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Morph update döngü hatası: {ex.Message}", ex);
                    await Task.Delay(100, cancellationToken); // Hata durumunda kısa bekleme;
                }
            }

            _logger.LogInformation("Morph update döngüsü sonlandırıldı.");
        }

        /// <summary>
        /// Aktif kanalları günceller;
        /// </summary>
        private async Task UpdateActiveChannelsAsync()
        {
            var activeChannels = GetActiveChannels();

            if (activeChannels.Count == 0)
                return;

            foreach (var channel in activeChannels)
            {
                // Channel değerini güncelle (driver'lardan)
                await UpdateChannelFromDriversAsync(channel);
            }
        }

        /// <summary>
        /// Kanal offset'lerini hesaplar;
        /// </summary>
        private async Task<float[]> CalculateChannelOffsetsAsync(MorphChannel channel)
        {
            if (channel == null || channel.Targets.Count == 0)
                return null;

            try
            {
                // Cache kontrolü;
                var cacheKey = $"channel_offsets_{channel.Id}_{channel.CurrentValue}";
                var cachedOffsets = await _morphCache.GetChannelOffsetsAsync(cacheKey);

                if (cachedOffsets != null)
                {
                    return cachedOffsets;
                }

                // Base vertex sayısı (ilk target'tan al)
                var firstTarget = await GetMorphTargetAsync(channel.Targets[0].Target.Id);
                var vertexCount = firstTarget.Data.BaseVertices.Length / 3;
                var offsets = new float[vertexCount * 3];

                // Tüm target'ların katkısını hesapla;
                foreach (var channelTarget in channel.Targets)
                {
                    var target = await GetMorphTargetAsync(channelTarget.Target.Id);
                    var targetOffsets = target.Data.TargetVertices;
                    var weight = channelTarget.Weight * channel.CurrentValue;

                    // Target offset'lerini ekle;
                    for (int i = 0; i < targetOffsets.Length; i++)
                    {
                        offsets[i] += targetOffsets[i] * weight;
                    }
                }

                // Blend mode'a göre işlem;
                offsets = ApplyBlendMode(offsets, channel.BlendMode, channel.CurrentValue);

                // Cache'e kaydet;
                await _morphCache.StoreChannelOffsetsAsync(cacheKey, offsets);

                return offsets;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Channel offset hesaplama hatası: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Kombine offset'leri hesaplar;
        /// </summary>
        private async Task<float[]> CalculateCombinedOffsetsAsync(List<MorphChannel> channels)
        {
            if (channels == null || channels.Count == 0)
                return null;

            try
            {
                // Cache kontrolü;
                var cacheKey = "combined_offsets_" + string.Join("_", channels.Select(c => $"{c.Id}_{c.CurrentValue}"));
                var cachedOffsets = await _morphCache.GetChannelOffsetsAsync(cacheKey);

                if (cachedOffsets != null)
                {
                    return cachedOffsets;
                }

                // Base vertex sayısı;
                var firstChannel = channels[0];
                if (firstChannel.Targets.Count == 0)
                    return null;

                var firstTarget = await GetMorphTargetAsync(firstChannel.Targets[0].Target.Id);
                var vertexCount = firstTarget.Data.BaseVertices.Length / 3;
                var combinedOffsets = new float[vertexCount * 3];

                // Tüm kanalların offset'lerini birleştir;
                foreach (var channel in channels)
                {
                    var channelOffsets = await CalculateChannelOffsetsAsync(channel);

                    if (channelOffsets != null)
                    {
                        for (int i = 0; i < channelOffsets.Length; i++)
                        {
                            combinedOffsets[i] += channelOffsets[i];
                        }
                    }
                }

                // Clamp values;
                for (int i = 0; i < combinedOffsets.Length; i++)
                {
                    combinedOffsets[i] = Math.Clamp(combinedOffsets[i], -1.0f, 1.0f);
                }

                // Cache'e kaydet;
                await _morphCache.StoreChannelOffsetsAsync(cacheKey, combinedOffsets);

                return combinedOffsets;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Combined offset hesaplama hatası: {ex.Message}", ex);
                return null;
            }
        }

        #endregion;

        #region Private Methods - Morph Target Operations;

        /// <summary>
        /// Morph verisini doğrular;
        /// </summary>
        private void ValidateMorphData(MorphTargetData data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.BaseVertices == null || data.BaseVertices.Length == 0)
                throw new MorphEngineException("Base vertices boş olamaz.");

            if (data.TargetVertices == null || data.TargetVertices.Length == 0)
                throw new MorphEngineException("Target vertices boş olamaz.");

            if (data.BaseVertices.Length != data.TargetVertices.Length)
                throw new MorphEngineException("Base ve target vertex sayıları eşit olmalıdır.");

            // Vertex sayısı 3'ün katı olmalı (x, y, z)
            if (data.BaseVertices.Length % 3 != 0)
                throw new MorphEngineException("Vertex verisi 3'ün katı olmalıdır (x, y, z).");

            // Normals varsa doğrula;
            if (data.Normals != null && data.Normals.Length != data.BaseVertices.Length)
                throw new MorphEngineException("Normal sayısı vertex sayısına eşit olmalıdır.");
        }

        /// <summary>
        /// Morph target'ı optimize eder;
        /// </summary>
        private async Task<MorphTarget> OptimizeMorphTargetAsync(MorphTarget target, OptimizationLevel level)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (level == OptimizationLevel.None)
                return target;

            try
            {
                var optimizedData = target.Data.Clone();

                // Optimization level'a göre işlemler;
                switch (level)
                {
                    case OptimizationLevel.Low:
                        // Sadece küçük vertex'leri temizle;
                        optimizedData = RemoveSmallOffsets(optimizedData, 0.001f);
                        break;

                    case OptimizationLevel.Medium:
                        // Orta seviye optimizasyon;
                        optimizedData = RemoveSmallOffsets(optimizedData, 0.005f);
                        optimizedData = SimplifyGeometry(optimizedData, 0.1f);
                        break;

                    case OptimizationLevel.High:
                        // Yüksek seviye optimizasyon;
                        optimizedData = RemoveSmallOffsets(optimizedData, 0.01f);
                        optimizedData = SimplifyGeometry(optimizedData, 0.2f);
                        optimizedData = CompressVertices(optimizedData);
                        break;

                    case OptimizationLevel.Ultra:
                        // Ultra optimizasyon (real-time için)
                        optimizedData = RemoveSmallOffsets(optimizedData, 0.02f);
                        optimizedData = SimplifyGeometry(optimizedData, 0.3f);
                        optimizedData = CompressVertices(optimizedData);
                        optimizedData = QuantizeVertices(optimizedData, 16); // 16-bit quantization;
                        break;
                }

                var optimizedTarget = target.Clone();
                optimizedTarget.Data = optimizedData;
                optimizedTarget.OptimizationLevel = level;
                optimizedTarget.LastOptimized = DateTime.UtcNow;

                return optimizedTarget;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target optimizasyon hatası: {ex.Message}", ex);
                return target; // Optimizasyon başarısız olursa orijinal target'ı döndür;
            }
        }

        /// <summary>
        /// Symmetry uygular;
        /// </summary>
        private async Task<MorphTarget> ApplySymmetryAsync(MorphTarget target, MorphSymmetry symmetry)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (symmetry == MorphSymmetry.None)
                return target;

            try
            {
                var symmetricData = target.Data.Clone();

                switch (symmetry)
                {
                    case MorphSymmetry.LeftToRight:
                        symmetricData = MirrorVertices(symmetricData, MirrorAxis.X, true);
                        break;

                    case MorphSymmetry.RightToLeft:
                        symmetricData = MirrorVertices(symmetricData, MirrorAxis.X, false);
                        break;

                    case MorphSymmetry.Both:
                        // Hem left hem right uygula ve ortalamasını al;
                        var leftData = MirrorVertices(symmetricData, MirrorAxis.X, true);
                        var rightData = MirrorVertices(symmetricData, MirrorAxis.X, false);
                        symmetricData = AverageMorphData(leftData, rightData);
                        break;

                    case MorphSymmetry.Radial:
                        symmetricData = ApplyRadialSymmetry(symmetricData);
                        break;
                }

                var symmetricTarget = target.Clone();
                symmetricTarget.Data = symmetricData;
                symmetricTarget.SymmetryMode = symmetry

                return symmetricTarget;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Symmetry uygulama hatası: {ex.Message}", ex);
                return target;
            }
        }

        /// <summary>
        /// Morph verilerini birleştirir;
        /// </summary>
        private async Task<MorphTargetData> CombineMorphDataAsync(List<MorphTarget> targets, CombineOptions options)
        {
            if (targets == null || targets.Count == 0)
                throw new ArgumentException("En az bir morph target gereklidir.", nameof(targets));

            // İlk target'ı base olarak al;
            var baseData = targets[0].Data.Clone();
            var combinedVertices = new float[baseData.TargetVertices.Length];

            // Tüm target'ları birleştir;
            foreach (var target in targets)
            {
                var targetData = target.Data;

                // Vertex sayıları eşit mi kontrol et;
                if (targetData.TargetVertices.Length != baseData.TargetVertices.Length)
                {
                    throw new MorphEngineException($"Target vertex sayıları uyuşmuyor: {targets[0].Id} vs {target.Id}");
                }

                // Blend mode'a göre birleştir;
                for (int i = 0; i < targetData.TargetVertices.Length; i++)
                {
                    switch (options.BlendMode)
                    {
                        case BlendMode.Additive:
                            combinedVertices[i] += targetData.TargetVertices[i];
                            break;

                        case BlendMode.Average:
                            combinedVertices[i] += targetData.TargetVertices[i] / targets.Count;
                            break;

                        case BlendMode.Max:
                            combinedVertices[i] = Math.Max(combinedVertices[i], targetData.TargetVertices[i]);
                            break;

                        case BlendMode.Min:
                            combinedVertices[i] = Math.Min(combinedVertices[i], targetData.TargetVertices[i]);
                            break;
                    }
                }
            }

            // Clamp values;
            if (options.ClampValues)
            {
                for (int i = 0; i < combinedVertices.Length; i++)
                {
                    combinedVertices[i] = Math.Clamp(combinedVertices[i], -options.MaxValue, options.MaxValue);
                }
            }

            baseData.TargetVertices = combinedVertices;
            return baseData;
        }

        /// <summary>
        /// Modifikasyonları uygular;
        /// </summary>
        private async Task<MorphTarget> ApplyModificationsAsync(MorphTarget target, List<MorphModification> modifications)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (modifications == null || modifications.Count == 0)
                return target;

            var modifiedData = target.Data.Clone();

            foreach (var modification in modifications)
            {
                switch (modification.Type)
                {
                    case ModificationType.Scale:
                        modifiedData = ScaleVertices(modifiedData, modification.Value);
                        break;

                    case ModificationType.Translate:
                        modifiedData = TranslateVertices(modifiedData,
                            new Vector3(modification.Value, modification.SecondaryValue, 0));
                        break;

                    case ModificationType.Rotate:
                        modifiedData = RotateVertices(modifiedData, modification.Value);
                        break;

                    case ModificationType.Noise:
                        modifiedData = AddNoise(modifiedData, modification.Value);
                        break;

                    case ModificationType.Smooth:
                        modifiedData = SmoothVertices(modifiedData, (int)modification.Value);
                        break;
                }
            }

            var modifiedTarget = target.Clone();
            modifiedTarget.Data = modifiedData;

            return modifiedTarget;
        }

        #endregion;

        #region Private Methods - Morph Data Operations;

        /// <summary>
        /// Morph verileri arasında interpolation yapar;
        /// </summary>
        private async Task<MorphTargetData> InterpolateMorphDataAsync(MorphTargetData data1,
            MorphTargetData data2, float factor, InterpolationOptions options)
        {
            if (data1.BaseVertices.Length != data2.BaseVertices.Length)
                throw new MorphEngineException("Vertex sayıları uyuşmuyor.");

            var interpolatedData = data1.Clone();

            // Vertex interpolation;
            for (int i = 0; i < data1.TargetVertices.Length; i++)
            {
                switch (options.InterpolationMethod)
                {
                    case InterpolationMethod.Linear:
                        interpolatedData.TargetVertices[i] =
                            data1.TargetVertices[i] * (1 - factor) + data2.TargetVertices[i] * factor;
                        break;

                    case InterpolationMethod.Cubic:
                        interpolatedData.TargetVertices[i] =
                            CubicInterpolate(data1.TargetVertices[i], data2.TargetVertices[i], factor);
                        break;

                    case InterpolationMethod.Hermite:
                        interpolatedData.TargetVertices[i] =
                            HermiteInterpolate(data1.TargetVertices[i], data2.TargetVertices[i], factor);
                        break;
                }
            }

            // Normal interpolation (varsa)
            if (data1.Normals != null && data2.Normals != null)
            {
                for (int i = 0; i < data1.Normals.Length; i += 3)
                {
                    var normal1 = new Vector3(data1.Normals[i], data1.Normals[i + 1], data1.Normals[i + 2]);
                    var normal2 = new Vector3(data2.Normals[i], data2.Normals[i + 1], data2.Normals[i + 2]);

                    var interpolatedNormal = Vector3.Lerp(normal1, normal2, factor);
                    interpolatedNormal = Vector3.Normalize(interpolatedNormal);

                    interpolatedData.Normals[i] = interpolatedNormal.X;
                    interpolatedData.Normals[i + 1] = interpolatedNormal.Y;
                    interpolatedData.Normals[i + 2] = interpolatedNormal.Z;
                }
            }

            return interpolatedData;
        }

        /// <summary>
        /// Morph verisini mirror yapar;
        /// </summary>
        private async Task<MorphTargetData> MirrorMorphDataAsync(MorphTargetData data,
            MirrorAxis axis, MirrorOptions options)
        {
            var mirroredData = data.Clone();

            for (int i = 0; i < data.TargetVertices.Length; i += 3)
            {
                var x = data.TargetVertices[i];
                var y = data.TargetVertices[i + 1];
                var z = data.TargetVertices[i + 2];

                switch (axis)
                {
                    case MirrorAxis.X:
                        mirroredData.TargetVertices[i] = options.Invert ? -x : x;
                        mirroredData.TargetVertices[i + 1] = y;
                        mirroredData.TargetVertices[i + 2] = z;
                        break;

                    case MirrorAxis.Y:
                        mirroredData.TargetVertices[i] = x;
                        mirroredData.TargetVertices[i + 1] = options.Invert ? -y : y;
                        mirroredData.TargetVertices[i + 2] = z;
                        break;

                    case MirrorAxis.Z:
                        mirroredData.TargetVertices[i] = x;
                        mirroredData.TargetVertices[i + 1] = y;
                        mirroredData.TargetVertices[i + 2] = options.Invert ? -z : z;
                        break;
                }
            }

            // Normalleri de mirror et;
            if (mirroredData.Normals != null)
            {
                for (int i = 0; i < mirroredData.Normals.Length; i += 3)
                {
                    switch (axis)
                    {
                        case MirrorAxis.X:
                            mirroredData.Normals[i] = -mirroredData.Normals[i];
                            break;
                        case MirrorAxis.Y:
                            mirroredData.Normals[i + 1] = -mirroredData.Normals[i + 1];
                            break;
                        case MirrorAxis.Z:
                            mirroredData.Normals[i + 2] = -mirroredData.Normals[i + 2];
                            break;
                    }
                }
            }

            return mirroredData;
        }

        /// <summary>
        /// Morph verisini smooth yapar;
        /// </summary>
        private async Task<MorphTargetData> SmoothMorphDataAsync(MorphTargetData data,
            float smoothFactor, SmoothOptions options)
        {
            var smoothedData = data.Clone();

            // Gaussian blur benzeri smoothing;
            var kernelSize = options.KernelSize;
            var kernel = CreateGaussianKernel(kernelSize, smoothFactor);

            for (int i = 0; i < data.TargetVertices.Length; i += 3)
            {
                var sumX = 0.0f;
                var sumY = 0.0f;
                var sumZ = 0.0f;
                var weightSum = 0.0f;

                for (int k = -kernelSize; k <= kernelSize; k++)
                {
                    var idx = i + k * 3;
                    if (idx >= 0 && idx < data.TargetVertices.Length - 2)
                    {
                        var weight = kernel[k + kernelSize];
                        sumX += data.TargetVertices[idx] * weight;
                        sumY += data.TargetVertices[idx + 1] * weight;
                        sumZ += data.TargetVertices[idx + 2] * weight;
                        weightSum += weight;
                    }
                }

                if (weightSum > 0)
                {
                    smoothedData.TargetVertices[i] = sumX / weightSum;
                    smoothedData.TargetVertices[i + 1] = sumY / weightSum;
                    smoothedData.TargetVertices[i + 2] = sumZ / weightSum;
                }
            }

            return smoothedData;
        }

        /// <summary>
        /// Morph verisini normalize eder;
        /// </summary>
        private async Task<MorphTargetData> NormalizeMorphDataAsync(MorphTargetData data,
            NormalizeOptions options)
        {
            var normalizedData = data.Clone();

            // Min-max değerlerini bul;
            var min = float.MaxValue;
            var max = float.MinValue;

            foreach (var value in data.TargetVertices)
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }

            // Normalize et;
            var range = max - min;
            if (range > 0)
            {
                for (int i = 0; i < data.TargetVertices.Length; i++)
                {
                    normalizedData.TargetVertices[i] = (data.TargetVertices[i] - min) / range;

                    // Target range'a scale et;
                    normalizedData.TargetVertices[i] =
                        normalizedData.TargetVertices[i] * (options.TargetMax - options.TargetMin) + options.TargetMin;
                }
            }

            return normalizedData;
        }

        #endregion;

        #region Private Methods - Helper Methods;

        /// <summary>
        /// Küçük offset'leri kaldırır;
        /// </summary>
        private MorphTargetData RemoveSmallOffsets(MorphTargetData data, float threshold)
        {
            var cleanedData = data.Clone();

            for (int i = 0; i < data.TargetVertices.Length; i++)
            {
                if (Math.Abs(data.TargetVertices[i]) < threshold)
                {
                    cleanedData.TargetVertices[i] = 0;
                }
            }

            return cleanedData;
        }

        /// <summary>
        /// Geometriyi sadeleştirir;
        /// </summary>
        private MorphTargetData SimplifyGeometry(MorphTargetData data, float factor)
        {
            // Vertex clustering basit implementasyonu;
            var simplifiedData = data.Clone();
            var clusterSize = (int)(data.TargetVertices.Length * factor / 100);

            if (clusterSize < 2)
                return data;

            for (int i = 0; i < data.TargetVertices.Length; i += clusterSize * 3)
            {
                var clusterAvg = new Vector3(0, 0, 0);
                var clusterCount = 0;

                for (int j = 0; j < clusterSize * 3 && i + j < data.TargetVertices.Length; j += 3)
                {
                    clusterAvg.X += data.TargetVertices[i + j];
                    clusterAvg.Y += data.TargetVertices[i + j + 1];
                    clusterAvg.Z += data.TargetVertices[i + j + 2];
                    clusterCount++;
                }

                if (clusterCount > 0)
                {
                    clusterAvg /= clusterCount;

                    for (int j = 0; j < clusterSize * 3 && i + j < data.TargetVertices.Length; j += 3)
                    {
                        simplifiedData.TargetVertices[i + j] = clusterAvg.X;
                        simplifiedData.TargetVertices[i + j + 1] = clusterAvg.Y;
                        simplifiedData.TargetVertices[i + j + 2] = clusterAvg.Z;
                    }
                }
            }

            return simplifiedData;
        }

        /// <summary>
        /// Vertex'leri sıkıştırır;
        /// </summary>
        private MorphTargetData CompressVertices(MorphTargetData data)
        {
            // Delta encoding basit implementasyonu;
            var compressedData = data.Clone();
            var compressedVertices = new List<float>();

            float lastX = 0, lastY = 0, lastZ = 0;

            for (int i = 0; i < data.TargetVertices.Length; i += 3)
            {
                var deltaX = data.TargetVertices[i] - lastX;
                var deltaY = data.TargetVertices[i + 1] - lastY;
                var deltaZ = data.TargetVertices[i + 2] - lastZ;

                // Sadece önemli delta'ları sakla;
                if (Math.Abs(deltaX) > 0.001f || Math.Abs(deltaY) > 0.001f || Math.Abs(deltaZ) > 0.001f)
                {
                    compressedVertices.Add(deltaX);
                    compressedVertices.Add(deltaY);
                    compressedVertices.Add(deltaZ);

                    lastX = data.TargetVertices[i];
                    lastY = data.TargetVertices[i + 1];
                    lastZ = data.TargetVertices[i + 2];
                }
                else;
                {
                    compressedVertices.Add(0);
                    compressedVertices.Add(0);
                    compressedVertices.Add(0);
                }
            }

            compressedData.TargetVertices = compressedVertices.ToArray();
            return compressedData;
        }

        /// <summary>
        /// Vertex'leri quantize eder;
        /// </summary>
        private MorphTargetData QuantizeVertices(MorphTargetData data, int bits)
        {
            var quantizedData = data.Clone();
            var maxValue = 1 << (bits - 1); // Signed için;

            for (int i = 0; i < data.TargetVertices.Length; i++)
            {
                // [-1, 1] aralığını [0, maxValue] aralığına map et;
                var normalized = (data.TargetVertices[i] + 1) / 2; // [0, 1]
                var quantized = (int)(normalized * maxValue);
                var dequantized = (quantized / (float)maxValue) * 2 - 1; // [-1, 1]'e geri dön;

                quantizedData.TargetVertices[i] = dequantized;
            }

            return quantizedData;
        }

        /// <summary>
        /// Vertex'leri mirror eder;
        /// </summary>
        private MorphTargetData MirrorVertices(MorphTargetData data, MirrorAxis axis, bool leftToRight)
        {
            var mirroredData = data.Clone();

            for (int i = 0; i < data.BaseVertices.Length; i += 3)
            {
                var baseX = data.BaseVertices[i];

                // X pozisyonuna göre left/right belirle;
                if ((leftToRight && baseX < 0) || (!leftToRight && baseX > 0))
                {
                    // Bu vertex'i mirror et;
                    var targetIdx = FindMirrorVertex(data.BaseVertices, i, axis, leftToRight);

                    if (targetIdx >= 0)
                    {
                        // Target vertex'e offset'leri kopyala;
                        mirroredData.TargetVertices[targetIdx] = data.TargetVertices[i];
                        mirroredData.TargetVertices[targetIdx + 1] = data.TargetVertices[i + 1];
                        mirroredData.TargetVertices[targetIdx + 2] = data.TargetVertices[i + 2];
                    }
                }
            }

            return mirroredData;
        }

        /// <summary>
        /// Mirror vertex indeksini bulur;
        /// </summary>
        private int FindMirrorVertex(float[] baseVertices, int vertexIndex, MirrorAxis axis, bool leftToRight)
        {
            var x = baseVertices[vertexIndex];
            var y = baseVertices[vertexIndex + 1];
            var z = baseVertices[vertexIndex + 2];

            var mirrorX = leftToRight ? -x : x;

            // En yakın vertex'i bul;
            var minDistance = float.MaxValue;
            var minIndex = -1;

            for (int i = 0; i < baseVertices.Length; i += 3)
            {
                if (i == vertexIndex) continue;

                var dx = baseVertices[i] - mirrorX;
                var dy = baseVertices[i + 1] - y;
                var dz = baseVertices[i + 2] - z;

                var distance = dx * dx + dy * dy + dz * dz;

                if (distance < minDistance)
                {
                    minDistance = distance;
                    minIndex = i;
                }
            }

            return minDistance < 0.001f ? minIndex : -1; // Threshold;
        }

        /// <summary>
        /// Radial symmetry uygular;
        /// </summary>
        private MorphTargetData ApplyRadialSymmetry(MorphTargetData data)
        {
            var symmetricData = data.Clone();

            // Merkez noktasını bul (ortalama)
            var center = new Vector3(0, 0, 0);
            for (int i = 0; i < data.BaseVertices.Length; i += 3)
            {
                center.X += data.BaseVertices[i];
                center.Y += data.BaseVertices[i + 1];
                center.Z += data.BaseVertices[i + 2];
            }
            center /= data.BaseVertices.Length / 3;

            // Her vertex için radial symmetry uygula;
            for (int i = 0; i < data.BaseVertices.Length; i += 3)
            {
                var pos = new Vector3(
                    data.BaseVertices[i],
                    data.BaseVertices[i + 1],
                    data.BaseVertices[i + 2]
                );

                // Merkeze göre vektör;
                var toCenter = center - pos;
                var distance = toCenter.Length();

                if (distance > 0)
                {
                    // Radial olarak offset'i ayarla;
                    var radialFactor = 1.0f - (distance / 10.0f); // 10 birim max distance varsayımı;
                    radialFactor = Math.Clamp(radialFactor, 0, 1);

                    symmetricData.TargetVertices[i] *= radialFactor;
                    symmetricData.TargetVertices[i + 1] *= radialFactor;
                    symmetricData.TargetVertices[i + 2] *= radialFactor;
                }
            }

            return symmetricData;
        }

        /// <summary>
        /// Morph verilerini ortalar;
        /// </summary>
        private MorphTargetData AverageMorphData(MorphTargetData data1, MorphTargetData data2)
        {
            var averagedData = data1.Clone();

            for (int i = 0; i < data1.TargetVertices.Length; i++)
            {
                averagedData.TargetVertices[i] = (data1.TargetVertices[i] + data2.TargetVertices[i]) / 2;
            }

            return averagedData;
        }

        /// <summary>
        /// Vertex'leri scale eder;
        /// </summary>
        private MorphTargetData ScaleVertices(MorphTargetData data, float scale)
        {
            var scaledData = data.Clone();

            for (int i = 0; i < data.TargetVertices.Length; i++)
            {
                scaledData.TargetVertices[i] *= scale;
            }

            return scaledData;
        }

        /// <summary>
        /// Vertex'leri translate eder;
        /// </summary>
        private MorphTargetData TranslateVertices(MorphTargetData data, Vector3 translation)
        {
            var translatedData = data.Clone();

            for (int i = 0; i < data.TargetVertices.Length; i += 3)
            {
                translatedData.TargetVertices[i] += translation.X;
                translatedData.TargetVertices[i + 1] += translation.Y;
                translatedData.TargetVertices[i + 2] += translation.Z;
            }

            return translatedData;
        }

        /// <summary>
        /// Vertex'leri rotate eder;
        /// </summary>
        private MorphTargetData RotateVertices(MorphTargetData data, float angle)
        {
            var rotatedData = data.Clone();
            var rad = angle * (float)Math.PI / 180.0f;
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);

            for (int i = 0; i < data.TargetVertices.Length; i += 3)
            {
                var x = data.TargetVertices[i];
                var y = data.TargetVertices[i + 1];

                // Z ekseni etrafında rotate (2D rotate)
                rotatedData.TargetVertices[i] = x * cos - y * sin;
                rotatedData.TargetVertices[i + 1] = x * sin + y * cos;
            }

            return rotatedData;
        }

        /// <summary>
        /// Noise ekler;
        /// </summary>
        private MorphTargetData AddNoise(MorphTargetData data, float intensity)
        {
            var noisyData = data.Clone();
            var random = new Random();

            for (int i = 0; i < data.TargetVertices.Length; i++)
            {
                var noise = (float)(random.NextDouble() * 2 - 1) * intensity;
                noisyData.TargetVertices[i] += noise;
            }

            return noisyData;
        }

        /// <summary>
        /// Vertex'leri smooth eder;
        /// </summary>
        private MorphTargetData SmoothVertices(MorphTargetData data, int iterations)
        {
            var smoothedData = data.Clone();

            for (int iter = 0; iter < iterations; iter++)
            {
                var temp = smoothedData.TargetVertices.ToArray();

                for (int i = 3; i < smoothedData.TargetVertices.Length - 3; i += 3)
                {
                    // Komşu vertex'lerin ortalamasını al;
                    for (int j = 0; j < 3; j++)
                    {
                        var prev = temp[i - 3 + j];
                        var curr = temp[i + j];
                        var next = temp[i + 3 + j];

                        smoothedData.TargetVertices[i + j] = (prev + curr + next) / 3;
                    }
                }
            }

            return smoothedData;
        }

        /// <summary>
        /// Gaussian kernel oluşturur;
        /// </summary>
        private float[] CreateGaussianKernel(int size, float sigma)
        {
            var kernel = new float[size * 2 + 1];
            var sum = 0.0f;

            for (int i = -size; i <= size; i++)
            {
                var x = i;
                var value = (float)Math.Exp(-(x * x) / (2 * sigma * sigma)) / (float)Math.Sqrt(2 * Math.PI * sigma * sigma);
                kernel[i + size] = value;
                sum += value;
            }

            // Normalize et;
            for (int i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= sum;
            }

            return kernel;
        }

        /// <summary>
        /// Cubic interpolation;
        /// </summary>
        private float CubicInterpolate(float y0, float y1, float mu)
        {
            var mu2 = mu * mu;
            var mu3 = mu2 * mu;
            var a0 = y1 - y0;
            var a1 = a0;
            return a0 * mu + a1 * mu2 + y0;
        }

        /// <summary>
        /// Hermite interpolation;
        /// </summary>
        private float HermiteInterpolate(float y0, float y1, float mu, float tension = 0, float bias = 0)
        {
            var mu2 = mu * mu;
            var mu3 = mu2 * mu;
            var m0 = (y1 - y0) * (1 + bias) * (1 - tension) / 2;
            var m1 = (y1 - y0) * (1 - bias) * (1 - tension) / 2;
            var a0 = 2 * mu3 - 3 * mu2 + 1;
            var a1 = mu3 - 2 * mu2 + mu;
            var a2 = mu3 - mu2;
            var a3 = -2 * mu3 + 3 * mu2;
            return a0 * y0 + a1 * m0 + a2 * m1 + a3 * y1;
        }

        /// <summary>
        /// Blend mode uygular;
        /// </summary>
        private float[] ApplyBlendMode(float[] offsets, BlendMode blendMode, float intensity)
        {
            var result = offsets.ToArray();

            switch (blendMode)
            {
                case BlendMode.Additive:
                    // Zaten additive;
                    break;

                case BlendMode.Overlay:
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        if (offsets[i] < 0.5f)
                            result[i] = 2 * offsets[i] * intensity;
                        else;
                            result[i] = 1 - 2 * (1 - offsets[i]) * (1 - intensity);
                    }
                    break;

                case BlendMode.Multiply:
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        result[i] = offsets[i] * intensity;
                    }
                    break;

                case BlendMode.Screen:
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        result[i] = 1 - (1 - offsets[i]) * (1 - intensity);
                    }
                    break;
            }

            return result;
        }

        /// <summary>
        /// Easing fonksiyonu uygular;
        /// </summary>
        private float ApplyEasing(float t, EasingFunction easing)
        {
            switch (easing)
            {
                case EasingFunction.Linear:
                    return t;

                case EasingFunction.EaseInQuad:
                    return t * t;

                case EasingFunction.EaseOutQuad:
                    return t * (2 - t);

                case EasingFunction.EaseInOutQuad:
                    return t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;

                case EasingFunction.EaseInCubic:
                    return t * t * t;

                case EasingFunction.EaseOutCubic:
                    return (--t) * t * t + 1;

                case EasingFunction.EaseInOutCubic:
                    return t < 0.5 ? 4 * t * t * t : (t - 1) * (2 * t - 2) * (2 * t - 2) + 1;

                default:
                    return t;
            }
        }

        /// <summary>
        /// Mirror symmetry'yi hesaplar;
        /// </summary>
        private MorphSymmetry GetMirroredSymmetry(MorphSymmetry original)
        {
            return original switch;
            {
                MorphSymmetry.LeftToRight => MorphSymmetry.RightToLeft,
                MorphSymmetry.RightToLeft => MorphSymmetry.LeftToRight,
                _ => original;
            };
        }

        #endregion;

        #region Private Methods - Channel Operations;

        /// <summary>
        /// Kanal ağırlıklarını normalize eder;
        /// </summary>
        private async Task NormalizeChannelWeightsAsync(string channelId)
        {
            MorphChannel channel;
            lock (_lockObject)
            {
                if (!_morphChannels.TryGetValue(channelId, out channel))
                {
                    return;
                }
            }

            if (channel.Targets.Count == 0)
                return;

            // Toplam ağırlığı hesapla;
            var totalWeight = channel.Targets.Sum(t => t.Weight);

            if (totalWeight <= 0)
                return;

            // Normalize et;
            var factor = 1.0f / totalWeight;

            lock (_lockObject)
            {
                foreach (var target in channel.Targets)
                {
                    target.Weight *= factor;
                }
            }
        }

        /// <summary>
        /// Target'ı kullanan kanalları günceller;
        /// </summary>
        private async Task UpdateChannelsUsingTargetAsync(string targetId)
        {
            var affectedChannels = new List<string>();

            lock (_lockObject)
            {
                foreach (var channel in _morphChannels.Values)
                {
                    if (channel.Targets.Any(t => t.Target.Id == targetId))
                    {
                        affectedChannels.Add(channel.Id);
                    }
                }
            }

            // Cache'i temizle;
            foreach (var channelId in affectedChannels)
            {
                await _morphCache.ClearChannelCacheAsync(channelId);
            }
        }

        /// <summary>
        /// Target'ı kanallardan kaldırır;
        /// </summary>
        private async Task RemoveTargetFromChannelsAsync(string targetId)
        {
            lock (_lockObject)
            {
                foreach (var channel in _morphChannels.Values)
                {
                    channel.RemoveTarget(targetId);
                }
            }

            // Cache'i temizle;
            await _morphCache.ClearTargetCacheAsync(targetId);
        }

        /// <summary>
        /// Kanalı driver'lardan günceller;
        /// </summary>
        private async Task UpdateChannelFromDriversAsync(MorphChannel channel)
        {
            if (channel.Drivers.Count == 0)
                return;

            var newValue = channel.CurrentValue;

            foreach (var driverId in channel.Drivers)
            {
                if (_morphDrivers.TryGetValue(driverId, out var driver))
                {
                    var driverValue = await driver.GetValueAsync();
                    newValue = driver.BlendFunction(newValue, driverValue, driver.Weight);
                }
            }

            // Değeri güncelle (apply immediately false, update drivers false)
            await SetChannelValueAsync(channel.Id, newValue, false, false);
        }

        /// <summary>
        /// Kanal için driver'ları günceller;
        /// </summary>
        private async Task UpdateDriversForChannelAsync(string channelId, float channelValue)
        {
            MorphChannel channel;
            lock (_lockObject)
            {
                if (!_morphChannels.TryGetValue(channelId, out channel))
                {
                    return;
                }
            }

            foreach (var driverId in channel.Drivers)
            {
                if (_morphDrivers.TryGetValue(driverId, out var driver))
                {
                    await driver.UpdateFromChannelAsync(channelValue);
                }
            }
        }

        /// <summary>
        /// Tüm driver'ları günceller;
        /// </summary>
        private async Task UpdateAllDriversAsync()
        {
            foreach (var driver in _morphDrivers.Values)
            {
                await driver.UpdateAsync();
            }
        }

        #endregion;

        #region Private Methods - Displacement and Normal Generation;

        /// <summary>
        /// Displacement data oluşturur;
        /// </summary>
        private async Task<byte[]> GenerateDisplacementDataAsync(MorphTargetData data, int textureSize,
            DisplacementOptions options)
        {
            // Basit displacement map generation;
            var displacementMap = new float[textureSize * textureSize];

            // UV koordinatlarını kullanarak displacement hesapla;
            // Bu kısım gerçek implementasyonda daha kompleks olacak;
            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    var u = x / (float)textureSize;
                    var v = y / (float)textureSize;

                    // Vertex offset'ini displacement olarak kullan;
                    var displacement = CalculateDisplacementAtUV(data, u, v);
                    displacementMap[y * textureSize + x] = displacement;
                }
            }

            // Float array'ı byte array'a çevir;
            var byteData = new byte[displacementMap.Length * sizeof(float)];
            Buffer.BlockCopy(displacementMap, 0, byteData, 0, byteData.Length);

            return byteData;
        }

        /// <summary>
        /// UV koordinatında displacement hesaplar;
        /// </summary>
        private float CalculateDisplacementAtUV(MorphTargetData data, float u, float v)
        {
            // Basit barycentric interpolation;
            // Gerçek implementasyonda mesh UV'lerini kullanacak;
            return 0.0f;
        }

        /// <summary>
        /// Normal data oluşturur;
        /// </summary>
        private async Task<byte[]> GenerateNormalDataAsync(MorphTargetData data, int textureSize,
            NormalMapOptions options)
        {
            // Displacement map'ten normal map oluştur;
            var displacementData = await GenerateDisplacementDataAsync(data, textureSize,
                new DisplacementOptions());

            // Convert to normal map using Sobel filter;
            var normalMap = GenerateNormalMapFromDisplacement(displacementData, textureSize, options.Strength);

            return normalMap;
        }

        /// <summary>
        /// Displacement map'ten normal map oluşturur;
        /// </summary>
        private byte[] GenerateNormalMapFromDisplacement(byte[] displacementData, int textureSize, float strength)
        {
            // Sobel filter for normal map generation;
            var normalMap = new byte[textureSize * textureSize * 3]; // RGB;

            for (int y = 1; y < textureSize - 1; y++)
            {
                for (int x = 1; x < textureSize - 1; x++)
                {
                    // Sobel kernels;
                    var dX = (
                        GetDisplacementValue(displacementData, x + 1, y - 1, textureSize) * -1 +
                        GetDisplacementValue(displacementData, x + 1, y, textureSize) * -2 +
                        GetDisplacementValue(displacementData, x + 1, y + 1, textureSize) * -1 +
                        GetDisplacementValue(displacementData, x - 1, y - 1, textureSize) * 1 +
                        GetDisplacementValue(displacementData, x - 1, y, textureSize) * 2 +
                        GetDisplacementValue(displacementData, x - 1, y + 1, textureSize) * 1;
                    ) / 8.0f;

                    var dY = (
                        GetDisplacementValue(displacementData, x - 1, y + 1, textureSize) * 1 +
                        GetDisplacementValue(displacementData, x, y + 1, textureSize) * 2 +
                        GetDisplacementValue(displacementData, x + 1, y + 1, textureSize) * 1 +
                        GetDisplacementValue(displacementData, x - 1, y - 1, textureSize) * -1 +
                        GetDisplacementValue(displacementData, x, y - 1, textureSize) * -2 +
                        GetDisplacementValue(displacementData, x + 1, y - 1, textureSize) * -1;
                    ) / 8.0f;

                    // Normal vector;
                    var normal = new Vector3(-dX * strength, -dY * strength, 1.0f);
                    normal = Vector3.Normalize(normal);

                    // Map to [0, 255]
                    var idx = (y * textureSize + x) * 3;
                    normalMap[idx] = (byte)((normal.X + 1) * 127.5f);
                    normalMap[idx + 1] = (byte)((normal.Y + 1) * 127.5f);
                    normalMap[idx + 2] = (byte)((normal.Z + 1) * 127.5f);
                }
            }

            return normalMap;
        }

        /// <summary>
        /// Displacement değerini getirir;
        /// </summary>
        private float GetDisplacementValue(byte[] displacementData, int x, int y, int textureSize)
        {
            if (x < 0 || x >= textureSize || y < 0 || y >= textureSize)
                return 0.0f;

            var idx = y * textureSize + x;
            return BitConverter.ToSingle(displacementData, idx * sizeof(float));
        }

        /// <summary>
        /// Kombine displacement hesaplar;
        /// </summary>
        private async Task<byte[]> CalculateCombinedDisplacementAsync(List<MorphTarget> targets, int textureSize)
        {
            // Tüm target'ların displacement'ını birleştir;
            var combinedDisplacement = new float[textureSize * textureSize];

            foreach (var target in targets)
            {
                var displacementData = await GenerateDisplacementDataAsync(target.Data, textureSize,
                    new DisplacementOptions());

                var floatData = new float[displacementData.Length / sizeof(float)];
                Buffer.BlockCopy(displacementData, 0, floatData, 0, displacementData.Length);

                for (int i = 0; i < floatData.Length; i++)
                {
                    combinedDisplacement[i] += floatData[i];
                }
            }

            // Byte array'a çevir;
            var byteData = new byte[combinedDisplacement.Length * sizeof(float)];
            Buffer.BlockCopy(combinedDisplacement, 0, byteData, 0, byteData.Length);

            return byteData;
        }

        #endregion;

        #region Private Methods - Expression and Phoneme Operations;

        /// <summary>
        /// Dynamic expression oluşturur;
        /// </summary>
        private async Task CreateDynamicExpressionAsync(ExpressionType expression, string presetId,
            ExpressionOptions options)
        {
            // Expression'a özgü kanal değerleri;
            var channelValues = new Dictionary<string, float>();

            switch (expression)
            {
                case ExpressionType.Neutral:
                    // Tüm değerler 0;
                    break;

                case ExpressionType.Happy:
                    channelValues["smile_left"] = 1.0f;
                    channelValues["smile_right"] = 1.0f;
                    channelValues["eyes_squint_left"] = 0.5f;
                    channelValues["eyes_squint_right"] = 0.5f;
                    break;

                case ExpressionType.Sad:
                    channelValues["frown_left"] = 0.8f;
                    channelValues["frown_right"] = 0.8f;
                    channelValues["brow_down_left"] = 0.6f;
                    channelValues["brow_down_right"] = 0.6f;
                    break;

                case ExpressionType.Angry:
                    channelValues["brow_down_left"] = 1.0f;
                    channelValues["brow_down_right"] = 1.0f;
                    channelValues["snarl_left"] = 0.7f;
                    channelValues["snarl_right"] = 0.7f;
                    break;

                case ExpressionType.Surprised:
                    channelValues["brow_up_left"] = 1.0f;
                    channelValues["brow_up_right"] = 1.0f;
                    channelValues["eyes_wide_left"] = 0.9f;
                    channelValues["eyes_wide_right"] = 0.9f;
                    break;

                case ExpressionType.Fear:
                    channelValues["brow_up_center"] = 0.8f;
                    channelValues["eyes_wide_left"] = 1.0f;
                    channelValues["eyes_wide_right"] = 1.0f;
                    channelValues["mouth_open"] = 0.3f;
                    break;

                case ExpressionType.Disgust:
                    channelValues["nose_scrunch"] = 0.9f;
                    channelValues["upper_lip_raise"] = 0.7f;
                    channelValues["brow_down_center"] = 0.6f;
                    break;

                case ExpressionType.Contempt:
                    channelValues["smirk_left"] = 0.8f;
                    channelValues["smirk_right"] = 0.3f;
                    channelValues["brow_up_left"] = 0.4f;
                    break;
            }

            // Preset oluştur;
            var preset = new MorphPreset(presetId)
            {
                Name = expression.ToString(),
                Description = $"Dynamic expression: {expression}",
                Category = MorphCategory.Expression,
                CreatedDate = DateTime.UtcNow;
            };

            preset.ChannelValues = channelValues;

            lock (_lockObject)
            {
                _morphPresets[presetId] = preset;
            }
        }

        /// <summary>
        /// Expression'ı animasyonla uygular;
        /// </summary>
        private async Task AnimateExpressionAsync(ExpressionType expression, float intensity,
            float duration, EasingFunction easing)
        {
            // Expression animasyonu;
            // Gerçek implementasyonda daha kompleks olacak;
            await Task.Delay((int)(duration * 1000));
        }

        /// <summary>
        /// Dynamic phoneme oluşturur;
        /// </summary>
        private async Task CreateDynamicPhonemeAsync(PhonemeType phoneme, string presetId,
            PhonemeOptions options)
        {
            // Phoneme'a özgü kanal değerleri;
            var channelValues = new Dictionary<string, float>();

            switch (phoneme)
            {
                case PhonemeType.AI:
                    channelValues["jaw_open"] = 0.3f;
                    channelValues["mouth_wide"] = 0.2f;
                    break;

                case PhonemeType.EE:
                    channelValues["mouth_wide"] = 0.6f;
                    channelValues["lips_stretch"] = 0.5f;
                    break;

                case PhonemeType.O:
                    channelValues["mouth_round"] = 0.7f;
                    channelValues["jaw_open"] = 0.2f;
                    break;

                case PhonemeType.U:
                    channelValues["mouth_round"] = 0.9f;
                    channelValues["lips_pucker"] = 0.4f;
                    break;

                case PhonemeType.FV:
                    channelValues["lower_lip_under"] = 0.8f;
                    channelValues["mouth_narrow"] = 0.3f;
                    break;

                case PhonemeType.MBP:
                    channelValues["lips_together"] = 1.0f;
                    break;

                case PhonemeType.L:
                    channelValues["tongue_up"] = 0.6f;
                    channelValues["mouth_open"] = 0.2f;
                    break;

                case PhonemeType.R:
                    channelValues["tongue_curl"] = 0.7f;
                    channelValues["mouth_narrow"] = 0.3f;
                    break;

                case PhonemeType.THD:
                    channelValues["tongue_out"] = 0.4f;
                    channelValues["mouth_open"] = 0.1f;
                    break;

                case PhonemeType.SZ:
                    channelValues["mouth_narrow"] = 0.5f;
                    channelValues["tongue_flat"] = 0.3f;
                    break;

                case PhonemeType.SHCH:
                    channelValues["lips_funnel"] = 0.6f;
                    channelValues["tongue_up"] = 0.4f;
                    break;

                case PhonemeType.KG:
                    channelValues["tongue_back"] = 0.8f;
                    channelValues["jaw_open"] = 0.3f;
                    break;
            }

            // Preset oluştur;
            var preset = new MorphPreset(presetId)
            {
                Name = phoneme.ToString(),
                Description = $"Dynamic phoneme: {phoneme}",
                Category = MorphCategory.Phoneme,
                CreatedDate = DateTime.UtcNow;
            };

            preset.ChannelValues = channelValues;

            lock (_lockObject)
            {
                _morphPresets[presetId] = preset;
            }
        }

        /// <summary>
        /// Lip sync düzeltmeleri uygular;
        /// </summary>
        private async Task ApplyLipSyncCorrectionsAsync(PhonemeType phoneme, float intensity,
            PhonemeOptions options)
        {
            // Lip sync için özel düzeltmeler;
            // Gerçek implementasyonda daha kompleks olacak;
            await Task.CompletedTask;
        }

        #endregion;

        #region Private Methods - Export and Import Operations;

        /// <summary>
        /// Import formatını tespit eder;
        /// </summary>
        private ImportFormat DetectImportFormat(byte[] data)
        {
            if (data.Length < 4)
                return ImportFormat.Unknown;

            // Check for common file signatures;
            var header = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(100, data.Length));

            if (header.Contains("FBX"))
                return ImportFormat.FBX;

            if (header.StartsWith("# OBJ"))
                return ImportFormat.OBJ;

            if (header.Contains("blendShape"))
                return ImportFormat.BlendShape;

            if (header.StartsWith("{") || header.StartsWith("["))
                return ImportFormat.JSON;

            if (header.StartsWith("NEDA"))
                return ImportFormat.NEDA;

            return ImportFormat.Unknown;
        }

        /// <summary>
        /// NEDA formatına export;
        /// </summary>
        private byte[] ExportToNedaFormat(MorphTarget target)
        {
            var exportData = new MorphExport;
            {
                Target = target,
                ExportVersion = "1.0",
                ExportDate = DateTime.UtcNow;
            };

            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// FBX formatına export;
        /// </summary>
        private async Task<byte[]> ExportToFbxFormatAsync(MorphTarget target)
        {
            // FBX export işlemi;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// OBJ formatına export;
        /// </summary>
        private async Task<byte[]> ExportToObjFormatAsync(MorphTarget target)
        {
            // OBJ export işlemi;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// BlendShape formatına export;
        /// </summary>
        private async Task<byte[]> ExportToBlendShapeFormatAsync(MorphTarget target)
        {
            // BlendShape export işlemi;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// JSON formatına export;
        /// </summary>
        private byte[] ExportToJsonFormat(MorphTarget target)
        {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(target,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// NEDA formatından import;
        /// </summary>
        private MorphTarget ImportFromNedaFormat(byte[] data)
        {
            var exportData = System.Text.Json.JsonSerializer.Deserialize<MorphExport>(data);
            return exportData.Target;
        }

        /// <summary>
        /// FBX formatından import;
        /// </summary>
        private async Task<MorphTarget> ImportFromFbxFormatAsync(byte[] data)
        {
            // FBX import işlemi;
            return await Task.FromResult(new MorphTarget("imported", new MorphTargetData()));
        }

        /// <summary>
        /// OBJ formatından import;
        /// </summary>
        private async Task<MorphTarget> ImportFromObjFormatAsync(byte[] data)
        {
            // OBJ import işlemi;
            return await Task.FromResult(new MorphTarget("imported", new MorphTargetData()));
        }

        /// <summary>
        /// BlendShape formatından import;
        /// </summary>
        private async Task<MorphTarget> ImportFromBlendShapeFormatAsync(byte[] data)
        {
            // BlendShape import işlemi;
            return await Task.FromResult(new MorphTarget("imported", new MorphTargetData()));
        }

        /// <summary>
        /// JSON formatından import;
        /// </summary>
        private MorphTarget ImportFromJsonFormat(byte[] data)
        {
            return System.Text.Json.JsonSerializer.Deserialize<MorphTarget>(data);
        }

        #endregion;

        #region Private Methods - Performance and Diagnostics;

        /// <summary>
        /// Performans izlemeyi başlatır;
        /// </summary>
        private void StartPerformanceMonitoring()
        {
            _performanceMetrics.Reset();
            _lastMetricsUpdate = DateTime.UtcNow;
        }

        /// <summary>
        /// Performans izlemeyi durdurur;
        /// </summary>
        private void StopPerformanceMonitoring()
        {
            // Performans verilerini kaydet;
            SavePerformanceMetrics();
        }

        /// <summary>
        /// Performans metriklerini günceller;
        /// </summary>
        private void UpdatePerformanceMetrics(float frameTime, List<float> frameTimes)
        {
            // Frame time istatistikleri;
            _performanceMetrics.FrameCount++;
            _performanceMetrics.TotalFrameTime += frameTime;
            _performanceMetrics.AverageFrameTime = _performanceMetrics.TotalFrameTime / _performanceMetrics.FrameCount;
            _performanceMetrics.MaxFrameTime = Math.Max(_performanceMetrics.MaxFrameTime, frameTime);
            _performanceMetrics.MinFrameTime = Math.Min(_performanceMetrics.MinFrameTime, frameTime);

            // FPS hesapla;
            if (frameTime > 0)
            {
                _performanceMetrics.CurrentFPS = 1.0f / frameTime;
                _performanceMetrics.AverageFPS = _performanceMetrics.FrameCount / _performanceMetrics.TotalFrameTime;
            }

            // Frame time window (son 60 frame)
            if (frameTimes.Count > 60)
            {
                frameTimes.RemoveAt(0);
            }

            // Percentile hesapla;
            if (frameTimes.Count > 0)
            {
                var sortedTimes = frameTimes.OrderBy(t => t).ToList();
                _performanceMetrics.P95FrameTime = sortedTimes[(int)(sortedTimes.Count * 0.95)];
                _performanceMetrics.P99FrameTime = sortedTimes[(int)(sortedTimes.Count * 0.99)];
            }

            // Periodic save (her 60 saniyede bir)
            if ((DateTime.UtcNow - _lastMetricsUpdate).TotalSeconds >= 60)
            {
                SavePerformanceMetrics();
                _lastMetricsUpdate = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Performans metriklerini kaydeder;
        /// </summary>
        private void SavePerformanceMetrics()
        {
            try
            {
                var metricsPath = Path.Combine(_config.AppDataPath, "MorphEngine", "Performance");
                Directory.CreateDirectory(metricsPath);

                var fileName = $"metrics_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                var filePath = Path.Combine(metricsPath, fileName);

                var json = System.Text.Json.JsonSerializer.Serialize(_performanceMetrics,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Performans metrikleri kaydetme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Memory kullanımını hesaplar;
        /// </summary>
        private long GetMemoryUsage()
        {
            try
            {
                // Basit memory hesaplama;
                long totalBytes = 0;

                lock (_lockObject)
                {
                    foreach (var target in _morphTargets.Values)
                    {
                        totalBytes += target.Data.EstimatedSize;
                    }

                    totalBytes += _morphCache.EstimatedSize;
                }

                return totalBytes;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Tüm morph target'ları optimize eder;
        /// </summary>
        private async Task OptimizeAllMorphTargetsAsync(OptimizationLevel level)
        {
            var targetIds = new List<string>();

            lock (_lockObject)
            {
                targetIds.AddRange(_morphTargets.Keys);
            }

            foreach (var targetId in targetIds)
            {
                try
                {
                    var target = await GetMorphTargetAsync(targetId);
                    var optimizedTarget = await OptimizeMorphTargetAsync(target, level);

                    lock (_lockObject)
                    {
                        _morphTargets[targetId] = optimizedTarget;
                    }

                    await _morphCache.StoreMorphTargetAsync(optimizedTarget);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Morph target optimizasyon hatası ({targetId}): {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Tüm kanalları optimize eder;
        /// </summary>
        private async Task OptimizeAllChannelsAsync(OptimizationLevel level)
        {
            // Channel optimizasyon işlemleri;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Memory cleanup yapar;
        /// </summary>
        private async Task CleanupMemoryAsync()
        {
            try
            {
                // Cache cleanup;
                await _morphCache.CleanupAsync();

                // Unused targets cleanup;
                await CleanupUnusedTargetsAsync();

                // Memory collection;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                _logger.LogDebug("Memory cleanup tamamlandı.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Memory cleanup hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Kullanılmayan target'ları temizler;
        /// </summary>
        private async Task CleanupUnusedTargetsAsync()
        {
            var usedTargets = new HashSet<string>();

            // Kanal'larda kullanılan target'ları topla;
            lock (_lockObject)
            {
                foreach (var channel in _morphChannels.Values)
                {
                    foreach (var target in channel.Targets)
                    {
                        usedTargets.Add(target.Target.Id);
                    }
                }
            }

            // Kullanılmayan target'ları sil;
            var unusedTargets = new List<string>();

            lock (_lockObject)
            {
                foreach (var targetId in _morphTargets.Keys)
                {
                    if (!usedTargets.Contains(targetId))
                    {
                        unusedTargets.Add(targetId);
                    }
                }
            }

            foreach (var targetId in unusedTargets)
            {
                await DeleteMorphTargetAsync(targetId);
            }
        }

        #endregion;

        #region Private Methods - Diagnostic Tests;

        /// <summary>
        /// Initialization test;
        /// </summary>
        private async Task<DiagnosticTestResult> TestInitializationAsync()
        {
            var result = new DiagnosticTestResult;
            {
                TestName = "Initialization Test",
                StartTime = DateTime.UtcNow;
            };

            try
            {
                // Test initialization;
                var testEngine = new MorphEngine(_logger, _config, _renderEngine, _boneManager, _ikSystem);
                await testEngine.InitializeAsync();

                result.Passed = testEngine.IsInitialized;
                result.Message = testEngine.IsInitialized ? "Initialization successful" : "Initialization failed";

                await testEngine.ShutdownAsync();
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Initialization error: {ex.Message}";
                result.Error = ex;
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        /// <summary>
        /// Morph target operations test;
        /// </summary>
        private async Task<DiagnosticTestResult> TestMorphTargetOperationsAsync()
        {
            var result = new DiagnosticTestResult;
            {
                TestName = "Morph Target Operations Test",
                StartTime = DateTime.UtcNow;
            };

            try
            {
                // Create test data;
                var testData = CreateTestMorphData();

                // Test create;
                var target = await CreateMorphTargetAsync("test_target", testData);
                result.Passed = target != null;
                result.Message = target != null ? "Create successful" : "Create failed";

                if (target != null)
                {
                    // Test get;
                    var retrieved = await GetMorphTargetAsync("test_target");
                    result.Passed &= retrieved != null;
                    result.Message += retrieved != null ? ", Get successful" : ", Get failed";

                    // Test update;
                    var update = new MorphTargetUpdate;
                    {
                        Name = "Updated Test Target"
                    };
                    await UpdateMorphTargetAsync("test_target", update);

                    // Test delete;
                    var deleted = await DeleteMorphTargetAsync("test_target");
                    result.Passed &= deleted;
                    result.Message += deleted ? ", Delete successful" : ", Delete failed";
                }
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Morph target operations error: {ex.Message}";
                result.Error = ex;
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        /// <summary>
        /// Channel operations test;
        /// </summary>
        private async Task<DiagnosticTestResult> TestChannelOperationsAsync()
        {
            var result = new DiagnosticTestResult;
            {
                TestName = "Channel Operations Test",
                StartTime = DateTime.UtcNow;
            };

            try
            {
                // Create test channel;
                var channel = CreateMorphChannel("test_channel");
                result.Passed = channel != null;
                result.Message = channel != null ? "Channel create successful" : "Channel create failed";

                if (channel != null)
                {
                    // Test value set;
                    await SetChannelValueAsync("test_channel", 0.5f);
                    result.Passed &= Math.Abs(GetChannel("test_channel").CurrentValue - 0.5f) < 0.01f;
                    result.Message += result.Passed ? ", Set value successful" : ", Set value failed";

                    // Test reset;
                    await ResetChannelAsync("test_channel");
                    result.Passed &= Math.Abs(GetChannel("test_channel").CurrentValue) < 0.01f;
                    result.Message += result.Passed ? ", Reset successful" : ", Reset failed";
                }
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Channel operations error: {ex.Message}";
                result.Error = ex;
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        /// <summary>
        /// Performance test;
        /// </summary>
        private async Task<DiagnosticTestResult> TestPerformanceAsync()
        {
            var result = new DiagnosticTestResult;
            {
                TestName = "Performance Test",
                StartTime = DateTime.UtcNow;
            };

            try
            {
                // Performance test: çoklu kanal update;
                var channels = new List<string>();
                for (int i = 0; i < 10; i++)
                {
                    var channelId = $"perf_test_channel_{i}";
                    CreateMorphChannel(channelId);
                    channels.Add(channelId);
                }

                // Update test;
                var startTime = DateTime.UtcNow;
                var iterations = 100;

                for (int iter = 0; iter < iterations; iter++)
                {
                    foreach (var channelId in channels)
                    {
                        await SetChannelValueAsync(channelId, (float)iter / iterations, false, false);
                    }
                    await ApplyAllChannelsAsync();
                }

                var endTime = DateTime.UtcNow;
                var duration = (endTime - startTime).TotalSeconds;
                var opsPerSecond = (iterations * channels.Count) / duration;

                result.Passed = opsPerSecond > 100; // 100 ops/s minimum;
                result.Message = $"Performance: {opsPerSecond:F2} operations/second";
                result.Metrics["ops_per_second"] = opsPerSecond;
                result.Metrics["duration"] = duration;

                // Cleanup;
                foreach (var channelId in channels)
                {
                    // Channel silme işlemi;
                }
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Performance test error: {ex.Message}";
                result.Error = ex;
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        /// <summary>
        /// Memory test;
        /// </summary>
        private async Task<DiagnosticTestResult> TestMemoryAsync()
        {
            var result = new DiagnosticTestResult;
            {
                TestName = "Memory Test",
                StartTime = DateTime.UtcNow;
            };

            try
            {
                // Memory usage test;
                var initialMemory = GetMemoryUsage();

                // Create multiple targets to test memory;
                var targets = new List<string>();
                for (int i = 0; i < 5; i++)
                {
                    var targetId = $"mem_test_target_{i}";
                    var testData = CreateTestMorphData();
                    await CreateMorphTargetAsync(targetId, testData);
                    targets.Add(targetId);
                }

                var memoryAfterCreate = GetMemoryUsage();
                var memoryIncrease = memoryAfterCreate - initialMemory;

                // Delete targets;
                foreach (var targetId in targets)
                {
                    await DeleteMorphTargetAsync(targetId);
                }

                var memoryAfterDelete = GetMemoryUsage();
                var memoryDecrease = memoryAfterCreate - memoryAfterDelete;

                result.Passed = memoryDecrease >= memoryIncrease * 0.9f; // %90'ını geri kazanmalı;
                result.Message = $"Memory test: Increase={memoryIncrease}, Decrease={memoryDecrease}";
                result.Metrics["initial_memory"] = initialMemory;
                result.Metrics["memory_increase"] = memoryIncrease;
                result.Metrics["memory_decrease"] = memoryDecrease;
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Memory test error: {ex.Message}";
                result.Error = ex;
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        /// <summary>
        /// Test morph data oluşturur;
        /// </summary>
        private MorphTargetData CreateTestMorphData()
        {
            // Basit test data (8 vertex - bir küp)
            var baseVertices = new float[]
            {
                -1, -1, -1,  // 0;
                 1, -1, -1,  // 1;
                 1,  1, -1,  // 2;
                -1,  1, -1,  // 3;
                -1, -1,  1,  // 4;
                 1, -1,  1,  // 5;
                 1,  1,  1,  // 6;
                -1,  1,  1   // 7;
            };

            // Basit offset'ler;
            var targetVertices = new float[]
            {
                0, 0, 0,  // 0;
                0, 0, 0,  // 1;
                0, 0, 0,  // 2;
                0, 0, 0,  // 3;
                0, 0, 0.5f,  // 4 (yukarı çık)
                0, 0, 0.5f,  // 5;
                0, 0, 0.5f,  // 6;
                0, 0, 0.5f   // 7;
            };

            return new MorphTargetData;
            {
                BaseVertices = baseVertices,
                TargetVertices = targetVertices;
            };
        }

        #endregion;

        #region Private Methods - Resource Management;

        /// <summary>
        /// Yapılandırmayı yükler;
        /// </summary>
        private async Task LoadConfigurationAsync()
        {
            try
            {
                var configPath = Path.Combine(_config.AppDataPath, "MorphEngine", "config.json");
                if (File.Exists(configPath))
                {
                    var json = await File.ReadAllTextAsync(configPath);
                    _configuration = System.Text.Json.JsonSerializer.Deserialize<MorphConfiguration>(json);
                }
                else;
                {
                    // Varsayılan yapılandırma;
                    _configuration = new MorphConfiguration;
                    {
                        TargetFPS = 60,
                        MaxActiveChannels = 50,
                        MaxMorphTargets = 1000,
                        CacheSettings = new CacheSettings;
                        {
                            MaxCacheSize = 1024 * 1024 * 100, // 100 MB;
                            DefaultExpiration = TimeSpan.FromMinutes(30),
                            CleanupInterval = TimeSpan.FromMinutes(5)
                        },
                        OptimizationLevel = OptimizationLevel.Medium,
                        EnableAsyncProcessing = true,
                        ThreadPoolSize = Environment.ProcessorCount,
                        MaxUpdateThreads = 4,
                        EnableCompression = true,
                        CompressionLevel = CompressionLevel.Balanced;
                    };

                    // Varsayılan yapılandırmayı kaydet;
                    Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                    var defaultJson = System.Text.Json.JsonSerializer.Serialize(_configuration,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(configPath, defaultJson);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Yapılandırma yükleme hatası: {ex.Message}", ex);
                // Varsayılan yapılandırmaya devam et;
            }
        }

        /// <summary>
        /// Performans ayarlarını yükler;
        /// </summary>
        private async Task LoadPerformanceSettingsAsync()
        {
            try
            {
                var settingsPath = Path.Combine(_config.AppDataPath, "MorphEngine", "performance.json");
                if (File.Exists(settingsPath))
                {
                    var json = await File.ReadAllTextAsync(settingsPath);
                    _performanceSettings = System.Text.Json.JsonSerializer.Deserialize<MorphPerformanceSettings>(json);
                }
                else;
                {
                    // Varsayılan performans ayarları;
                    _performanceSettings = new MorphPerformanceSettings;
                    {
                        TargetOptimizationLevel = OptimizationLevel.Medium,
                        ChannelOptimizationLevel = OptimizationLevel.Low,
                        CacheOptimizationLevel = OptimizationLevel.High,
                        EnableFrameRateLimiting = true,
                        TargetFrameRate = 60,
                        EnableMemoryMonitoring = true,
                        MemoryWarningThreshold = 1024 * 1024 * 500, // 500 MB;
                        MemoryCriticalThreshold = 1024 * 1024 * 800, // 800 MB;
                        EnablePerformanceLogging = true,
                        LoggingInterval = TimeSpan.FromMinutes(5),
                        EnableDiagnostics = true,
                        DiagnosticsInterval = TimeSpan.FromHours(1)
                    };

                    // Varsayılan ayarları kaydet;
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                    var defaultJson = System.Text.Json.JsonSerializer.Serialize(_performanceSettings,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(settingsPath, defaultJson);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Performans ayarları yükleme hatası: {ex.Message}", ex);
                // Varsayılan ayarlara devam et;
            }
        }

        /// <summary>
        /// Morph target'ları yükler;
        /// </summary>
        private async Task LoadMorphTargetsAsync()
        {
            try
            {
                var targetsPath = Path.Combine(_config.AppDataPath, "MorphEngine", "Targets");
                if (!Directory.Exists(targetsPath))
                {
                    Directory.CreateDirectory(targetsPath);
                    await CreateDefaultTargetsAsync(targetsPath);
                }

                await LoadTargetsFromDirectoryAsync(targetsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph target'ları yükleme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Morph preset'lerini yükler;
        /// </summary>
        private async Task LoadMorphPresetsAsync()
        {
            try
            {
                var presetsPath = Path.Combine(_config.AppDataPath, "MorphEngine", "Presets");
                if (!Directory.Exists(presetsPath))
                {
                    Directory.CreateDirectory(presetsPath);
                    await CreateDefaultPresetsAsync(presetsPath);
                }

                await LoadPresetsFromDirectoryAsync(presetsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Morph preset'leri yükleme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Kaynakları serbest bırakır;
        /// </summary>
        private async Task ReleaseResourcesAsync()
        {
            try
            {
                // Cache'i temizle;
                await _morphCache.ClearAsync();

                // Collections'ları temizle;
                lock (_lockObject)
                {
                    _morphTargets.Clear();
                    _morphChannels.Clear();
                    _morphPresets.Clear();
                    _activeCompositions.Clear();
                    _morphDrivers.Clear();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kaynakları serbest bırakma hatası: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Private Methods - Default Content Creation;

        /// <summary>
        /// Varsayılan morph target'ları oluşturur;
        /// </summary>
        private async Task CreateDefaultTargetsAsync(string targetsPath)
        {
            // Temel yüz ifadeleri için default target'lar;
            var defaultTargets = new[]
            {
                CreateDefaultTarget("brow_up_left", MorphRegion.Brow, MorphCategory.Expression),
                CreateDefaultTarget("brow_up_right", MorphRegion.Brow, MorphCategory.Expression),
                CreateDefaultTarget("brow_down_left", MorphRegion.Brow, MorphCategory.Expression),
                CreateDefaultTarget("brow_down_right", MorphRegion.Brow, MorphCategory.Expression),
                CreateDefaultTarget("eye_blink_left", MorphRegion.Eye, MorphCategory.Expression),
                CreateDefaultTarget("eye_blink_right", MorphRegion.Eye, MorphCategory.Expression),
                CreateDefaultTarget("eye_wide_left", MorphRegion.Eye, MorphCategory.Expression),
                CreateDefaultTarget("eye_wide_right", MorphRegion.Eye, MorphCategory.Expression),
                CreateDefaultTarget("smile_left", MorphRegion.Mouth, MorphCategory.Expression),
                CreateDefaultTarget("smile_right", MorphRegion.Mouth, MorphCategory.Expression),
                CreateDefaultTarget("frown_left", MorphRegion.Mouth, MorphCategory.Expression),
                CreateDefaultTarget("frown_right", MorphRegion.Mouth, MorphCategory.Expression),
                CreateDefaultTarget("jaw_open", MorphRegion.Jaw, MorphCategory.Phoneme),
                CreateDefaultTarget("jaw_left", MorphRegion.Jaw, MorphCategory.Phoneme),
                CreateDefaultTarget("jaw_right", MorphRegion.Jaw, MorphCategory.Phoneme),
                CreateDefaultTarget("mouth_wide", MorphRegion.Mouth, MorphCategory.Phoneme),
                CreateDefaultTarget("mouth_narrow", MorphRegion.Mouth, MorphCategory.Phoneme),
                CreateDefaultTarget("lips_together", MorphRegion.Mouth, MorphCategory.Phoneme),
                CreateDefaultTarget("lips_part", MorphRegion.Mouth, MorphCategory.Phoneme),
                CreateDefaultTarget("tongue_out", MorphRegion.Tongue, MorphCategory.Phoneme),
                CreateDefaultTarget("tongue_up", MorphRegion.Tongue, MorphCategory.Phoneme),
                CreateDefaultTarget("tongue_down", MorphRegion.Tongue, MorphCategory.Phoneme)
            };

            foreach (var target in defaultTargets)
            {
                var filePath = Path.Combine(targetsPath, $"{target.Id}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(target,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json);
            }
        }

        /// <summary>
        /// Varsayılan morph preset'leri oluşturur;
        /// </summary>
        private async Task CreateDefaultPresetsAsync(string presetsPath)
        {
            var defaultPresets = new[]
            {
                CreateDefaultPreset("expression_happy", ExpressionType.Happy),
                CreateDefaultPreset("expression_sad", ExpressionType.Sad),
                CreateDefaultPreset("expression_angry", ExpressionType.Angry),
                CreateDefaultPreset("expression_surprised", ExpressionType.Surprised),
                CreateDefaultPreset("expression_neutral", ExpressionType.Neutral),
                CreateDefaultPreset("phoneme_ai", PhonemeType.AI),
                CreateDefaultPreset("phoneme_ee", PhonemeType.EE),
                CreateDefaultPreset("phoneme_o", PhonemeType.O),
                CreateDefaultPreset("phoneme_fv", PhonemeType.FV)
            };

            foreach (var preset in defaultPresets)
            {
                var filePath = Path.Combine(presetsPath, $"{preset.Id}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(preset,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json);
            }
        }

        /// <summary>
        /// Varsayılan morph target oluşturur;
        /// </summary>
        private MorphTarget CreateDefaultTarget(string id, MorphRegion region, MorphCategory category)
        {
            // Basit test verisi;
            var data = CreateTestMorphData();

            return new MorphTarget(id, data)
            {
                Name = id.Replace("_", " "),
                Category = category,
                Region = region,
                InfluenceRange = 1.0f,
                SymmetryMode = id.EndsWith("_left") ? MorphSymmetry.LeftToRight :
                              id.EndsWith("_right") ? MorphSymmetry.RightToLeft : MorphSymmetry.None,
                CreatedDate = DateTime.UtcNow,
                Version = 1;
            };
        }

        /// <summary>
        /// Varsayılan morph preset oluşturur;
        /// </summary>
        private MorphPreset CreateDefaultPreset(string id, ExpressionType expression)
        {
            var preset = new MorphPreset(id)
            {
                Name = expression.ToString(),
                Description = $"Default {expression} expression",
                Category = MorphCategory.Expression,
                CreatedDate = DateTime.UtcNow;
            };

            // Expression'a göre kanal değerleri;
            switch (expression)
            {
                case ExpressionType.Happy:
                    preset.ChannelValues["smile_left"] = 1.0f;
                    preset.ChannelValues["smile_right"] = 1.0f;
                    preset.ChannelValues["eyes_squint_left"] = 0.5f;
                    preset.ChannelValues["eyes_squint_right"] = 0.5f;
                    break;

                case ExpressionType.Sad:
                    preset.ChannelValues["frown_left"] = 0.8f;
                    preset.ChannelValues["frown_right"] = 0.8f;
                    preset.ChannelValues["brow_down_left"] = 0.6f;
                    preset.ChannelValues["brow_down_right"] = 0.6f;
                    break;

                case ExpressionType.Angry:
                    preset.ChannelValues["brow_down_left"] = 1.0f;
                    preset.ChannelValues["brow_down_right"] = 1.0f;
                    preset.ChannelValues["snarl_left"] = 0.7f;
                    preset.ChannelValues["snarl_right"] = 0.7f;
                    break;

                case ExpressionType.Surprised:
                    preset.ChannelValues["brow_up_left"] = 1.0f;
                    preset.ChannelValues["brow_up_right"] = 1.0f;
                    preset.ChannelValues["eyes_wide_left"] = 0.9f;
                    preset.ChannelValues["eyes_wide_right"] = 0.9f;
                    break;

                case ExpressionType.Neutral:
                    // Tüm değerler 0;
                    break;
            }

            return preset;
        }

        private MorphPreset CreateDefaultPreset(string id, PhonemeType phoneme)
        {
            var preset = new MorphPreset(id)
            {
                Name = phoneme.ToString(),
                Description = $"Default {phoneme} phoneme",
                Category = MorphCategory.Phoneme,
                CreatedDate = DateTime.UtcNow;
            };

            // Phoneme'a göre kanal değerleri;
            switch (phoneme)
            {
                case PhonemeType.AI:
                    preset.ChannelValues["jaw_open"] = 0.3f;
                    preset.ChannelValues["mouth_wide"] = 0.2f;
                    break;

                case PhonemeType.EE:
                    preset.ChannelValues["mouth_wide"] = 0.6f;
                    preset.ChannelValues["lips_stretch"] = 0.5f;
                    break;

                case PhonemeType.O:
                    preset.ChannelValues["mouth_round"] = 0.7f;
                    preset.ChannelValues["jaw_open"] = 0.2f;
                    break;

                case PhonemeType.FV:
                    preset.ChannelValues["lower_lip_under"] = 0.8f;
                    preset.ChannelValues["mouth_narrow"] = 0.3f;
                    break;
            }

            return preset;
        }

        #endregion;

        #region Private Methods - File Operations;

        /// <summary>
        /// Target'ları dizinden yükler;
        /// </summary>
        private async Task LoadTargetsFromDirectoryAsync(string directoryPath)
        {
            var targetFiles = Directory.GetFiles(directoryPath, "*.json");

            foreach (var file in targetFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var target = System.Text.Json.JsonSerializer.Deserialize<MorphTarget>(json);

                    if (target != null)
                    {
                        lock (_lockObject)
                        {
                            _morphTargets[target.Id] = target;
                        }

                        // Cache'e ekle;
                        await _morphCache.StoreMorphTargetAsync(target);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Morph target yükleme hatası ({Path.GetFileName(file)}): {ex.Message}", ex);
                }
            }

            _logger.LogDebug($"{targetFiles.Length} morph target yüklendi.");
        }

        /// <summary>
        /// Preset'leri dizinden yükler;
        /// </summary>
        private async Task LoadPresetsFromDirectoryAsync(string directoryPath)
        {
            var presetFiles = Directory.GetFiles(directoryPath, "*.json");

            foreach (var file in presetFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var preset = System.Text.Json.JsonSerializer.Deserialize<MorphPreset>(json);

                    if (preset != null)
                    {
                        lock (_lockObject)
                        {
                            _morphPresets[preset.Id] = preset;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Morph preset yükleme hatası ({Path.GetFileName(file)}): {ex.Message}", ex);
                }
            }

            _logger.LogDebug($"{presetFiles.Length} morph preset yüklendi.");
        }

        /// <summary>
        /// Preset'i dosyaya kaydeder;
        /// </summary>
        private async Task SavePresetToFileAsync(MorphPreset preset, string filePath = null)
        {
            filePath ??= Path.Combine(_config.AppDataPath, "MorphEngine", "Presets", $"{preset.Id}.json");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            var json = System.Text.Json.JsonSerializer.Serialize(preset,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(filePath, json);
        }

        #endregion;

        #region Private Methods - Helper Getters;

        /// <summary>
        /// Morph kanalını getirir;
        /// </summary>
        private MorphChannel GetChannel(string channelId)
        {
            lock (_lockObject)
            {
                if (_morphChannels.TryGetValue(channelId, out var channel))
                {
                    return channel;
                }
            }
            return null;
        }

        /// <summary>
        /// Aktif kanalları getirir;
        /// </summary>
        private List<MorphChannel> GetActiveChannels()
        {
            lock (_lockObject)
            {
                return _morphChannels.Values;
                    .Where(c => c.IsActive && Math.Abs(c.CurrentValue) > 0.001f)
                    .ToList();
            }
        }

        /// <summary>
        /// Tüm kanalları getirir;
        /// </summary>
        private List<MorphChannel> GetAllChannels()
        {
            lock (_lockObject)
            {
                return _morphChannels.Values.ToList();
            }
        }

        /// <summary>
        /// Varsayılan kanalları başlatır;
        /// </summary>
        private void InitializeDefaultChannels()
        {
            // Temel yüz ifadesi kanalları;
            var defaultChannels = new[]
            {
                new { Id = "brow_up", Name = "Brow Up", Category = MorphCategory.Expression, Region = MorphRegion.Brow },
                new { Id = "brow_down", Name = "Brow Down", Category = MorphCategory.Expression, Region = MorphRegion.Brow },
                new { Id = "eye_blink", Name = "Eye Blink", Category = MorphCategory.Expression, Region = MorphRegion.Eye },
                new { Id = "eye_wide", Name = "Eye Wide", Category = MorphCategory.Expression, Region = MorphRegion.Eye },
                new { Id = "smile", Name = "Smile", Category = MorphCategory.Expression, Region = MorphRegion.Mouth },
                new { Id = "frown", Name = "Frown", Category = MorphCategory.Expression, Region = MorphRegion.Mouth },
                new { Id = "jaw_open", Name = "Jaw Open", Category = MorphCategory.Phoneme, Region = MorphRegion.Jaw },
                new { Id = "mouth_wide", Name = "Mouth Wide", Category = MorphCategory.Phoneme, Region = MorphRegion.Mouth },
                new { Id = "mouth_narrow", Name = "Mouth Narrow", Category = MorphCategory.Phoneme, Region = MorphRegion.Mouth }
            };

            foreach (var channelInfo in defaultChannels)
            {
                if (!_morphChannels.ContainsKey(channelInfo.Id))
                {
                    var channel = new MorphChannel(channelInfo.Id)
                    {
                        Name = channelInfo.Name,
                        Category = channelInfo.Category,
                        Region = channelInfo.Region,
                        MinValue = 0,
                        MaxValue = 1,
                        DefaultValue = 0,
                        IsActive = true,
                        CreatedDate = DateTime.UtcNow;
                    };

                    _morphChannels.Add(channelInfo.Id, channel);
                }
            }
        }

        /// <summary>
        /// Morph driver'larını başlatır;
        /// </summary>
        private void InitializeMorphDrivers()
        {
            // Temel driver'lar;
            var defaultDrivers = new[]
            {
                new { Id = "time", Type = DriverType.Time, Channel = "auto_blink" },
                new { Id = "random", Type = DriverType.Random, Channel = "micro_movements" },
                new { Id = "breathing", Type = DriverType.Breathing, Channel = "chest_movement" }
            };

            foreach (var driverInfo in defaultDrivers)
            {
                var driver = new MorphDriver(driverInfo.Id, driverInfo.Type);
                _morphDrivers.Add(driverInfo.Id, driver);

                // İlgili kanala bağla;
                if (_morphChannels.TryGetValue(driverInfo.Channel, out var channel))
                {
                    channel.Drivers.Add(driverInfo.Id);
                }
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        /// <summary>
        /// Kaynakları serbest bırakır;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Yönetilen kaynakları serbest bırak;
                ShutdownAsync().Wait();

                _processingCancellation?.Dispose();

                lock (_lockObject)
                {
                    _morphTargets?.Clear();
                    _morphChannels?.Clear();
                    _morphPresets?.Clear();
                    _activeCompositions?.Clear();
                    _morphDrivers?.Clear();
                }

                _morphCache?.Dispose();
            }

            _disposed = true;
        }

        ~MorphEngine()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Morph target;
    /// </summary>
    public class MorphTarget;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MorphTargetData Data { get; set; }
        public MorphCategory Category { get; set; }
        public MorphRegion Region { get; set; }
        public float InfluenceRange { get; set; }
        public MorphSymmetry SymmetryMode { get; set; }
        public OptimizationLevel OptimizationLevel { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime LastOptimized { get; set; }
        public int Version { get; set; }

        public int VertexCount => Data?.BaseVertices?.Length / 3 ?? 0;
        public long EstimatedSize => (Data?.BaseVertices?.Length * sizeof(float) ?? 0) +
                                    (Data?.TargetVertices?.Length * sizeof(float) ?? 0) +
                                    (Data?.Normals?.Length * sizeof(float) ?? 0);

        public MorphTarget(string id, MorphTargetData data)
        {
            Id = id;
            Name = id;
            Data = data;
            Category = MorphCategory.Custom;
            Region = MorphRegion.Any;
            InfluenceRange = 1.0f;
            SymmetryMode = MorphSymmetry.None;
            OptimizationLevel = OptimizationLevel.None;
            CreatedDate = DateTime.UtcNow;
            LastModified = DateTime.UtcNow;
            Version = 1;
        }

        public MorphTarget Clone()
        {
            return new MorphTarget(Id, Data?.Clone())
            {
                Name = Name,
                Category = Category,
                Region = Region,
                InfluenceRange = InfluenceRange,
                SymmetryMode = SymmetryMode,
                OptimizationLevel = OptimizationLevel,
                CreatedDate = CreatedDate,
                LastModified = LastModified,
                LastOptimized = LastOptimized,
                Version = Version;
            };
        }
    }

    /// <summary>
    /// Morph target verisi;
    /// </summary>
    public class MorphTargetData;
    {
        public float[] BaseVertices { get; set; }
        public float[] TargetVertices { get; set; }
        public float[] Normals { get; set; }
        public int[] Indices { get; set; }
        public float[] UVs { get; set; }

        public MorphTargetData Clone()
        {
            return new MorphTargetData;
            {
                BaseVertices = BaseVertices?.ToArray(),
                TargetVertices = TargetVertices?.ToArray(),
                Normals = Normals?.ToArray(),
                Indices = Indices?.ToArray(),
                UVs = UVs?.ToArray()
            };
        }
    }

    /// <summary>
    /// Morph kanalı;
    /// </summary>
    public class MorphChannel;
    {
        public string Id { get; }
        public string Name { get; set; }
        public MorphCategory Category { get; set; }
        public MorphRegion Region { get; set; }
        public BlendMode BlendMode { get; set; }
        public float Influence { get; set; }
        public float MinValue { get; set; }
        public float MaxValue { get; set; }
        public float DefaultValue { get; set; }
        public float CurrentValue { get; set; }
        public bool IsActive { get; set; }
        public bool IsLocked { get; set; }
        public List<MorphChannelTarget> Targets { get; private set; }
        public List<string> Drivers { get; private set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastUpdate { get; set; }

        public MorphChannel(string id)
        {
            Id = id;
            Name = id;
            Category = MorphCategory.Custom;
            Region = MorphRegion.Any;
            BlendMode = BlendMode.Additive;
            Influence = 1.0f;
            MinValue = 0.0f;
            MaxValue = 1.0f;
            DefaultValue = 0.0f;
            CurrentValue = 0.0f;
            IsActive = true;
            IsLocked = false;
            Targets = new List<MorphChannelTarget>();
            Drivers = new List<string>();
            CreatedDate = DateTime.UtcNow;
            LastUpdate = DateTime.UtcNow;
        }

        public void AddTarget(MorphChannelTarget target)
        {
            Targets.Add(target);
        }

        public bool RemoveTarget(string targetId)
        {
            return Targets.RemoveAll(t => t.Target.Id == targetId) > 0;
        }

        public MorphChannel Clone()
        {
            var clone = new MorphChannel(Id)
            {
                Name = Name,
                Category = Category,
                Region = Region,
                BlendMode = BlendMode,
                Influence = Influence,
                MinValue = MinValue,
                MaxValue = MaxValue,
                DefaultValue = DefaultValue,
                CurrentValue = CurrentValue,
                IsActive = IsActive,
                IsLocked = IsLocked,
                CreatedDate = CreatedDate,
                LastUpdate = LastUpdate;
            };

            clone.Targets.AddRange(Targets.Select(t => t.Clone()));
            clone.Drivers.AddRange(Drivers);

            return clone;
        }
    }

    /// <summary>
    /// Morph kanal target'ı;
    /// </summary>
    public class MorphChannelTarget;
    {
        public MorphTarget Target { get; }
        public float Weight { get; set; }
        public float Influence { get; set; }

        public MorphChannelTarget(MorphTarget target, float weight)
        {
            Target = target;
            Weight = weight;
            Influence = 1.0f;
        }

        public MorphChannelTarget Clone()
        {
            return new MorphChannelTarget(Target.Clone(), Weight)
            {
                Influence = Influence;
            };
        }
    }

    /// <summary>
    /// Morph kompozisyonu;
    /// </summary>
    public class MorphComposition;
    {
        public string Id { get; }
        public string Name { get; set; }
        public string Description { get; set; }
        public MorphCategory Category { get; set; }
        public BlendMode BlendMode { get; set; }
        public List<MorphCompositionChannel> Channels { get; private set; }
        public DateTime CreatedDate { get; set; }

        public MorphComposition(string id)
        {
            Id = id;
            Name = id;
            Category = MorphCategory.Composition;
            BlendMode = BlendMode.Additive;
            Channels = new List<MorphCompositionChannel>();
            CreatedDate = DateTime.UtcNow;
        }

        public void AddChannel(MorphChannel channel, float weight)
        {
            Channels.Add(new MorphCompositionChannel(channel, weight));
        }

        public MorphComposition Clone()
        {
            var clone = new MorphComposition(Id)
            {
                Name = Name,
                Description = Description,
                Category = Category,
                BlendMode = BlendMode,
                CreatedDate = CreatedDate;
            };

            clone.Channels.AddRange(Channels.Select(c => c.Clone()));

            return clone;
        }
    }

    /// <summary>
    /// Morph kompozisyon kanalı;
    /// </summary>
    public class MorphCompositionChannel;
    {
        public MorphChannel Channel { get; }
        public float Weight { get; set; }

        public MorphCompositionChannel(MorphChannel channel, float weight)
        {
            Channel = channel;
            Weight = weight;
        }

        public MorphCompositionChannel Clone()
        {
            return new MorphCompositionChannel(Channel.Clone(), Weight);
        }
    }

    /// <summary>
    /// Morph preset;
    /// </summary>
    public class MorphPreset;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public MorphCategory Category { get; set; }
        public Dictionary<string, float> ChannelValues { get; set; }
        public DateTime CreatedDate { get; set; }

        public MorphPreset(string id)
        {
            Id = id;
            Name = id;
            ChannelValues = new Dictionary<string, float>();
            CreatedDate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Morph driver;
    /// </summary>
    public class MorphDriver;
    {
        public string Id { get; }
        public DriverType Type { get; }
        public float Value { get; private set; }
        public float Weight { get; set; }
        public Func<float, float, float, float> BlendFunction { get; set; }

        public MorphDriver(string id, DriverType type)
        {
            Id = id;
            Type = type;
            Weight = 1.0f;
            BlendFunction = (current, driver, weight) => current + driver * weight;
        }

        public async Task<float> GetValueAsync()
        {
            await UpdateAsync();
            return Value;
        }

        public async Task UpdateAsync()
        {
            Value = Type switch;
            {
                DriverType.Time => (float)DateTime.UtcNow.Second / 60.0f,
                DriverType.Random => (float)new Random().NextDouble(),
                DriverType.Breathing => (float)Math.Sin(DateTime.UtcNow.Second * Math.PI / 30.0f),
                _ => 0.0f;
            };

            await Task.CompletedTask;
        }

        public async Task UpdateFromChannelAsync(float channelValue)
        {
            // Channel değerine göre driver güncelleme;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Morph yapılandırması;
    /// </summary>
    public class MorphConfiguration;
    {
        public int TargetFPS { get; set; } = 60;
        public int MaxActiveChannels { get; set; } = 50;
        public int MaxMorphTargets { get; set; } = 1000;
        public CacheSettings CacheSettings { get; set; } = new CacheSettings();
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public bool EnableAsyncProcessing { get; set; } = true;
        public int ThreadPoolSize { get; set; } = Environment.ProcessorCount;
        public int MaxUpdateThreads { get; set; } = 4;
        public bool EnableCompression { get; set; } = true;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Balanced;
    }

    /// <summary>
    /// Cache ayarları;
    /// </summary>
    public class CacheSettings;
    {
        public long MaxCacheSize { get; set; } = 1024 * 1024 * 100; // 100 MB;
        public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Performans ayarları;
    /// </summary>
    public class MorphPerformanceSettings;
    {
        public OptimizationLevel TargetOptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public OptimizationLevel ChannelOptimizationLevel { get; set; } = OptimizationLevel.Low;
        public OptimizationLevel CacheOptimizationLevel { get; set; } = OptimizationLevel.High;
        public bool EnableFrameRateLimiting { get; set; } = true;
        public int TargetFrameRate { get; set; } = 60;
        public bool EnableMemoryMonitoring { get; set; } = true;
        public long MemoryWarningThreshold { get; set; } = 1024 * 1024 * 500; // 500 MB;
        public long MemoryCriticalThreshold { get; set; } = 1024 * 1024 * 800; // 800 MB;
        public bool EnablePerformanceLogging { get; set; } = true;
        public TimeSpan LoggingInterval { get; set; } = TimeSpan.FromMinutes(5);
        public bool EnableDiagnostics { get; set; } = true;
        public TimeSpan DiagnosticsInterval { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Performans metrikleri;
    /// </summary>
    public class MorphPerformanceMetrics;
    {
        public int FrameCount { get; set; }
        public float TotalFrameTime { get; set; }
        public float AverageFrameTime { get; set; }
        public float MaxFrameTime { get; set; }
        public float MinFrameTime { get; set; } = float.MaxValue;
        public float CurrentFPS { get; set; }
        public float AverageFPS { get; set; }
        public float P95FrameTime { get; set; }
        public float P99FrameTime { get; set; }
        public int ChannelsApplied { get; set; }
        public int FullApplyCalls { get; set; }
        public long TotalVerticesAffected { get; set; }

        public void Reset()
        {
            FrameCount = 0;
            TotalFrameTime = 0;
            AverageFrameTime = 0;
            MaxFrameTime = 0;
            MinFrameTime = float.MaxValue;
            CurrentFPS = 0;
            AverageFPS = 0;
            P95FrameTime = 0;
            P99FrameTime = 0;
            ChannelsApplied = 0;
            FullApplyCalls = 0;
            TotalVerticesAffected = 0;
        }
    }

    /// <summary>
    /// Morph engine durum raporu;
    /// </summary>
    public class MorphEngineStatus;
    {
        public DateTime ReportTime { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsProcessing { get; set; }
        public int TotalMorphTargets { get; set; }
        public int ActiveChannels { get; set; }
        public int TotalChannels { get; set; }
        public int ActiveCompositions { get; set; }
        public int TotalPresets { get; set; }
        public Dictionary<MorphRegion, int> RegionDistribution { get; set; } = new Dictionary<MorphRegion, int>();
        public Dictionary<MorphCategory, int> CategoryDistribution { get; set; } = new Dictionary<MorphCategory, int>();
        public MorphCacheStatistics CacheStatistics { get; set; }
        public MorphPerformanceMetrics PerformanceMetrics { get; set; }
        public long MemoryUsage { get; set; }
        public float AverageFrameTime { get; set; }
        public float MaxFrameTime { get; set; }
    }

    /// <summary>
    /// Diagnostic sonucu;
    /// </summary>
    public class DiagnosticResult;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Passed { get; set; }
        public List<DiagnosticTestResult> Tests { get; set; }
    }

    /// <summary>
    /// Diagnostic test sonucu;
    /// </summary>
    public class DiagnosticTestResult;
    {
        public string TestName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Passed { get; set; }
        public string Message { get; set; }
        public Exception Error { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Morph kategori;
    /// </summary>
    public enum MorphCategory;
    {
        Expression,
        Phoneme,
        Emotion,
        Viseme,
        Custom,
        Combined,
        Interpolated,
        Composition;
    }

    /// <summary>
    /// Morph bölge;
    /// </summary>
    public enum MorphRegion;
    {
        Any,
        FullFace,
        Brow,
        Eye,
        Nose,
        Cheek,
        Mouth,
        Jaw,
        Tongue,
        Ear,
        Head,
        Neck;
    }

    /// <summary>
    /// Morph symmetry
    /// </summary>
    public enum MorphSymmetry
    {
        None,
        LeftToRight,
        RightToLeft,
        Both,
        Radial;
    }

    /// <summary>
    /// Blend mode;
    /// </summary>
    public enum BlendMode;
    {
        Additive,
        Overlay,
        Multiply,
        Screen,
        Difference,
        Exclusion;
    }

    /// <summary>
    /// Optimization level;
    /// </summary>
    public enum OptimizationLevel;
    {
        None,
        Low,
        Medium,
        High,
        Ultra;
    }

    /// <summary>
    /// Compression level;
    /// </summary>
    public enum CompressionLevel;
    {
        None,
        Fast,
        Balanced,
        High,
        Maximum;
    }

    /// <summary>
    /// Driver tipi;
    /// </summary>
    public enum DriverType;
    {
        Time,
        Random,
        Breathing,
        Physics,
        Audio,
        Gameplay,
        AI,
        UserInput;
    }

    /// <summary>
    /// Expression tipi;
    /// </summary>
    public enum ExpressionType;
    {
        Neutral,
        Happy,
        Sad,
        Angry,
        Surprised,
        Fear,
        Disgust,
        Contempt;
    }

    /// <summary>
    /// Phoneme tipi;
    /// </summary>
    public enum PhonemeType;
    {
        AI,
        EE,
        O,
        U,
        FV,
        MBP,
        L,
        R,
        THD,
        SZ,
        SHCH,
        KG;
    }

    /// <summary>
    /// Interpolation method;
    /// </summary>
    public enum InterpolationMethod;
    {
        Linear,
        Cubic,
        Hermite;
    }

    /// <summary>
    /// Mirror axis;
    /// </summary>
    public enum MirrorAxis;
    {
        X,
        Y,
        Z;
    }

    /// <summary>
    /// Easing function;
    /// </summary>
    public enum EasingFunction;
    {
        Linear,
        EaseInQuad,
        EaseOutQuad,
        EaseInOutQuad,
        EaseInCubic,
        EaseOutCubic,
        EaseInOutCubic;
    }

    /// <summary>
    /// Export format;
    /// </summary>
    public enum ExportFormat;
    {
        NEDA,
        FBX,
        OBJ,
        BlendShape,
        JSON;
    }

    /// <summary>
    /// Import format;
    /// </summary>
    public enum ImportFormat;
    {
        AutoDetect,
        NEDA,
        FBX,
        OBJ,
        BlendShape,
        JSON,
        Unknown;
    }

    /// <summary>
    /// Modification tipi;
    /// </summary>
    public enum ModificationType;
    {
        Scale,
        Translate,
        Rotate,
        Noise,
        Smooth;
    }

    // Options classes;
    public class MorphTargetOptions { /* ... */ }
    public class MorphTargetUpdate { /* ... */ }
    public class CloneOptions { /* ... */ }
    public class CombineOptions { /* ... */ }
    public class MorphChannelOptions { /* ... */ }
    public class CompositionOptions { /* ... */ }
    public class PresetSaveOptions { /* ... */ }
    public class ExpressionOptions { /* ... */ }
    public class PhonemeOptions { /* ... */ }
    public class InterpolationOptions { /* ... */ }
    public class MirrorOptions { /* ... */ }
    public class SmoothOptions { /* ... */ }
    public class NormalizeOptions { /* ... */ }
    public class DisplacementOptions { /* ... */ }
    public class NormalMapOptions { /* ... */ }
    public class OptimizationOptions { /* ... */ }
    public class DiagnosticOptions { /* ... */ }
    public class MorphModification { /* ... */ }

    // Cache classes;
    public class MorphCache : IDisposable { /* ... */ }
    public class MorphCacheStatistics { /* ... */ }

    // Export/Import classes;
    public class MorphExport { /* ... */ }
    public class ChannelsExport { /* ... */ }

    /// <summary>
    /// MorphEngine özel istisnası;
    /// </summary>
    public class MorphEngineException : Exception
    {
        public MorphEngineException(string message) : base(message) { }
        public MorphEngineException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
