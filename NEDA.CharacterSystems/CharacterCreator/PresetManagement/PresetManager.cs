using NEDA.AI.NaturalLanguage;
using NEDA.Configuration;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.SecurityModules.Encryption;
using NEDA.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
{
    /// <summary>
    /// Giysi preset'lerini yöneten sistem;
    /// </summary>
    public class PresetManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly FileService _fileService;
        private readonly CryptoEngine _cryptoEngine;
        private readonly AppConfig _config;

        private readonly Dictionary<string, ClothingPreset> _presetCache;
        private readonly Dictionary<string, PresetCategory> _categoryIndex;
        private readonly PresetValidator _validator;
        private readonly PresetSerializer _serializer;
        private bool _isInitialized;
        private readonly string _presetsDirectory;
        private readonly object _cacheLock = new object();

        public event EventHandler<PresetEventArgs> PresetCreated;
        public event EventHandler<PresetEventArgs> PresetUpdated;
        public event EventHandler<PresetEventArgs> PresetDeleted;
        public event EventHandler<PresetCategoryEventArgs> CategoryCreated;

        public PresetManager(ILogger logger, FileService fileService, CryptoEngine cryptoEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));

            _config = ConfigurationManager.GetSection<PresetManagerConfig>("PresetManager");
            _presetsDirectory = Path.Combine(_config.BaseDirectory, "Presets");

            _presetCache = new Dictionary<string, ClothingPreset>(StringComparer.OrdinalIgnoreCase);
            _categoryIndex = new Dictionary<string, PresetCategory>(StringComparer.OrdinalIgnoreCase);
            _validator = new PresetValidator();
            _serializer = new PresetSerializer(_cryptoEngine);

            InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("PresetManager başlatılıyor...");

                // Dizinleri oluştur;
                await CreateDirectoriesAsync();

                // Kategorileri yükle;
                await LoadCategoriesAsync();

                // Preset'leri yükle;
                await LoadAllPresetsAsync();

                // İndeksleri oluştur;
                await BuildIndexesAsync();

                _isInitialized = true;
                _logger.LogInformation($"PresetManager başlatıldı. {_presetCache.Count} preset yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"PresetManager başlatma hatası: {ex.Message}", ex);
                throw new PresetManagerInitializationException(
                    "PresetManager başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Yeni preset oluşturur;
        /// </summary>
        public async Task<ClothingPreset> CreatePresetAsync(PresetCreationRequest request)
        {
            ValidateManagerState();
            ValidateRequest(request);

            try
            {
                _logger.LogDebug($"Yeni preset oluşturuluyor: {request.Name}");

                // Benzersiz ID oluştur;
                var presetId = GeneratePresetId(request.Name, request.Creator);

                // Preset objesini oluştur;
                var preset = new ClothingPreset;
                {
                    Id = presetId,
                    Name = request.Name,
                    Description = request.Description,
                    ClothingItems = request.ClothingItems.ToList(),
                    Category = request.Category,
                    Creator = request.Creator,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Tags = request.Tags?.ToList() ?? new List<string>(),
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    Version = 1,
                    IsPublic = request.IsPublic,
                    Rating = 0.0,
                    UsageCount = 0;
                };

                // Validasyon;
                var validationResult = await _validator.ValidatePresetAsync(preset);
                if (!validationResult.IsValid)
                {
                    throw new PresetValidationException(
                        $"Preset validasyon hatası: {string.Join(", ", validationResult.Errors)}");
                }

                // Önbelleğe ekle;
                lock (_cacheLock)
                {
                    if (_presetCache.ContainsKey(presetId))
                    {
                        throw new PresetAlreadyExistsException($"Preset zaten mevcut: {presetId}");
                    }
                    _presetCache[presetId] = preset;
                }

                // Kategori indeksine ekle;
                await AddToCategoryIndexAsync(preset);

                // Dosya sistemine kaydet;
                await SavePresetToFileAsync(preset);

                // Versiyon geçmişini kaydet;
                await CreateVersionHistoryAsync(preset);

                // Etiket indeksini güncelle;
                await UpdateTagIndexAsync(preset);

                OnPresetCreated(new PresetEventArgs;
                {
                    Preset = preset,
                    Action = PresetAction.Created,
                    Timestamp = DateTime.UtcNow,
                    User = request.Creator;
                });

                _logger.LogInformation($"Preset oluşturuldu: {preset.Name} (ID: {presetId})");

                return preset;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset oluşturma hatası: {ex.Message}", ex);
                throw new PresetCreationException($"Preset oluşturulamadı: {request.Name}", ex);
            }
        }

        /// <summary>
        /// Mevcut preset'i günceller;
        /// </summary>
        public async Task<ClothingPreset> UpdatePresetAsync(string presetId, PresetUpdateRequest update)
        {
            ValidateManagerState();
            ValidatePresetId(presetId);

            try
            {
                _logger.LogDebug($"Preset güncelleniyor: {presetId}");

                // Preset'i getir;
                var existingPreset = await GetPresetByIdAsync(presetId);
                if (existingPreset == null)
                {
                    throw new PresetNotFoundException($"Preset bulunamadı: {presetId}");
                }

                // Güncelleme izin kontrolü;
                await ValidateUpdatePermissionAsync(existingPreset, update.Updater);

                // Yeni versiyon oluştur;
                var updatedPreset = existingPreset.Clone();
                updatedPreset.Name = update.Name ?? existingPreset.Name;
                updatedPreset.Description = update.Description ?? existingPreset.Description;
                updatedPreset.ClothingItems = update.ClothingItems?.ToList() ?? existingPreset.ClothingItems;
                updatedPreset.Category = update.Category ?? existingPreset.Category;
                updatedPreset.Tags = update.Tags?.ToList() ?? existingPreset.Tags;
                updatedPreset.Metadata = MergeMetadata(existingPreset.Metadata, update.Metadata);
                updatedPreset.UpdatedAt = DateTime.UtcNow;
                updatedPreset.Version = existingPreset.Version + 1;
                updatedPreset.UpdatedBy = update.Updater;

                // Validasyon;
                var validationResult = await _validator.ValidatePresetAsync(updatedPreset);
                if (!validationResult.IsValid)
                {
                    throw new PresetValidationException(
                        $"Preset güncelleme validasyon hatası: {string.Join(", ", validationResult.Errors)}");
                }

                // Önbelleği güncelle;
                lock (_cacheLock)
                {
                    _presetCache[presetId] = updatedPreset;
                }

                // Kategori indeksini güncelle;
                await UpdateCategoryIndexAsync(existingPreset, updatedPreset);

                // Dosya sistemine kaydet;
                await SavePresetToFileAsync(updatedPreset);

                // Versiyon geçmişine ekle;
                await AddToVersionHistoryAsync(updatedPreset);

                // Etiket indeksini güncelle;
                await UpdateTagIndexAsync(updatedPreset, existingPreset.Tags);

                OnPresetUpdated(new PresetEventArgs;
                {
                    Preset = updatedPreset,
                    Action = PresetAction.Updated,
                    Timestamp = DateTime.UtcNow,
                    User = update.Updater,
                    PreviousVersion = existingPreset.Version;
                });

                _logger.LogInformation($"Preset güncellendi: {updatedPreset.Name} (v{updatedPreset.Version})");

                return updatedPreset;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset güncelleme hatası: {ex.Message}", ex);
                throw new PresetUpdateException($"Preset güncellenemedi: {presetId}", ex);
            }
        }

        /// <summary>
        /// Preset'i siler;
        /// </summary>
        public async Task<bool> DeletePresetAsync(string presetId, string deleter, bool permanent = false)
        {
            ValidateManagerState();
            ValidatePresetId(presetId);

            try
            {
                _logger.LogDebug($"Preset siliniyor: {presetId}");

                // Preset'i getir;
                var preset = await GetPresetByIdAsync(presetId);
                if (preset == null)
                {
                    throw new PresetNotFoundException($"Preset bulunamadı: {presetId}");
                }

                // Silme izin kontrolü;
                await ValidateDeletePermissionAsync(preset, deleter);

                if (permanent)
                {
                    // Kalıcı silme;
                    await PermanentDeletePresetAsync(preset);
                }
                else;
                {
                    // Çöp kutusuna taşı;
                    await MoveToTrashAsync(preset, deleter);
                }

                // Önbellekten kaldır;
                lock (_cacheLock)
                {
                    _presetCache.Remove(presetId);
                }

                // Kategori indeksinden kaldır;
                await RemoveFromCategoryIndexAsync(preset);

                // Etiket indeksinden kaldır;
                await RemoveFromTagIndexAsync(preset);

                OnPresetDeleted(new PresetEventArgs;
                {
                    Preset = preset,
                    Action = PresetAction.Deleted,
                    Timestamp = DateTime.UtcNow,
                    User = deleter,
                    IsPermanent = permanent;
                });

                _logger.LogInformation($"Preset silindi: {preset.Name} (Kalıcı: {permanent})");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset silme hatası: {ex.Message}", ex);
                throw new PresetDeleteException($"Preset silinemedi: {presetId}", ex);
            }
        }

        /// <summary>
        /// ID'ye göre preset getirir;
        /// </summary>
        public async Task<ClothingPreset> GetPresetByIdAsync(string presetId)
        {
            ValidateManagerState();
            ValidatePresetId(presetId);

            try
            {
                // Önbellekten getir;
                lock (_cacheLock)
                {
                    if (_presetCache.TryGetValue(presetId, out var cachedPreset))
                    {
                        return cachedPreset;
                    }
                }

                // Önbellekte yoksa dosyadan yükle;
                var preset = await LoadPresetFromFileAsync(presetId);
                if (preset != null)
                {
                    lock (_cacheLock)
                    {
                        _presetCache[presetId] = preset;
                    }
                }

                return preset;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset getirme hatası: {ex.Message}", ex);
                throw new PresetRetrievalException($"Preset getirilemedi: {presetId}", ex);
            }
        }

        /// <summary>
        /// İsme göre preset arar;
        /// </summary>
        public async Task<IEnumerable<ClothingPreset>> SearchPresetsAsync(PresetSearchCriteria criteria)
        {
            ValidateManagerState();

            try
            {
                _logger.LogDebug($"Preset aranıyor: {criteria.Query}");

                var results = new List<ClothingPreset>();

                // Önbellekte ara;
                lock (_cacheLock)
                {
                    var query = criteria.Query?.ToLowerInvariant() ?? string.Empty;

                    results = _presetCache.Values;
                        .Where(preset => MatchesSearchCriteria(preset, criteria))
                        .ToList();
                }

                // Sayfalama;
                if (criteria.PageSize > 0)
                {
                    results = results;
                        .Skip((criteria.PageNumber - 1) * criteria.PageSize)
                        .Take(criteria.PageSize)
                        .ToList();
                }

                // Sıralama;
                results = SortPresets(results, criteria.SortBy, criteria.SortDirection);

                // Kullanım sayısını artır;
                if (criteria.IncrementUsage)
                {
                    foreach (var preset in results)
                    {
                        await IncrementUsageCountAsync(preset.Id);
                    }
                }

                _logger.LogDebug($"{results.Count} preset bulundu");

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset arama hatası: {ex.Message}", ex);
                throw new PresetSearchException("Preset arama işlemi başarısız", ex);
            }
        }

        /// <summary>
        /// Kategoriye göre preset'leri getirir;
        /// </summary>
        public async Task<IEnumerable<ClothingPreset>> GetPresetsByCategoryAsync(string category, bool includeSubcategories = false)
        {
            ValidateManagerState();

            try
            {
                var presets = new List<ClothingPreset>();

                if (_categoryIndex.TryGetValue(category, out var categoryInfo))
                {
                    lock (_cacheLock)
                    {
                        presets = _presetCache.Values;
                            .Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }

                    if (includeSubcategories && categoryInfo.Subcategories.Any())
                    {
                        foreach (var subcategory in categoryInfo.Subcategories)
                        {
                            var subcategoryPresets = await GetPresetsByCategoryAsync(subcategory, true);
                            presets.AddRange(subcategoryPresets);
                        }
                    }
                }

                return presets.OrderByDescending(p => p.UsageCount).ThenBy(p => p.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kategoriye göre preset getirme hatası: {ex.Message}", ex);
                throw new PresetRetrievalException($"Kategori preset'leri getirilemedi: {category}", ex);
            }
        }

        /// <summary>
        /// Etikete göre preset'leri getirir;
        /// </summary>
        public async Task<IEnumerable<ClothingPreset>> GetPresetsByTagAsync(string tag)
        {
            ValidateManagerState();

            try
            {
                var tagIndex = await LoadTagIndexAsync();
                if (tagIndex.TryGetValue(tag.ToLowerInvariant(), out var presetIds))
                {
                    var presets = new List<ClothingPreset>();

                    lock (_cacheLock)
                    {
                        foreach (var presetId in presetIds)
                        {
                            if (_presetCache.TryGetValue(presetId, out var preset))
                            {
                                presets.Add(preset);
                            }
                        }
                    }

                    return presets.OrderByDescending(p => p.Rating).ThenBy(p => p.Name);
                }

                return Enumerable.Empty<ClothingPreset>();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Etikete göre preset getirme hatası: {ex.Message}", ex);
                throw new PresetRetrievalException($"Etiket preset'leri getirilemedi: {tag}", ex);
            }
        }

        /// <summary>
        /// Preset'i klonlar;
        /// </summary>
        public async Task<ClothingPreset> ClonePresetAsync(string presetId, string newName, string cloner)
        {
            ValidateManagerState();

            try
            {
                var original = await GetPresetByIdAsync(presetId);
                if (original == null)
                {
                    throw new PresetNotFoundException($"Klonlanacak preset bulunamadı: {presetId}");
                }

                var cloneRequest = new PresetCreationRequest;
                {
                    Name = newName,
                    Description = $"{original.Description} (Klon)",
                    ClothingItems = original.ClothingItems,
                    Category = original.Category,
                    Creator = cloner,
                    Tags = original.Tags,
                    Metadata = new Dictionary<string, object>(original.Metadata)
                    {
                        ["ClonedFrom"] = presetId,
                        ["OriginalCreator"] = original.Creator,
                        ["CloneDate"] = DateTime.UtcNow;
                    },
                    IsPublic = original.IsPublic;
                };

                var clonedPreset = await CreatePresetAsync(cloneRequest);

                // Orijinal preset'in kullanım sayısını artır;
                await IncrementCloneCountAsync(presetId);

                _logger.LogInformation($"Preset klonlandı: {original.Name} -> {clonedPreset.Name}");

                return clonedPreset;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset klonlama hatası: {ex.Message}", ex);
                throw new PresetCloneException($"Preset klonlanamadı: {presetId}", ex);
            }
        }

        /// <summary>
        /// Preset'i dışa aktarır;
        /// </summary>
        public async Task<byte[]> ExportPresetAsync(string presetId, ExportFormat format)
        {
            ValidateManagerState();

            try
            {
                var preset = await GetPresetByIdAsync(presetId);
                if (preset == null)
                {
                    throw new PresetNotFoundException($"Dışa aktarılacak preset bulunamadı: {presetId}");
                }

                byte[] exportData;

                switch (format)
                {
                    case ExportFormat.Json:
                        exportData = await _serializer.SerializeToJsonAsync(preset);
                        break;
                    case ExportFormat.Xml:
                        exportData = await _serializer.SerializeToXmlAsync(preset);
                        break;
                    case ExportFormat.Binary:
                        exportData = await _serializer.SerializeToBinaryAsync(preset);
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen format: {format}");
                }

                // Export geçmişini kaydet;
                await LogExportAsync(presetId, format);

                // Kullanım sayısını artır;
                await IncrementExportCountAsync(presetId);

                return exportData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset dışa aktarma hatası: {ex.Message}", ex);
                throw new PresetExportException($"Preset dışa aktarılamadı: {presetId}", ex);
            }
        }

        /// <summary>
        /// Preset'i içe aktarır;
        /// </summary>
        public async Task<ClothingPreset> ImportPresetAsync(byte[] data, ImportOptions options)
        {
            ValidateManagerState();

            try
            {
                ClothingPreset preset;

                switch (options.Format)
                {
                    case ExportFormat.Json:
                        preset = await _serializer.DeserializeFromJsonAsync(data);
                        break;
                    case ExportFormat.Xml:
                        preset = await _serializer.DeserializeFromXmlAsync(data);
                        break;
                    case ExportFormat.Binary:
                        preset = await _serializer.DeserializeFromBinaryAsync(data);
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen format: {options.Format}");
                }

                // İçe aktarma ayarlarını uygula;
                if (options.OverrideName)
                {
                    preset.Name = options.NewName ?? preset.Name;
                }

                if (options.OverrideCreator)
                {
                    preset.Creator = options.Importer;
                }

                // Benzersiz ID oluştur;
                preset.Id = GeneratePresetId(preset.Name, preset.Creator);

                // Kaydet;
                var savedPreset = await CreatePresetAsync(new PresetCreationRequest;
                {
                    Name = preset.Name,
                    Description = preset.Description,
                    ClothingItems = preset.ClothingItems,
                    Category = preset.Category,
                    Creator = preset.Creator,
                    Tags = preset.Tags,
                    Metadata = preset.Metadata,
                    IsPublic = preset.IsPublic;
                });

                // İçe aktarma geçmişini kaydet;
                await LogImportAsync(savedPreset.Id, options);

                return savedPreset;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset içe aktarma hatası: {ex.Message}", ex);
                throw new PresetImportException("Preset içe aktarılamadı", ex);
            }
        }

        /// <summary>
        /// Preset'i derecelendirir;
        /// </summary>
        public async Task<double> RatePresetAsync(string presetId, string rater, int rating, string comment = null)
        {
            ValidateManagerState();

            try
            {
                var preset = await GetPresetByIdAsync(presetId);
                if (preset == null)
                {
                    throw new PresetNotFoundException($"Derecelendirilecek preset bulunamadı: {presetId}");
                }

                // Derecelendirme validasyonu;
                if (rating < 1 || rating > 5)
                {
                    throw new ArgumentException("Derecelendirme 1-5 arasında olmalıdır", nameof(rating));
                }

                // Aynı kullanıcının önceki derecelendirmesini kontrol et;
                var existingRating = await GetUserRatingAsync(presetId, rater);
                var isUpdate = existingRating != null;

                // Derecelendirmeyi kaydet;
                await SaveRatingAsync(presetId, rater, rating, comment, isUpdate);

                // Ortalama derecelendirmeyi güncelle;
                var newAverage = await CalculateAverageRatingAsync(presetId);
                preset.Rating = newAverage;

                // Önbelleği güncelle;
                lock (_cacheLock)
                {
                    _presetCache[presetId] = preset;
                }

                // Dosyayı güncelle;
                await SavePresetToFileAsync(preset);

                // Derecelendirme event'ini tetikle;
                OnPresetRated(new PresetRatingEventArgs;
                {
                    PresetId = presetId,
                    Rater = rater,
                    Rating = rating,
                    Comment = comment,
                    IsUpdate = isUpdate,
                    NewAverage = newAverage,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Preset derecelendirildi: {preset.Name} - {rating} yıldız");

                return newAverage;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset derecelendirme hatası: {ex.Message}", ex);
                throw new PresetRatingException($"Preset derecelendirilemedi: {presetId}", ex);
            }
        }

        /// <summary>
        /// Preset kullanım istatistiklerini getirir;
        /// </summary>
        public async Task<PresetStatistics> GetPresetStatisticsAsync(string presetId)
        {
            ValidateManagerState();

            try
            {
                var stats = new PresetStatistics;
                {
                    PresetId = presetId,
                    UsageCount = await GetUsageCountAsync(presetId),
                    CloneCount = await GetCloneCountAsync(presetId),
                    ExportCount = await GetExportCountAsync(presetId),
                    AverageRating = await GetAverageRatingAsync(presetId),
                    RatingCount = await GetRatingCountAsync(presetId),
                    LastUsed = await GetLastUsageDateAsync(presetId),
                    Created = await GetCreationDateAsync(presetId)
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preset istatistik getirme hatası: {ex.Message}", ex);
                throw new PresetStatisticsException($"Preset istatistikleri getirilemedi: {presetId}", ex);
            }
        }

        /// <summary>
        /// Kategori oluşturur;
        /// </summary>
        public async Task<PresetCategory> CreateCategoryAsync(CategoryCreationRequest request)
        {
            ValidateManagerState();

            try
            {
                if (_categoryIndex.ContainsKey(request.Name))
                {
                    throw new CategoryAlreadyExistsException($"Kategori zaten mevcut: {request.Name}");
                }

                var category = new PresetCategory;
                {
                    Id = GenerateCategoryId(request.Name),
                    Name = request.Name,
                    Description = request.Description,
                    ParentCategory = request.ParentCategory,
                    Icon = request.Icon,
                    Color = request.Color,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = request.Creator,
                    IsSystemCategory = request.IsSystemCategory,
                    Metadata = request.Metadata ?? new Dictionary<string, object>()
                };

                // Kategoriyi kaydet;
                await SaveCategoryAsync(category);

                // İndekse ekle;
                _categoryIndex[category.Name] = category;

                // Alt kategori ilişkisini güncelle;
                if (!string.IsNullOrEmpty(category.ParentCategory))
                {
                    await AddSubcategoryAsync(category.ParentCategory, category.Name);
                }

                OnCategoryCreated(new PresetCategoryEventArgs;
                {
                    Category = category,
                    Action = CategoryAction.Created,
                    Timestamp = DateTime.UtcNow,
                    User = request.Creator;
                });

                _logger.LogInformation($"Kategori oluşturuldu: {category.Name}");

                return category;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kategori oluşturma hatası: {ex.Message}", ex);
                throw new CategoryCreationException($"Kategori oluşturulamadı: {request.Name}", ex);
            }
        }

        /// <summary>
        /// Tüm kategorileri getirir;
        /// </summary>
        public IEnumerable<PresetCategory> GetAllCategories()
        {
            ValidateManagerState();
            return _categoryIndex.Values.OrderBy(c => c.Name);
        }

        private void ValidateManagerState()
        {
            if (!_isInitialized)
            {
                throw new PresetManagerNotInitializedException("PresetManager başlatılmamış");
            }
        }

        private void ValidateRequest(PresetCreationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("Preset ismi boş olamaz", nameof(request.Name));

            if (string.IsNullOrWhiteSpace(request.Creator))
                throw new ArgumentException("Oluşturucu boş olamaz", nameof(request.Creator));

            if (request.ClothingItems == null || !request.ClothingItems.Any())
                throw new ArgumentException("En az bir giysi öğesi gereklidir", nameof(request.ClothingItems));
        }

        private void ValidatePresetId(string presetId)
        {
            if (string.IsNullOrWhiteSpace(presetId))
                throw new ArgumentException("Preset ID boş olamaz", nameof(presetId));
        }

        private string GeneratePresetId(string name, string creator)
        {
            var normalizedName = name.ToLowerInvariant().Replace(" ", "_");
            var normalizedCreator = creator.ToLowerInvariant().Replace(" ", "_");
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            return $"{normalizedCreator}_{normalizedName}_{timestamp}";
        }

        private string GenerateCategoryId(string name)
        {
            return name.ToLowerInvariant().Replace(" ", "_");
        }

        private bool MatchesSearchCriteria(ClothingPreset preset, PresetSearchCriteria criteria)
        {
            if (string.IsNullOrWhiteSpace(criteria.Query))
                return true;

            var query = criteria.Query.ToLowerInvariant();

            return preset.Name.ToLowerInvariant().Contains(query) ||
                   preset.Description?.ToLowerInvariant().Contains(query) == true ||
                   preset.Tags?.Any(tag => tag.ToLowerInvariant().Contains(query)) == true ||
                   preset.Category.ToLowerInvariant().Contains(query) ||
                   preset.Creator.ToLowerInvariant().Contains(query);
        }

        private List<ClothingPreset> SortPresets(List<ClothingPreset> presets, string sortBy, SortDirection direction)
        {
            Func<ClothingPreset, IComparable> keySelector = sortBy?.ToLowerInvariant() switch;
            {
                "name" => p => p.Name,
                "rating" => p => p.Rating,
                "usagecount" => p => p.UsageCount,
                "createdat" => p => p.CreatedAt,
                "updatedat" => p => p.UpdatedAt,
                _ => p => p.Name;
            };

            return direction == SortDirection.Descending;
                ? presets.OrderByDescending(keySelector).ToList()
                : presets.OrderBy(keySelector).ToList();
        }

        private Dictionary<string, object> MergeMetadata(
            Dictionary<string, object> existing,
            Dictionary<string, object> updates)
        {
            var merged = new Dictionary<string, object>(existing);

            if (updates != null)
            {
                foreach (var kvp in updates)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            return merged;
        }

        private async Task CreateDirectoriesAsync()
        {
            var directories = new[]
            {
                _presetsDirectory,
                Path.Combine(_presetsDirectory, "Categories"),
                Path.Combine(_presetsDirectory, "Versions"),
                Path.Combine(_presetsDirectory, "Trash"),
                Path.Combine(_presetsDirectory, "Exports"),
                Path.Combine(_presetsDirectory, "Imports"),
                Path.Combine(_presetsDirectory, "Ratings"),
                Path.Combine(_presetsDirectory, "Statistics")
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug($"Dizin oluşturuldu: {directory}");
                }
            }
        }

        private async Task LoadCategoriesAsync()
        {
            var categoriesFile = Path.Combine(_presetsDirectory, "Categories", "categories.json");

            if (File.Exists(categoriesFile))
            {
                var json = await File.ReadAllTextAsync(categoriesFile);
                var categories = JsonConvert.DeserializeObject<List<PresetCategory>>(json);

                foreach (var category in categories)
                {
                    _categoryIndex[category.Name] = category;
                }
            }
            else;
            {
                // Varsayılan kategorileri oluştur;
                await CreateDefaultCategoriesAsync();
            }
        }

        private async Task CreateDefaultCategoriesAsync()
        {
            var defaultCategories = new[]
            {
                new PresetCategory;
                {
                    Id = "casual",
                    Name = "Casual",
                    Description = "Günlük kullanım için giysiler",
                    Icon = "tshirt",
                    Color = "#4CAF50",
                    IsSystemCategory = true;
                },
                new PresetCategory;
                {
                    Id = "formal",
                    Name = "Formal",
                    Description = "Resmi etkinlikler için giysiler",
                    Icon = "suit",
                    Color = "#2196F3",
                    IsSystemCategory = true;
                },
                new PresetCategory;
                {
                    Id = "sport",
                    Name = "Sport",
                    Description = "Spor aktiviteleri için giysiler",
                    Icon = "running",
                    Color = "#FF9800",
                    IsSystemCategory = true;
                },
                new PresetCategory;
                {
                    Id = "work",
                    Name = "Work",
                    Description = "İş ortamı için giysiler",
                    Icon = "briefcase",
                    Color = "#795548",
                    IsSystemCategory = true;
                }
            };

            foreach (var category in defaultCategories)
            {
                category.CreatedAt = DateTime.UtcNow;
                category.CreatedBy = "System";
                _categoryIndex[category.Name] = category;
                await SaveCategoryAsync(category);
            }
        }

        private async Task LoadAllPresetsAsync()
        {
            var presetFiles = Directory.GetFiles(_presetsDirectory, "*.preset", SearchOption.AllDirectories);

            foreach (var file in presetFiles)
            {
                try
                {
                    var preset = await _serializer.DeserializeFromFileAsync(file);
                    if (preset != null)
                    {
                        _presetCache[preset.Id] = preset;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Preset yükleme hatası ({file}): {ex.Message}");
                }
            }
        }

        private async Task BuildIndexesAsync()
        {
            // Kategori indeksini güncelle;
            foreach (var preset in _presetCache.Values)
            {
                if (!_categoryIndex.ContainsKey(preset.Category))
                {
                    // Otomatik kategori oluştur;
                    await CreateCategoryAsync(new CategoryCreationRequest;
                    {
                        Name = preset.Category,
                        Description = $"Otomatik oluşturulan kategori: {preset.Category}",
                        Creator = "System",
                        IsSystemCategory = false;
                    });
                }
            }
        }

        private async Task SavePresetToFileAsync(ClothingPreset preset)
        {
            var fileName = Path.Combine(_presetsDirectory, $"{preset.Id}.preset");
            await _serializer.SerializeToFileAsync(preset, fileName);
        }

        private async Task<ClothingPreset> LoadPresetFromFileAsync(string presetId)
        {
            var fileName = Path.Combine(_presetsDirectory, $"{presetId}.preset");

            if (File.Exists(fileName))
            {
                return await _serializer.DeserializeFromFileAsync(fileName);
            }

            return null;
        }

        private async Task AddToCategoryIndexAsync(ClothingPreset preset)
        {
            if (!_categoryIndex.ContainsKey(preset.Category))
            {
                await CreateCategoryAsync(new CategoryCreationRequest;
                {
                    Name = preset.Category,
                    Description = $"Otomatik oluşturulan kategori: {preset.Category}",
                    Creator = preset.Creator,
                    IsSystemCategory = false;
                });
            }
        }

        private async Task UpdateCategoryIndexAsync(ClothingPreset oldPreset, ClothingPreset newPreset)
        {
            if (!oldPreset.Category.Equals(newPreset.Category, StringComparison.OrdinalIgnoreCase))
            {
                // Eski kategoriden kaldır, yeni kategoriye ekle;
                await RemoveFromCategoryIndexAsync(oldPreset);
                await AddToCategoryIndexAsync(newPreset);
            }
        }

        private async Task RemoveFromCategoryIndexAsync(ClothingPreset preset)
        {
            // Kategori indeksinde preset sayısını azalt;
            // (Gerçek implementasyonda kategori-preset ilişkisi yönetimi)
        }

        private async Task<Dictionary<string, List<string>>> LoadTagIndexAsync()
        {
            var tagIndexFile = Path.Combine(_presetsDirectory, "tag_index.json");

            if (File.Exists(tagIndexFile))
            {
                var json = await File.ReadAllTextAsync(tagIndexFile);
                return JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            }

            return new Dictionary<string, List<string>>();
        }

        private async Task UpdateTagIndexAsync(ClothingPreset preset, List<string> oldTags = null)
        {
            var tagIndex = await LoadTagIndexAsync();

            // Eski etiketleri kaldır;
            if (oldTags != null)
            {
                foreach (var tag in oldTags)
                {
                    var normalizedTag = tag.ToLowerInvariant();
                    if (tagIndex.ContainsKey(normalizedTag))
                    {
                        tagIndex[normalizedTag].Remove(preset.Id);
                        if (tagIndex[normalizedTag].Count == 0)
                        {
                            tagIndex.Remove(normalizedTag);
                        }
                    }
                }
            }

            // Yeni etiketleri ekle;
            foreach (var tag in preset.Tags)
            {
                var normalizedTag = tag.ToLowerInvariant();
                if (!tagIndex.ContainsKey(normalizedTag))
                {
                    tagIndex[normalizedTag] = new List<string>();
                }

                if (!tagIndex[normalizedTag].Contains(preset.Id))
                {
                    tagIndex[normalizedTag].Add(preset.Id);
                }
            }

            // Tag indeksini kaydet;
            var tagIndexFile = Path.Combine(_presetsDirectory, "tag_index.json");
            var json = JsonConvert.SerializeObject(tagIndex, Formatting.Indented);
            await File.WriteAllTextAsync(tagIndexFile, json);
        }

        private async Task RemoveFromTagIndexAsync(ClothingPreset preset)
        {
            await UpdateTagIndexAsync(preset, preset.Tags);

            // Preset ID'sini tüm etiketlerden kaldır;
            var tagIndex = await LoadTagIndexAsync();

            foreach (var tagList in tagIndex.Values)
            {
                tagList.Remove(preset.Id);
            }

            // Boş etiketleri temizle;
            var emptyTags = tagIndex.Where(kvp => !kvp.Value.Any()).Select(kvp => kvp.Key).ToList();
            foreach (var emptyTag in emptyTags)
            {
                tagIndex.Remove(emptyTag);
            }

            // Güncellenmiş indeksi kaydet;
            var tagIndexFile = Path.Combine(_presetsDirectory, "tag_index.json");
            var json = JsonConvert.SerializeObject(tagIndex, Formatting.Indented);
            await File.WriteAllTextAsync(tagIndexFile, json);
        }

        private async Task CreateVersionHistoryAsync(ClothingPreset preset)
        {
            var versionDir = Path.Combine(_presetsDirectory, "Versions", preset.Id);
            Directory.CreateDirectory(versionDir);

            var versionFile = Path.Combine(versionDir, $"v{preset.Version}.json");
            var versionData = new;
            {
                Preset = preset,
                Created = DateTime.UtcNow,
                Version = preset.Version;
            };

            var json = JsonConvert.SerializeObject(versionData, Formatting.Indented);
            await File.WriteAllTextAsync(versionFile, json);
        }

        private async Task AddToVersionHistoryAsync(ClothingPreset preset)
        {
            await CreateVersionHistoryAsync(preset);
        }

        private async Task PermanentDeletePresetAsync(ClothingPreset preset)
        {
            // Preset dosyasını sil;
            var presetFile = Path.Combine(_presetsDirectory, $"{preset.Id}.preset");
            if (File.Exists(presetFile))
            {
                File.Delete(presetFile);
            }

            // Versiyon geçmişini sil;
            var versionDir = Path.Combine(_presetsDirectory, "Versions", preset.Id);
            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, true);
            }

            // İstatistikleri sil;
            var statsFile = Path.Combine(_presetsDirectory, "Statistics", $"{preset.Id}.json");
            if (File.Exists(statsFile))
            {
                File.Delete(statsFile);
            }
        }

        private async Task MoveToTrashAsync(ClothingPreset preset, string deleter)
        {
            var trashDir = Path.Combine(_presetsDirectory, "Trash");
            var trashFile = Path.Combine(trashDir, $"{preset.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}.trash");

            var trashData = new;
            {
                Preset = preset,
                DeletedBy = deleter,
                DeletedAt = DateTime.UtcNow,
                OriginalPath = Path.Combine(_presetsDirectory, $"{preset.Id}.preset")
            };

            var json = JsonConvert.SerializeObject(trashData, Formatting.Indented);
            await File.WriteAllTextAsync(trashFile, json);

            // Orijinal dosyayı sil;
            var originalFile = Path.Combine(_presetsDirectory, $"{preset.Id}.preset");
            if (File.Exists(originalFile))
            {
                File.Delete(originalFile);
            }
        }

        private async Task SaveCategoryAsync(PresetCategory category)
        {
            var categoriesDir = Path.Combine(_presetsDirectory, "Categories");
            var categoryFile = Path.Combine(categoriesDir, $"{category.Id}.json");

            var json = JsonConvert.SerializeObject(category, Formatting.Indented);
            await File.WriteAllTextAsync(categoryFile, json);
        }

        private async Task AddSubcategoryAsync(string parentCategory, string subcategory)
        {
            if (_categoryIndex.TryGetValue(parentCategory, out var parent))
            {
                if (!parent.Subcategories.Contains(subcategory))
                {
                    parent.Subcategories.Add(subcategory);
                    await SaveCategoryAsync(parent);
                }
            }
        }

        // Yardımcı metodlar (kısaltılmış)
        private async Task ValidateUpdatePermissionAsync(ClothingPreset preset, string updater) { /* Implementasyon */ }
        private async Task ValidateDeletePermissionAsync(ClothingPreset preset, string deleter) { /* Implementasyon */ }
        private async Task LogExportAsync(string presetId, ExportFormat format) { /* Implementasyon */ }
        private async Task LogImportAsync(string presetId, ImportOptions options) { /* Implementasyon */ }
        private async Task IncrementUsageCountAsync(string presetId) { /* Implementasyon */ }
        private async Task IncrementCloneCountAsync(string presetId) { /* Implementasyon */ }
        private async Task IncrementExportCountAsync(string presetId) { /* Implementasyon */ }
        private async Task<int> GetUsageCountAsync(string presetId) { return await Task.FromResult(0); /* Implementasyon */ }
        private async Task<int> GetCloneCountAsync(string presetId) { return await Task.FromResult(0); /* Implementasyon */ }
        private async Task<int> GetExportCountAsync(string presetId) { return await Task.FromResult(0); /* Implementasyon */ }
        private async Task<double> GetAverageRatingAsync(string presetId) { return await Task.FromResult(0.0); /* Implementasyon */ }
        private async Task<int> GetRatingCountAsync(string presetId) { return await Task.FromResult(0); /* Implementasyon */ }
        private async Task<DateTime?> GetLastUsageDateAsync(string presetId) { return await Task.FromResult<DateTime?>(null); /* Implementasyon */ }
        private async Task<DateTime> GetCreationDateAsync(string presetId) { return await Task.FromResult(DateTime.UtcNow); /* Implementasyon */ }
        private async Task<UserRating> GetUserRatingAsync(string presetId, string userId) { return await Task.FromResult<UserRating>(null); /* Implementasyon */ }
        private async Task SaveRatingAsync(string presetId, string userId, int rating, string comment, bool isUpdate) { /* Implementasyon */ }
        private async Task<double> CalculateAverageRatingAsync(string presetId) { return await Task.FromResult(0.0); /* Implementasyon */ }

        private void OnPresetCreated(PresetEventArgs e) => PresetCreated?.Invoke(this, e);
        private void OnPresetUpdated(PresetEventArgs e) => PresetUpdated?.Invoke(this, e);
        private void OnPresetDeleted(PresetEventArgs e) => PresetDeleted?.Invoke(this, e);
        private void OnCategoryCreated(PresetCategoryEventArgs e) => CategoryCreated?.Invoke(this, e);
        private void OnPresetRated(PresetRatingEventArgs e) { /* Event handler */ }

        public void Dispose()
        {
            _presetCache.Clear();
            _categoryIndex.Clear();
            _isInitialized = false;
        }

        #region Yardımcı Sınıflar ve Enum'lar;

        public enum PresetAction;
        {
            Created,
            Updated,
            Deleted,
            Cloned,
            Exported,
            Imported,
            Rated;
        }

        public enum CategoryAction;
        {
            Created,
            Updated,
            Deleted,
            Merged;
        }

        public enum ExportFormat;
        {
            Json,
            Xml,
            Binary;
        }

        public enum SortDirection;
        {
            Ascending,
            Descending;
        }

        public class PresetCreationRequest;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public IEnumerable<ClothingItem> ClothingItems { get; set; }
            public string Category { get; set; }
            public string Creator { get; set; }
            public IEnumerable<string> Tags { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
            public bool IsPublic { get; set; } = true;
        }

        public class PresetUpdateRequest;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public IEnumerable<ClothingItem> ClothingItems { get; set; }
            public string Category { get; set; }
            public IEnumerable<string> Tags { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
            public string Updater { get; set; }
        }

        public class PresetSearchCriteria;
        {
            public string Query { get; set; }
            public string Category { get; set; }
            public IEnumerable<string> Tags { get; set; }
            public string Creator { get; set; }
            public bool? IsPublic { get; set; }
            public double? MinRating { get; set; }
            public double? MaxRating { get; set; }
            public int? MinUsageCount { get; set; }
            public DateTime? CreatedAfter { get; set; }
            public DateTime? CreatedBefore { get; set; }
            public string SortBy { get; set; } = "name";
            public SortDirection SortDirection { get; set; } = SortDirection.Ascending;
            public int PageNumber { get; set; } = 1;
            public int PageSize { get; set; } = 20;
            public bool IncrementUsage { get; set; } = false;
        }

        public class ImportOptions;
        {
            public ExportFormat Format { get; set; }
            public bool OverrideName { get; set; }
            public string NewName { get; set; }
            public bool OverrideCreator { get; set; }
            public string Importer { get; set; }
            public bool MergeMetadata { get; set; } = true;
            public ConflictResolution ConflictResolution { get; set; } = ConflictResolution.Rename;
        }

        public enum ConflictResolution;
        {
            Overwrite,
            Rename,
            Skip,
            Merge;
        }

        public class PresetCategory;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string ParentCategory { get; set; }
            public List<string> Subcategories { get; set; } = new List<string>();
            public string Icon { get; set; }
            public string Color { get; set; }
            public DateTime CreatedAt { get; set; }
            public string CreatedBy { get; set; }
            public bool IsSystemCategory { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        public class CategoryCreationRequest;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string ParentCategory { get; set; }
            public string Icon { get; set; }
            public string Color { get; set; }
            public string Creator { get; set; }
            public bool IsSystemCategory { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
        }

        public class PresetEventArgs : EventArgs;
        {
            public ClothingPreset Preset { get; set; }
            public PresetAction Action { get; set; }
            public DateTime Timestamp { get; set; }
            public string User { get; set; }
            public int? PreviousVersion { get; set; }
            public bool IsPermanent { get; set; }
            public Dictionary<string, object> AdditionalInfo { get; set; }
        }

        public class PresetCategoryEventArgs : EventArgs;
        {
            public PresetCategory Category { get; set; }
            public CategoryAction Action { get; set; }
            public DateTime Timestamp { get; set; }
            public string User { get; set; }
            public Dictionary<string, object> AdditionalInfo { get; set; }
        }

        public class PresetRatingEventArgs : EventArgs;
        {
            public string PresetId { get; set; }
            public string Rater { get; set; }
            public int Rating { get; set; }
            public string Comment { get; set; }
            public bool IsUpdate { get; set; }
            public double NewAverage { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class PresetStatistics;
        {
            public string PresetId { get; set; }
            public int UsageCount { get; set; }
            public int CloneCount { get; set; }
            public int ExportCount { get; set; }
            public double AverageRating { get; set; }
            public int RatingCount { get; set; }
            public DateTime? LastUsed { get; set; }
            public DateTime Created { get; set; }
            public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
        }

        public class UserRating;
        {
            public string PresetId { get; set; }
            public string UserId { get; set; }
            public int Rating { get; set; }
            public string Comment { get; set; }
            public DateTime RatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
        }

        public class PresetValidator;
        {
            public async Task<ValidationResult> ValidatePresetAsync(ClothingPreset preset)
            {
                var errors = new List<string>();

                if (string.IsNullOrWhiteSpace(preset.Name))
                    errors.Add("Preset ismi boş olamaz");

                if (preset.Name.Length > 100)
                    errors.Add("Preset ismi 100 karakterden uzun olamaz");

                if (string.IsNullOrWhiteSpace(preset.Category))
                    errors.Add("Kategori belirtilmelidir");

                if (preset.ClothingItems == null || !preset.ClothingItems.Any())
                    errors.Add("En az bir giysi öğesi gereklidir");

                if (preset.ClothingItems.Count() > 50)
                    errors.Add("Maksimum 50 giysi öğesi eklenebilir");

                if (preset.Tags != null && preset.Tags.Count() > 20)
                    errors.Add("Maksimum 20 etiket eklenebilir");

                return new ValidationResult;
                {
                    IsValid = errors.Count == 0,
                    Errors = errors;
                };
            }
        }

        public class PresetSerializer;
        {
            private readonly CryptoEngine _cryptoEngine;

            public PresetSerializer(CryptoEngine cryptoEngine)
            {
                _cryptoEngine = cryptoEngine;
            }

            public async Task<byte[]> SerializeToJsonAsync(ClothingPreset preset)
            {
                var json = JsonConvert.SerializeObject(preset, Formatting.Indented);
                return System.Text.Encoding.UTF8.GetBytes(json);
            }

            public async Task<ClothingPreset> DeserializeFromJsonAsync(byte[] data)
            {
                var json = System.Text.Encoding.UTF8.GetString(data);
                return JsonConvert.DeserializeObject<ClothingPreset>(json);
            }

            public async Task SerializeToFileAsync(ClothingPreset preset, string filePath)
            {
                var json = await SerializeToJsonAsync(preset);
                await File.WriteAllBytesAsync(filePath, json);
            }

            public async Task<ClothingPreset> DeserializeFromFileAsync(string filePath)
            {
                var data = await File.ReadAllBytesAsync(filePath);
                return await DeserializeFromJsonAsync(data);
            }

            public async Task<byte[]> SerializeToXmlAsync(ClothingPreset preset)
            {
                // XML serialization implementasyonu;
                throw new NotImplementedException();
            }

            public async Task<ClothingPreset> DeserializeFromXmlAsync(byte[] data)
            {
                // XML deserialization implementasyonu;
                throw new NotImplementedException();
            }

            public async Task<byte[]> SerializeToBinaryAsync(ClothingPreset preset)
            {
                // Binary serialization implementasyonu;
                throw new NotImplementedException();
            }

            public async Task<ClothingPreset> DeserializeFromBinaryAsync(byte[] data)
            {
                // Binary deserialization implementasyonu;
                throw new NotImplementedException();
            }
        }

        // Exception Sınıfları;
        public class PresetManagerException : Exception
        {
            public PresetManagerException(string message) : base(message) { }
            public PresetManagerException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetManagerInitializationException : PresetManagerException;
        {
            public PresetManagerInitializationException(string message) : base(message) { }
            public PresetManagerInitializationException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetManagerNotInitializedException : PresetManagerException;
        {
            public PresetManagerNotInitializedException(string message) : base(message) { }
            public PresetManagerNotInitializedException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetValidationException : PresetManagerException;
        {
            public PresetValidationException(string message) : base(message) { }
            public PresetValidationException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetAlreadyExistsException : PresetManagerException;
        {
            public PresetAlreadyExistsException(string message) : base(message) { }
            public PresetAlreadyExistsException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetNotFoundException : PresetManagerException;
        {
            public PresetNotFoundException(string message) : base(message) { }
            public PresetNotFoundException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetCreationException : PresetManagerException;
        {
            public PresetCreationException(string message) : base(message) { }
            public PresetCreationException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetUpdateException : PresetManagerException;
        {
            public PresetUpdateException(string message) : base(message) { }
            public PresetUpdateException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetDeleteException : PresetManagerException;
        {
            public PresetDeleteException(string message) : base(message) { }
            public PresetDeleteException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetRetrievalException : PresetManagerException;
        {
            public PresetRetrievalException(string message) : base(message) { }
            public PresetRetrievalException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetSearchException : PresetManagerException;
        {
            public PresetSearchException(string message) : base(message) { }
            public PresetSearchException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetCloneException : PresetManagerException;
        {
            public PresetCloneException(string message) : base(message) { }
            public PresetCloneException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetExportException : PresetManagerException;
        {
            public PresetExportException(string message) : base(message) { }
            public PresetExportException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetImportException : PresetManagerException;
        {
            public PresetImportException(string message) : base(message) { }
            public PresetImportException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetRatingException : PresetManagerException;
        {
            public PresetRatingException(string message) : base(message) { }
            public PresetRatingException(string message, Exception inner) : base(message, inner) { }
        }

        public class PresetStatisticsException : PresetManagerException;
        {
            public PresetStatisticsException(string message) : base(message) { }
            public PresetStatisticsException(string message, Exception inner) : base(message, inner) { }
        }

        public class CategoryAlreadyExistsException : PresetManagerException;
        {
            public CategoryAlreadyExistsException(string message) : base(message) { }
            public CategoryAlreadyExistsException(string message, Exception inner) : base(message, inner) { }
        }

        public class CategoryCreationException : PresetManagerException;
        {
            public CategoryCreationException(string message) : base(message) { }
            public CategoryCreationException(string message, Exception inner) : base(message, inner) { }
        }

        // Config sınıfı;
        public class PresetManagerConfig;
        {
            public string BaseDirectory { get; set; } = "Data/Presets";
            public int MaxPresetsPerUser { get; set; } = 1000;
            public int MaxPresetSizeMB { get; set; } = 10;
            public bool EnableEncryption { get; set; } = true;
            public bool EnableCompression { get; set; } = true;
            public int AutoBackupIntervalHours { get; set; } = 24;
            public int MaxVersionsPerPreset { get; set; } = 50;
            public int TrashRetentionDays { get; set; } = 30;
        }

        // ClothingPreset sınıfı (ClothingSystem.cs'den genişletilmiş)
        public class ClothingPreset;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public List<ClothingItem> ClothingItems { get; set; }
            public string Category { get; set; }
            public string Creator { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public string UpdatedBy { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
            public int Version { get; set; } = 1;
            public bool IsPublic { get; set; } = true;
            public double Rating { get; set; }
            public int UsageCount { get; set; }
            public int CloneCount { get; set; }
            public int ExportCount { get; set; }

            public ClothingPreset Clone()
            {
                return new ClothingPreset;
                {
                    Id = this.Id,
                    Name = this.Name,
                    Description = this.Description,
                    ClothingItems = this.ClothingItems?.Select(i => i.Clone()).ToList(),
                    Category = this.Category,
                    Creator = this.Creator,
                    CreatedAt = this.CreatedAt,
                    UpdatedAt = this.UpdatedAt,
                    UpdatedBy = this.UpdatedBy,
                    Tags = new List<string>(this.Tags),
                    Metadata = new Dictionary<string, object>(this.Metadata),
                    Version = this.Version,
                    IsPublic = this.IsPublic,
                    Rating = this.Rating,
                    UsageCount = this.UsageCount,
                    CloneCount = this.CloneCount,
                    ExportCount = this.ExportCount;
                };
            }
        }

        #endregion;
    }
}
