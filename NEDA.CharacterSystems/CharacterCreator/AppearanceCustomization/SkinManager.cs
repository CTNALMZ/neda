using NEDA.AI.NaturalLanguage;
using NEDA.Animation.SequenceEditor.CameraAnimation;
using NEDA.CharacterSystems.CharacterCreator.MorphTargets;
using NEDA.ContentCreation.TextureCreation.MaterialGenerator;
using NEDA.Core.Common;
using NEDA.Core.Configuration;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.RenderManager;
using NEDA.EngineIntegration.Unreal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.CharacterCreator.AppearanceCustomization;
{
    /// <summary>
    /// Karakter cilt yönetim sistemi;
    /// Ten rengi, doku, özellikler, yaşlanma etkileri ve görünüm özelleştirmelerini yönetir;
    /// </summary>
    public class SkinManager : IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private readonly MorphEngine _morphEngine;
        private readonly MaterialCreator _materialCreator;
        private readonly RenderEngine _renderEngine;

        private Dictionary<string, SkinProfile> _skinProfiles;
        private Dictionary<string, SkinPreset> _skinPresets;
        private Dictionary<string, SkinMaterial> _skinMaterials;
        private Dictionary<string, List<SkinFeature>> _skinFeatures;

        private SkinConfiguration _configuration;
        private SkinQualitySettings _qualitySettings;
        private SkinShaderLibrary _shaderLibrary;

        private bool _isInitialized;
        private bool _isRendering;
        private readonly object _lockObject = new object();

        // Skin cache for performance;
        private Dictionary<string, CachedSkinData> _skinCache;
        private Dictionary<string, DateTime> _cacheTimestamps;

        /// <summary>
        /// Yönetilen skin profili sayısı;
        /// </summary>
        public int ManagedSkinProfiles => _skinProfiles?.Count ?? 0;

        /// <summary>
        /// Mevcut skin preset sayısı;
        /// </summary>
        public int AvailablePresets => _skinPresets?.Count ?? 0;

        /// <summary>
        /// Skin kalite ayarları;
        /// </summary>
        public SkinQualitySettings QualitySettings => _qualitySettings;

        /// <summary>
        /// Skin yönetiminin aktif olup olmadığı;
        /// </summary>
        public bool IsActive => _isInitialized && _isRendering;

        #endregion;

        #region Constructors;

        /// <summary>
        /// SkinManager yapıcı metodu;
        /// </summary>
        public SkinManager(ILogger logger, AppConfig config, MorphEngine morphEngine,
                          MaterialCreator materialCreator, RenderEngine renderEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _morphEngine = morphEngine ?? throw new ArgumentNullException(nameof(morphEngine));
            _materialCreator = materialCreator ?? throw new ArgumentNullException(nameof(materialCreator));
            _renderEngine = renderEngine ?? throw new ArgumentNullException(nameof(renderEngine));

            InitializeCollections();
            _configuration = new SkinConfiguration();
            _qualitySettings = new SkinQualitySettings();
            _shaderLibrary = new SkinShaderLibrary();
            _isInitialized = false;
            _isRendering = false;
        }

        private void InitializeCollections()
        {
            _skinProfiles = new Dictionary<string, SkinProfile>();
            _skinPresets = new Dictionary<string, SkinPreset>();
            _skinMaterials = new Dictionary<string, SkinMaterial>();
            _skinFeatures = new Dictionary<string, List<SkinFeature>>();
            _skinCache = new Dictionary<string, CachedSkinData>();
            _cacheTimestamps = new Dictionary<string, DateTime>();
        }

        #endregion;

        #region Public Methods - Initialization;

        /// <summary>
        /// SkinManager'ı başlatır ve yapılandırır;
        /// </summary>
        public async Task InitializeAsync(SkinConfiguration configuration = null)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("SkinManager zaten başlatılmış.");
                return;
            }

            try
            {
                _logger.LogInformation("SkinManager başlatılıyor...");

                // Yapılandırmayı uygula;
                if (configuration != null)
                {
                    _configuration = configuration;
                }
                else;
                {
                    await LoadConfigurationAsync();
                }

                // Skin kalite ayarlarını yükle;
                await LoadQualitySettingsAsync();

                // Skin shader kütüphanesini yükle;
                await LoadShaderLibraryAsync();

                // Varsayılan skin materyallerini yükle;
                await LoadDefaultSkinMaterialsAsync();

                // Skin presetlerini yükle;
                await LoadSkinPresetsAsync();

                // Skin özelliklerini yükle;
                await LoadSkinFeaturesAsync();

                // Cache sistemini başlat;
                InitializeCacheSystem();

                _isInitialized = true;
                _isRendering = true;

                _logger.LogInformation($"SkinManager başarıyla başlatıldı. {_skinMaterials.Count} materyal, {_skinPresets.Count} preset yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"SkinManager başlatma hatası: {ex.Message}", ex);
                throw new SkinManagerException("SkinManager başlatma hatası", ex);
            }
        }

        /// <summary>
        /// SkinManager'ı durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                _isRendering = false;

                // Cache'i temizle;
                ClearCache();

                // Render kaynaklarını serbest bırak;
                await ReleaseRenderResourcesAsync();

                _isInitialized = false;

                _logger.LogInformation("SkinManager durduruldu.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"SkinManager kapatma hatası: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Public Methods - Skin Profile Management;

        /// <summary>
        /// Yeni bir skin profili oluşturur;
        /// </summary>
        public SkinProfile CreateSkinProfile(string profileId, SkinBaseParameters baseParameters)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            if (baseParameters == null)
                throw new ArgumentNullException(nameof(baseParameters));

            lock (_lockObject)
            {
                if (_skinProfiles.ContainsKey(profileId))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' zaten mevcut.");
                }

                var profile = new SkinProfile(profileId, baseParameters)
                {
                    CreatedDate = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow,
                    QualityLevel = _qualitySettings.CurrentQuality;
                };

                // Varsayılan skin materyallerini ata;
                ApplyDefaultSkinMaterials(profile);

                // Varsayılan özellikleri ata;
                ApplyDefaultSkinFeatures(profile);

                _skinProfiles.Add(profileId, profile);

                _logger.LogDebug($"Yeni skin profili oluşturuldu: {profileId}");

                return profile;
            }
        }

        /// <summary>
        /// Mevcut bir skin profilini getirir;
        /// </summary>
        public SkinProfile GetSkinProfile(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                return profile.Clone();
            }
        }

        /// <summary>
        /// Skin profilini günceller;
        /// </summary>
        public void UpdateSkinProfile(string profileId, SkinProfileUpdate update)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            if (update == null)
                throw new ArgumentNullException(nameof(update));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                // Güncelleme işlemleri;
                if (update.BaseParameters != null)
                {
                    profile.BaseParameters = update.BaseParameters;
                }

                if (update.SkinTone != null)
                {
                    profile.SkinTone = update.SkinTone.Value;
                }

                if (update.AgeParameters != null)
                {
                    profile.AgeParameters = update.AgeParameters;
                }

                if (update.HealthParameters != null)
                {
                    profile.HealthParameters = update.HealthParameters;
                }

                if (update.MaterialOverrides != null)
                {
                    foreach (var materialOverride in update.MaterialOverrides)
                    {
                        profile.SetMaterialOverride(materialOverride.Key, materialOverride.Value);
                    }
                }

                if (update.FeatureOverrides != null)
                {
                    foreach (var featureOverride in update.FeatureOverrides)
                    {
                        profile.SetFeatureOverride(featureOverride.Key, featureOverride.Value);
                    }
                }

                profile.LastModified = DateTime.UtcNow;
                profile.Version++;

                // Cache'i geçersiz kıl;
                InvalidateCache(profileId);

                _logger.LogDebug($"Skin profili güncellendi: {profileId} (v{profile.Version})");
            }
        }

        /// <summary>
        /// Skin profilini siler;
        /// </summary>
        public bool DeleteSkinProfile(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            lock (_lockObject)
            {
                bool removed = _skinProfiles.Remove(profileId);

                if (removed)
                {
                    // Cache'i temizle;
                    InvalidateCache(profileId);

                    _logger.LogDebug($"Skin profili silindi: {profileId}");
                }

                return removed;
            }
        }

        /// <summary>
        /// Tüm skin profillerini listeler;
        /// </summary>
        public List<SkinProfileInfo> ListSkinProfiles()
        {
            lock (_lockObject)
            {
                return _skinProfiles.Values;
                    .Select(p => new SkinProfileInfo;
                    {
                        ProfileId = p.ProfileId,
                        Name = p.Name,
                        CreatedDate = p.CreatedDate,
                        LastModified = p.LastModified,
                        Version = p.Version,
                        SkinTone = p.SkinTone,
                        Age = p.AgeParameters?.Age ?? 0,
                        QualityLevel = p.QualityLevel;
                    })
                    .ToList();
            }
        }

        #endregion;

        #region Public Methods - Skin Generation;

        /// <summary>
        /// Skin profilinden render verisi oluşturur;
        /// </summary>
        public async Task<SkinRenderData> GenerateSkinRenderDataAsync(string profileId,
            RenderQuality quality = RenderQuality.High, bool forceRegenerate = false)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            // Cache kontrolü;
            if (!forceRegenerate)
            {
                var cachedData = GetCachedSkinData(profileId, quality);
                if (cachedData != null)
                {
                    _logger.LogDebug($"Cache'ten skin verisi getirildi: {profileId}");
                    return cachedData;
                }
            }

            try
            {
                SkinProfile profile;
                lock (_lockObject)
                {
                    if (!_skinProfiles.TryGetValue(profileId, out profile))
                    {
                        throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                    }
                }

                _logger.LogInformation($"Skin render verisi oluşturuluyor: {profileId} (Quality: {quality})");

                // Skin render işlem hattı;
                var renderData = new SkinRenderData;
                {
                    ProfileId = profileId,
                    Quality = quality,
                    GeneratedTime = DateTime.UtcNow;
                };

                // 1. Base skin texture oluştur;
                var baseTexture = await GenerateBaseSkinTextureAsync(profile, quality);
                renderData.BaseTexture = baseTexture;

                // 2. Normal map oluştur;
                var normalMap = await GenerateNormalMapAsync(profile, baseTexture, quality);
                renderData.NormalMap = normalMap;

                // 3. Specular map oluştur;
                var specularMap = await GenerateSpecularMapAsync(profile, baseTexture, quality);
                renderData.SpecularMap = specularMap;

                // 4. Roughness map oluştur;
                var roughnessMap = await GenerateRoughnessMapAsync(profile, baseTexture, quality);
                renderData.RoughnessMap = roughnessMap;

                // 5. Displacement map oluştur;
                var displacementMap = await GenerateDisplacementMapAsync(profile, quality);
                renderData.DisplacementMap = displacementMap;

                // 6. Özellik tekstürleri oluştur;
                var featureTextures = await GenerateFeatureTexturesAsync(profile, quality);
                renderData.FeatureTextures = featureTextures;

                // 7. Material parametrelerini hesapla;
                var materialParams = CalculateMaterialParameters(profile);
                renderData.MaterialParameters = materialParams;

                // 8. Shader parametrelerini hazırla;
                var shaderParams = PrepareShaderParameters(profile, materialParams);
                renderData.ShaderParameters = shaderParams;

                // 9. Render mesh için vertex data hazırla;
                var vertexData = await PrepareVertexDataAsync(profile, displacementMap);
                renderData.VertexData = vertexData;

                // Cache'e kaydet;
                CacheSkinData(profileId, quality, renderData);

                _logger.LogDebug($"Skin render verisi oluşturuldu: {profileId} ({renderData.TextureCount} texture)");

                return renderData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Skin render verisi oluşturma hatası: {ex.Message}", ex);
                throw new SkinManagerException("Skin render verisi oluşturma hatası", ex);
            }
        }

        /// <summary>
        /// Real-time skin preview oluşturur;
        /// </summary>
        public async Task<SkinPreview> GenerateSkinPreviewAsync(string profileId,
            PreviewSettings settings = null)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            settings ??= new PreviewSettings();

            try
            {
                var profile = GetSkinProfile(profileId);

                // Düşük kalitede render data oluştur;
                var renderData = await GenerateSkinRenderDataAsync(profileId, RenderQuality.Low);

                // Preview texture oluştur;
                var previewTexture = await GeneratePreviewTextureAsync(renderData, settings);

                // Preview mesh hazırla;
                var previewMesh = await GeneratePreviewMeshAsync(renderData, settings);

                var preview = new SkinPreview;
                {
                    ProfileId = profileId,
                    PreviewTexture = previewTexture,
                    PreviewMesh = previewMesh,
                    RenderData = renderData,
                    GeneratedTime = DateTime.UtcNow,
                    Settings = settings;
                };

                return preview;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Skin preview oluşturma hatası: {ex.Message}", ex);
                throw new SkinManagerException("Skin preview oluşturma hatası", ex);
            }
        }

        /// <summary>
        /// Skin presetini uygular;
        /// </summary>
        public SkinProfile ApplySkinPreset(string profileId, string presetId,
            float blendFactor = 1.0f, bool applyFeatures = true)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            if (string.IsNullOrWhiteSpace(presetId))
                throw new ArgumentException("Preset ID boş olamaz.", nameof(presetId));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                if (!_skinPresets.TryGetValue(presetId, out var preset))
                {
                    throw new SkinManagerException($"Skin preset '{presetId}' bulunamadı.");
                }

                // Preset uygula;
                var updatedProfile = ApplyPresetToProfile(profile, preset, blendFactor, applyFeatures);

                // Profili güncelle;
                _skinProfiles[profileId] = updatedProfile;

                // Cache'i geçersiz kıl;
                InvalidateCache(profileId);

                _logger.LogDebug($"Skin preset uygulandı: {profileId} -> {presetId} (Blend: {blendFactor})");

                return updatedProfile.Clone();
            }
        }

        /// <summary>
        /// Karıştırma ile yeni skin oluşturur;
        /// </summary>
        public SkinProfile BlendSkins(string profileId1, string profileId2,
            float blendFactor = 0.5f, string newProfileId = null)
        {
            if (string.IsNullOrWhiteSpace(profileId1) || string.IsNullOrWhiteSpace(profileId2))
                throw new ArgumentException("Profil ID'leri boş olamaz.");

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId1, out var profile1))
                {
                    throw new SkinManagerException($"Skin profili '{profileId1}' bulunamadı.");
                }

                if (!_skinProfiles.TryGetValue(profileId2, out var profile2))
                {
                    throw new SkinManagerException($"Skin profili '{profileId2}' bulunamadı.");
                }

                // Yeni profil ID'si oluştur;
                newProfileId ??= $"blended_{profileId1}_{profileId2}_{Guid.NewGuid():N}";

                // Skin karıştırma;
                var blendedProfile = BlendSkinProfiles(profile1, profile2, blendFactor, newProfileId);

                // Yeni profili ekle;
                _skinProfiles.Add(newProfileId, blendedProfile);

                _logger.LogDebug($"Skin karıştırma tamamlandı: {profileId1} + {profileId2} -> {newProfileId}");

                return blendedProfile.Clone();
            }
        }

        #endregion;

        #region Public Methods - Skin Features;

        /// <summary>
        /// Skin özelliği ekler;
        /// </summary>
        public void AddSkinFeature(string profileId, SkinFeature feature,
            float intensity = 1.0f, Vector2 position = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            if (feature == null)
                throw new ArgumentNullException(nameof(feature));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                // Özelliği profile ekle;
                var appliedFeature = new AppliedSkinFeature(feature, intensity, position);
                profile.AddFeature(appliedFeature);

                // Cache'i geçersiz kıl;
                InvalidateCache(profileId);

                _logger.LogDebug($"Skin özelliği eklendi: {profileId} -> {feature.FeatureType}");
            }
        }

        /// <summary>
        /// Skin özelliğini kaldırır;
        /// </summary>
        public bool RemoveSkinFeature(string profileId, string featureId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            if (string.IsNullOrWhiteSpace(featureId))
                throw new ArgumentException("Özellik ID boş olamaz.", nameof(featureId));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                bool removed = profile.RemoveFeature(featureId);

                if (removed)
                {
                    InvalidateCache(profileId);
                    _logger.LogDebug($"Skin özelliği kaldırıldı: {profileId} -> {featureId}");
                }

                return removed;
            }
        }

        /// <summary>
        /// Yaşlanma etkisi uygular;
        /// </summary>
        public SkinProfile ApplyAgingEffect(string profileId, int targetAge,
            AgingIntensity intensity = AgingIntensity.Natural)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                // Yaşlanma efektlerini uygula;
                var agedProfile = ApplyAgingToProfile(profile, targetAge, intensity);

                // Profili güncelle;
                _skinProfiles[profileId] = agedProfile;

                // Cache'i geçersiz kıl;
                InvalidateCache(profileId);

                _logger.LogDebug($"Yaşlanma efekti uygulandı: {profileId} -> {targetAge} yaş");

                return agedProfile.Clone();
            }
        }

        /// <summary>
        /// Sağlık durumu efektleri uygular;
        /// </summary>
        public SkinProfile ApplyHealthEffects(string profileId, HealthCondition condition,
            float severity = 0.5f)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                // Sağlık efektlerini uygula;
                var healthProfile = ApplyHealthToProfile(profile, condition, severity);

                // Profili güncelle;
                _skinProfiles[profileId] = healthProfile;

                // Cache'i geçersiz kıl;
                InvalidateCache(profileId);

                _logger.LogDebug($"Sağlık efekti uygulandı: {profileId} -> {condition}");

                return healthProfile.Clone();
            }
        }

        /// <summary>
        /// Çevresel etkileri uygular;
        /// </summary>
        public SkinProfile ApplyEnvironmentalEffects(string profileId,
            EnvironmentalCondition condition, float exposureTime)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            lock (_lockObject)
            {
                if (!_skinProfiles.TryGetValue(profileId, out var profile))
                {
                    throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                }

                // Çevresel efektleri uygula;
                var environmentalProfile = ApplyEnvironmentToProfile(profile, condition, exposureTime);

                // Profili güncelle;
                _skinProfiles[profileId] = environmentalProfile;

                // Cache'i geçersiz kıl;
                InvalidateCache(profileId);

                _logger.LogDebug($"Çevresel efekt uygulandı: {profileId} -> {condition}");

                return environmentalProfile.Clone();
            }
        }

        #endregion;

        #region Public Methods - Export & Import;

        /// <summary>
        /// Skin profilini dışa aktarır;
        /// </summary>
        public async Task<SkinExportData> ExportSkinProfileAsync(string profileId,
            ExportFormat format = ExportFormat.NEDA)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profil ID boş olamaz.", nameof(profileId));

            try
            {
                SkinProfile profile;
                lock (_lockObject)
                {
                    if (!_skinProfiles.TryGetValue(profileId, out profile))
                    {
                        throw new SkinManagerException($"Skin profili '{profileId}' bulunamadı.");
                    }
                }

                _logger.LogInformation($"Skin profili dışa aktarılıyor: {profileId} (Format: {format})");

                var exportData = new SkinExportData;
                {
                    ProfileId = profileId,
                    ExportFormat = format,
                    ExportTime = DateTime.UtcNow,
                    ProfileData = profile;
                };

                // Format'a göre dışa aktarım işlemi;
                switch (format)
                {
                    case ExportFormat.NEDA:
                        exportData.Data = ExportToNedaFormat(profile);
                        break;

                    case ExportFormat.UnrealEngine:
                        exportData.Data = await ExportToUnrealFormatAsync(profile);
                        break;

                    case ExportFormat.Unity:
                        exportData.Data = await ExportToUnityFormatAsync(profile);
                        break;

                    case ExportFormat.FBX:
                        exportData.Data = await ExportToFbxFormatAsync(profile);
                        break;

                    case ExportFormat.GLTF:
                        exportData.Data = await ExportToGltfFormatAsync(profile);
                        break;

                    case ExportFormat.JSON:
                        exportData.Data = ExportToJsonFormat(profile);
                        break;

                    default:
                        throw new SkinManagerException($"Desteklenmeyen export formatı: {format}");
                }

                _logger.LogDebug($"Skin profili dışa aktarıldı: {profileId} ({exportData.Data.Length} bytes)");

                return exportData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Skin profili export hatası: {ex.Message}", ex);
                throw new SkinManagerException("Skin profili export hatası", ex);
            }
        }

        /// <summary>
        /// Skin profilini içe aktarır;
        /// </summary>
        public async Task<SkinProfile> ImportSkinProfileAsync(byte[] importData,
            ImportFormat format = ImportFormat.AutoDetect, string profileId = null)
        {
            if (importData == null || importData.Length == 0)
                throw new ArgumentException("Import verisi boş olamaz.", nameof(importData));

            try
            {
                _logger.LogInformation("Skin profili içe aktarılıyor...");

                SkinProfile profile;
                ImportFormat detectedFormat = format;

                // Format tespiti;
                if (format == ImportFormat.AutoDetect)
                {
                    detectedFormat = DetectImportFormat(importData);
                }

                // Format'a göre içe aktarım işlemi;
                switch (detectedFormat)
                {
                    case ImportFormat.NEDA:
                        profile = ImportFromNedaFormat(importData);
                        break;

                    case ImportFormat.UnrealEngine:
                        profile = await ImportFromUnrealFormatAsync(importData);
                        break;

                    case ImportFormat.Unity:
                        profile = await ImportFromUnityFormatAsync(importData);
                        break;

                    case ImportFormat.FBX:
                        profile = await ImportFromFbxFormatAsync(importData);
                        break;

                    case ImportFormat.GLTF:
                        profile = await ImportFromGltfFormatAsync(importData);
                        break;

                    case ImportFormat.JSON:
                        profile = ImportFromJsonFormat(importData);
                        break;

                    default:
                        throw new SkinManagerException($"Desteklenmeyen import formatı: {detectedFormat}");
                }

                // Profil ID ataması;
                if (!string.IsNullOrWhiteSpace(profileId))
                {
                    profile.ProfileId = profileId;
                }

                // Profili kaydet;
                lock (_lockObject)
                {
                    if (_skinProfiles.ContainsKey(profile.ProfileId))
                    {
                        // Mevcutsa güncelle;
                        profile.LastModified = DateTime.UtcNow;
                        profile.Version++;
                        _skinProfiles[profile.ProfileId] = profile;
                    }
                    else;
                    {
                        // Yeni profil ekle;
                        profile.CreatedDate = DateTime.UtcNow;
                        profile.LastModified = DateTime.UtcNow;
                        _skinProfiles.Add(profile.ProfileId, profile);
                    }
                }

                _logger.LogDebug($"Skin profili içe aktarıldı: {profile.ProfileId}");

                return profile.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Skin profili import hatası: {ex.Message}", ex);
                throw new SkinManagerException("Skin profili import hatası", ex);
            }
        }

        #endregion;

        #region Private Methods - Core Skin Generation;

        /// <summary>
        /// Base skin texture oluşturur;
        /// </summary>
        private async Task<SkinTexture> GenerateBaseSkinTextureAsync(SkinProfile profile,
            RenderQuality quality)
        {
            var textureSize = GetTextureSizeForQuality(quality);

            var parameters = new BaseTextureParameters;
            {
                SkinTone = profile.SkinTone,
                Age = profile.AgeParameters?.Age ?? 25,
                Gender = profile.BaseParameters.Gender,
                Ethnicity = profile.BaseParameters.Ethnicity,
                TextureSize = textureSize,
                Quality = quality,
                IncludeFeatures = true,
                FeatureIntensity = 0.8f;
            };

            // Base color hesapla;
            var baseColor = CalculateBaseSkinColor(profile.SkinTone, profile.BaseParameters.Ethnicity);
            parameters.BaseColor = baseColor;

            // Age efektlerini ekle;
            if (profile.AgeParameters != null)
            {
                parameters.AgeEffects = CalculateAgeEffects(profile.AgeParameters);
            }

            // Health efektlerini ekle;
            if (profile.HealthParameters != null)
            {
                parameters.HealthEffects = CalculateHealthEffects(profile.HealthParameters);
            }

            // MaterialCreator ile texture oluştur;
            var texture = await _materialCreator.GenerateSkinTextureAsync(parameters);

            return new SkinTexture;
            {
                TextureType = TextureType.BaseColor,
                Width = textureSize,
                Height = textureSize,
                Data = texture.Data,
                Format = texture.Format,
                MipLevels = texture.MipLevels;
            };
        }

        /// <summary>
        /// Normal map oluşturur;
        /// </summary>
        private async Task<SkinTexture> GenerateNormalMapAsync(SkinProfile profile,
            SkinTexture baseTexture, RenderQuality quality)
        {
            var textureSize = GetTextureSizeForQuality(quality);

            var parameters = new NormalMapParameters;
            {
                BaseTexture = baseTexture.Data,
                TextureSize = textureSize,
                Intensity = 1.0f,
                Smoothness = profile.BaseParameters.SkinSmoothness,
                IncludePores = profile.BaseParameters.IncludeSkinPores,
                PoreSize = profile.BaseParameters.PoreSize,
                PoreIntensity = profile.BaseParameters.PoreIntensity;
            };

            // Özelliklere göre normal map ayarları;
            if (profile.Features.Any())
            {
                foreach (var feature in profile.Features)
                {
                    if (feature.Feature.NormalMapInfluence > 0)
                    {
                        parameters.FeatureNormalMaps.Add(feature.Feature.Id,
                            new FeatureNormalData;
                            {
                                Position = feature.Position,
                                Intensity = feature.Intensity * feature.Feature.NormalMapInfluence,
                                Scale = feature.Feature.NormalMapScale;
                            });
                    }
                }
            }

            var normalMap = await _materialCreator.GenerateNormalMapAsync(parameters);

            return new SkinTexture;
            {
                TextureType = TextureType.NormalMap,
                Width = textureSize,
                Height = textureSize,
                Data = normalMap.Data,
                Format = TextureFormat.RGBA16F,
                MipLevels = normalMap.MipLevels;
            };
        }

        /// <summary>
        /// Specular map oluşturur;
        /// </summary>
        private async Task<SkinTexture> GenerateSpecularMapAsync(SkinProfile profile,
            SkinTexture baseTexture, RenderQuality quality)
        {
            var textureSize = GetTextureSizeForQuality(quality);

            var parameters = new SpecularMapParameters;
            {
                BaseTexture = baseTexture.Data,
                TextureSize = textureSize,
                BaseSpecular = profile.BaseParameters.SkinSpecular,
                Oiliness = profile.BaseParameters.SkinOiliness,
                Moisture = profile.BaseParameters.SkinMoisture;
            };

            // Yaş ve sağlık faktörleri;
            if (profile.AgeParameters != null)
            {
                parameters.AgeFactor = profile.AgeParameters.Age / 100.0f;
                parameters.IncludeWrinkles = profile.AgeParameters.IncludeWrinkles;
            }

            var specularMap = await _materialCreator.GenerateSpecularMapAsync(parameters);

            return new SkinTexture;
            {
                TextureType = TextureType.SpecularMap,
                Width = textureSize,
                Height = textureSize,
                Data = specularMap.Data,
                Format = TextureFormat.R8,
                MipLevels = specularMap.MipLevels;
            };
        }

        /// <summary>
        /// Roughness map oluşturur;
        /// </summary>
        private async Task<SkinTexture> GenerateRoughnessMapAsync(SkinProfile profile,
            SkinTexture baseTexture, RenderQuality quality)
        {
            var textureSize = GetTextureSizeForQuality(quality);

            var parameters = new RoughnessMapParameters;
            {
                BaseTexture = baseTexture.Data,
                TextureSize = textureSize,
                BaseRoughness = profile.BaseParameters.SkinRoughness,
                SmoothnessVariation = profile.BaseParameters.SkinSmoothnessVariation;
            };

            // Özelliklere göre roughness;
            if (profile.Features.Any())
            {
                foreach (var feature in profile.Features)
                {
                    if (feature.Feature.AffectsRoughness)
                    {
                        parameters.FeatureRoughnessAdjustments.Add(feature.Feature.Id,
                            feature.Intensity * feature.Feature.RoughnessInfluence);
                    }
                }
            }

            var roughnessMap = await _materialCreator.GenerateRoughnessMapAsync(parameters);

            return new SkinTexture;
            {
                TextureType = TextureType.RoughnessMap,
                Width = textureSize,
                Height = textureSize,
                Data = roughnessMap.Data,
                Format = TextureFormat.R8,
                MipLevels = roughnessMap.MipLevels;
            };
        }

        /// <summary>
        /// Displacement map oluşturur;
        /// </summary>
        private async Task<SkinTexture> GenerateDisplacementMapAsync(SkinProfile profile,
            RenderQuality quality)
        {
            var textureSize = GetTextureSizeForQuality(quality);

            var parameters = new DisplacementMapParameters;
            {
                TextureSize = textureSize,
                BaseDisplacement = 0.0f,
                IncludeMicroDetails = quality >= RenderQuality.Medium;
            };

            // Morph target verilerini kullan;
            if (_morphEngine != null && profile.BaseParameters.MorphTargets.Any())
            {
                var morphData = await _morphEngine.GenerateDisplacementDataAsync(
                    profile.BaseParameters.MorphTargets, textureSize);
                parameters.MorphDisplacements = morphData;
            }

            // Özellik displacement'leri;
            if (profile.Features.Any())
            {
                foreach (var feature in profile.Features)
                {
                    if (feature.Feature.HasDisplacement)
                    {
                        parameters.FeatureDisplacements.Add(new FeatureDisplacement;
                        {
                            FeatureId = feature.Feature.Id,
                            Position = feature.Position,
                            Intensity = feature.Intensity,
                            DisplacementMap = feature.Feature.DisplacementMap;
                        });
                    }
                }
            }

            var displacementMap = await _materialCreator.GenerateDisplacementMapAsync(parameters);

            return new SkinTexture;
            {
                TextureType = TextureType.DisplacementMap,
                Width = textureSize,
                Height = textureSize,
                Data = displacementMap.Data,
                Format = TextureFormat.R16F,
                MipLevels = displacementMap.MipLevels;
            };
        }

        /// <summary>
        /// Özellik tekstürleri oluşturur;
        /// </summary>
        private async Task<Dictionary<string, SkinTexture>> GenerateFeatureTexturesAsync(
            SkinProfile profile, RenderQuality quality)
        {
            var featureTextures = new Dictionary<string, SkinTexture>();

            if (!profile.Features.Any())
                return featureTextures;

            var textureSize = GetTextureSizeForQuality(quality, true); // Daha küçük boyut;

            foreach (var appliedFeature in profile.Features)
            {
                var feature = appliedFeature.Feature;

                if (feature.RequiresSeparateTexture)
                {
                    var parameters = new FeatureTextureParameters;
                    {
                        FeatureType = feature.FeatureType,
                        Position = appliedFeature.Position,
                        Intensity = appliedFeature.Intensity,
                        TextureSize = textureSize,
                        BlendMode = feature.BlendMode,
                        Color = feature.Color,
                        Size = feature.Size;
                    };

                    var featureTexture = await _materialCreator.GenerateFeatureTextureAsync(parameters);

                    featureTextures.Add(feature.Id, new SkinTexture;
                    {
                        TextureType = TextureType.FeatureMask,
                        Width = textureSize,
                        Height = textureSize,
                        Data = featureTexture.Data,
                        Format = TextureFormat.RGBA8,
                        MipLevels = featureTexture.MipLevels;
                    });
                }
            }

            return featureTextures;
        }

        /// <summary>
        /// Material parametrelerini hesaplar;
        /// </summary>
        private SkinMaterialParameters CalculateMaterialParameters(SkinProfile profile)
        {
            var parameters = new SkinMaterialParameters;
            {
                SubsurfaceColor = CalculateSubsurfaceColor(profile.SkinTone),
                SubsurfaceRadius = CalculateSubsurfaceRadius(profile.BaseParameters),
                Specular = profile.BaseParameters.SkinSpecular,
                Roughness = profile.BaseParameters.SkinRoughness,
                Metallic = 0.0f, // Skin metallic değil;
                ClearCoat = profile.BaseParameters.SkinClearCoat,
                ClearCoatRoughness = profile.BaseParameters.SkinClearCoatRoughness,
                Anisotropy = profile.BaseParameters.SkinAnisotropy,
                Sheen = profile.BaseParameters.SkinSheen,
                SheenTint = profile.BaseParameters.SkinSheenTint,
                Transmission = profile.BaseParameters.SkinTransmission,
                Emission = Vector3.Zero,
                IOR = 1.38f, // Skin IOR değeri;
                ThinWalled = false;
            };

            // Age efektleri;
            if (profile.AgeParameters != null)
            {
                parameters = ApplyAgeToMaterialParameters(parameters, profile.AgeParameters);
            }

            // Health efektleri;
            if (profile.HealthParameters != null)
            {
                parameters = ApplyHealthToMaterialParameters(parameters, profile.HealthParameters);
            }

            return parameters;
        }

        /// <summary>
        /// Shader parametrelerini hazırlar;
        /// </summary>
        private SkinShaderParameters PrepareShaderParameters(SkinProfile profile,
            SkinMaterialParameters materialParams)
        {
            var shader = _shaderLibrary.GetShaderForProfile(profile);

            var parameters = new SkinShaderParameters;
            {
                ShaderName = shader.Name,
                ShaderPath = shader.Path,
                QualityLevel = profile.QualityLevel,
                UseSSS = shader.SupportsSSS && _qualitySettings.EnableSubsurfaceScattering,
                UseClearCoat = shader.SupportsClearCoat,
                UseAnisotropy = shader.SupportsAnisotropy,
                UseSheen = shader.SupportsSheen,
                UseTransmission = shader.SupportsTransmission,
                TessellationFactor = shader.SupportsTessellation ? _qualitySettings.TessellationFactor : 0,
                DisplacementScale = shader.SupportsDisplacement ? _qualitySettings.DisplacementScale : 0.0f,
                MaterialParameters = materialParams;
            };

            // Texture slot'ları;
            parameters.TextureSlots.Add("BaseColor", 0);
            parameters.TextureSlots.Add("Normal", 1);
            parameters.TextureSlots.Add("Specular", 2);
            parameters.TextureSlots.Add("Roughness", 3);
            parameters.TextureSlots.Add("Displacement", 4);
            parameters.TextureSlots.Add("AO", 5);

            // Feature texture slot'ları;
            int slotIndex = 6;
            foreach (var feature in profile.Features.Where(f => f.Feature.RequiresSeparateTexture))
            {
                parameters.TextureSlots.Add($"Feature_{feature.Feature.Id}", slotIndex++);
            }

            return parameters;
        }

        /// <summary>
        /// Vertex data hazırlar;
        /// </summary>
        private async Task<SkinVertexData> PrepareVertexDataAsync(SkinProfile profile,
            SkinTexture displacementMap)
        {
            var vertexData = new SkinVertexData;
            {
                VertexCount = 0,
                IndexCount = 0,
                HasNormals = true,
                HasTangents = true,
                HasUVs = true,
                HasColors = true;
            };

            // Base mesh al;
            var baseMesh = await GetBaseMeshForProfileAsync(profile);

            // Displacement uygula;
            if (displacementMap != null && _qualitySettings.EnableDisplacement)
            {
                vertexData = await ApplyDisplacementToMeshAsync(baseMesh, displacementMap);
            }
            else;
            {
                vertexData = ConvertMeshToVertexData(baseMesh);
            }

            // Morph targets uygula;
            if (profile.BaseParameters.MorphTargets.Any() && _morphEngine != null)
            {
                vertexData = await ApplyMorphTargetsAsync(vertexData, profile.BaseParameters.MorphTargets);
            }

            return vertexData;
        }

        #endregion;

        #region Private Methods - Helper Methods;

        /// <summary>
        /// Base skin color hesaplar;
        /// </summary>
        private Vector3 CalculateBaseSkinColor(SkinTone skinTone, Ethnicity ethnicity)
        {
            // Skin tone tablosu;
            var skinToneColors = new Dictionary<SkinTone, Vector3>
            {
                [SkinTone.Porcelain] = new Vector3(0.95f, 0.85f, 0.75f),
                [SkinTone.Fair] = new Vector3(0.90f, 0.75f, 0.65f),
                [SkinTone.Light] = new Vector3(0.85f, 0.70f, 0.60f),
                [SkinTone.Medium] = new Vector3(0.75f, 0.60f, 0.50f),
                [SkinTone.Olive] = new Vector3(0.70f, 0.55f, 0.45f),
                [SkinTone.Tan] = new Vector3(0.65f, 0.50f, 0.40f),
                [SkinTone.Brown] = new Vector3(0.55f, 0.40f, 0.30f),
                [SkinTone.Dark] = new Vector3(0.45f, 0.30f, 0.20f),
                [SkinTone.Ebony] = new Vector3(0.35f, 0.25f, 0.15f)
            };

            var baseColor = skinToneColors.ContainsKey(skinTone)
                ? skinToneColors[skinTone]
                : new Vector3(0.75f, 0.60f, 0.50f); // Varsayılan;

            // Ethnicity adjustments;
            switch (ethnicity)
            {
                case Ethnicity.Caucasian:
                    baseColor *= new Vector3(1.05f, 1.0f, 0.95f);
                    break;
                case Ethnicity.Asian:
                    baseColor *= new Vector3(1.0f, 0.95f, 0.85f);
                    break;
                case Ethnicity.African:
                    baseColor *= new Vector3(0.9f, 0.8f, 0.7f);
                    break;
                case Ethnicity.Hispanic:
                    baseColor *= new Vector3(0.95f, 0.85f, 0.75f);
                    break;
                case Ethnicity.MiddleEastern:
                    baseColor *= new Vector3(0.92f, 0.82f, 0.72f);
                    break;
            }

            return Vector3.Clamp(baseColor, Vector3.Zero, Vector3.One);
        }

        /// <summary>
        /// Subsurface color hesaplar;
        /// </summary>
        private Vector3 CalculateSubsurfaceColor(SkinTone skinTone)
        {
            // Subsurface colors (daha kırmızı tonlar)
            var subsurfaceColors = new Dictionary<SkinTone, Vector3>
            {
                [SkinTone.Porcelain] = new Vector3(0.95f, 0.70f, 0.65f),
                [SkinTone.Fair] = new Vector3(0.90f, 0.65f, 0.60f),
                [SkinTone.Light] = new Vector3(0.85f, 0.60f, 0.55f),
                [SkinTone.Medium] = new Vector3(0.75f, 0.50f, 0.45f),
                [SkinTone.Olive] = new Vector3(0.70f, 0.45f, 0.40f),
                [SkinTone.Tan] = new Vector3(0.65f, 0.40f, 0.35f),
                [SkinTone.Brown] = new Vector3(0.55f, 0.30f, 0.25f),
                [SkinTone.Dark] = new Vector3(0.45f, 0.20f, 0.15f),
                [SkinTone.Ebony] = new Vector3(0.35f, 0.15f, 0.10f)
            };

            return subsurfaceColors.ContainsKey(skinTone)
                ? subsurfaceColors[skinTone]
                : new Vector3(0.75f, 0.50f, 0.45f);
        }

        /// <summary>
        /// Subsurface radius hesaplar;
        /// </summary>
        private Vector3 CalculateSubsurfaceRadius(SkinBaseParameters parameters)
        {
            // Skin thickness based on parameters;
            float redRadius = 0.5f + parameters.SkinThickness * 0.5f;
            float greenRadius = 0.3f + parameters.SkinThickness * 0.3f;
            float blueRadius = 0.1f + parameters.SkinThickness * 0.1f;

            return new Vector3(redRadius, greenRadius, blueRadius);
        }

        /// <summary>
        /// Yaş efektlerini hesaplar;
        /// </summary>
        private AgeEffects CalculateAgeEffects(AgeParameters ageParams)
        {
            var age = ageParams.Age;
            var effects = new AgeEffects();

            // Wrinkle intensity;
            effects.WrinkleIntensity = Math.Clamp((age - 30) / 50.0f, 0.0f, 1.0f);

            // Age spots;
            effects.AgeSpotIntensity = Math.Clamp((age - 40) / 40.0f, 0.0f, 1.0f);
            effects.AgeSpotCount = (int)(effects.AgeSpotIntensity * 50);

            // Skin thinning;
            effects.SkinThinning = Math.Clamp((age - 50) / 30.0f, 0.0f, 1.0f);

            // Color changes;
            effects.ColorYellowing = Math.Clamp((age - 60) / 40.0f, 0.0f, 0.3f);
            effects.ColorDarkening = Math.Clamp((age - 40) / 60.0f, 0.0f, 0.2f);

            // Roughness increase;
            effects.RoughnessIncrease = Math.Clamp((age - 20) / 80.0f, 0.0f, 0.5f);

            // Specular decrease;
            effects.SpecularDecrease = Math.Clamp((age - 30) / 70.0f, 0.0f, 0.4f);

            return effects;
        }

        /// <summary>
        /// Sağlık efektlerini hesaplar;
        /// </summary>
        private HealthEffects CalculateHealthEffects(HealthParameters healthParams)
        {
            var effects = new HealthEffects();

            // Paleness based on health;
            effects.Paleness = healthParams.HealthLevel < 0.5f ? 1.0f - healthParams.HealthLevel * 2 : 0.0f;

            // Redness for fever/inflammation;
            effects.Redness = healthParams.HasFever ? 0.3f : 0.0f;
            effects.Redness += healthParams.InflammationLevel * 0.2f;

            // Yellowing for jaundice;
            effects.Yellowing = healthParams.HasJaundice ? 0.4f : 0.0f;

            // Blueness for poor circulation;
            effects.Blueness = healthParams.PoorCirculation ? 0.2f : 0.0f;

            // Sweat for fever;
            effects.SweatIntensity = healthParams.HasFever ? 0.5f : 0.0f;

            // Dryness;
            effects.Dryness = healthParams.SkinDryness;

            // Oiliness changes;
            effects.OilinessChange = healthParams.HealthLevel < 0.3f ? -0.3f : 0.0f;

            return effects;
        }

        /// <summary>
        /// Kaliteye göre texture boyutu belirler;
        /// </summary>
        private int GetTextureSizeForQuality(RenderQuality quality, bool isFeatureTexture = false)
        {
            if (isFeatureTexture)
            {
                return quality switch;
                {
                    RenderQuality.Low => 256,
                    RenderQuality.Medium => 512,
                    RenderQuality.High => 1024,
                    RenderQuality.Ultra => 2048,
                    _ => 512;
                };
            }

            return quality switch;
            {
                RenderQuality.Low => 512,
                RenderQuality.Medium => 1024,
                RenderQuality.High => 2048,
                RenderQuality.Ultra => 4096,
                _ => 1024;
            };
        }

        #endregion;

        #region Private Methods - Cache Management;

        /// <summary>
        /// Cache sistemini başlatır;
        /// </summary>
        private void InitializeCacheSystem()
        {
            _skinCache.Clear();
            _cacheTimestamps.Clear();

            // Cache cleanup task başlat;
            Task.Run(async () => await CacheCleanupTaskAsync());
        }

        /// <summary>
        /// Cache'ten skin verisi getirir;
        /// </summary>
        private SkinRenderData GetCachedSkinData(string profileId, RenderQuality quality)
        {
            var cacheKey = $"{profileId}_{quality}";

            lock (_lockObject)
            {
                if (_skinCache.TryGetValue(cacheKey, out var cachedData))
                {
                    // Cache süresi kontrolü;
                    if (_cacheTimestamps.TryGetValue(cacheKey, out var timestamp))
                    {
                        var age = DateTime.UtcNow - timestamp;
                        if (age < _configuration.CacheDuration)
                        {
                            return cachedData;
                        }
                        else;
                        {
                            // Expired cache'i temizle;
                            _skinCache.Remove(cacheKey);
                            _cacheTimestamps.Remove(cacheKey);
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Skin verisini cache'e kaydeder;
        /// </summary>
        private void CacheSkinData(string profileId, RenderQuality quality, SkinRenderData data)
        {
            var cacheKey = $"{profileId}_{quality}";

            lock (_lockObject)
            {
                _skinCache[cacheKey] = data;
                _cacheTimestamps[cacheKey] = DateTime.UtcNow;

                // Cache limit kontrolü;
                if (_skinCache.Count > _configuration.MaxCacheSize)
                {
                    CleanupOldestCacheEntries();
                }
            }
        }

        /// <summary>
        /// Cache'i geçersiz kılar;
        /// </summary>
        private void InvalidateCache(string profileId)
        {
            lock (_lockObject)
            {
                var keysToRemove = _skinCache.Keys;
                    .Where(k => k.StartsWith(profileId + "_"))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _skinCache.Remove(key);
                    _cacheTimestamps.Remove(key);
                }
            }
        }

        /// <summary>
        /// Cache'i temizler;
        /// </summary>
        private void ClearCache()
        {
            lock (_lockObject)
            {
                _skinCache.Clear();
                _cacheTimestamps.Clear();
            }
        }

        /// <summary>
        /// Eski cache girdilerini temizler;
        /// </summary>
        private void CleanupOldestCacheEntries()
        {
            var oldestEntries = _cacheTimestamps;
                .OrderBy(kv => kv.Value)
                .Take(_skinCache.Count - _configuration.MaxCacheSize / 2)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldestEntries)
            {
                _skinCache.Remove(key);
                _cacheTimestamps.Remove(key);
            }
        }

        /// <summary>
        /// Cache cleanup task'ı;
        /// </summary>
        private async Task CacheCleanupTaskAsync()
        {
            while (_isInitialized)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));

                try
                {
                    CleanupExpiredCacheEntries();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Cache cleanup hatası: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Süresi dolmuş cache girdilerini temizler;
        /// </summary>
        private void CleanupExpiredCacheEntries()
        {
            lock (_lockObject)
            {
                var expiredKeys = new List<string>();
                var now = DateTime.UtcNow;

                foreach (var kvp in _cacheTimestamps)
                {
                    if (now - kvp.Value > _configuration.CacheDuration)
                    {
                        expiredKeys.Add(kvp.Key);
                    }
                }

                foreach (var key in expiredKeys)
                {
                    _skinCache.Remove(key);
                    _cacheTimestamps.Remove(key);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogDebug($"{expiredKeys.Count} expired cache entry temizlendi.");
                }
            }
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
                var configPath = Path.Combine(_config.AppDataPath, "SkinManager", "config.json");
                if (File.Exists(configPath))
                {
                    var json = await File.ReadAllTextAsync(configPath);
                    _configuration = System.Text.Json.JsonSerializer.Deserialize<SkinConfiguration>(json);
                }
                else;
                {
                    // Varsayılan yapılandırma;
                    _configuration = new SkinConfiguration;
                    {
                        MaxCacheSize = 100,
                        CacheDuration = TimeSpan.FromHours(6),
                        DefaultQuality = RenderQuality.High,
                        EnableCompression = true,
                        CompressionQuality = 90,
                        MaxTextureSize = 4096,
                        MinTextureSize = 256,
                        EnableMipMaps = true,
                        MaxMipLevels = 12,
                        EnableAsyncLoading = true,
                        ThreadPoolSize = Environment.ProcessorCount;
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
        /// Kalite ayarlarını yükler;
        /// </summary>
        private async Task LoadQualitySettingsAsync()
        {
            try
            {
                var settingsPath = Path.Combine(_config.AppDataPath, "SkinManager", "quality.json");
                if (File.Exists(settingsPath))
                {
                    var json = await File.ReadAllTextAsync(settingsPath);
                    _qualitySettings = System.Text.Json.JsonSerializer.Deserialize<SkinQualitySettings>(json);
                }
                else;
                {
                    // Varsayılan kalite ayarları;
                    _qualitySettings = new SkinQualitySettings;
                    {
                        CurrentQuality = RenderQuality.High,
                        EnableSubsurfaceScattering = true,
                        SSSQuality = SSSQuality.High,
                        EnableDisplacement = true,
                        DisplacementScale = 0.1f,
                        TessellationFactor = 3,
                        EnableParallaxOcclusion = true,
                        ParallaxQuality = ParallaxQuality.Medium,
                        EnableScreenSpaceReflections = false,
                        EnableAmbientOcclusion = true,
                        AOQuality = AOQuality.Medium,
                        EnableDynamicTessellation = true,
                        MaxTessellationFactor = 8,
                        MinTessellationFactor = 1,
                        EnableAsyncTextureLoading = true,
                        TextureStreamingEnabled = true,
                        MaxStreamingTextures = 50,
                        EnableTextureCompression = true,
                        CompressionFormat = TextureCompressionFormat.BC7;
                    };

                    // Varsayılan ayarları kaydet;
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                    var defaultJson = System.Text.Json.JsonSerializer.Serialize(_qualitySettings,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(settingsPath, defaultJson);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kalite ayarları yükleme hatası: {ex.Message}", ex);
                // Varsayılan ayarlara devam et;
            }
        }

        /// <summary>
        /// Shader kütüphanesini yükler;
        /// </summary>
        private async Task LoadShaderLibraryAsync()
        {
            try
            {
                var shaderPath = Path.Combine(_config.AppDataPath, "SkinManager", "Shaders");
                if (!Directory.Exists(shaderPath))
                {
                    Directory.CreateDirectory(shaderPath);
                    await CreateDefaultShadersAsync(shaderPath);
                }

                await _shaderLibrary.LoadShadersAsync(shaderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Shader kütüphanesi yükleme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Varsayılan skin materyallerini yükler;
        /// </summary>
        private async Task LoadDefaultSkinMaterialsAsync()
        {
            try
            {
                var materialsPath = Path.Combine(_config.AppDataPath, "SkinManager", "Materials");
                if (!Directory.Exists(materialsPath))
                {
                    Directory.CreateDirectory(materialsPath);
                    await CreateDefaultMaterialsAsync(materialsPath);
                }

                await LoadMaterialsFromDirectoryAsync(materialsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Skin materyalleri yükleme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Skin presetlerini yükler;
        /// </summary>
        private async Task LoadSkinPresetsAsync()
        {
            try
            {
                var presetsPath = Path.Combine(_config.AppDataPath, "SkinManager", "Presets");
                if (!Directory.Exists(presetsPath))
                {
                    Directory.CreateDirectory(presetsPath);
                    await CreateDefaultPresetsAsync(presetsPath);
                }

                await LoadPresetsFromDirectoryAsync(presetsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Skin presetleri yükleme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Skin özelliklerini yükler;
        /// </summary>
        private async Task LoadSkinFeaturesAsync()
        {
            try
            {
                var featuresPath = Path.Combine(_config.AppDataPath, "SkinManager", "Features");
                if (!Directory.Exists(featuresPath))
                {
                    Directory.CreateDirectory(featuresPath);
                    await CreateDefaultFeaturesAsync(featuresPath);
                }

                await LoadFeaturesFromDirectoryAsync(featuresPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Skin özellikleri yükleme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Render kaynaklarını serbest bırakır;
        /// </summary>
        private async Task ReleaseRenderResourcesAsync()
        {
            try
            {
                // Texture'ları serbest bırak;
                foreach (var texture in _skinMaterials.Values.SelectMany(m => m.Textures.Values))
                {
                    await texture.ReleaseAsync();
                }

                // Shader'ları serbest bırak;
                await _shaderLibrary.ReleaseResourcesAsync();

                // Cache'i temizle;
                ClearCache();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Render kaynakları serbest bırakma hatası: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Private Methods - Default Content Creation;

        /// <summary>
        /// Varsayılan shader'ları oluşturur;
        /// </summary>
        private async Task CreateDefaultShadersAsync(string shaderPath)
        {
            // Bu kısım gerçek shader dosyalarını oluşturur;
            // Örnek olarak birkaç temel shader;
            var defaultShaders = new[]
            {
                new { Name = "Skin_Basic.shader", Content = GetBasicSkinShader() },
                new { Name = "Skin_Advanced.shader", Content = GetAdvancedSkinShader() },
                new { Name = "Skin_SSS.shader", Content = GetSSSSkinShader() },
                new { Name = "Skin_Displacement.shader", Content = GetDisplacementSkinShader() }
            };

            foreach (var shader in defaultShaders)
            {
                var filePath = Path.Combine(shaderPath, shader.Name);
                await File.WriteAllTextAsync(filePath, shader.Content);
            }
        }

        /// <summary>
        /// Varsayılan materyalleri oluşturur;
        /// </summary>
        private async Task CreateDefaultMaterialsAsync(string materialsPath)
        {
            var defaultMaterials = new[]
            {
                CreateDefaultMaterial("Human_Skin_Basic", SkinType.Human, SkinTone.Medium),
                CreateDefaultMaterial("Human_Skin_Fair", SkinType.Human, SkinTone.Fair),
                CreateDefaultMaterial("Human_Skin_Dark", SkinType.Human, SkinTone.Dark),
                CreateDefaultMaterial("Alien_Skin", SkinType.Alien, SkinTone.Olive),
                CreateDefaultMaterial("Creature_Skin", SkinType.Creature, SkinTone.Brown),
                CreateDefaultMaterial("Fantasy_Skin", SkinType.Fantasy, SkinTone.Porcelain)
            };

            foreach (var material in defaultMaterials)
            {
                var filePath = Path.Combine(materialsPath, $"{material.Name}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(material,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json);
            }
        }

        /// <summary>
        /// Varsayılan preset'leri oluşturur;
        /// </summary>
        private async Task CreateDefaultPresetsAsync(string presetsPath)
        {
            var defaultPresets = new[]
            {
                CreateDefaultPreset("Young_Adult", 25, SkinTone.Medium, HealthCondition.Healthy),
                CreateDefaultPreset("Middle_Aged", 45, SkinTone.Light, HealthCondition.Normal),
                CreateDefaultPreset("Elderly", 70, SkinTone.Fair, HealthCondition.Aging),
                CreateDefaultPreset("Athlete", 30, SkinTone.Tan, HealthCondition.VeryHealthy),
                CreateDefaultPreset("Sick", 35, SkinTone.Porcelain, HealthCondition.Sick),
                CreateDefaultPreset("Sun_Damaged", 50, SkinTone.Brown, HealthCondition.SunDamaged),
                CreateDefaultPreset("Fantasy_Elf", 200, SkinTone.Porcelain, HealthCondition.Healthy),
                CreateDefaultPreset("Fantasy_Orc", 40, SkinTone.Green, HealthCondition.Healthy)
            };

            foreach (var preset in defaultPresets)
            {
                var filePath = Path.Combine(presetsPath, $"{preset.Name}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(preset,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json);
            }
        }

        /// <summary>
        /// Varsayılan özellikleri oluşturur;
        /// </summary>
        private async Task CreateDefaultFeaturesAsync(string featuresPath)
        {
            var defaultFeatures = new[]
            {
                CreateDefaultFeature("Freckles", SkinFeatureType.Freckles),
                CreateDefaultFeature("Moles", SkinFeatureType.Moles),
                CreateDefaultFeature("Scars", SkinFeatureType.Scars),
                CreateDefaultFeature("Tattoos", SkinFeatureType.Tattoos),
                CreateDefaultFeature("Birthmarks", SkinFeatureType.Birthmarks),
                CreateDefaultFeature("Acne", SkinFeatureType.Acne),
                CreateDefaultFeature("Wrinkles", SkinFeatureType.Wrinkles),
                CreateDefaultFeature("Rosacea", SkinFeatureType.Rosacea),
                CreateDefaultFeature("Vitiligo", SkinFeatureType.Vitiligo),
                CreateDefaultFeature("Bruises", SkinFeatureType.Bruises),
                CreateDefaultFeature("Blisters", SkinFeatureType.Blisters),
                CreateDefaultFeature("Rashes", SkinFeatureType.Rashes)
            };

            foreach (var feature in defaultFeatures)
            {
                var filePath = Path.Combine(featuresPath, $"{feature.Name}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(feature,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json);
            }
        }

        #endregion;

        #region Private Methods - Profile Operations;

        /// <summary>
        /// Varsayılan skin materyallerini uygular;
        /// </summary>
        private void ApplyDefaultSkinMaterials(SkinProfile profile)
        {
            var skinType = profile.BaseParameters.SkinType;
            var skinTone = profile.SkinTone;

            // Skin tipine ve tonuna göre materyal seç;
            var materialKey = $"{skinType}_{skinTone}";

            if (_skinMaterials.TryGetValue(materialKey, out var material))
            {
                profile.BaseMaterial = material;
            }
            else;
            {
                // Varsayılan materyal;
                profile.BaseMaterial = _skinMaterials.Values.FirstOrDefault();
            }
        }

        /// <summary>
        /// Varsayılan skin özelliklerini uygular;
        /// </summary>
        private void ApplyDefaultSkinFeatures(SkinProfile profile)
        {
            // Yaş ve cinsiyete göre varsayılan özellikler;
            var age = profile.AgeParameters?.Age ?? 25;
            var gender = profile.BaseParameters.Gender;

            // Freckles for younger characters;
            if (age < 30 && _random.NextDouble() < 0.3)
            {
                var freckles = GetSkinFeature("Freckles");
                if (freckles != null)
                {
                    var intensity = (float)(_random.NextDouble() * 0.5 + 0.2);
                    var position = new Vector2(
                        (float)(_random.NextDouble() * 0.8 - 0.4),
                        (float)(_random.NextDouble() * 0.6 - 0.3)
                    );
                    profile.AddFeature(new AppliedSkinFeature(freckles, intensity, position));
                }
            }

            // Moles for adult characters;
            if (age > 18 && _random.NextDouble() < 0.4)
            {
                var moles = GetSkinFeature("Moles");
                if (moles != null)
                {
                    var moleCount = _random.Next(1, 5);
                    for (int i = 0; i < moleCount; i++)
                    {
                        var intensity = (float)(_random.NextDouble() * 0.3 + 0.1);
                        var position = new Vector2(
                            (float)(_random.NextDouble() * 1.6 - 0.8),
                            (float)(_random.NextDouble() * 1.2 - 0.6)
                        );
                        profile.AddFeature(new AppliedSkinFeature(moles, intensity, position));
                    }
                }
            }

            // Wrinkles for older characters;
            if (age > 50)
            {
                var wrinkles = GetSkinFeature("Wrinkles");
                if (wrinkles != null)
                {
                    var intensity = Math.Clamp((age - 50) / 50.0f, 0.1f, 0.8f);
                    profile.AddFeature(new AppliedSkinFeature(wrinkles, intensity, Vector2.Zero));
                }
            }
        }

        /// <summary>
        /// Preset'i profile uygular;
        /// </summary>
        private SkinProfile ApplyPresetToProfile(SkinProfile profile, SkinPreset preset,
            float blendFactor, bool applyFeatures)
        {
            var result = profile.Clone();

            // Base parameters blend;
            if (preset.BaseParameters != null)
            {
                result.BaseParameters = BlendBaseParameters(
                    profile.BaseParameters,
                    preset.BaseParameters,
                    blendFactor);
            }

            // Skin tone blend;
            if (preset.SkinTone.HasValue)
            {
                result.SkinTone = BlendSkinTone(profile.SkinTone, preset.SkinTone.Value, blendFactor);
            }

            // Age parameters blend;
            if (preset.AgeParameters != null)
            {
                result.AgeParameters = BlendAgeParameters(
                    profile.AgeParameters ?? new AgeParameters { Age = 25 },
                    preset.AgeParameters,
                    blendFactor);
            }

            // Health parameters blend;
            if (preset.HealthParameters != null)
            {
                result.HealthParameters = BlendHealthParameters(
                    profile.HealthParameters ?? new HealthParameters { HealthLevel = 1.0f },
                    preset.HealthParameters,
                    blendFactor);
            }

            // Features;
            if (applyFeatures && preset.Features.Any())
            {
                foreach (var presetFeature in preset.Features)
                {
                    var existingFeature = result.Features;
                        .FirstOrDefault(f => f.Feature.Id == presetFeature.Feature.Id);

                    if (existingFeature != null)
                    {
                        // Update existing feature;
                        existingFeature.Intensity = MathHelper.Lerp(
                            existingFeature.Intensity,
                            presetFeature.Intensity,
                            blendFactor);
                        existingFeature.Position = Vector2.Lerp(
                            existingFeature.Position,
                            presetFeature.Position,
                            blendFactor);
                    }
                    else;
                    {
                        // Add new feature with blended intensity;
                        var blendedFeature = new AppliedSkinFeature(
                            presetFeature.Feature,
                            presetFeature.Intensity * blendFactor,
                            presetFeature.Position);
                        result.AddFeature(blendedFeature);
                    }
                }
            }

            result.LastModified = DateTime.UtcNow;
            result.Version++;

            return result;
        }

        /// <summary>
        /// Skin profillerini karıştırır;
        /// </summary>
        private SkinProfile BlendSkinProfiles(SkinProfile profile1, SkinProfile profile2,
            float blendFactor, string newProfileId)
        {
            var blendedProfile = new SkinProfile(newProfileId, profile1.BaseParameters)
            {
                CreatedDate = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                QualityLevel = Math.Max(profile1.QualityLevel, profile2.QualityLevel)
            };

            // Base parameters blend;
            blendedProfile.BaseParameters = BlendBaseParameters(
                profile1.BaseParameters,
                profile2.BaseParameters,
                blendFactor);

            // Skin tone blend;
            blendedProfile.SkinTone = BlendSkinTone(profile1.SkinTone, profile2.SkinTone, blendFactor);

            // Age parameters blend;
            if (profile1.AgeParameters != null && profile2.AgeParameters != null)
            {
                blendedProfile.AgeParameters = BlendAgeParameters(
                    profile1.AgeParameters,
                    profile2.AgeParameters,
                    blendFactor);
            }
            else if (profile1.AgeParameters != null)
            {
                blendedProfile.AgeParameters = profile1.AgeParameters;
            }
            else if (profile2.AgeParameters != null)
            {
                blendedProfile.AgeParameters = profile2.AgeParameters;
            }

            // Health parameters blend;
            if (profile1.HealthParameters != null && profile2.HealthParameters != null)
            {
                blendedProfile.HealthParameters = BlendHealthParameters(
                    profile1.HealthParameters,
                    profile2.HealthParameters,
                    blendFactor);
            }
            else if (profile1.HealthParameters != null)
            {
                blendedProfile.HealthParameters = profile1.HealthParameters;
            }
            else if (profile2.HealthParameters != null)
            {
                blendedProfile.HealthParameters = profile2.HealthParameters;
            }

            // Features blend;
            BlendSkinFeatures(blendedProfile, profile1, profile2, blendFactor);

            // Material;
            blendedProfile.BaseMaterial = blendFactor < 0.5f ? profile1.BaseMaterial : profile2.BaseMaterial;

            return blendedProfile;
        }

        /// <summary>
        /// Yaşlanma efektlerini profile uygular;
        /// </summary>
        private SkinProfile ApplyAgingToProfile(SkinProfile profile, int targetAge,
            AgingIntensity intensity)
        {
            var agedProfile = profile.Clone();

            if (agedProfile.AgeParameters == null)
            {
                agedProfile.AgeParameters = new AgeParameters();
            }

            // Age update;
            agedProfile.AgeParameters.Age = targetAge;

            // Intensity multipliers;
            float intensityMultiplier = intensity switch;
            {
                AgingIntensity.Light => 0.5f,
                AgingIntensity.Natural => 1.0f,
                AgingIntensity.Heavy => 1.5f,
                AgingIntensity.Extreme => 2.0f,
                _ => 1.0f;
            };

            // Wrinkles;
            if (targetAge > 30)
            {
                var wrinkles = GetSkinFeature("Wrinkles");
                if (wrinkles != null)
                {
                    var wrinkleIntensity = Math.Clamp((targetAge - 30) / 50.0f * intensityMultiplier, 0.0f, 1.0f);

                    var existingWrinkles = agedProfile.Features;
                        .FirstOrDefault(f => f.Feature.FeatureType == SkinFeatureType.Wrinkles);

                    if (existingWrinkles != null)
                    {
                        existingWrinkles.Intensity = wrinkleIntensity;
                    }
                    else;
                    {
                        agedProfile.AddFeature(new AppliedSkinFeature(wrinkles, wrinkleIntensity, Vector2.Zero));
                    }
                }
            }

            // Age spots;
            if (targetAge > 40)
            {
                var ageSpots = GetSkinFeature("AgeSpots");
                if (ageSpots != null)
                {
                    var spotIntensity = Math.Clamp((targetAge - 40) / 40.0f * intensityMultiplier, 0.0f, 1.0f);

                    var existingSpots = agedProfile.Features;
                        .FirstOrDefault(f => f.Feature.FeatureType == SkinFeatureType.AgeSpots);

                    if (existingSpots != null)
                    {
                        existingSpots.Intensity = spotIntensity;
                    }
                    else;
                    {
                        agedProfile.AddFeature(new AppliedSkinFeature(ageSpots, spotIntensity, Vector2.Zero));
                    }
                }
            }

            // Skin color changes;
            if (agedProfile.AgeParameters != null)
            {
                agedProfile.AgeParameters.IncludeWrinkles = targetAge > 30;
                agedProfile.AgeParameters.WrinkleIntensity = Math.Clamp((targetAge - 30) / 50.0f, 0.0f, 1.0f);
                agedProfile.AgeParameters.IncludeAgeSpots = targetAge > 40;
                agedProfile.AgeParameters.AgeSpotIntensity = Math.Clamp((targetAge - 40) / 40.0f, 0.0f, 1.0f);
                agedProfile.AgeParameters.SkinThinning = Math.Clamp((targetAge - 50) / 30.0f, 0.0f, 1.0f);
            }

            agedProfile.LastModified = DateTime.UtcNow;
            agedProfile.Version++;

            return agedProfile;
        }

        /// <summary>
        /// Sağlık efektlerini profile uygular;
        /// </summary>
        private SkinProfile ApplyHealthToProfile(SkinProfile profile, HealthCondition condition,
            float severity)
        {
            var healthProfile = profile.Clone();

            if (healthProfile.HealthParameters == null)
            {
                healthProfile.HealthParameters = new HealthParameters();
            }

            // Update health parameters based on condition;
            switch (condition)
            {
                case HealthCondition.VeryHealthy:
                    healthProfile.HealthParameters.HealthLevel = 1.0f;
                    healthProfile.HealthParameters.SkinDryness = 0.1f;
                    healthProfile.HealthParameters.HasFever = false;
                    healthProfile.HealthParameters.HasJaundice = false;
                    healthProfile.HealthParameters.PoorCirculation = false;
                    healthProfile.HealthParameters.InflammationLevel = 0.0f;
                    break;

                case HealthCondition.Healthy:
                    healthProfile.HealthParameters.HealthLevel = 0.8f;
                    healthProfile.HealthParameters.SkinDryness = 0.2f;
                    healthProfile.HealthParameters.HasFever = false;
                    healthProfile.HealthParameters.HasJaundice = false;
                    healthProfile.HealthParameters.PoorCirculation = false;
                    healthProfile.HealthParameters.InflammationLevel = 0.1f;
                    break;

                case HealthCondition.Normal:
                    healthProfile.HealthParameters.HealthLevel = 0.6f;
                    healthProfile.HealthParameters.SkinDryness = 0.3f;
                    healthProfile.HealthParameters.HasFever = false;
                    healthProfile.HealthParameters.HasJaundice = false;
                    healthProfile.HealthParameters.PoorCirculation = false;
                    healthProfile.HealthParameters.InflammationLevel = 0.2f;
                    break;

                case HealthCondition.Sick:
                    healthProfile.HealthParameters.HealthLevel = 0.4f * severity;
                    healthProfile.HealthParameters.SkinDryness = 0.5f;
                    healthProfile.HealthParameters.HasFever = _random.NextDouble() < 0.7;
                    healthProfile.HealthParameters.HasJaundice = _random.NextDouble() < 0.3;
                    healthProfile.HealthParameters.PoorCirculation = _random.NextDouble() < 0.5;
                    healthProfile.HealthParameters.InflammationLevel = 0.5f;
                    break;

                case HealthCondition.VerySick:
                    healthProfile.HealthParameters.HealthLevel = 0.2f * severity;
                    healthProfile.HealthParameters.SkinDryness = 0.7f;
                    healthProfile.HealthParameters.HasFever = true;
                    healthProfile.HealthParameters.HasJaundice = _random.NextDouble() < 0.6;
                    healthProfile.HealthParameters.PoorCirculation = true;
                    healthProfile.HealthParameters.InflammationLevel = 0.8f;
                    break;

                case HealthCondition.Aging:
                    healthProfile.HealthParameters.HealthLevel = 0.5f;
                    healthProfile.HealthParameters.SkinDryness = 0.6f;
                    healthProfile.HealthParameters.HasFever = false;
                    healthProfile.HealthParameters.HasJaundice = false;
                    healthProfile.HealthParameters.PoorCirculation = _random.NextDouble() < 0.4;
                    healthProfile.HealthParameters.InflammationLevel = 0.3f;
                    break;

                case HealthCondition.SunDamaged:
                    healthProfile.HealthParameters.HealthLevel = 0.7f;
                    healthProfile.HealthParameters.SkinDryness = 0.8f;
                    healthProfile.HealthParameters.HasFever = false;
                    healthProfile.HealthParameters.HasJaundice = false;
                    healthProfile.HealthParameters.PoorCirculation = false;
                    healthProfile.HealthParameters.InflammationLevel = 0.4f;
                    break;
            }

            // Apply visual features based on health;
            if (healthProfile.HealthParameters.HasFever)
            {
                var redness = GetSkinFeature("Rosacea");
                if (redness != null)
                {
                    healthProfile.AddFeature(new AppliedSkinFeature(redness, 0.3f * severity, Vector2.Zero));
                }
            }

            if (healthProfile.HealthParameters.HasJaundice)
            {
                // Add yellowing effect;
                healthProfile.HealthParameters.Yellowing = 0.4f * severity;
            }

            if (healthProfile.HealthParameters.PoorCirculation)
            {
                var bruises = GetSkinFeature("Bruises");
                if (bruises != null && _random.NextDouble() < 0.5)
                {
                    healthProfile.AddFeature(new AppliedSkinFeature(bruises, 0.2f * severity,
                        new Vector2((float)(_random.NextDouble() * 0.5 - 0.25), 0.3f)));
                }
            }

            if (healthProfile.HealthParameters.InflammationLevel > 0.3f)
            {
                var rash = GetSkinFeature("Rashes");
                if (rash != null)
                {
                    healthProfile.AddFeature(new AppliedSkinFeature(rash,
                        healthProfile.HealthParameters.InflammationLevel * 0.5f, Vector2.Zero));
                }
            }

            healthProfile.LastModified = DateTime.UtcNow;
            healthProfile.Version++;

            return healthProfile;
        }

        /// <summary>
        /// Çevresel efektleri profile uygular;
        /// </summary>
        private SkinProfile ApplyEnvironmentToProfile(SkinProfile profile,
            EnvironmentalCondition condition, float exposureTime)
        {
            var envProfile = profile.Clone();

            // Apply effects based on condition and exposure time;
            switch (condition)
            {
                case EnvironmentalCondition.SunExposed:
                    // Sun damage - tanning, freckles, dryness;
                    if (envProfile.SkinTone < SkinTone.Ebony)
                    {
                        // Darken skin tone;
                        envProfile.SkinTone = (SkinTone)Math.Min((int)envProfile.SkinTone + 1, (int)SkinTone.Ebony);
                    }

                    // Add freckles;
                    var freckles = GetSkinFeature("Freckles");
                    if (freckles != null && exposureTime > 10)
                    {
                        var freckleIntensity = Math.Clamp(exposureTime / 100.0f, 0.1f, 0.8f);
                        envProfile.AddFeature(new AppliedSkinFeature(freckles, freckleIntensity,
                            new Vector2(0, 0.2f)));
                    }

                    // Increase dryness;
                    if (envProfile.HealthParameters == null)
                        envProfile.HealthParameters = new HealthParameters();

                    envProfile.HealthParameters.SkinDryness = Math.Clamp(
                        (envProfile.HealthParameters.SkinDryness ?? 0.3f) + exposureTime / 50.0f,
                        0.0f, 1.0f);
                    break;

                case EnvironmentalCondition.Cold:
                    // Cold effects - redness, dryness, cracking;
                    if (envProfile.HealthParameters == null)
                        envProfile.HealthParameters = new HealthParameters();

                    envProfile.HealthParameters.PoorCirculation = true;
                    envProfile.HealthParameters.SkinDryness = Math.Clamp(
                        (envProfile.HealthParameters.SkinDryness ?? 0.3f) + exposureTime / 30.0f,
                        0.0f, 1.0f);

                    // Add redness;
                    var rosacea = GetSkinFeature("Rosacea");
                    if (rosacea != null && exposureTime > 5)
                    {
                        envProfile.AddFeature(new AppliedSkinFeature(rosacea,
                            Math.Clamp(exposureTime / 20.0f, 0.1f, 0.5f), Vector2.Zero));
                    }
                    break;

                case EnvironmentalCondition.Humid:
                    // Humid effects - oiliness, acne;
                    if (envProfile.BaseParameters != null)
                    {
                        envProfile.BaseParameters.SkinOiliness = Math.Clamp(
                            envProfile.BaseParameters.SkinOiliness + exposureTime / 40.0f,
                            0.0f, 1.0f);
                    }

                    // Add acne for extended exposure;
                    if (exposureTime > 20)
                    {
                        var acne = GetSkinFeature("Acne");
                        if (acne != null)
                        {
                            envProfile.AddFeature(new AppliedSkinFeature(acne,
                                Math.Clamp(exposureTime / 50.0f, 0.1f, 0.4f),
                                new Vector2(0, -0.2f)));
                        }
                    }
                    break;

                case EnvironmentalCondition.Polluted:
                    // Pollution effects - dullness, spots, irritation;
                    if (envProfile.BaseParameters != null)
                    {
                        envProfile.BaseParameters.SkinSmoothness = Math.Max(
                            0.1f, envProfile.BaseParameters.SkinSmoothness - exposureTime / 60.0f);
                    }

                    // Add irritation;
                    if (exposureTime > 15)
                    {
                        var rash = GetSkinFeature("Rashes");
                        if (rash != null)
                        {
                            envProfile.AddFeature(new AppliedSkinFeature(rash,
                                Math.Clamp(exposureTime / 40.0f, 0.1f, 0.6f), Vector2.Zero));
                        }
                    }
                    break;

                case EnvironmentalCondition.Windy:
                    // Wind effects - dryness, chapping;
                    if (envProfile.HealthParameters == null)
                        envProfile.HealthParameters = new HealthParameters();

                    envProfile.HealthParameters.SkinDryness = Math.Clamp(
                        (envProfile.HealthParameters.SkinDryness ?? 0.3f) + exposureTime / 25.0f,
                        0.0f, 1.0f);

                    // Add chapping/blisters for extreme exposure;
                    if (exposureTime > 30)
                    {
                        var blisters = GetSkinFeature("Blisters");
                        if (blisters != null)
                        {
                            envProfile.AddFeature(new AppliedSkinFeature(blisters,
                                Math.Clamp((exposureTime - 30) / 40.0f, 0.1f, 0.3f),
                                new Vector2(0.3f, 0.1f)));
                        }
                    }
                    break;
            }

            envProfile.LastModified = DateTime.UtcNow;
            envProfile.Version++;

            return envProfile;
        }

        #endregion;

        #region Private Methods - Blending Operations;

        /// <summary>
        /// Base parametreleri karıştırır;
        /// </summary>
        private SkinBaseParameters BlendBaseParameters(SkinBaseParameters param1,
            SkinBaseParameters param2, float blendFactor)
        {
            return new SkinBaseParameters;
            {
                SkinType = blendFactor < 0.5f ? param1.SkinType : param2.SkinType,
                Gender = blendFactor < 0.5f ? param1.Gender : param2.Gender,
                Ethnicity = blendFactor < 0.5f ? param1.Ethnicity : param2.Ethnicity,
                SkinSmoothness = MathHelper.Lerp(param1.SkinSmoothness, param2.SkinSmoothness, blendFactor),
                SkinRoughness = MathHelper.Lerp(param1.SkinRoughness, param2.SkinRoughness, blendFactor),
                SkinSpecular = MathHelper.Lerp(param1.SkinSpecular, param2.SkinSpecular, blendFactor),
                SkinOiliness = MathHelper.Lerp(param1.SkinOiliness, param2.SkinOiliness, blendFactor),
                SkinMoisture = MathHelper.Lerp(param1.SkinMoisture, param2.SkinMoisture, blendFactor),
                SkinClearCoat = MathHelper.Lerp(param1.SkinClearCoat, param2.SkinClearCoat, blendFactor),
                SkinClearCoatRoughness = MathHelper.Lerp(param1.SkinClearCoatRoughness, param2.SkinClearCoatRoughness, blendFactor),
                SkinAnisotropy = MathHelper.Lerp(param1.SkinAnisotropy, param2.SkinAnisotropy, blendFactor),
                SkinSheen = MathHelper.Lerp(param1.SkinSheen, param2.SkinSheen, blendFactor),
                SkinSheenTint = MathHelper.Lerp(param1.SkinSheenTint, param2.SkinSheenTint, blendFactor),
                SkinTransmission = MathHelper.Lerp(param1.SkinTransmission, param2.SkinTransmission, blendFactor),
                SkinThickness = MathHelper.Lerp(param1.SkinThickness, param2.SkinThickness, blendFactor),
                IncludeSkinPores = blendFactor < 0.5f ? param1.IncludeSkinPores : param2.IncludeSkinPores,
                PoreSize = MathHelper.Lerp(param1.PoreSize, param2.PoreSize, blendFactor),
                PoreIntensity = MathHelper.Lerp(param1.PoreIntensity, param2.PoreIntensity, blendFactor),
                SkinSmoothnessVariation = MathHelper.Lerp(param1.SkinSmoothnessVariation, param2.SkinSmoothnessVariation, blendFactor),
                MorphTargets = blendFactor < 0.5f ? param1.MorphTargets : param2.MorphTargets;
            };
        }

        /// <summary>
        /// Skin tonlarını karıştırır;
        /// </summary>
        private SkinTone BlendSkinTone(SkinTone tone1, SkinTone tone2, float blendFactor)
        {
            if (blendFactor < 0.5f)
                return tone1;

            if (blendFactor > 0.5f)
                return tone2;

            // Orta noktada en yakın ton;
            var tone1Value = (int)tone1;
            var tone2Value = (int)tone2;
            var blendedValue = (int)Math.Round(tone1Value + (tone2Value - tone1Value) * blendFactor);

            return (SkinTone)Math.Clamp(blendedValue, (int)SkinTone.Porcelain, (int)SkinTone.Ebony);
        }

        /// <summary>
        /// Yaş parametrelerini karıştırır;
        /// </summary>
        private AgeParameters BlendAgeParameters(AgeParameters param1, AgeParameters param2, float blendFactor)
        {
            return new AgeParameters;
            {
                Age = (int)Math.Round(param1.Age + (param2.Age - param1.Age) * blendFactor),
                IncludeWrinkles = blendFactor < 0.5f ? param1.IncludeWrinkles : param2.IncludeWrinkles,
                WrinkleIntensity = MathHelper.Lerp(param1.WrinkleIntensity, param2.WrinkleIntensity, blendFactor),
                IncludeAgeSpots = blendFactor < 0.5f ? param1.IncludeAgeSpots : param2.IncludeAgeSpots,
                AgeSpotIntensity = MathHelper.Lerp(param1.AgeSpotIntensity, param2.AgeSpotIntensity, blendFactor),
                SkinThinning = MathHelper.Lerp(param1.SkinThinning, param2.SkinThinning, blendFactor)
            };
        }

        /// <summary>
        /// Sağlık parametrelerini karıştırır;
        /// </summary>
        private HealthParameters BlendHealthParameters(HealthParameters param1, HealthParameters param2, float blendFactor)
        {
            return new HealthParameters;
            {
                HealthLevel = MathHelper.Lerp(param1.HealthLevel, param2.HealthLevel, blendFactor),
                SkinDryness = MathHelper.Lerp(param1.SkinDryness ?? 0.3f, param2.SkinDryness ?? 0.3f, blendFactor),
                HasFever = blendFactor < 0.5f ? param1.HasFever : param2.HasFever,
                HasJaundice = blendFactor < 0.5f ? param1.HasJaundice : param2.HasJaundice,
                PoorCirculation = blendFactor < 0.5f ? param1.PoorCirculation : param2.PoorCirculation,
                InflammationLevel = MathHelper.Lerp(param1.InflammationLevel, param2.InflammationLevel, blendFactor),
                Yellowing = MathHelper.Lerp(param1.Yellowing ?? 0.0f, param2.Yellowing ?? 0.0f, blendFactor)
            };
        }

        /// <summary>
        /// Skin özelliklerini karıştırır;
        /// </summary>
        private void BlendSkinFeatures(SkinProfile result, SkinProfile profile1,
            SkinProfile profile2, float blendFactor)
        {
            // Tüm özellikleri topla;
            var allFeatures = new Dictionary<string, (AppliedSkinFeature, float)>();

            // Profile1 özellikleri (1-blendFactor ağırlığında)
            foreach (var feature in profile1.Features)
            {
                allFeatures[feature.Feature.Id] = (feature, 1.0f - blendFactor);
            }

            // Profile2 özellikleri (blendFactor ağırlığında)
            foreach (var feature in profile2.Features)
            {
                if (allFeatures.ContainsKey(feature.Feature.Id))
                {
                    var existing = allFeatures[feature.Feature.Id];
                    var blendedIntensity = existing.Item1.Intensity * (1.0f - blendFactor) +
                                          feature.Intensity * blendFactor;
                    var blendedPosition = Vector2.Lerp(existing.Item1.Position, feature.Position, blendFactor);

                    allFeatures[feature.Feature.Id] = (
                        new AppliedSkinFeature(feature.Feature, blendedIntensity, blendedPosition),
                        1.0f;
                    );
                }
                else;
                {
                    allFeatures[feature.Feature.Id] = (feature, blendFactor);
                }
            }

            // Sonuç özelliklerini ekle;
            foreach (var kvp in allFeatures)
            {
                var (feature, weight) = kvp.Value;
                if (weight > 0.1f) // Minimum eşik;
                {
                    result.AddFeature(feature);
                }
            }
        }

        #endregion;

        #region Private Methods - Material Operations;

        /// <summary>
        /// Yaş efektlerini material parametrelerine uygular;
        /// </summary>
        private SkinMaterialParameters ApplyAgeToMaterialParameters(SkinMaterialParameters parameters,
            AgeParameters ageParams)
        {
            var age = ageParams.Age;

            // Roughness increases with age;
            parameters.Roughness += ageParams.WrinkleIntensity * 0.2f;

            // Specular decreases with age;
            parameters.Specular -= ageParams.WrinkleIntensity * 0.1f;

            // Clear coat decreases with age;
            parameters.ClearCoat -= ageParams.SkinThinning * 0.3f;

            // Subsurface scattering changes;
            if (age > 50)
            {
                // Older skin has less subsurface scattering;
                parameters.SubsurfaceRadius *= 0.8f;
            }

            return parameters;
        }

        /// <summary>
        /// Sağlık efektlerini material parametrelerine uygular;
        /// </summary>
        private SkinMaterialParameters ApplyHealthToMaterialParameters(SkinMaterialParameters parameters,
            HealthParameters healthParams)
        {
            // Paleness affects subsurface color;
            if (healthParams.Paleness > 0)
            {
                parameters.SubsurfaceColor *= (1.0f - healthParams.Paleness * 0.5f);
            }

            // Dryness affects specular and roughness;
            if (healthParams.SkinDryness.HasValue)
            {
                var dryness = healthParams.SkinDryness.Value;
                parameters.Specular -= dryness * 0.2f;
                parameters.Roughness += dryness * 0.3f;
            }

            // Fever/redness affects subsurface color;
            if (healthParams.Redness > 0)
            {
                parameters.SubsurfaceColor = new Vector3(
                    parameters.SubsurfaceColor.X + healthParams.Redness * 0.3f,
                    parameters.SubsurfaceColor.Y,
                    parameters.SubsurfaceColor.Z;
                );
            }

            // Jaundice/yellowing;
            if (healthParams.Yellowing.HasValue && healthParams.Yellowing > 0)
            {
                parameters.SubsurfaceColor = new Vector3(
                    parameters.SubsurfaceColor.X,
                    parameters.SubsurfaceColor.Y + healthParams.Yellowing.Value * 0.4f,
                    parameters.SubsurfaceColor.Z;
                );
            }

            return parameters;
        }

        #endregion;

        #region Private Methods - Helper Methods;

        /// <summary>
        /// Skin özelliği getirir;
        /// </summary>
        private SkinFeature GetSkinFeature(string featureName)
        {
            foreach (var featureList in _skinFeatures.Values)
            {
                var feature = featureList.FirstOrDefault(f => f.Name == featureName);
                if (feature != null)
                    return feature;
            }
            return null;
        }

        /// <summary>
        /// Base mesh getirir;
        /// </summary>
        private async Task<MeshData> GetBaseMeshForProfileAsync(SkinProfile profile)
        {
            // Bu kısım gerçek mesh loading işlemini yapar;
            // Şimdilik basit bir mesh döndürüyoruz;
            return await Task.FromResult(new MeshData());
        }

        /// <summary>
        /// Displacement'ı mesh'e uygular;
        /// </summary>
        private async Task<SkinVertexData> ApplyDisplacementToMeshAsync(MeshData mesh,
            SkinTexture displacementMap)
        {
            // Gerçek displacement uygulama işlemi;
            return await Task.FromResult(new SkinVertexData());
        }

        /// <summary>
        /// Mesh'i vertex data'ya çevirir;
        /// </summary>
        private SkinVertexData ConvertMeshToVertexData(MeshData mesh)
        {
            return new SkinVertexData();
        }

        /// <summary>
        /// Morph target'ları uygular;
        /// </summary>
        private async Task<SkinVertexData> ApplyMorphTargetsAsync(SkinVertexData vertexData,
            List<MorphTarget> morphTargets)
        {
            // Morph target uygulama işlemi;
            return await Task.FromResult(vertexData);
        }

        /// <summary>
        /// Preview texture oluşturur;
        /// </summary>
        private async Task<byte[]> GeneratePreviewTextureAsync(SkinRenderData renderData,
            PreviewSettings settings)
        {
            // Preview texture generation;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// Preview mesh oluşturur;
        /// </summary>
        private async Task<MeshData> GeneratePreviewMeshAsync(SkinRenderData renderData,
            PreviewSettings settings)
        {
            // Preview mesh generation;
            return await Task.FromResult(new MeshData());
        }

        /// <summary>
        /// Import formatını tespit eder;
        /// </summary>
        private ImportFormat DetectImportFormat(byte[] data)
        {
            // Basit format tespiti;
            if (data.Length < 4)
                return ImportFormat.Unknown;

            // Check for common file signatures;
            if (System.Text.Encoding.UTF8.GetString(data, 0, 4) == "NEDA")
                return ImportFormat.NEDA;

            if (System.Text.Encoding.UTF8.GetString(data, 0, 4) == "FBX")
                return ImportFormat.FBX;

            if (System.Text.Encoding.UTF8.GetString(data, 0, 4) == "glTF")
                return ImportFormat.GLTF;

            if (data[0] == '{' || data[0] == '[') // JSON;
                return ImportFormat.JSON;

            return ImportFormat.Unknown;
        }

        #endregion;

        #region Private Methods - Export/Import Formats;

        /// <summary>
        /// NEDA formatına export;
        /// </summary>
        private byte[] ExportToNedaFormat(SkinProfile profile)
        {
            var exportData = new SkinExport;
            {
                Profile = profile,
                ExportVersion = "1.0",
                ExportDate = DateTime.UtcNow,
                Metadata = new ExportMetadata;
                {
                    Author = "NEDA SkinManager",
                    Software = "NEDA Character Systems",
                    Version = "1.0.0"
                }
            };

            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Unreal Engine formatına export;
        /// </summary>
        private async Task<byte[]> ExportToUnrealFormatAsync(SkinProfile profile)
        {
            // Unreal export işlemi;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// Unity formatına export;
        /// </summary>
        private async Task<byte[]> ExportToUnityFormatAsync(SkinProfile profile)
        {
            // Unity export işlemi;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// FBX formatına export;
        /// </summary>
        private async Task<byte[]> ExportToFbxFormatAsync(SkinProfile profile)
        {
            // FBX export işlemi;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// glTF formatına export;
        /// </summary>
        private async Task<byte[]> ExportToGltfFormatAsync(SkinProfile profile)
        {
            // glTF export işlemi;
            return await Task.FromResult(new byte[0]);
        }

        /// <summary>
        /// JSON formatına export;
        /// </summary>
        private byte[] ExportToJsonFormat(SkinProfile profile)
        {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(profile,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// NEDA formatından import;
        /// </summary>
        private SkinProfile ImportFromNedaFormat(byte[] data)
        {
            var exportData = System.Text.Json.JsonSerializer.Deserialize<SkinExport>(data);
            return exportData.Profile;
        }

        /// <summary>
        /// Unreal Engine formatından import;
        /// </summary>
        private async Task<SkinProfile> ImportFromUnrealFormatAsync(byte[] data)
        {
            // Unreal import işlemi;
            return await Task.FromResult(new SkinProfile("imported", new SkinBaseParameters()));
        }

        /// <summary>
        /// Unity formatından import;
        /// </summary>
        private async Task<SkinProfile> ImportFromUnityFormatAsync(byte[] data)
        {
            // Unity import işlemi;
            return await Task.FromResult(new SkinProfile("imported", new SkinBaseParameters()));
        }

        /// <summary>
        /// FBX formatından import;
        /// </summary>
        private async Task<SkinProfile> ImportFromFbxFormatAsync(byte[] data)
        {
            // FBX import işlemi;
            return await Task.FromResult(new SkinProfile("imported", new SkinBaseParameters()));
        }

        /// <summary>
        /// glTF formatından import;
        /// </summary>
        private async Task<SkinProfile> ImportFromGltfFormatAsync(byte[] data)
        {
            // glTF import işlemi;
            return await Task.FromResult(new SkinProfile("imported", new SkinBaseParameters()));
        }

        /// <summary>
        /// JSON formatından import;
        /// </summary>
        private SkinProfile ImportFromJsonFormat(byte[] data)
        {
            return System.Text.Json.JsonSerializer.Deserialize<SkinProfile>(data);
        }

        #endregion;

        #region Private Methods - Default Content Creators;

        /// <summary>
        /// Basic skin shader içeriği;
        /// </summary>
        private string GetBasicSkinShader()
        {
            return @"// Basic Skin Shader for NEDA;
Shader ""NEDA/Skin/Basic""
{
    Properties;
    {
        _BaseColor (""Base Color"", Color) = (0.75, 0.6, 0.5, 1)
        _BaseMap (""Base Map"", 2D) = ""white"" {}
        _NormalMap (""Normal Map"", 2D) = ""bump"" {}
        _Roughness (""Roughness"", Range(0, 1)) = 0.5;
        _Specular (""Specular"", Range(0, 1)) = 0.5;
    }
    
    SubShader;
    {
        Tags { ""RenderType""=""Opaque"" }
        
        CGPROGRAM;
        #pragma surface surf Standard;
        #pragma target 3.0;
        
        struct Input;
        {
            float2 uv_BaseMap;
        };
        
        sampler2D _BaseMap;
        sampler2D _NormalMap;
        float4 _BaseColor;
        float _Roughness;
        float _Specular;
        
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            o.Albedo = tex2D(_BaseMap, IN.uv_BaseMap).rgb * _BaseColor.rgb;
            o.Normal = UnpackNormal(tex2D(_NormalMap, IN.uv_BaseMap));
            o.Metallic = 0.0;
            o.Smoothness = 1.0 - _Roughness;
            o.Specular = _Specular;
        }
        ENDCG;
    }
    FallBack ""Diffuse""
}";
        }

        /// <summary>
        /// Advanced skin shader içeriği;
        /// </summary>
        private string GetAdvancedSkinShader()
        {
            return @"// Advanced Skin Shader with SSS;
Shader ""NEDA/Skin/Advanced""
{
    Properties;
    {
        // Base properties;
        _BaseColor (""Base Color"", Color) = (0.75, 0.6, 0.5, 1)
        _BaseMap (""Base Map"", 2D) = ""white"" {}
        
        // Normal mapping;
        _NormalMap (""Normal Map"", 2D) = ""bump"" {}
        _NormalScale (""Normal Scale"", Float) = 1.0;
        
        // Specular/Roughness;
        _SpecularMap (""Specular Map"", 2D) = ""white"" {}
        _RoughnessMap (""Roughness Map"", 2D) = ""white"" {}
        _Specular (""Specular"", Range(0, 1)) = 0.5;
        _Roughness (""Roughness"", Range(0, 1)) = 0.5;
        
        // Subsurface Scattering;
        _SSSColor (""SSS Color"", Color) = (0.75, 0.5, 0.45, 1)
        _SSSRadius (""SSS Radius"", Vector) = (0.5, 0.3, 0.1, 0)
        _SSSStrength (""SSS Strength"", Range(0, 2)) = 1.0;
        
        // Clear Coat;
        _ClearCoat (""Clear Coat"", Range(0, 1)) = 0.3;
        _ClearCoatRoughness (""Clear Coat Roughness"", Range(0, 1)) = 0.1;
        
        // Sheen;
        _Sheen (""Sheen"", Range(0, 1)) = 0.2;
        _SheenTint (""Sheen Tint"", Range(0, 1)) = 0.5;
    }
    
    SubShader;
    {
        Tags { ""RenderType""=""Opaque"" ""Queue""=""Geometry"" }
        
        CGPROGRAM;
        #pragma surface surf Standard subsurface:SSSLighting;
        #pragma target 4.0;
        
        // SSS lighting function;
        half4 LightingSSSLighting (SurfaceOutputStandard s, half3 lightDir, half3 viewDir, half atten)
        {
            // Simplified SSS calculation;
            half NdotL = max(0, dot(s.Normal, lightDir));
            half3 sss = s.Albedo * _SSSColor.rgb * _SSSStrength;
            
            half4 c;
            c.rgb = (s.Albedo * _LightColor0.rgb * NdotL + sss * _LightColor0.rgb * (1 - NdotL)) * atten;
            c.a = s.Alpha;
            
            return c;
        }
        
        struct Input;
        {
            float2 uv_BaseMap;
            float2 uv_NormalMap;
            float2 uv_SpecularMap;
            float2 uv_RoughnessMap;
        };
        
        sampler2D _BaseMap;
        sampler2D _NormalMap;
        sampler2D _SpecularMap;
        sampler2D _RoughnessMap;
        
        float4 _BaseColor;
        float4 _SSSColor;
        float3 _SSSRadius;
        float _SSSStrength;
        
        float _NormalScale;
        float _Specular;
        float _Roughness;
        float _ClearCoat;
        float _ClearCoatRoughness;
        float _Sheen;
        float _SheenTint;
        
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Base color;
            o.Albedo = tex2D(_BaseMap, IN.uv_BaseMap).rgb * _BaseColor.rgb;
            
            // Normal mapping;
            o.Normal = UnpackNormal(tex2D(_NormalMap, IN.uv_NormalMap));
            o.Normal.xy *= _NormalScale;
            
            // Specular/Roughness;
            float specularValue = tex2D(_SpecularMap, IN.uv_SpecularMap).r * _Specular;
            float roughnessValue = tex2D(_RoughnessMap, IN.uv_RoughnessMap).r * _Roughness;
            
            o.Metallic = 0.0;
            o.Smoothness = 1.0 - roughnessValue;
            o.Specular = specularValue;
            
            // Additional parameters;
            o.CustomData0 = _ClearCoat; // Clear coat in custom data;
            o.CustomData1 = _ClearCoatRoughness;
            o.CustomData2 = _Sheen;
            o.CustomData3 = _SheenTint;
        }
        ENDCG;
    }
    FallBack ""Standard""
}";
        }

        /// <summary>
        /// SSS skin shader içeriği;
        /// </summary>
        private string GetSSSSkinShader()
        {
            // Kısaltılmış SSS shader;
            return @"// Subsurface Scattering Skin Shader";
        }

        /// <summary>
        /// Displacement skin shader içeriği;
        /// </summary>
        private string GetDisplacementSkinShader()
        {
            // Kısaltılmış displacement shader;
            return @"// Displacement Skin Shader";
        }

        /// <summary>
        /// Varsayılan materyal oluşturur;
        /// </summary>
        private SkinMaterial CreateDefaultMaterial(string name, SkinType skinType, SkinTone skinTone)
        {
            return new SkinMaterial;
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                SkinType = skinType,
                SkinTone = skinTone,
                ShaderName = skinType == SkinType.Human ? "Skin_SSS" : "Skin_Advanced",
                Textures = new Dictionary<string, SkinTexture>(),
                Parameters = new SkinMaterialParameters;
                {
                    SubsurfaceColor = CalculateSubsurfaceColor(skinTone),
                    SubsurfaceRadius = new Vector3(0.5f, 0.3f, 0.1f),
                    Specular = 0.5f,
                    Roughness = 0.4f,
                    ClearCoat = 0.3f,
                    ClearCoatRoughness = 0.1f;
                }
            };
        }

        /// <summary>
        /// Varsayılan preset oluşturur;
        /// </summary>
        private SkinPreset CreateDefaultPreset(string name, int age, SkinTone skinTone,
            HealthCondition health)
        {
            return new SkinPreset;
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Description = $"Default {name} skin preset",
                SkinTone = skinTone,
                AgeParameters = new AgeParameters;
                {
                    Age = age,
                    IncludeWrinkles = age > 30,
                    WrinkleIntensity = Math.Clamp((age - 30) / 50.0f, 0.0f, 0.8f),
                    IncludeAgeSpots = age > 40,
                    AgeSpotIntensity = Math.Clamp((age - 40) / 40.0f, 0.0f, 0.6f),
                    SkinThinning = Math.Clamp((age - 50) / 30.0f, 0.0f, 1.0f)
                },
                HealthParameters = new HealthParameters;
                {
                    HealthLevel = health == HealthCondition.VeryHealthy ? 1.0f :
                                 health == HealthCondition.Sick ? 0.4f : 0.7f,
                    SkinDryness = health == HealthCondition.SunDamaged ? 0.8f : 0.3f,
                    HasFever = health == HealthCondition.Sick || health == HealthCondition.VerySick,
                    InflammationLevel = health == HealthCondition.Sick ? 0.5f : 0.1f;
                },
                Features = new List<AppliedSkinFeature>()
            };
        }

        /// <summary>
        /// Varsayılan özellik oluşturur;
        /// </summary>
        private SkinFeature CreateDefaultFeature(string name, SkinFeatureType featureType)
        {
            return new SkinFeature;
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                FeatureType = featureType,
                Description = $"Default {name} feature",
                Color = GetDefaultColorForFeature(featureType),
                Size = GetDefaultSizeForFeature(featureType),
                BlendMode = GetDefaultBlendModeForFeature(featureType),
                NormalMapInfluence = GetDefaultNormalInfluenceForFeature(featureType),
                AffectsRoughness = featureType == SkinFeatureType.Wrinkles ||
                                   featureType == SkinFeatureType.Scars,
                RequiresSeparateTexture = featureType == SkinFeatureType.Tattoos ||
                                         featureType == SkinFeatureType.Birthmarks;
            };
        }

        /// <summary>
        /// Özellik tipine göre varsayılan renk getirir;
        /// </summary>
        private Vector3 GetDefaultColorForFeature(SkinFeatureType featureType)
        {
            return featureType switch;
            {
                SkinFeatureType.Freckles => new Vector3(0.6f, 0.4f, 0.3f),
                SkinFeatureType.Moles => new Vector3(0.3f, 0.2f, 0.15f),
                SkinFeatureType.Scars => new Vector3(0.9f, 0.8f, 0.7f),
                SkinFeatureType.Tattoos => new Vector3(0.1f, 0.1f, 0.1f), // Black tattoos;
                SkinFeatureType.Birthmarks => new Vector3(0.4f, 0.2f, 0.1f),
                SkinFeatureType.Acne => new Vector3(0.8f, 0.2f, 0.2f),
                SkinFeatureType.Wrinkles => new Vector3(0.5f, 0.4f, 0.3f),
                SkinFeatureType.Rosacea => new Vector3(0.9f, 0.3f, 0.3f),
                SkinFeatureType.Vitiligo => new Vector3(0.95f, 0.9f, 0.85f),
                SkinFeatureType.Bruises => new Vector3(0.4f, 0.2f, 0.6f),
                SkinFeatureType.Blisters => new Vector3(0.9f, 0.8f, 0.6f),
                SkinFeatureType.Rashes => new Vector3(0.8f, 0.4f, 0.3f),
                SkinFeatureType.AgeSpots => new Vector3(0.7f, 0.5f, 0.3f),
                _ => new Vector3(0.5f, 0.5f, 0.5f)
            };
        }

        /// <summary>
        /// Özellik tipine göre varsayılan boyut getirir;
        /// </summary>
        private float GetDefaultSizeForFeature(SkinFeatureType featureType)
        {
            return featureType switch;
            {
                SkinFeatureType.Freckles => 0.01f,
                SkinFeatureType.Moles => 0.02f,
                SkinFeatureType.Scars => 0.05f,
                SkinFeatureType.Tattoos => 0.1f,
                SkinFeatureType.Birthmarks => 0.03f,
                SkinFeatureType.Acne => 0.005f,
                SkinFeatureType.Wrinkles => 0.002f,
                SkinFeatureType.Rosacea => 0.03f,
                SkinFeatureType.Vitiligo => 0.05f,
                SkinFeatureType.Bruises => 0.04f,
                SkinFeatureType.Blisters => 0.008f,
                SkinFeatureType.Rashes => 0.02f,
                SkinFeatureType.AgeSpots => 0.01f,
                _ => 0.01f;
            };
        }

        /// <summary>
        /// Özellik tipine göre varsayılan blend mode getirir;
        /// </summary>
        private BlendMode GetDefaultBlendModeForFeature(SkinFeatureType featureType)
        {
            return featureType switch;
            {
                SkinFeatureType.Tattoos => BlendMode.Multiply,
                SkinFeatureType.Vitiligo => BlendMode.Additive,
                _ => BlendMode.Overlay;
            };
        }

        /// <summary>
        /// Özellik tipine göre varsayılan normal map etkisi getirir;
        /// </summary>
        private float GetDefaultNormalInfluenceForFeature(SkinFeatureType featureType)
        {
            return featureType switch;
            {
                SkinFeatureType.Wrinkles => 0.8f,
                SkinFeatureType.Scars => 0.6f,
                SkinFeatureType.Blisters => 0.4f,
                SkinFeatureType.Acne => 0.3f,
                _ => 0.1f;
            };
        }

        #endregion;

        #region Private Methods - File Operations;

        /// <summary>
        /// Materyalleri dizinden yükler;
        /// </summary>
        private async Task LoadMaterialsFromDirectoryAsync(string directoryPath)
        {
            var materialFiles = Directory.GetFiles(directoryPath, "*.json");

            foreach (var file in materialFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var material = System.Text.Json.JsonSerializer.Deserialize<SkinMaterial>(json);

                    if (material != null)
                    {
                        var key = $"{material.SkinType}_{material.SkinTone}";
                        _skinMaterials[key] = material;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Materyal yükleme hatası ({Path.GetFileName(file)}): {ex.Message}", ex);
                }
            }

            _logger.LogDebug($"{materialFiles.Length} skin materyali yüklendi.");
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
                    var preset = System.Text.Json.JsonSerializer.Deserialize<SkinPreset>(json);

                    if (preset != null)
                    {
                        _skinPresets[preset.Id] = preset;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Preset yükleme hatası ({Path.GetFileName(file)}): {ex.Message}", ex);
                }
            }

            _logger.LogDebug($"{presetFiles.Length} skin preset yüklendi.");
        }

        /// <summary>
        /// Özellikleri dizinden yükler;
        /// </summary>
        private async Task LoadFeaturesFromDirectoryAsync(string directoryPath)
        {
            var featureFiles = Directory.GetFiles(directoryPath, "*.json");

            foreach (var file in featureFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var feature = System.Text.Json.JsonSerializer.Deserialize<SkinFeature>(json);

                    if (feature != null)
                    {
                        if (!_skinFeatures.ContainsKey(feature.FeatureType.ToString()))
                        {
                            _skinFeatures[feature.FeatureType.ToString()] = new List<SkinFeature>();
                        }

                        _skinFeatures[feature.FeatureType.ToString()].Add(feature);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Özellik yükleme hatası ({Path.GetFileName(file)}): {ex.Message}", ex);
                }
            }

            _logger.LogDebug($"{featureFiles.Length} skin özelliği yüklendi.");
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

                lock (_lockObject)
                {
                    _skinProfiles?.Clear();
                    _skinPresets?.Clear();
                    _skinMaterials?.Clear();
                    _skinFeatures?.Clear();
                    _skinCache?.Clear();
                    _cacheTimestamps?.Clear();
                }
            }

            _disposed = true;
        }

        ~SkinManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Skin profili;
    /// </summary>
    public class SkinProfile;
    {
        public string ProfileId { get; set; }
        public string Name { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public int Version { get; set; }

        public SkinBaseParameters BaseParameters { get; set; }
        public SkinTone SkinTone { get; set; }
        public AgeParameters AgeParameters { get; set; }
        public HealthParameters HealthParameters { get; set; }
        public SkinMaterial BaseMaterial { get; set; }
        public List<AppliedSkinFeature> Features { get; private set; }
        public Dictionary<string, string> MaterialOverrides { get; private set; }
        public Dictionary<string, float> FeatureOverrides { get; private set; }
        public RenderQuality QualityLevel { get; set; }

        public SkinProfile(string profileId, SkinBaseParameters baseParameters)
        {
            ProfileId = profileId;
            Name = $"Skin_{profileId}";
            BaseParameters = baseParameters;
            SkinTone = SkinTone.Medium;
            Features = new List<AppliedSkinFeature>();
            MaterialOverrides = new Dictionary<string, string>();
            FeatureOverrides = new Dictionary<string, float>();
            QualityLevel = RenderQuality.High;
        }

        public void AddFeature(AppliedSkinFeature feature)
        {
            Features.Add(feature);
        }

        public bool RemoveFeature(string featureId)
        {
            return Features.RemoveAll(f => f.Feature.Id == featureId) > 0;
        }

        public void SetMaterialOverride(string materialSlot, string texturePath)
        {
            MaterialOverrides[materialSlot] = texturePath;
        }

        public void SetFeatureOverride(string featureId, float intensity)
        {
            FeatureOverrides[featureId] = intensity;
        }

        public SkinProfile Clone()
        {
            return new SkinProfile(ProfileId, BaseParameters)
            {
                Name = Name,
                CreatedDate = CreatedDate,
                LastModified = LastModified,
                Version = Version,
                SkinTone = SkinTone,
                AgeParameters = AgeParameters?.Clone(),
                HealthParameters = HealthParameters?.Clone(),
                BaseMaterial = BaseMaterial?.Clone(),
                Features = Features.Select(f => f.Clone()).ToList(),
                MaterialOverrides = new Dictionary<string, string>(MaterialOverrides),
                FeatureOverrides = new Dictionary<string, float>(FeatureOverrides),
                QualityLevel = QualityLevel;
            };
        }
    }

    /// <summary>
    /// Skin temel parametreleri;
    /// </summary>
    public class SkinBaseParameters;
    {
        public SkinType SkinType { get; set; } = SkinType.Human;
        public Gender Gender { get; set; } = Gender.Male;
        public Ethnicity Ethnicity { get; set; } = Ethnicity.Caucasian;

        public float SkinSmoothness { get; set; } = 0.7f;
        public float SkinRoughness { get; set; } = 0.4f;
        public float SkinSpecular { get; set; } = 0.5f;
        public float SkinOiliness { get; set; } = 0.3f;
        public float SkinMoisture { get; set; } = 0.5f;
        public float SkinClearCoat { get; set; } = 0.3f;
        public float SkinClearCoatRoughness { get; set; } = 0.1f;
        public float SkinAnisotropy { get; set; } = 0.0f;
        public float SkinSheen { get; set; } = 0.2f;
        public float SkinSheenTint { get; set; } = 0.5f;
        public float SkinTransmission { get; set; } = 0.1f;
        public float SkinThickness { get; set; } = 0.5f;

        public bool IncludeSkinPores { get; set; } = true;
        public float PoreSize { get; set; } = 0.01f;
        public float PoreIntensity { get; set; } = 0.5f;
        public float SkinSmoothnessVariation { get; set; } = 0.2f;

        public List<MorphTarget> MorphTargets { get; set; } = new List<MorphTarget>();
    }

    /// <summary>
    /// Yaş parametreleri;
    /// </summary>
    public class AgeParameters;
    {
        public int Age { get; set; } = 25;
        public bool IncludeWrinkles { get; set; } = false;
        public float WrinkleIntensity { get; set; } = 0.0f;
        public bool IncludeAgeSpots { get; set; } = false;
        public float AgeSpotIntensity { get; set; } = 0.0f;
        public float SkinThinning { get; set; } = 0.0f;

        public AgeParameters Clone()
        {
            return new AgeParameters;
            {
                Age = Age,
                IncludeWrinkles = IncludeWrinkles,
                WrinkleIntensity = WrinkleIntensity,
                IncludeAgeSpots = IncludeAgeSpots,
                AgeSpotIntensity = AgeSpotIntensity,
                SkinThinning = SkinThinning;
            };
        }
    }

    /// <summary>
    /// Sağlık parametreleri;
    /// </summary>
    public class HealthParameters;
    {
        public float HealthLevel { get; set; } = 1.0f;
        public float? SkinDryness { get; set; }
        public bool HasFever { get; set; } = false;
        public bool HasJaundice { get; set; } = false;
        public bool PoorCirculation { get; set; } = false;
        public float InflammationLevel { get; set; } = 0.0f;
        public float? Yellowing { get; set; }

        public HealthParameters Clone()
        {
            return new HealthParameters;
            {
                HealthLevel = HealthLevel,
                SkinDryness = SkinDryness,
                HasFever = HasFever,
                HasJaundice = HasJaundice,
                PoorCirculation = PoorCirculation,
                InflammationLevel = InflammationLevel,
                Yellowing = Yellowing;
            };
        }
    }

    /// <summary>
    /// Uygulanmış skin özelliği;
    /// </summary>
    public class AppliedSkinFeature;
    {
        public SkinFeature Feature { get; }
        public float Intensity { get; set; }
        public Vector2 Position { get; set; }
        public float Rotation { get; set; }
        public float Scale { get; set; } = 1.0f;

        public AppliedSkinFeature(SkinFeature feature, float intensity, Vector2 position)
        {
            Feature = feature;
            Intensity = intensity;
            Position = position;
        }

        public AppliedSkinFeature Clone()
        {
            return new AppliedSkinFeature(Feature, Intensity, Position)
            {
                Rotation = Rotation,
                Scale = Scale;
            };
        }
    }

    /// <summary>
    /// Skin özelliği;
    /// </summary>
    public class SkinFeature;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public SkinFeatureType FeatureType { get; set; }
        public string Description { get; set; }
        public Vector3 Color { get; set; }
        public float Size { get; set; }
        public BlendMode BlendMode { get; set; }
        public float NormalMapInfluence { get; set; }
        public float NormalMapScale { get; set; } = 1.0f;
        public bool AffectsRoughness { get; set; }
        public float RoughnessInfluence { get; set; }
        public bool HasDisplacement { get; set; }
        public float DisplacementStrength { get; set; }
        public byte[] DisplacementMap { get; set; }
        public bool RequiresSeparateTexture { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Skin materyali;
    /// </summary>
    public class SkinMaterial;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public SkinType SkinType { get; set; }
        public SkinTone SkinTone { get; set; }
        public string ShaderName { get; set; }
        public Dictionary<string, SkinTexture> Textures { get; set; }
        public SkinMaterialParameters Parameters { get; set; }

        public SkinMaterial Clone()
        {
            return new SkinMaterial;
            {
                Id = Id,
                Name = Name,
                SkinType = SkinType,
                SkinTone = SkinTone,
                ShaderName = ShaderName,
                Textures = Textures?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
                Parameters = Parameters?.Clone()
            };
        }
    }

    /// <summary>
    /// Skin texture;
    /// </summary>
    public class SkinTexture;
    {
        public string Id { get; set; }
        public TextureType TextureType { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public TextureFormat Format { get; set; }
        public byte[] Data { get; set; }
        public int MipLevels { get; set; }
        public bool IsCompressed { get; set; }

        public async Task ReleaseAsync()
        {
            // Texture memory release;
            await Task.CompletedTask;
        }

        public SkinTexture Clone()
        {
            return new SkinTexture;
            {
                Id = Id,
                TextureType = TextureType,
                Width = Width,
                Height = Height,
                Format = Format,
                Data = Data?.ToArray(),
                MipLevels = MipLevels,
                IsCompressed = IsCompressed;
            };
        }
    }

    /// <summary>
    /// Skin materyal parametreleri;
    /// </summary>
    public class SkinMaterialParameters;
    {
        public Vector3 SubsurfaceColor { get; set; }
        public Vector3 SubsurfaceRadius { get; set; }
        public float Specular { get; set; }
        public float Roughness { get; set; }
        public float Metallic { get; set; }
        public float ClearCoat { get; set; }
        public float ClearCoatRoughness { get; set; }
        public float Anisotropy { get; set; }
        public float Sheen { get; set; }
        public float SheenTint { get; set; }
        public float Transmission { get; set; }
        public Vector3 Emission { get; set; }
        public float IOR { get; set; }
        public bool ThinWalled { get; set; }

        public SkinMaterialParameters Clone()
        {
            return new SkinMaterialParameters;
            {
                SubsurfaceColor = SubsurfaceColor,
                SubsurfaceRadius = SubsurfaceRadius,
                Specular = Specular,
                Roughness = Roughness,
                Metallic = Metallic,
                ClearCoat = ClearCoat,
                ClearCoatRoughness = ClearCoatRoughness,
                Anisotropy = Anisotropy,
                Sheen = Sheen,
                SheenTint = SheenTint,
                Transmission = Transmission,
                Emission = Emission,
                IOR = IOR,
                ThinWalled = ThinWalled;
            };
        }
    }

    /// <summary>
    /// Skin render verisi;
    /// </summary>
    public class SkinRenderData;
    {
        public string ProfileId { get; set; }
        public RenderQuality Quality { get; set; }
        public DateTime GeneratedTime { get; set; }

        public SkinTexture BaseTexture { get; set; }
        public SkinTexture NormalMap { get; set; }
        public SkinTexture SpecularMap { get; set; }
        public SkinTexture RoughnessMap { get; set; }
        public SkinTexture DisplacementMap { get; set; }
        public Dictionary<string, SkinTexture> FeatureTextures { get; set; }
        public SkinMaterialParameters MaterialParameters { get; set; }
        public SkinShaderParameters ShaderParameters { get; set; }
        public SkinVertexData VertexData { get; set; }

        public int TextureCount => 4 + (FeatureTextures?.Count ?? 0);
    }

    /// <summary>
    /// Skin shader parametreleri;
    /// </summary>
    public class SkinShaderParameters;
    {
        public string ShaderName { get; set; }
        public string ShaderPath { get; set; }
        public RenderQuality QualityLevel { get; set; }
        public bool UseSSS { get; set; }
        public bool UseClearCoat { get; set; }
        public bool UseAnisotropy { get; set; }
        public bool UseSheen { get; set; }
        public bool UseTransmission { get; set; }
        public int TessellationFactor { get; set; }
        public float DisplacementScale { get; set; }
        public SkinMaterialParameters MaterialParameters { get; set; }
        public Dictionary<string, int> TextureSlots { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Skin vertex verisi;
    /// </summary>
    public class SkinVertexData;
    {
        public int VertexCount { get; set; }
        public int IndexCount { get; set; }
        public byte[] VertexBuffer { get; set; }
        public byte[] IndexBuffer { get; set; }
        public bool HasNormals { get; set; }
        public bool HasTangents { get; set; }
        public bool HasUVs { get; set; }
        public bool HasColors { get; set; }
        public VertexFormat Format { get; set; }
    }

    /// <summary>
    /// Skin preview;
    /// </summary>
    public class SkinPreview;
    {
        public string ProfileId { get; set; }
        public byte[] PreviewTexture { get; set; }
        public MeshData PreviewMesh { get; set; }
        public SkinRenderData RenderData { get; set; }
        public DateTime GeneratedTime { get; set; }
        public PreviewSettings Settings { get; set; }
    }

    /// <summary>
    /// Skin export verisi;
    /// </summary>
    public class SkinExportData;
    {
        public string ProfileId { get; set; }
        public ExportFormat ExportFormat { get; set; }
        public DateTime ExportTime { get; set; }
        public SkinProfile ProfileData { get; set; }
        public byte[] Data { get; set; }
    }

    /// <summary>
    /// Skin yapılandırması;
    /// </summary>
    public class SkinConfiguration;
    {
        public int MaxCacheSize { get; set; } = 100;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(6);
        public RenderQuality DefaultQuality { get; set; } = RenderQuality.High;
        public bool EnableCompression { get; set; } = true;
        public int CompressionQuality { get; set; } = 90;
        public int MaxTextureSize { get; set; } = 4096;
        public int MinTextureSize { get; set; } = 256;
        public bool EnableMipMaps { get; set; } = true;
        public int MaxMipLevels { get; set; } = 12;
        public bool EnableAsyncLoading { get; set; } = true;
        public int ThreadPoolSize { get; set; } = Environment.ProcessorCount;
    }

    /// <summary>
    /// Skin kalite ayarları;
    /// </summary>
    public class SkinQualitySettings;
    {
        public RenderQuality CurrentQuality { get; set; } = RenderQuality.High;
        public bool EnableSubsurfaceScattering { get; set; } = true;
        public SSSQuality SSSQuality { get; set; } = SSSQuality.High;
        public bool EnableDisplacement { get; set; } = true;
        public float DisplacementScale { get; set; } = 0.1f;
        public int TessellationFactor { get; set; } = 3;
        public bool EnableParallaxOcclusion { get; set; } = true;
        public ParallaxQuality ParallaxQuality { get; set; } = ParallaxQuality.Medium;
        public bool EnableScreenSpaceReflections { get; set; } = false;
        public bool EnableAmbientOcclusion { get; set; } = true;
        public AOQuality AOQuality { get; set; } = AOQuality.Medium;
        public bool EnableDynamicTessellation { get; set; } = true;
        public int MaxTessellationFactor { get; set; } = 8;
        public int MinTessellationFactor { get; set; } = 1;
        public bool EnableAsyncTextureLoading { get; set; } = true;
        public bool TextureStreamingEnabled { get; set; } = true;
        public int MaxStreamingTextures { get; set; } = 50;
        public bool EnableTextureCompression { get; set; } = true;
        public TextureCompressionFormat CompressionFormat { get; set; } = TextureCompressionFormat.BC7;
    }

    /// <summary>
    /// Skin preset;
    /// </summary>
    public class SkinPreset;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public SkinBaseParameters BaseParameters { get; set; }
        public SkinTone? SkinTone { get; set; }
        public AgeParameters AgeParameters { get; set; }
        public HealthParameters HealthParameters { get; set; }
        public List<AppliedSkinFeature> Features { get; set; } = new List<AppliedSkinFeature>();
    }

    /// <summary>
    /// Skin profile bilgisi;
    /// </summary>
    public class SkinProfileInfo;
    {
        public string ProfileId { get; set; }
        public string Name { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public int Version { get; set; }
        public SkinTone SkinTone { get; set; }
        public int Age { get; set; }
        public RenderQuality QualityLevel { get; set; }
    }

    /// <summary>
    /// Skin profile güncelleme;
    /// </summary>
    public class SkinProfileUpdate;
    {
        public SkinBaseParameters BaseParameters { get; set; }
        public SkinTone? SkinTone { get; set; }
        public AgeParameters AgeParameters { get; set; }
        public HealthParameters HealthParameters { get; set; }
        public Dictionary<string, string> MaterialOverrides { get; set; }
        public Dictionary<string, float> FeatureOverrides { get; set; }
    }

    /// <summary>
    /// Preview ayarları;
    /// </summary>
    public class PreviewSettings;
    {
        public int Width { get; set; } = 512;
        public int Height { get; set; } = 512;
        public RenderQuality Quality { get; set; } = RenderQuality.Medium;
        public bool ShowWireframe { get; set; } = false;
        public bool ShowTexture { get; set; } = true;
        public Vector3 CameraPosition { get; set; } = new Vector3(0, 0, 2);
        public Vector3 LightDirection { get; set; } = new Vector3(0.5f, 0.5f, 0.5f);
    }

    /// <summary>
    /// Cached skin verisi;
    /// </summary>
    public class CachedSkinData;
    {
        public string ProfileId { get; set; }
        public RenderQuality Quality { get; set; }
        public SkinRenderData RenderData { get; set; }
        public DateTime CachedTime { get; set; }
    }

    /// <summary>
    /// Skin tipi;
    /// </summary>
    public enum SkinType;
    {
        Human,
        Alien,
        Creature,
        Fantasy,
        Robot,
        Zombie,
        Vampire,
        Werewolf;
    }

    /// <summary>
    /// Skin tonu;
    /// </summary>
    public enum SkinTone;
    {
        Porcelain = 0,
        Fair = 1,
        Light = 2,
        Medium = 3,
        Olive = 4,
        Tan = 5,
        Brown = 6,
        Dark = 7,
        Ebony = 8,
        Green = 9,
        Blue = 10,
        Red = 11,
        Purple = 12,
        Gray = 13,
        White = 14,
        Black = 15;
    }

    /// <summary>
    /// Cinsiyet;
    /// </summary>
    public enum Gender;
    {
        Male,
        Female,
        Other;
    }

    /// <summary>
    /// Etnik köken;
    /// </summary>
    public enum Ethnicity;
    {
        Caucasian,
        Asian,
        African,
        Hispanic,
        MiddleEastern,
        Mixed,
        Other;
    }

    /// <summary>
    /// Skin özellik tipi;
    /// </summary>
    public enum SkinFeatureType;
    {
        Freckles,
        Moles,
        Scars,
        Tattoos,
        Birthmarks,
        Acne,
        Wrinkles,
        Rosacea,
        Vitiligo,
        Bruises,
        Blisters,
        Rashes,
        AgeSpots;
    }

    /// <summary>
    /// Blend modu;
    /// </summary>
    public enum BlendMode;
    {
        Overlay,
        Multiply,
        Additive,
        Screen,
        Difference,
        Exclusion;
    }

    /// <summary>
    /// Render kalitesi;
    /// </summary>
    public enum RenderQuality;
    {
        Low,
        Medium,
        High,
        Ultra;
    }

    /// <summary>
    /// Texture tipi;
    /// </summary>
    public enum TextureType;
    {
        BaseColor,
        NormalMap,
        SpecularMap,
        RoughnessMap,
        DisplacementMap,
        AmbientOcclusion,
        Emissive,
        FeatureMask;
    }

    /// <summary>
    /// Texture formatı;
    /// </summary>
    public enum TextureFormat;
    {
        R8,
        RG8,
        RGB8,
        RGBA8,
        R16F,
        RG16F,
        RGB16F,
        RGBA16F,
        R32F,
        RG32F,
        RGB32F,
        RGBA32F;
    }

    /// <summary>
    /// Vertex formatı;
    /// </summary>
    public enum VertexFormat;
    {
        Position,
        PositionNormal,
        PositionNormalUV,
        PositionNormalUVTangent,
        PositionNormalUVTangentColor;
    }

    /// <summary>
    /// Export formatı;
    /// </summary>
    public enum ExportFormat;
    {
        NEDA,
        UnrealEngine,
        Unity,
        FBX,
        GLTF,
        JSON;
    }

    /// <summary>
    /// Import formatı;
    /// </summary>
    public enum ImportFormat;
    {
        AutoDetect,
        NEDA,
        UnrealEngine,
        Unity,
        FBX,
        GLTF,
        JSON,
        Unknown;
    }

    /// <summary>
    /// Sağlık durumu;
    /// </summary>
    public enum HealthCondition;
    {
        VeryHealthy,
        Healthy,
        Normal,
        Sick,
        VerySick,
        Aging,
        SunDamaged;
    }

    /// <summary>
    /// Yaşlanma yoğunluğu;
    /// </summary>
    public enum AgingIntensity;
    {
        Light,
        Natural,
        Heavy,
        Extreme;
    }

    /// <summary>
    /// Çevresel durum;
    /// </summary>
    public enum EnvironmentalCondition;
    {
        SunExposed,
        Cold,
        Humid,
        Polluted,
        Windy,
        Underwater;
    }

    /// <summary>
    /// SSS kalitesi;
    /// </summary>
    public enum SSSQuality;
    {
        Off,
        Low,
        Medium,
        High,
        Ultra;
    }

    /// <summary>
    /// Parallax kalitesi;
    /// </summary>
    public enum ParallaxQuality;
    {
        Off,
        Low,
        Medium,
        High;
    }

    /// <summary>
    /// AO kalitesi;
    /// </summary>
    public enum AOQuality;
    {
        Off,
        Low,
        Medium,
        High,
        Ultra;
    }

    /// <summary>
    /// Texture sıkıştırma formatı;
    /// </summary>
    public enum TextureCompressionFormat;
    {
        None,
        DXT1,
        DXT5,
        BC3,
        BC5,
        BC7,
        ASTC;
    }

    /// <summary>
    /// SkinManager özel istisnası;
    /// </summary>
    public class SkinManagerException : Exception
    {
        public SkinManagerException(string message) : base(message) { }
        public SkinManagerException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    // Helper classes for internal use;
    internal class BaseTextureParameters { /* ... */ }
    internal class NormalMapParameters { /* ... */ }
    internal class SpecularMapParameters { /* ... */ }
    internal class RoughnessMapParameters { /* ... */ }
    internal class DisplacementMapParameters { /* ... */ }
    internal class FeatureTextureParameters { /* ... */ }
    internal class AgeEffects { /* ... */ }
    internal class HealthEffects { /* ... */ }
    internal class FeatureNormalData { /* ... */ }
    internal class FeatureDisplacement { /* ... */ }
    internal class SkinShaderLibrary { /* ... */ }
    internal class SkinExport { /* ... */ }
    internal class ExportMetadata { /* ... */ }
    internal class MeshData { /* ... */ }

    #endregion;
}
