// NEDA.CharacterSystems/CharacterCreator/OutfitSystems/TemplateLibrary.cs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.ContentCreation.AssetPipeline;
using NEDA.EngineIntegration.AssetManager;
using NEDA.Services.FileService;
using NEDA.Services.NotificationService;

namespace NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
{
    /// <summary>
    /// Outfit template kütüphanesi - Template'leri yönetir, kategorize eder, filtreler ve öneriler sunar;
    /// </summary>
    public class TemplateLibrary : ITemplateLibrary, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IFileManager _fileManager;
        private readonly IAssetManager _assetManager;
        private readonly IAppConfig _appConfig;
        private readonly INotificationManager _notificationManager;

        private readonly Dictionary<string, OutfitTemplate> _templates;
        private readonly Dictionary<string, TemplateCategory> _categories;
        private readonly Dictionary<string, List<string>> _templateIndex;
        private readonly TemplateCache _templateCache;
        private readonly TemplateSearchEngine _searchEngine;

        private bool _isInitialized;
        private bool _isIndexing;
        private readonly string _templateDatabasePath;
        private readonly string _templateStoragePath;
        private readonly SemaphoreSlim _templateAccessSemaphore;
        private readonly Timer _autoSaveTimer;
        private readonly JsonSerializerOptions _jsonOptions;

        private const int AUTO_SAVE_INTERVAL = 300000; // 5 dakika;
        private const int MAX_TEMPLATE_CACHE_SIZE = 1000;

        // Eventler;
        public event EventHandler<TemplateAddedEventArgs> TemplateAdded;
        public event EventHandler<TemplateUpdatedEventArgs> TemplateUpdated;
        public event EventHandler<TemplateRemovedEventArgs> TemplateRemoved;
        public event EventHandler<LibraryIndexedEventArgs> LibraryIndexed;

