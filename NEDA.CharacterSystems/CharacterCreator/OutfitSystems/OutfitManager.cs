// NEDA.CharacterSystems/CharacterCreator/OutfitSystems/OutfitManager.cs;

using NEDA.ContentCreation;
using NEDA.ContentCreation.AssetPipeline;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
.3DModeling.ModelOptimization;
using NEDA.EngineIntegration.AssetManager;

namespace NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
{
    /// <summary>
    /// Karakter kıyafet yönetim sistemi - Outfit (kostüm) oluşturma, yükleme, değiştirme ve optimize etme işlemlerini yönetir;
    /// </summary>
    public class OutfitManager : IOutfitManager, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IAssetManager _assetManager;
        private readonly IMeshOptimizer _meshOptimizer;
        private readonly IAppConfig _appConfig;

        private readonly Dictionary<string, CharacterOutfit> _loadedOutfits;
        private readonly Dictionary<string, OutfitPreset> _outfitPresets;
        private readonly Dictionary<string, MaterialLibrary> _materialLibraries;
        private readonly OutfitCache _outfitCache;

        private bool _isInitialized;
        private readonly string _outfitDatabasePath;
        private readonly string _materialLibraryPath;
        private readonly SemaphoreSlim _outfitLoadSemaphore;

        // Eventler;
        public event EventHandler<OutfitLoadedEventArgs> OutfitLoaded;
        public event EventHandler<OutfitChangedEventArgs> OutfitChanged;
        public event EventHandler<MaterialAppliedEventArgs> MaterialApplied;

