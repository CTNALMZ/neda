using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.RenderManager;
using NEDA.EngineIntegration.Unreal.Physics;
using NEDA.GameDesign.GameplayDesign.MechanicsDesign;
using NEDA.GameDesign.LevelDesign.WorldBuilding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.GameDesign.LevelDesign.NavigationMesh;
{
    /// <summary>
    /// Navigation Mesh (NavMesh) oluşturma ve yönetim sistemi;
    /// </summary>
    public class NavMeshBuilder : INavMeshBuilder, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IGeometryAnalyzer _geometryAnalyzer;
        private readonly IPhysicsEngine _physicsEngine;
        private readonly AppConfig _config;
        private readonly NavMeshSettings _settings;
        private readonly object _buildLock = new object();
        private readonly CancellationTokenSource _cancellationTokenSource;

        private NavMeshData _currentNavMesh;
        private bool _isBuilding;
        private bool _isDisposed;
        private BuildMetrics _lastBuildMetrics;
        private Dictionary<string, NavMeshRegion> _regions;
        private List<NavMeshAgentProfile> _agentProfiles;

        /// <summary>
        /// NavMeshBuilder constructor;
        /// </summary>
        public NavMeshBuilder(
            ILogger logger,
            IGeometryAnalyzer geometryAnalyzer,
            IPhysicsEngine physicsEngine,
            AppConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _geometryAnalyzer = geometryAnalyzer ?? throw new ArgumentNullException(nameof(geometryAnalyzer));
            _physicsEngine = physicsEngine ?? throw new ArgumentNullException(nameof(physicsEngine));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _settings = LoadSettings();
            _cancellationTokenSource = new CancellationTokenSource();
            _regions = new Dictionary<string, NavMeshRegion>();
            _agentProfiles = new List<NavMeshAgentProfile>();

            InitializeDefaultAgentProfiles();

            _logger.LogInformation("NavMeshBuilder initialized.");
        }

        /// <summary>
        /// Seviye geometrisinden NavMesh oluştur;
        /// </summary>
        public async Task<NavMeshData> BuildNavMeshAsync(
            WorldGeometry worldGeometry,
            BuildParameters parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (worldGeometry == null)
                throw new ArgumentNullException(nameof(worldGeometry));

            parameters ??= new BuildParameters();

            try
            {
                lock (_buildLock)
                {
                    if (_isBuilding)
                        throw new InvalidOperationException("NavMesh build already in progress.");
                    _isBuilding = true;
                }

                var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _cancellationTokenSource.Token).Token;

                _logger.LogInformation($"Starting NavMesh build for world: {worldGeometry.Name}");

                var buildResult = await BuildInternalAsync(worldGeometry, parameters, combinedToken);

                _currentNavMesh = buildResult.NavMeshData;
                _lastBuildMetrics = buildResult.Metrics;

                await PostProcessNavMeshAsync(buildResult.NavMeshData, parameters);
                await ValidateNavMeshAsync(buildResult.NavMeshData);

                _logger.LogInformation($"NavMesh build completed. Metrics: {buildResult.Metrics}");

                return buildResult.NavMeshData;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("NavMesh build was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build NavMesh.");
                throw;
            }
            finally
            {
                lock (_buildLock)
                {
                    _isBuilding = false;
                }
            }
        }

        /// <summary>
        /// Incremental (kısmi) NavMesh güncellemesi;
        /// </summary>
        public async Task<NavMeshData> UpdateNavMeshAsync(
            WorldGeometry changedGeometry,
            UpdateParameters parameters = null)
        {
            ValidateNotDisposed();

            if (changedGeometry == null)
                throw new ArgumentNullException(nameof(changedGeometry));

            if (_currentNavMesh == null)
                throw new InvalidOperationException("No NavMesh to update. Build a NavMesh first.");

            parameters ??= new UpdateParameters();

            try
            {
                _logger.LogInformation($"Starting incremental NavMesh update for area: {changedGeometry.Name}");

                // Değişen bölgeyi tanımla;
                var affectedRegion = await AnalyzeChangedRegionAsync(changedGeometry);

                // Eski NavMesh bölümünü kaldır;
                await RemoveRegionFromNavMeshAsync(affectedRegion);

                // Yeni geometriyi ekle;
                await AddGeometryToNavMeshAsync(changedGeometry, affectedRegion);

                // Yeniden hesaplama;
                await RecalculateConnectionsAsync(affectedRegion);

                // Optimizasyon;
                await OptimizeRegionAsync(affectedRegion);

                // Validasyon;
                await ValidateRegionAsync(affectedRegion);

                _logger.LogInformation($"Incremental NavMesh update completed for region: {affectedRegion.Id}");

                return _currentNavMesh;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update NavMesh incrementally.");
                throw;
            }
        }

        /// <summary>
        /// NavMesh'i belirli bir agent profili için optimize et;
        /// </summary>
        public async Task<NavMeshData> OptimizeForAgentAsync(
            string agentProfileId,
            OptimizationParameters parameters = null)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(agentProfileId))
                throw new ArgumentNullException(nameof(agentProfileId));

            if (_currentNavMesh == null)
                throw new InvalidOperationException("No NavMesh to optimize. Build a NavMesh first.");

            parameters ??= new OptimizationParameters();

            var agentProfile = GetAgentProfile(agentProfileId);
            if (agentProfile == null)
                throw new ArgumentException($"Agent profile not found: {agentProfileId}");

            try
            {
                _logger.LogInformation($"Optimizing NavMesh for agent: {agentProfile.Name}");

                // Agent boyutuna göre mesh'i yeniden boyutlandır;
                await ResizeForAgentAsync(agentProfile);

                // Agent yeteneklerine göre yüzeyleri işaretle;
                await MarkSurfacesForAgentAsync(agentProfile);

                // Agent hareket kısıtlamalarını uygula;
                await ApplyAgentConstraintsAsync(agentProfile);

                // Performans optimizasyonu;
                await SimplifyForAgentAsync(agentProfile, parameters);

                _logger.LogInformation($"NavMesh optimized for agent: {agentProfile.Name}");

                return _currentNavMesh;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to optimize NavMesh for agent: {agentProfileId}");
                throw;
            }
        }

        /// <summary>
        /// NavMesh'i bölgelere ayır;
        /// </summary>
        public async Task<IReadOnlyList<NavMeshRegion>> PartitionNavMeshAsync(
            PartitionParameters parameters = null)
        {
            ValidateNotDisposed();

            if (_currentNavMesh == null)
                throw new InvalidOperationException("No NavMesh to partition. Build a NavMesh first.");

            parameters ??= new PartitionParameters();

            try
            {
                _logger.LogInformation("Partitioning NavMesh into regions...");

                var regions = await PartitionInternalAsync(parameters);

                _regions.Clear();
                foreach (var region in regions)
                {
                    _regions[region.Id] = region;
                }

                await BuildRegionConnectionsAsync(regions);

                _logger.LogInformation($"NavMesh partitioned into {regions.Count} regions.");

                return regions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to partition NavMesh.");
                throw;
            }
        }

        /// <summary>
        /// NavMesh'i dosyaya kaydet;
        /// </summary>
        public async Task SaveNavMeshAsync(
            string filePath,
            SaveFormat format = SaveFormat.Binary)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));

            if (_currentNavMesh == null)
                throw new InvalidOperationException("No NavMesh to save. Build a NavMesh first.");

            try
            {
                using (var fileStream = System.IO.File.Create(filePath))
                {
                    switch (format)
                    {
                        case SaveFormat.Binary:
                            await SaveBinaryAsync(fileStream);
                            break;
                        case SaveFormat.JSON:
                            await SaveJsonAsync(fileStream);
                            break;
                        case SaveFormat.XML:
                            await SaveXmlAsync(fileStream);
                            break;
                        default:
                            throw new ArgumentException($"Unsupported format: {format}");
                    }
                }

                _logger.LogInformation($"NavMesh saved to: {filePath} (Format: {format})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save NavMesh to: {filePath}");
                throw;
            }
        }

        /// <summary>
        /// NavMesh'i dosyadan yükle;
        /// </summary>
        public async Task<NavMeshData> LoadNavMeshAsync(
            string filePath,
            SaveFormat format = SaveFormat.Binary)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));

            if (!System.IO.File.Exists(filePath))
                throw new System.IO.FileNotFoundException($"NavMesh file not found: {filePath}");

            try
            {
                using (var fileStream = System.IO.File.OpenRead(filePath))
                {
                    NavMeshData loadedData;

                    switch (format)
                    {
                        case SaveFormat.Binary:
                            loadedData = await LoadBinaryAsync(fileStream);
                            break;
                        case SaveFormat.JSON:
                            loadedData = await LoadJsonAsync(fileStream);
                            break;
                        case SaveFormat.XML:
                            loadedData = await LoadXmlAsync(fileStream);
                            break;
                        default:
                            throw new ArgumentException($"Unsupported format: {format}");
                    }

                    _currentNavMesh = loadedData;
                    await ValidateNavMeshAsync(loadedData);

                    _logger.LogInformation($"NavMesh loaded from: {filePath} (Format: {format})");

                    return loadedData;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load NavMesh from: {filePath}");
                throw;
            }
        }

        /// <summary>
        /// NavMesh'i serbest bırak;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();

            _currentNavMesh?.Dispose();
            _currentNavMesh = null;

            foreach (var region in _regions.Values)
            {
                region.Dispose();
            }
            _regions.Clear();

            _agentProfiles.Clear();

            _isDisposed = true;

            _logger.LogInformation("NavMeshBuilder disposed.");
        }

        #region Public Properties;

        /// <summary>
        /// Mevcut NavMesh verisi;
        /// </summary>
        public NavMeshData CurrentNavMesh => _currentNavMesh;

        /// <summary>
        /// NavMesh oluşturuluyor mu?
        /// </summary>
        public bool IsBuilding => _isBuilding;

        /// <summary>
        /// Son build metrikleri;
        /// </summary>
        public BuildMetrics LastBuildMetrics => _lastBuildMetrics;

        /// <summary>
        /// Kayıtlı bölgeler;
        /// </summary>
        public IReadOnlyDictionary<string, NavMeshRegion> Regions => _regions;

        /// <summary>
        /// Agent profilleri;
        /// </summary>
        public IReadOnlyList<NavMeshAgentProfile> AgentProfiles => _agentProfiles;

        #endregion;

        #region Private Methods;

        private NavMeshSettings LoadSettings()
        {
            var configSection = _config.GetSection<NavMeshConfiguration>("NavMesh");

            return new NavMeshSettings;
            {
                CellSize = configSection.CellSize,
                CellHeight = configSection.CellHeight,
                AgentHeight = configSection.AgentHeight,
                AgentRadius = configSection.AgentRadius,
                AgentMaxClimb = configSection.AgentMaxClimb,
                AgentMaxSlope = configSection.AgentMaxSlope,
                RegionMinSize = configSection.RegionMinSize,
                RegionMergeSize = configSection.RegionMergeSize,
                EdgeMaxLen = configSection.EdgeMaxLen,
                EdgeMaxError = configSection.EdgeMaxError,
                VertsPerPoly = configSection.VertsPerPoly,
                DetailSampleDist = configSection.DetailSampleDist,
                DetailSampleMaxError = configSection.DetailSampleMaxError;
            };
        }

        private void InitializeDefaultAgentProfiles()
        {
            // Humanoid agent;
            _agentProfiles.Add(new NavMeshAgentProfile;
            {
                Id = "humanoid_standard",
                Name = "Standard Humanoid",
                Radius = 0.3f,
                Height = 1.8f,
                StepHeight = 0.4f,
                MaxSlope = 45.0f,
                MaxClimb = 0.5f,
                WalkableSurfaceFlags = SurfaceFlags.Walkable | SurfaceFlags.Concrete | SurfaceFlags.Wood | SurfaceFlags.Metal;
            });

            // Small creature agent;
            _agentProfiles.Add(new NavMeshAgentProfile;
            {
                Id = "small_creature",
                Name = "Small Creature",
                Radius = 0.1f,
                Height = 0.3f,
                StepHeight = 0.1f,
                MaxSlope = 60.0f,
                MaxClimb = 0.2f,
                WalkableSurfaceFlags = SurfaceFlags.Walkable | SurfaceFlags.Grass | SurfaceFlags.Dirt;
            });

            // Vehicle agent;
            _agentProfiles.Add(new NavMeshAgentProfile;
            {
                Id = "vehicle_standard",
                Name = "Standard Vehicle",
                Radius = 1.0f,
                Height = 2.0f,
                StepHeight = 0.2f,
                MaxSlope = 30.0f,
                MaxClimb = 0.3f,
                WalkableSurfaceFlags = SurfaceFlags.Walkable | SurfaceFlags.Concrete | SurfaceFlags.Asphalt;
            });
        }

        private async Task<BuildResult> BuildInternalAsync(
            WorldGeometry worldGeometry,
            BuildParameters parameters,
            CancellationToken cancellationToken)
        {
            var metrics = new BuildMetrics;
            {
                StartTime = DateTime.UtcNow,
                WorldName = worldGeometry.Name,
                TriangleCount = worldGeometry.Triangles.Count;
            };

            try
            {
                // 1. Geometriyi rasterize et (voxel grid)
                _logger.LogDebug("Rasterizing geometry...");
                var voxelGrid = await RasterizeGeometryAsync(worldGeometry, cancellationToken);
                metrics.VoxelCount = voxelGrid.VoxelCount;

                // 2. Walkable surface'leri tanımla;
                _logger.LogDebug("Identifying walkable surfaces...");
                var walkableVoxels = await IdentifyWalkableSurfacesAsync(voxelGrid, parameters, cancellationToken);
                metrics.WalkableVoxelCount = walkableVoxels.Count;

                // 3. Region'ları oluştur;
                _logger.LogDebug("Building regions...");
                var regions = await BuildRegionsAsync(walkableVoxels, cancellationToken);
                metrics.RegionCount = regions.Count;

                // 4. Contour'ları çıkar;
                _logger.LogDebug("Extracting contours...");
                var contours = await ExtractContoursAsync(regions, cancellationToken);
                metrics.ContourCount = contours.Count;

                // 5. Polygon mesh oluştur;
                _logger.LogDebug("Building polygon mesh...");
                var polygonMesh = await BuildPolygonMeshAsync(contours, cancellationToken);
                metrics.PolygonCount = polygonMesh.Polygons.Count;

                // 6. Detail mesh ekle;
                _logger.LogDebug("Adding detail mesh...");
                var detailMesh = await BuildDetailMeshAsync(polygonMesh, cancellationToken);
                metrics.DetailMeshCount = detailMesh.DetailMeshes.Count;

                // 7. NavMesh verisini oluştur;
                var navMeshData = new NavMeshData;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = worldGeometry.Name,
                    Bounds = worldGeometry.Bounds,
                    VoxelGrid = voxelGrid,
                    PolygonMesh = polygonMesh,
                    DetailMesh = detailMesh,
                    Regions = regions,
                    BuildParameters = parameters,
                    BuildTimestamp = DateTime.UtcNow;
                };

                metrics.EndTime = DateTime.UtcNow;
                metrics.Duration = metrics.EndTime - metrics.StartTime;
                metrics.Success = true;

                return new BuildResult;
                {
                    NavMeshData = navMeshData,
                    Metrics = metrics;
                };
            }
            catch (Exception ex)
            {
                metrics.EndTime = DateTime.UtcNow;
                metrics.Duration = metrics.EndTime - metrics.StartTime;
                metrics.Success = false;
                metrics.Error = ex.Message;

                throw;
            }
        }

        private async Task<VoxelGrid> RasterizeGeometryAsync(
            WorldGeometry geometry,
            CancellationToken cancellationToken)
        {
            var bounds = geometry.Bounds;
            var cellSize = _settings.CellSize;
            var cellHeight = _settings.CellHeight;

            var width = (int)Math.Ceiling(bounds.Size.X / cellSize);
            var depth = (int)Math.Ceiling(bounds.Size.Z / cellSize);
            var height = (int)Math.Ceiling(bounds.Size.Y / cellHeight);

            var voxelGrid = new VoxelGrid(width, depth, height, cellSize, cellHeight, bounds.Min);

            await Task.Run(() =>
            {
                foreach (var triangle in geometry.Triangles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Triangle'ı voxel grid'e rasterize et;
                    voxelGrid.RasterizeTriangle(triangle);
                }
            }, cancellationToken);

            return voxelGrid;
        }

        private async Task<List<Voxel>> IdentifyWalkableSurfacesAsync(
            VoxelGrid voxelGrid,
            BuildParameters parameters,
            CancellationToken cancellationToken)
        {
            var walkableVoxels = new List<Voxel>();

            await Task.Run(() =>
            {
                for (int x = 0; x < voxelGrid.Width; x++)
                {
                    for (int z = 0; z < voxelGrid.Depth; z++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        var column = voxelGrid.GetColumn(x, z);

                        for (int y = 0; y < column.Count; y++)
                        {
                            var voxel = column[y];

                            // Walkable kontrolü;
                            if (IsWalkableVoxel(voxel, parameters))
                            {
                                walkableVoxels.Add(voxel);
                            }
                        }
                    }
                }
            }, cancellationToken);

            return walkableVoxels;
        }

        private bool IsWalkableVoxel(Voxel voxel, BuildParameters parameters)
        {
            // Eğim kontrolü;
            if (voxel.Slope > _settings.AgentMaxSlope)
                return false;

            // Yüzey tipi kontrolü;
            if (!parameters.AllowedSurfaceTypes.HasFlag(voxel.SurfaceType))
                return false;

            // Agent boyutu kontrolü;
            if (voxel.Height < _settings.AgentHeight)
                return false;

            // Diğer kriterler...
            return true;
        }

        private async Task<List<NavMeshRegion>> BuildRegionsAsync(
            List<Voxel> walkableVoxels,
            CancellationToken cancellationToken)
        {
            var regions = new List<NavMeshRegion>();
            var visited = new HashSet<Voxel>();

            await Task.Run(() =>
            {
                foreach (var voxel in walkableVoxels)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    if (visited.Contains(voxel))
                        continue;

                    // Region oluştur;
                    var region = FloodFillRegion(voxel, walkableVoxels, visited);

                    if (region.Voxels.Count >= _settings.RegionMinSize)
                    {
                        regions.Add(region);
                    }
                }
            }, cancellationToken);

            // Region'ları birleştir;
            return await MergeSmallRegionsAsync(regions, cancellationToken);
        }

        private NavMeshRegion FloodFillRegion(
            Voxel startVoxel,
            List<Voxel> walkableVoxels,
            HashSet<Voxel> visited)
        {
            var region = new NavMeshRegion(Guid.NewGuid().ToString());
            var queue = new Queue<Voxel>();
            var walkableSet = new HashSet<Voxel>(walkableVoxels);

            queue.Enqueue(startVoxel);
            visited.Add(startVoxel);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                region.AddVoxel(current);

                // Komşu voxel'leri kontrol et;
                foreach (var neighbor in current.GetNeighbors())
                {
                    if (walkableSet.Contains(neighbor) && !visited.Contains(neighbor))
                    {
                        queue.Enqueue(neighbor);
                        visited.Add(neighbor);
                    }
                }
            }

            return region;
        }

        private async Task<List<NavMeshRegion>> MergeSmallRegionsAsync(
            List<NavMeshRegion> regions,
            CancellationToken cancellationToken)
        {
            var mergedRegions = new List<NavMeshRegion>(regions);
            bool merged;

            do;
            {
                merged = false;

                for (int i = 0; i < mergedRegions.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return mergedRegions;

                    var region = mergedRegions[i];

                    if (region.Voxels.Count < _settings.RegionMergeSize)
                    {
                        // En yakın büyük region'ı bul;
                        var nearestRegion = FindNearestRegion(region, mergedRegions.Where(r => r != region).ToList());

                        if (nearestRegion != null)
                        {
                            nearestRegion.Merge(region);
                            mergedRegions.RemoveAt(i);
                            merged = true;
                            i--;
                        }
                    }
                }

            } while (merged);

            return mergedRegions;
        }

        private NavMeshRegion FindNearestRegion(NavMeshRegion source, List<NavMeshRegion> targets)
        {
            NavMeshRegion nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (var target in targets)
            {
                var distance = source.Center.DistanceTo(target.Center);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = target;
                }
            }

            return nearest;
        }

        private async Task PostProcessNavMeshAsync(NavMeshData navMeshData, BuildParameters parameters)
        {
            // NavMesh'i optimize et;
            await navMeshData.OptimizeAsync(parameters.OptimizationLevel);

            // Link'leri oluştur;
            await navMeshData.BuildLinksAsync();

            // Off-mesh connection'ları ekle;
            if (parameters.GenerateOffMeshConnections)
            {
                await navMeshData.GenerateOffMeshConnectionsAsync(_settings);
            }
        }

        private async Task ValidateNavMeshAsync(NavMeshData navMeshData)
        {
            var validationResult = await navMeshData.ValidateAsync();

            if (!validationResult.IsValid)
            {
                _logger.LogWarning($"NavMesh validation failed: {validationResult.ErrorCount} errors");

                foreach (var error in validationResult.Errors)
                {
                    _logger.LogWarning($"  - {error}");
                }
            }
            else;
            {
                _logger.LogInformation("NavMesh validation passed.");
            }
        }

        private async Task SaveBinaryAsync(System.IO.Stream stream)
        {
            await _currentNavMesh.SerializeToStreamAsync(stream, SerializationFormat.Binary);
        }

        private async Task SaveJsonAsync(System.IO.Stream stream)
        {
            await _currentNavMesh.SerializeToStreamAsync(stream, SerializationFormat.JSON);
        }

        private async Task SaveXmlAsync(System.IO.Stream stream)
        {
            await _currentNavMesh.SerializeToStreamAsync(stream, SerializationFormat.XML);
        }

        private async Task<NavMeshData> LoadBinaryAsync(System.IO.Stream stream)
        {
            return await NavMeshData.DeserializeFromStreamAsync(stream, SerializationFormat.Binary);
        }

        private async Task<NavMeshData> LoadJsonAsync(System.IO.Stream stream)
        {
            return await NavMeshData.DeserializeFromStreamAsync(stream, SerializationFormat.JSON);
        }

        private async Task<NavMeshData> LoadXmlAsync(System.IO.Stream stream)
        {
            return await NavMeshData.DeserializeFromStreamAsync(stream, SerializationFormat.XML);
        }

        private NavMeshAgentProfile GetAgentProfile(string profileId)
        {
            return _agentProfiles.FirstOrDefault(p => p.Id == profileId);
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(NavMeshBuilder));
        }

        #endregion;

        #region Helper Classes;

        private class BuildResult;
        {
            public NavMeshData NavMeshData { get; set; }
            public BuildMetrics Metrics { get; set; }
        }

        #endregion;
    }

    /// <summary>
    /// NavMesh oluşturma ayarları;
    /// </summary>
    public class NavMeshSettings;
    {
        public float CellSize { get; set; } = 0.2f;
        public float CellHeight { get; set; } = 0.1f;
        public float AgentHeight { get; set; } = 1.8f;
        public float AgentRadius { get; set; } = 0.3f;
        public float AgentMaxClimb { get; set; } = 0.5f;
        public float AgentMaxSlope { get; set; } = 45.0f;
        public int RegionMinSize { get; set; } = 8;
        public int RegionMergeSize { get; set; } = 20;
        public float EdgeMaxLen { get; set; } = 12.0f;
        public float EdgeMaxError { get; set; } = 1.3f;
        public int VertsPerPoly { get; set; } = 6;
        public float DetailSampleDist { get; set; } = 6.0f;
        public float DetailSampleMaxError { get; set; } = 1.0f;
    }

    /// <summary>
    /// NavMesh oluşturma parametreleri;
    /// </summary>
    public class BuildParameters;
    {
        public SurfaceFlags AllowedSurfaceTypes { get; set; } = SurfaceFlags.Walkable | SurfaceFlags.Concrete;
        public bool GenerateOffMeshConnections { get; set; } = true;
        public bool KeepInterResults { get; set; } = false;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.High;
        public float AgentRadiusOverride { get; set; } = 0.0f;
        public float AgentHeightOverride { get; set; } = 0.0f;
        public float AgentMaxClimbOverride { get; set; } = 0.0f;
        public float AgentMaxSlopeOverride { get; set; } = 0.0f;
    }

    /// <summary>
    /// NavMesh güncelleme parametreleri;
    /// </summary>
    public class UpdateParameters;
    {
        public UpdateMode Mode { get; set; } = UpdateMode.Incremental;
        public bool RebuildConnections { get; set; } = true;
        public bool ValidateAfterUpdate { get; set; } = true;
        public float UpdateRadius { get; set; } = 10.0f;
    }

    /// <summary>
    /// Optimizasyon parametreleri;
    /// </summary>
    public class OptimizationParameters;
    {
        public bool SimplifyGeometry { get; set; } = true;
        public bool MergeVertices { get; set; } = true;
        public bool RemoveDegenerateFaces { get; set; } = true;
        public float SimplificationThreshold { get; set; } = 0.1f;
        public int TargetPolygonCount { get; set; } = 0; // 0 = otomatik;
    }

    /// <summary>
    /// Partition (bölümleme) parametreleri;
    /// </summary>
    public class PartitionParameters;
    {
        public PartitionMethod Method { get; set; } = PartitionMethod.Watershed;
        public int TargetRegionCount { get; set; } = 0; // 0 = otomatik;
        public float RegionSize { get; set; } = 50.0f;
        public int MinRegionSize { get; set; } = 8;
        public int MaxRegionSize { get; set; } = 128;
    }

    /// <summary>
    /// Build metrikleri;
    /// </summary>
    public class BuildMetrics;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public string WorldName { get; set; }
        public int TriangleCount { get; set; }
        public int VoxelCount { get; set; }
        public int WalkableVoxelCount { get; set; }
        public int RegionCount { get; set; }
        public int ContourCount { get; set; }
        public int PolygonCount { get; set; }
        public int DetailMeshCount { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }

        public override string ToString()
        {
            return $"Duration: {Duration.TotalSeconds:F2}s, Triangles: {TriangleCount}, " +
                   $"Polygons: {PolygonCount}, Regions: {RegionCount}, Success: {Success}";
        }
    }

    /// <summary>
    /// Yüzey flag'leri;
    /// </summary>
    [Flags]
    public enum SurfaceFlags;
    {
        None = 0,
        Walkable = 1 << 0,
        Concrete = 1 << 1,
        Wood = 1 << 2,
        Metal = 1 << 3,
        Grass = 1 << 4,
        Dirt = 1 << 5,
        Sand = 1 << 6,
        Gravel = 1 << 7,
        Water = 1 << 8,
        Snow = 1 << 9,
        Ice = 1 << 10,
        Lava = 1 << 11,
        Carpet = 1 << 12,
        Asphalt = 1 << 13;
    }

    /// <summary>
    /// Optimizasyon seviyesi;
    /// </summary>
    public enum OptimizationLevel;
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Maximum = 4;
    }

    /// <summary>
    /// Güncelleme modu;
    /// </summary>
    public enum UpdateMode;
    {
        Full = 0,
        Incremental = 1,
        Partial = 2;
    }

    /// <summary>
    /// Partition metodu;
    /// </summary>
    public enum PartitionMethod;
    {
        Watershed = 0,
        Monotone = 1,
        Layers = 2,
        Custom = 3;
    }

    /// <summary>
    /// Kaydetme formatı;
    /// </summary>
    public enum SaveFormat;
    {
        Binary = 0,
        JSON = 1,
        XML = 2;
    }

    /// <summary>
    /// Serializasyon formatı;
    /// </summary>
    public enum SerializationFormat;
    {
        Binary = 0,
        JSON = 1,
        XML = 2;
    }

    /// <summary>
    /// NavMeshBuilder interface'i;
    /// </summary>
    public interface INavMeshBuilder : IDisposable
    {
        Task<NavMeshData> BuildNavMeshAsync(
            WorldGeometry worldGeometry,
            BuildParameters parameters = null,
            CancellationToken cancellationToken = default);

        Task<NavMeshData> UpdateNavMeshAsync(
            WorldGeometry changedGeometry,
            UpdateParameters parameters = null);

        Task<NavMeshData> OptimizeForAgentAsync(
            string agentProfileId,
            OptimizationParameters parameters = null);

        Task<IReadOnlyList<NavMeshRegion>> PartitionNavMeshAsync(
            PartitionParameters parameters = null);

        Task SaveNavMeshAsync(
            string filePath,
            SaveFormat format = SaveFormat.Binary);

        Task<NavMeshData> LoadNavMeshAsync(
            string filePath,
            SaveFormat format = SaveFormat.Binary);

        NavMeshData CurrentNavMesh { get; }
        bool IsBuilding { get; }
        BuildMetrics LastBuildMetrics { get; }
        IReadOnlyDictionary<string, NavMeshRegion> Regions { get; }
        IReadOnlyList<NavMeshAgentProfile> AgentProfiles { get; }
    }

    /// <summary>
    /// Dünya geometri verisi;
    /// </summary>
    public class WorldGeometry
    {
        public string Name { get; set; }
        public BoundingBox Bounds { get; set; }
        public List<MeshTriangle> Triangles { get; set; } = new List<MeshTriangle>();
        public List<WorldObject> Objects { get; set; } = new List<WorldObject>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// NavMesh veri yapısı;
    /// </summary>
    public class NavMeshData : IDisposable
    {
        // Implementasyon proje yapısına göre genişletilecek;
        // Detaylı implementasyon için ilgili sınıflar gereklidir;

        public string Id { get; set; }
        public string Name { get; set; }
        public BoundingBox Bounds { get; set; }
        public VoxelGrid VoxelGrid { get; set; }
        public PolygonMesh PolygonMesh { get; set; }
        public DetailMesh DetailMesh { get; set; }
        public List<NavMeshRegion> Regions { get; set; }
        public BuildParameters BuildParameters { get; set; }
        public DateTime BuildTimestamp { get; set; }

        public void Dispose()
        {
            // Kaynakları serbest bırak;
        }

        public async Task OptimizeAsync(OptimizationLevel level)
        {
            // Optimizasyon implementasyonu;
            await Task.CompletedTask;
        }

        public async Task BuildLinksAsync()
        {
            // Link oluşturma implementasyonu;
            await Task.CompletedTask;
        }

        public async Task GenerateOffMeshConnectionsAsync(NavMeshSettings settings)
        {
            // Off-mesh connection implementasyonu;
            await Task.CompletedTask;
        }

        public async Task<ValidationResult> ValidateAsync()
        {
            // Validasyon implementasyonu;
            return await Task.FromResult(new ValidationResult { IsValid = true });
        }

        public async Task SerializeToStreamAsync(System.IO.Stream stream, SerializationFormat format)
        {
            // Serializasyon implementasyonu;
            await Task.CompletedTask;
        }

        public static async Task<NavMeshData> DeserializeFromStreamAsync(System.IO.Stream stream, SerializationFormat format)
        {
            // Deserializasyon implementasyonu;
            return await Task.FromResult(new NavMeshData());
        }
    }
}