        /// <summary>
        /// TemplateLibrary constructor;
        /// </summary>
        public TemplateLibrary(
            ILogger<TemplateLibrary> logger,
            IFileManager fileManager,
            IAssetManager assetManager,
            IAppConfig appConfig,
            INotificationManager notificationManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _assetManager = assetManager ?? throw new ArgumentNullException(nameof(assetManager));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));

            _templates = new Dictionary<string, OutfitTemplate>();
            _categories = new Dictionary<string, TemplateCategory>();
            _templateIndex = new Dictionary<string, List<string>>();
            _templateCache = new TemplateCache(MAX_TEMPLATE_CACHE_SIZE);
            _searchEngine = new TemplateSearchEngine();

            _templateDatabasePath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:TemplateDatabasePath") ?? "Data/Templates",
                "Templates.db");
            _templateStoragePath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:TemplateStoragePath") ?? "Data/Templates/Storage",
                "Templates");

            _templateAccessSemaphore = new SemaphoreSlim(1, 1);

            // JSON serialization ayarları;
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() }
            };

            // Otomatik kaydetme timer'ı;
            _autoSaveTimer = new Timer(AutoSaveCallback, null, AUTO_SAVE_INTERVAL, AUTO_SAVE_INTERVAL);

            _logger.LogInformation("TemplateLibrary initialized");
        }

        /// <summary>
        /// TemplateLibrary'yi başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Initializing TemplateLibrary...");

                // Storage dizinlerini oluştur;
                await EnsureDirectoriesExistAsync(cancellationToken);

                // Template database'ini yükle;
                await LoadTemplateDatabaseAsync(cancellationToken);

                // Kategorileri yükle;
                await LoadCategoriesAsync(cancellationToken);

                // Search engine'i başlat;
                await _searchEngine.InitializeAsync(_templates.Values, cancellationToken);

                // Cache'i ısıt;
                await WarmUpCacheAsync(cancellationToken);

                _isInitialized = true;
                _logger.LogInformation($"TemplateLibrary initialized successfully with {_templates.Count} templates");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize TemplateLibrary");
                throw new TemplateLibraryException("Failed to initialize TemplateLibrary", ex);
            }
        }

        /// <summary>
        /// Template ekler;
        /// </summary>
        public async Task<OutfitTemplate> AddTemplateAsync(
            OutfitTemplate template,
            TemplateImportOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (template == null)
                throw new ArgumentNullException(nameof(template));

            if (string.IsNullOrEmpty(template.Id))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(template));

            try
            {
                await _templateAccessSemaphore.WaitAsync(cancellationToken);

                options ??= TemplateImportOptions.Default;

                // ID kontrolü;
                if (_templates.ContainsKey(template.Id))
                {
                    if (options.OverwriteExisting)
                    {
                        _logger.LogWarning($"Template {template.Id} already exists, overwriting");
                        await RemoveTemplateInternalAsync(template.Id, false, cancellationToken);
                    }
                    else;
                    {
                        throw new TemplateAlreadyExistsException($"Template with ID {template.Id} already exists");
                    }
                }

                // Template validasyonu;
                ValidateTemplate(template);

                // Asset'leri kontrol et/yükle;
                await ProcessTemplateAssetsAsync(template, options, cancellationToken);

                // Metadata'yi güncelle;
                UpdateTemplateMetadata(template);

                // Template'i kaydet;
                await SaveTemplateToStorageAsync(template, cancellationToken);

                // Memory'ye ekle;
                _templates[template.Id] = template;
                _templateCache.Add(template.Id, template);

                // Index'i güncelle;
                UpdateTemplateIndex(template);

                // Search engine'i güncelle;
                _searchEngine.AddTemplate(template);

                _logger.LogInformation($"Template added: {template.Name} ({template.Id})");

                // Event tetikle;
                TemplateAdded?.Invoke(this, new TemplateAddedEventArgs;
                {
                    TemplateId = template.Id,
                    Template = template,
                    AddedTime = DateTime.UtcNow;
                });

                // Bildirim gönder;
                await SendNotificationAsync($"Template added: {template.Name}",
                    NotificationType.Info, cancellationToken);

                return template;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add template {template.Id}");
                throw new TemplateAddException($"Failed to add template: {ex.Message}", ex);
            }
            finally
            {
                _templateAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Template günceller;
        /// </summary>
        public async Task<OutfitTemplate> UpdateTemplateAsync(
            string templateId,
            TemplateUpdate update,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));

            if (update == null)
                throw new ArgumentNullException(nameof(update));

            try
            {
                await _templateAccessSemaphore.WaitAsync(cancellationToken);

                // Template'i bul;
                if (!_templates.TryGetValue(templateId, out var existingTemplate))
                {
                    // Storage'dan yükle;
                    existingTemplate = await LoadTemplateFromStorageAsync(templateId, cancellationToken);
                    if (existingTemplate == null)
                    {
                        throw new TemplateNotFoundException($"Template {templateId} not found");
                    }
                    _templates[templateId] = existingTemplate;
                }

                // Template'i klonla ve güncelle;
                var updatedTemplate = existingTemplate.Clone();
                ApplyTemplateUpdate(updatedTemplate, update);

                // Validasyon;
                ValidateTemplate(updatedTemplate);

                // Değişiklikleri kontrol et;
                var changes = DetectTemplateChanges(existingTemplate, updatedTemplate);

                // Template'i kaydet;
                await SaveTemplateToStorageAsync(updatedTemplate, cancellationToken);

                // Memory'yi güncelle;
                _templates[templateId] = updatedTemplate;
                _templateCache.Update(templateId, updatedTemplate);

                // Index'i güncelle;
                UpdateTemplateIndex(updatedTemplate);

                // Search engine'i güncelle;
                _searchEngine.UpdateTemplate(updatedTemplate);

                _logger.LogInformation($"Template updated: {updatedTemplate.Name} ({templateId})");

                // Event tetikle;
                TemplateUpdated?.Invoke(this, new TemplateUpdatedEventArgs;
                {
                    TemplateId = templateId,
                    OldTemplate = existingTemplate,
                    NewTemplate = updatedTemplate,
                    Changes = changes,
                    UpdatedTime = DateTime.UtcNow;
                });

                return updatedTemplate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update template {templateId}");
                throw new TemplateUpdateException($"Failed to update template: {ex.Message}", ex);
            }
            finally
            {
                _templateAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Template siler;
        /// </summary>
        public async Task<bool> RemoveTemplateAsync(
            string templateId,
            bool removeAssets = false,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));

            try
            {
                return await RemoveTemplateInternalAsync(templateId, removeAssets, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove template {templateId}");
                throw new TemplateRemoveException($"Failed to remove template: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Template ID'ye göre getirir;
        /// </summary>
        public async Task<OutfitTemplate> GetTemplateAsync(
            string templateId,
            bool loadFromCache = true,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));

            try
            {
                // Cache'den getir;
                if (loadFromCache)
                {
                    var cachedTemplate = _templateCache.Get(templateId);
                    if (cachedTemplate != null)
                    {
                        return cachedTemplate;
                    }
                }

                // Memory'den getir;
                if (_templates.TryGetValue(templateId, out var template))
                {
                    _templateCache.Add(templateId, template);
                    return template;
                }

                // Storage'dan yükle;
                template = await LoadTemplateFromStorageAsync(templateId, cancellationToken);
                if (template != null)
                {
                    _templates[templateId] = template;
                    _templateCache.Add(templateId, template);
                }

                return template;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get template {templateId}");
                throw new TemplateLoadException($"Failed to load template: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Tüm template'leri getirir (filtreleme ile)
        /// </summary>
        public async Task<TemplateCollection> GetAllTemplatesAsync(
            TemplateFilter filter = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                filter ??= TemplateFilter.Default;

                // Cache'den getir (eğer tüm template'ler cache'deyse)
                if (_templateCache.IsComplete && filter.IsDefault)
                {
                    var cachedTemplates = _templateCache.GetAll();
                    return new TemplateCollection;
                    {
                        Templates = cachedTemplates.ToList(),
                        TotalCount = cachedTemplates.Count,
                        Filter = filter;
                    };
                }

                // Filtreleme yap;
                IEnumerable<OutfitTemplate> filteredTemplates = _templates.Values;

                // Kategori filtresi;
                if (filter.Categories != null && filter.Categories.Any())
                {
                    filteredTemplates = filteredTemplates;
                        .Where(t => filter.Categories.Contains(t.Category));
                }

                // Tag filtresi;
                if (filter.Tags != null && filter.Tags.Any())
                {
                    filteredTemplates = filteredTemplates;
                        .Where(t => t.Tags != null && filter.Tags.All(tag => t.Tags.Contains(tag)));
                }

                // Arama filtresi;
                if (!string.IsNullOrEmpty(filter.SearchQuery))
                {
                    var searchResults = await _searchEngine.SearchAsync(
                        filter.SearchQuery,
                        filter.MaxSearchResults,
                        cancellationToken);

                    filteredTemplates = filteredTemplates;
                        .Where(t => searchResults.Contains(t.Id));
                }

                // Sıralama;
                filteredTemplates = SortTemplates(filteredTemplates, filter.SortBy, filter.SortOrder);

                // Sayfalama;
                var totalCount = filteredTemplates.Count();
                if (filter.PageSize > 0 && filter.PageNumber > 0)
                {
                    filteredTemplates = filteredTemplates;
                        .Skip((filter.PageNumber - 1) * filter.PageSize)
                        .Take(filter.PageSize);
                }

                // Sonuçları topla;
                var templates = filteredTemplates.ToList();

                return new TemplateCollection;
                {
                    Templates = templates,
                    TotalCount = totalCount,
                    PageNumber = filter.PageNumber,
                    PageSize = filter.PageSize,
                    Filter = filter;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all templates");
                throw new TemplateQueryException($"Failed to query templates: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Template kategorisi ekler;
        /// </summary>
        public async Task<TemplateCategory> AddCategoryAsync(
            TemplateCategory category,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (category == null)
                throw new ArgumentNullException(nameof(category));

            if (string.IsNullOrEmpty(category.Id))
                throw new ArgumentException("Category ID cannot be null or empty", nameof(category));

            try
            {
                await _templateAccessSemaphore.WaitAsync(cancellationToken);

                if (_categories.ContainsKey(category.Id))
                {
                    throw new CategoryAlreadyExistsException($"Category with ID {category.Id} already exists");
                }

                // Kategoriyi kaydet;
                _categories[category.Id] = category;
                await SaveCategoriesAsync(cancellationToken);

                _logger.LogInformation($"Category added: {category.Name} ({category.Id})");

                return category;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add category {category.Id}");
                throw new CategoryAddException($"Failed to add category: {ex.Message}", ex);
            }
            finally
            {
                _templateAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Tüm kategorileri getirir;
        /// </summary>
        public Task<Dictionary<string, TemplateCategory>> GetAllCategoriesAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            return Task.FromResult(new Dictionary<string, TemplateCategory>(_categories));
        }

        /// <summary>
        /// Template araması yapar;
        /// </summary>
        public async Task<TemplateSearchResult> SearchTemplatesAsync(
            string query,
            SearchOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(query))
                throw new ArgumentException("Search query cannot be null or empty", nameof(query));

            try
            {
                options ??= SearchOptions.Default;

                _logger.LogInformation($"Searching templates with query: {query}");

                // Search engine ile arama yap;
                var searchResults = await _searchEngine.SearchAsync(
                    query,
                    options.MaxResults,
                    cancellationToken);

                // Sonuçları getir;
                var templates = new List<OutfitTemplate>();
                foreach (var templateId in searchResults)
                {
                    var template = await GetTemplateAsync(templateId, true, cancellationToken);
                    if (template != null)
                    {
                        templates.Add(template);
                    }
                }

                // İlgililik skorlarını hesapla;
                var scoredResults = CalculateRelevanceScores(templates, query, options);

                return new TemplateSearchResult;
                {
                    Query = query,
                    Results = scoredResults,
                    TotalResults = searchResults.Count,
                    SearchTime = DateTime.UtcNow,
                    Options = options;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to search templates with query: {query}");
                throw new TemplateSearchException($"Failed to search templates: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Template önerileri getirir;
        /// </summary>
        public async Task<List<TemplateRecommendation>> GetRecommendationsAsync(
            string referenceTemplateId = null,
            RecommendationCriteria criteria = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                criteria ??= RecommendationCriteria.Default;

                List<OutfitTemplate> candidateTemplates;

                if (!string.IsNullOrEmpty(referenceTemplateId))
                {
                    // Referans template'e göre öneri;
                    var referenceTemplate = await GetTemplateAsync(referenceTemplateId, true, cancellationToken);
                    if (referenceTemplate == null)
                    {
                        throw new TemplateNotFoundException($"Reference template {referenceTemplateId} not found");
                    }

                    candidateTemplates = await FindSimilarTemplatesAsync(referenceTemplate, criteria, cancellationToken);
                }
                else;
                {
                    // Popüler template'ler;
                    candidateTemplates = await GetPopularTemplatesAsync(criteria.MaxResults, cancellationToken);
                }

                // Önerileri oluştur;
                var recommendations = new List<TemplateRecommendation>();
                foreach (var template in candidateTemplates)
                {
                    var score = CalculateRecommendationScore(template, criteria);
                    recommendations.Add(new TemplateRecommendation;
                    {
                        Template = template,
                        Score = score,
                        Reason = GenerateRecommendationReason(template, criteria),
                        Confidence = CalculateConfidenceLevel(score)
                    });
                }

                // Skora göre sırala;
                recommendations = recommendations;
                    .OrderByDescending(r => r.Score)
                    .Take(criteria.MaxResults)
                    .ToList();

                return recommendations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get template recommendations");
                throw new RecommendationException($"Failed to get recommendations: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Template'i dosyadan içe aktarır;
        /// </summary>
        public async Task<OutfitTemplate> ImportTemplateAsync(
            string filePath,
            TemplateImportOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Template file not found: {filePath}");

            try
            {
                options ??= TemplateImportOptions.Default;

                _logger.LogInformation($"Importing template from: {filePath}");

                // Dosyayı oku;
                var json = await File.ReadAllTextAsync(filePath);

                // Deserialize et;
                var template = JsonSerializer.Deserialize<OutfitTemplate>(json, _jsonOptions);
                if (template == null)
                {
                    throw new TemplateImportException($"Failed to deserialize template from file: {filePath}");
                }

                // Asset'leri işle;
                await ProcessImportedTemplateAssetsAsync(template, filePath, options, cancellationToken);

                // Template'i ekle;
                var importedTemplate = await AddTemplateAsync(template, options, cancellationToken);

                _logger.LogInformation($"Template imported successfully: {importedTemplate.Name}");

                return importedTemplate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to import template from {filePath}");
                throw new TemplateImportException($"Failed to import template: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Template'i dosyaya dışa aktarır;
        /// </summary>
        public async Task<string> ExportTemplateAsync(
            string templateId,
            string exportPath = null,
            ExportOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));

            try
            {
                options ??= ExportOptions.Default;

                // Template'i getir;
                var template = await GetTemplateAsync(templateId, true, cancellationToken);
                if (template == null)
                {
                    throw new TemplateNotFoundException($"Template {templateId} not found");
                }

                // Export path belirle;
                if (string.IsNullOrEmpty(exportPath))
                {
                    exportPath = Path.Combine(
                        _templateStoragePath,
                        "Exports",
                        $"{template.Name}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.template");
                }

                // Asset'leri işle;
                if (options.IncludeAssets)
                {
                    await ProcessTemplateAssetsForExportAsync(template, exportPath, options, cancellationToken);
                }

                // Template'i serialize et;
                var json = JsonSerializer.Serialize(template, _jsonOptions);

                // Dosyaya yaz;
                await File.WriteAllTextAsync(exportPath, json);

                _logger.LogInformation($"Template exported to: {exportPath}");

                return exportPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to export template {templateId}");
                throw new TemplateExportException($"Failed to export template: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Template library'yi indeksler (arama performansı için)
        /// </summary>
        public async Task<bool> ReindexLibraryAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (_isIndexing)
            {
                _logger.LogWarning("Library indexing already in progress");
                return false;
            }

            try
            {
                _isIndexing = true;

                _logger.LogInformation("Starting library reindexing...");

                // Mevcut index'i temizle;
                _templateIndex.Clear();
                _searchEngine.ClearIndex();

                // Tüm template'leri indeksle;
                int indexedCount = 0;
                foreach (var template in _templates.Values)
                {
                    UpdateTemplateIndex(template);
                    _searchEngine.AddTemplate(template);
                    indexedCount++;

                    if (cancellationToken.IsCancellationRequested)
                        break;
                }

                // Search engine'i optimize et;
                await _searchEngine.OptimizeAsync(cancellationToken);

                _logger.LogInformation($"Library reindexing completed. Indexed {indexedCount} templates.");

                // Event tetikle;
                LibraryIndexed?.Invoke(this, new LibraryIndexedEventArgs;
                {
                    IndexedCount = indexedCount,
                    IndexingTime = DateTime.UtcNow,
                    Success = true;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library reindexing failed");

                LibraryIndexed?.Invoke(this, new LibraryIndexedEventArgs;
                {
                    IndexedCount = 0,
                    IndexingTime = DateTime.UtcNow,
                    Success = false,
                    Error = ex.Message;
                });

                throw new IndexingException($"Library reindexing failed: {ex.Message}", ex);
            }
            finally
            {
                _isIndexing = false;
            }
        }

        /// <summary>
        /// Template library istatistiklerini getirir;
        /// </summary>
        public TemplateLibraryStats GetLibraryStats()
        {
            ValidateInitialized();

            var stats = new TemplateLibraryStats;
            {
                TotalTemplates = _templates.Count,
                TotalCategories = _categories.Count,
                CacheHitRate = _templateCache.HitRate,
                CacheSize = _templateCache.Size,
                IndexSize = _templateIndex.Sum(kvp => kvp.Value.Count),
                SearchEngineStats = _searchEngine.GetStats(),
                LastIndexTime = DateTime.UtcNow,
                MemoryUsage = CalculateMemoryUsage()
            };

            // Kategori dağılımı;
            stats.CategoryDistribution = _templates.Values;
                .GroupBy(t => t.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            // Tag dağılımı;
            var allTags = _templates.Values;
                .Where(t => t.Tags != null)
                .SelectMany(t => t.Tags)
                .GroupBy(tag => tag)
                .ToDictionary(g => g.Key, g => g.Count());

            stats.TagDistribution = allTags;

            return stats;
        }

        /// <summary>
        /// Template library'yi yedekler;
        /// </summary>
        public async Task<string> BackupLibraryAsync(
            string backupPath = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                if (string.IsNullOrEmpty(backupPath))
                {
                    backupPath = Path.Combine(
                        _templateStoragePath,
                        "Backups",
                        $"backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");
                }

                _logger.LogInformation($"Creating library backup to: {backupPath}");

                // Backup oluştur;
                var backupResult = await _fileManager.CreateBackupAsync(
                    new List<string> { _templateDatabasePath, _templateStoragePath },
                    backupPath,
                    cancellationToken);

                if (!backupResult.Success)
                {
                    throw new BackupException($"Backup failed: {backupResult.ErrorMessage}");
                }

                _logger.LogInformation($"Library backup created successfully: {backupPath}");

                return backupPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library backup failed");
                throw new BackupException($"Library backup failed: {ex.Message}", ex);
            }
        }

        #region Private Methods;

        private async Task EnsureDirectoriesExistAsync(CancellationToken cancellationToken)
        {
            var directories = new[]
            {
                Path.GetDirectoryName(_templateDatabasePath),
                _templateStoragePath,
                Path.Combine(_templateStoragePath, "Exports"),
                Path.Combine(_templateStoragePath, "Backups"),
                Path.Combine(_templateStoragePath, "Assets")
            };

            foreach (var directory in directories)
            {
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogInformation($"Created directory: {directory}");
                }
            }

            await Task.CompletedTask;
        }

        private async Task LoadTemplateDatabaseAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_templateDatabasePath))
            {
                _logger.LogWarning($"Template database not found at {_templateDatabasePath}. Creating new database.");
                await SaveTemplateDatabaseAsync(cancellationToken);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_templateDatabasePath);
                var database = JsonSerializer.Deserialize<TemplateDatabase>(json, _jsonOptions);

                if (database?.Templates != null)
                {
                    // Template'leri yükle;
                    foreach (var templateEntry in database.Templates)
                    {
                        try
                        {
                            var template = await LoadTemplateFromStorageAsync(templateEntry.Id, cancellationToken);
                            if (template != null)
                            {
                                _templates[template.Id] = template;
                                _templateCache.Add(template.Id, template);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to load template {templateEntry.Id} from database");
                        }
                    }

                    _logger.LogInformation($"Loaded {_templates.Count} templates from database");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load template database");
                throw;
            }
        }

        private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
        {
            var categoriesPath = Path.Combine(
                Path.GetDirectoryName(_templateDatabasePath),
                "Categories.json");

            if (!File.Exists(categoriesPath))
            {
                _logger.LogWarning($"Categories file not found at {categoriesPath}");
                await CreateDefaultCategoriesAsync(cancellationToken);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(categoriesPath);
                var categories = JsonSerializer.Deserialize<List<TemplateCategory>>(json, _jsonOptions);

                if (categories != null)
                {
                    foreach (var category in categories)
                    {
                        _categories[category.Id] = category;
                    }

                    _logger.LogInformation($"Loaded {categories.Count} categories");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load categories");
                await CreateDefaultCategoriesAsync(cancellationToken);
            }
        }

        private async Task CreateDefaultCategoriesAsync(CancellationToken cancellationToken)
        {
            var defaultCategories = new[]
            {
                new TemplateCategory { Id = "casual", Name = "Casual", Description = "Casual outfits" },
                new TemplateCategory { Id = "formal", Name = "Formal", Description = "Formal outfits" },
                new TemplateCategory { Id = "sports", Name = "Sports", Description = "Sports outfits" },
                new TemplateCategory { Id = "fantasy", Name = "Fantasy", Description = "Fantasy outfits" },
                new TemplateCategory { Id = "sci-fi", Name = "Sci-Fi", Description = "Sci-Fi outfits" },
                new TemplateCategory { Id = "military", Name = "Military", Description = "Military outfits" },
                new TemplateCategory { Id = "historical", Name = "Historical", Description = "Historical outfits" }
            };

            foreach (var category in defaultCategories)
            {
                _categories[category.Id] = category;
            }

            await SaveCategoriesAsync(cancellationToken);
            _logger.LogInformation($"Created {defaultCategories.Length} default categories");
        }

        private async Task WarmUpCacheAsync(CancellationToken cancellationToken)
        {
            // En sık kullanılan template'leri cache'e yükle;
            var popularTemplates = await GetPopularTemplatesAsync(50, cancellationToken);

            foreach (var template in popularTemplates)
            {
                _templateCache.Add(template.Id, template);
            }

            _logger.LogInformation($"Warmed up cache with {popularTemplates.Count} templates");
        }

        private async Task<OutfitTemplate> LoadTemplateFromStorageAsync(
            string templateId,
            CancellationToken cancellationToken)
        {
            var templatePath = Path.Combine(_templateStoragePath, $"{templateId}.template");

            if (!File.Exists(templatePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(templatePath);
                var template = JsonSerializer.Deserialize<OutfitTemplate>(json, _jsonOptions);

                if (template != null)
                {
                    // Asset'leri yükle;
                    await LoadTemplateAssetsAsync(template, cancellationToken);
                    return template;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load template from storage: {templateId}");
                return null;
            }
        }

        private async Task SaveTemplateToStorageAsync(
            OutfitTemplate template,
            CancellationToken cancellationToken)
        {
            var templatePath = Path.Combine(_templateStoragePath, $"{template.Id}.template");

            // Asset'leri kaydet;
            await SaveTemplateAssetsAsync(template, cancellationToken);

            // Template'i serialize et (asset referanslarını temizle)
            var templateClone = template.Clone();
            ClearAssetReferences(templateClone);

            var json = JsonSerializer.Serialize(templateClone, _jsonOptions);
            await File.WriteAllTextAsync(templatePath, json);
        }

        private async Task SaveTemplateDatabaseAsync(CancellationToken cancellationToken)
        {
            var database = new TemplateDatabase;
            {
                Version = "1.0",
                LastUpdated = DateTime.UtcNow,
                Templates = _templates.Values.Select(t => new TemplateDatabaseEntry
                {
                    Id = t.Id,
                    Name = t.Name,
                    Category = t.Category,
                    Version = t.Version,
                    FilePath = Path.Combine(_templateStoragePath, $"{t.Id}.template"),
                    LastAccessed = DateTime.UtcNow,
                    AccessCount = 0;
                }).ToList()
            };

            var json = JsonSerializer.Serialize(database, _jsonOptions);
            await File.WriteAllTextAsync(_templateDatabasePath, json);
        }

        private async Task SaveCategoriesAsync(CancellationToken cancellationToken)
        {
            var categoriesPath = Path.Combine(
                Path.GetDirectoryName(_templateDatabasePath),
                "Categories.json");

            var categories = _categories.Values.ToList();
            var json = JsonSerializer.Serialize(categories, _jsonOptions);
            await File.WriteAllTextAsync(categoriesPath, json);
        }

        private async Task<bool> RemoveTemplateInternalAsync(
            string templateId,
            bool removeAssets,
            CancellationToken cancellationToken)
        {
            await _templateAccessSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Template'i bul;
                if (!_templates.TryGetValue(templateId, out var template))
                {
                    template = await LoadTemplateFromStorageAsync(templateId, cancellationToken);
                    if (template == null)
                    {
                        _logger.LogWarning($"Template {templateId} not found for removal");
                        return false;
                    }
                }

                // Asset'leri sil;
                if (removeAssets)
                {
                    await RemoveTemplateAssetsAsync(template, cancellationToken);
                }

                // Storage'dan sil;
                var templatePath = Path.Combine(_templateStoragePath, $"{templateId}.template");
                if (File.Exists(templatePath))
                {
                    File.Delete(templatePath);
                }

                // Memory'den sil;
                _templates.Remove(templateId);
                _templateCache.Remove(templateId);

                // Index'ten sil;
                RemoveTemplateFromIndex(templateId);

                // Search engine'den sil;
                _searchEngine.RemoveTemplate(templateId);

                _logger.LogInformation($"Template removed: {template.Name} ({templateId})");

                // Event tetikle;
                TemplateRemoved?.Invoke(this, new TemplateRemovedEventArgs;
                {
                    TemplateId = templateId,
                    Template = template,
                    RemovedTime = DateTime.UtcNow,
                    AssetsRemoved = removeAssets;
                });

                return true;
            }
            finally
            {
                _templateAccessSemaphore.Release();
            }
        }

        private void ValidateTemplate(OutfitTemplate template)
        {
            if (string.IsNullOrEmpty(template.Name))
                throw new TemplateValidationException("Template name cannot be null or empty");

            if (string.IsNullOrEmpty(template.Category))
                throw new TemplateValidationException("Template category cannot be null or empty");

            if (template.ComponentDefinitions == null || !template.ComponentDefinitions.Any())
                throw new TemplateValidationException("Template must have at least one component definition");

            // Component validasyonu;
            foreach (var component in template.ComponentDefinitions)
            {
                if (string.IsNullOrEmpty(component.Type))
                    throw new TemplateValidationException("Component type cannot be null or empty");

                if (string.IsNullOrEmpty(component.AssetPath))
                    throw new TemplateValidationException("Component asset path cannot be null or empty");
            }
        }

        private async Task ProcessTemplateAssetsAsync(
            OutfitTemplate template,
            TemplateImportOptions options,
            CancellationToken cancellationToken)
        {
            // Asset'leri yükle ve optimize et;
            foreach (var component in template.ComponentDefinitions)
            {
                if (!string.IsNullOrEmpty(component.AssetPath))
                {
                    // Asset'i yükle;
                    var asset = await _assetManager.LoadAssetAsync(component.AssetPath, cancellationToken);
                    if (asset == null && !options.ContinueOnMissingAssets)
                    {
                        throw new AssetLoadException($"Failed to load asset: {component.AssetPath}");
                    }

                    // Asset'i optimize et;
                    if (options.OptimizeAssets && asset != null)
                    {
                        await _assetManager.OptimizeAssetAsync(asset, options.OptimizationLevel, cancellationToken);
                    }
                }
            }
        }

        private void UpdateTemplateMetadata(OutfitTemplate template)
        {
            template.Version ??= "1.0.0";
            template.LastModified = DateTime.UtcNow;

            if (template.Tags == null)
                template.Tags = new List<string>();

            if (!template.Tags.Contains(template.Category))
                template.Tags.Add(template.Category);
        }

        private void UpdateTemplateIndex(OutfitTemplate template)
        {
            // Kategori index'i;
            if (!_templateIndex.ContainsKey(template.Category))
                _templateIndex[template.Category] = new List<string>();

            if (!_templateIndex[template.Category].Contains(template.Id))
                _templateIndex[template.Category].Add(template.Id);

            // Tag index'i;
            if (template.Tags != null)
            {
                foreach (var tag in template.Tags)
                {
                    if (!_templateIndex.ContainsKey($"tag:{tag}"))
                        _templateIndex[$"tag:{tag}"] = new List<string>();

                    if (!_templateIndex[$"tag:{tag}"].Contains(template.Id))
                        _templateIndex[$"tag:{tag}"].Add(template.Id);
                }
            }
        }

        private void RemoveTemplateFromIndex(string templateId)
        {
            // Tüm index'lerden template'i kaldır;
            foreach (var key in _templateIndex.Keys.ToList())
            {
                _templateIndex[key].Remove(templateId);

                // Boş listeleri temizle;
                if (!_templateIndex[key].Any())
                    _templateIndex.Remove(key);
            }
        }

        private IEnumerable<OutfitTemplate> SortTemplates(
            IEnumerable<OutfitTemplate> templates,
            SortBy sortBy,
            SortOrder sortOrder)
        {
            switch (sortBy)
            {
                case SortBy.Name:
                    templates = sortOrder == SortOrder.Ascending;
                        ? templates.OrderBy(t => t.Name)
                        : templates.OrderByDescending(t => t.Name);
                    break;

                case SortBy.Category:
                    templates = sortOrder == SortOrder.Ascending;
                        ? templates.OrderBy(t => t.Category)
                        : templates.OrderByDescending(t => t.Category);
                    break;

                case SortBy.LastModified:
                    templates = sortOrder == SortOrder.Ascending;
                        ? templates.OrderBy(t => t.LastModified)
                        : templates.OrderByDescending(t => t.LastModified);
                    break;

                case SortBy.Popularity:
                    // Popularity sorting logic;
                    break;

                default:
                    templates = templates.OrderBy(t => t.Name);
                    break;
            }

            return templates;
        }

        private async Task<List<OutfitTemplate>> FindSimilarTemplatesAsync(
            OutfitTemplate referenceTemplate,
            RecommendationCriteria criteria,
            CancellationToken cancellationToken)
        {
            // Benzer template'leri bul;
            var similarTemplates = new List<OutfitTemplate>();

            // Aynı kategorideki template'ler;
            var sameCategory = _templates.Values;
                .Where(t => t.Id != referenceTemplate.Id && t.Category == referenceTemplate.Category)
                .Take(criteria.MaxResults / 2);

            similarTemplates.AddRange(sameCategory);

            // Benzer tag'leri olan template'ler;
            if (referenceTemplate.Tags != null)
            {
                var sharedTags = referenceTemplate.Tags;
                    .Where(tag => _templateIndex.ContainsKey($"tag:{tag}"))
                    .SelectMany(tag => _templateIndex[$"tag:{tag}"])
                    .Where(id => id != referenceTemplate.Id)
                    .Distinct()
                    .Take(criteria.MaxResults / 2);

                foreach (var templateId in sharedTags)
                {
                    var template = await GetTemplateAsync(templateId, true, cancellationToken);
                    if (template != null && !similarTemplates.Any(t => t.Id == templateId))
                    {
                        similarTemplates.Add(template);
                    }
                }
            }

            return similarTemplates.DistinctBy(t => t.Id).ToList();
        }

        private async Task<List<OutfitTemplate>> GetPopularTemplatesAsync(
            int count,
            CancellationToken cancellationToken)
        {
            // Populariteye göre template'leri getir;
            // Gerçek implementasyonda kullanım istatistiklerine bakılır;
            return _templates.Values;
                .OrderByDescending(t => t.LastModified)
                .Take(count)
                .ToList();
        }

        private float CalculateRecommendationScore(
            OutfitTemplate template,
            RecommendationCriteria criteria)
        {
            float score = 0.5f; // Base score;

            // Kategori eşleşmesi;
            if (criteria.PreferredCategories?.Contains(template.Category) == true)
                score += 0.2f;

            // Tag eşleşmesi;
            if (criteria.PreferredTags != null && template.Tags != null)
            {
                var matchingTags = criteria.PreferredTags.Intersect(template.Tags).Count();
                score += matchingTags * 0.1f;
            }

            // Karmaşıklık eşleşmesi;
            if (criteria.ComplexityLevel.HasValue)
            {
                var complexityMatch = 1.0f - Math.Abs(
                    (int)criteria.ComplexityLevel.Value - (int)template.Complexity) / 3.0f;
                score += complexityMatch * 0.15f;
            }

            // Popülerlik bonusu;
            if (template.IsPopular)
                score += 0.1f;

            return Math.Min(score, 1.0f);
        }

        private string GenerateRecommendationReason(
            OutfitTemplate template,
            RecommendationCriteria criteria)
        {
            var reasons = new List<string>();

            if (criteria.PreferredCategories?.Contains(template.Category) == true)
                reasons.Add("matches your preferred category");

            if (criteria.PreferredTags != null && template.Tags != null)
            {
                var matchingTags = criteria.PreferredTags.Intersect(template.Tags);
                if (matchingTags.Any())
                    reasons.Add($"has tags: {string.Join(", ", matchingTags)}");
            }

            if (template.IsPopular)
                reasons.Add("popular choice");

            if (template.IsNew)
                reasons.Add("newly added");

            return reasons.Any()
                ? $"Recommended because it {string.Join(" and ", reasons)}"
                : "Recommended based on overall quality";
        }

        private ConfidenceLevel CalculateConfidenceLevel(float score)
        {
            if (score >= 0.8f) return ConfidenceLevel.High;
            if (score >= 0.6f) return ConfidenceLevel.Medium;
            if (score >= 0.4f) return ConfidenceLevel.Low;
            return ConfidenceLevel.VeryLow;
        }

        private List<ScoredTemplate> CalculateRelevanceScores(
            List<OutfitTemplate> templates,
            string query,
            SearchOptions options)
        {
            var scoredTemplates = new List<ScoredTemplate>();

            foreach (var template in templates)
            {
                var score = _searchEngine.CalculateRelevanceScore(template, query);

                scoredTemplates.Add(new ScoredTemplate;
                {
                    Template = template,
                    RelevanceScore = score,
                    MatchFactors = _searchEngine.GetMatchFactors(template, query)
                });
            }

            return scoredTemplates;
                .OrderByDescending(st => st.RelevanceScore)
                .ToList();
        }

        private void ApplyTemplateUpdate(OutfitTemplate template, TemplateUpdate update)
        {
            if (update.Name != null)
                template.Name = update.Name;

            if (update.Category != null)
                template.Category = update.Category;

            if (update.Tags != null)
                template.Tags = update.Tags;

            if (update.Description != null)
                template.Description = update.Description;

            if (update.ComponentDefinitions != null)
                template.ComponentDefinitions = update.ComponentDefinitions;

            if (update.MaterialSet != null)
                template.MaterialSet = update.MaterialSet;

            if (update.Metadata != null)
                template.Metadata = update.Metadata;

            template.Version = IncrementVersion(template.Version);
            template.LastModified = DateTime.UtcNow;
        }

        private List<TemplateChange> DetectTemplateChanges(
            OutfitTemplate oldTemplate,
            OutfitTemplate newTemplate)
        {
            var changes = new List<TemplateChange>();

            if (oldTemplate.Name != newTemplate.Name)
                changes.Add(new TemplateChange { Field = "Name", OldValue = oldTemplate.Name, NewValue = newTemplate.Name });

            if (oldTemplate.Category != newTemplate.Category)
                changes.Add(new TemplateChange { Field = "Category", OldValue = oldTemplate.Category, NewValue = newTemplate.Category });

            if (oldTemplate.Description != newTemplate.Description)
                changes.Add(new TemplateChange { Field = "Description", OldValue = oldTemplate.Description, NewValue = newTemplate.Description });

            // Component değişiklikleri;
            var componentChanges = DetectComponentChanges(oldTemplate.ComponentDefinitions, newTemplate.ComponentDefinitions);
            changes.AddRange(componentChanges);

            return changes;
        }

        private async Task SendNotificationAsync(
            string message,
            NotificationType type,
            CancellationToken cancellationToken)
        {
            try
            {
                await _notificationManager.SendNotificationAsync(
                    new Notification;
                    {
                        Title = "Template Library",
                        Message = message,
                        Type = type,
                        Timestamp = DateTime.UtcNow,
                        Category = "TemplateManagement"
                    },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification");
            }
        }

        private void AutoSaveCallback(object state)
        {
            try
            {
                if (_isInitialized && _templates.Any())
                {
                    _ = SaveTemplateDatabaseAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-save failed");
            }
        }

        private long CalculateMemoryUsage()
        {
            long total = 0;

            // Template'lerin memory kullanımı;
            foreach (var template in _templates.Values)
            {
                total += template.EstimatedMemoryUsage;
            }

            // Cache memory kullanımı;
            total += _templateCache.MemoryUsage;

            // Index memory kullanımı;
            total += _templateIndex.Sum(kvp =>
                kvp.Key.Length * 2 + kvp.Value.Sum(id => id.Length * 2));

            return total;
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("TemplateLibrary is not initialized. Call InitializeAsync first.");
        }

        private string IncrementVersion(string currentVersion)
        {
            if (string.IsNullOrEmpty(currentVersion))
                return "1.0.0";

            var parts = currentVersion.Split('.');
            if (parts.Length >= 3 && int.TryParse(parts[2], out int patch))
            {
                parts[2] = (patch + 1).ToString();
                return string.Join(".", parts);
            }

            return $"{currentVersion}.1";
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
                    // Auto-save timer'ı durdur;
                    _autoSaveTimer?.Dispose();

                    // Son kaydetme işlemi;
                    if (_isInitialized)
                    {
                        try
                        {
                            SaveTemplateDatabaseAsync(CancellationToken.None).Wait(5000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to save template database on dispose");
                        }
                    }

                    // Semaphore'ı serbest bırak;
                    _templateAccessSemaphore?.Dispose();

                    // Cache'i temizle;
                    _templateCache?.Dispose();

                    // Search engine'i temizle;
                    _searchEngine?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~TemplateLibrary()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    public interface ITemplateLibrary;
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<OutfitTemplate> AddTemplateAsync(
            OutfitTemplate template,
            TemplateImportOptions options = null,
            CancellationToken cancellationToken = default);
        Task<OutfitTemplate> UpdateTemplateAsync(
            string templateId,
            TemplateUpdate update,
            CancellationToken cancellationToken = default);
        Task<bool> RemoveTemplateAsync(
            string templateId,
            bool removeAssets = false,
            CancellationToken cancellationToken = default);
        Task<OutfitTemplate> GetTemplateAsync(
            string templateId,
            bool loadFromCache = true,
            CancellationToken cancellationToken = default);
        Task<TemplateCollection> GetAllTemplatesAsync(
            TemplateFilter filter = null,
            CancellationToken cancellationToken = default);
        Task<TemplateCategory> AddCategoryAsync(
            TemplateCategory category,
            CancellationToken cancellationToken = default);
        Task<Dictionary<string, TemplateCategory>> GetAllCategoriesAsync(
            CancellationToken cancellationToken = default);
        Task<TemplateSearchResult> SearchTemplatesAsync(
            string query,
            SearchOptions options = null,
            CancellationToken cancellationToken = default);
        Task<List<TemplateRecommendation>> GetRecommendationsAsync(
            string referenceTemplateId = null,
            RecommendationCriteria criteria = null,
            CancellationToken cancellationToken = default);
        Task<OutfitTemplate> ImportTemplateAsync(
            string filePath,
            TemplateImportOptions options = null,
            CancellationToken cancellationToken = default);
        Task<string> ExportTemplateAsync(
            string templateId,
            string exportPath = null,
            ExportOptions options = null,
            CancellationToken cancellationToken = default);
        Task<bool> ReindexLibraryAsync(CancellationToken cancellationToken = default);
        TemplateLibraryStats GetLibraryStats();
        Task<string> BackupLibraryAsync(
            string backupPath = null,
            CancellationToken cancellationToken = default);

        event EventHandler<TemplateAddedEventArgs> TemplateAdded;
        event EventHandler<TemplateUpdatedEventArgs> TemplateUpdated;
        event EventHandler<TemplateRemovedEventArgs> TemplateRemoved;
        event EventHandler<LibraryIndexedEventArgs> LibraryIndexed;
    }

    public class TemplateCategory;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }
        public int Order { get; set; }
        public bool IsVisible { get; set; } = true;
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }

    public class TemplateFilter;
    {
        public static TemplateFilter Default => new TemplateFilter();

        public List<string> Categories { get; set; }
        public List<string> Tags { get; set; }
        public string SearchQuery { get; set; }
        public SortBy SortBy { get; set; } = SortBy.Name;
        public SortOrder SortOrder { get; set; } = SortOrder.Ascending;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public bool IncludeHidden { get; set; } = false;

        public bool IsDefault =>
            (Categories == null || !Categories.Any()) &&
            (Tags == null || !Tags.Any()) &&
            string.IsNullOrEmpty(SearchQuery) &&
            SortBy == SortBy.Name &&
            SortOrder == SortOrder.Ascending &&
            PageNumber == 1 &&
            PageSize == 50;
    }

    public class TemplateCollection;
    {
        public List<OutfitTemplate> Templates { get; set; } = new List<OutfitTemplate>();
        public int TotalCount { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public int PageCount => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
        public TemplateFilter Filter { get; set; }
    }

    public class TemplateSearchResult;
    {
        public string Query { get; set; }
        public List<ScoredTemplate> Results { get; set; } = new List<ScoredTemplate>();
        public int TotalResults { get; set; }
        public DateTime SearchTime { get; set; }
        public TimeSpan SearchDuration { get; set; }
        public SearchOptions Options { get; set; }
    }

    public class ScoredTemplate;
    {
        public OutfitTemplate Template { get; set; }
        public float RelevanceScore { get; set; }
        public Dictionary<string, float> MatchFactors { get; set; } = new Dictionary<string, float>();
    }

    public class TemplateRecommendation;
    {
        public OutfitTemplate Template { get; set; }
        public float Score { get; set; }
        public string Reason { get; set; }
        public ConfidenceLevel Confidence { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }

    public class TemplateLibraryStats;
    {
        public int TotalTemplates { get; set; }
        public int TotalCategories { get; set; }
        public float CacheHitRate { get; set; }
        public int CacheSize { get; set; }
        public int IndexSize { get; set; }
        public SearchEngineStats SearchEngineStats { get; set; }
        public Dictionary<string, int> CategoryDistribution { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> TagDistribution { get; set; } = new Dictionary<string, int>();
        public DateTime LastIndexTime { get; set; }
        public long MemoryUsage { get; set; }
    }

    // Options Classes;
    public class TemplateImportOptions;
    {
        public static TemplateImportOptions Default => new TemplateImportOptions();

        public bool OverwriteExisting { get; set; } = false;
        public bool OptimizeAssets { get; set; } = true;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public bool ContinueOnMissingAssets { get; set; } = false;
        public bool ValidateTemplate { get; set; } = true;
    }

    public class ExportOptions;
    {
        public static ExportOptions Default => new ExportOptions();

        public bool IncludeAssets { get; set; } = true;
        public bool CompressOutput { get; set; } = true;
        public ExportFormat Format { get; set; } = ExportFormat.Json;
        public bool IncludeMetadata { get; set; } = true;
    }

    public class SearchOptions;
    {
        public static SearchOptions Default => new SearchOptions();

        public int MaxResults { get; set; } = 50;
        public bool UseFuzzySearch { get; set; } = true;
        public float MinimumRelevance { get; set; } = 0.1f;
        public List<string> SearchFields { get; set; } = new List<string> { "Name", "Description", "Tags" };
    }

    public class RecommendationCriteria;
    {
        public static RecommendationCriteria Default => new RecommendationCriteria();

        public List<string> PreferredCategories { get; set; }
        public List<string> PreferredTags { get; set; }
        public TemplateComplexity? ComplexityLevel { get; set; }
        public int MaxResults { get; set; } = 10;
        public bool IncludePopular { get; set; } = true;
        public bool IncludeNew { get; set; } = true;
    }

    public class TemplateUpdate;
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
        public List<ComponentDefinition> ComponentDefinitions { get; set; }
        public MaterialSet MaterialSet { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class TemplateChange;
    {
        public string Field { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public ChangeType ChangeType { get; set; }
    }

    // Event Args Classes;
    public class TemplateAddedEventArgs : EventArgs;
    {
        public string TemplateId { get; set; }
        public OutfitTemplate Template { get; set; }
        public DateTime AddedTime { get; set; }
    }

    public class TemplateUpdatedEventArgs : EventArgs;
    {
        public string TemplateId { get; set; }
        public OutfitTemplate OldTemplate { get; set; }
        public OutfitTemplate NewTemplate { get; set; }
        public List<TemplateChange> Changes { get; set; } = new List<TemplateChange>();
        public DateTime UpdatedTime { get; set; }
    }

    public class TemplateRemovedEventArgs : EventArgs;
    {
        public string TemplateId { get; set; }
        public OutfitTemplate Template { get; set; }
        public DateTime RemovedTime { get; set; }
        public bool AssetsRemoved { get; set; }
    }

    public class LibraryIndexedEventArgs : EventArgs;
    {
        public int IndexedCount { get; set; }
        public DateTime IndexingTime { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    // Enums;
    public enum SortBy { Name, Category, LastModified, Popularity, Complexity }
    public enum SortOrder { Ascending, Descending }
    public enum TemplateComplexity { Simple, Medium, Complex, VeryComplex }
    public enum ConfidenceLevel { VeryLow, Low, Medium, High }
    public enum ChangeType { Added, Modified, Removed }
    public enum ExportFormat { Json, Xml, Binary }

    // Exceptions;
    public class TemplateLibraryException : Exception
    {
        public TemplateLibraryException(string message) : base(message) { }
        public TemplateLibraryException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateAddException : TemplateLibraryException;
    {
        public TemplateAddException(string message) : base(message) { }
        public TemplateAddException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateUpdateException : TemplateLibraryException;
    {
        public TemplateUpdateException(string message) : base(message) { }
        public TemplateUpdateException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateRemoveException : TemplateLibraryException;
    {
        public TemplateRemoveException(string message) : base(message) { }
        public TemplateRemoveException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateLoadException : TemplateLibraryException;
    {
        public TemplateLoadException(string message) : base(message) { }
        public TemplateLoadException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateQueryException : TemplateLibraryException;
    {
        public TemplateQueryException(string message) : base(message) { }
        public TemplateQueryException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateSearchException : TemplateLibraryException;
    {
        public TemplateSearchException(string message) : base(message) { }
        public TemplateSearchException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateImportException : TemplateLibraryException;
    {
        public TemplateImportException(string message) : base(message) { }
        public TemplateImportException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateExportException : TemplateLibraryException;
    {
        public TemplateExportException(string message) : base(message) { }
        public TemplateExportException(string message, Exception inner) : base(message, inner) { }
    }

    public class TemplateValidationException : TemplateLibraryException;
    {
        public TemplateValidationException(string message) : base(message) { }
    }

    public class TemplateAlreadyExistsException : TemplateLibraryException;
    {
        public TemplateAlreadyExistsException(string message) : base(message) { }
    }

    public class TemplateNotFoundException : TemplateLibraryException;
    {
        public TemplateNotFoundException(string message) : base(message) { }
    }

    public class CategoryAddException : TemplateLibraryException;
    {
        public CategoryAddException(string message) : base(message) { }
        public CategoryAddException(string message, Exception inner) : base(message, inner) { }
    }

    public class CategoryAlreadyExistsException : TemplateLibraryException;
    {
        public CategoryAlreadyExistsException(string message) : base(message) { }
    }

    public class RecommendationException : TemplateLibraryException;
    {
        public RecommendationException(string message) : base(message) { }
        public RecommendationException(string message, Exception inner) : base(message, inner) { }
    }

    public class IndexingException : TemplateLibraryException;
    {
        public IndexingException(string message) : base(message) { }
        public IndexingException(string message, Exception inner) : base(message, inner) { }
    }

    public class BackupException : TemplateLibraryException;
    {
        public BackupException(string message) : base(message) { }
        public BackupException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;

    #region Internal Helper Classes;

    internal class TemplateCache : IDisposable
    {
        private readonly Dictionary<string, OutfitTemplate> _cache;
        private readonly LinkedList<string> _accessOrder;
        private readonly int _maxSize;
        private int _hitCount;
        private int _missCount;
        private long _memoryUsage;

        public TemplateCache(int maxSize = 1000)
        {
            _maxSize = maxSize;
            _cache = new Dictionary<string, OutfitTemplate>(maxSize);
            _accessOrder = new LinkedList<string>();
        }

        public void Add(string templateId, OutfitTemplate template)
        {
            if (_cache.Count >= _maxSize)
            {
                // LRU eviction;
                var oldest = _accessOrder.First;
                if (oldest != null)
                {
                    Remove(oldest.Value);
                }
            }

            _cache[templateId] = template;
            _accessOrder.AddLast(templateId);
            _memoryUsage += template.EstimatedMemoryUsage;
        }

        public OutfitTemplate Get(string templateId)
        {
            if (_cache.TryGetValue(templateId, out var template))
            {
                // Access order'ı güncelle;
                _accessOrder.Remove(templateId);
                _accessOrder.AddLast(templateId);
                _hitCount++;
                return template;
            }

            _missCount++;
            return null;
        }

        public void Update(string templateId, OutfitTemplate template)
        {
            if (_cache.ContainsKey(templateId))
            {
                var oldTemplate = _cache[templateId];
                _memoryUsage -= oldTemplate.EstimatedMemoryUsage;

                _cache[templateId] = template;
                _memoryUsage += template.EstimatedMemoryUsage;

                // Access order'ı güncelle;
                _accessOrder.Remove(templateId);
                _accessOrder.AddLast(templateId);
            }
        }

        public void Remove(string templateId)
        {
            if (_cache.TryGetValue(templateId, out var template))
            {
                _cache.Remove(templateId);
                _accessOrder.Remove(templateId);
                _memoryUsage -= template.EstimatedMemoryUsage;
            }
        }

        public IEnumerable<OutfitTemplate> GetAll()
        {
            return _cache.Values;
        }

        public void Clear()
        {
            _cache.Clear();
            _accessOrder.Clear();
            _memoryUsage = 0;
        }

        public float HitRate => _hitCount + _missCount > 0;
            ? (float)_hitCount / (_hitCount + _missCount)
            : 0;

        public int Size => _cache.Count;
        public long MemoryUsage => _memoryUsage;
        public bool IsComplete => _cache.Count >= _maxSize;

        public void Dispose()
        {
            Clear();
        }
    }

    internal class TemplateSearchEngine : IDisposable
    {
        private Dictionary<string, SearchIndexEntry> _index;
        private bool _isInitialized;

        public Task InitializeAsync(
            IEnumerable<OutfitTemplate> templates,
            CancellationToken cancellationToken)
        {
            _index = new Dictionary<string, SearchIndexEntry>();

            foreach (var template in templates)
            {
                AddTemplateToIndex(template);
            }

            _isInitialized = true;
            return Task.CompletedTask;
        }

        public void AddTemplate(OutfitTemplate template)
        {
            if (!_isInitialized) return;
            AddTemplateToIndex(template);
        }

        public void UpdateTemplate(OutfitTemplate template)
        {
            if (!_isInitialized) return;
            RemoveTemplateFromIndex(template.Id);
            AddTemplateToIndex(template);
        }

        public void RemoveTemplate(string templateId)
        {
            if (!_isInitialized) return;
            RemoveTemplateFromIndex(templateId);
        }

        public Task<List<string>> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken)
        {
            if (!_isInitialized || string.IsNullOrEmpty(query))
                return Task.FromResult(new List<string>());

            var normalizedQuery = NormalizeText(query);
            var queryTerms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var scores = new Dictionary<string, float>();

            foreach (var term in queryTerms)
            {
                if (term.Length < 2) continue;

                foreach (var entry in _index.Values)
                {
                    var score = CalculateTermScore(entry, term);
                    if (score > 0)
                    {
                        if (!scores.ContainsKey(entry.TemplateId))
                            scores[entry.TemplateId] = 0;

                        scores[entry.TemplateId] += score;
                    }
                }
            }

            var results = scores;
                .Where(kvp => kvp.Value > 0.1f)
                .OrderByDescending(kvp => kvp.Value)
                .Take(maxResults)
                .Select(kvp => kvp.Key)
                .ToList();

            return Task.FromResult(results);
        }

        public float CalculateRelevanceScore(OutfitTemplate template, string query)
        {
            if (!_isInitialized || !_index.TryGetValue(template.Id, out var entry))
                return 0;

            var normalizedQuery = NormalizeText(query);
            var queryTerms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            float totalScore = 0;
            foreach (var term in queryTerms)
            {
                if (term.Length < 2) continue;
                totalScore += CalculateTermScore(entry, term);
            }

            return Math.Min(totalScore, 1.0f);
        }

        public Dictionary<string, float> GetMatchFactors(OutfitTemplate template, string query)
        {
            var factors = new Dictionary<string, float>();

            if (!_isInitialized || !_index.TryGetValue(template.Id, out var entry))
                return factors;

            var normalizedQuery = NormalizeText(query);
            var queryTerms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var term in queryTerms)
            {
                if (term.Length < 2) continue;

                // Name match;
                if (entry.NormalizedName.Contains(term))
                    factors["NameMatch"] = factors.GetValueOrDefault("NameMatch") + 0.3f;

                // Description match;
                if (entry.NormalizedDescription.Contains(term))
                    factors["DescriptionMatch"] = factors.GetValueOrDefault("DescriptionMatch") + 0.2f;

                // Tag match;
                if (entry.NormalizedTags.Any(tag => tag.Contains(term)))
                    factors["TagMatch"] = factors.GetValueOrDefault("TagMatch") + 0.2f;

                // Category match;
                if (entry.NormalizedCategory.Contains(term))
                    factors["CategoryMatch"] = factors.GetValueOrDefault("CategoryMatch") + 0.3f;
            }

            return factors;
        }

        public void ClearIndex()
        {
            _index?.Clear();
        }

        public Task OptimizeAsync(CancellationToken cancellationToken)
        {
            // Index optimizasyonu;
            return Task.CompletedTask;
        }

        public SearchEngineStats GetStats()
        {
            return new SearchEngineStats;
            {
                IndexSize = _index?.Count ?? 0,
                IsInitialized = _isInitialized;
            };
        }

        public void Dispose()
        {
            ClearIndex();
        }

        private void AddTemplateToIndex(OutfitTemplate template)
        {
            var entry = new SearchIndexEntry
            {
                TemplateId = template.Id,
                NormalizedName = NormalizeText(template.Name),
                NormalizedDescription = NormalizeText(template.Description),
                NormalizedCategory = NormalizeText(template.Category),
                NormalizedTags = template.Tags?.Select(NormalizeText).ToList() ?? new List<string>()
            };

            _index[template.Id] = entry
        }

        private void RemoveTemplateFromIndex(string templateId)
        {
            _index.Remove(templateId);
        }

        private float CalculateTermScore(SearchIndexEntry entry, string term)
        {
            float score = 0;

            // Name'de tam eşleşme yüksek skor;
            if (entry.NormalizedName.Contains(term))
                score += 0.5f;

            // Description'da eşleşme orta skor;
            if (entry.NormalizedDescription.Contains(term))
                score += 0.3f;

            // Tag'lerde eşleşme;
            if (entry.NormalizedTags.Any(tag => tag.Contains(term)))
                score += 0.4f;

            // Category'de eşleşme;
            if (entry.NormalizedCategory.Contains(term))
                score += 0.6f;

            return score;
        }

        private string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text.ToLowerInvariant().Trim();
        }
    }

    internal class SearchIndexEntry
    {
        public string TemplateId { get; set; }
        public string NormalizedName { get; set; }
        public string NormalizedDescription { get; set; }
        public string NormalizedCategory { get; set; }
        public List<string> NormalizedTags { get; set; } = new List<string>();
    }

    internal class SearchEngineStats;
    {
        public int IndexSize { get; set; }
        public bool IsInitialized { get; set; }
    }

    internal class TemplateDatabase;
    {
        public string Version { get; set; } = "1.0";
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public List<TemplateDatabaseEntry> Templates { get; set; } = new List<TemplateDatabaseEntry>();
    }

    internal class TemplateDatabaseEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Version { get; set; }
        public string FilePath { get; set; }
        public DateTime LastAccessed { get; set; }
        public int AccessCount { get; set; }
    }

    #endregion;
}