        /// <summary>
        /// OutfitManager constructor;
        /// </summary>
        public OutfitManager(
            ILogger<OutfitManager> logger,
            IAssetManager assetManager,
            IMeshOptimizer meshOptimizer,
            IAppConfig appConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _assetManager = assetManager ?? throw new ArgumentNullException(nameof(assetManager));
            _meshOptimizer = meshOptimizer ?? throw new ArgumentNullException(nameof(meshOptimizer));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            _loadedOutfits = new Dictionary<string, CharacterOutfit>();
            _outfitPresets = new Dictionary<string, OutfitPreset>();
            _materialLibraries = new Dictionary<string, MaterialLibrary>();
            _outfitCache = new OutfitCache();

            _outfitDatabasePath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:OutfitDatabasePath") ?? "Data/Outfits",
                "Outfits.json");
            _materialLibraryPath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:MaterialLibraryPath") ?? "Data/Materials",
                "Materials.json");

            _outfitLoadSemaphore = new SemaphoreSlim(1, 1);

            _logger.LogInformation("OutfitManager initialized");
        }

        /// <summary>
        /// OutfitManager'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Initializing OutfitManager...");

                // Outfit database'ini yükle;
                await LoadOutfitDatabaseAsync(cancellationToken);

                // Material kütüphanelerini yükle;
                await LoadMaterialLibrariesAsync(cancellationToken);

                // Outfit cache'ini başlat;
                await _outfitCache.InitializeAsync(cancellationToken);

                _isInitialized = true;
                _logger.LogInformation("OutfitManager initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize OutfitManager");
                throw new OutfitSystemException("Failed to initialize OutfitManager", ex);
            }
        }

        /// <summary>
        /// Karakter için yeni outfit oluşturur;
        /// </summary>
        public async Task<CharacterOutfit> CreateOutfitAsync(
            string characterId,
            OutfitTemplate template,
            OutfitCreationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (template == null)
                throw new ArgumentNullException(nameof(template));

            try
            {
                options ??= OutfitCreationOptions.Default;

                _logger.LogInformation($"Creating outfit for character {characterId} using template {template.Name}");

                // Outfit bileşenlerini yükle;
                var components = await LoadOutfitComponentsAsync(template, options, cancellationToken);

                // Mesh'leri optimize et;
                if (options.OptimizeMeshes)
                {
                    await OptimizeOutfitMeshesAsync(components, options.OptimizationLevel, cancellationToken);
                }

                // Material'ları uygula;
                if (options.ApplyMaterials)
                {
                    await ApplyOutfitMaterialsAsync(components, template.MaterialSet, cancellationToken);
                }

                // Outfit oluştur;
                var outfit = new CharacterOutfit;
                {
                    Id = GenerateOutfitId(characterId, template.Id),
                    CharacterId = characterId,
                    TemplateId = template.Id,
                    Name = template.Name,
                    Components = components,
                    CreationDate = DateTime.UtcNow,
                    Version = template.Version,
                    Metadata = new OutfitMetadata;
                    {
                        PolyCount = CalculateTotalPolyCount(components),
                        TextureSize = CalculateTextureSize(components),
                        MaterialCount = components.Sum(c => c.Materials?.Count ?? 0),
                        PhysicsEnabled = template.SupportsPhysics,
                        LODLevels = options.LODLevels;
                    }
                };

                // Outfit'i kaydet;
                await SaveOutfitAsync(outfit, cancellationToken);

                // Cache'e ekle;
                _loadedOutfits[outfit.Id] = outfit;

                _logger.LogInformation($"Outfit created successfully: {outfit.Id}");

                // Event tetikle;
                OutfitLoaded?.Invoke(this, new OutfitLoadedEventArgs;
                {
                    OutfitId = outfit.Id,
                    CharacterId = characterId,
                    Outfit = outfit;
                });

                return outfit;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create outfit for character {characterId}");
                throw new OutfitCreationException($"Failed to create outfit: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Karaktere outfit uygular;
        /// </summary>
        public async Task<bool> ApplyOutfitAsync(
            string characterId,
            string outfitId,
            OutfitApplicationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (string.IsNullOrEmpty(outfitId))
                throw new ArgumentException("Outfit ID cannot be null or empty", nameof(outfitId));

            try
            {
                options ??= OutfitApplicationOptions.Default;

                _logger.LogInformation($"Applying outfit {outfitId} to character {characterId}");

                // Outfit'i yükle (eğer yüklü değilse)
                var outfit = await GetOrLoadOutfitAsync(outfitId, cancellationToken);
                if (outfit == null)
                {
                    _logger.LogWarning($"Outfit {outfitId} not found");
                    return false;
                }

                // Character ID kontrolü;
                if (outfit.CharacterId != characterId && !options.AllowDifferentCharacter)
                {
                    _logger.LogWarning($"Outfit {outfitId} belongs to different character {outfit.CharacterId}");
                    return false;
                }

                // Physics uyumluluğunu kontrol et;
                if (options.CheckPhysicsCompatibility && !outfit.Metadata.PhysicsEnabled)
                {
                    _logger.LogWarning($"Outfit {outfitId} does not support physics");
                    if (!options.ForceApply)
                        return false;
                }

                // Performans optimizasyonu;
                if (options.OptimizeForPerformance)
                {
                    await OptimizeOutfitForPerformanceAsync(outfit, options.TargetPerformance, cancellationToken);
                }

                // Görsel efektleri uygula;
                if (options.ApplyVisualEffects)
                {
                    await ApplyVisualEffectsAsync(outfit, options.VisualEffects, cancellationToken);
                }

                _logger.LogInformation($"Outfit {outfitId} applied successfully to character {characterId}");

                // Event tetikle;
                OutfitChanged?.Invoke(this, new OutfitChangedEventArgs;
                {
                    CharacterId = characterId,
                    OldOutfitId = options.PreviousOutfitId,
                    NewOutfitId = outfitId,
                    Outfit = outfit,
                    ApplicationTime = DateTime.UtcNow;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply outfit {outfitId} to character {characterId}");
                throw new OutfitApplicationException($"Failed to apply outfit: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Outfit bileşenini değiştirir (parça güncelleme)
        /// </summary>
        public async Task<bool> UpdateOutfitComponentAsync(
            string outfitId,
            string componentType,
            OutfitComponent newComponent,
            ComponentUpdateOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(outfitId))
                throw new ArgumentException("Outfit ID cannot be null or empty", nameof(outfitId));

            if (string.IsNullOrEmpty(componentType))
                throw new ArgumentException("Component type cannot be null or empty", nameof(componentType));

            if (newComponent == null)
                throw new ArgumentNullException(nameof(newComponent));

            try
            {
                await _outfitLoadSemaphore.WaitAsync(cancellationToken);

                if (!_loadedOutfits.TryGetValue(outfitId, out var outfit))
                {
                    // Outfit'i yükle;
                    outfit = await LoadOutfitFromStorageAsync(outfitId, cancellationToken);
                    if (outfit == null)
                    {
                        _logger.LogWarning($"Outfit {outfitId} not found for component update");
                        return false;
                    }
                }

                // Eski component'ı bul;
                var oldComponent = outfit.Components;
                    .FirstOrDefault(c => c.ComponentType == componentType);

                if (oldComponent == null)
                {
                    // Yeni component ekle;
                    outfit.Components.Add(newComponent);
                    _logger.LogInformation($"Added new component {componentType} to outfit {outfitId}");
                }
                else;
                {
                    // Component'ı güncelle;
                    var index = outfit.Components.IndexOf(oldComponent);
                    outfit.Components[index] = newComponent;
                    _logger.LogInformation($"Updated component {componentType} in outfit {outfitId}");
                }

                // Metadata'yi güncelle;
                UpdateOutfitMetadata(outfit);

                // Değişiklikleri kaydet;
                await SaveOutfitAsync(outfit, cancellationToken);

                // Cache'i güncelle;
                _loadedOutfits[outfitId] = outfit;

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update component {componentType} in outfit {outfitId}");
                throw new OutfitComponentException($"Failed to update outfit component: {ex.Message}", ex);
            }
            finally
            {
                _outfitLoadSemaphore.Release();
            }
        }

        /// <summary>
        /// Outfit için material uygular;
        /// </summary>
        public async Task ApplyMaterialToOutfitAsync(
            string outfitId,
            string materialId,
            MaterialApplicationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(outfitId))
                throw new ArgumentException("Outfit ID cannot be null or empty", nameof(outfitId));

            if (string.IsNullOrEmpty(materialId))
                throw new ArgumentException("Material ID cannot be null or empty", nameof(materialId));

            try
            {
                options ??= MaterialApplicationOptions.Default;

                // Material'ı yükle;
                var material = await LoadMaterialAsync(materialId, cancellationToken);
                if (material == null)
                {
                    _logger.LogWarning($"Material {materialId} not found");
                    throw new MaterialNotFoundException($"Material {materialId} not found");
                }

                // Outfit'i yükle;
                var outfit = await GetOrLoadOutfitAsync(outfitId, cancellationToken);
                if (outfit == null)
                {
                    _logger.LogWarning($"Outfit {outfitId} not found");
                    throw new OutfitNotFoundException($"Outfit {outfitId} not found");
                }

                // Material'ı uygula;
                foreach (var component in outfit.Components)
                {
                    if (options.TargetComponents == null ||
                        options.TargetComponents.Contains(component.ComponentType))
                    {
                        await ApplyMaterialToComponentAsync(component, material, options, cancellationToken);
                    }
                }

                // Outfit'i güncelle;
                outfit.LastModified = DateTime.UtcNow;
                await SaveOutfitAsync(outfit, cancellationToken);

                // Event tetikle;
                MaterialApplied?.Invoke(this, new MaterialAppliedEventArgs;
                {
                    OutfitId = outfitId,
                    MaterialId = materialId,
                    Material = material,
                    ApplicationTime = DateTime.UtcNow;
                });

                _logger.LogInformation($"Material {materialId} applied to outfit {outfitId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply material {materialId} to outfit {outfitId}");
                throw new MaterialApplicationException($"Failed to apply material: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Outfit preset'ini kaydeder;
        /// </summary>
        public async Task SaveOutfitPresetAsync(
            string outfitId,
            string presetName,
            string presetDescription = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(outfitId))
                throw new ArgumentException("Outfit ID cannot be null or empty", nameof(outfitId));

            if (string.IsNullOrEmpty(presetName))
                throw new ArgumentException("Preset name cannot be null or empty", nameof(presetName));

            try
            {
                // Outfit'i yükle;
                var outfit = await GetOrLoadOutfitAsync(outfitId, cancellationToken);
                if (outfit == null)
                {
                    _logger.LogWarning($"Outfit {outfitId} not found for preset save");
                    throw new OutfitNotFoundException($"Outfit {outfitId} not found");
                }

                // Preset oluştur;
                var preset = new OutfitPreset;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = presetName,
                    Description = presetDescription,
                    OutfitId = outfitId,
                    CharacterId = outfit.CharacterId,
                    CreationDate = DateTime.UtcNow,
                    Components = outfit.Components.Select(c => c.Clone()).ToList(),
                    MaterialReferences = ExtractMaterialReferences(outfit),
                    Tags = new List<string> { outfit.TemplateId, "preset" },
                    PreviewImagePath = await GeneratePresetPreviewAsync(outfit, cancellationToken)
                };

                // Preset'i kaydet;
                _outfitPresets[preset.Id] = preset;
                await SavePresetToDatabaseAsync(preset, cancellationToken);

                _logger.LogInformation($"Outfit preset saved: {presetName} ({preset.Id})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save outfit preset for outfit {outfitId}");
                throw new OutfitPresetException($"Failed to save outfit preset: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Outfit preset'ini yükler;
        /// </summary>
        public async Task<CharacterOutfit> LoadOutfitPresetAsync(
            string presetId,
            string targetCharacterId = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(presetId))
                throw new ArgumentException("Preset ID cannot be null or empty", nameof(presetId));

            try
            {
                // Preset'i yükle;
                if (!_outfitPresets.TryGetValue(presetId, out var preset))
                {
                    preset = await LoadPresetFromDatabaseAsync(presetId, cancellationToken);
                    if (preset == null)
                    {
                        _logger.LogWarning($"Preset {presetId} not found");
                        throw new PresetNotFoundException($"Preset {presetId} not found");
                    }
                    _outfitPresets[presetId] = preset;
                }

                // Eğer target character belirtilmişse, preset'i bu character için uyarla;
                if (!string.IsNullOrEmpty(targetCharacterId) && preset.CharacterId != targetCharacterId)
                {
                    preset = await AdaptPresetForCharacterAsync(preset, targetCharacterId, cancellationToken);
                }

                // Preset'ten outfit oluştur;
                var outfit = new CharacterOutfit;
                {
                    Id = GenerateOutfitId(preset.CharacterId, $"preset_{preset.Id}"),
                    CharacterId = preset.CharacterId,
                    TemplateId = "preset",
                    Name = preset.Name,
                    Components = preset.Components,
                    CreationDate = DateTime.UtcNow,
                    PresetId = preset.Id,
                    Metadata = new OutfitMetadata;
                    {
                        PolyCount = CalculateTotalPolyCount(preset.Components),
                        TextureSize = CalculateTextureSize(preset.Components),
                        MaterialCount = preset.MaterialReferences.Count,
                        IsPreset = true;
                    }
                };

                // Material'ları yeniden yükle ve uygula;
                await LoadAndApplyPresetMaterialsAsync(outfit, preset, cancellationToken);

                // Outfit'i kaydet;
                await SaveOutfitAsync(outfit, cancellationToken);
                _loadedOutfits[outfit.Id] = outfit;

                _logger.LogInformation($"Outfit preset loaded: {preset.Name} -> {outfit.Id}");

                return outfit;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load outfit preset {presetId}");
                throw new OutfitPresetException($"Failed to load outfit preset: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Outfit optimizasyonu yapar;
        /// </summary>
        public async Task<OutfitOptimizationResult> OptimizeOutfitAsync(
            string outfitId,
            OptimizationProfile profile,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(outfitId))
                throw new ArgumentException("Outfit ID cannot be null or empty", nameof(outfitId));

            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            try
            {
                // Outfit'i yükle;
                var outfit = await GetOrLoadOutfitAsync(outfitId, cancellationToken);
                if (outfit == null)
                {
                    _logger.LogWarning($"Outfit {outfitId} not found for optimization");
                    throw new OutfitNotFoundException($"Outfit {outfitId} not found");
                }

                var result = new OutfitOptimizationResult;
                {
                    OutfitId = outfitId,
                    OriginalPolyCount = outfit.Metadata.PolyCount,
                    OriginalTextureSize = outfit.Metadata.TextureSize,
                    OptimizationStartTime = DateTime.UtcNow;
                };

                // Mesh optimizasyonu;
                if (profile.OptimizeMeshes)
                {
                    await OptimizeMeshesAsync(outfit.Components, profile.MeshQuality, cancellationToken);
                }

                // Texture optimizasyonu;
                if (profile.OptimizeTextures)
                {
                    await OptimizeTexturesAsync(outfit.Components, profile.TextureQuality, cancellationToken);
                }

                // Material optimizasyonu;
                if (profile.OptimizeMaterials)
                {
                    await OptimizeMaterialsAsync(outfit.Components, profile.MaterialComplexity, cancellationToken);
                }

                // LOD oluşturma;
                if (profile.GenerateLODs)
                {
                    await GenerateLODsAsync(outfit.Components, profile.LODLevels, cancellationToken);
                }

                // Metadata'yi güncelle;
                UpdateOutfitMetadata(outfit);

                // Optimizasyon sonuçlarını hesapla;
                result.OptimizedPolyCount = outfit.Metadata.PolyCount;
                result.OptimizedTextureSize = outfit.Metadata.TextureSize;
                result.PolygonReduction = CalculateReductionPercentage(
                    result.OriginalPolyCount, result.OptimizedPolyCount);
                result.TextureReduction = CalculateReductionPercentage(
                    result.OriginalTextureSize, result.OptimizedTextureSize);
                result.OptimizationEndTime = DateTime.UtcNow;
                result.Success = true;

                // Outfit'i güncelle;
                outfit.LastModified = DateTime.UtcNow;
                outfit.Metadata.LastOptimization = DateTime.UtcNow;
                await SaveOutfitAsync(outfit, cancellationToken);

                _logger.LogInformation($"Outfit {outfitId} optimized: {result.PolygonReduction:F1}% polygon reduction, " +
                                      $"{result.TextureReduction:F1}% texture reduction");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to optimize outfit {outfitId}");
                throw new OutfitOptimizationException($"Failed to optimize outfit: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Outfit'i serbest bırakır (memory management)
        /// </summary>
        public void ReleaseOutfit(string outfitId)
        {
            if (string.IsNullOrEmpty(outfitId))
                return;

            if (_loadedOutfits.TryGetValue(outfitId, out var outfit))
            {
                // Component'ları temizle;
                foreach (var component in outfit.Components)
                {
                    component.Dispose();
                }

                _loadedOutfits.Remove(outfitId);
                _logger.LogInformation($"Outfit {outfitId} released from memory");
            }
        }

        /// <summary>
        /// Tüm outfit'leri serbest bırakır;
        /// </summary>
        public void ReleaseAllOutfits()
        {
            foreach (var outfitId in _loadedOutfits.Keys.ToList())
            {
                ReleaseOutfit(outfitId);
            }

            _logger.LogInformation("All outfits released from memory");
        }

        /// <summary>
        /// Sistem durumunu raporlar;
        /// </summary>
        public OutfitSystemStatus GetSystemStatus()
        {
            return new OutfitSystemStatus;
            {
                IsInitialized = _isInitialized,
                LoadedOutfitCount = _loadedOutfits.Count,
                PresetCount = _outfitPresets.Count,
                MaterialLibraryCount = _materialLibraries.Count,
                CacheStatus = _outfitCache.GetStatus(),
                MemoryUsage = CalculateMemoryUsage(),
                LastOperationTime = DateTime.UtcNow;
            };
        }

        #region Private Methods;

        private async Task LoadOutfitDatabaseAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_outfitDatabasePath))
            {
                _logger.LogWarning($"Outfit database not found at {_outfitDatabasePath}. Creating new database.");
                await SaveOutfitDatabaseAsync(new List<OutfitDatabaseEntry>(), cancellationToken);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_outfitDatabasePath);
                var database = JsonConvert.DeserializeObject<OutfitDatabase>(json);

                if (database?.Entries != null)
                {
                    foreach (var entry in database.Entries)
                    {
                        _outfitCache.AddEntry(entry);
                    }

                    _logger.LogInformation($"Loaded {database.Entries.Count} outfit entries from database");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load outfit database");
                throw;
            }
        }

        private async Task LoadMaterialLibrariesAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_materialLibraryPath))
            {
                _logger.LogWarning($"Material library not found at {_materialLibraryPath}");
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_materialLibraryPath);
                var libraries = JsonConvert.DeserializeObject<List<MaterialLibrary>>(json);

                if (libraries != null)
                {
                    foreach (var library in libraries)
                    {
                        _materialLibraries[library.Id] = library;
                    }

                    _logger.LogInformation($"Loaded {libraries.Count} material libraries");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load material libraries");
                throw;
            }
        }

        private async Task<List<OutfitComponent>> LoadOutfitComponentsAsync(
            OutfitTemplate template,
            OutfitCreationOptions options,
            CancellationToken cancellationToken)
        {
            var components = new List<OutfitComponent>();

            foreach (var componentDef in template.ComponentDefinitions)
            {
                try
                {
                    var component = await LoadComponentAsync(componentDef, options, cancellationToken);
                    if (component != null)
                    {
                        components.Add(component);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to load component {componentDef.Type} from {componentDef.AssetPath}");

                    if (!options.ContinueOnComponentLoadError)
                        throw;
                }
            }

            return components;
        }

        private async Task<OutfitComponent> LoadComponentAsync(
            ComponentDefinition definition,
            OutfitCreationOptions options,
            CancellationToken cancellationToken)
        {
            // Asset'i yükle;
            var asset = await _assetManager.LoadAssetAsync(definition.AssetPath, cancellationToken);
            if (asset == null)
                throw new AssetLoadException($"Failed to load asset: {definition.AssetPath}");

            // Component oluştur;
            var component = new OutfitComponent;
            {
                ComponentType = definition.Type,
                AssetPath = definition.AssetPath,
                Asset = asset,
                PositionOffset = definition.PositionOffset,
                RotationOffset = definition.RotationOffset,
                Scale = definition.Scale,
                ParentBone = definition.ParentBone,
                Materials = new List<Material>(),
                PhysicsProperties = definition.PhysicsProperties,
                CollisionEnabled = definition.CollisionEnabled,
                IsVisible = true;
            };

            // Material'ları yükle;
            if (definition.MaterialPaths != null)
            {
                foreach (var materialPath in definition.MaterialPaths)
                {
                    var material = await LoadMaterialAsync(materialPath, cancellationToken);
                    if (material != null)
                    {
                        component.Materials.Add(material);
                    }
                }
            }

            return component;
        }

        private async Task<Material> LoadMaterialAsync(string materialId, CancellationToken cancellationToken)
        {
            // Önce cache'de ara;
            var material = _outfitCache.GetMaterial(materialId);
            if (material != null)
                return material;

            // Material library'lerde ara;
            foreach (var library in _materialLibraries.Values)
            {
                material = library.Materials.FirstOrDefault(m => m.Id == materialId);
                if (material != null)
                {
                    _outfitCache.AddMaterial(material);
                    return material;
                }
            }

            // Dosya sisteminden yükle;
            var materialPath = Path.Combine(_materialLibraryPath, $"{materialId}.mat");
            if (File.Exists(materialPath))
            {
                var json = await File.ReadAllTextAsync(materialPath);
                material = JsonConvert.DeserializeObject<Material>(json);

                if (material != null)
                {
                    _outfitCache.AddMaterial(material);
                    return material;
                }
            }

            return null;
        }

        private async Task<CharacterOutfit> GetOrLoadOutfitAsync(string outfitId, CancellationToken cancellationToken)
        {
            if (_loadedOutfits.TryGetValue(outfitId, out var outfit))
                return outfit;

            return await LoadOutfitFromStorageAsync(outfitId, cancellationToken);
        }

        private async Task<CharacterOutfit> LoadOutfitFromStorageAsync(string outfitId, CancellationToken cancellationToken)
        {
            // Cache'de ara;
            var outfit = _outfitCache.GetOutfit(outfitId);
            if (outfit != null)
            {
                _loadedOutfits[outfitId] = outfit;
                return outfit;
            }

            // Dosya sisteminden yükle;
            var outfitPath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:OutfitStoragePath") ?? "Data/Outfits/Storage",
                $"{outfitId}.outfit");

            if (!File.Exists(outfitPath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(outfitPath);
                outfit = JsonConvert.DeserializeObject<CharacterOutfit>(json);

                if (outfit != null)
                {
                    // Component asset'lerini yeniden yükle;
                    await ReloadOutfitAssetsAsync(outfit, cancellationToken);

                    _outfitCache.AddOutfit(outfit);
                    _loadedOutfits[outfitId] = outfit;

                    _logger.LogInformation($"Outfit loaded from storage: {outfitId}");
                }

                return outfit;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load outfit from storage: {outfitId}");
                return null;
            }
        }

        private async Task ReloadOutfitAssetsAsync(CharacterOutfit outfit, CancellationToken cancellationToken)
        {
            foreach (var component in outfit.Components)
            {
                if (!string.IsNullOrEmpty(component.AssetPath))
                {
                    component.Asset = await _assetManager.LoadAssetAsync(
                        component.AssetPath, cancellationToken);
                }
            }
        }

        private async Task SaveOutfitAsync(CharacterOutfit outfit, CancellationToken cancellationToken)
        {
            var outfitPath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:OutfitStoragePath") ?? "Data/Outfits/Storage",
                $"{outfit.Id}.outfit");

            // Asset referanslarını temizle (serialization için)
            var outfitClone = outfit.Clone();
            foreach (var component in outfitClone.Components)
            {
                component.Asset = null; // Asset serialization yapılmayacak;
            }

            var json = JsonConvert.SerializeObject(outfitClone, Formatting.Indented);
            await File.WriteAllTextAsync(outfitPath, json);

            // Database entry güncelle;
            var entry = new OutfitDatabaseEntry
            {
                OutfitId = outfit.Id,
                CharacterId = outfit.CharacterId,
                Name = outfit.Name,
                TemplateId = outfit.TemplateId,
                CreationDate = outfit.CreationDate,
                LastModified = outfit.LastModified,
                FilePath = outfitPath,
                Metadata = outfit.Metadata;
            };

            _outfitCache.AddEntry(entry);
            await UpdateOutfitDatabaseAsync(entry, cancellationToken);
        }

        private async Task UpdateOutfitDatabaseAsync(OutfitDatabaseEntry entry, CancellationToken cancellationToken)
        {
            // Database güncelleme işlemleri;
            // Burada gerçek database operasyonları yapılacak;
            await Task.CompletedTask;
        }

        private async Task OptimizeMeshesAsync(
            List<OutfitComponent> components,
            MeshQuality quality,
            CancellationToken cancellationToken)
        {
            foreach (var component in components)
            {
                if (component.Asset != null)
                {
                    await _meshOptimizer.OptimizeMeshAsync(
                        component.Asset,
                        quality,
                        cancellationToken);
                }
            }
        }

        private async Task OptimizeTexturesAsync(
            List<OutfitComponent> components,
            TextureQuality quality,
            CancellationToken cancellationToken)
        {
            // Texture optimizasyon işlemleri;
            await Task.CompletedTask;
        }

        private async Task OptimizeMaterialsAsync(
            List<OutfitComponent> components,
            MaterialComplexity complexity,
            CancellationToken cancellationToken)
        {
            // Material optimizasyon işlemleri;
            await Task.CompletedTask;
        }

        private async Task GenerateLODsAsync(
            List<OutfitComponent> components,
            int lodLevels,
            CancellationToken cancellationToken)
        {
            foreach (var component in components)
            {
                if (component.Asset != null)
                {
                    await _meshOptimizer.GenerateLODsAsync(
                        component.Asset,
                        lodLevels,
                        cancellationToken);
                }
            }
        }

        private void UpdateOutfitMetadata(CharacterOutfit outfit)
        {
            outfit.Metadata.PolyCount = CalculateTotalPolyCount(outfit.Components);
            outfit.Metadata.TextureSize = CalculateTextureSize(outfit.Components);
            outfit.Metadata.MaterialCount = outfit.Components.Sum(c => c.Materials?.Count ?? 0);
            outfit.Metadata.ComponentCount = outfit.Components.Count;
            outfit.LastModified = DateTime.UtcNow;
        }

        private int CalculateTotalPolyCount(List<OutfitComponent> components)
        {
            return components.Sum(c => c.Asset?.PolyCount ?? 0);
        }

        private long CalculateTextureSize(List<OutfitComponent> components)
        {
            long totalSize = 0;
            foreach (var component in components)
            {
                if (component.Materials != null)
                {
                    totalSize += component.Materials.Sum(m => m.TextureSize);
                }
            }
            return totalSize;
        }

        private float CalculateReductionPercentage(long original, long optimized)
        {
            if (original == 0) return 0;
            return ((original - optimized) / (float)original) * 100f;
        }

        private string GenerateOutfitId(string characterId, string templateId)
        {
            return $"{characterId}_{templateId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("OutfitManager is not initialized. Call InitializeAsync first.");
        }

        private long CalculateMemoryUsage()
        {
            long total = 0;

            foreach (var outfit in _loadedOutfits.Values)
            {
                total += outfit.Components.Sum(c => c.EstimatedMemoryUsage);
            }

            return total;
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
                    ReleaseAllOutfits();
                    _outfitLoadSemaphore?.Dispose();
                    _outfitCache?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~OutfitManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    public interface IOutfitManager;
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<CharacterOutfit> CreateOutfitAsync(
            string characterId,
            OutfitTemplate template,
            OutfitCreationOptions options = null,
            CancellationToken cancellationToken = default);
        Task<bool> ApplyOutfitAsync(
            string characterId,
            string outfitId,
            OutfitApplicationOptions options = null,
            CancellationToken cancellationToken = default);
        Task<bool> UpdateOutfitComponentAsync(
            string outfitId,
            string componentType,
            OutfitComponent newComponent,
            ComponentUpdateOptions options = null,
            CancellationToken cancellationToken = default);
        Task ApplyMaterialToOutfitAsync(
            string outfitId,
            string materialId,
            MaterialApplicationOptions options = null,
            CancellationToken cancellationToken = default);
        Task SaveOutfitPresetAsync(
            string outfitId,
            string presetName,
            string presetDescription = null,
            CancellationToken cancellationToken = default);
        Task<CharacterOutfit> LoadOutfitPresetAsync(
            string presetId,
            string targetCharacterId = null,
            CancellationToken cancellationToken = default);
        Task<OutfitOptimizationResult> OptimizeOutfitAsync(
            string outfitId,
            OptimizationProfile profile,
            CancellationToken cancellationToken = default);
        void ReleaseOutfit(string outfitId);
        void ReleaseAllOutfits();
        OutfitSystemStatus GetSystemStatus();

        event EventHandler<OutfitLoadedEventArgs> OutfitLoaded;
        event EventHandler<OutfitChangedEventArgs> OutfitChanged;
        event EventHandler<MaterialAppliedEventArgs> MaterialApplied;
    }

    public class CharacterOutfit : ICloneable, IDisposable;
    {
        public string Id { get; set; }
        public string CharacterId { get; set; }
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public string PresetId { get; set; }
        public List<OutfitComponent> Components { get; set; } = new List<OutfitComponent>();
        public DateTime CreationDate { get; set; }
        public DateTime LastModified { get; set; }
        public string Version { get; set; }
        public OutfitMetadata Metadata { get; set; } = new OutfitMetadata();

        public object Clone()
        {
            return new CharacterOutfit;
            {
                Id = this.Id,
                CharacterId = this.CharacterId,
                TemplateId = this.TemplateId,
                Name = this.Name,
                PresetId = this.PresetId,
                Components = this.Components.Select(c => (OutfitComponent)c.Clone()).ToList(),
                CreationDate = this.CreationDate,
                LastModified = this.LastModified,
                Version = this.Version,
                Metadata = this.Metadata.Clone()
            };
        }

        public void Dispose()
        {
            foreach (var component in Components)
            {
                component.Dispose();
            }
        }

        public long EstimatedMemoryUsage =>
            Components.Sum(c => c.EstimatedMemoryUsage);
    }

    public class OutfitComponent : ICloneable, IDisposable;
    {
        public string ComponentType { get; set; }
        public string AssetPath { get; set; }
        public object Asset { get; set; }
        public Vector3 PositionOffset { get; set; }
        public Quaternion RotationOffset { get; set; }
        public Vector3 Scale { get; set; } = Vector3.One;
        public string ParentBone { get; set; }
        public List<Material> Materials { get; set; }
        public PhysicsProperties PhysicsProperties { get; set; }
        public bool CollisionEnabled { get; set; }
        public bool IsVisible { get; set; }
        public Dictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();

        public long EstimatedMemoryUsage { get; set; }

        public object Clone()
        {
            return new OutfitComponent;
            {
                ComponentType = this.ComponentType,
                AssetPath = this.AssetPath,
                Asset = this.Asset, // Shallow copy - asset'ler paylaşılır;
                PositionOffset = this.PositionOffset,
                RotationOffset = this.RotationOffset,
                Scale = this.Scale,
                ParentBone = this.ParentBone,
                Materials = this.Materials?.Select(m => m.Clone()).ToList(),
                PhysicsProperties = this.PhysicsProperties?.Clone(),
                CollisionEnabled = this.CollisionEnabled,
                IsVisible = this.IsVisible,
                CustomProperties = new Dictionary<string, object>(this.CustomProperties),
                EstimatedMemoryUsage = this.EstimatedMemoryUsage;
            };
        }

        public void Dispose()
        {
            // Asset disposal logic;
        }
    }

    public class OutfitTemplate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Version { get; set; }
        public List<ComponentDefinition> ComponentDefinitions { get; set; } = new List<ComponentDefinition>();
        public MaterialSet MaterialSet { get; set; }
        public bool SupportsPhysics { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class Material : ICloneable;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MaterialType Type { get; set; }
        public Shader Shader { get; set; }
        public Dictionary<string, Texture> Textures { get; set; } = new Dictionary<string, Texture>();
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public long TextureSize { get; set; }

        public object Clone()
        {
            return new Material;
            {
                Id = this.Id,
                Name = this.Name,
                Type = this.Type,
                Shader = this.Shader?.Clone(),
                Textures = new Dictionary<string, Texture>(this.Textures),
                Parameters = new Dictionary<string, object>(this.Parameters),
                TextureSize = this.TextureSize;
            };
        }
    }

    public class OutfitMetadata : ICloneable;
    {
        public int PolyCount { get; set; }
        public long TextureSize { get; set; }
        public int MaterialCount { get; set; }
        public int ComponentCount { get; set; }
        public int LODLevels { get; set; }
        public bool PhysicsEnabled { get; set; }
        public bool IsPreset { get; set; }
        public DateTime LastOptimization { get; set; }

        public object Clone()
        {
            return new OutfitMetadata;
            {
                PolyCount = this.PolyCount,
                TextureSize = this.TextureSize,
                MaterialCount = this.MaterialCount,
                ComponentCount = this.ComponentCount,
                LODLevels = this.LODLevels,
                PhysicsEnabled = this.PhysicsEnabled,
                IsPreset = this.IsPreset,
                LastOptimization = this.LastOptimization;
            };
        }
    }

    public class OutfitCreationOptions;
    {
        public static OutfitCreationOptions Default => new OutfitCreationOptions();

        public bool OptimizeMeshes { get; set; } = true;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public bool ApplyMaterials { get; set; } = true;
        public int LODLevels { get; set; } = 3;
        public bool ContinueOnComponentLoadError { get; set; } = false;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public class OutfitApplicationOptions;
    {
        public static OutfitApplicationOptions Default => new OutfitApplicationOptions();

        public bool CheckPhysicsCompatibility { get; set; } = true;
        public bool ForceApply { get; set; } = false;
        public bool OptimizeForPerformance { get; set; } = true;
        public PerformanceLevel TargetPerformance { get; set; } = PerformanceLevel.High;
        public bool ApplyVisualEffects { get; set; } = true;
        public List<VisualEffect> VisualEffects { get; set; }
        public string PreviousOutfitId { get; set; }
        public bool AllowDifferentCharacter { get; set; } = false;
    }

    public class OutfitOptimizationResult;
    {
        public string OutfitId { get; set; }
        public int OriginalPolyCount { get; set; }
        public int OptimizedPolyCount { get; set; }
        public long OriginalTextureSize { get; set; }
        public long OptimizedTextureSize { get; set; }
        public float PolygonReduction { get; set; }
        public float TextureReduction { get; set; }
        public DateTime OptimizationStartTime { get; set; }
        public DateTime OptimizationEndTime { get; set; }
        public TimeSpan Duration => OptimizationEndTime - OptimizationStartTime;
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class OutfitSystemStatus;
    {
        public bool IsInitialized { get; set; }
        public int LoadedOutfitCount { get; set; }
        public int PresetCount { get; set; }
        public int MaterialLibraryCount { get; set; }
        public CacheStatus CacheStatus { get; set; }
        public long MemoryUsage { get; set; }
        public DateTime LastOperationTime { get; set; }
    }

    // Event Args Classes;
    public class OutfitLoadedEventArgs : EventArgs;
    {
        public string OutfitId { get; set; }
        public string CharacterId { get; set; }
        public CharacterOutfit Outfit { get; set; }
    }

    public class OutfitChangedEventArgs : EventArgs;
    {
        public string CharacterId { get; set; }
        public string OldOutfitId { get; set; }
        public string NewOutfitId { get; set; }
        public CharacterOutfit Outfit { get; set; }
        public DateTime ApplicationTime { get; set; }
    }

    public class MaterialAppliedEventArgs : EventArgs;
    {
        public string OutfitId { get; set; }
        public string MaterialId { get; set; }
        public Material Material { get; set; }
        public DateTime ApplicationTime { get; set; }
    }

    // Enums;
    public enum MaterialType { Diffuse, Specular, Normal, Emissive, Metallic, Roughness }
    public enum OptimizationLevel { Low, Medium, High, Ultra }
    public enum PerformanceLevel { Low, Medium, High, Ultra }
    public enum MeshQuality { LowPoly, MediumPoly, HighPoly, UltraPoly }
    public enum TextureQuality { Low, Medium, High, Ultra }
    public enum MaterialComplexity { Simple, Standard, Complex, PBR }

    // Exceptions;
    public class OutfitSystemException : Exception
    {
        public OutfitSystemException(string message) : base(message) { }
        public OutfitSystemException(string message, Exception inner) : base(message, inner) { }
    }

    public class OutfitCreationException : OutfitSystemException;
    {
        public OutfitCreationException(string message) : base(message) { }
        public OutfitCreationException(string message, Exception inner) : base(message, inner) { }
    }

    public class OutfitApplicationException : OutfitSystemException;
    {
        public OutfitApplicationException(string message) : base(message) { }
        public OutfitApplicationException(string message, Exception inner) : base(message, inner) { }
    }

    public class OutfitComponentException : OutfitSystemException;
    {
        public OutfitComponentException(string message) : base(message) { }
        public OutfitComponentException(string message, Exception inner) : base(message, inner) { }
    }

    public class MaterialNotFoundException : OutfitSystemException;
    {
        public MaterialNotFoundException(string message) : base(message) { }
    }

    public class OutfitNotFoundException : OutfitSystemException;
    {
        public OutfitNotFoundException(string message) : base(message) { }
    }

    public class OutfitOptimizationException : OutfitSystemException;
    {
        public OutfitOptimizationException(string message) : base(message) { }
        public OutfitOptimizationException(string message, Exception inner) : base(message, inner) { }
    }

    public class OutfitPresetException : OutfitSystemException;
    {
        public OutfitPresetException(string message) : base(message) { }
        public OutfitPresetException(string message, Exception inner) : base(message, inner) { }
    }

    public class PresetNotFoundException : OutfitSystemException;
    {
        public PresetNotFoundException(string message) : base(message) { }
    }

    public class MaterialApplicationException : OutfitSystemException;
    {
        public MaterialApplicationException(string message) : base(message) { }
        public MaterialApplicationException(string message, Exception inner) : base(message, inner) { }
    }

    public class AssetLoadException : OutfitSystemException;
    {
        public AssetLoadException(string message) : base(message) { }
    }

    #endregion;
}
