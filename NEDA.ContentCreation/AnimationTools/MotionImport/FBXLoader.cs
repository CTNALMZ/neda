using NEDA.Animation.AnimationTools.BlendSpaces;
using NEDA.API.Middleware;
using NEDA.Common;
using NEDA.ContentCreation.AssetPipeline.FormatConverters;
using NEDA.ContentCreation.AssetPipeline.ImportManagers;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Mathematics;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.ContentCreation.AssetPipeline.FormatConverters;
{
    /// <summary>
    /// FBX dosya formatı yükleyicisi - 3D modeller, animasyonlar ve sahneler için;
    /// </summary>
    public class FBXLoader : IFBXLoader, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly IEventBus _eventBus;
        private readonly IImportEngine _importEngine;
        private readonly IAssetValidator _assetValidator;

        private readonly Dictionary<string, FBXScene> _loadedScenes;
        private readonly Dictionary<string, FBXMesh> _loadedMeshes;
        private readonly Dictionary<string, FBXAnimation> _loadedAnimations;
        private readonly Dictionary<string, FBXMaterial> _loadedMaterials;

        private FBXLoaderConfig _config;
        private bool _isInitialized;
        private bool _isLoading;
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// FBXLoader olayları;
        /// </summary>
        public event EventHandler<FBXLoadStartedEventArgs> LoadStarted;
        public event EventHandler<FBXLoadProgressEventArgs> LoadProgress;
        public event EventHandler<FBXLoadCompletedEventArgs> LoadCompleted;
        public event EventHandler<FBXLoadFailedEventArgs> LoadFailed;
        public event EventHandler<SceneImportedEventArgs> SceneImported;
        public event EventHandler<MeshImportedEventArgs> MeshImported;
        public event EventHandler<AnimationImportedEventArgs> AnimationImported;
        public event EventHandler<MaterialImportedEventArgs> MaterialImported;

        /// <summary>
        /// Yükleyici versiyonu;
        /// </summary>
        public Version LoaderVersion { get; } = new Version(1, 0, 0);

        /// <summary>
        /// Desteklenen FBX versiyonları;
        /// </summary>
        public IReadOnlyList<string> SupportedFBXVersions { get; }

        /// <summary>
        /// Yüklenmiş sahne sayısı;
        /// </summary>
        public int LoadedSceneCount => _loadedScenes.Count;

        /// <summary>
        /// Yüklenmiş mesh sayısı;
        /// </summary>
        public int LoadedMeshCount => _loadedMeshes.Count;

        /// <summary>
        /// Yüklenmiş animasyon sayısı;
        /// </summary>
        public int LoadedAnimationCount => _loadedAnimations.Count;

        /// <summary>
        /// Yüklenmiş materyal sayısı;
        /// </summary>
        public int LoadedMaterialCount => _loadedMaterials.Count;

        /// <summary>
        /// Yükleyici yapılandırması;
        /// </summary>
        public FBXLoaderConfig Config;
        {
            get => _config;
            set;
            {
                if (_config != value)
                {
                    _config = value ?? throw new ArgumentNullException(nameof(value));
                    OnConfigChanged(_config);
                }
            }
        }

        /// <summary>
        /// Yükleyici durumu;
        /// </summary>
        public LoaderState State { get; private set; }

        /// <summary>
        /// Son yükleme süresi;
        /// </summary>
        public TimeSpan LastLoadDuration { get; private set; }

        /// <summary>
        /// Toplam yüklenen byte sayısı;
        /// </summary>
        public long TotalBytesLoaded { get; private set; }

        /// <summary>
        /// FBXLoader sınıfı yapıcı metodu;
        /// </summary>
        public FBXLoader(
            ILogger logger,
            IErrorReporter errorReporter,
            IEventBus eventBus,
            IImportEngine importEngine,
            IAssetValidator assetValidator)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _importEngine = importEngine ?? throw new ArgumentNullException(nameof(importEngine));
            _assetValidator = assetValidator ?? throw new ArgumentNullException(nameof(assetValidator));

            _loadedScenes = new Dictionary<string, FBXScene>();
            _loadedMeshes = new Dictionary<string, FBXMesh>();
            _loadedAnimations = new Dictionary<string, FBXAnimation>();
            _loadedMaterials = new Dictionary<string, FBXMaterial>();

            _config = new FBXLoaderConfig();
            State = LoaderState.Idle;

            SupportedFBXVersions = new List<string>
            {
                "FBX 2020",
                "FBX 2019",
                "FBX 2018",
                "FBX 2017",
                "FBX 2016",
                "FBX 2015",
                "FBX 2014",
                "FBX 2013",
                "FBX 2012",
                "FBX 2011"
            };

            _logger.Info("FBXLoader initialized");
        }

        /// <summary>
        /// Yükleyiciyi başlat;
        /// </summary>
        public async Task InitializeAsync(FBXLoaderConfig config = null)
        {
            try
            {
                _logger.Info("Initializing FBXLoader...");

                if (config != null)
                {
                    Config = config;
                }

                // Alt bileşenleri başlat;
                await _importEngine.InitializeAsync();
                await _assetValidator.InitializeAsync();

                // FBX kütüphanelerini başlat;
                await InitializeFBXLibrariesAsync();

                // Cache'i temizle;
                await ClearCacheAsync();

                _isInitialized = true;
                State = LoaderState.Ready;

                _logger.Info("FBXLoader initialized successfully");

                // Olay yayınla;
                _eventBus.Publish(new FBXLoaderInitializedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    LoaderVersion = LoaderVersion.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize FBXLoader: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.InitializeAsync");
                throw new FBXLoaderInitializationException(
                    "Failed to initialize FBXLoader", ex);
            }
        }

        /// <summary>
        /// FBX dosyası yükle;
        /// </summary>
        public async Task<FBXScene> LoadFBXAsync(string filePath, LoadOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Loading FBX file: {filePath}");

                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"FBX file not found: {filePath}");
                }

                // Dosya uzantısını kontrol et;
                if (!Path.GetExtension(filePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidFileFormatException($"File is not an FBX file: {filePath}");
                }

                // Yükleme durumunu güncelle;
                State = LoaderState.Loading;
                _isLoading = true;
                _cancellationTokenSource = new CancellationTokenSource();

                var loadStartTime = DateTime.UtcNow;

                // Olay yayınla;
                LoadStarted?.Invoke(this, new FBXLoadStartedEventArgs;
                {
                    FilePath = filePath,
                    FileSize = new FileInfo(filePath).Length,
                    Timestamp = loadStartTime;
                });

                // Dosyayı doğrula;
                await ValidateFBXFileAsync(filePath);

                // FBX dosyasını yükle;
                var scene = await LoadFBXFileAsync(filePath, options, _cancellationTokenSource.Token);

                // Sahneyi işle;
                await ProcessLoadedSceneAsync(scene, options);

                // Önbelleğe ekle;
                CacheScene(scene);

                var loadEndTime = DateTime.UtcNow;
                LastLoadDuration = loadEndTime - loadStartTime;
                TotalBytesLoaded += new FileInfo(filePath).Length;

                // Durumu güncelle;
                State = LoaderState.Ready;
                _isLoading = false;

                _logger.Info($"FBX file loaded successfully: {filePath} ({LastLoadDuration.TotalMilliseconds}ms)");

                // Olay yayınla;
                LoadCompleted?.Invoke(this, new FBXLoadCompletedEventArgs;
                {
                    FilePath = filePath,
                    Scene = scene,
                    LoadDuration = LastLoadDuration,
                    Timestamp = loadEndTime;
                });

                SceneImported?.Invoke(this, new SceneImportedEventArgs;
                {
                    Scene = scene,
                    SourcePath = filePath,
                    Timestamp = loadEndTime;
                });

                _eventBus.Publish(new FBXFileLoadedEvent;
                {
                    FilePath = filePath,
                    SceneId = scene.Id,
                    LoadDuration = LastLoadDuration,
                    Timestamp = loadEndTime;
                });

                return scene;
            }
            catch (OperationCanceledException)
            {
                _logger.Warning($"FBX load cancelled: {filePath}");
                State = LoaderState.Ready;
                _isLoading = false;
                throw new LoadCancelledException($"Load cancelled for file: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load FBX file '{filePath}': {ex.Message}", ex);

                State = LoaderState.Error;
                _isLoading = false;

                LoadFailed?.Invoke(this, new FBXLoadFailedEventArgs;
                {
                    FilePath = filePath,
                    Error = ex,
                    Timestamp = DateTime.UtcNow;
                });

                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.LoadFBXAsync");
                throw new FBXLoadException($"Failed to load FBX file: {filePath}", ex);
            }
        }

        /// <summary>
        /// Çoklu FBX dosyalarını yükle;
        /// </summary>
        public async Task<List<FBXScene>> LoadMultipleFBXAsync(List<string> filePaths, LoadOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Loading {filePaths.Count} FBX files");

                var results = new List<FBXScene>();
                var failedFiles = new List<string>();

                // Paralel yükleme için config;
                var maxDegreeOfParallelism = Config.MaxParallelLoads;
                var parallelOptions = new ParallelOptions;
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    CancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
                };

                await Parallel.ForEachAsync(filePaths, parallelOptions, async (filePath, cancellationToken) =>
                {
                    try
                    {
                        var scene = await LoadFBXAsync(filePath, options);
                        results.Add(scene);

                        _logger.Debug($"Successfully loaded: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to load FBX file '{filePath}': {ex.Message}");
                        failedFiles.Add(filePath);

                        // Hata raporla;
                        await _errorReporter.ReportErrorAsync(ex, "FBXLoader.LoadMultipleFBXAsync");
                    }
                });

                if (failedFiles.Count > 0)
                {
                    _logger.Warning($"{failedFiles.Count} files failed to load");
                }

                _logger.Info($"Loaded {results.Count} FBX files successfully");

                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load multiple FBX files: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.LoadMultipleFBXAsync");
                throw;
            }
        }

        /// <summary>
        /// FBX sahnesinden mesh çıkar;
        /// </summary>
        public async Task<List<FBXMesh>> ExtractMeshesFromSceneAsync(FBXScene scene, MeshExtractionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Extracting meshes from scene: {scene.Name}");

                var meshes = new List<FBXMesh>();
                options ??= new MeshExtractionOptions();

                // Sahnedeki tüm mesh'leri çıkar;
                foreach (var meshNode in scene.MeshNodes)
                {
                    var mesh = await ExtractMeshAsync(meshNode, options);
                    if (mesh != null)
                    {
                        meshes.Add(mesh);

                        // Cache'e ekle;
                        if (!_loadedMeshes.ContainsKey(mesh.Id))
                        {
                            _loadedMeshes[mesh.Id] = mesh;
                        }

                        // Olay yayınla;
                        MeshImported?.Invoke(this, new MeshImportedEventArgs;
                        {
                            Mesh = mesh,
                            SourceSceneId = scene.Id,
                            Timestamp = DateTime.UtcNow;
                        });

                        _logger.Debug($"Extracted mesh: {mesh.Name} ({mesh.VertexCount} vertices)");
                    }
                }

                _logger.Info($"Extracted {meshes.Count} meshes from scene: {scene.Name}");

                return meshes;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to extract meshes from scene: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.ExtractMeshesFromSceneAsync");
                throw;
            }
        }

        /// <summary>
        /// FBX sahnesinden animasyonları çıkar;
        /// </summary>
        public async Task<List<FBXAnimation>> ExtractAnimationsFromSceneAsync(FBXScene scene, AnimationExtractionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Extracting animations from scene: {scene.Name}");

                var animations = new List<FBXAnimation>();
                options ??= new AnimationExtractionOptions();

                // Sahnedeki tüm animasyonları çıkar;
                foreach (var animationStack in scene.AnimationStacks)
                {
                    var animation = await ExtractAnimationAsync(animationStack, options);
                    if (animation != null)
                    {
                        animations.Add(animation);

                        // Cache'e ekle;
                        if (!_loadedAnimations.ContainsKey(animation.Id))
                        {
                            _loadedAnimations[animation.Id] = animation;
                        }

                        // Olay yayınla;
                        AnimationImported?.Invoke(this, new AnimationImportedEventArgs;
                        {
                            Animation = animation,
                            SourceSceneId = scene.Id,
                            Timestamp = DateTime.UtcNow;
                        });

                        _logger.Debug($"Extracted animation: {animation.Name} ({animation.FrameCount} frames)");
                    }
                }

                _logger.Info($"Extracted {animations.Count} animations from scene: {scene.Name}");

                return animations;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to extract animations from scene: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.ExtractAnimationsFromSceneAsync");
                throw;
            }
        }

        /// <summary>
        /// FBX sahnesinden materyalleri çıkar;
        /// </summary>
        public async Task<List<FBXMaterial>> ExtractMaterialsFromSceneAsync(FBXScene scene, MaterialExtractionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Extracting materials from scene: {scene.Name}");

                var materials = new List<FBXMaterial>();
                options ??= new MaterialExtractionOptions();

                // Sahnedeki tüm materyalleri çıkar;
                foreach (var materialNode in scene.MaterialNodes)
                {
                    var material = await ExtractMaterialAsync(materialNode, options);
                    if (material != null)
                    {
                        materials.Add(material);

                        // Cache'e ekle;
                        if (!_loadedMaterials.ContainsKey(material.Id))
                        {
                            _loadedMaterials[material.Id] = material;
                        }

                        // Olay yayınla;
                        MaterialImported?.Invoke(this, new MaterialImportedEventArgs;
                        {
                            Material = material,
                            SourceSceneId = scene.Id,
                            Timestamp = DateTime.UtcNow;
                        });

                        _logger.Debug($"Extracted material: {material.Name}");
                    }
                }

                _logger.Info($"Extracted {materials.Count} materials from scene: {scene.Name}");

                return materials;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to extract materials from scene: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.ExtractMaterialsFromSceneAsync");
                throw;
            }
        }

        /// <summary>
        /// FBX dosyasını dışa aktar;
        /// </summary>
        public async Task ExportFBXAsync(FBXScene scene, string filePath, ExportOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Exporting FBX file: {filePath}");

                // Export seçeneklerini ayarla;
                options ??= new ExportOptions;
                {
                    FBXVersion = Config.DefaultExportVersion,
                    ExportMeshes = true,
                    ExportAnimations = true,
                    ExportMaterials = true,
                    ExportTextures = Config.ExportEmbeddedTextures,
                    BinaryFormat = Config.UseBinaryFormat;
                };

                // Sahneyi FBX formatına dönüştür;
                var fbxData = await ConvertSceneToFBXAsync(scene, options);

                // Dosyaya yaz;
                await WriteFBXFileAsync(filePath, fbxData, options);

                _logger.Info($"FBX file exported successfully: {filePath}");

                // Olay yayınla;
                _eventBus.Publish(new FBXFileExportedEvent;
                {
                    FilePath = filePath,
                    SceneId = scene.Id,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to export FBX file '{filePath}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.ExportFBXAsync");
                throw new FBXExportException($"Failed to export FBX file: {filePath}", ex);
            }
        }

        /// <summary>
        /// Mesh'i FBX formatına dönüştür;
        /// </summary>
        public async Task<byte[]> ConvertMeshToFBXAsync(FBXMesh mesh, ExportOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Converting mesh to FBX: {mesh.Name}");

                // Yeni sahne oluştur;
                var scene = new FBXScene;
                {
                    Name = mesh.Name,
                    RootNode = new FBXNode { Name = "Root" }
                };

                // Mesh'i sahneye ekle;
                var meshNode = new FBXMeshNode;
                {
                    Name = mesh.Name,
                    Mesh = mesh,
                    Transform = Matrix4x4.Identity;
                };

                scene.MeshNodes.Add(meshNode);

                // FBX'ye dönüştür;
                var fbxData = await ConvertSceneToFBXAsync(scene, options);

                _logger.Info($"Mesh converted to FBX successfully: {mesh.Name}");

                return fbxData;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to convert mesh to FBX: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.ConvertMeshToFBXAsync");
                throw;
            }
        }

        /// <summary>
        /// Animasyonu FBX formatına dönüştür;
        /// </summary>
        public async Task<byte[]> ConvertAnimationToFBXAsync(FBXAnimation animation, FBXScene targetScene, ExportOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Converting animation to FBX: {animation.Name}");

                // Animasyonu sahneye ekle;
                targetScene.AnimationStacks.Add(new FBXAnimationStack;
                {
                    Name = animation.Name,
                    Animation = animation;
                });

                // FBX'ye dönüştür;
                var fbxData = await ConvertSceneToFBXAsync(targetScene, options);

                _logger.Info($"Animation converted to FBX successfully: {animation.Name}");

                return fbxData;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to convert animation to FBX: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.ConvertAnimationToFBXAsync");
                throw;
            }
        }

        /// <summary>
        /// FBX dosyasını doğrula;
        /// </summary>
        public async Task<ValidationResult> ValidateFBXFileAsync(string filePath)
        {
            try
            {
                _logger.Debug($"Validating FBX file: {filePath}");

                if (!File.Exists(filePath))
                {
                    return new ValidationResult;
                    {
                        IsValid = false,
                        Errors = new List<string> { "File does not exist" }
                    };
                }

                // Dosya boyutunu kontrol et;
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > Config.MaxFileSize)
                {
                    return new ValidationResult;
                    {
                        IsValid = false,
                        Errors = new List<string> { $"File size exceeds maximum limit ({Config.MaxFileSize} bytes)" }
                    };
                }

                // FBX başlığını kontrol et;
                var headerValid = await CheckFBXHeaderAsync(filePath);
                if (!headerValid)
                {
                    return new ValidationResult;
                    {
                        IsValid = false,
                        Errors = new List<string> { "Invalid FBX file header" }
                    };
                }

                // Asset validator ile doğrula;
                var validationResult = await _assetValidator.ValidateAssetAsync(filePath, AssetType.FBX);

                _logger.Debug($"FBX file validation completed: {validationResult.IsValid}");

                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to validate FBX file: {ex.Message}", ex);
                return new ValidationResult;
                {
                    IsValid = false,
                    Errors = new List<string> { $"Validation error: {ex.Message}" }
                };
            }
        }

        /// <summary>
        /// FBX dosyasının meta verilerini al;
        /// </summary>
        public async Task<FBXMetadata> GetFBXMetadataAsync(string filePath)
        {
            try
            {
                _logger.Debug($"Getting FBX metadata: {filePath}");

                var metadata = new FBXMetadata;
                {
                    FilePath = filePath,
                    FileSize = new FileInfo(filePath).Length,
                    LastModified = File.GetLastWriteTimeUtc(filePath)
                };

                // FBX başlığını oku;
                using var fileStream = File.OpenRead(filePath);
                using var reader = new BinaryReader(fileStream);

                // FBX versiyonunu oku;
                var version = await ReadFBXVersionAsync(reader);
                metadata.FBXVersion = version;

                // Sahne bilgilerini tahmin et;
                metadata.EstimatedMeshCount = await EstimateMeshCountAsync(reader);
                metadata.EstimatedAnimationCount = await EstimateAnimationCountAsync(reader);
                metadata.EstimatedMaterialCount = await EstimateMaterialCountAsync(reader);

                _logger.Debug($"FBX metadata retrieved: {metadata.FBXVersion}");

                return metadata;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get FBX metadata: {ex.Message}", ex);
                throw new FBXMetadataException($"Failed to get metadata for FBX file: {filePath}", ex);
            }
        }

        /// <summary>
        /// Yüklemeyi iptal et;
        /// </summary>
        public void CancelLoad()
        {
            if (_isLoading && _cancellationTokenSource != null)
            {
                _logger.Info("Cancelling FBX load...");
                _cancellationTokenSource.Cancel();
                State = LoaderState.Cancelled;
            }
        }

        /// <summary>
        /// Cache'i temizle;
        /// </summary>
        public async Task ClearCacheAsync()
        {
            try
            {
                _logger.Info("Clearing FBXLoader cache...");

                // Kaynakları serbest bırak;
                foreach (var scene in _loadedScenes.Values)
                {
                    if (scene is IAsyncDisposable disposableScene)
                    {
                        await disposableScene.DisposeAsync();
                    }
                }

                foreach (var mesh in _loadedMeshes.Values)
                {
                    if (mesh is IAsyncDisposable disposableMesh)
                    {
                        await disposableMesh.DisposeAsync();
                    }
                }

                foreach (var animation in _loadedAnimations.Values)
                {
                    if (animation is IAsyncDisposable disposableAnimation)
                    {
                        await disposableAnimation.DisposeAsync();
                    }
                }

                foreach (var material in _loadedMaterials.Values)
                {
                    if (material is IAsyncDisposable disposableMaterial)
                    {
                        await disposableMaterial.DisposeAsync();
                    }
                }

                // Cache'i temizle;
                _loadedScenes.Clear();
                _loadedMeshes.Clear();
                _loadedAnimations.Clear();
                _loadedMaterials.Clear();

                TotalBytesLoaded = 0;

                _logger.Info("FBXLoader cache cleared successfully");

                // Olay yayınla;
                _eventBus.Publish(new FBXCacheClearedEvent;
                {
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to clear cache: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.ClearCacheAsync");
                throw;
            }
        }

        /// <summary>
        /// Bellek optimizasyonu yap;
        /// </summary>
        public async Task OptimizeMemoryAsync(MemoryOptimizationOptions options = null)
        {
            try
            {
                _logger.Info("Optimizing FBXLoader memory...");

                options ??= new MemoryOptimizationOptions();

                // Eski cache girdilerini temizle;
                var cutoffTime = DateTime.UtcNow - TimeSpan.FromHours(options.CacheRetentionHours);
                var scenesToRemove = _loadedScenes.Values;
                    .Where(s => s.LastAccessTime < cutoffTime)
                    .ToList();

                foreach (var scene in scenesToRemove)
                {
                    await RemoveSceneFromCacheAsync(scene.Id);
                }

                // Bellek kullanımını optimize et;
                if (options.CompressMeshes)
                {
                    await CompressMeshDataAsync();
                }

                if (options.OptimizeAnimations)
                {
                    await OptimizeAnimationDataAsync();
                }

                // Cache boyutunu kontrol et;
                await EnforceCacheLimitsAsync();

                _logger.Info($"Memory optimized. Removed {scenesToRemove.Count} scenes from cache");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to optimize memory: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.OptimizeMemoryAsync");
                throw;
            }
        }

        /// <summary>
        /// Yükleyici istatistiklerini al;
        /// </summary>
        public FBXLoaderStats GetStats()
        {
            return new FBXLoaderStats;
            {
                LoadedSceneCount = LoadedSceneCount,
                LoadedMeshCount = LoadedMeshCount,
                LoadedAnimationCount = LoadedAnimationCount,
                LoadedMaterialCount = LoadedMaterialCount,
                TotalBytesLoaded = TotalBytesLoaded,
                LastLoadDuration = LastLoadDuration,
                State = State,
                CacheMemoryUsage = CalculateCacheMemoryUsage(),
                IsInitialized = _isInitialized;
            };
        }

        /// <summary>
        /// Desteklenen özellikleri kontrol et;
        /// </summary>
        public bool IsFeatureSupported(FBXFeature feature)
        {
            return feature switch;
            {
                FBXFeature.Animation => Config.ImportAnimations,
                FBXFeature.Materials => Config.ImportMaterials,
                FBXFeature.Textures => Config.ImportTextures,
                FBXFeature.Skinning => Config.ImportSkinning,
                FBXFeature.BlendShapes => Config.ImportBlendShapes,
                FBXFeature.Lights => Config.ImportLights,
                FBXFeature.Cameras => Config.ImportCameras,
                _ => false;
            };
        }

        /// <summary>
        /// Kaynakları serbest bırak;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                _logger.Info("Disposing FBXLoader...");

                // Yüklemeyi iptal et;
                CancelLoad();

                // Cache'i temizle;
                await ClearCacheAsync();

                // FBX kütüphanelerini kapat;
                await ShutdownFBXLibrariesAsync();

                _isInitialized = false;
                State = LoaderState.Disposed;

                _logger.Info("FBXLoader disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error disposing FBXLoader: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "FBXLoader.DisposeAsync");
            }
        }

        #region Private Methods;

        private async Task InitializeFBXLibrariesAsync()
        {
            try
            {
                _logger.Info("Initializing FBX libraries...");

                // FBX SDK veya diğer kütüphaneleri başlat;
                // Burada FBX SDK, Assimp gibi kütüphaneler başlatılır;

                await Task.Delay(100); // Simüle edilmiş başlatma;

                _logger.Info("FBX libraries initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize FBX libraries: {ex.Message}", ex);
                throw new FBXLibraryException("Failed to initialize FBX libraries", ex);
            }
        }

        private async Task<FBXScene> LoadFBXFileAsync(string filePath, LoadOptions options, CancellationToken cancellationToken)
        {
            var progress = new Progress<LoadProgress>(p =>
            {
                LoadProgress?.Invoke(this, new FBXLoadProgressEventArgs;
                {
                    FilePath = filePath,
                    Progress = p.Percentage,
                    CurrentStep = p.Step,
                    Timestamp = DateTime.UtcNow;
                });
            });

            // FBX dosyasını yükle;
            var scene = await Task.Run(() => LoadFBXFileInternal(filePath, options, progress, cancellationToken), cancellationToken);

            return scene;
        }

        private FBXScene LoadFBXFileInternal(string filePath, LoadOptions options, IProgress<LoadProgress> progress, CancellationToken cancellationToken)
        {
            // Burada gerçek FBX yükleme işlemi yapılır;
            // FBX SDK veya Assimp kullanılarak implemente edilir;

            progress.Report(new LoadProgress { Step = "Reading file", Percentage = 10 });
            cancellationToken.ThrowIfCancellationRequested();

            // Simüle edilmiş yükleme;
            var scene = new FBXScene;
            {
                Id = Guid.NewGuid().ToString(),
                Name = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                ImportTime = DateTime.UtcNow,
                LastAccessTime = DateTime.UtcNow;
            };

            progress.Report(new LoadProgress { Step = "Parsing nodes", Percentage = 30 });
            cancellationToken.ThrowIfCancellationRequested();

            // Node'ları oluştur;
            scene.RootNode = new FBXNode { Name = "Root" };

            progress.Report(new LoadProgress { Step = "Loading geometry", Percentage = 50 });
            cancellationToken.ThrowIfCancellationRequested();

            // Mesh'leri yükle;
            if (options?.ImportMeshes ?? Config.ImportMeshes)
            {
                // Mesh yükleme işlemi;
            }

            progress.Report(new LoadProgress { Step = "Loading materials", Percentage = 70 });
            cancellationToken.ThrowIfCancellationRequested();

            // Materyalleri yükle;
            if (options?.ImportMaterials ?? Config.ImportMaterials)
            {
                // Material yükleme işlemi;
            }

            progress.Report(new LoadProgress { Step = "Loading animations", Percentage = 90 });
            cancellationToken.ThrowIfCancellationRequested();

            // Animasyonları yükle;
            if (options?.ImportAnimations ?? Config.ImportAnimations)
            {
                // Animation yükleme işlemi;
            }

            progress.Report(new LoadProgress { Step = "Finalizing", Percentage = 100 });

            return scene;
        }

        private async Task ProcessLoadedSceneAsync(FBXScene scene, LoadOptions options)
        {
            // Sahneyi optimize et;
            if (options?.OptimizeScene ?? Config.OptimizeOnImport)
            {
                await OptimizeSceneAsync(scene, options);
            }

            // Dönüşümleri uygula;
            if (options?.ApplyTransformations ?? Config.ApplyTransformations)
            {
                ApplyTransformations(scene);
            }

            // Texture yollarını düzelt;
            if (options?.FixTexturePaths ?? Config.FixTexturePaths)
            {
                await FixTexturePathsAsync(scene);
            }

            // Sahneyi doğrula;
            await ValidateSceneAsync(scene);
        }

        private void CacheScene(FBXScene scene)
        {
            if (!_loadedScenes.ContainsKey(scene.Id))
            {
                _loadedScenes[scene.Id] = scene;

                // Cache limitini kontrol et;
                if (_loadedScenes.Count > Config.MaxCachedScenes)
                {
                    RemoveOldestSceneFromCache();
                }
            }
        }

        private async Task RemoveSceneFromCacheAsync(string sceneId)
        {
            if (_loadedScenes.TryGetValue(sceneId, out var scene))
            {
                if (scene is IAsyncDisposable disposableScene)
                {
                    await disposableScene.DisposeAsync();
                }

                _loadedScenes.Remove(sceneId);
            }
        }

        private void RemoveOldestSceneFromCache()
        {
            var oldestScene = _loadedScenes.Values;
                .OrderBy(s => s.LastAccessTime)
                .FirstOrDefault();

            if (oldestScene != null)
            {
                _loadedScenes.Remove(oldestScene.Id);
            }
        }

        private async Task<FBXMesh> ExtractMeshAsync(FBXMeshNode meshNode, MeshExtractionOptions options)
        {
            // Mesh extraction işlemi;
            await Task.CompletedTask;

            var mesh = new FBXMesh;
            {
                Id = Guid.NewGuid().ToString(),
                Name = meshNode.Name,
                VertexCount = 0, // Gerçek değerler yüklemeden gelir;
                TriangleCount = 0,
                ImportTime = DateTime.UtcNow;
            };

            return mesh;
        }

        private async Task<FBXAnimation> ExtractAnimationAsync(FBXAnimationStack animationStack, AnimationExtractionOptions options)
        {
            // Animation extraction işlemi;
            await Task.CompletedTask;

            var animation = new FBXAnimation;
            {
                Id = Guid.NewGuid().ToString(),
                Name = animationStack.Name,
                FrameCount = 0,
                Duration = 0,
                FrameRate = 30,
                ImportTime = DateTime.UtcNow;
            };

            return animation;
        }

        private async Task<FBXMaterial> ExtractMaterialAsync(FBXMaterialNode materialNode, MaterialExtractionOptions options)
        {
            // Material extraction işlemi;
            await Task.CompletedTask;

            var material = new FBXMaterial;
            {
                Id = Guid.NewGuid().ToString(),
                Name = materialNode.Name,
                ImportTime = DateTime.UtcNow;
            };

            return material;
        }

        private async Task<byte[]> ConvertSceneToFBXAsync(FBXScene scene, ExportOptions options)
        {
            // Sahneyi FBX binary formatına dönüştür;
            await Task.CompletedTask;

            // Simüle edilmiş FBX data;
            return new byte[1024];
        }

        private async Task WriteFBXFileAsync(string filePath, byte[] fbxData, ExportOptions options)
        {
            // FBX dosyasını disk'e yaz;
            await File.WriteAllBytesAsync(filePath, fbxData);
        }

        private async Task<bool> CheckFBXHeaderAsync(string filePath)
        {
            using var fileStream = File.OpenRead(filePath);
            using var reader = new BinaryReader(fileStream);

            // FBX binary header kontrolü;
            var header = reader.ReadBytes(23);

            // "Kaydara FBX Binary  " header'ını kontrol et;
            var expectedHeader = new byte[] { 0x4B, 0x61, 0x79, 0x64, 0x61, 0x72, 0x61, 0x20, 0x46, 0x42, 0x58, 0x20, 0x42, 0x69, 0x6E, 0x61, 0x72, 0x79, 0x20, 0x20, 0x00, 0x1A, 0x00 };

            return header.SequenceEqual(expectedHeader);
        }

        private async Task<string> ReadFBXVersionAsync(BinaryReader reader)
        {
            // FBX versiyonunu oku;
            reader.BaseStream.Seek(23, SeekOrigin.Begin);
            var version = reader.ReadUInt32();

            return $"FBX {version / 1000}";
        }

        private async Task<int> EstimateMeshCountAsync(BinaryReader reader)
        {
            // Tahmini mesh sayısı;
            return 1;
        }

        private async Task<int> EstimateAnimationCountAsync(BinaryReader reader)
        {
            // Tahmini animasyon sayısı;
            return 0;
        }

        private async Task<int> EstimateMaterialCountAsync(BinaryReader reader)
        {
            // Tahmini materyal sayısı;
            return 1;
        }

        private async Task OptimizeSceneAsync(FBXScene scene, LoadOptions options)
        {
            // Sahne optimizasyon işlemleri;
            await Task.CompletedTask;
        }

        private void ApplyTransformations(FBXScene scene)
        {
            // Dönüşüm uygulama işlemleri;
        }

        private async Task FixTexturePathsAsync(FBXScene scene)
        {
            // Texture path düzeltme işlemleri;
            await Task.CompletedTask;
        }

        private async Task ValidateSceneAsync(FBXScene scene)
        {
            // Sahne doğrulama işlemleri;
            await Task.CompletedTask;
        }

        private async Task CompressMeshDataAsync()
        {
            // Mesh verilerini sıkıştır;
            await Task.CompletedTask;
        }

        private async Task OptimizeAnimationDataAsync()
        {
            // Animasyon verilerini optimize et;
            await Task.CompletedTask;
        }

        private async Task EnforceCacheLimitsAsync()
        {
            // Cache limitlerini uygula;
            await Task.CompletedTask;
        }

        private long CalculateCacheMemoryUsage()
        {
            // Cache bellek kullanımını hesapla;
            return _loadedScenes.Count * 1024 * 1024; // Simüle edilmiş;
        }

        private async Task ShutdownFBXLibrariesAsync()
        {
            // FBX kütüphanelerini kapat;
            await Task.CompletedTask;
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new FBXLoaderNotInitializedException(
                    "FBXLoader must be initialized before use");
            }
        }

        private void OnConfigChanged(FBXLoaderConfig newConfig)
        {
            _eventBus.Publish(new FBXLoaderConfigChangedEvent;
            {
                Config = newConfig,
                Timestamp = DateTime.UtcNow;
            });
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            DisposeAsync().AsTask().Wait();
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Data Classes;

    /// <summary>
    /// FBX sahnesi;
    /// </summary>
    public class FBXScene : IAsyncDisposable;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FilePath { get; set; }
        public FBXNode RootNode { get; set; }
        public List<FBXMeshNode> MeshNodes { get; set; }
        public List<FBXMaterialNode> MaterialNodes { get; set; }
        public List<FBXAnimationStack> AnimationStacks { get; set; }
        public List<FBXLightNode> LightNodes { get; set; }
        public List<FBXCameraNode> CameraNodes { get; set; }
        public BoundingBox Bounds { get; set; }
        public DateTime ImportTime { get; set; }
        public DateTime LastAccessTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public FBXScene()
        {
            MeshNodes = new List<FBXMeshNode>();
            MaterialNodes = new List<FBXMaterialNode>();
            AnimationStacks = new List<FBXAnimationStack>();
            LightNodes = new List<FBXLightNode>();
            CameraNodes = new List<FBXCameraNode>();
            Metadata = new Dictionary<string, object>();
        }

        public async ValueTask DisposeAsync()
        {
            // Kaynakları serbest bırak;
            foreach (var meshNode in MeshNodes)
            {
                if (meshNode.Mesh is IAsyncDisposable disposableMesh)
                {
                    await disposableMesh.DisposeAsync();
                }
            }

            foreach (var materialNode in MaterialNodes)
            {
                if (materialNode.Material is IAsyncDisposable disposableMaterial)
                {
                    await disposableMaterial.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// FBX mesh;
    /// </summary>
    public class FBXMesh : IAsyncDisposable;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public List<Vector3> Vertices { get; set; }
        public List<Vector3> Normals { get; set; }
        public List<Vector2> UVs { get; set; }
        public List<Vector4> Tangents { get; set; }
        public List<Color> Colors { get; set; }
        public List<int> Indices { get; set; }
        public List<FBXBlendShape> BlendShapes { get; set; }
        public FBXSkinningData SkinningData { get; set; }
        public BoundingBox Bounds { get; set; }
        public DateTime ImportTime { get; set; }

        public FBXMesh()
        {
            Vertices = new List<Vector3>();
            Normals = new List<Vector3>();
            UVs = new List<Vector2>();
            Tangents = new List<Vector4>();
            Colors = new List<Color>();
            Indices = new List<int>();
            BlendShapes = new List<FBXBlendShape>();
        }

        public async ValueTask DisposeAsync()
        {
            // Mesh verilerini temizle;
            Vertices.Clear();
            Normals.Clear();
            UVs.Clear();
            Tangents.Clear();
            Colors.Clear();
            Indices.Clear();
            BlendShapes.Clear();

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// FBX animasyon;
    /// </summary>
    public class FBXAnimation : IAsyncDisposable;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int FrameCount { get; set; }
        public float Duration { get; set; }
        public float FrameRate { get; set; }
        public List<FBXAnimationCurve> Curves { get; set; }
        public Dictionary<string, FBXAnimationTrack> Tracks { get; set; }
        public DateTime ImportTime { get; set; }

        public FBXAnimation()
        {
            Curves = new List<FBXAnimationCurve>();
            Tracks = new Dictionary<string, FBXAnimationTrack>();
        }

        public async ValueTask DisposeAsync()
        {
            Curves.Clear();
            Tracks.Clear();
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// FBX materyal;
    /// </summary>
    public class FBXMaterial : IAsyncDisposable;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MaterialType Type { get; set; }
        public Color DiffuseColor { get; set; }
        public Color SpecularColor { get; set; }
        public Color EmissiveColor { get; set; }
        public float Shininess { get; set; }
        public float Opacity { get; set; }
        public Dictionary<string, FBXTexture> Textures { get; set; }
        public DateTime ImportTime { get; set; }

        public FBXMaterial()
        {
            Textures = new Dictionary<string, FBXTexture>();
            DiffuseColor = Color.White;
            Opacity = 1.0f;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var texture in Textures.Values)
            {
                if (texture is IAsyncDisposable disposableTexture)
                {
                    await disposableTexture.DisposeAsync();
                }
            }
            Textures.Clear();
        }
    }

    /// <summary>
    /// FBX yükleyici yapılandırması;
    /// </summary>
    public class FBXLoaderConfig;
    {
        public bool ImportMeshes { get; set; } = true;
        public bool ImportMaterials { get; set; } = true;
        public bool ImportTextures { get; set; } = true;
        public bool ImportAnimations { get; set; } = true;
        public bool ImportSkinning { get; set; } = true;
        public bool ImportBlendShapes { get; set; } = true;
        public bool ImportLights { get; set; } = true;
        public bool ImportCameras { get; set; } = true;
        public bool OptimizeOnImport { get; set; } = true;
        public bool ApplyTransformations { get; set; } = true;
        public bool FixTexturePaths { get; set; } = true;
        public bool UseBinaryFormat { get; set; } = true;
        public bool ExportEmbeddedTextures { get; set; } = true;
        public string DefaultExportVersion { get; set; } = "FBX 2020";
        public int MaxParallelLoads { get; set; } = 4;
        public long MaxFileSize { get; set; } = 1024 * 1024 * 1024; // 1GB;
        public int MaxCachedScenes { get; set; } = 50;
        public int MaxCachedMeshes { get; set; } = 100;
        public int MaxCachedAnimations { get; set; } = 50;
        public int MaxCachedMaterials { get; set; } = 100;
        public bool EnableLogging { get; set; } = true;
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
    }

    /// <summary>
    /// Yükleme seçenekleri;
    /// </summary>
    public class LoadOptions;
    {
        public bool ImportMeshes { get; set; } = true;
        public bool ImportMaterials { get; set; } = true;
        public bool ImportTextures { get; set; } = true;
        public bool ImportAnimations { get; set; } = true;
        public bool ImportSkinning { get; set; } = true;
        public bool ImportBlendShapes { get; set; } = true;
        public bool ImportLights { get; set; } = true;
        public bool ImportCameras { get; set; } = true;
        public bool OptimizeScene { get; set; } = true;
        public bool ApplyTransformations { get; set; } = true;
        public bool FixTexturePaths { get; set; } = true;
        public bool MergeMeshes { get; set; } = false;
        public bool GenerateNormals { get; set; } = false;
        public bool GenerateTangents { get; set; } = false;
        public float ScaleFactor { get; set; } = 1.0f;
        public Vector3 UpAxis { get; set; } = Vector3.UnitY;
        public Vector3 ForwardAxis { get; set; } = Vector3.UnitZ;
    }

    /// <summary>
    /// Export seçenekleri;
    /// </summary>
    public class ExportOptions;
    {
        public string FBXVersion { get; set; } = "FBX 2020";
        public bool ExportMeshes { get; set; } = true;
        public bool ExportAnimations { get; set; } = true;
        public bool ExportMaterials { get; set; } = true;
        public bool ExportTextures { get; set; } = true;
        public bool BinaryFormat { get; set; } = true;
        public bool EmbedTextures { get; set; } = false;
        public bool ApplyTransformations { get; set; } = true;
        public float ScaleFactor { get; set; } = 1.0f;
        public Vector3 UpAxis { get; set; } = Vector3.UnitY;
        public Vector3 ForwardAxis { get; set; } = Vector3.UnitZ;
    }

    /// <summary>
    /// Mesh çıkarma seçenekleri;
    /// </summary>
    public class MeshExtractionOptions;
    {
        public bool IncludeNormals { get; set; } = true;
        public bool IncludeUVs { get; set; } = true;
        public bool IncludeTangents { get; set; } = true;
        public bool IncludeColors { get; set; } = true;
        public bool IncludeBlendShapes { get; set; } = true;
        public bool IncludeSkinning { get; set; } = true;
        public bool OptimizeMesh { get; set; } = true;
        public bool GenerateLODs { get; set; } = false;
        public int LODCount { get; set; } = 3;
        public float LODReductionFactor { get; set; } = 0.5f;
    }

    /// <summary>
    /// Animasyon çıkarma seçenekleri;
    /// </summary>
    public class AnimationExtractionOptions;
    {
        public bool IncludePosition { get; set; } = true;
        public bool IncludeRotation { get; set; } = true;
        public bool IncludeScale { get; set; } = true;
        public bool OptimizeCurves { get; set; } = true;
        public float KeyReductionTolerance { get; set; } = 0.001f;
        public bool NormalizeCurves { get; set; } = false;
        public float TargetFrameRate { get; set; } = 30.0f;
    }

    /// <summary>
    /// Materyal çıkarma seçenekleri;
    /// </summary>
    public class MaterialExtractionOptions;
    {
        public bool ExtractTextures { get; set; } = true;
        public bool ConvertTextures { get; set; } = true;
        public string TextureFormat { get; set; } = "PNG";
        public int TextureMaxSize { get; set; } = 2048;
        public bool GenerateMipMaps { get; set; } = true;
        public bool NormalizeColors { get; set; } = false;
    }

    /// <summary>
    /// Bellek optimizasyon seçenekleri;
    /// </summary>
    public class MemoryOptimizationOptions;
    {
        public bool CompressMeshes { get; set; } = true;
        public bool OptimizeAnimations { get; set; } = true;
        public bool ClearUnusedCache { get; set; } = true;
        public int CacheRetentionHours { get; set; } = 24;
        public float CompressionRatio { get; set; } = 0.7f;
    }

    /// <summary>
    /// FBX metadata;
    /// </summary>
    public class FBXMetadata;
    {
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }
        public string FBXVersion { get; set; }
        public int EstimatedMeshCount { get; set; }
        public int EstimatedAnimationCount { get; set; }
        public int EstimatedMaterialCount { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreationTime { get; set; }
    }

    /// <summary>
    /// FBX yükleyici istatistikleri;
    /// </summary>
    public class FBXLoaderStats;
    {
        public int LoadedSceneCount { get; set; }
        public int LoadedMeshCount { get; set; }
        public int LoadedAnimationCount { get; set; }
        public int LoadedMaterialCount { get; set; }
        public long TotalBytesLoaded { get; set; }
        public TimeSpan LastLoadDuration { get; set; }
        public LoaderState State { get; set; }
        public long CacheMemoryUsage { get; set; }
        public bool IsInitialized { get; set; }
    }

    /// <summary>
    /// Doğrulama sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }
        public Dictionary<string, object> Details { get; set; }

        public ValidationResult()
        {
            Warnings = new List<string>();
            Errors = new List<string>();
            Details = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Yükleme ilerlemesi;
    /// </summary>
    public class LoadProgress;
    {
        public string Step { get; set; }
        public float Percentage { get; set; }
        public string Details { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Yükleyici durumu;
    /// </summary>
    public enum LoaderState;
    {
        Idle,
        Initializing,
        Ready,
        Loading,
        Processing,
        Error,
        Cancelled,
        Disposed;
    }

    /// <summary>
    /// FBX özellikleri;
    /// </summary>
    public enum FBXFeature;
    {
        Animation,
        Materials,
        Textures,
        Skinning,
        BlendShapes,
        Lights,
        Cameras;
    }

    /// <summary>
    /// Materyal tipi;
    /// </summary>
    public enum MaterialType;
    {
        Lambert,
        Phong,
        Blinn,
        PBR;
    }

    /// <summary>
    /// Asset tipi;
    /// </summary>
    public enum AssetType;
    {
        FBX,
        OBJ,
        GLTF,
        STL,
        DAE,
        _3DS;
    }

    #endregion;

    #region Event Classes;

    public class FBXLoadStartedEventArgs : EventArgs;
    {
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FBXLoadProgressEventArgs : EventArgs;
    {
        public string FilePath { get; set; }
        public string CurrentStep { get; set; }
        public float Progress { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FBXLoadCompletedEventArgs : EventArgs;
    {
        public string FilePath { get; set; }
        public FBXScene Scene { get; set; }
        public TimeSpan LoadDuration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FBXLoadFailedEventArgs : EventArgs;
    {
        public string FilePath { get; set; }
        public Exception Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SceneImportedEventArgs : EventArgs;
    {
        public FBXScene Scene { get; set; }
        public string SourcePath { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MeshImportedEventArgs : EventArgs;
    {
        public FBXMesh Mesh { get; set; }
        public string SourceSceneId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class AnimationImportedEventArgs : EventArgs;
    {
        public FBXAnimation Animation { get; set; }
        public string SourceSceneId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MaterialImportedEventArgs : EventArgs;
    {
        public FBXMaterial Material { get; set; }
        public string SourceSceneId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Event Bus Events;

    public class FBXLoaderInitializedEvent : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public string LoaderVersion { get; set; }
    }

    public class FBXLoaderConfigChangedEvent : IEvent;
    {
        public FBXLoaderConfig Config { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FBXFileLoadedEvent : IEvent;
    {
        public string FilePath { get; set; }
        public string SceneId { get; set; }
        public TimeSpan LoadDuration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FBXFileExportedEvent : IEvent;
    {
        public string FilePath { get; set; }
        public string SceneId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FBXCacheClearedEvent : IEvent;
    {
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Reference Classes (diğer dosyalarda implemente edilecek)

    public class FBXNode;
    {
        public string Name { get; set; }
        public Matrix4x4 Transform { get; set; }
        public FBXNode Parent { get; set; }
        public List<FBXNode> Children { get; set; }

        public FBXNode()
        {
            Children = new List<FBXNode>();
            Transform = Matrix4x4.Identity;
        }
    }

    public class FBXMeshNode : FBXNode;
    {
        public FBXMesh Mesh { get; set; }
        public List<FBXMaterial> Materials { get; set; }

        public FBXMeshNode()
        {
            Materials = new List<FBXMaterial>();
        }
    }

    public class FBXMaterialNode : FBXNode;
    {
        public FBXMaterial Material { get; set; }
    }

    public class FBXLightNode : FBXNode;
    {
        public LightType LightType { get; set; }
        public Color Color { get; set; }
        public float Intensity { get; set; }
    }

    public class FBXCameraNode : FBXNode;
    {
        public CameraType CameraType { get; set; }
        public float FieldOfView { get; set; }
        public float NearPlane { get; set; }
        public float FarPlane { get; set; }
    }

    public class FBXAnimationStack;
    {
        public string Name { get; set; }
        public FBXAnimation Animation { get; set; }
        public float StartTime { get; set; }
        public float EndTime { get; set; }
    }

    public class FBXAnimationCurve;
    {
        public string Name { get; set; }
        public List<FBXKeyFrame> KeyFrames { get; set; }
        public InterpolationType Interpolation { get; set; }

        public FBXAnimationCurve()
        {
            KeyFrames = new List<FBXKeyFrame>();
        }
    }

    public class FBXAnimationTrack;
    {
        public string NodeName { get; set; }
        public FBXAnimationCurve PositionCurve { get; set; }
        public FBXAnimationCurve RotationCurve { get; set; }
        public FBXAnimationCurve ScaleCurve { get; set; }
    }

    public class FBXKeyFrame;
    {
        public float Time { get; set; }
        public float Value { get; set; }
        public float InTangent { get; set; }
        public float OutTangent { get; set; }
    }

    public class FBXBlendShape;
    {
        public string Name { get; set; }
        public List<Vector3> Vertices { get; set; }
        public List<Vector3> Normals { get; set; }
        public float Weight { get; set; }

        public FBXBlendShape()
        {
            Vertices = new List<Vector3>();
            Normals = new List<Vector3>();
        }
    }

    public class FBXSkinningData;
    {
        public List<FBXBone> Bones { get; set; }
        public List<FBXVertexWeight> VertexWeights { get; set; }

        public FBXSkinningData()
        {
            Bones = new List<FBXBone>();
            VertexWeights = new List<FBXVertexWeight>();
        }
    }

    public class FBXBone;
    {
        public string Name { get; set; }
        public Matrix4x4 OffsetMatrix { get; set; }
        public FBXNode Node { get; set; }
    }

    public class FBXVertexWeight;
    {
        public int VertexIndex { get; set; }
        public int BoneIndex { get; set; }
        public float Weight { get; set; }
    }

    public class FBXTexture;
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public TextureType Type { get; set; }
        public byte[] Data { get; set; }
    }

    public class BoundingBox;
    {
        public Vector3 Min { get; set; }
        public Vector3 Max { get; set; }
        public Vector3 Center { get; set; }
        public Vector3 Size { get; set; }
    }

    public class Color;
    {
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
        public float A { get; set; }

        public static Color White => new Color { R = 1, G = 1, B = 1, A = 1 };
        public static Color Black => new Color { R = 0, G = 0, B = 0, A = 1 };
    }

    public enum LightType;
    {
        Directional,
        Point,
        Spot,
        Area;
    }

    public enum CameraType;
    {
        Perspective,
        Orthographic;
    }

    public enum InterpolationType;
    {
        Constant,
        Linear,
        Cubic;
    }

    public enum TextureType;
    {
        Diffuse,
        Normal,
        Specular,
        Emissive,
        Occlusion,
        Roughness,
        Metallic;
    }

    #endregion;

    #region Exceptions;

    public class FBXLoaderException : Exception
    {
        public FBXLoaderException(string message) : base(message) { }
        public FBXLoaderException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class FBXLoaderInitializationException : FBXLoaderException;
    {
        public FBXLoaderInitializationException(string message) : base(message) { }
        public FBXLoaderInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class FBXLoaderNotInitializedException : FBXLoaderException;
    {
        public FBXLoaderNotInitializedException(string message) : base(message) { }
    }

    public class FBXLoadException : FBXLoaderException;
    {
        public FBXLoadException(string message) : base(message) { }
        public FBXLoadException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class FBXExportException : FBXLoaderException;
    {
        public FBXExportException(string message) : base(message) { }
        public FBXExportException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class InvalidFileFormatException : FBXLoaderException;
    {
        public InvalidFileFormatException(string message) : base(message) { }
    }

    public class FBXLibraryException : FBXLoaderException;
    {
        public FBXLibraryException(string message) : base(message) { }
        public FBXLibraryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class FBXMetadataException : FBXLoaderException;
    {
        public FBXMetadataException(string message) : base(message) { }
        public FBXMetadataException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class LoadCancelledException : FBXLoaderException;
    {
        public LoadCancelledException(string message) : base(message) { }
    }

    #endregion;

    #region Interfaces;

    public interface IFBXLoader : IAsyncDisposable;
    {
        event EventHandler<FBXLoadStartedEventArgs> LoadStarted;
        event EventHandler<FBXLoadProgressEventArgs> LoadProgress;
        event EventHandler<FBXLoadCompletedEventArgs> LoadCompleted;
        event EventHandler<FBXLoadFailedEventArgs> LoadFailed;
        event EventHandler<SceneImportedEventArgs> SceneImported;
        event EventHandler<MeshImportedEventArgs> MeshImported;
        event EventHandler<AnimationImportedEventArgs> AnimationImported;
        event EventHandler<MaterialImportedEventArgs> MaterialImported;

        Version LoaderVersion { get; }
        IReadOnlyList<string> SupportedFBXVersions { get; }
        int LoadedSceneCount { get; }
        int LoadedMeshCount { get; }
        int LoadedAnimationCount { get; }
        int LoadedMaterialCount { get; }
        FBXLoaderConfig Config { get; }
        LoaderState State { get; }
        TimeSpan LastLoadDuration { get; }
        long TotalBytesLoaded { get; }

        Task InitializeAsync(FBXLoaderConfig config = null);
        Task<FBXScene> LoadFBXAsync(string filePath, LoadOptions options = null);
        Task<List<FBXScene>> LoadMultipleFBXAsync(List<string> filePaths, LoadOptions options = null);
        Task<List<FBXMesh>> ExtractMeshesFromSceneAsync(FBXScene scene, MeshExtractionOptions options = null);
        Task<List<FBXAnimation>> ExtractAnimationsFromSceneAsync(FBXScene scene, AnimationExtractionOptions options = null);
        Task<List<FBXMaterial>> ExtractMaterialsFromSceneAsync(FBXScene scene, MaterialExtractionOptions options = null);
        Task ExportFBXAsync(FBXScene scene, string filePath, ExportOptions options = null);
        Task<byte[]> ConvertMeshToFBXAsync(FBXMesh mesh, ExportOptions options = null);
        Task<byte[]> ConvertAnimationToFBXAsync(FBXAnimation animation, FBXScene targetScene, ExportOptions options = null);
        Task<ValidationResult> ValidateFBXFileAsync(string filePath);
        Task<FBXMetadata> GetFBXMetadataAsync(string filePath);
        void CancelLoad();
        Task ClearCacheAsync();
        Task OptimizeMemoryAsync(MemoryOptimizationOptions options = null);
        FBXLoaderStats GetStats();
        bool IsFeatureSupported(FBXFeature feature);
    }

    #endregion;
}
