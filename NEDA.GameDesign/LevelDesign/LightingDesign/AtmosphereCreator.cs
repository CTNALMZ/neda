using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.RenderManager;
using NEDA.GameDesign.LevelDesign.WorldBuilding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.GameDesign.LevelDesign.LightingDesign;
{
    /// <summary>
    /// Atmosferik efektler ve gökyüzü sistemi oluşturucu;
    /// </summary>
    public class AtmosphereCreator : IAtmosphereCreator, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IRenderEngine _renderEngine;
        private readonly IWeatherSystem _weatherSystem;
        private readonly AppConfig _config;
        private readonly AtmosphericSettings _settings;
        private readonly object _syncLock = new object();

        private AtmosphereState _currentState;
        private AtmosphereProfile _currentProfile;
        private Dictionary<string, AtmosphereProfile> _profiles;
        private List<AtmosphericLayer> _layers;
        private AtmosphericCache _cache;
        private bool _isInitialized;
        private bool _isDisposed;
        private CancellationTokenSource _animationTokenSource;
        private DateTime _lastUpdateTime;

        /// <summary>
        /// Atmosferik yaratıcı constructor;
        /// </summary>
        public AtmosphereCreator(
            ILogger logger,
            IRenderEngine renderEngine,
            IWeatherSystem weatherSystem,
            AppConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _renderEngine = renderEngine ?? throw new ArgumentNullException(nameof(renderEngine));
            _weatherSystem = weatherSystem ?? throw new ArgumentNullException(nameof(weatherSystem));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _settings = LoadSettings();
            _currentState = new AtmosphereState();
            _profiles = new Dictionary<string, AtmosphereProfile>();
            _layers = new List<AtmosphericLayer>();
            _cache = new AtmosphericCache();
            _animationTokenSource = new CancellationTokenSource();

            InitializeDefaultProfiles();

            _logger.LogInformation("AtmosphereCreator initialized.");
        }

        /// <summary>
        /// Atmosfer sistemini başlat;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                await LoadConfigurationAsync();
                await InitializeRenderSystemAsync();
                await InitializeLayersAsync();

                _currentState.TimeOfDay = CalculateCurrentTimeOfDay();
                await UpdateAtmosphereFromTimeAsync(_currentState.TimeOfDay);

                _isInitialized = true;

                _logger.LogInformation("AtmosphereCreator initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AtmosphereCreator.");
                throw;
            }
        }

        /// <summary>
        /// Profil ile atmosfer oluştur;
        /// </summary>
        public async Task<AtmosphereState> CreateAtmosphereAsync(
            string profileName,
            CreationParameters parameters = null)
        {
            ValidateNotDisposed();
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentNullException(nameof(profileName));

            parameters ??= new CreationParameters();

            try
            {
                var profile = GetProfile(profileName);
                if (profile == null)
                    throw new ArgumentException($"Atmosphere profile not found: {profileName}");

                _currentProfile = profile;

                _logger.LogInformation($"Creating atmosphere with profile: {profile.Name}");

                // Profil ayarlarını uygula;
                await ApplyProfileAsync(profile, parameters);

                // Atmosferik katmanları oluştur;
                await CreateAtmosphericLayersAsync(profile);

                // Fiziksel hesaplamaları yap;
                await CalculatePhysicalPropertiesAsync();

                // Render sistemine aktar;
                await ApplyToRenderSystemAsync();

                // Cache'e kaydet;
                await CacheCurrentStateAsync();

                _logger.LogDebug($"Atmosphere created: {profile.Name}");
                return _currentState;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create atmosphere: {profileName}");
                throw;
            }
        }

        /// <summary>
        /// Zaman bazlı atmosfer güncellemesi;
        /// </summary>
        public async Task UpdateAtmosphereFromTimeAsync(TimeOfDay time)
        {
            ValidateNotDisposed();
            EnsureInitialized();

            try
            {
                _currentState.TimeOfDay = time;

                // Güneş pozisyonunu hesapla;
                var sunPosition = CalculateSunPosition(time);
                _currentState.SunPosition = sunPosition;

                // Ay pozisyonunu hesapla;
                var moonPosition = CalculateMoonPosition(time);
                _currentState.MoonPosition = moonPosition;

                // Işık renklerini güncelle;
                await UpdateLightColorsFromTimeAsync(time);

                // Atmosferik saçılmayı güncelle;
                await UpdateAtmosphericScatteringAsync(time);

                // Bulutları güncelle;
                await UpdateCloudsFromTimeAsync(time);

                // Render sistemine uygula;
                await ApplyTimeBasedChangesAsync();

                _currentState.LastUpdateTime = DateTime.UtcNow;

                _logger.LogDebug($"Atmosphere updated for time: {time}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update atmosphere for time: {time}");
                throw;
            }
        }

        /// <summary>
        /// Hava durumu efektlerini uygula;
        /// </summary>
        public async Task ApplyWeatherEffectsAsync(
            WeatherCondition condition,
            WeatherParameters parameters = null)
        {
            ValidateNotDisposed();
            EnsureInitialized();

            if (condition == null)
                throw new ArgumentNullException(nameof(condition));

            parameters ??= new WeatherParameters();

            try
            {
                _currentState.WeatherCondition = condition;

                _logger.LogInformation($"Applying weather effects: {condition.Name}");

                // Sis efektini ayarla;
                if (condition.HasFog)
                {
                    await ApplyFogEffectAsync(condition.FogDensity, condition.FogColor);
                }

                // Yağmur efekti;
                if (condition.HasRain)
                {
                    await ApplyRainEffectAsync(condition.RainIntensity, condition.RainDirection);
                }

                // Kar efekti;
                if (condition.HasSnow)
                {
                    await ApplySnowEffectAsync(condition.SnowIntensity, condition.SnowCoverage);
                }

                // Rüzgar efekti;
                if (condition.HasWind)
                {
                    await ApplyWindEffectAsync(condition.WindSpeed, condition.WindDirection);
                }

                // Fırtına efekti;
                if (condition.HasStorm)
                {
                    await ApplyStormEffectAsync(condition.LightningFrequency);
                }

                // Atmosferik yoğunluğu güncelle;
                await UpdateAtmosphericDensityAsync(condition.AtmosphericDensity);

                // Render sistemine uygula;
                await ApplyWeatherToRenderSystemAsync();

                _logger.LogDebug($"Weather effects applied: {condition.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply weather effects: {condition.Name}");
                throw;
            }
        }

        /// <summary>
        /// Gerçek zamanlı atmosfer animasyonu başlat;
        /// </summary>
        public async Task StartRealTimeAnimationAsync(
            TimeSpan dayDuration,
            AnimationParameters parameters = null)
        {
            ValidateNotDisposed();
            EnsureInitialized();

            parameters ??= new AnimationParameters();

            try
            {
                if (_animationTokenSource.IsCancellationRequested)
                {
                    _animationTokenSource.Dispose();
                    _animationTokenSource = new CancellationTokenSource();
                }

                _currentState.IsAnimating = true;
                _currentState.DayDuration = dayDuration;
                _currentState.AnimationStartTime = DateTime.UtcNow;

                _logger.LogInformation($"Starting real-time atmosphere animation (Day duration: {dayDuration})");

                // Animasyon loop'u başlat;
                _ = Task.Run(async () =>
                {
                    await AnimateAtmosphereLoopAsync(dayDuration, parameters, _animationTokenSource.Token);
                }, _animationTokenSource.Token);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start real-time atmosphere animation.");
                throw;
            }
        }

        /// <summary>
        /// Atmosfer animasyonunu durdur;
        /// </summary>
        public async Task StopAnimationAsync()
        {
            ValidateNotDisposed();

            try
            {
                _animationTokenSource.Cancel();
                _currentState.IsAnimating = false;

                _logger.LogInformation("Atmosphere animation stopped.");

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop atmosphere animation.");
                throw;
            }
        }

        /// <summary>
        /// Özel atmosferik efekt ekle;
        /// </summary>
        public async Task<AtmosphericEffect> AddCustomEffectAsync(
            string effectName,
            EffectParameters parameters)
        {
            ValidateNotDisposed();
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(effectName))
                throw new ArgumentNullException(nameof(effectName));

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            try
            {
                var effect = new AtmosphericEffect(effectName, parameters);

                await InitializeEffectAsync(effect);
                await ApplyEffectToLayersAsync(effect);

                _currentState.CustomEffects.Add(effect);

                _logger.LogDebug($"Custom effect added: {effectName}");
                return effect;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add custom effect: {effectName}");
                throw;
            }
        }

        /// <summary>
        /// Atmosferik saçılma parametrelerini güncelle;
        /// </summary>
        public async Task UpdateScatteringParametersAsync(
            ScatteringParameters parameters)
        {
            ValidateNotDisposed();
            EnsureInitialized();

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            try
            {
                _currentState.ScatteringParameters = parameters;

                await RecalculateScatteringAsync();
                await UpdateRenderScatteringAsync();

                _logger.LogDebug("Scattering parameters updated.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update scattering parameters.");
                throw;
            }
        }

        /// <summary>
        /// Görüş mesafesini ayarla;
        /// </summary>
        public async Task SetVisibilityDistanceAsync(
            float distance,
            TransitionParameters transition = null)
        {
            ValidateNotDisposed();
            EnsureInitialized();

            distance = Math.Max(0, distance);
            transition ??= new TransitionParameters();

            try
            {
                if (transition.Duration > TimeSpan.Zero)
                {
                    await AnimateVisibilityChangeAsync(_currentState.VisibilityDistance, distance, transition);
                }
                else;
                {
                    _currentState.VisibilityDistance = distance;
                    await UpdateVisibilityInRenderSystemAsync();
                }

                _logger.LogDebug($"Visibility distance set to: {distance}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set visibility distance: {distance}");
                throw;
            }
        }

        /// <summary>
        /// Atmosfer profili kaydet;
        /// </summary>
        public async Task SaveProfileAsync(
            string profileName,
            AtmosphereProfile profile)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentNullException(nameof(profileName));

            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            try
            {
                _profiles[profileName] = profile;

                // Dosyaya kaydet;
                await SaveProfileToFileAsync(profileName, profile);

                _logger.LogInformation($"Atmosphere profile saved: {profileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save atmosphere profile: {profileName}");
                throw;
            }
        }

        /// <summary>
        /// Atmosfer profili yükle;
        /// </summary>
        public async Task<AtmosphereProfile> LoadProfileAsync(string profileName)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentNullException(nameof(profileName));

            try
            {
                // Önce cache'ten kontrol et;
                if (_profiles.TryGetValue(profileName, out var cachedProfile))
                {
                    return cachedProfile;
                }

                // Dosyadan yükle;
                var profile = await LoadProfileFromFileAsync(profileName);
                if (profile != null)
                {
                    _profiles[profileName] = profile;
                }

                _logger.LogDebug($"Atmosphere profile loaded: {profileName}");
                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load atmosphere profile: {profileName}");
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

            try
            {
                StopAnimationAsync().Wait();

                _animationTokenSource?.Cancel();
                _animationTokenSource?.Dispose();

                foreach (var layer in _layers)
                {
                    layer.Dispose();
                }
                _layers.Clear();

                _profiles.Clear();
                _cache.Clear();

                _isDisposed = true;

                _logger.LogInformation("AtmosphereCreator disposed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during AtmosphereCreator disposal.");
            }
        }

        #region Public Properties;

        /// <summary>
        /// Mevcut atmosfer durumu;
        /// </summary>
        public AtmosphereState CurrentState => _currentState;

        /// <summary>
        /// Mevcut profil;
        /// </summary>
        public AtmosphereProfile CurrentProfile => _currentProfile;

        /// <summary>
        /// Atmosferik katmanlar;
        /// </summary>
        public IReadOnlyList<AtmosphericLayer> Layers => _layers;

        /// <summary>
        /// Kayıtlı profiller;
        /// </summary>
        public IReadOnlyDictionary<string, AtmosphereProfile> Profiles => _profiles;

        /// <summary>
        /// Atmosfer cache'i;
        /// </summary>
        public AtmosphericCache Cache => _cache;

        #endregion;

        #region Private Methods;

        private AtmosphericSettings LoadSettings()
        {
            var configSection = _config.GetSection<AtmosphereConfiguration>("Atmosphere");

            return new AtmosphericSettings;
            {
                RayleighCoefficient = configSection.RayleighCoefficient,
                MieCoefficient = configSection.MieCoefficient,
                ScaleHeight = configSection.ScaleHeight,
                SunIntensity = configSection.SunIntensity,
                AtmosphericDensity = configSection.AtmosphericDensity,
                OzoneConcentration = configSection.OzoneConcentration,
                DefaultVisibility = configSection.DefaultVisibility,
                MaxLayerCount = configSection.MaxLayerCount,
                UsePrecomputedTextures = configSection.UsePrecomputedTextures,
                ScatteringSamples = configSection.ScatteringSamples,
                EnableGodRays = configSection.EnableGodRays,
                EnableVolumetricClouds = configSection.EnableVolumetricClouds;
            };
        }

        private void InitializeDefaultProfiles()
        {
            // Earth-like atmosfer;
            _profiles["earth_day"] = new AtmosphereProfile;
            {
                Name = "Earth Day",
                Description = "Standard Earth daytime atmosphere",
                RayleighScattering = new ScatteringParameters;
                {
                    Wavelengths = new Vector3(680, 550, 440),
                    Coefficient = 5.8e-6f,
                    ScaleHeight = 8000;
                },
                MieScattering = new ScatteringParameters;
                {
                    Wavelengths = Vector3.One,
                    Coefficient = 2.0e-5f,
                    ScaleHeight = 1200;
                },
                SunColor = new Color(1.0f, 0.956f, 0.839f),
                SkyColor = new Color(0.53f, 0.81f, 0.98f),
                HorizonColor = new Color(0.7f, 0.85f, 1.0f),
                AmbientColor = new Color(0.3f, 0.4f, 0.5f),
                FogDensity = 0.0001f,
                FogColor = new Color(0.8f, 0.9f, 1.0f)
            };

            // Mars-like atmosfer;
            _profiles["mars"] = new AtmosphereProfile;
            {
                Name = "Mars",
                Description = "Martian atmosphere with dust particles",
                RayleighScattering = new ScatteringParameters;
                {
                    Wavelengths = new Vector3(700, 600, 500),
                    Coefficient = 3.0e-5f,
                    ScaleHeight = 11000;
                },
                MieScattering = new ScatteringParameters;
                {
                    Wavelengths = new Vector3(1.2f, 1.1f, 1.0f),
                    Coefficient = 1.0e-4f,
                    ScaleHeight = 800;
                },
                SunColor = new Color(1.0f, 0.8f, 0.6f),
                SkyColor = new Color(0.8f, 0.4f, 0.2f),
                HorizonColor = new Color(0.9f, 0.5f, 0.3f),
                AmbientColor = new Color(0.4f, 0.2f, 0.1f),
                FogDensity = 0.001f,
                FogColor = new Color(0.9f, 0.5f, 0.3f)
            };

            // Alien world;
            _profiles["alien"] = new AtmosphereProfile;
            {
                Name = "Alien World",
                Description = "Exotic atmosphere with unusual scattering",
                RayleighScattering = new ScatteringParameters;
                {
                    Wavelengths = new Vector3(500, 450, 700),
                    Coefficient = 1.0e-5f,
                    ScaleHeight = 15000;
                },
                MieScattering = new ScatteringParameters;
                {
                    Wavelengths = new Vector3(0.8f, 1.2f, 0.9f),
                    Coefficient = 5.0e-5f,
                    ScaleHeight = 2000;
                },
                SunColor = new Color(0.8f, 1.0f, 0.7f),
                SkyColor = new Color(0.4f, 0.6f, 0.8f),
                HorizonColor = new Color(0.6f, 0.8f, 0.9f),
                AmbientColor = new Color(0.2f, 0.3f, 0.4f),
                FogDensity = 0.0005f,
                FogColor = new Color(0.7f, 0.9f, 0.8f)
            };

            // Night time;
            _profiles["earth_night"] = new AtmosphereProfile;
            {
                Name = "Earth Night",
                Description = "Earth nighttime with moonlight",
                RayleighScattering = new ScatteringParameters;
                {
                    Wavelengths = new Vector3(680, 550, 440),
                    Coefficient = 2.0e-6f,
                    ScaleHeight = 8000;
                },
                MieScattering = new ScatteringParameters;
                {
                    Wavelengths = Vector3.One,
                    Coefficient = 1.0e-5f,
                    ScaleHeight = 1200;
                },
                SunColor = new Color(0.1f, 0.1f, 0.15f),
                SkyColor = new Color(0.02f, 0.02f, 0.05f),
                HorizonColor = new Color(0.05f, 0.05f, 0.1f),
                AmbientColor = new Color(0.01f, 0.01f, 0.02f),
                FogDensity = 0.00005f,
                FogColor = new Color(0.05f, 0.05f, 0.1f),
                StarIntensity = 1.0f,
                EnableMilkyWay = true;
            };
        }

        private async Task LoadConfigurationAsync()
        {
            var atmosphereConfig = _config.GetSection<AtmosphereConfiguration>("Atmosphere");

            _settings.RayleighCoefficient = atmosphereConfig.RayleighCoefficient;
            _settings.MieCoefficient = atmosphereConfig.MieCoefficient;
            _settings.ScaleHeight = atmosphereConfig.ScaleHeight;
            _settings.SunIntensity = atmosphereConfig.SunIntensity;
            _settings.AtmosphericDensity = atmosphereConfig.AtmosphericDensity;
            _settings.OzoneConcentration = atmosphereConfig.OzoneConcentration;
            _settings.DefaultVisibility = atmosphereConfig.DefaultVisibility;

            await Task.CompletedTask;
        }

        private async Task InitializeRenderSystemAsync()
        {
            await _renderEngine.InitializeAtmosphereSystemAsync(_settings);
        }

        private async Task InitializeLayersAsync()
        {
            // Troposfer (0-12km)
            _layers.Add(new AtmosphericLayer;
            {
                Name = "Troposphere",
                AltitudeRange = new Range(0, 12000),
                Density = 1.0f,
                ScatteringCoefficient = 1.0f,
                TemperatureGradient = -6.5f // °C per km;
            });

            // Stratosfer (12-50km)
            _layers.Add(new AtmosphericLayer;
            {
                Name = "Stratosphere",
                AltitudeRange = new Range(12000, 50000),
                Density = 0.3f,
                ScatteringCoefficient = 0.5f,
                TemperatureGradient = 1.0f;
            });

            // Mezosfer (50-80km)
            _layers.Add(new AtmosphericLayer;
            {
                Name = "Mesosphere",
                AltitudeRange = new Range(50000, 80000),
                Density = 0.01f,
                ScatteringCoefficient = 0.1f,
                TemperatureGradient = -2.8f;
            });

            // Termosfer (80-700km)
            _layers.Add(new AtmosphericLayer;
            {
                Name = "Thermosphere",
                AltitudeRange = new Range(80000, 700000),
                Density = 0.001f,
                ScatteringCoefficient = 0.01f,
                TemperatureGradient = 2.0f;
            });

            await Task.WhenAll(_layers.Select(layer => layer.InitializeAsync()));
        }

        private TimeOfDay CalculateCurrentTimeOfDay()
        {
            var now = DateTime.Now;
            var totalSeconds = now.Hour * 3600 + now.Minute * 60 + now.Second;
            var normalizedTime = totalSeconds / 86400.0; // 0 to 1;

            return new TimeOfDay;
            {
                NormalizedTime = normalizedTime,
                Hour = now.Hour,
                Minute = now.Minute,
                Second = now.Second;
            };
        }

        private Vector3 CalculateSunPosition(TimeOfDay time)
        {
            // Gerçekçi güneş pozisyonu hesaplaması;
            var sunAltitude = CalculateSunAltitude(time.NormalizedTime);
            var sunAzimuth = CalculateSunAzimuth(time.NormalizedTime);

            // Küresel koordinatlardan Kartezyen koordinatlara dönüşüm;
            return SphericalToCartesian(sunAltitude, sunAzimuth, 1.0f);
        }

        private Vector3 CalculateMoonPosition(TimeOfDay time)
        {
            // Ay pozisyonu hesaplaması (güneşe göre offset)
            var normalizedTime = time.NormalizedTime;
            var moonPhase = CalculateMoonPhase(DateTime.Now);

            var moonAltitude = CalculateMoonAltitude(normalizedTime, moonPhase);
            var moonAzimuth = CalculateMoonAzimuth(normalizedTime, moonPhase);

            return SphericalToCartesian(moonAltitude, moonAzimuth, 1.0f);
        }

        private float CalculateSunAltitude(double normalizedTime)
        {
            // Basit sinüs fonksiyonu ile güneş yüksekliği;
            // Sabah 6: güneş doğuyor, öğlen 12: en yüksek, akşam 18: batıyor;
            var angle = (normalizedTime - 0.25) * 2 * Math.PI; // 0.25 = 06:00;
            return (float)(Math.Sin(angle) * 0.5 + 0.5) * 90.0f;
        }

        private float CalculateSunAzimuth(double normalizedTime)
        {
            // Güneş azimutu (doğudan batıya)
            return (float)(normalizedTime * 360.0);
        }

        private float CalculateMoonPhase(DateTime date)
        {
            // Ay evresi hesaplaması;
            var daysSinceNewMoon = (date - new DateTime(2000, 1, 6)).TotalDays % 29.53;
            return (float)(daysSinceNewMoon / 29.53);
        }

        private Vector3 SphericalToCartesian(float altitude, float azimuth, float radius)
        {
            var altRad = altitude * Math.PI / 180.0;
            var aziRad = azimuth * Math.PI / 180.0;

            var x = radius * Math.Cos(altRad) * Math.Cos(aziRad);
            var y = radius * Math.Sin(altRad);
            var z = radius * Math.Cos(altRad) * Math.Sin(aziRad);

            return new Vector3((float)x, (float)y, (float)z);
        }

        private async Task ApplyProfileAsync(AtmosphereProfile profile, CreationParameters parameters)
        {
            _currentState.ProfileName = profile.Name;
            _currentState.ScatteringParameters = profile.RayleighScattering;
            _currentState.MieParameters = profile.MieScattering;
            _currentState.SunColor = profile.SunColor;
            _currentState.SkyColor = profile.SkyColor;
            _currentState.HorizonColor = profile.HorizonColor;
            _currentState.AmbientColor = profile.AmbientColor;
            _currentState.FogColor = profile.FogColor;
            _currentState.FogDensity = profile.FogDensity;

            if (parameters.ApplyImmediately)
            {
                await ApplyToRenderSystemAsync();
            }
        }

        private async Task CreateAtmosphericLayersAsync(AtmosphereProfile profile)
        {
            // Profil bazlı katman ayarları;
            foreach (var layer in _layers)
            {
                await layer.ApplyProfileAsync(profile);
            }
        }

        private async Task CalculatePhysicalPropertiesAsync()
        {
            // Atmosferik yoğunluk hesaplaması;
            _currentState.AtmosphericDensity = await CalculateAtmosphericDensityAsync();

            // Saçılma katsayıları;
            _currentState.RayleighCoefficient = CalculateRayleighCoefficient();
            _currentState.MieCoefficient = CalculateMieCoefficient();

            // Optik derinlik;
            _currentState.OpticalDepth = await CalculateOpticalDepthAsync();
        }

        private async Task<float> CalculateAtmosphericDensityAsync()
        {
            var totalDensity = 0f;

            foreach (var layer in _layers)
            {
                totalDensity += await layer.GetDensityAtAltitudeAsync(_currentState.ViewerAltitude);
            }

            return totalDensity / _layers.Count;
        }

        private async Task UpdateLightColorsFromTimeAsync(TimeOfDay time)
        {
            var normalizedTime = time.NormalizedTime;

            // Günün saatine göre renk interpolasyonu;
            if (normalizedTime < 0.25) // Gece 00:00 - 06:00;
            {
                _currentState.SkyColor = Color.Lerp(
                    _profiles["earth_night"].SkyColor,
                    _profiles["earth_day"].SkyColor,
                    normalizedTime / 0.25f);
            }
            else if (normalizedTime < 0.5) // Sabah 06:00 - 12:00;
            {
                _currentState.SkyColor = Color.Lerp(
                    _profiles["earth_day"].SkyColor,
                    Color.SkyBlue,
                    (normalizedTime - 0.25f) / 0.25f);
            }
            else if (normalizedTime < 0.75) // Öğleden sonra 12:00 - 18:00;
            {
                _currentState.SkyColor = Color.Lerp(
                    Color.SkyBlue,
                    Color.OrangeRed * 0.8f,
                    (normalizedTime - 0.5f) / 0.25f);
            }
            else // Akşam 18:00 - 00:00;
            {
                _currentState.SkyColor = Color.Lerp(
                    Color.OrangeRed * 0.8f,
                    _profiles["earth_night"].SkyColor,
                    (normalizedTime - 0.75f) / 0.25f);
            }

            await Task.CompletedTask;
        }

        private async Task UpdateAtmosphericScatteringAsync(TimeOfDay time)
        {
            // Rayleigh saçılması;
            var rayleigh = await CalculateRayleighScatteringAsync(time);
            _currentState.RayleighScattering = rayleigh;

            // Mie saçılması;
            var mie = await CalculateMieScatteringAsync(time);
            _currentState.MieScattering = mie;

            // Kombine edilmiş saçılma;
            _currentState.TotalScattering = await CombineScatteringAsync(rayleigh, mie);
        }

        private async Task<ScatteringResult> CalculateRayleighScatteringAsync(TimeOfDay time)
        {
            // Rayleigh saçılma formülü: β(λ) = (8π³(n²-1)²) / (3Nλ⁴)
            var wavelength = _currentState.ScatteringParameters.Wavelengths;
            var coefficient = _currentState.ScatteringParameters.Coefficient;
            var sunAngle = CalculateSunAngle(time);

            var intensity = coefficient / (wavelength * wavelength * wavelength * wavelength);
            var phase = (3.0f / (16.0f * Math.PI)) * (1 + Math.Cos(sunAngle * Math.PI / 180.0) * Math.Cos(sunAngle * Math.PI / 180.0));

            return new ScatteringResult;
            {
                Intensity = intensity,
                PhaseFunction = (float)phase,
                WavelengthDependency = wavelength;
            };
        }

        private async Task ApplyToRenderSystemAsync()
        {
            var renderData = new AtmosphericRenderData;
            {
                SkyColor = _currentState.SkyColor,
                HorizonColor = _currentState.HorizonColor,
                SunColor = _currentState.SunColor,
                SunPosition = _currentState.SunPosition,
                MoonPosition = _currentState.MoonPosition,
                FogColor = _currentState.FogColor,
                FogDensity = _currentState.FogDensity,
                VisibilityDistance = _currentState.VisibilityDistance,
                AtmosphericDensity = _currentState.AtmosphericDensity,
                ScatteringParameters = _currentState.ScatteringParameters,
                MieParameters = _currentState.MieParameters;
            };

            await _renderEngine.SetAtmosphericSettingsAsync(renderData);
        }

        private async Task AnimateAtmosphereLoopAsync(
            TimeSpan dayDuration,
            AnimationParameters parameters,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            var lastUpdate = startTime;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var currentTime = DateTime.UtcNow;
                    var elapsed = currentTime - startTime;

                    // Normalize edilmiş zaman (0-1)
                    var normalizedTime = (float)(elapsed.TotalSeconds % dayDuration.TotalSeconds) / (float)dayDuration.TotalSeconds;

                    var timeOfDay = new TimeOfDay;
                    {
                        NormalizedTime = normalizedTime,
                        Hour = (int)(normalizedTime * 24),
                        Minute = (int)((normalizedTime * 24 * 60) % 60),
                        Second = (int)((normalizedTime * 24 * 60 * 60) % 60)
                    };

                    await UpdateAtmosphereFromTimeAsync(timeOfDay);

                    // Frame rate kontrolü;
                    var frameTime = currentTime - lastUpdate;
                    var targetFrameTime = TimeSpan.FromSeconds(1.0 / parameters.FrameRate);

                    if (frameTime < targetFrameTime)
                    {
                        await Task.Delay(targetFrameTime - frameTime, cancellationToken);
                    }

                    lastUpdate = DateTime.UtcNow;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in atmosphere animation loop.");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private AtmosphereProfile GetProfile(string profileName)
        {
            lock (_syncLock)
            {
                return _profiles.TryGetValue(profileName, out var profile) ? profile : null;
            }
        }

        private void EnsureInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("AtmosphereCreator not initialized. Call InitializeAsync first.");
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(AtmosphereCreator));
        }

        #endregion;
    }

    /// <summary>
    /// Atmosferik ayarlar;
    /// </summary>
    public class AtmosphericSettings;
    {
        public float RayleighCoefficient { get; set; } = 5.8e-6f;
        public float MieCoefficient { get; set; } = 2.0e-5f;
        public float ScaleHeight { get; set; } = 8000.0f;
        public float SunIntensity { get; set; } = 1.0f;
        public float AtmosphericDensity { get; set; } = 1.0f;
        public float OzoneConcentration { get; set; } = 0.25f;
        public float DefaultVisibility { get; set; } = 10000.0f;
        public int MaxLayerCount { get; set; } = 8;
        public bool UsePrecomputedTextures { get; set; } = true;
        public int ScatteringSamples { get; set; } = 16;
        public bool EnableGodRays { get; set; } = true;
        public bool EnableVolumetricClouds { get; set; } = true;
        public bool EnableDynamicWeather { get; set; } = true;
        public float NightSkyBrightness { get; set; } = 0.1f;
        public int StarCount { get; set; } = 5000;
        public bool EnableAurora { get; set; } = false;
    }

    /// <summary>
    /// Atmosfer profili;
    /// </summary>
    public class AtmosphereProfile;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ScatteringParameters RayleighScattering { get; set; }
        public ScatteringParameters MieScattering { get; set; }
        public Color SunColor { get; set; }
        public Color SkyColor { get; set; }
        public Color HorizonColor { get; set; }
        public Color AmbientColor { get; set; }
        public Color FogColor { get; set; }
        public float FogDensity { get; set; }
        public float StarIntensity { get; set; } = 0.5f;
        public bool EnableMilkyWay { get; set; } = false;
        public float CloudCoverage { get; set; } = 0.3f;
        public float CloudHeight { get; set; } = 2000.0f;
        public float WindSpeed { get; set; } = 5.0f;
        public Vector3 WindDirection { get; set; } = Vector3.Right;
        public float PrecipitationProbability { get; set; } = 0.1f;
    }

    /// <summary>
    /// Atmosfer durumu;
    /// </summary>
    public class AtmosphereState;
    {
        public string ProfileName { get; set; }
        public TimeOfDay TimeOfDay { get; set; }
        public Vector3 SunPosition { get; set; }
        public Vector3 MoonPosition { get; set; }
        public Color SkyColor { get; set; }
        public Color HorizonColor { get; set; }
        public Color SunColor { get; set; }
        public Color AmbientColor { get; set; }
        public Color FogColor { get; set; }
        public float FogDensity { get; set; }
        public float VisibilityDistance { get; set; } = 10000.0f;
        public float AtmosphericDensity { get; set; } = 1.0f;
        public ScatteringParameters ScatteringParameters { get; set; }
        public ScatteringParameters MieParameters { get; set; }
        public ScatteringResult RayleighScattering { get; set; }
        public ScatteringResult MieScattering { get; set; }
        public ScatteringResult TotalScattering { get; set; }
        public float RayleighCoefficient { get; set; }
        public float MieCoefficient { get; set; }
        public float OpticalDepth { get; set; }
        public WeatherCondition WeatherCondition { get; set; }
        public List<AtmosphericEffect> CustomEffects { get; set; } = new List<AtmosphericEffect>();
        public float ViewerAltitude { get; set; }
        public bool IsAnimating { get; set; }
        public TimeSpan DayDuration { get; set; }
        public DateTime AnimationStartTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }

    /// <summary>
    /// Gün saati bilgisi;
    /// </summary>
    public class TimeOfDay;
    {
        public double NormalizedTime { get; set; } // 0-1 arası;
        public int Hour { get; set; }
        public int Minute { get; set; }
        public int Second { get; set; }

        public override string ToString()
        {
            return $"{Hour:D2}:{Minute:D2}:{Second:D2}";
        }
    }

    /// <summary>
    /// Atmosferik katman;
    /// </summary>
    public class AtmosphericLayer : IDisposable
    {
        public string Name { get; set; }
        public Range AltitudeRange { get; set; }
        public float Density { get; set; }
        public float ScatteringCoefficient { get; set; }
        public float TemperatureGradient { get; set; } // °C per km;
        public Color LayerColor { get; set; } = Color.White;
        public float Opacity { get; set; } = 1.0f;

        public async Task InitializeAsync()
        {
            // Katman başlatma işlemleri;
            await Task.CompletedTask;
        }

        public async Task ApplyProfileAsync(AtmosphereProfile profile)
        {
            // Profil ayarlarını uygula;
            await Task.CompletedTask;
        }

        public async Task<float> GetDensityAtAltitudeAsync(float altitude)
        {
            // Barometrik formül: ρ = ρ₀ * exp(-h/H)
            var normalizedAltitude = (altitude - AltitudeRange.Min) / (AltitudeRange.Max - AltitudeRange.Min);
            var density = Density * (float)Math.Exp(-normalizedAltitude);

            return await Task.FromResult(density);
        }

        public void Dispose()
        {
            // Kaynakları serbest bırak;
        }
    }

    /// <summary>
    /// Saçılma parametreleri;
    /// </summary>
    public class ScatteringParameters;
    {
        public Vector3 Wavelengths { get; set; } // in nanometers;
        public float Coefficient { get; set; }
        public float ScaleHeight { get; set; }
        public float AsymmetryFactor { get; set; } = 0.76f; // For Mie scattering;
        public float OpticalDepth { get; set; } = 0.25f;
    }

    /// <summary>
    /// Saçılma sonucu;
    /// </summary>
    public class ScatteringResult;
    {
        public Vector3 Intensity { get; set; }
        public float PhaseFunction { get; set; }
        public Vector3 WavelengthDependency { get; set; }
        public float TotalIntensity => Intensity.X + Intensity.Y + Intensity.Z;
    }

    /// <summary>
    /// Hava durumu koşulu;
    /// </summary>
    public class WeatherCondition;
    {
        public string Name { get; set; }
        public WeatherType Type { get; set; }
        public float Intensity { get; set; } = 1.0f;
        public bool HasRain { get; set; }
        public float RainIntensity { get; set; }
        public Vector3 RainDirection { get; set; }
        public bool HasSnow { get; set; }
        public float SnowIntensity { get; set; }
        public float SnowCoverage { get; set; }
        public bool HasFog { get; set; }
        public float FogDensity { get; set; }
        public Color FogColor { get; set; }
        public bool HasWind { get; set; }
        public float WindSpeed { get; set; }
        public Vector3 WindDirection { get; set; }
        public bool HasStorm { get; set; }
        public float LightningFrequency { get; set; }
        public float AtmosphericDensity { get; set; } = 1.0f;
        public float CloudCoverage { get; set; }
        public float Temperature { get; set; } = 20.0f;
        public float Humidity { get; set; } = 50.0f;
    }

    /// <summary>
    /// Atmosferik efekt;
    /// </summary>
    public class AtmosphericEffect;
    {
        public string Name { get; }
        public EffectType Type { get; }
        public float Intensity { get; set; }
        public Color EffectColor { get; set; }
        public Vector3 Position { get; set; }
        public float Radius { get; set; }
        public float Duration { get; set; } // seconds, 0 = infinite;
        public DateTime StartTime { get; set; }

        public AtmosphericEffect(string name, EffectParameters parameters)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = parameters.Type;
            Intensity = parameters.Intensity;
            EffectColor = parameters.Color;
            Position = parameters.Position;
            Radius = parameters.Radius;
            Duration = parameters.Duration;
            StartTime = DateTime.UtcNow;
        }

        public bool IsExpired => Duration > 0 && (DateTime.UtcNow - StartTime).TotalSeconds > Duration;
    }

    /// <summary>
    /// Atmosferik cache;
    /// </summary>
    public class AtmosphericCache;
    {
        private readonly Dictionary<string, object> _cache = new Dictionary<string, object>();

        public void Set<T>(string key, T value)
        {
            _cache[key] = value;
        }

        public T Get<T>(string key)
        {
            return _cache.TryGetValue(key, out var value) ? (T)value : default;
        }

        public bool Contains(string key) => _cache.ContainsKey(key);

        public void Remove(string key) => _cache.Remove(key);

        public void Clear() => _cache.Clear();
    }

    /// <summary>
    /// Oluşturma parametreleri;
    /// </summary>
    public class CreationParameters;
    {
        public bool ApplyImmediately { get; set; } = true;
        public bool CacheResults { get; set; } = true;
        public float Quality { get; set; } = 1.0f;
        public bool EnableRealTimeUpdates { get; set; } = true;
        public TimeSpan TransitionDuration { get; set; } = TimeSpan.Zero;
    }

    /// <summary>
    /// Hava durumu parametreleri;
    /// </summary>
    public class WeatherParameters;
    {
        public TimeSpan TransitionDuration { get; set; } = TimeSpan.FromSeconds(5);
        public bool GradualChange { get; set; } = true;
        public float MaxIntensity { get; set; } = 1.0f;
        public bool AffectLighting { get; set; } = true;
        public bool AffectVisibility { get; set; } = true;
        public bool PlaySoundEffects { get; set; } = true;
    }

    /// <summary>
    /// Animasyon parametreleri;
    /// </summary>
    public class AnimationParameters;
    {
        public float FrameRate { get; set; } = 30.0f;
        public bool SmoothTransitions { get; set; } = true;
        public float TimeScale { get; set; } = 1.0f;
        public bool Loop { get; set; } = true;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero; // 0 = infinite;
        public AnimationCurve TimeCurve { get; set; } = AnimationCurve.Linear;
    }

    /// <summary>
    /// Efekt parametreleri;
    /// </summary>
    public class EffectParameters;
    {
        public EffectType Type { get; set; }
        public float Intensity { get; set; } = 1.0f;
        public Color Color { get; set; } = Color.White;
        public Vector3 Position { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 10.0f;
        public float Duration { get; set; } = 0.0f; // 0 = infinite;
        public bool FadeIn { get; set; } = true;
        public bool FadeOut { get; set; } = true;
    }

    /// <summary>
    /// Geçiş parametreleri;
    /// </summary>
    public class TransitionParameters;
    {
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(2);
        public AnimationCurve Curve { get; set; } = AnimationCurve.EaseInOut;
        public bool Smooth { get; set; } = true;
    }

    /// <summary>
    /// Hava durumu tipi enum'ı;
    /// </summary>
    public enum WeatherType;
    {
        Clear = 0,
        Sunny = 1,
        Cloudy = 2,
        Overcast = 3,
        Rain = 4,
        Thunderstorm = 5,
        Snow = 6,
        Fog = 7,
        Mist = 8,
        Haze = 9,
        DustStorm = 10,
        Sandstorm = 11,
        Hurricane = 12,
        Tornado = 13;
    }

    /// <summary>
    /// Efekt tipi enum'ı;
    /// </summary>
    public enum EffectType;
    {
        GodRays = 0,
        VolumetricFog = 1,
        Aurora = 2,
        Rainbow = 3,
        Halo = 4,
        Glory = 5,
        SunDog = 6,
        Lightning = 7,
        Fire = 8,
        Smoke = 9,
        Dust = 10;
    }

    /// <summary>
    /// Animasyon eğrisi enum'ı;
    /// </summary>
    public enum AnimationCurve;
    {
        Linear = 0,
        EaseIn = 1,
        EaseOut = 2,
        EaseInOut = 3,
        Quadratic = 4,
        Cubic = 5,
        Elastic = 6,
        Bounce = 7;
    }

    /// <summary>
    /// Atmosferik render verisi;
    /// </summary>
    public class AtmosphericRenderData;
    {
        public Color SkyColor { get; set; }
        public Color HorizonColor { get; set; }
        public Color SunColor { get; set; }
        public Vector3 SunPosition { get; set; }
        public Vector3 MoonPosition { get; set; }
        public Color FogColor { get; set; }
        public float FogDensity { get; set; }
        public float VisibilityDistance { get; set; }
        public float AtmosphericDensity { get; set; }
        public ScatteringParameters ScatteringParameters { get; set; }
        public ScatteringParameters MieParameters { get; set; }
        public bool EnableScattering { get; set; } = true;
        public bool EnableShadows { get; set; } = true;
        public float Exposure { get; set; } = 1.0f;
        public float Gamma { get; set; } = 2.2f;
    }

    /// <summary>
    /// Range sınıfı;
    /// </summary>
    public class Range;
    {
        public float Min { get; set; }
        public float Max { get; set; }

        public Range(float min, float max)
        {
            Min = min;
            Max = max;
        }

        public float Length => Max - Min;
        public bool Contains(float value) => value >= Min && value <= Max;
    }

    /// <summary>
    /// Atmosfer yaratıcı interface'i;
    /// </summary>
    public interface IAtmosphereCreator : IDisposable
    {
        Task InitializeAsync();
        Task<AtmosphereState> CreateAtmosphereAsync(string profileName, CreationParameters parameters = null);
        Task UpdateAtmosphereFromTimeAsync(TimeOfDay time);
        Task ApplyWeatherEffectsAsync(WeatherCondition condition, WeatherParameters parameters = null);
        Task StartRealTimeAnimationAsync(TimeSpan dayDuration, AnimationParameters parameters = null);
        Task StopAnimationAsync();
        Task<AtmosphericEffect> AddCustomEffectAsync(string effectName, EffectParameters parameters);
        Task UpdateScatteringParametersAsync(ScatteringParameters parameters);
        Task SetVisibilityDistanceAsync(float distance, TransitionParameters transition = null);
        Task SaveProfileAsync(string profileName, AtmosphereProfile profile);
        Task<AtmosphereProfile> LoadProfileAsync(string profileName);

        AtmosphereState CurrentState { get; }
        AtmosphereProfile CurrentProfile { get; }
        IReadOnlyList<AtmosphericLayer> Layers { get; }
        IReadOnlyDictionary<string, AtmosphereProfile> Profiles { get; }
        AtmosphericCache Cache { get; }
    }
}
