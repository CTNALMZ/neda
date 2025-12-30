using NEDA.AI.NaturalLanguage;
using NEDA.CharacterSystems.CharacterCreator.AppearanceCustomization;
using NEDA.CharacterSystems.CharacterCreator.MorphTargets;
using NEDA.Configuration;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
{
    /// <summary>
    /// Giyim sistemi - karakter kıyafetlerini yönetir;
    /// </summary>
    public class ClothingSystem : IDisposable
    {
        private readonly ILogger _logger;
        private readonly FileService _fileService;
        private readonly MorphEngine _morphEngine;
        private readonly SkinManager _skinManager;
        private readonly AppConfig _config;

        private readonly Dictionary<string, ClothingItem> _activeClothing;
        private readonly Dictionary<string, ClothingPreset> _clothingPresets;
        private readonly ClothingValidator _validator;
        private bool _isInitialized;

        public event EventHandler<ClothingChangedEventArgs> ClothingChanged;
        public event EventHandler<ClothingLayerUpdatedEventArgs> LayerUpdated;

        public ClothingSystem(ILogger logger, FileService fileService,
                            MorphEngine morphEngine, SkinManager skinManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _morphEngine = morphEngine ?? throw new ArgumentNullException(nameof(morphEngine));
            _skinManager = skinManager ?? throw new ArgumentNullException(nameof(skinManager));

            _config = ConfigurationManager.GetSection<ClothingSystemConfig>("ClothingSystem");
            _activeClothing = new Dictionary<string, ClothingItem>();
            _clothingPresets = new Dictionary<string, ClothingPreset>();
            _validator = new ClothingValidator();

            InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("ClothingSystem başlatılıyor...");

                // Varsayılan giyim katmanlarını yükle;
                await LoadDefaultLayersAsync();

                // Giysi kütüphanesini başlat;
                await InitializeClothingLibraryAsync();

                // Preset'leri yükle;
                await LoadPresetsAsync();

                _isInitialized = true;
                _logger.LogInformation("ClothingSystem başlatma tamamlandı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"ClothingSystem başlatma hatası: {ex.Message}", ex);
                throw new ClothingSystemInitializationException(
                    "ClothingSystem başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Karaktere giysi ekler;
        /// </summary>
        public async Task<ClothingResult> EquipClothingAsync(string characterId, ClothingItem clothing)
        {
            ValidateSystemState();
            ValidateCharacter(characterId);

            try
            {
                // Giysi validasyonu;
                var validationResult = await _validator.ValidateClothingAsync(clothing, characterId);
                if (!validationResult.IsValid)
                {
                    throw new ClothingValidationException(
                        $"Giysi validasyon hatası: {validationResult.Errors.First()}");
                }

                // Fiziksel uyum kontrolü;
                var fitResult = await CheckClothingFitAsync(characterId, clothing);
                if (!fitResult.FitsProperly)
                {
                    // Otomatik uyum sağla;
                    clothing = await AdjustClothingForCharacterAsync(clothing, characterId);
                }

                // Kumaş fizik özelliklerini uygula;
                await ApplyFabricPhysicsAsync(clothing);

                // Morfolojik uyum sağla;
                await ApplyMorphAdjustmentsAsync(characterId, clothing);

                // Katman yönetimi;
                await ManageClothingLayersAsync(characterId, clothing);

                // Görünürlük ve renk ayarları;
                await ApplyVisualPropertiesAsync(clothing);

                // Önbelleğe ekle;
                _activeClothing[GetClothingKey(characterId, clothing.Category)] = clothing;

                var result = new ClothingResult;
                {
                    Success = true,
                    ClothingItem = clothing,
                    FitQuality = fitResult.FitQuality,
                    AppliedAdjustments = fitResult.RequiredAdjustments;
                };

                OnClothingChanged(new ClothingChangedEventArgs;
                {
                    CharacterId = characterId,
                    ClothingItem = clothing,
                    Action = ClothingAction.Equipped,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"{characterId} karakterine {clothing.Name} giydirildi");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Giysi giydirme hatası: {ex.Message}", ex);
                throw new ClothingEquipException(
                    $"{characterId} karakterine {clothing.Name} giydirilemedi", ex);
            }
        }

        /// <summary>
        /// Karakterden giysi çıkarır;
        /// </summary>
        public async Task<bool> UnequipClothingAsync(string characterId, string clothingCategory)
        {
            ValidateSystemState();

            try
            {
                var key = GetClothingKey(characterId, clothingCategory);

                if (!_activeClothing.ContainsKey(key))
                {
                    _logger.LogWarning($"Çıkarılacak giysi bulunamadı: {clothingCategory}");
                    return false;
                }

                var clothing = _activeClothing[key];

                // Fiziksel efekleri kaldır;
                await RemoveFabricPhysicsAsync(clothing);

                // Morfolojik ayarları sıfırla;
                await ResetMorphAdjustmentsAsync(characterId, clothingCategory);

                // Katmandan çıkar;
                await RemoveFromClothingLayerAsync(characterId, clothingCategory);

                // Önbellekten kaldır;
                _activeClothing.Remove(key);

                OnClothingChanged(new ClothingChangedEventArgs;
                {
                    CharacterId = characterId,
                    ClothingItem = clothing,
                    Action = ClothingAction.Removed,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"{characterId} karakterinden {clothingCategory} çıkarıldı");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Giysi çıkarma hatası: {ex.Message}", ex);
                throw new ClothingUnequipException(
                    $"{characterId} karakterinden {clothingCategory} çıkarılamadı", ex);
            }
        }

        /// <summary>
        /// Giysi katmanlarını yönetir;
        /// </summary>
        private async Task ManageClothingLayersAsync(string characterId, ClothingItem newClothing)
        {
            var layerOrder = GetLayerOrder(newClothing.Category);
            var existingItems = GetClothingInSameLayer(characterId, layerOrder);

            // Katman çakışması kontrolü;
            foreach (var existingItem in existingItems)
            {
                if (await CheckLayerConflictAsync(existingItem, newClothing))
                {
                    // Çakışan giysiyi çıkar;
                    await UnequipClothingAsync(characterId, existingItem.Category);
                }
            }

            // Katman görünürlüğünü güncelle;
            await UpdateLayerVisibilityAsync(characterId, newClothing);

            OnLayerUpdated(new ClothingLayerUpdatedEventArgs;
            {
                CharacterId = characterId,
                Layer = layerOrder,
                ClothingItem = newClothing,
                Action = LayerAction.Added;
            });
        }

        /// <summary>
        /// Giysi uyumunu kontrol eder;
        /// </summary>
        private async Task<ClothingFitResult> CheckClothingFitAsync(string characterId, ClothingItem clothing)
        {
            var characterMeasurements = await GetCharacterMeasurementsAsync(characterId);
            var clothingMeasurements = clothing.Measurements;

            var tolerance = _config.FitTolerance;
            var fitIssues = new List<FitIssue>();
            var adjustments = new List<ClothingAdjustment>();

            // Boyut karşılaştırması;
            foreach (var dimension in clothingMeasurements.Keys)
            {
                if (characterMeasurements.ContainsKey(dimension))
                {
                    var characterValue = characterMeasurements[dimension];
                    var clothingValue = clothingMeasurements[dimension];
                    var difference = Math.Abs(characterValue - clothingValue);

                    if (difference > tolerance)
                    {
                        fitIssues.Add(new FitIssue;
                        {
                            Dimension = dimension,
                            Difference = difference,
                            IssueType = difference > 0 ? FitIssueType.TooTight : FitIssueType.TooLoose;
                        });

                        adjustments.Add(new ClothingAdjustment;
                        {
                            Dimension = dimension,
                            AdjustmentAmount = characterValue - clothingValue,
                            AdjustmentType = AdjustmentType.Resize;
                        });
                    }
                }
            }

            // Kumaş esneme kontrolü;
            if (clothing.FabricType.HasStretch)
            {
                var stretchFactor = await CalculateStretchFactorAsync(clothing.FabricType);
                foreach (var issue in fitIssues.ToList())
                {
                    if (issue.Difference <= tolerance * stretchFactor)
                    {
                        fitIssues.Remove(issue);
                        adjustments.RemoveAll(a => a.Dimension == issue.Dimension);
                    }
                }
            }

            return new ClothingFitResult;
            {
                FitsProperly = fitIssues.Count == 0,
                FitQuality = CalculateFitQuality(fitIssues),
                FitIssues = fitIssues,
                RequiredAdjustments = adjustments;
            };
        }

        /// <summary>
        /// Giysiyi karaktere göre ayarlar;
        /// </summary>
        private async Task<ClothingItem> AdjustClothingForCharacterAsync(ClothingItem clothing, string characterId)
        {
            var adjustedClothing = clothing.Clone();
            var characterMeasurements = await GetCharacterMeasurementsAsync(characterId);

            foreach (var measurement in characterMeasurements)
            {
                if (adjustedClothing.Measurements.ContainsKey(measurement.Key))
                {
                    // Ölçüyü karaktere göre ayarla;
                    adjustedClothing.Measurements[measurement.Key] = measurement.Value;

                    // UV haritasını güncelle;
                    await UpdateUVMapForMeasurementAsync(adjustedClothing, measurement.Key, measurement.Value);
                }
            }

            // Doku kaydırmasını ayarla;
            await AdjustTextureMappingAsync(adjustedClothing, characterId);

            // Fiziksel özellikleri yeniden hesapla;
            await RecalculatePhysicsPropertiesAsync(adjustedClothing);

            return adjustedClothing;
        }

        /// <summary>
        /// Kumaş fizik özelliklerini uygular;
        /// </summary>
        private async Task ApplyFabricPhysicsAsync(ClothingItem clothing)
        {
            var fabricProperties = await GetFabricPropertiesAsync(clothing.FabricType);

            // Ağırlık uygula;
            await ApplyWeightAsync(clothing, fabricProperties.WeightPerSquareMeter);

            // Esneklik uygula;
            await ApplyElasticityAsync(clothing, fabricProperties.Elasticity);

            // Sürükleme katsayısı;
            await ApplyDragCoefficientAsync(clothing, fabricProperties.DragCoefficient);

            // Kırışıklık simülasyonu;
            if (fabricProperties.WrinkleFactor > 0)
            {
                await ApplyWrinkleSimulationAsync(clothing, fabricProperties.WrinkleFactor);
            }
        }

        /// <summary>
        /// Morfolojik ayarlamaları uygular;
        /// </summary>
        private async Task ApplyMorphAdjustmentsAsync(string characterId, ClothingItem clothing)
        {
            var morphTargets = await CalculateRequiredMorphsAsync(characterId, clothing);

            foreach (var morphTarget in morphTargets)
            {
                await _morphEngine.ApplyMorphAsync(characterId, morphTarget);
            }

            // Blend shape'leri güncelle;
            await UpdateBlendShapesForClothingAsync(characterId, clothing);
        }

        /// <summary>
        /// Görsel özellikleri uygular;
        /// </summary>
        private async Task ApplyVisualPropertiesAsync(ClothingItem clothing)
        {
            // Renk uygula;
            await ApplyColorAsync(clothing);

            // Doku uygula;
            await ApplyTextureAsync(clothing);

            // Normal map uygula;
            await ApplyNormalMapAsync(clothing);

            // Roughness/Metallic map uygula;
            await ApplyMaterialMapsAsync(clothing);

            // Transparency/Opacity ayarla;
            await ApplyTransparencyAsync(clothing);
        }

        /// <summary>
        /// Aktif giysileri getirir;
        /// </summary>
        public IReadOnlyDictionary<string, ClothingItem> GetActiveClothing(string characterId)
        {
            return _activeClothing;
                .Where(kvp => kvp.Key.StartsWith($"{characterId}:"))
                .ToDictionary(kvp => kvp.Key.Split(':')[1], kvp => kvp.Value);
        }

        /// <summary>
        /// Giysi preset'ini uygular;
        /// </summary>
        public async Task ApplyPresetAsync(string characterId, string presetName)
        {
            if (!_clothingPresets.ContainsKey(presetName))
            {
                throw new PresetNotFoundException($"Preset bulunamadı: {presetName}");
            }

            var preset = _clothingPresets[presetName];

            // Mevcut giysileri temizle;
            var currentClothing = GetActiveClothing(characterId);
            foreach (var clothing in currentClothing.Values)
            {
                await UnequipClothingAsync(characterId, clothing.Category);
            }

            // Preset'teki giysileri uygula;
            foreach (var clothingItem in preset.ClothingItems)
            {
                await EquipClothingAsync(characterId, clothingItem);
            }

            _logger.LogInformation($"{characterId} karakterine {presetName} preset'i uygulandı");
        }

        /// <summary>
        /// Özel giysi preset'i oluşturur;
        /// </summary>
        public async Task<string> CreatePresetAsync(string presetName, IEnumerable<ClothingItem> clothingItems)
        {
            var preset = new ClothingPreset;
            {
                Name = presetName,
                ClothingItems = clothingItems.ToList(),
                CreatedAt = DateTime.UtcNow,
                Category = DeterminePresetCategory(clothingItems)
            };

            _clothingPresets[presetName] = preset;
            await SavePresetAsync(preset);

            _logger.LogDebug($"Yeni preset oluşturuldu: {presetName}");

            return presetName;
        }

        /// <summary>
        /// Giysi kombinasyonu önerir;
        /// </summary>
        public async Task<IEnumerable<ClothingCombination>> SuggestOutfitsAsync(
            string characterId,
            OutfitContext context)
        {
            var characterStyle = await GetCharacterStyleProfileAsync(characterId);
            var availableClothing = await GetAvailableClothingAsync(characterId);

            var suggestions = new List<ClothingCombination>();

            // Bağlama göre filtrele;
            var filteredClothing = FilterClothingByContext(availableClothing, context);

            // Stil profiline göre kombinasyonlar oluştur;
            var combinations = GenerateStyleCombinations(filteredClothing, characterStyle);

            // Fizibilite kontrolü;
            foreach (var combination in combinations)
            {
                if (await IsCombinationFeasibleAsync(characterId, combination))
                {
                    // Estetik puanı hesapla;
                    var aestheticScore = CalculateAestheticScore(combination, characterStyle);

                    // Uygunluk puanı hesapla;
                    var practicalityScore = CalculatePracticalityScore(combination, context);

                    suggestions.Add(new ClothingCombination;
                    {
                        Items = combination,
                        AestheticScore = aestheticScore,
                        PracticalityScore = practicalityScore,
                        OverallScore = (aestheticScore + practicalityScore) / 2,
                        ContextSuitability = CalculateContextSuitability(combination, context)
                    });
                }
            }

            return suggestions;
                .OrderByDescending(s => s.OverallScore)
                .Take(_config.MaxSuggestions);
        }

        private void ValidateSystemState()
        {
            if (!_isInitialized)
            {
                throw new ClothingSystemNotInitializedException(
                    "ClothingSystem başlatılmamış");
            }
        }

        private void ValidateCharacter(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
            {
                throw new ArgumentException("Karakter ID geçersiz", nameof(characterId));
            }
        }

        private string GetClothingKey(string characterId, string category)
        {
            return $"{characterId}:{category}";
        }

        private async Task<Dictionary<string, double>> GetCharacterMeasurementsAsync(string characterId)
        {
            // Karakter ölçülerini veritabanından veya servisten al;
            // Bu metod implementasyonu bağlama bağlıdır;
            return await Task.FromResult(new Dictionary<string, double>
            {
                ["chest"] = 95.0,
                ["waist"] = 80.0,
                ["hips"] = 100.0,
                ["height"] = 175.0;
            });
        }

        private async Task<FabricProperties> GetFabricPropertiesAsync(FabricType fabricType)
        {
            // Kumaş özelliklerini veritabanından al;
            return await Task.FromResult(new FabricProperties;
            {
                WeightPerSquareMeter = 0.2,
                Elasticity = 0.15,
                DragCoefficient = 0.05,
                WrinkleFactor = 0.3,
                ThermalConductivity = 0.04;
            });
        }

        private void OnClothingChanged(ClothingChangedEventArgs e)
        {
            ClothingChanged?.Invoke(this, e);
        }

        private void OnLayerUpdated(ClothingLayerUpdatedEventArgs e)
        {
            LayerUpdated?.Invoke(this, e);
        }

        public void Dispose()
        {
            _activeClothing.Clear();
            _clothingPresets.Clear();
            _isInitialized = false;
        }

        // Yardımcı metodlar ve özel sınıflar;
        private async Task LoadDefaultLayersAsync() { /* Implementasyon */ }
        private async Task InitializeClothingLibraryAsync() { /* Implementasyon */ }
        private async Task LoadPresetsAsync() { /* Implementasyon */ }
        private int GetLayerOrder(string category) { return 0; /* Implementasyon */ }
        private IEnumerable<ClothingItem> GetClothingInSameLayer(string characterId, int layerOrder)
        {
            return Enumerable.Empty<ClothingItem>(); /* Implementasyon */
        }
        private async Task<bool> CheckLayerConflictAsync(ClothingItem existing, ClothingItem newItem)
        {
            return await Task.FromResult(false); /* Implementasyon */
        }
        private async Task UpdateLayerVisibilityAsync(string characterId, ClothingItem clothing)
        { /* Implementasyon */ }
        private double CalculateStretchFactorAsync(FabricType fabricType) { return 1.0; /* Implementasyon */ }
        private FitQuality CalculateFitQuality(List<FitIssue> fitIssues) { return FitQuality.Perfect; /* Implementasyon */ }
        private async Task UpdateUVMapForMeasurementAsync(ClothingItem clothing, string dimension, double value)
        { /* Implementasyon */ }
        private async Task AdjustTextureMappingAsync(ClothingItem clothing, string characterId)
        { /* Implementasyon */ }
        private async Task RecalculatePhysicsPropertiesAsync(ClothingItem clothing)
        { /* Implementasyon */ }
        private async Task ApplyWeightAsync(ClothingItem clothing, double weight)
        { /* Implementasyon */ }
        private async Task ApplyElasticityAsync(ClothingItem clothing, double elasticity)
        { /* Implementasyon */ }
        private async Task ApplyDragCoefficientAsync(ClothingItem clothing, double dragCoefficient)
        { /* Implementasyon */ }
        private async Task ApplyWrinkleSimulationAsync(ClothingItem clothing, double wrinkleFactor)
        { /* Implementasyon */ }
        private async Task<List<MorphTarget>> CalculateRequiredMorphsAsync(string characterId, ClothingItem clothing)
        {
            return new List<MorphTarget>(); /* Implementasyon */
        }
        private async Task UpdateBlendShapesForClothingAsync(string characterId, ClothingItem clothing)
        { /* Implementasyon */ }
        private async Task ApplyColorAsync(ClothingItem clothing) { /* Implementasyon */ }
        private async Task ApplyTextureAsync(ClothingItem clothing) { /* Implementasyon */ }
        private async Task ApplyNormalMapAsync(ClothingItem clothing) { /* Implementasyon */ }
        private async Task ApplyMaterialMapsAsync(ClothingItem clothing) { /* Implementasyon */ }
        private async Task ApplyTransparencyAsync(ClothingItem clothing) { /* Implementasyon */ }
        private async Task RemoveFabricPhysicsAsync(ClothingItem clothing) { /* Implementasyon */ }
        private async Task ResetMorphAdjustmentsAsync(string characterId, string category)
        { /* Implementasyon */ }
        private async Task RemoveFromClothingLayerAsync(string characterId, string category)
        { /* Implementasyon */ }
        private async Task<StyleProfile> GetCharacterStyleProfileAsync(string characterId)
        {
            return new StyleProfile(); /* Implementasyon */
        }
        private async Task<List<ClothingItem>> GetAvailableClothingAsync(string characterId)
        {
            return new List<ClothingItem>(); /* Implementasyon */
        }
        private IEnumerable<ClothingItem> FilterClothingByContext(List<ClothingItem> clothing, OutfitContext context)
        {
            return clothing; /* Implementasyon */
        }
        private IEnumerable<List<ClothingItem>> GenerateStyleCombinations(
            IEnumerable<ClothingItem> clothing, StyleProfile styleProfile)
        {
            yield return new List<ClothingItem>(); /* Implementasyon */
        }
        private async Task<bool> IsCombinationFeasibleAsync(string characterId, List<ClothingItem> combination)
        {
            return await Task.FromResult(true); /* Implementasyon */
        }
        private double CalculateAestheticScore(List<ClothingItem> combination, StyleProfile styleProfile)
        {
            return 0.0; /* Implementasyon */
        }
        private double CalculatePracticalityScore(List<ClothingItem> combination, OutfitContext context)
        {
            return 0.0; /* Implementasyon */
        }
        private ContextSuitability CalculateContextSuitability(List<ClothingItem> combination, OutfitContext context)
        {
            return ContextSuitability.High; /* Implementasyon */
        }
        private async Task SavePresetAsync(ClothingPreset preset) { /* Implementasyon */ }
        private PresetCategory DeterminePresetCategory(IEnumerable<ClothingItem> clothingItems)
        {
            return PresetCategory.Casual; /* Implementasyon */
        }
    }

    #region Yardımcı Sınıflar ve Enum'lar;

    public enum ClothingAction;
    {
        Equipped,
        Removed,
        Modified,
        Adjusted;
    }

    public enum LayerAction;
    {
        Added,
        Removed,
        Updated,
        Reordered;
    }

    public enum FitIssueType;
    {
        TooTight,
        TooLoose,
        LengthMismatch,
        ProportionMismatch;
    }

    public enum FitQuality;
    {
        Perfect,
        Good,
        Acceptable,
        Poor,
        Unwearable;
    }

    public enum AdjustmentType;
    {
        Resize,
        Reshape,
        Relocate,
        Remap;
    }

    public enum ContextSuitability;
    {
        Perfect,
        High,
        Medium,
        Low,
        Unsuitable;
    }

    public enum PresetCategory;
    {
        Casual,
        Formal,
        Sport,
        Work,
        Ceremonial,
        Seasonal,
        Thematic;
    }

    public class ClothingItem;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public FabricType FabricType { get; set; }
        public Dictionary<string, double> Measurements { get; set; }
        public ColorConfiguration Color { get; set; }
        public TextureConfiguration Texture { get; set; }
        public MaterialProperties Material { get; set; }
        public List<string> CompatibleLayers { get; set; }
        public Dictionary<string, object> CustomProperties { get; set; }

        public ClothingItem Clone()
        {
            return new ClothingItem;
            {
                Id = this.Id,
                Name = this.Name,
                Category = this.Category,
                FabricType = this.FabricType,
                Measurements = new Dictionary<string, double>(this.Measurements),
                Color = this.Color?.Clone(),
                Texture = this.Texture?.Clone(),
                Material = this.Material?.Clone(),
                CompatibleLayers = new List<string>(this.CompatibleLayers),
                CustomProperties = new Dictionary<string, object>(this.CustomProperties)
            };
        }
    }

    public class ClothingPreset;
    {
        public string Name { get; set; }
        public List<ClothingItem> ClothingItems { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
        public PresetCategory Category { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ClothingResult;
    {
        public bool Success { get; set; }
        public ClothingItem ClothingItem { get; set; }
        public FitQuality FitQuality { get; set; }
        public List<ClothingAdjustment> AppliedAdjustments { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    public class ClothingFitResult;
    {
        public bool FitsProperly { get; set; }
        public FitQuality FitQuality { get; set; }
        public List<FitIssue> FitIssues { get; set; }
        public List<ClothingAdjustment> RequiredAdjustments { get; set; }
    }

    public class FitIssue;
    {
        public string Dimension { get; set; }
        public double Difference { get; set; }
        public FitIssueType IssueType { get; set; }
        public string Description { get; set; }
    }

    public class ClothingAdjustment;
    {
        public string Dimension { get; set; }
        public double AdjustmentAmount { get; set; }
        public AdjustmentType AdjustmentType { get; set; }
        public bool Applied { get; set; }
    }

    public class FabricProperties;
    {
        public double WeightPerSquareMeter { get; set; }
        public double Elasticity { get; set; }
        public double DragCoefficient { get; set; }
        public double WrinkleFactor { get; set; }
        public double ThermalConductivity { get; set; }
        public double WaterResistance { get; set; }
        public bool HasStretch => Elasticity > 0.1;
    }

    public class OutfitContext;
    {
        public string Occasion { get; set; }
        public string Season { get; set; }
        public string Weather { get; set; }
        public string Location { get; set; }
        public string Activity { get; set; }
        public TimeOfDay TimeOfDay { get; set; }
        public Dictionary<string, object> AdditionalContext { get; set; }
    }

    public class StyleProfile;
    {
        public string StyleName { get; set; }
        public List<string> PreferredColors { get; set; }
        public List<string> AvoidedColors { get; set; }
        public Dictionary<string, double> StyleWeights { get; set; }
        public List<string> FashionPreferences { get; set; }
    }

    public class ClothingCombination;
    {
        public List<ClothingItem> Items { get; set; }
        public double AestheticScore { get; set; }
        public double PracticalityScore { get; set; }
        public double OverallScore { get; set; }
        public ContextSuitability ContextSuitability { get; set; }
        public Dictionary<string, object> Metrics { get; set; }
    }

    public class ClothingChangedEventArgs : EventArgs;
    {
        public string CharacterId { get; set; }
        public ClothingItem ClothingItem { get; set; }
        public ClothingAction Action { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; }
    }

    public class ClothingLayerUpdatedEventArgs : EventArgs;
    {
        public string CharacterId { get; set; }
        public int Layer { get; set; }
        public ClothingItem ClothingItem { get; set; }
        public LayerAction Action { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ClothingValidator;
    {
        public async Task<ValidationResult> ValidateClothingAsync(ClothingItem clothing, string characterId)
        {
            var errors = new List<string>();

            // Temel validasyonlar;
            if (string.IsNullOrWhiteSpace(clothing.Id))
                errors.Add("Giysi ID boş olamaz");

            if (string.IsNullOrWhiteSpace(clothing.Category))
                errors.Add("Giysi kategorisi boş olamaz");

            if (clothing.Measurements == null || !clothing.Measurements.Any())
                errors.Add("Giysi ölçüleri tanımlanmalı");

            // Ölçü validasyonları;
            foreach (var measurement in clothing.Measurements)
            {
                if (measurement.Value <= 0)
                    errors.Add($"{measurement.Key} ölçüsü geçersiz: {measurement.Value}");
            }

            // Malzeme validasyonu;
            if (clothing.Material == null)
                errors.Add("Malzeme özellikleri tanımlanmalı");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; }
    }

    // Özel Exception sınıfları;
    public class ClothingSystemException : Exception
    {
        public ClothingSystemException(string message) : base(message) { }
        public ClothingSystemException(string message, Exception inner) : base(message, inner) { }
    }

    public class ClothingSystemInitializationException : ClothingSystemException;
    {
        public ClothingSystemInitializationException(string message) : base(message) { }
        public ClothingSystemInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ClothingValidationException : ClothingSystemException;
    {
        public ClothingValidationException(string message) : base(message) { }
        public ClothingValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ClothingEquipException : ClothingSystemException;
    {
        public ClothingEquipException(string message) : base(message) { }
        public ClothingEquipException(string message, Exception inner) : base(message, inner) { }
    }

    public class ClothingUnequipException : ClothingSystemException;
    {
        public ClothingUnequipException(string message) : base(message) { }
        public ClothingUnequipException(string message, Exception inner) : base(message, inner) { }
    }

    public class PresetNotFoundException : ClothingSystemException;
    {
        public PresetNotFoundException(string message) : base(message) { }
        public PresetNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class ClothingSystemNotInitializedException : ClothingSystemException;
    {
        public ClothingSystemNotInitializedException(string message) : base(message) { }
        public ClothingSystemNotInitializedException(string message, Exception inner) : base(message, inner) { }
    }

    // Diğer yardımcı sınıflar (kısaltılmış)
    public class FabricType { public bool HasStretch { get; set; } }
    public class MorphTarget { }
    public class ColorConfiguration { public ColorConfiguration Clone() => new(); }
    public class TextureConfiguration { public TextureConfiguration Clone() => new(); }
    public class MaterialProperties { public MaterialProperties Clone() => new(); }
    public enum TimeOfDay { Morning, Afternoon, Evening, Night }
    public class ClothingSystemConfig { public double FitTolerance { get; set; } = 2.0; public int MaxSuggestions { get; set; } = 10; }

    #endregion;
}
