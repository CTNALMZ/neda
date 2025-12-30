using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.RenderManager;
using NEDA.GameDesign.LevelDesign.WorldBuilding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.GameDesign.LevelDesign.LightingDesign;
{
    /// <summary>
    /// Işık yönetimi için ana sınıf. Sahnedeki tüm ışık kaynaklarını yönetir.
    /// </summary>
    public class LightManager : ILightManager, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IRenderEngine _renderEngine;
        private readonly AppConfig _config;
        private readonly Dictionary<string, LightInstance> _lights;
        private readonly Dictionary<LightType, List<LightInstance>> _lightsByType;
        private readonly LightSettings _globalSettings;
        private readonly object _syncLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;

        /// <summary>
        /// Işık yönetim sistemi oluşturucu;
        /// </summary>
        public LightManager(ILogger logger, IRenderEngine renderEngine, AppConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _renderEngine = renderEngine ?? throw new ArgumentNullException(nameof(renderEngine));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _lights = new Dictionary<string, LightInstance>(StringComparer.OrdinalIgnoreCase);
            _lightsByType = new Dictionary<LightType, List<LightInstance>>();
            _globalSettings = new LightSettings();

            InitializeLightTypeCollections();
        }

        /// <summary>
        /// Işık yöneticisini başlat;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                await LoadConfigurationAsync();
                await InitializeRenderSystemAsync();
                _isInitialized = true;

                _logger.LogInformation("LightManager initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize LightManager.");
                throw;
            }
        }

        /// <summary>
        /// Yeni bir ışık oluştur ve sahneye ekle;
        /// </summary>
        public async Task<LightInstance> CreateLightAsync(LightCreateParams createParams)
        {
            ValidateNotDisposed();

            if (createParams == null)
                throw new ArgumentNullException(nameof(createParams));

            if (string.IsNullOrWhiteSpace(createParams.Name))
                throw new ArgumentException("Light name cannot be null or empty.", nameof(createParams));

            lock (_syncLock)
            {
                if (_lights.ContainsKey(createParams.Name))
                    throw new InvalidOperationException($"Light with name '{createParams.Name}' already exists.");
            }

            try
            {
                var lightInstance = new LightInstance(createParams);

                await InitializeLightInstanceAsync(lightInstance);

                lock (_syncLock)
                {
                    _lights.Add(createParams.Name, lightInstance);
                    _lightsByType[lightInstance.Type].Add(lightInstance);
                }

                await ApplyLightToSceneAsync(lightInstance);

                _logger.LogDebug($"Created light: {createParams.Name} ({lightInstance.Type})");
                return lightInstance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create light: {createParams.Name}");
                throw;
            }
        }

        /// <summary>
        /// İsimle ışık bul;
        /// </summary>
        public LightInstance GetLight(string name)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            lock (_syncLock)
            {
                return _lights.TryGetValue(name, out var light) ? light : null;
            }
        }

        /// <summary>
        /// Tüm ışıkları getir;
        /// </summary>
        public IReadOnlyList<LightInstance> GetAllLights()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _lights.Values.ToList();
            }
        }

        /// <summary>
        /// Türüne göre ışıkları getir;
        /// </summary>
        public IReadOnlyList<LightInstance> GetLightsByType(LightType type)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _lightsByType.TryGetValue(type, out var lights)
                    ? lights.ToList()
                    : new List<LightInstance>();
            }
        }

        /// <summary>
        /// Işık özelliklerini güncelle;
        /// </summary>
        public async Task UpdateLightAsync(string name, LightUpdateParams updateParams)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            if (updateParams == null)
                throw new ArgumentNullException(nameof(updateParams));

            LightInstance light;
            lock (_syncLock)
            {
                if (!_lights.TryGetValue(name, out light))
                    throw new KeyNotFoundException($"Light '{name}' not found.");
            }

            try
            {
                // Eğer tür değiştiyse, eski tür listesinden çıkar;
                if (updateParams.Type.HasValue && updateParams.Type.Value != light.Type)
                {
                    lock (_syncLock)
                    {
                        _lightsByType[light.Type].Remove(light);
                        _lightsByType[updateParams.Type.Value].Add(light);
                    }
                }

                await light.UpdateAsync(updateParams);
                await UpdateLightInSceneAsync(light);

                _logger.LogDebug($"Updated light: {name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update light: {name}");
                throw;
            }
        }

        /// <summary>
        /// Işığı sahnenen kaldır;
        /// </summary>
        public async Task RemoveLightAsync(string name)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            LightInstance light;
            lock (_syncLock)
            {
                if (!_lights.TryGetValue(name, out light))
                    return;
            }

            try
            {
                await RemoveLightFromSceneAsync(light);

                lock (_syncLock)
                {
                    _lights.Remove(name);
                    _lightsByType[light.Type].Remove(light);
                }

                light.Dispose();

                _logger.LogDebug($"Removed light: {name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove light: {name}");
                throw;
            }
        }

        /// <summary>
        /// Global ışık ayarlarını güncelle;
        /// </summary>
        public async Task UpdateGlobalSettingsAsync(LightSettings settings)
        {
            ValidateNotDisposed();

            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            _globalSettings.UpdateFrom(settings);

            try
            {
                await ApplyGlobalSettingsToSceneAsync();

                _logger.LogInformation("Global light settings updated.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update global light settings.");
                throw;
            }
        }

        /// <summary>
        /// Gölge kalitesini ayarla;
        /// </summary>
        public async Task SetShadowQualityAsync(ShadowQuality quality)
        {
            ValidateNotDisposed();

            _globalSettings.ShadowQuality = quality;

            try
            {
                await _renderEngine.UpdateShadowSettingsAsync(new ShadowSettings;
                {
                    Quality = quality,
                    Resolution = GetShadowResolutionForQuality(quality),
                    CascadeCount = GetCascadeCountForQuality(quality)
                });

                await UpdateAllLightsShadowSettingsAsync();

                _logger.LogInformation($"Shadow quality set to: {quality}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set shadow quality: {quality}");
                throw;
            }
        }

        /// <summary>
        /// Atmosferik ışık efektlerini güncelle;
        /// </summary>
        public async Task UpdateAtmosphericLightingAsync(AtmosphericParams atmosphericParams)
        {
            ValidateNotDisposed();

            if (atmosphericParams == null)
                throw new ArgumentNullException(nameof(atmosphericParams));

            try
            {
                await _renderEngine.SetAtmosphericLightingAsync(atmosphericParams);

                // Gün ışığı gibi directional ışıkları güncelle;
                var directionalLights = GetLightsByType(LightType.Directional);
                foreach (var light in directionalLights)
                {
                    if (light.IsSunLight)
                    {
                        await light.UpdateAtmosphericParamsAsync(atmosphericParams);
                    }
                }

                _logger.LogDebug("Atmospheric lighting updated.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update atmospheric lighting.");
                throw;
            }
        }

        /// <summary>
        /// Performans için ışıkları optimize et;
        /// </summary>
        public async Task OptimizePerformanceAsync(PerformanceProfile profile)
        {
            ValidateNotDisposed();

            try
            {
                foreach (var light in GetAllLights())
                {
                    await light.OptimizeForPerformanceAsync(profile);
                }

                await _renderEngine.OptimizeLightingAsync(profile);

                _logger.LogInformation($"Lighting optimized for profile: {profile}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to optimize lighting performance.");
                throw;
            }
        }

        /// <summary>
        /// Sahnedeki tüm ışıkları kapat/aç;
        /// </summary>
        public async Task SetAllLightsEnabledAsync(bool enabled)
        {
            ValidateNotDisposed();

            try
            {
                foreach (var light in GetAllLights())
                {
                    await light.SetEnabledAsync(enabled);
                }

                await _renderEngine.SetGlobalLightEnabledAsync(enabled);

                _logger.LogDebug($"All lights {(enabled ? "enabled" : "disabled")}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set all lights enabled: {enabled}");
                throw;
            }
        }

        /// <summary>
        /// Işık gruplarını yönet;
        /// </summary>
        public async Task<LightGroup> CreateLightGroupAsync(string groupName)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(groupName))
                throw new ArgumentNullException(nameof(groupName));

            var group = new LightGroup(groupName);

            try
            {
                await _renderEngine.RegisterLightGroupAsync(group);

                _logger.LogDebug($"Created light group: {groupName}");
                return group;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create light group: {groupName}");
                throw;
            }
        }

        /// <summary>
        /// Işık haritası (lightmap) oluştur;
        /// </summary>
        public async Task GenerateLightmapAsync(LightmapGenerationParams generationParams)
        {
            ValidateNotDisposed();

            if (generationParams == null)
                throw new ArgumentNullException(nameof(generationParams));

            try
            {
                _logger.LogInformation("Starting lightmap generation...");

                // Statik ışıkları topla;
                var staticLights = GetAllLights().Where(l => l.IsStatic).ToList();

                // Baketime hesaplamalarını yap;
                await _renderEngine.BeginLightmapBakeAsync(generationParams);

                // Her ışık için baking işlemi;
                foreach (var light in staticLights)
                {
                    await light.BakeToLightmapAsync(generationParams);
                }

                await _renderEngine.FinalizeLightmapBakeAsync();

                _logger.LogInformation($"Lightmap generation completed. Baked {staticLights.Count} lights.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lightmap generation failed.");
                await _renderEngine.CancelLightmapBakeAsync();
                throw;
            }
        }

        /// <summary>
        /// Kaynakları serbest bırak;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            lock (_syncLock)
            {
                foreach (var light in _lights.Values)
                {
                    light.Dispose();
                }

                _lights.Clear();
                _lightsByType.Clear();

                _renderEngine?.Dispose();

                _isDisposed = true;
            }

            _logger.LogDebug("LightManager disposed.");
        }

        #region Private Methods;

        private void InitializeLightTypeCollections()
        {
            foreach (LightType type in Enum.GetValues(typeof(LightType)))
            {
                _lightsByType[type] = new List<LightInstance>();
            }
        }

        private async Task LoadConfigurationAsync()
        {
            var lightingConfig = _config.GetSection<LightingConfiguration>("Lighting");

            _globalSettings.AmbientIntensity = lightingConfig.AmbientIntensity;
            _globalSettings.ShadowQuality = lightingConfig.ShadowQuality;
            _globalSettings.MaxActiveLights = lightingConfig.MaxActiveLights;
            _globalSettings.UseHDR = lightingConfig.UseHDR;
            _globalSettings.GlobalIllumination = lightingConfig.GlobalIllumination;

            await Task.CompletedTask;
        }

        private async Task InitializeRenderSystemAsync()
        {
            await _renderEngine.InitializeLightingSystemAsync(_globalSettings);
        }

        private async Task InitializeLightInstanceAsync(LightInstance light)
        {
            await light.InitializeAsync(_renderEngine);
        }

        private async Task ApplyLightToSceneAsync(LightInstance light)
        {
            await _renderEngine.AddLightAsync(light.GetRenderData());
        }

        private async Task UpdateLightInSceneAsync(LightInstance light)
        {
            await _renderEngine.UpdateLightAsync(light.Id, light.GetRenderData());
        }

        private async Task RemoveLightFromSceneAsync(LightInstance light)
        {
            await _renderEngine.RemoveLightAsync(light.Id);
        }

        private async Task ApplyGlobalSettingsToSceneAsync()
        {
            await _renderEngine.UpdateGlobalLightSettingsAsync(_globalSettings);
        }

        private async Task UpdateAllLightsShadowSettingsAsync()
        {
            foreach (var light in GetAllLights())
            {
                if (light.CastShadows)
                {
                    await light.UpdateShadowSettingsAsync(_globalSettings.ShadowQuality);
                }
            }
        }

        private ShadowResolution GetShadowResolutionForQuality(ShadowQuality quality)
        {
            return quality switch;
            {
                ShadowQuality.Low => ShadowResolution._512x512,
                ShadowQuality.Medium => ShadowResolution._1024x1024,
                ShadowQuality.High => ShadowResolution._2048x2048,
                ShadowQuality.Ultra => ShadowResolution._4096x4096,
                _ => ShadowResolution._1024x1024;
            };
        }

        private int GetCascadeCountForQuality(ShadowQuality quality)
        {
            return quality switch;
            {
                ShadowQuality.Low => 1,
                ShadowQuality.Medium => 2,
                ShadowQuality.High => 3,
                ShadowQuality.Ultra => 4,
                _ => 2;
            };
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(LightManager));
        }

        #endregion;
    }

    /// <summary>
    /// Işık türleri enum'ı;
    /// </summary>
    public enum LightType;
    {
        Directional = 0,
        Point = 1,
        Spot = 2,
        Area = 3,
        Ambient = 4;
    }

    /// <summary>
    /// Işık oluşturma parametreleri;
    /// </summary>
    public class LightCreateParams;
    {
        public string Name { get; set; }
        public LightType Type { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Color Color { get; set; } = Color.White;
        public float Intensity { get; set; } = 1.0f;
        public float Range { get; set; } = 10.0f;
        public float InnerAngle { get; set; } = 30.0f;
        public float OuterAngle { get; set; } = 45.0f;
        public bool CastShadows { get; set; } = true;
        public bool IsStatic { get; set; } = false;
        public bool Enabled { get; set; } = true;
        public bool IsSunLight { get; set; } = false;
        public LightFalloff Falloff { get; set; } = LightFalloff.InverseSquare;
        public string LightCookie { get; set; }
    }

    /// <summary>
    /// Işık güncelleme parametreleri;
    /// </summary>
    public class LightUpdateParams;
    {
        public LightType? Type { get; set; }
        public Vector3? Position { get; set; }
        public Quaternion? Rotation { get; set; }
        public Color? Color { get; set; }
        public float? Intensity { get; set; }
        public float? Range { get; set; }
        public float? InnerAngle { get; set; }
        public float? OuterAngle { get; set; }
        public bool? CastShadows { get; set; }
        public bool? Enabled { get; set; }
        public LightFalloff? Falloff { get; set; }
    }

    /// <summary>
    /// Global ışık ayarları;
    /// </summary>
    public class LightSettings;
    {
        public float AmbientIntensity { get; set; } = 0.1f;
        public Color AmbientColor { get; set; } = new Color(0.2f, 0.2f, 0.3f);
        public ShadowQuality ShadowQuality { get; set; } = ShadowQuality.High;
        public int MaxActiveLights { get; set; } = 32;
        public bool UseHDR { get; set; } = true;
        public bool GlobalIllumination { get; set; } = false;
        public float Exposure { get; set; } = 1.0f;
        public float Gamma { get; set; } = 2.2f;

        public void UpdateFrom(LightSettings other)
        {
            AmbientIntensity = other.AmbientIntensity;
            AmbientColor = other.AmbientColor;
            ShadowQuality = other.ShadowQuality;
            MaxActiveLights = other.MaxActiveLights;
            UseHDR = other.UseHDR;
            GlobalIllumination = other.GlobalIllumination;
            Exposure = other.Exposure;
            Gamma = other.Gamma;
        }
    }

    /// <summary>
    /// Atmosferik parametreler;
    /// </summary>
    public class AtmosphericParams;
    {
        public Color SkyColor { get; set; } = Color.SkyBlue;
        public Color HorizonColor { get; set; } = Color.LightBlue;
        public Color GroundColor { get; set; } = Color.LightGray;
        public float SunIntensity { get; set; } = 1.0f;
        public float AtmosphereDensity { get; set; } = 0.5f;
        public float RayleighScattering { get; set; } = 0.0025f;
        public float MieScattering { get; set; } = 0.0010f;
        public Vector3 SunDirection { get; set; } = new Vector3(0, -1, 0);
    }

    /// <summary>
    /// Işık grupları için sınıf;
    /// </summary>
    public class LightGroup;
    {
        public string Name { get; }
        public List<string> LightIds { get; }
        public bool Enabled { get; private set; } = true;
        public Color GroupTint { get; private set; } = Color.White;
        public float GroupIntensity { get; private set; } = 1.0f;

        public LightGroup(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            LightIds = new List<string>();
        }

        public void AddLight(string lightId) => LightIds.Add(lightId);
        public void RemoveLight(string lightId) => LightIds.Remove(lightId);
        public void SetEnabled(bool enabled) => Enabled = enabled;
        public void SetTint(Color tint) => GroupTint = tint;
        public void SetIntensity(float intensity) => GroupIntensity = Math.Clamp(intensity, 0, 10);
    }

    /// <summary>
    /// Gölge kalitesi enum'ı;
    /// </summary>
    public enum ShadowQuality;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3;
    }

    /// <summary>
    /// Gölge çözünürlüğü enum'ı;
    /// </summary>
    public enum ShadowResolution;
    {
        _256x256 = 256,
        _512x512 = 512,
        _1024x1024 = 1024,
        _2048x2048 = 2048,
        _4096x4096 = 4096;
    }

    /// <summary>
    /// Işık sönümleme tipi;
    /// </summary>
    public enum LightFalloff;
    {
        Linear = 0,
        Inverse = 1,
        InverseSquare = 2,
        Custom = 3;
    }

    /// <summary>
    /// Performans profili enum'ı;
    /// </summary>
    public enum PerformanceProfile;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3,
        Custom = 4;
    }

    /// <summary>
    /// Lightmap oluşturma parametreleri;
    /// </summary>
    public class LightmapGenerationParams;
    {
        public int Resolution { get; set; } = 1024;
        public float SampleDistance { get; set; } = 50.0f;
        public int SampleCount { get; set; } = 256;
        public bool GenerateAmbientOcclusion { get; set; } = true;
        public bool CompressLightmaps { get; set; } = true;
        public string OutputPath { get; set; }
        public LightmapFormat Format { get; set; } = LightmapFormat.BC7;
    }

    /// <summary>
    /// Lightmap formatı enum'ı;
    /// </summary>
    public enum LightmapFormat;
    {
        RGB24 = 0,
        RGBA32 = 1,
        BC7 = 2,
        BC6H = 3;
    }

    /// <summary>
    /// Işık yönetimi interface'i;
    /// </summary>
    public interface ILightManager : IDisposable
    {
        Task InitializeAsync();
        Task<LightInstance> CreateLightAsync(LightCreateParams createParams);
        LightInstance GetLight(string name);
        IReadOnlyList<LightInstance> GetAllLights();
        IReadOnlyList<LightInstance> GetLightsByType(LightType type);
        Task UpdateLightAsync(string name, LightUpdateParams updateParams);
        Task RemoveLightAsync(string name);
        Task UpdateGlobalSettingsAsync(LightSettings settings);
        Task SetShadowQualityAsync(ShadowQuality quality);
        Task UpdateAtmosphericLightingAsync(AtmosphericParams atmosphericParams);
        Task OptimizePerformanceAsync(PerformanceProfile profile);
        Task SetAllLightsEnabledAsync(bool enabled);
        Task<LightGroup> CreateLightGroupAsync(string groupName);
        Task GenerateLightmapAsync(LightmapGenerationParams generationParams);
    }
}
