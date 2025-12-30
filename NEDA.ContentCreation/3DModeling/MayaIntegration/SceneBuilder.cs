using NEDA.Communication.MultiModalCommunication.BodyLanguage;
using NEDA.ContentCreation._3DModeling.MayaIntegration;
using NEDA.ContentCreation.AnimationTools.MayaIntegration;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.Unreal;
using NEDA.ExceptionHandling;
using NEDA.Monitoring;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.ContentCreation._3DModeling.MayaIntegration;
{
    /// <summary>
    /// Maya sahne oluşturma ve yönetme motoru.
    /// Unreal Engine ve diğer 3D araçlarla entegre çalışır.
    /// </summary>
    public class SceneBuilder : IDisposable
    {
        #region Fields;

        private readonly ILogger _logger;
        private readonly MayaConnector _mayaConnector;
        private readonly UnrealEngine _unrealEngine;
        private readonly MELScriptRunner _melScriptRunner;
        private readonly PerformanceMonitor _performanceMonitor;
        private bool _isInitialized;
        private bool _isBuilding;
        private SceneConfiguration _currentConfig;
        private readonly Dictionary<string, SceneObject> _sceneObjects;
        private readonly Queue<SceneOperation> _operationQueue;
        private readonly object _syncLock = new object();

        #endregion;

        #region Properties;

        /// <summary>
        /// Aktif sahne yapılandırması;
        /// </summary>
        public SceneConfiguration CurrentConfiguration => _currentConfig;

        /// <summary>
        /// Sahne oluşturma durumu;
        /// </summary>
        public SceneBuildStatus Status { get; private set; }

        /// <summary>
        /// Toplam sahne nesnesi sayısı;
        /// </summary>
        public int TotalObjects => _sceneObjects.Count;

        /// <summary>
        /// Sahne karmaşıklık seviyesi;
        /// </summary>
        public SceneComplexity Complexity { get; private set; }

        /// <summary>
        /// Optimizasyon modu aktif mi?
        /// </summary>
        public bool OptimizationEnabled { get; set; } = true;

        #endregion;

        #region Events;

        /// <summary>
        /// Sahne oluşturma başladığında tetiklenir;
        /// </summary>
        public event EventHandler<SceneBuildEventArgs> BuildStarted;

        /// <summary>
        /// Sahne oluşturma tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<SceneBuildEventArgs> BuildCompleted;

        /// <summary>
        /// Sahne oluşturma ilerlemesi değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<SceneProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Sahne nesnesi eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<SceneObjectEventArgs> ObjectAdded;

        /// <summary>
        /// Hata oluştuğunda tetiklenir;
        /// </summary>
        public event EventHandler<SceneErrorEventArgs> ErrorOccurred;

        #endregion;

        #region Constructor;

        /// <summary>
        /// SceneBuilder sınıfını başlatır;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="mayaConnector">Maya bağlantısı</param>
        /// <param name="unrealEngine">Unreal Engine entegrasyonu</param>
        public SceneBuilder(ILogger logger = null, MayaConnector mayaConnector = null, UnrealEngine unrealEngine = null)
        {
            _logger = logger ?? LogManager.GetLogger(typeof(SceneBuilder));
            _mayaConnector = mayaConnector ?? new MayaConnector();
            _unrealEngine = unrealEngine ?? new UnrealEngine();
            _melScriptRunner = new MELScriptRunner();
            _performanceMonitor = new PerformanceMonitor("SceneBuilder");

            _sceneObjects = new Dictionary<string, SceneObject>();
            _operationQueue = new Queue<SceneOperation>();

            Status = SceneBuildStatus.Idle;
            Complexity = SceneComplexity.Low;

            InitializeInternal();
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Yeni sahne oluşturur;
        /// </summary>
        /// <param name="config">Sahne yapılandırması</param>
        /// <returns>Oluşturulan sahne</returns>
        public async Task<SceneResult> CreateSceneAsync(SceneConfiguration config)
        {
            ValidateConfiguration(config);

            try
            {
                _logger.Info($"Yeni sahne oluşturuluyor: {config.SceneName}");

                lock (_syncLock)
                {
                    if (_isBuilding)
                        throw new InvalidOperationException("Sahne oluşturma işlemi devam ediyor");

                    _isBuilding = true;
                    Status = SceneBuildStatus.Building;
                    _currentConfig = config;
                }

                OnBuildStarted(new SceneBuildEventArgs(config.SceneName, DateTime.UtcNow));

                // Sahne temizleme;
                await ClearSceneAsync();

                // Maya'da yeni sahne oluştur;
                await _mayaConnector.CreateNewSceneAsync(config.SceneName);

                // Temel ayarları uygula;
                await ApplyBaseSettingsAsync(config);

                // Işıklandırma ekle;
                if (config.EnableLighting)
                    await AddLightingAsync(config.LightingSetup);

                // Kamera ekle;
                if (config.AddDefaultCamera)
                    await AddCameraAsync(config.CameraSettings);

                // Çevre ekle;
                if (config.EnableEnvironment)
                    await AddEnvironmentAsync(config.EnvironmentSettings);

                // Optimizasyon uygula;
                if (OptimizationEnabled)
                    await OptimizeSceneAsync();

                // Performans metriklerini güncelle;
                UpdateComplexity();

                var result = new SceneResult;
                {
                    Success = true,
                    SceneName = config.SceneName,
                    ObjectCount = _sceneObjects.Count,
                    Complexity = Complexity,
                    FilePath = await SaveSceneAsync(config.OutputPath)
                };

                Status = SceneBuildStatus.Completed;
                OnBuildCompleted(new SceneBuildEventArgs(config.SceneName, DateTime.UtcNow, result));

                _logger.Info($"Sahne başarıyla oluşturuldu: {config.SceneName}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Sahne oluşturma hatası: {ex.Message}", ex);
                Status = SceneBuildStatus.Error;
                OnErrorOccurred(new SceneErrorEventArgs(ex, "CreateSceneAsync"));
                throw new SceneBuilderException($"Sahne oluşturma başarısız: {ex.Message}", ex);
            }
            finally
            {
                _isBuilding = false;
            }
        }

        /// <summary>
        /// 3D modeli sahneye ekler;
        /// </summary>
        /// <param name="modelPath">Model dosya yolu</param>
        /// <param name="position">Pozisyon</param>
        /// <param name="scale">Ölçek</param>
        /// <param name="rotation">Rotasyon</param>
        /// <returns>Eklenen nesne</returns>
        public async Task<SceneObject> AddModelAsync(string modelPath, Vector3 position, Vector3 scale, Quaternion rotation)
        {
            ValidateModelPath(modelPath);

            try
            {
                _logger.Debug($"Model ekleniyor: {Path.GetFileName(modelPath)}");

                // Modeli Maya'ya import et;
                var mayaObject = await _mayaConnector.ImportModelAsync(modelPath);

                // Transform uygula;
                await _mayaConnector.SetTransformAsync(mayaObject.Id, position, scale, rotation);

                // SceneObject oluştur;
                var sceneObject = new SceneObject;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = Path.GetFileNameWithoutExtension(modelPath),
                    Type = SceneObjectType.Model,
                    MeshPath = modelPath,
                    Position = position,
                    Scale = scale,
                    Rotation = rotation,
                    MayaObjectId = mayaObject.Id,
                    MaterialCount = mayaObject.MaterialCount,
                    VertexCount = mayaObject.VertexCount,
                    TriangleCount = mayaObject.TriangleCount;
                };

                // Cache'e ekle;
                lock (_syncLock)
                {
                    _sceneObjects[sceneObject.Id] = sceneObject;
                }

                // Optimizasyon kontrolü;
                if (OptimizationEnabled)
                    await OptimizeObjectAsync(sceneObject);

                UpdateComplexity();

                OnObjectAdded(new SceneObjectEventArgs(sceneObject));
                OnProgressChanged(new SceneProgressEventArgs("Model eklendi", _sceneObjects.Count));

                _logger.Info($"Model başarıyla eklendi: {sceneObject.Name}");

                return sceneObject;
            }
            catch (Exception ex)
            {
                _logger.Error($"Model ekleme hatası: {ex.Message}", ex);
                throw new SceneBuilderException($"Model eklenemedi: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sahneye ışık ekler;
        /// </summary>
        /// <param name="lightType">Işık türü</param>
        /// <param name="position">Pozisyon</param>
        /// <param name="intensity">Yoğunluk</param>
        /// <param name="color">Renk</param>
        /// <returns>Eklenen ışık</returns>
        public async Task<SceneObject> AddLightAsync(LightType lightType, Vector3 position, float intensity, Color color)
        {
            try
            {
                _logger.Debug($"{lightType} ışığı ekleniyor");

                // Maya'da ışık oluştur;
                var mayaLight = await _mayaConnector.CreateLightAsync(lightType, position);

                // Işık ayarlarını uygula;
                await _mayaConnector.SetLightPropertiesAsync(mayaLight.Id, intensity, color);

                var lightObject = new SceneObject;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = $"{lightType}Light_{_sceneObjects.Count + 1}",
                    Type = SceneObjectType.Light,
                    Position = position,
                    LightType = lightType,
                    LightIntensity = intensity,
                    LightColor = color,
                    MayaObjectId = mayaLight.Id;
                };

                lock (_syncLock)
                {
                    _sceneObjects[lightObject.Id] = lightObject;
                }

                OnObjectAdded(new SceneObjectEventArgs(lightObject));

                _logger.Info($"{lightType} ışığı eklendi");

                return lightObject;
            }
            catch (Exception ex)
            {
                _logger.Error($"Işık ekleme hatası: {ex.Message}", ex);
                throw new SceneBuilderException($"Işık eklenemedi: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sahneyi Unreal Engine projesine dışa aktarır;
        /// </summary>
        /// <param name="projectPath">Unreal proje yolu</param>
        /// <param name="exportSettings">Dışa aktarma ayarları</param>
        /// <returns>Dışa aktarma sonucu</returns>
        public async Task<ExportResult> ExportToUnrealAsync(string projectPath, ExportSettings exportSettings)
        {
            ValidateProjectPath(projectPath);

            try
            {
                _logger.Info($"Sahne Unreal Engine'e aktarılıyor: {projectPath}");

                // Sahneyi FBX olarak export et;
                var fbxPath = await ExportToFbxAsync(exportSettings);

                // Unreal Engine'e import et;
                var importResult = await _unrealEngine.ImportSceneAsync(projectPath, fbxPath, exportSettings);

                // Material'ları işle;
                if (exportSettings.ExportMaterials)
                    await ProcessMaterialsForUnrealAsync(projectPath);

                // Texture'ları işle;
                if (exportSettings.ExportTextures)
                    await ProcessTexturesForUnrealAsync(projectPath);

                _logger.Info($"Sahne başarıyla Unreal Engine'e aktarıldı");

                return importResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unreal export hatası: {ex.Message}", ex);
                throw new SceneBuilderException($"Unreal Engine'e aktarma başarısız: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sahneyi optimize eder;
        /// </summary>
        /// <param name="optimizationLevel">Optimizasyon seviyesi</param>
        /// <returns>Optimizasyon sonucu</returns>
        public async Task<OptimizationResult> OptimizeSceneAsync(OptimizationLevel optimizationLevel = OptimizationLevel.Medium)
        {
            try
            {
                _logger.Info($"Sahne optimizasyonu başlatılıyor: {optimizationLevel}");

                var result = new OptimizationResult;
                {
                    StartTime = DateTime.UtcNow,
                    OptimizationLevel = optimizationLevel,
                    InitialObjectCount = _sceneObjects.Count;
                };

                // LOD oluştur;
                if (optimizationLevel >= OptimizationLevel.Medium)
                    await GenerateLODsAsync();

                // Polygon azaltma;
                if (optimizationLevel >= OptimizationLevel.High)
                    await ReducePolygonsAsync();

                // Material birleştirme;
                if (optimizationLevel >= OptimizationLevel.Medium)
                    await MergeMaterialsAsync();

                // Lightmap UV'leri oluştur;
                await GenerateLightmapUVsAsync();

                // Performans metriklerini hesapla;
                result.EndTime = DateTime.UtcNow;
                result.PerformanceGain = CalculatePerformanceGain();
                result.MemoryReduction = CalculateMemoryReduction();

                _logger.Info($"Sahne optimizasyonu tamamlandı: {result.PerformanceGain}% performans artışı");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Optimizasyon hatası: {ex.Message}", ex);
                throw new SceneBuilderException($"Sahne optimizasyonu başarısız: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sahneyi temizler;
        /// </summary>
        public async Task ClearSceneAsync()
        {
            try
            {
                _logger.Info("Sahne temizleniyor");

                await _mayaConnector.ClearSceneAsync();

                lock (_syncLock)
                {
                    _sceneObjects.Clear();
                    _operationQueue.Clear();
                }

                Complexity = SceneComplexity.Low;

                _logger.Info("Sahne temizlendi");
            }
            catch (Exception ex)
            {
                _logger.Error($"Sahne temizleme hatası: {ex.Message}", ex);
                throw new SceneBuilderException($"Sahne temizlenemedi: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sahneyi kaydeder;
        /// </summary>
        /// <param name="filePath">Kayıt yolu</param>
        /// <returns>Kaydedilen dosya yolu</returns>
        public async Task<string> SaveSceneAsync(string filePath)
        {
            try
            {
                _logger.Debug($"Sahne kaydediliyor: {filePath}");

                var savedPath = await _mayaConnector.SaveSceneAsync(filePath);

                _logger.Info($"Sahne kaydedildi: {savedPath}");

                return savedPath;
            }
            catch (Exception ex)
            {
                _logger.Error($"Sahne kaydetme hatası: {ex.Message}", ex);
                throw new SceneBuilderException($"Sahne kaydedilemedi: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Private Methods;

        private void InitializeInternal()
        {
            try
            {
                _logger.Debug("SceneBuilder başlatılıyor");

                // Maya bağlantısını başlat;
                _mayaConnector.Initialize();

                // MEL script runner başlat;
                _melScriptRunner.Initialize();

                // Performans monitörü başlat;
                _performanceMonitor.Start();

                _isInitialized = true;

                _logger.Info("SceneBuilder başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.Error($"Başlatma hatası: {ex.Message}", ex);
                throw new SceneBuilderException($"SceneBuilder başlatılamadı: {ex.Message}", ex);
            }
        }

        private async Task ApplyBaseSettingsAsync(SceneConfiguration config)
        {
            try
            {
                // Unit scale ayarla;
                await _mayaConnector.SetUnitScaleAsync(config.UnitScale);

                // Grid ayarları;
                await _mayaConnector.SetGridSettingsAsync(config.GridSize, config.GridVisibility);

                // Viewport ayarları;
                await _mayaConnector.SetViewportSettingsAsync(config.ViewportQuality);

                // Time slider ayarları;
                if (config.AnimationSettings != null)
                    await _mayaConnector.SetTimeSliderAsync(
                        config.AnimationSettings.StartFrame,
                        config.AnimationSettings.EndFrame;
                    );
            }
            catch (Exception ex)
            {
                _logger.Error($"Temel ayarlar uygulanamadı: {ex.Message}", ex);
                throw;
            }
        }

        private async Task AddLightingAsync(LightingSetup lightingSetup)
        {
            try
            {
                switch (lightingSetup.Type)
                {
                    case LightingType.ThreePoint:
                        await CreateThreePointLighting(lightingSetup);
                        break;
                    case LightingType.HDRI:
                        await CreateHDRILighting(lightingSetup);
                        break;
                    case LightingType.Studio:
                        await CreateStudioLighting(lightingSetup);
                        break;
                    case LightingType.Outdoor:
                        await CreateOutdoorLighting(lightingSetup);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Işıklandırma eklenemedi: {ex.Message}", ex);
                throw;
            }
        }

        private async Task CreateThreePointLighting(LightingSetup setup)
        {
            // Key light;
            await AddLightAsync(
                LightType.Directional,
                new Vector3(-5, 5, 5),
                setup.KeyLightIntensity,
                Color.White;
            );

            // Fill light;
            await AddLightAsync(
                LightType.Directional,
                new Vector3(5, 3, 5),
                setup.FillLightIntensity,
                new Color(0.8f, 0.8f, 1.0f)
            );

            // Back light;
            await AddLightAsync(
                LightType.Directional,
                new Vector3(0, 5, -5),
                setup.BackLightIntensity,
                Color.White;
            );
        }

        private async Task AddCameraAsync(CameraSettings settings)
        {
            try
            {
                await _mayaConnector.CreateCameraAsync(
                    settings.Position,
                    settings.Target,
                    settings.FieldOfView,
                    settings.NearClip,
                    settings.FarClip;
                );
            }
            catch (Exception ex)
            {
                _logger.Error($"Kamera eklenemedi: {ex.Message}", ex);
                throw;
            }
        }

        private async Task AddEnvironmentAsync(EnvironmentSettings settings)
        {
            try
            {
                if (settings.AddGroundPlane)
                    await CreateGroundPlaneAsync(settings.GroundSize);

                if (settings.AddSkyDome)
                    await CreateSkyDomeAsync(settings.SkyRadius);

                if (settings.AddAtmosphere)
                    await CreateAtmosphereEffectAsync(settings.AtmosphereDensity);
            }
            catch (Exception ex)
            {
                _logger.Error($"Çevre eklenemedi: {ex.Message}", ex);
                throw;
            }
        }

        private async Task CreateGroundPlaneAsync(float size)
        {
            var groundObject = await _mayaConnector.CreatePlaneAsync(size, size);

            var sceneObject = new SceneObject;
            {
                Id = Guid.NewGuid().ToString(),
                Name = "GroundPlane",
                Type = SceneObjectType.Ground,
                Position = Vector3.Zero,
                Scale = Vector3.One,
                MayaObjectId = groundObject.Id;
            };

            lock (_syncLock)
            {
                _sceneObjects[sceneObject.Id] = sceneObject;
            }
        }

        private async Task<string> ExportToFbxAsync(ExportSettings settings)
        {
            try
            {
                var exportPath = Path.Combine(
                    Path.GetTempPath(),
                    "NEDA_Exports",
                    $"{_currentConfig.SceneName}_{DateTime.Now:yyyyMMdd_HHmmss}.fbx"
                );

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath));

                await _mayaConnector.ExportToFbxAsync(exportPath, settings);

                return exportPath;
            }
            catch (Exception ex)
            {
                _logger.Error($"FBX export hatası: {ex.Message}", ex);
                throw;
            }
        }

        private async Task ProcessMaterialsForUnrealAsync(string projectPath)
        {
            var materials = _sceneObjects.Values;
                .Where(o => o.Type == SceneObjectType.Model)
                .SelectMany(o => o.Materials)
                .Distinct();

            foreach (var material in materials)
            {
                await _unrealEngine.CreateMaterialAsync(projectPath, material);
            }
        }

        private async Task ProcessTexturesForUnrealAsync(string projectPath)
        {
            // Texture'ları Unreal formatına dönüştür ve import et;
            var textures = CollectTexturesFromScene();

            foreach (var texture in textures)
            {
                await _unrealEngine.ImportTextureAsync(projectPath, texture);
            }
        }

        private async Task GenerateLODsAsync()
        {
            var models = _sceneObjects.Values.Where(o => o.Type == SceneObjectType.Model);

            foreach (var model in models)
            {
                if (model.TriangleCount > 1000) // Sadece yüksek poly modeller için;
                {
                    await _mayaConnector.GenerateLODsAsync(model.MayaObjectId, 3);
                    model.HasLODs = true;
                }
            }
        }

        private async Task ReducePolygonsAsync()
        {
            var highPolyModels = _sceneObjects.Values;
                .Where(o => o.Type == SceneObjectType.Model && o.TriangleCount > 5000);

            foreach (var model in highPolyModels)
            {
                await _mayaConnector.ReducePolygonsAsync(model.MayaObjectId, 0.7f); // %30 azaltma;
                model.IsOptimized = true;
            }
        }

        private async Task MergeMaterialsAsync()
        {
            await _mayaConnector.MergeSimilarMaterialsAsync();
        }

        private async Task GenerateLightmapUVsAsync()
        {
            var models = _sceneObjects.Values.Where(o => o.Type == SceneObjectType.Model);

            foreach (var model in models)
            {
                await _mayaConnector.GenerateLightmapUVsAsync(model.MayaObjectId);
                model.HasLightmapUVs = true;
            }
        }

        private async Task OptimizeObjectAsync(SceneObject sceneObject)
        {
            if (sceneObject.TriangleCount > 10000)
            {
                await _mayaConnector.OptimizeMeshAsync(sceneObject.MayaObjectId);
                sceneObject.IsOptimized = true;
            }
        }

        private void UpdateComplexity()
        {
            var totalTriangles = _sceneObjects.Values.Sum(o => o.TriangleCount);
            var totalLights = _sceneObjects.Values.Count(o => o.Type == SceneObjectType.Light);

            var complexityScore = (totalTriangles / 1000) + (totalLights * 10);

            if (complexityScore < 50)
                Complexity = SceneComplexity.Low;
            else if (complexityScore < 200)
                Complexity = SceneComplexity.Medium;
            else if (complexityScore < 500)
                Complexity = SceneComplexity.High;
            else;
                Complexity = SceneComplexity.VeryHigh;
        }

        private float CalculatePerformanceGain()
        {
            // Basit bir performans kazancı hesaplaması;
            var optimizedObjects = _sceneObjects.Values.Count(o => o.IsOptimized);
            var totalObjects = _sceneObjects.Count;

            if (totalObjects == 0) return 0;

            return (optimizedObjects / (float)totalObjects) * 30; // %30'a kadar performans artışı;
        }

        private float CalculateMemoryReduction()
        {
            var reducedModels = _sceneObjects.Values.Count(o => o.IsOptimized && o.Type == SceneObjectType.Model);
            var totalModels = _sceneObjects.Values.Count(o => o.Type == SceneObjectType.Model);

            if (totalModels == 0) return 0;

            return (reducedModels / (float)totalModels) * 40; // %40'a kadar bellek azaltması;
        }

        private IEnumerable<string> CollectTexturesFromScene()
        {
            var textures = new List<string>();

            foreach (var sceneObject in _sceneObjects.Values)
            {
                if (sceneObject.Textures != null)
                    textures.AddRange(sceneObject.Textures);
            }

            return textures.Distinct();
        }

        #endregion;

        #region Validation Methods;

        private void ValidateConfiguration(SceneConfiguration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config), "Sahne yapılandırması null olamaz");

            if (string.IsNullOrWhiteSpace(config.SceneName))
                throw new ArgumentException("Sahne adı boş olamaz", nameof(config.SceneName));

            if (config.UnitScale <= 0)
                throw new ArgumentException("Unit scale pozitif olmalı", nameof(config.UnitScale));
        }

        private void ValidateModelPath(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("Model yolu boş olamaz", nameof(modelPath));

            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Model dosyası bulunamadı: {modelPath}");

            var validExtensions = new[] { ".fbx", ".obj", ".ma", ".mb" };
            var extension = Path.GetExtension(modelPath).ToLowerInvariant();

            if (!validExtensions.Contains(extension))
                throw new ArgumentException($"Desteklenmeyen dosya formatı: {extension}");
        }

        private void ValidateProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Proje yolu boş olamaz", nameof(projectPath));

            if (!Directory.Exists(projectPath))
                throw new DirectoryNotFoundException($"Proje dizini bulunamadı: {projectPath}");

            // Unreal proje dosyası kontrolü;
            var uprojectFiles = Directory.GetFiles(projectPath, "*.uproject");
            if (!uprojectFiles.Any())
                throw new InvalidOperationException($"Geçerli Unreal Engine projesi değil: {projectPath}");
        }

        #endregion;

        #region Event Methods;

        protected virtual void OnBuildStarted(SceneBuildEventArgs e)
        {
            BuildStarted?.Invoke(this, e);
        }

        protected virtual void OnBuildCompleted(SceneBuildEventArgs e)
        {
            BuildCompleted?.Invoke(this, e);
        }

        protected virtual void OnProgressChanged(SceneProgressEventArgs e)
        {
            ProgressChanged?.Invoke(this, e);
        }

        protected virtual void OnObjectAdded(SceneObjectEventArgs e)
        {
            ObjectAdded?.Invoke(this, e);
        }

        protected virtual void OnErrorOccurred(SceneErrorEventArgs e)
        {
            ErrorOccurred?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _mayaConnector?.Dispose();
                    _melScriptRunner?.Dispose();
                    _performanceMonitor?.Stop();

                    lock (_syncLock)
                    {
                        _sceneObjects.Clear();
                        _operationQueue.Clear();
                    }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SceneBuilder()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Sahne nesnesi temsilcisi;
        /// </summary>
        public class SceneObject;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public SceneObjectType Type { get; set; }
            public string MeshPath { get; set; }
            public Vector3 Position { get; set; }
            public Vector3 Scale { get; set; }
            public Quaternion Rotation { get; set; }
            public string MayaObjectId { get; set; }
            public LightType LightType { get; set; }
            public float LightIntensity { get; set; }
            public Color LightColor { get; set; }
            public int MaterialCount { get; set; }
            public int VertexCount { get; set; }
            public int TriangleCount { get; set; }
            public bool IsOptimized { get; set; }
            public bool HasLODs { get; set; }
            public bool HasLightmapUVs { get; set; }
            public List<string> Materials { get; set; } = new List<string>();
            public List<string> Textures { get; set; } = new List<string>();
        }

        /// <summary>
        /// Sahne yapılandırması;
        /// </summary>
        public class SceneConfiguration;
        {
            public string SceneName { get; set; }
            public string OutputPath { get; set; }
            public float UnitScale { get; set; } = 1.0f;
            public float GridSize { get; set; } = 1.0f;
            public bool GridVisibility { get; set; } = true;
            public ViewportQuality ViewportQuality { get; set; } = ViewportQuality.High;
            public bool EnableLighting { get; set; } = true;
            public LightingSetup LightingSetup { get; set; } = new LightingSetup();
            public bool AddDefaultCamera { get; set; } = true;
            public CameraSettings CameraSettings { get; set; } = new CameraSettings();
            public bool EnableEnvironment { get; set; } = true;
            public EnvironmentSettings EnvironmentSettings { get; set; } = new EnvironmentSettings();
            public AnimationSettings AnimationSettings { get; set; }
        }

        /// <summary>
        /// Işıklandırma kurulumu;
        /// </summary>
        public class LightingSetup;
        {
            public LightingType Type { get; set; } = LightingType.ThreePoint;
            public float KeyLightIntensity { get; set; } = 1.0f;
            public float FillLightIntensity { get; set; } = 0.5f;
            public float BackLightIntensity { get; set; } = 0.3f;
            public string HDRI Path { get; set; }
    }

    /// <summary>
    /// Kamera ayarları;
    /// </summary>
    public class CameraSettings;
    {
        public Vector3 Position { get; set; } = new Vector3(0, 5, -10);
        public Vector3 Target { get; set; } = Vector3.Zero;
        public float FieldOfView { get; set; } = 60.0f;
        public float NearClip { get; set; } = 0.1f;
        public float FarClip { get; set; } = 1000.0f;
    }

    /// <summary>
    /// Çevre ayarları;
    /// </summary>
    public class EnvironmentSettings;
    {
        public bool AddGroundPlane { get; set; } = true;
        public float GroundSize { get; set; } = 100.0f;
        public bool AddSkyDome { get; set; } = true;
        public float SkyRadius { get; set; } = 500.0f;
        public bool AddAtmosphere { get; set; } = true;
        public float AtmosphereDensity { get; set; } = 0.5f;
    }

    /// <summary>
    /// Animasyon ayarları;
    /// </summary>
    public class AnimationSettings;
    {
        public int StartFrame { get; set; } = 0;
        public int EndFrame { get; set; } = 100;
        public float FrameRate { get; set; } = 24.0f;
    }

    /// <summary>
    /// Dışa aktarma ayarları;
    /// </summary>
    public class ExportSettings;
    {
        public bool ExportMaterials { get; set; } = true;
        public bool ExportTextures { get; set; } = true;
        public bool ExportAnimations { get; set; } = true;
        public bool ExportLODs { get; set; } = true;
        public float ScaleFactor { get; set; } = 1.0f;
        public CoordinateSystem TargetSystem { get; set; } = CoordinateSystem.Unreal;
    }

    /// <summary>
    /// Sahne oluşturma sonucu;
    /// </summary>
    public class SceneResult;
    {
        public bool Success { get; set; }
        public string SceneName { get; set; }
        public int ObjectCount { get; set; }
        public SceneComplexity Complexity { get; set; }
        public string FilePath { get; set; }
        public DateTime CreationTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Optimizasyon sonucu;
    /// </summary>
    public class OptimizationResult;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public OptimizationLevel OptimizationLevel { get; set; }
        public int InitialObjectCount { get; set; }
        public float PerformanceGain { get; set; }
        public float MemoryReduction { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
    }

    /// <summary>
    /// Dışa aktarma sonucu;
    /// </summary>
    public class ExportResult;
    {
        public bool Success { get; set; }
        public string ExportPath { get; set; }
        public int ImportedObjects { get; set; }
        public List<string> ImportedMaterials { get; set; } = new List<string>();
        public List<string> ImportedTextures { get; set; } = new List<string>();
    }

    /// <summary>
    /// Sahne operasyonu;
    /// </summary>
    private class SceneOperation;
    {
        public OperationType Type { get; set; }
        public object Data { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Sahne nesne türleri;
    /// </summary>
    public enum SceneObjectType;
    {
        Model,
        Light,
        Camera,
        Ground,
        Environment,
        Helper;
    }

    /// <summary>
    /// Işık türleri;
    /// </summary>
    public enum LightType;
    {
        Directional,
        Point,
        Spot,
        Area,
        Ambient;
    }

    /// <summary>
    /// Işıklandırma türleri;
    /// </summary>
    public enum LightingType;
    {
        ThreePoint,
        HDRI,
        Studio,
        Outdoor,
        Cinematic;
    }

    /// <summary>
    /// Viewport kalite seviyeleri;
    /// </summary>
    public enum ViewportQuality;
    {
        Low,
        Medium,
        High,
        Ultra;
    }

    /// <summary>
    /// Koordinat sistemleri;
    /// </summary>
    public enum CoordinateSystem;
    {
        Maya,
        Unreal,
        Unity,
        Max;
    }

    /// <summary>
    /// Sahne karmaşıklık seviyeleri;
    /// </summary>
    public enum SceneComplexity;
    {
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh;
    }

    /// <summary>
    /// Optimizasyon seviyeleri;
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
    /// Sahne oluşturma durumları;
    /// </summary>
    public enum SceneBuildStatus;
    {
        Idle,
        Building,
        Optimizing,
        Exporting,
        Completed,
        Error;
    }

    /// <summary>
    /// Operasyon türleri;
    /// </summary>
    private enum OperationType;
    {
        AddModel,
        AddLight,
        AddCamera,
        Transform,
        Delete,
        Duplicate,
        Group,
        Ungroup;
    }

    #endregion;

    #region Event Args Classes;

    /// <summary>
    /// Sahne oluşturma event argümanları;
    /// </summary>
    public class SceneBuildEventArgs : EventArgs;
    {
        public string SceneName { get; }
        public DateTime StartTime { get; }
        public DateTime? EndTime { get; }
        public SceneResult Result { get; }

        public SceneBuildEventArgs(string sceneName, DateTime startTime)
        {
            SceneName = sceneName;
            StartTime = startTime;
        }

        public SceneBuildEventArgs(string sceneName, DateTime startTime, SceneResult result)
            : this(sceneName, startTime)
        {
            EndTime = DateTime.UtcNow;
            Result = result;
        }
    }

    /// <summary>
    /// İlerleme event argümanları;
    /// </summary>
    public class SceneProgressEventArgs : EventArgs;
    {
        public string Message { get; }
        public int CurrentCount { get; }
        public DateTime Timestamp { get; }

        public SceneProgressEventArgs(string message, int currentCount)
        {
            Message = message;
            CurrentCount = currentCount;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Sahne nesnesi event argümanları;
    /// </summary>
    public class SceneObjectEventArgs : EventArgs;
    {
        public SceneObject SceneObject { get; }
        public DateTime Timestamp { get; }

        public SceneObjectEventArgs(SceneObject sceneObject)
        {
            SceneObject = sceneObject;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Hata event argümanları;
    /// </summary>
    public class SceneErrorEventArgs : EventArgs;
    {
        public Exception Exception { get; }
        public string Operation { get; }
        public DateTime Timestamp { get; }

        public SceneErrorEventArgs(Exception exception, string operation)
        {
            Exception = exception;
            Operation = operation;
            Timestamp = DateTime.UtcNow;
        }
    }

    #endregion;
}

/// <summary>
/// SceneBuilder özel exception sınıfı;
/// </summary>
public class SceneBuilderException : Exception
{
    public string SceneName { get; }
    public string Operation { get; }

    public SceneBuilderException(string message) : base(message) { }

    public SceneBuilderException(string message, Exception innerException)
        : base(message, innerException) { }

    public SceneBuilderException(string message, string sceneName, string operation)
        : base(message)
    {
        SceneName = sceneName;
        Operation = operation;
    }
}

#region Helper Classes (Diğer dosyalarda tanımlanacak)

/// <summary>
/// 3D vektör temsilcisi;
/// </summary>
public struct Vector3;
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static Vector3 Zero => new Vector3(0, 0, 0);
    public static Vector3 One => new Vector3(1, 1, 1);
}

/// <summary>
/// Quaternion temsilcisi;
/// </summary>
public struct Quaternion;
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; }

    public Quaternion(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public static Quaternion Identity => new Quaternion(0, 0, 0, 1);
}

/// <summary>
/// Renk temsilcisi;
/// </summary>
public struct Color;
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }

    public Color(float r, float g, float b, float a = 1.0f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static Color White => new Color(1, 1, 1);
    public static Color Black => new Color(0, 0, 0);
    public static Color Red => new Color(1, 0, 0);
    public static Color Green => new Color(0, 1, 0);
    public static Color Blue => new Color(0, 0, 1);
}

    #endregion;
}
