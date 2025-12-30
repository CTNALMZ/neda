using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.RenderManager;
using NEDA.Monitoring.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace NEDA.GameDesign.LevelDesign.LightingDesign;
{
    /// <summary>
    /// Gelişmiş Gölge Sistemi - Dinamik gölge yönetimi ve optimizasyonu sağlar;
    /// </summary>
    public class ShadowSystem : IDisposable
    {
        #region Sabitler ve Tanımlamalar;

        private const int MAX_SHADOW_MAPS = 8;
        private const float SHADOW_BIAS = 0.001f;
        private const float CASCADE_SPLIT_LAMBDA = 0.95f;
        private const int PCF_KERNEL_SIZE = 3;

        public enum ShadowQuality;
        {
            Low = 0,        // 512x512, basit gölgeler;
            Medium = 1,     // 1024x1024, PCF 2x2;
            High = 2,       // 2048x2048, PCF 3x3, VSM;
            Ultra = 3       // 4096x4096, CSM 4 seviye, ESM;
        }

        public enum ShadowTechnique;
        {
            ShadowMapping,
            VarianceShadowMaps,
            ExponentialShadowMaps,
            CascadedShadowMaps,
            PercentageCloserSoftShadows;
        }

        #endregion;

        #region Özellikler ve Alanlar;

        private readonly ILogger _logger;
        private readonly DiagnosticTool _diagnosticTool;
        private readonly RenderEngine _renderEngine;

        private bool _isInitialized = false;
        private bool _isEnabled = true;
        private ShadowQuality _currentQuality = ShadowQuality.High;
        private ShadowTechnique _currentTechnique = ShadowTechnique.CascadedShadowMaps;

        private Dictionary<int, ShadowMap> _shadowMaps;
        private List<CascadeSplit> _cascadeSplits;
        private ShadowConfiguration _config;

        private float _shadowDistance = 100.0f;
        private float _shadowFadeStart = 80.0f;
        private int _maxShadowCascades = 4;

        private Matrix4x4[] _cascadeMatrices;
        private float[] _cascadeSplitDistances;

        private object _renderLock = new object();

        #endregion;

        #region Yapılar ve Sınıflar;

        /// <summary>
        /// Gölge haritası yapılandırması;
        /// </summary>
        public struct ShadowConfiguration;
        {
            public int Resolution;
            public int DepthFormat;
            public bool GenerateMipMaps;
            public bool UseHardwarePcf;
            public float MaxDistance;
            public bool StabilizeCascades;
            public float SlopeScaleBias;
            public float DepthBias;
        }

        /// <summary>
        /// Gölge haritası verileri;
        /// </summary>
        private class ShadowMap : IDisposable
        {
            public int Id { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public uint TextureId { get; set; }
            public uint FramebufferId { get; set; }
            public Matrix4x4 ViewProjectionMatrix { get; set; }
            public Vector3 LightDirection { get; set; }
            public float NearPlane { get; set; }
            public float FarPlane { get; set; }
            public DateTime LastUpdate { get; set; }
            public bool IsDirty { get; set; }

            public void Dispose()
            {
                // GPU kaynaklarını serbest bırak;
                ReleaseResources();
            }

            private void ReleaseResources()
            {
                // Gerçek GPU kaynak serbest bırakma işlemleri;
                // Bu kısım render engine'e bağımlı olacaktır;
            }
        }

        /// <summary>
        Cascade bölme verileri;
        /// </summary>
        private struct CascadeSplit;
        {
            public float Near;
            public float Far;
            public Matrix4x4 ViewProjection;
            public Vector4 FrustumCorners;
        }

        /// <summary>
        /// Gölge istatistikleri;
        /// </summary>
        public struct ShadowStatistics;
        {
            public int TotalShadowMaps;
            public int RenderedThisFrame;
            public float AverageRenderTime;
            public int TotalDrawCalls;
            public float MemoryUsageMB;
            public int VisibleShadowCasters;
        }

        #endregion;

        #region Olaylar;

        /// <summary>
        /// Gölge kalitesi değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ShadowQualityChangedEventArgs> ShadowQualityChanged;

        /// <summary>
        /// Gölge tekniği değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ShadowTechniqueChangedEventArgs> ShadowTechniqueChanged;

        /// <summary>
        /// Gölge haritası oluşturulduğunda tetiklenir;
        /// </summary>
        public event EventHandler<ShadowMapCreatedEventArgs> ShadowMapCreated;

        public class ShadowQualityChangedEventArgs : EventArgs;
        {
            public ShadowQuality OldQuality { get; set; }
            public ShadowQuality NewQuality { get; set; }
        }

        public class ShadowTechniqueChangedEventArgs : EventArgs;
        {
            public ShadowTechnique OldTechnique { get; set; }
            public ShadowTechnique NewTechnique { get; set; }
        }

        public class ShadowMapCreatedEventArgs : EventArgs;
        {
            public int ShadowMapId { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        #endregion;

        #region Constructor ve Initialization;

        /// <summary>
        /// ShadowSystem constructor;
        /// </summary>
        public ShadowSystem(RenderEngine renderEngine, ILogger logger = null)
        {
            _renderEngine = renderEngine ?? throw new ArgumentNullException(nameof(renderEngine));
            _logger = logger ?? LogManager.GetLogger(typeof(ShadowSystem).FullName);
            _diagnosticTool = new DiagnosticTool("ShadowSystem");

            _shadowMaps = new Dictionary<int, ShadowMap>();
            _cascadeSplits = new List<CascadeSplit>();
            _cascadeMatrices = new Matrix4x4[_maxShadowCascades];
            _cascadeSplitDistances = new float[_maxShadowCascades + 1];

            _config = new ShadowConfiguration;
            {
                Resolution = GetResolutionForQuality(_currentQuality),
                DepthFormat = 32,
                GenerateMipMaps = _currentQuality >= ShadowQuality.High,
                UseHardwarePcf = true,
                MaxDistance = _shadowDistance,
                StabilizeCascades = true,
                SlopeScaleBias = 1.5f,
                DepthBias = SHADOW_BIAS;
            };
        }

        /// <summary>
        /// Gölge sistemini başlat;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.Warning("ShadowSystem zaten başlatılmış.");
                return;
            }

            try
            {
                _diagnosticTool.StartOperation("Initialize");

                await Task.Run(() =>
                {
                    // GPU kaynaklarını hazırla;
                    InitializeGpuResources();

                    // Cascade split'leri hesapla;
                    CalculateCascadeSplits();

                    // Varsayılan gölge haritalarını oluştur;
                    CreateDefaultShadowMaps();

                    _isInitialized = true;

                    _logger.Info($"ShadowSystem başlatıldı. Kalite: {_currentQuality}, Teknik: {_currentTechnique}");
                });

                _diagnosticTool.EndOperation();
            }
            catch (Exception ex)
            {
                _logger.Error($"ShadowSystem başlatma hatası: {ex.Message}", ex);
                throw new ShadowSystemInitializationException(
                    "Gölge sistemi başlatılamadı", ex);
            }
        }

        private void InitializeGpuResources()
        {
            // Render engine üzerinden GPU kaynaklarını hazırla;
            // Bu kısım render engine API'sine bağlı olacaktır;
            // Örnek: _renderEngine.CreateShadowTexture(...)
        }

        private void CreateDefaultShadowMaps()
        {
            lock (_renderLock)
            {
                // Ana directional light için gölge haritası;
                CreateShadowMap(0, new Vector3(-0.5f, -1.0f, -0.25f),
                    _config.Resolution, _config.Resolution);

                // Ek gölge haritaları için slotlar rezerve et;
                for (int i = 1; i < MAX_SHADOW_MAPS; i++)
                {
                    // Boş shadow map slotları oluştur;
                    _shadowMaps[i] = null;
                }
            }
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Gölge sistemini güncelle;
        /// </summary>
        public void Update(float deltaTime, Camera camera, List<ILightSource> lightSources)
        {
            if (!_isEnabled || !_isInitialized) return;

            _diagnosticTool.StartOperation("Update");

            try
            {
                UpdateShadowCascades(camera);
                UpdateShadowMaps(camera, lightSources);
                OptimizeShadowMaps();
            }
            finally
            {
                _diagnosticTool.EndOperation();
            }
        }

        /// <summary>
        /// Gölge haritası oluştur;
        /// </summary>
        public int CreateShadowMap(Vector3 lightDirection, int width, int height)
        {
            ValidateResolution(width, height);

            lock (_renderLock)
            {
                int newId = FindAvailableShadowMapId();
                if (newId == -1)
                {
                    throw new ShadowSystemException("Maksimum gölge haritası sayısına ulaşıldı");
                }

                var shadowMap = new ShadowMap;
                {
                    Id = newId,
                    Width = width,
                    Height = height,
                    LightDirection = Vector3.Normalize(lightDirection),
                    LastUpdate = DateTime.UtcNow,
                    IsDirty = true;
                };

                // GPU kaynağı oluştur;
                CreateGpuShadowMap(shadowMap);

                _shadowMaps[newId] = shadowMap;

                OnShadowMapCreated(newId, width, height);

                _logger.Debug($"Gölge haritası oluşturuldu: ID={newId}, Boyut={width}x{height}");

                return newId;
            }
        }

        /// <summary>
        /// Gölge haritasını sil;
        /// </summary>
        public void DeleteShadowMap(int shadowMapId)
        {
            lock (_renderLock)
            {
                if (_shadowMaps.TryGetValue(shadowMapId, out var shadowMap))
                {
                    shadowMap.Dispose();
                    _shadowMaps.Remove(shadowMapId);

                    _logger.Debug($"Gölge haritası silindi: ID={shadowMapId}");
                }
            }
        }

        /// <summary>
        /// Gölge kalitesini değiştir;
        /// </summary>
        public void SetShadowQuality(ShadowQuality quality)
        {
            if (_currentQuality == quality) return;

            var oldQuality = _currentQuality;
            _currentQuality = quality;

            // Yapılandırmayı güncelle;
            _config.Resolution = GetResolutionForQuality(quality);
            _config.GenerateMipMaps = quality >= ShadowQuality.High;

            // Mevcut gölge haritalarını yeniden boyutlandır;
            ResizeExistingShadowMaps();

            OnShadowQualityChanged(oldQuality, quality);

            _logger.Info($"Gölge kalitesi değiştirildi: {oldQuality} -> {quality}");
        }

        /// <summary>
        /// Gölge tekniğini değiştir;
        /// </summary>
        public void SetShadowTechnique(ShadowTechnique technique)
        {
            if (_currentTechnique == technique) return;

            var oldTechnique = _currentTechnique;
            _currentTechnique = technique;

            // Tekniğe özgü ayarları uygula;
            ApplyTechniqueSettings(technique);

            OnShadowTechniqueChanged(oldTechnique, technique);

            _logger.Info($"Gölge tekniği değiştirildi: {oldTechnique} -> {technique}");
        }

        /// <summary>
        /// Gölge mesafesini ayarla;
        /// </summary>
        public void SetShadowDistance(float distance)
        {
            if (distance <= 0)
                throw new ArgumentException("Gölge mesafesi pozitif olmalıdır");

            _shadowDistance = distance;
            _config.MaxDistance = distance;

            // Cascade split'leri yeniden hesapla;
            CalculateCascadeSplits();

            _logger.Debug($"Gölge mesafesi ayarlandı: {distance}");
        }

        /// <summary>
        /// Gölge haritası çözünürlüğünü al;
        /// </summary>
        public Vector2 GetShadowMapResolution(int shadowMapId)
        {
            if (_shadowMaps.TryGetValue(shadowMapId, out var shadowMap))
            {
                return new Vector2(shadowMap.Width, shadowMap.Height);
            }

            return Vector2.Zero;
        }

        /// <summary>
        /// Gölge matrisini al;
        /// </summary>
        public Matrix4x4 GetShadowMatrix(int shadowMapId)
        {
            if (_shadowMaps.TryGetValue(shadowMapId, out var shadowMap))
            {
                return shadowMap.ViewProjectionMatrix;
            }

            return Matrix4x4.Identity;
        }

        /// <summary>
        /// Gölge istatistiklerini al;
        /// </summary>
        public ShadowStatistics GetStatistics()
        {
            int renderedThisFrame = 0;
            float totalMemory = 0;

            foreach (var shadowMap in _shadowMaps.Values)
            {
                if (shadowMap != null && shadowMap.LastUpdate.Date == DateTime.UtcNow.Date)
                {
                    renderedThisFrame++;
                    totalMemory += (shadowMap.Width * shadowMap.Height * 4) / (1024f * 1024f); // MB cinsinden;
                }
            }

            return new ShadowStatistics;
            {
                TotalShadowMaps = _shadowMaps.Count,
                RenderedThisFrame = renderedThisFrame,
                AverageRenderTime = _diagnosticTool.GetAverageOperationTime("Update"),
                TotalDrawCalls = 0, // Render engine'den alınacak;
                MemoryUsageMB = totalMemory,
                VisibleShadowCasters = CalculateVisibleShadowCasters()
            };
        }

        /// <summary>
        /// Gölge testi yap;
        /// </summary>
        public float SampleShadow(Vector3 worldPosition, int shadowMapId)
        {
            if (!_shadowMaps.TryGetValue(shadowMapId, out var shadowMap) || shadowMap == null)
                return 1.0f; // Gölge yok;

            // Dünya pozisyonunu gölge uzayına dönüştür;
            Vector4 shadowPos = Vector4.Transform(
                new Vector4(worldPosition, 1.0f),
                shadowMap.ViewProjectionMatrix);

            // Perspektif bölme;
            shadowPos /= shadowPos.W;

            // [0,1] aralığına getir;
            shadowPos.X = shadowPos.X * 0.5f + 0.5f;
            shadowPos.Y = shadowPos.Y * 0.5f + 0.5f;
            shadowPos.Z = shadowPos.Z * 0.5f + 0.5f;

            // Gölge haritasından derinlik değerini oku;
            float shadowDepth = SampleShadowMapDepth(shadowMap, shadowPos.X, shadowPos.Y);

            // Gölge karşılaştırması;
            float currentDepth = shadowPos.Z;
            float bias = CalculateBias(shadowMap.LightDirection);

            return currentDepth - bias > shadowDepth ? 0.0f : 1.0f;
        }

        /// <summary>
        /// Yumuşak gölge testi;
        /// </summary>
        public float SampleShadowPCF(Vector3 worldPosition, int shadowMapId, int kernelSize = PCF_KERNEL_SIZE)
        {
            float shadow = 0.0f;
            float totalWeight = 0.0f;

            // Poisson disk örnekleme noktaları;
            Vector2[] poissonDisk = GeneratePoissonDisk(kernelSize * kernelSize);

            for (int i = 0; i < poissonDisk.Length; i++)
            {
                Vector3 samplePos = worldPosition;
                samplePos.X += poissonDisk[i].X * 0.001f; // Ölçek faktörü;
                samplePos.Y += poissonDisk[i].Y * 0.001f;

                shadow += SampleShadow(samplePos, shadowMapId);
                totalWeight += 1.0f;
            }

            return shadow / totalWeight;
        }

        /// <summary>
        /// Cascade gölge testi;
        /// </summary>
        public float SampleCascadedShadow(Vector3 worldPosition, Camera camera)
        {
            float viewDepth = Vector3.Distance(worldPosition, camera.Position);

            // Doğru cascade'i bul;
            int cascadeIndex = 0;
            for (; cascadeIndex < _maxShadowCascades; cascadeIndex++)
            {
                if (viewDepth < _cascadeSplitDistances[cascadeIndex + 1])
                    break;
            }

            cascadeIndex = Math.Min(cascadeIndex, _maxShadowCascades - 1);

            // İlgili cascade için gölge testi yap;
            return SampleShadow(worldPosition, cascadeIndex);
        }

        #endregion;

        #region Private Methods;

        private void UpdateShadowCascades(Camera camera)
        {
            if (_currentTechnique != ShadowTechnique.CascadedShadowMaps)
                return;

            // Kameranın görüş frustumunu al;
            var frustumCorners = GetFrustumCorners(camera);

            // Her cascade için gölge matrisini hesapla;
            for (int i = 0; i < _maxShadowCascades; i++)
            {
                float nearSplit = _cascadeSplitDistances[i];
                float farSplit = _cascadeSplitDistances[i + 1];

                // Cascade frustum köşelerini hesapla;
                var cascadeCorners = CalculateCascadeFrustum(frustumCorners, nearSplit, farSplit);

                // Cascade için gölge matrisini hesapla;
                _cascadeMatrices[i] = CalculateShadowMatrix(cascadeCorners,
                    GetMainLightDirection());

                // Cascade stabilizasyonu;
                if (_config.StabilizeCascades)
                {
                    _cascadeMatrices[i] = StabilizeCascade(_cascadeMatrices[i],
                        _config.Resolution);
                }
            }
        }

        private void UpdateShadowMaps(Camera camera, List<ILightSource> lightSources)
        {
            foreach (var light in lightSources)
            {
                if (!light.CastShadows || light.ShadowMapId == -1)
                    continue;

                // Gölge haritasını güncelle;
                UpdateShadowMap(light.ShadowMapId, camera, light);
            }
        }

        private void UpdateShadowMap(int shadowMapId, Camera camera, ILightSource light)
        {
            if (!_shadowMaps.TryGetValue(shadowMapId, out var shadowMap) || shadowMap == null)
                return;

            // Gölge haritasını render et;
            RenderShadowMap(shadowMap, camera, light);

            shadowMap.LastUpdate = DateTime.UtcNow;
            shadowMap.IsDirty = false;
        }

        private void RenderShadowMap(ShadowMap shadowMap, Camera camera, ILightSource light)
        {
            // Gölge haritası render pass'ini başlat;
            _renderEngine.BeginShadowPass(shadowMap.TextureId, shadowMap.FramebufferId);

            try
            {
                // Gölge matrisini hesapla;
                shadowMap.ViewProjectionMatrix = CalculateLightViewProjection(
                    camera, light.Direction, shadowMap.NearPlane, shadowMap.FarPlane);

                // Gölge caster'larını render et;
                RenderShadowCasters(shadowMap);

                // Gölge haritasını blurla (VSM/ESM için)
                if (_currentQuality >= ShadowQuality.High)
                {
                    ApplyShadowFiltering(shadowMap);
                }
            }
            finally
            {
                _renderEngine.EndShadowPass();
            }
        }

        private Matrix4x4 CalculateLightViewProjection(Camera camera, Vector3 lightDir,
            float nearPlane, float farPlane)
        {
            // Light view matrix;
            Vector3 lightPos = camera.Position - lightDir * farPlane * 0.5f;
            Vector3 target = camera.Position;
            Vector3 up = Vector3.UnitY;

            if (Math.Abs(Vector3.Dot(lightDir, up)) > 0.99f)
                up = Vector3.UnitZ;

            Matrix4x4 lightView = Matrix4x4.CreateLookAt(lightPos, target, up);

            // Light projection matrix (orthographic)
            float orthoSize = CalculateOrthographicSize(camera, farPlane);
            Matrix4x4 lightProj = Matrix4x4.CreateOrthographic(
                orthoSize, orthoSize, nearPlane, farPlane);

            return lightView * lightProj;
        }

        private float CalculateOrthographicSize(Camera camera, float distance)
        {
            // Kameranın görüş açısına göre orthographic boyut hesapla;
            float halfFov = camera.FieldOfView * 0.5f * MathF.PI / 180.0f;
            return MathF.Tan(halfFov) * distance * 2.0f;
        }

        private void RenderShadowCasters(ShadowMap shadowMap)
        {
            // Gölge düşüren nesneleri bul ve render et;
            var shadowCasters = FindShadowCasters(shadowMap);

            foreach (var caster in shadowCasters)
            {
                // Depth-only render;
                _renderEngine.RenderDepthOnly(caster, shadowMap.ViewProjectionMatrix);
            }
        }

        private List<IRenderable> FindShadowCasters(ShadowMap shadowMap)
        {
            // Görünür ve gölge düşüren nesneleri bul;
            // Bu kısım render engine ve scene management ile entegre olacak;
            return new List<IRenderable>();
        }

        private void ApplyShadowFiltering(ShadowMap shadowMap)
        {
            switch (_currentTechnique)
            {
                case ShadowTechnique.VarianceShadowMaps:
                    ApplyVarianceShadowFilter(shadowMap);
                    break;
                case ShadowTechnique.ExponentialShadowMaps:
                    ApplyExponentialShadowFilter(shadowMap);
                    break;
                case ShadowTechnique.PercentageCloserSoftShadows:
                    ApplyPCSSFilter(shadowMap);
                    break;
            }
        }

        private void ApplyVarianceShadowFilter(ShadowMap shadowMap)
        {
            // VSM blur ve mipmap işlemleri;
            _renderEngine.ApplyGaussianBlur(shadowMap.TextureId, 2);
        }

        private void CalculateCascadeSplits()
        {
            _cascadeSplitDistances[0] = 0.1f; // Near plane;

            for (int i = 1; i <= _maxShadowCascades; i++)
            {
                float split = i / (float)_maxShadowCascades;
                float logSplit = 0.1f * MathF.Pow(_shadowDistance / 0.1f, split);
                float uniformSplit = 0.1f + (_shadowDistance - 0.1f) * split;

                // Log-uniform blend;
                _cascadeSplitDistances[i] = MathHelper.Lerp(
                    uniformSplit, logSplit, CASCADE_SPLIT_LAMBDA);
            }
        }

        private Vector3 GetMainLightDirection()
        {
            // Ana ışık kaynağının yönünü al;
            // Bu kısım ışık yönetim sistemi ile entegre olacak;
            return new Vector3(-0.5f, -1.0f, -0.25f);
        }

        private float CalculateBias(Vector3 lightDir)
        {
            // Dinamik bias hesaplama;
            float minBias = 0.001f;
            float maxBias = 0.01f;

            // Işık açısına göre bias ayarla;
            float dot = Math.Abs(Vector3.Dot(lightDir, Vector3.UnitY));
            float slopeScale = _config.SlopeScaleBias * (1.0f - dot);

            return _config.DepthBias + slopeScale;
        }

        private void OptimizeShadowMaps()
        {
            // Kullanılmayan gölge haritalarını temizle;
            var now = DateTime.UtcNow;
            var toRemove = new List<int>();

            foreach (var kvp in _shadowMaps)
            {
                if (kvp.Value != null &&
                    (now - kvp.Value.LastUpdate).TotalSeconds > 30.0f)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
            {
                DeleteShadowMap(id);
            }
        }

        private int FindAvailableShadowMapId()
        {
            for (int i = 0; i < MAX_SHADOW_MAPS; i++)
            {
                if (!_shadowMaps.ContainsKey(i) || _shadowMaps[i] == null)
                    return i;
            }

            return -1;
        }

        private void ValidateResolution(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Çözünürlük pozitif olmalıdır");

            if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height))
                throw new ArgumentException("Çözünürlük 2'nin kuvveti olmalıdır");

            int maxRes = GetResolutionForQuality(ShadowQuality.Ultra);
            if (width > maxRes || height > maxRes)
                throw new ArgumentException($"Maksimum çözünürlük: {maxRes}");
        }

        private bool IsPowerOfTwo(int x)
        {
            return (x & (x - 1)) == 0;
        }

        private int GetResolutionForQuality(ShadowQuality quality)
        {
            return quality switch;
            {
                ShadowQuality.Low => 512,
                ShadowQuality.Medium => 1024,
                ShadowQuality.High => 2048,
                ShadowQuality.Ultra => 4096,
                _ => 1024;
            };
        }

        private void ResizeExistingShadowMaps()
        {
            lock (_renderLock)
            {
                foreach (var shadowMap in _shadowMaps.Values)
                {
                    if (shadowMap != null)
                    {
                        // GPU kaynağını yeniden boyutlandır;
                        ResizeGpuShadowMap(shadowMap, _config.Resolution, _config.Resolution);
                        shadowMap.Width = _config.Resolution;
                        shadowMap.Height = _config.Resolution;
                        shadowMap.IsDirty = true;
                    }
                }
            }
        }

        private void ApplyTechniqueSettings(ShadowTechnique technique)
        {
            switch (technique)
            {
                case ShadowTechnique.VarianceShadowMaps:
                    _config.GenerateMipMaps = true;
                    _config.DepthFormat = 32; // Float format;
                    break;

                case ShadowTechnique.ExponentialShadowMaps:
                    _config.GenerateMipMaps = false;
                    _config.DepthFormat = 16; // Half-float;
                    break;

                case ShadowTechnique.CascadedShadowMaps:
                    _maxShadowCascades = 4;
                    CalculateCascadeSplits();
                    break;

                case ShadowTechnique.PercentageCloserSoftShadows:
                    _config.UseHardwarePcf = true;
                    break;
            }
        }

        private int CalculateVisibleShadowCasters()
        {
            // Görünür gölge caster'larını hesapla;
            // Bu kısım culling sistemi ile entegre olacak;
            return 0;
        }

        private float SampleShadowMapDepth(ShadowMap shadowMap, float u, float v)
        {
            // Gölge haritasından derinlik değerini oku;
            // Bu kısım render engine'e bağımlı;
            return _renderEngine.SampleDepthTexture(shadowMap.TextureId, u, v);
        }

        private Vector2[] GeneratePoissonDisk(int sampleCount)
        {
            var samples = new Vector2[sampleCount];
            float angleStep = MathF.PI * 2.0f / sampleCount;

            for (int i = 0; i < sampleCount; i++)
            {
                float angle = angleStep * i;
                float radius = MathF.Sqrt((i + 0.5f) / sampleCount);

                samples[i] = new Vector2(
                    radius * MathF.Cos(angle),
                    radius * MathF.Sin(angle));
            }

            return samples;
        }

        private Vector3[] GetFrustumCorners(Camera camera)
        {
            // Kameranın görüş frustum köşelerini hesapla;
            var corners = new Vector3[8];
            float aspect = camera.AspectRatio;
            float fovY = camera.FieldOfView * MathF.PI / 180.0f;

            float near = camera.NearPlane;
            float far = _shadowDistance;

            float tanHalfFov = MathF.Tan(fovY * 0.5f);

            // Near plane;
            float nearHeight = near * tanHalfFov;
            float nearWidth = nearHeight * aspect;

            // Far plane;
            float farHeight = far * tanHalfFov;
            float farWidth = farHeight * aspect;

            // Köşeleri hesapla;
            corners[0] = new Vector3(-nearWidth, -nearHeight, near);  // Near bottom-left;
            corners[1] = new Vector3(nearWidth, -nearHeight, near);   // Near bottom-right;
            corners[2] = new Vector3(-nearWidth, nearHeight, near);   // Near top-left;
            corners[3] = new Vector3(nearWidth, nearHeight, near);    // Near top-right;

            corners[4] = new Vector3(-farWidth, -farHeight, far);     // Far bottom-left;
            corners[5] = new Vector3(farWidth, -farHeight, far);      // Far bottom-right;
            corners[6] = new Vector3(-farWidth, farHeight, far);      // Far top-left;
            corners[7] = new Vector3(farWidth, farHeight, far);       // Far top-right;

            return corners;
        }

        private Vector3[] CalculateCascadeFrustum(Vector3[] baseCorners, float near, float far)
        {
            var cascadeCorners = new Vector3[8];

            // Linear interpolation between near and far planes;
            float lerpNear = near / _shadowDistance;
            float lerpFar = far / _shadowDistance;

            for (int i = 0; i < 4; i++)
            {
                // Near plane corners;
                cascadeCorners[i] = Vector3.Lerp(baseCorners[i], baseCorners[i + 4], lerpNear);
                // Far plane corners;
                cascadeCorners[i + 4] = Vector3.Lerp(baseCorners[i], baseCorners[i + 4], lerpFar);
            }

            return cascadeCorners;
        }

        private Matrix4x4 CalculateShadowMatrix(Vector3[] frustumCorners, Vector3 lightDir)
        {
            // Calculate bounding sphere of frustum in light space;
            Vector3 center = Vector3.Zero;
            foreach (var corner in frustumCorners)
                center += corner;
            center /= frustumCorners.Length;

            // Calculate radius;
            float radius = 0;
            foreach (var corner in frustumCorners)
            {
                float distance = Vector3.Distance(corner, center);
                radius = Math.Max(radius, distance);
            }

            // Create orthographic projection;
            float orthoSize = radius;
            Matrix4x4 lightProj = Matrix4x4.CreateOrthographic(
                orthoSize * 2, orthoSize * 2, -radius, radius);

            // Create light view matrix;
            Vector3 lightPos = center - lightDir * radius;
            Matrix4x4 lightView = Matrix4x4.CreateLookAt(
                lightPos, center, Vector3.UnitY);

            return lightView * lightProj;
        }

        private Matrix4x4 StabilizeCascade(Matrix4x4 shadowMatrix, int resolution)
        {
            // Texel-size stabilization;
            Matrix4x4.Invert(shadowMatrix, out Matrix4x4 invShadowMatrix);

            // Project origin;
            Vector4 origin = Vector4.Transform(Vector4.Zero, invShadowMatrix);
            origin /= origin.W;

            // Round to texel size;
            float texelSize = 2.0f / resolution;
            origin.X = MathF.Floor(origin.X / texelSize) * texelSize;
            origin.Y = MathF.Floor(origin.Y / texelSize) * texelSize;

            // Translate shadow matrix;
            Vector4 roundedOrigin = Vector4.Transform(origin, shadowMatrix);
            Matrix4x4 translate = Matrix4x4.CreateTranslation(
                -roundedOrigin.X, -roundedOrigin.Y, 0);

            return shadowMatrix * translate;
        }

        private void CreateGpuShadowMap(ShadowMap shadowMap)
        {
            // Render engine üzerinden GPU kaynağı oluştur;
            shadowMap.TextureId = _renderEngine.CreateShadowTexture(
                shadowMap.Width,
                shadowMap.Height,
                _config.DepthFormat,
                _config.GenerateMipMaps);

            shadowMap.FramebufferId = _renderEngine.CreateFramebuffer(
                shadowMap.TextureId);
        }

        private void ResizeGpuShadowMap(ShadowMap shadowMap, int newWidth, int newHeight)
        {
            // Mevcut kaynakları serbest bırak;
            _renderEngine.DeleteTexture(shadowMap.TextureId);
            _renderEngine.DeleteFramebuffer(shadowMap.FramebufferId);

            // Yeni boyutta yeniden oluştur;
            CreateGpuShadowMap(shadowMap);
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnShadowQualityChanged(ShadowQuality oldQuality, ShadowQuality newQuality)
        {
            ShadowQualityChanged?.Invoke(this, new ShadowQualityChangedEventArgs;
            {
                OldQuality = oldQuality,
                NewQuality = newQuality;
            });
        }

        protected virtual void OnShadowTechniqueChanged(ShadowTechnique oldTechnique, ShadowTechnique newTechnique)
        {
            ShadowTechniqueChanged?.Invoke(this, new ShadowTechniqueChangedEventArgs;
            {
                OldTechnique = oldTechnique,
                NewTechnique = newTechnique;
            });
        }

        protected virtual void OnShadowMapCreated(int shadowMapId, int width, int height)
        {
            ShadowMapCreated?.Invoke(this, new ShadowMapCreatedEventArgs;
            {
                ShadowMapId = shadowMapId,
                Width = width,
                Height = height;
            });
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Managed kaynakları serbest bırak;
                    foreach (var shadowMap in _shadowMaps.Values)
                    {
                        shadowMap?.Dispose();
                    }
                    _shadowMaps.Clear();
                }

                // Unmanaged kaynakları serbest bırak;
                _diagnosticTool.Dispose();

                _disposed = true;
            }
        }

        ~ShadowSystem()
        {
            Dispose(false);
        }

        #endregion;

        #region Yardımcı Sınıflar ve Arayüzler;

        public interface ILightSource;
        {
            Vector3 Position { get; }
            Vector3 Direction { get; }
            bool CastShadows { get; }
            int ShadowMapId { get; set; }
            float ShadowIntensity { get; }
        }

        public interface IRenderable;
        {
            Matrix4x4 WorldMatrix { get; }
            bool CastsShadow { get; }
            BoundingBox Bounds { get; }
        }

        public class Camera;
        {
            public Vector3 Position { get; set; }
            public Vector3 Forward { get; set; }
            public Vector3 Up { get; set; }
            public float FieldOfView { get; set; } = 60.0f;
            public float AspectRatio { get; set; } = 16.0f / 9.0f;
            public float NearPlane { get; set; } = 0.1f;
            public float FarPlane { get; set; } = 1000.0f;
        }

        public struct BoundingBox;
        {
            public Vector3 Min;
            public Vector3 Max;
        }

        public static class MathHelper;
        {
            public static float Lerp(float a, float b, float t)
            {
                return a + (b - a) * t;
            }
        }

        #endregion;

        #region Özel Exception Sınıfları;

        public class ShadowSystemException : Exception
        {
            public ShadowSystemException(string message) : base(message) { }
            public ShadowSystemException(string message, Exception inner) : base(message, inner) { }
        }

        public class ShadowSystemInitializationException : ShadowSystemException;
        {
            public ShadowSystemInitializationException(string message) : base(message) { }
            public ShadowSystemInitializationException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }
}
