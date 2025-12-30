using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.CharacterSystems.GameplaySystems.InventoryManager.Contracts;

namespace NEDA.CharacterSystems.GameplaySystems.InventoryManager;
{
    /// <summary>
    /// Oyun içi eşya sistemi. Eşya yönetimi, özellikleri, istatistikleri ve etkileşimlerini yönetir.
    /// </summary>
    public class ItemSystem : IItemSystem, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IItemDatabase _itemDatabase;
        private readonly ItemSystemConfiguration _configuration;
        private readonly Dictionary<string, ItemInstance> _activeItems;
        private readonly Dictionary<string, ItemTemplate> _itemTemplates;
        private readonly Dictionary<string, ItemCategory> _itemCategories;
        private readonly ReaderWriterLockSlim _lock;
        private readonly ItemFactory _itemFactory;
        private readonly ItemValidator _itemValidator;
        private readonly ItemSerializer _itemSerializer;
        private bool _isDisposed;

        #endregion;

        #region Constants;

        private const int DEFAULT_MAX_STACK_SIZE = 99;
        private const int DEFAULT_INVENTORY_SLOTS = 50;
        private const int DEFAULT_ITEM_LEVEL = 1;
        private const ItemRarity DEFAULT_RARITY = ItemRarity.Common;

        #endregion;

        #region Properties;

        /// <summary>
        /// Aktif item sayısı.
        /// </summary>
        public int ActiveItemCount;
        {
            get;
            {
                _lock.EnterReadLock();
                try
                {
                    return _activeItems.Count;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Yüklenen item template sayısı.
        /// </summary>
        public int TemplateCount => _itemTemplates.Count;

        /// <summary>
        /// Yüklenen kategori sayısı.
        /// </summary>
        public int CategoryCount => _itemCategories.Count;

        /// <summary>
        /// Item sistemi konfigürasyonu.
        /// </summary>
        public ItemSystemConfiguration Configuration => _configuration;

        /// <summary>
        /// Item fabrikası.
        /// </summary>
        public IItemFactory ItemFactory => _itemFactory;

        /// <summary>
        /// Item validatörü.
        /// </summary>
        public IItemValidator ItemValidator => _itemValidator;

        #endregion;

        #region Events;

        /// <summary>
        /// Yeni item oluşturulduğunda tetiklenir.
        /// </summary>
        public event EventHandler<ItemCreatedEventArgs> ItemCreated;

        /// <summary>
        /// Item yok edildiğinde tetiklenir.
        /// </summary>
        public event EventHandler<ItemDestroyedEventArgs> ItemDestroyed;

        /// <summary>
        /// Item güncellendiğinde tetiklenir.
        /// </summary>
        public event EventHandler<ItemUpdatedEventArgs> ItemUpdated;

        /// <summary>
        /// Item kullanıldığında tetiklenir.
        /// </summary>
        public event EventHandler<ItemUsedEventArgs> ItemUsed;

        /// <summary>
        /// Item kombinasyonu yapıldığında tetiklenir.
        /// </summary>
        public event EventHandler<ItemCombinedEventArgs> ItemCombined;

        /// <summary>
        /// Item upgrade edildiğinde tetiklenir.
        /// </summary>
        public event EventHandler<ItemUpgradedEventArgs> ItemUpgraded;

        #endregion;

        #region Constructor;

        /// <summary>
        /// ItemSystem sınıfının yeni bir örneğini oluşturur.
        /// </summary>
        public ItemSystem(
            ILogger logger,
            IItemDatabase itemDatabase = null,
            ItemSystemConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _itemDatabase = itemDatabase;
            _configuration = configuration ?? new ItemSystemConfiguration();

            _activeItems = new Dictionary<string, ItemInstance>();
            _itemTemplates = new Dictionary<string, ItemTemplate>();
            _itemCategories = new Dictionary<string, ItemCategory>();
            _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

            _itemFactory = new ItemFactory(this);
            _itemValidator = new ItemValidator(this);
            _itemSerializer = new ItemSerializer();

            InitializeSystem();

            _logger.LogInformation("ItemSystem initialized with {MaxStackSize} max stack size",
                _configuration.MaxStackSize);
        }

        #endregion;

        #region Public Methods - System Management;

        /// <summary>
        /// Item sistemini başlatır.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("Initializing ItemSystem...");

                // Item veritabanını yükle;
                if (_itemDatabase != null)
                {
                    await LoadItemDatabaseAsync();
                }

                // Varsayılan itemları yükle;
                await LoadDefaultItemsAsync();

                // Sistem kontrolü yap;
                await ValidateSystemAsync();

                _logger.LogInformation("ItemSystem initialized successfully. Templates: {TemplateCount}, Categories: {CategoryCount}",
                    _itemTemplates.Count, _itemCategories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ItemSystem");
                throw new ItemSystemException("ItemSystem initialization failed", ex);
            }
        }

        /// <summary>
        /// Item veritabanını yükler.
        /// </summary>
        public async Task LoadItemDatabaseAsync()
        {
            try
            {
                _logger.LogInformation("Loading item database...");

                // Item kategorilerini yükle;
                var categories = await _itemDatabase.LoadCategoriesAsync();
                foreach (var category in categories)
                {
                    _itemCategories[category.Id] = category;
                }

                // Item template'lerini yükle;
                var templates = await _itemDatabase.LoadTemplatesAsync();
                foreach (var template in templates)
                {
                    _itemTemplates[template.Id] = template;
                }

                _logger.LogInformation("Item database loaded: {CategoryCount} categories, {TemplateCount} templates",
                    _itemCategories.Count, _itemTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load item database");
                throw new ItemSystemException("Failed to load item database", ex);
            }
        }

        /// <summary>
        /// Item sistemini doğrular.
        /// </summary>
        public async Task<bool> ValidateSystemAsync()
        {
            try
            {
                _logger.LogInformation("Validating ItemSystem...");

                var validationResults = new List<SystemValidationResult>();

                // Template validasyonu;
                validationResults.Add(ValidateTemplates());

                // Kategori validasyonu;
                validationResults.Add(ValidateCategories());

                // Bağımlılık validasyonu;
                validationResults.Add(ValidateDependencies());

                // Performans validasyonu;
                validationResults.Add(await ValidatePerformanceAsync());

                // Sonuçları analiz et;
                var criticalErrors = validationResults;
                    .SelectMany(r => r.Errors)
                    .Count(e => e.Severity == ValidationSeverity.Critical);

                var hasErrors = validationResults.Any(r => r.Errors.Any());

                if (criticalErrors > 0)
                {
                    _logger.LogError("ItemSystem validation failed with {CriticalErrors} critical errors", criticalErrors);
                    return false;
                }

                if (hasErrors)
                {
                    _logger.LogWarning("ItemSystem validation completed with warnings");
                }
                else;
                {
                    _logger.LogInformation("ItemSystem validation passed successfully");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ItemSystem validation failed");
                return false;
            }
        }

        /// <summary>
        /// Item sistemini temizler.
        /// </summary>
        public void ClearSystem()
        {
            _lock.EnterWriteLock();
            try
            {
                _activeItems.Clear();
                _itemTemplates.Clear();
                _itemCategories.Clear();

                _logger.LogInformation("ItemSystem cleared");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        #endregion;

        #region Public Methods - Item Management;

        /// <summary>
        /// Yeni bir item oluşturur.
        /// </summary>
        /// <param name="templateId">Item template ID'si</param>
        /// <param name="ownerId">Sahip ID'si</param>
        /// <param name="quantity">Miktar</param>
        /// <param name="properties">Özel özellikler</param>
        /// <returns>Oluşturulan item</returns>
        public async Task<ItemInstance> CreateItemAsync(
            string templateId,
            string ownerId = null,
            int quantity = 1,
            Dictionary<string, object> properties = null)
        {
            ValidateTemplateId(templateId);
            ValidateQuantity(quantity);

            _lock.EnterWriteLock();
            try
            {
                // Template'i kontrol et;
                if (!_itemTemplates.TryGetValue(templateId, out var template))
                {
                    throw new ItemNotFoundException($"Item template not found: {templateId}");
                }

                // Item oluştur;
                var item = _itemFactory.CreateItem(template, ownerId, quantity, properties);

                // Validasyon yap;
                var validationResult = _itemValidator.ValidateItem(item);
                if (!validationResult.IsValid)
                {
                    throw new ItemValidationException($"Item validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // Aktif item'lara ekle;
                _activeItems[item.InstanceId] = item;

                // Event tetikle;
                OnItemCreated(new ItemCreatedEventArgs;
                {
                    Item = item,
                    TemplateId = templateId,
                    OwnerId = ownerId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Item created: {ItemId} ({TemplateId}) for owner {OwnerId}",
                    item.InstanceId, templateId, ownerId);

                return item;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Varolan bir item'ı klonlar.
        /// </summary>
        public async Task<ItemInstance> CloneItemAsync(string itemId, string newOwnerId = null, int quantity = 1)
        {
            ValidateItemId(itemId);
            ValidateQuantity(quantity);

            _lock.EnterReadLock();
            try
            {
                if (!_activeItems.TryGetValue(itemId, out var sourceItem))
                {
                    throw new ItemNotFoundException($"Item not found: {itemId}");
                }

                // Yeni item oluştur;
                var clonedItem = _itemFactory.CloneItem(sourceItem, newOwnerId, quantity);

                // Validasyon yap;
                var validationResult = _itemValidator.ValidateItem(clonedItem);
                if (!validationResult.IsValid)
                {
                    throw new ItemValidationException($"Cloned item validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                _lock.ExitReadLock();
                _lock.EnterWriteLock();

                // Aktif item'lara ekle;
                _activeItems[clonedItem.InstanceId] = clonedItem;

                // Event tetikle;
                OnItemCreated(new ItemCreatedEventArgs;
                {
                    Item = clonedItem,
                    TemplateId = sourceItem.TemplateId,
                    OwnerId = newOwnerId,
                    IsClone = true,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Item cloned: {SourceItemId} -> {ClonedItemId}", itemId, clonedItem.InstanceId);

                return clonedItem;
            }
            finally
            {
                if (_lock.IsReadLockHeld) _lock.ExitReadLock();
                if (_lock.IsWriteLockHeld) _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Item'ı yok eder.
        /// </summary>
        public bool DestroyItem(string itemId, string reason = null)
        {
            ValidateItemId(itemId);

            _lock.EnterWriteLock();
            try
            {
                if (_activeItems.TryGetValue(itemId, out var item))
                {
                    // Event tetikle;
                    OnItemDestroyed(new ItemDestroyedEventArgs;
                    {
                        Item = item,
                        Reason = reason,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Item'ı kaldır;
                    _activeItems.Remove(itemId);

                    // Item'ı dispose et;
                    item.Dispose();

                    _logger.LogDebug("Item destroyed: {ItemId} ({TemplateId})", itemId, item.TemplateId);

                    return true;
                }

                return false;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Item'ı günceller.
        /// </summary>
        public async Task<ItemInstance> UpdateItemAsync(string itemId, ItemUpdateRequest updateRequest)
        {
            ValidateItemId(itemId);
            ValidateUpdateRequest(updateRequest);

            _lock.EnterWriteLock();
            try
            {
                if (!_activeItems.TryGetValue(itemId, out var item))
                {
                    throw new ItemNotFoundException($"Item not found: {itemId}");
                }

                // Eski değerleri kaydet;
                var oldItem = _itemSerializer.CloneItem(item);

                // Güncellemeleri uygula;
                ApplyItemUpdates(item, updateRequest);

                // Validasyon yap;
                var validationResult = _itemValidator.ValidateItem(item);
                if (!validationResult.IsValid)
                {
                    // Değişiklikleri geri al;
                    _itemSerializer.CopyItem(oldItem, item);
                    throw new ItemValidationException($"Item update validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // Event tetikle;
                OnItemUpdated(new ItemUpdatedEventArgs;
                {
                    Item = item,
                    OldItem = oldItem,
                    UpdateRequest = updateRequest,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Item updated: {ItemId} ({TemplateId})", itemId, item.TemplateId);

                return item;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Item'ı kullanır.
        /// </summary>
        public async Task<ItemUseResult> UseItemAsync(string itemId, string userId, ItemUseContext context = null)
        {
            ValidateItemId(itemId);
            ValidateUserId(userId);

            _lock.EnterWriteLock();
            try
            {
                if (!_activeItems.TryGetValue(itemId, out var item))
                {
                    throw new ItemNotFoundException($"Item not found: {itemId}");
                }

                // Kullanım validasyonu;
                var useValidation = ValidateItemUse(item, userId, context);
                if (!useValidation.IsValid)
                {
                    return new ItemUseResult;
                    {
                        Success = false,
                        ErrorMessage = useValidation.ErrorMessage,
                        Item = item;
                    };
                }

                // Kullanım öncesi event;
                var preUseArgs = new ItemPreUseEventArgs;
                {
                    Item = item,
                    UserId = userId,
                    Context = context,
                    Timestamp = DateTime.UtcNow;
                };

                // Item'ın kullanımını işle;
                var useResult = await ProcessItemUseAsync(item, userId, context);

                if (useResult.Success)
                {
                    // Miktarı azalt veya item'ı yok et;
                    if (item.IsConsumable && item.Quantity > 0)
                    {
                        item.Quantity--;

                        if (item.Quantity <= 0)
                        {
                            DestroyItem(itemId, "Consumed through use");
                        }
                    }

                    // Kullanım sayacını artır;
                    item.UsageCount++;
                    item.LastUsed = DateTime.UtcNow;

                    // Event tetikle;
                    OnItemUsed(new ItemUsedEventArgs;
                    {
                        Item = item,
                        UserId = userId,
                        Context = context,
                        Result = useResult,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogDebug("Item used: {ItemId} ({TemplateId}) by user {UserId}",
                        itemId, item.TemplateId, userId);
                }

                return useResult;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// İki item'ı birleştirir.
        /// </summary>
        public async Task<ItemCombineResult> CombineItemsAsync(
            string itemId1,
            string itemId2,
            string userId,
            Dictionary<string, object> combineOptions = null)
        {
            ValidateItemId(itemId1);
            ValidateItemId(itemId2);
            ValidateUserId(userId);

            _lock.EnterWriteLock();
            try
            {
                if (!_activeItems.TryGetValue(itemId1, out var item1))
                {
                    throw new ItemNotFoundException($"Item not found: {itemId1}");
                }

                if (!_activeItems.TryGetValue(itemId2, out var item2))
                {
                    throw new ItemNotFoundException($"Item not found: {itemId2}");
                }

                // Birleştirme validasyonu;
                var combineValidation = ValidateItemCombine(item1, item2, userId, combineOptions);
                if (!combineValidation.IsValid)
                {
                    return new ItemCombineResult;
                    {
                        Success = false,
                        ErrorMessage = combineValidation.ErrorMessage,
                        SourceItems = new[] { item1, item2 }
                    };
                }

                // Birleştirme işlemini gerçekleştir;
                var combineResult = await ProcessItemCombineAsync(item1, item2, userId, combineOptions);

                if (combineResult.Success && combineResult.ResultItem != null)
                {
                    // Kaynak item'ları yok et;
                    DestroyItem(itemId1, "Combined with another item");
                    DestroyItem(itemId2, "Combined with another item");

                    // Yeni item'ı ekle;
                    _activeItems[combineResult.ResultItem.InstanceId] = combineResult.ResultItem;

                    // Event tetikle;
                    OnItemCombined(new ItemCombinedEventArgs;
                    {
                        SourceItems = new[] { item1, item2 },
                        ResultItem = combineResult.ResultItem,
                        UserId = userId,
                        Options = combineOptions,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogDebug("Items combined: {ItemId1} + {ItemId2} -> {ResultItemId}",
                        itemId1, itemId2, combineResult.ResultItem.InstanceId);
                }

                return combineResult;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Item'ı upgrade eder.
        /// </summary>
        public async Task<ItemUpgradeResult> UpgradeItemAsync(
            string itemId,
            string userId,
            ItemUpgradeRequest upgradeRequest)
        {
            ValidateItemId(itemId);
            ValidateUserId(userId);
            ValidateUpgradeRequest(upgradeRequest);

            _lock.EnterWriteLock();
            try
            {
                if (!_activeItems.TryGetValue(itemId, out var item))
                {
                    throw new ItemNotFoundException($"Item not found: {itemId}");
                }

                // Upgrade validasyonu;
                var upgradeValidation = ValidateItemUpgrade(item, userId, upgradeRequest);
                if (!upgradeValidation.IsValid)
                {
                    return new ItemUpgradeResult;
                    {
                        Success = false,
                        ErrorMessage = upgradeValidation.ErrorMessage,
                        Item = item;
                    };
                }

                // Upgrade işlemini gerçekleştir;
                var upgradeResult = await ProcessItemUpgradeAsync(item, userId, upgradeRequest);

                if (upgradeResult.Success)
                {
                    // Item'ı güncelle;
                    ApplyItemUpgrade(item, upgradeResult);

                    // Event tetikle;
                    OnItemUpgraded(new ItemUpgradedEventArgs;
                    {
                        Item = item,
                        OldItem = upgradeResult.OldItemState,
                        UpgradeRequest = upgradeRequest,
                        Result = upgradeResult,
                        UserId = userId,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogDebug("Item upgraded: {ItemId} ({TemplateId}) by user {UserId}",
                        itemId, item.TemplateId, userId);
                }

                return upgradeResult;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        #endregion;

        #region Public Methods - Query Operations;

        /// <summary>
        /// Item'ı ID ile getirir.
        /// </summary>
        public ItemInstance GetItemById(string itemId)
        {
            ValidateItemId(itemId);

            _lock.EnterReadLock();
            try
            {
                return _activeItems.TryGetValue(itemId, out var item) ? item : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Sahibe ait item'ları getirir.
        /// </summary>
        public IReadOnlyList<ItemInstance> GetItemsByOwner(string ownerId, bool includeEquipped = false)
        {
            ValidateOwnerId(ownerId);

            _lock.EnterReadLock();
            try
            {
                return _activeItems.Values;
                    .Where(item => item.OwnerId == ownerId &&
                          (includeEquipped || !item.IsEquipped))
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Template ID'sine göre item'ları getirir.
        /// </summary>
        public IReadOnlyList<ItemInstance> GetItemsByTemplate(string templateId)
        {
            ValidateTemplateId(templateId);

            _lock.EnterReadLock();
            try
            {
                return _activeItems.Values;
                    .Where(item => item.TemplateId == templateId)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Kategoriye göre item'ları getirir.
        /// </summary>
        public IReadOnlyList<ItemInstance> GetItemsByCategory(string categoryId)
        {
            ValidateCategoryId(categoryId);

            _lock.EnterReadLock();
            try
            {
                if (!_itemTemplates.Values.Any(t => t.CategoryId == categoryId))
                {
                    return new List<ItemInstance>();
                }

                return _activeItems.Values;
                    .Where(item =>
                        _itemTemplates.TryGetValue(item.TemplateId, out var template) &&
                        template.CategoryId == categoryId)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Rarity'ye göre item'ları getirir.
        /// </summary>
        public IReadOnlyList<ItemInstance> GetItemsByRarity(ItemRarity rarity)
        {
            _lock.EnterReadLock();
            try
            {
                return _activeItems.Values;
                    .Where(item => item.Rarity == rarity)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Item'ları ara.
        /// </summary>
        public IReadOnlyList<ItemInstance> SearchItems(ItemSearchCriteria criteria)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            _lock.EnterReadLock();
            try
            {
                var query = _activeItems.Values.AsEnumerable();

                // Sahip filtreleme;
                if (!string.IsNullOrEmpty(criteria.OwnerId))
                {
                    query = query.Where(item => item.OwnerId == criteria.OwnerId);
                }

                // Template filtreleme;
                if (!string.IsNullOrEmpty(criteria.TemplateId))
                {
                    query = query.Where(item => item.TemplateId == criteria.TemplateId);
                }

                // Kategori filtreleme;
                if (!string.IsNullOrEmpty(criteria.CategoryId))
                {
                    query = query.Where(item =>
                        _itemTemplates.TryGetValue(item.TemplateId, out var template) &&
                        template.CategoryId == criteria.CategoryId);
                }

                // Rarity filtreleme;
                if (criteria.Rarity.HasValue)
                {
                    query = query.Where(item => item.Rarity == criteria.Rarity.Value);
                }

                // Seviye filtreleme;
                if (criteria.MinLevel.HasValue)
                {
                    query = query.Where(item => item.Level >= criteria.MinLevel.Value);
                }
                if (criteria.MaxLevel.HasValue)
                {
                    query = query.Where(item => item.Level <= criteria.MaxLevel.Value);
                }

                // İsimle arama;
                if (!string.IsNullOrEmpty(criteria.NameContains))
                {
                    query = query.Where(item =>
                        item.Name.IndexOf(criteria.NameContains, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // Açıklamayla arama;
                if (!string.IsNullOrEmpty(criteria.DescriptionContains))
                {
                    query = query.Where(item =>
                        item.Description.IndexOf(criteria.DescriptionContains, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // Sıralama;
                query = criteria.SortBy switch;
                {
                    ItemSortBy.Name => criteria.SortDescending;
                        ? query.OrderByDescending(item => item.Name)
                        : query.OrderBy(item => item.Name),
                    ItemSortBy.Level => criteria.SortDescending;
                        ? query.OrderByDescending(item => item.Level)
                        : query.OrderBy(item => item.Level),
                    ItemSortBy.Rarity => criteria.SortDescending;
                        ? query.OrderByDescending(item => item.Rarity)
                        : query.OrderBy(item => item.Rarity),
                    ItemSortBy.Value => criteria.SortDescending;
                        ? query.OrderByDescending(item => item.Value)
                        : query.OrderBy(item => item.Value),
                    ItemSortBy.CreatedDate => criteria.SortDescending;
                        ? query.OrderByDescending(item => item.CreatedDate)
                        : query.OrderBy(item => item.CreatedDate),
                    _ => criteria.SortDescending;
                        ? query.OrderByDescending(item => item.CreatedDate)
                        : query.OrderBy(item => item.CreatedDate)
                };

                // Sayfalama;
                if (criteria.PageSize > 0)
                {
                    query = query.Skip(criteria.PageIndex * criteria.PageSize)
                                .Take(criteria.PageSize);
                }

                return query.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Item istatistiklerini getirir.
        /// </summary>
        public ItemStatistics GetStatistics()
        {
            _lock.EnterReadLock();
            try
            {
                var items = _activeItems.Values.ToList();

                return new ItemStatistics;
                {
                    TotalItems = items.Count,
                    TotalValue = items.Sum(item => item.Value * item.Quantity),
                    AverageLevel = items.Any() ? items.Average(item => item.Level) : 0,
                    RarityDistribution = items;
                        .GroupBy(item => item.Rarity)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    CategoryDistribution = items;
                        .Where(item => _itemTemplates.TryGetValue(item.TemplateId, out var template))
                        .GroupBy(item => _itemTemplates[item.TemplateId].CategoryId)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    MostCommonItem = items;
                        .GroupBy(item => item.TemplateId)
                        .OrderByDescending(g => g.Count())
                        .Select(g => new;
                        {
                            TemplateId = g.Key,
                            Count = g.Count(),
                            Template = _itemTemplates.GetValueOrDefault(g.Key)?.Name;
                        })
                        .FirstOrDefault()
                };
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        #endregion;

        #region Public Methods - Template Management;

        /// <summary>
        /// Item template'ini getirir.
        /// </summary>
        public ItemTemplate GetTemplate(string templateId)
        {
            ValidateTemplateId(templateId);

            _lock.EnterReadLock();
            try
            {
                return _itemTemplates.TryGetValue(templateId, out var template) ? template : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Tüm template'leri getirir.
        /// </summary>
        public IReadOnlyList<ItemTemplate> GetAllTemplates()
        {
            _lock.EnterReadLock();
            try
            {
                return _itemTemplates.Values.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Kategoriye göre template'leri getirir.
        /// </summary>
        public IReadOnlyList<ItemTemplate> GetTemplatesByCategory(string categoryId)
        {
            ValidateCategoryId(categoryId);

            _lock.EnterReadLock();
            try
            {
                return _itemTemplates.Values;
                    .Where(template => template.CategoryId == categoryId)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Yeni template ekler.
        /// </summary>
        public bool AddTemplate(ItemTemplate template)
        {
            ValidateTemplate(template);

            _lock.EnterWriteLock();
            try
            {
                if (_itemTemplates.ContainsKey(template.Id))
                {
                    return false;
                }

                _itemTemplates[template.Id] = template;

                _logger.LogDebug("Template added: {TemplateId} ({TemplateName})", template.Id, template.Name);

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Template'i günceller.
        /// </summary>
        public bool UpdateTemplate(string templateId, ItemTemplate updatedTemplate)
        {
            ValidateTemplateId(templateId);
            ValidateTemplate(updatedTemplate);

            if (templateId != updatedTemplate.Id)
            {
                throw new ArgumentException("Template ID mismatch", nameof(updatedTemplate));
            }

            _lock.EnterWriteLock();
            try
            {
                if (!_itemTemplates.ContainsKey(templateId))
                {
                    return false;
                }

                _itemTemplates[templateId] = updatedTemplate;

                _logger.LogDebug("Template updated: {TemplateId}", templateId);

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Template'i kaldırır.
        /// </summary>
        public bool RemoveTemplate(string templateId)
        {
            ValidateTemplateId(templateId);

            _lock.EnterWriteLock();
            try
            {
                // Template'e bağlı aktif item'ları kontrol et;
                var dependentItems = _activeItems.Values;
                    .Where(item => item.TemplateId == templateId)
                    .ToList();

                if (dependentItems.Any())
                {
                    throw new ItemSystemException($"Cannot remove template {templateId}: {dependentItems.Count} active items depend on it");
                }

                return _itemTemplates.Remove(templateId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        #endregion;

        #region Private Methods - Initialization;

        private void InitializeSystem()
        {
            try
            {
                // Varsayılan kategorileri oluştur;
                CreateDefaultCategories();

                // Varsayılan template'leri oluştur;
                CreateDefaultTemplates();

                _logger.LogInformation("ItemSystem default data initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ItemSystem default data");
                throw new ItemSystemException("ItemSystem initialization failed", ex);
            }
        }

        private async Task LoadDefaultItemsAsync()
        {
            try
            {
                // Varsayılan item'ları JSON'dan yükle;
                var defaultItemsPath = _configuration.DefaultItemsPath;
                if (!string.IsNullOrEmpty(defaultItemsPath) && System.IO.File.Exists(defaultItemsPath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(defaultItemsPath);
                    var defaultItems = JsonSerializer.Deserialize<DefaultItemsConfig>(json);

                    if (defaultItems?.Templates != null)
                    {
                        foreach (var template in defaultItems.Templates)
                        {
                            if (!_itemTemplates.ContainsKey(template.Id))
                            {
                                _itemTemplates[template.Id] = template;
                            }
                        }
                    }

                    if (defaultItems?.Categories != null)
                    {
                        foreach (var category in defaultItems.Categories)
                        {
                            if (!_itemCategories.ContainsKey(category.Id))
                            {
                                _itemCategories[category.Id] = category;
                            }
                        }
                    }

                    _logger.LogInformation("Loaded {TemplateCount} templates and {CategoryCount} categories from default items",
                        defaultItems?.Templates?.Count ?? 0, defaultItems?.Categories?.Count ?? 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load default items");
            }
        }

        private void CreateDefaultCategories()
        {
            var defaultCategories = new[]
            {
                new ItemCategory("weapon", "Weapon", "Combat weapons"),
                new ItemCategory("armor", "Armor", "Protective gear"),
                new ItemCategory("consumable", "Consumable", "Items that can be used"),
                new ItemCategory("material", "Material", "Crafting materials"),
                new ItemCategory("quest", "Quest", "Quest-related items"),
                new ItemCategory("key", "Key", "Keys and access items"),
                new ItemCategory("currency", "Currency", "Game currency"),
                new ItemCategory("misc", "Miscellaneous", "Other items")
            };

            foreach (var category in defaultCategories)
            {
                _itemCategories[category.Id] = category;
            }
        }

        private void CreateDefaultTemplates()
        {
            // Varsayılan weapon template;
            var swordTemplate = new ItemTemplate("sword_basic", "Basic Sword", "A simple iron sword")
            {
                CategoryId = "weapon",
                MaxStackSize = 1,
                BaseValue = 100,
                BaseLevel = 1,
                Rarity = ItemRarity.Common,
                EquipmentSlot = EquipmentSlot.MainHand,
                Stats = new Dictionary<string, float>
                {
                    ["damage"] = 10,
                    ["attack_speed"] = 1.0f,
                    ["durability"] = 100;
                },
                Requirements = new Dictionary<string, object>
                {
                    ["level"] = 1;
                },
                Properties = new Dictionary<string, object>
                {
                    ["weapon_type"] = "sword",
                    ["material"] = "iron"
                }
            };

            // Varsayılan consumable template;
            var potionTemplate = new ItemTemplate("potion_health", "Health Potion", "Restores health")
            {
                CategoryId = "consumable",
                MaxStackSize = 20,
                BaseValue = 50,
                BaseLevel = 1,
                Rarity = ItemRarity.Common,
                IsConsumable = true,
                UseEffects = new List<ItemEffect>
                {
                    new ItemEffect;
                    {
                        Type = EffectType.HealthRestore,
                        Value = 50,
                        Duration = 0;
                    }
                }
            };

            AddTemplate(swordTemplate);
            AddTemplate(potionTemplate);
        }

        #endregion;

        #region Private Methods - Validation;

        private SystemValidationResult ValidateTemplates()
        {
            var result = new SystemValidationResult { Area = "Templates" };

            foreach (var template in _itemTemplates.Values)
            {
                // Template ID validasyonu;
                if (string.IsNullOrWhiteSpace(template.Id))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "TEMPLATE_ID_EMPTY",
                        Message = "Template ID cannot be empty",
                        Severity = ValidationSeverity.Critical;
                    });
                }

                // Kategori validasyonu;
                if (!string.IsNullOrEmpty(template.CategoryId) &&
                    !_itemCategories.ContainsKey(template.CategoryId))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "INVALID_CATEGORY",
                        Message = $"Template {template.Id} references invalid category: {template.CategoryId}",
                        Severity = ValidationSeverity.Warning;
                    });
                }

                // Max stack size validasyonu;
                if (template.MaxStackSize <= 0)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "INVALID_STACK_SIZE",
                        Message = $"Template {template.Id} has invalid max stack size: {template.MaxStackSize}",
                        Severity = ValidationSeverity.Warning;
                    });
                }
            }

            return result;
        }

        private SystemValidationResult ValidateCategories()
        {
            var result = new SystemValidationResult { Area = "Categories" };

            foreach (var category in _itemCategories.Values)
            {
                if (string.IsNullOrWhiteSpace(category.Id))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "CATEGORY_ID_EMPTY",
                        Message = "Category ID cannot be empty",
                        Severity = ValidationSeverity.Critical;
                    });
                }
            }

            return result;
        }

        private SystemValidationResult ValidateDependencies()
        {
            var result = new SystemValidationResult { Area = "Dependencies" };

            // Template referansları;
            foreach (var item in _activeItems.Values)
            {
                if (!_itemTemplates.ContainsKey(item.TemplateId))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "MISSING_TEMPLATE",
                        Message = $"Item {item.InstanceId} references missing template: {item.TemplateId}",
                        Severity = ValidationSeverity.Critical;
                    });
                }
            }

            return result;
        }

        private async Task<SystemValidationResult> ValidatePerformanceAsync()
        {
            var result = new SystemValidationResult { Area = "Performance" };

            try
            {
                // Item sayısı kontrolü;
                if (_activeItems.Count > _configuration.MaxActiveItems)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "TOO_MANY_ITEMS",
                        Message = $"Too many active items: {_activeItems.Count} (max: {_configuration.MaxActiveItems})",
                        Severity = ValidationSeverity.Warning;
                    });
                }

                // Template sayısı kontrolü;
                if (_itemTemplates.Count > _configuration.MaxTemplates)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "TOO_MANY_TEMPLATES",
                        Message = $"Too many templates: {_itemTemplates.Count} (max: {_configuration.MaxTemplates})",
                        Severity = ValidationSeverity.Warning;
                    });
                }

                // Performans testi;
                var startTime = DateTime.UtcNow;

                // 1000 item için arama testi;
                for (int i = 0; i < Math.Min(1000, _activeItems.Count); i++)
                {
                    var item = _activeItems.Values.ElementAt(i % _activeItems.Count);
                    var found = GetItemById(item.InstanceId);
                }

                var elapsed = DateTime.UtcNow - startTime;

                if (elapsed.TotalMilliseconds > 100)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "PERFORMANCE_ISSUE",
                        Message = $"Item lookup performance issue: {elapsed.TotalMilliseconds}ms for 1000 lookups",
                        Severity = ValidationSeverity.Warning;
                    });
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "PERFORMANCE_TEST_FAILED",
                    Message = $"Performance test failed: {ex.Message}",
                    Severity = ValidationSeverity.Warning;
                });
            }

            return result;
        }

        private UseValidationResult ValidateItemUse(ItemInstance item, string userId, ItemUseContext context)
        {
            var result = new UseValidationResult();

            // Item kullanılabilir mi?
            if (!item.IsUsable)
            {
                result.IsValid = false;
                result.ErrorMessage = "Item is not usable";
                return result;
            }

            // Item sahibi mi?
            if (!string.IsNullOrEmpty(item.OwnerId) && item.OwnerId != userId)
            {
                result.IsValid = false;
                result.ErrorMessage = "User does not own this item";
                return result;
            }

            // Item tükenmiş mi?
            if (item.IsConsumable && item.Quantity <= 0)
            {
                result.IsValid = false;
                result.ErrorMessage = "Item quantity is zero";
                return result;
            }

            // Seviye gereksinimi;
            if (item.LevelRequirement > 0 && context?.UserLevel < item.LevelRequirement)
            {
                result.IsValid = false;
                result.ErrorMessage = $"User level {context.UserLevel} is below required level {item.LevelRequirement}";
                return result;
            }

            // Cooldown kontrolü;
            if (item.CooldownEnd.HasValue && item.CooldownEnd > DateTime.UtcNow)
            {
                result.IsValid = false;
                result.ErrorMessage = "Item is on cooldown";
                return result;
            }

            // Context validasyonu;
            if (context != null)
            {
                // Location gereksinimi;
                if (!string.IsNullOrEmpty(item.RequiredLocation) &&
                    item.RequiredLocation != context.Location)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Item can only be used in {item.RequiredLocation}";
                    return result;
                }

                // Target gereksinimi;
                if (item.RequiresTarget && string.IsNullOrEmpty(context.TargetId))
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Item requires a target";
                    return result;
                }
            }

            result.IsValid = true;
            return result;
        }

        private CombineValidationResult ValidateItemCombine(ItemInstance item1, ItemInstance item2, string userId, Dictionary<string, object> options)
        {
            var result = new CombineValidationResult();

            // Item'lar birleştirilebilir mi?
            if (!item1.CanCombine || !item2.CanCombine)
            {
                result.IsValid = false;
                result.ErrorMessage = "One or both items cannot be combined";
                return result;
            }

            // Aynı item mı?
            if (item1.InstanceId == item2.InstanceId)
            {
                result.IsValid = false;
                result.ErrorMessage = "Cannot combine an item with itself";
                return result;
            }

            // Sahiplik kontrolü;
            if (item1.OwnerId != userId || item2.OwnerId != userId)
            {
                result.IsValid = false;
                result.ErrorMessage = "User does not own both items";
                return result;
            }

            // Item'lar donanmış mı?
            if (item1.IsEquipped || item2.IsEquipped)
            {
                result.IsValid = false;
                result.ErrorMessage = "Cannot combine equipped items";
                return result;
            }

            // Birleştirme kuralları;
            if (!ValidateCombineRules(item1, item2))
            {
                result.IsValid = false;
                result.ErrorMessage = "Items cannot be combined based on combine rules";
                return result;
            }

            result.IsValid = true;
            return result;
        }

        private UpgradeValidationResult ValidateItemUpgrade(ItemInstance item, string userId, ItemUpgradeRequest request)
        {
            var result = new UpgradeValidationResult();

            // Item upgrade edilebilir mi?
            if (!item.CanUpgrade)
            {
                result.IsValid = false;
                result.ErrorMessage = "Item cannot be upgraded";
                return result;
            }

            // Maksimum seviye kontrolü;
            if (item.Level >= item.MaxLevel)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Item is already at maximum level ({item.MaxLevel})";
                return result;
            }

            // Sahiplik kontrolü;
            if (item.OwnerId != userId)
            {
                result.IsValid = false;
                result.ErrorMessage = "User does not own this item";
                return result;
            }

            // Item donanmış mı?
            if (item.IsEquipped && !request.AllowUpgradeEquipped)
            {
                result.IsValid = false;
                result.ErrorMessage = "Cannot upgrade equipped item";
                return result;
            }

            // Kaynak kontrolü;
            if (request.RequiredResources != null)
            {
                foreach (var resource in request.RequiredResources)
                {
                    // TODO: Kullanıcının kaynağı kontrol et;
                }
            }

            result.IsValid = true;
            return result;
        }

        private bool ValidateCombineRules(ItemInstance item1, ItemInstance item2)
        {
            // Template'leri al;
            if (!_itemTemplates.TryGetValue(item1.TemplateId, out var template1) ||
                !_itemTemplates.TryGetValue(item2.TemplateId, out var template2))
            {
                return false;
            }

            // Aynı kategoride mi?
            if (template1.CategoryId != template2.CategoryId)
            {
                // Farklı kategoriler birleştirilebilir mi?
                if (!template1.AllowedCombineCategories?.Contains(template2.CategoryId) == true &&
                    !template2.AllowedCombineCategories?.Contains(template1.CategoryId) == true)
                {
                    return false;
                }
            }

            // Seviye farkı kontrolü;
            var levelDiff = Math.Abs(item1.Level - item2.Level);
            if (levelDiff > template1.MaxCombineLevelDiff)
            {
                return false;
            }

            // Rarity kontrolü;
            if (template1.RequiredCombineRarity != ItemRarity.None &&
                template1.RequiredCombineRarity != item2.Rarity)
            {
                return false;
            }

            return true;
        }

        #endregion;

        #region Private Methods - Processing;

        private async Task<ItemUseResult> ProcessItemUseAsync(ItemInstance item, string userId, ItemUseContext context)
        {
            var result = new ItemUseResult;
            {
                Item = item,
                UserId = userId,
                Context = context,
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Template'i al;
                if (!_itemTemplates.TryGetValue(item.TemplateId, out var template))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Template not found: {item.TemplateId}";
                    return result;
                }

                // Kullanım etkilerini uygula;
                if (template.UseEffects != null && template.UseEffects.Any())
                {
                    result.Effects = new List<AppliedEffect>();

                    foreach (var effect in template.UseEffects)
                    {
                        var appliedEffect = await ApplyItemEffectAsync(effect, item, userId, context);
                        result.Effects.Add(appliedEffect);

                        if (!appliedEffect.Success)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"Effect application failed: {appliedEffect.ErrorMessage}";
                            return result;
                        }
                    }
                }

                // Özel kullanım işlemi;
                if (template.CustomUseHandler != null)
                {
                    var customResult = await template.CustomUseHandler(item, userId, context);
                    if (!customResult.Success)
                    {
                        result.Success = false;
                        result.ErrorMessage = customResult.ErrorMessage;
                        return result;
                    }

                    result.CustomData = customResult.Data;
                }

                // Cooldown ayarla;
                if (template.CooldownSeconds > 0)
                {
                    item.CooldownEnd = DateTime.UtcNow.AddSeconds(template.CooldownSeconds);
                }

                result.Success = true;
                result.Message = $"Used {item.Name} successfully";

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item use: {ItemId}", item.InstanceId);

                result.Success = false;
                result.ErrorMessage = $"Error using item: {ex.Message}";

                return result;
            }
        }

        private async Task<AppliedEffect> ApplyItemEffectAsync(ItemEffect effect, ItemInstance item, string userId, ItemUseContext context)
        {
            var appliedEffect = new AppliedEffect;
            {
                EffectType = effect.Type,
                Value = effect.Value,
                Duration = effect.Duration,
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Etki tipine göre işle;
                switch (effect.Type)
                {
                    case EffectType.HealthRestore:
                        // TODO: Kullanıcının canını yenile;
                        appliedEffect.Message = $"Restored {effect.Value} health";
                        break;

                    case EffectType.ManaRestore:
                        // TODO: Kullanıcının manasını yenile;
                        appliedEffect.Message = $"Restored {effect.Value} mana";
                        break;

                    case EffectType.StatBoost:
                        // TODO: İstatistik artışı uygula;
                        appliedEffect.Message = $"Boosted {effect.StatName} by {effect.Value}";
                        break;

                    case EffectType.Damage:
                        // TODO: Hasar uygula;
                        if (!string.IsNullOrEmpty(context?.TargetId))
                        {
                            appliedEffect.Message = $"Dealt {effect.Value} damage to {context.TargetId}";
                        }
                        break;

                    case EffectType.Buff:
                        // TODO: Buff uygula;
                        appliedEffect.Message = $"Applied {effect.BuffName} buff";
                        break;

                    case EffectType.Teleport:
                        // TODO: Işınlama;
                        appliedEffect.Message = $"Teleported to {effect.TargetLocation}";
                        break;

                    default:
                        appliedEffect.Success = false;
                        appliedEffect.ErrorMessage = $"Unknown effect type: {effect.Type}";
                        return appliedEffect;
                }

                appliedEffect.Success = true;
                return appliedEffect;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying item effect: {EffectType}", effect.Type);

                appliedEffect.Success = false;
                appliedEffect.ErrorMessage = ex.Message;

                return appliedEffect;
            }
        }

        private async Task<ItemCombineResult> ProcessItemCombineAsync(
            ItemInstance item1,
            ItemInstance item2,
            string userId,
            Dictionary<string, object> options)
        {
            var result = new ItemCombineResult;
            {
                SourceItems = new[] { item1, item2 },
                UserId = userId,
                Options = options,
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Template'leri al;
                if (!_itemTemplates.TryGetValue(item1.TemplateId, out var template1) ||
                    !_itemTemplates.TryGetValue(item2.TemplateId, out var template2))
                {
                    result.Success = false;
                    result.ErrorMessage = "Failed to get item templates";
                    return result;
                }

                // Sonuç template'ini belirle;
                string resultTemplateId;

                if (template1.CombineResultTemplateId != null)
                {
                    resultTemplateId = template1.CombineResultTemplateId;
                }
                else if (template2.CombineResultTemplateId != null)
                {
                    resultTemplateId = template2.CombineResultTemplateId;
                }
                else if (template1.CombineWithTemplateId == item2.TemplateId)
                {
                    resultTemplateId = template1.CombineResultId;
                }
                else if (template2.CombineWithTemplateId == item1.TemplateId)
                {
                    resultTemplateId = template2.CombineResultId;
                }
                else;
                {
                    // Varsayılan birleştirme mantığı;
                    resultTemplateId = DetermineDefaultCombineResult(item1, item2);
                }

                if (string.IsNullOrEmpty(resultTemplateId))
                {
                    result.Success = false;
                    result.ErrorMessage = "Could not determine result template";
                    return result;
                }

                // Yeni item oluştur;
                var resultTemplate = GetTemplate(resultTemplateId);
                if (resultTemplate == null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Result template not found: {resultTemplateId}";
                    return result;
                }

                // Özellikleri birleştir;
                var combinedProperties = CombineItemProperties(item1, item2, resultTemplate);

                // Seviyeyi hesapla;
                int combinedLevel = CalculateCombinedLevel(item1, item2);

                // Yeni item oluştur;
                var resultItem = await CreateItemAsync(
                    resultTemplateId,
                    userId,
                    1,
                    combinedProperties);

                // Seviyeyi ayarla;
                resultItem.Level = combinedLevel;

                // Değeri hesapla;
                resultItem.Value = CalculateCombinedValue(item1, item2, resultItem);

                // Rarity'yi hesapla;
                resultItem.Rarity = CalculateCombinedRarity(item1, item2);

                result.Success = true;
                result.ResultItem = resultItem;
                result.Message = $"Combined {item1.Name} and {item2.Name} into {resultItem.Name}";

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error combining items: {ItemId1}, {ItemId2}", item1.InstanceId, item2.InstanceId);

                result.Success = false;
                result.ErrorMessage = $"Error combining items: {ex.Message}";

                return result;
            }
        }

        private async Task<ItemUpgradeResult> ProcessItemUpgradeAsync(
            ItemInstance item,
            string userId,
            ItemUpgradeRequest request)
        {
            var result = new ItemUpgradeResult;
            {
                Item = item,
                UserId = userId,
                UpgradeRequest = request,
                OldItemState = _itemSerializer.CloneItem(item),
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Template'i al;
                if (!_itemTemplates.TryGetValue(item.TemplateId, out var template))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Template not found: {item.TemplateId}";
                    return result;
                }

                // Başarı şansını hesapla;
                float successChance = CalculateUpgradeSuccessChance(item, request);
                bool success = CheckUpgradeSuccess(successChance, request);

                if (!success)
                {
                    // Upgrade başarısız;
                    result.Success = false;
                    result.UpgradeSuccess = false;
                    result.Message = "Upgrade failed";

                    // Başarısızlık etkileri;
                    if (request.FailurePenalty)
                    {
                        ApplyUpgradeFailurePenalty(item, request);
                        result.PenaltyApplied = true;
                    }

                    return result;
                }

                // Upgrade başarılı;
                result.Success = true;
                result.UpgradeSuccess = true;

                // Seviyeyi artır;
                item.Level++;

                // İstatistikleri artır;
                ApplyUpgradeStatIncreases(item, template);

                // Değeri artır;
                item.Value = CalculateUpgradedValue(item);

                // Rarity'i geliştir (şansa bağlı)
                if (request.AllowRarityUpgrade && CheckRarityUpgradeChance(item))
                {
                    item.Rarity = UpgradeRarity(item.Rarity);
                    result.RarityUpgraded = true;
                }

                // Durability yenile;
                if (request.RestoreDurability)
                {
                    item.Durability = item.MaxDurability;
                }

                result.NewLevel = item.Level;
                result.NewRarity = item.Rarity;
                result.Message = $"Successfully upgraded {item.Name} to level {item.Level}";

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error upgrading item: {ItemId}", item.InstanceId);

                result.Success = false;
                result.ErrorMessage = $"Error upgrading item: {ex.Message}";

                return result;
            }
        }

        private void ApplyItemUpdates(ItemInstance item, ItemUpdateRequest updateRequest)
        {
            // Miktar güncelleme;
            if (updateRequest.QuantityChange.HasValue)
            {
                item.Quantity = Math.Max(0, item.Quantity + updateRequest.QuantityChange.Value);
            }

            // Seviye güncelleme;
            if (updateRequest.LevelChange.HasValue)
            {
                item.Level = Math.Max(1, Math.Min(item.MaxLevel, item.Level + updateRequest.LevelChange.Value));
            }

            // Durability güncelleme;
            if (updateRequest.DurabilityChange.HasValue)
            {
                item.Durability = Math.Max(0, Math.Min(item.MaxDurability, item.Durability + updateRequest.DurabilityChange.Value));
            }

            // Özellikler güncelleme;
            if (updateRequest.Properties != null)
            {
                foreach (var prop in updateRequest.Properties)
                {
                    item.Properties[prop.Key] = prop.Value;
                }
            }

            // İstatistikler güncelleme;
            if (updateRequest.Stats != null)
            {
                foreach (var stat in updateRequest.Stats)
                {
                    item.Stats[stat.Key] = stat.Value;
                }
            }

            // Donanma durumu;
            if (updateRequest.IsEquipped.HasValue)
            {
                item.IsEquipped = updateRequest.IsEquipped.Value;
            }

            // Kilit durumu;
            if (updateRequest.IsLocked.HasValue)
            {
                item.IsLocked = updateRequest.IsLocked.Value;
            }

            // Açıklama güncelleme;
            if (!string.IsNullOrEmpty(updateRequest.Description))
            {
                item.Description = updateRequest.Description;
            }

            // İsim güncelleme;
            if (!string.IsNullOrEmpty(updateRequest.Name))
            {
                item.Name = updateRequest.Name;
            }

            // Son güncelleme zamanı;
            item.LastUpdated = DateTime.UtcNow;
        }

        private void ApplyItemUpgrade(ItemInstance item, ItemUpgradeResult upgradeResult)
        {
            // Seviye artışı;
            if (upgradeResult.NewLevel.HasValue)
            {
                item.Level = upgradeResult.NewLevel.Value;
            }

            // Rarity artışı;
            if (upgradeResult.RarityUpgraded && upgradeResult.NewRarity.HasValue)
            {
                item.Rarity = upgradeResult.NewRarity.Value;
            }

            // İstatistik artışları;
            if (upgradeResult.StatIncreases != null)
            {
                foreach (var statIncrease in upgradeResult.StatIncreases)
                {
                    if (item.Stats.ContainsKey(statIncrease.Key))
                    {
                        item.Stats[statIncrease.Key] += statIncrease.Value;
                    }
                    else;
                    {
                        item.Stats[statIncrease.Key] = statIncrease.Value;
                    }
                }
            }

            // Değer artışı;
            if (upgradeResult.NewValue.HasValue)
            {
                item.Value = upgradeResult.NewValue.Value;
            }
        }

        #endregion;

        #region Private Methods - Calculation Helpers;

        private string DetermineDefaultCombineResult(ItemInstance item1, ItemInstance item2)
        {
            // Varsayılan birleştirme kuralları;

            // Aynı template'ler -> üst seviye versiyon;
            if (item1.TemplateId == item2.TemplateId)
            {
                return GetUpgradedTemplateId(item1.TemplateId);
            }

            // Weapon + Material -> gelişmiş weapon;
            if (_itemTemplates[item1.TemplateId].CategoryId == "weapon" &&
                _itemTemplates[item2.TemplateId].CategoryId == "material")
            {
                return GetEnhancedWeaponTemplateId(item1.TemplateId, item2.TemplateId);
            }

            // Armor + Material -> gelişmiş armor;
            if (_itemTemplates[item1.TemplateId].CategoryId == "armor" &&
                _itemTemplates[item2.TemplateId].CategoryId == "material")
            {
                return GetEnhancedArmorTemplateId(item1.TemplateId, item2.TemplateId);
            }

            // Potion + Potion -> büyük potion;
            if (_itemTemplates[item1.TemplateId].CategoryId == "consumable" &&
                _itemTemplates[item2.TemplateId].CategoryId == "consumable")
            {
                return GetGreaterPotionTemplateId(item1.TemplateId, item2.TemplateId);
            }

            return null;
        }

        private Dictionary<string, object> CombineItemProperties(ItemInstance item1, ItemInstance item2, ItemTemplate resultTemplate)
        {
            var properties = new Dictionary<string, object>();

            // Item1 özelliklerini ekle;
            foreach (var prop in item1.Properties)
            {
                properties[prop.Key] = prop.Value;
            }

            // Item2 özelliklerini ekle (çakışmalarda item2 öncelikli)
            foreach (var prop in item2.Properties)
            {
                properties[prop.Key] = prop.Value;
            }

            // Result template özelliklerini ekle;
            if (resultTemplate.Properties != null)
            {
                foreach (var prop in resultTemplate.Properties)
                {
                    properties[prop.Key] = prop.Value;
                }
            }

            // Birleştirme özellikleri;
            properties["combined_from"] = new[] { item1.InstanceId, item2.InstanceId };
            properties["combined_date"] = DateTime.UtcNow;

            return properties;
        }

        private int CalculateCombinedLevel(ItemInstance item1, ItemInstance item2)
        {
            // Ortalama seviye, yukarı yuvarlanmış;
            return (int)Math.Ceiling((item1.Level + item2.Level) / 2.0);
        }

        private int CalculateCombinedValue(ItemInstance item1, ItemInstance item2, ItemInstance resultItem)
        {
            // Kaynak item'ların toplam değerinin %80'i + result template baz değeri;
            int totalSourceValue = (item1.Value * item1.Quantity) + (item2.Value * item2.Quantity);
            int combinedValue = (int)(totalSourceValue * 0.8) + resultItem.BaseValue;

            return Math.Max(resultItem.BaseValue, combinedValue);
        }

        private ItemRarity CalculateCombinedRarity(ItemInstance item1, ItemInstance item2)
        {
            // Daha yüksek rarity'yi al, küçük şansla bir seviye daha yükselt;
            ItemRarity baseRarity = item1.Rarity > item2.Rarity ? item1.Rarity : item2.Rarity;

            Random rand = new Random();
            if (rand.NextDouble() < 0.1) // %10 şans;
            {
                return UpgradeRarity(baseRarity);
            }

            return baseRarity;
        }

        private float CalculateUpgradeSuccessChance(ItemInstance item, ItemUpgradeRequest request)
        {
            float baseChance = 0.8f; // %80 temel şans;

            // Seviye faktörü (yüksek seviyelerde şans azalır)
            float levelFactor = 1.0f - (item.Level / (float)item.MaxLevel * 0.5f);

            // Rarity faktörü (nadir item'larda şans azalır)
            float rarityFactor = 1.0f - ((int)item.Rarity * 0.1f);

            // Upgrade materyali faktörü;
            float materialFactor = 1.0f;
            if (request.UpgradeMaterials != null)
            {
                materialFactor += request.UpgradeMaterials.Count * 0.05f;
            }

            // Son şansı hesapla;
            float successChance = baseChance * levelFactor * rarityFactor * materialFactor;

            // Minimum %10, maksimum %95 şans;
            return Math.Max(0.1f, Math.Min(0.95f, successChance));
        }

        private bool CheckUpgradeSuccess(float successChance, ItemUpgradeRequest request)
        {
            Random rand = new Random();
            float roll = (float)rand.NextDouble();

            // Lucky charm bonusu;
            if (request.UseLuckyCharm)
            {
                successChance *= 1.2f; // %20 bonus;
            }

            return roll <= successChance;
        }

        private bool CheckRarityUpgradeChance(ItemInstance item)
        {
            // Rarity upgrade şansı;
            float baseChance = 0.05f; // %5 temel şans;

            // Seviye faktörü;
            float levelFactor = item.Level / (float)item.MaxLevel;

            // Son şans;
            float upgradeChance = baseChance * levelFactor;

            Random rand = new Random();
            return rand.NextDouble() <= upgradeChance;
        }

        private ItemRarity UpgradeRarity(ItemRarity currentRarity)
        {
            return currentRarity switch;
            {
                ItemRarity.Common => ItemRarity.Uncommon,
                ItemRarity.Uncommon => ItemRarity.Rare,
                ItemRarity.Rare => ItemRarity.Epic,
                ItemRarity.Epic => ItemRarity.Legendary,
                ItemRarity.Legendary => ItemRarity.Mythic,
                _ => currentRarity;
            };
        }

        private int CalculateUpgradedValue(ItemInstance item)
        {
            // Seviye başına %20 değer artışı;
            float multiplier = 1.0f + (item.Level * 0.2f);

            // Rarity çarpanı;
            float rarityMultiplier = 1.0f + ((int)item.Rarity * 0.5f);

            return (int)(item.BaseValue * multiplier * rarityMultiplier);
        }

        private void ApplyUpgradeStatIncreases(ItemInstance item, ItemTemplate template)
        {
            if (template.UpgradeStatIncreases != null)
            {
                foreach (var statIncrease in template.UpgradeStatIncreases)
                {
                    if (item.Stats.ContainsKey(statIncrease.Key))
                    {
                        item.Stats[statIncrease.Key] += statIncrease.Value;
                    }
                    else;
                    {
                        item.Stats[statIncrease.Key] = statIncrease.Value;
                    }
                }
            }
            else;
            {
                // Varsayılan stat artışları;
                item.Stats["damage"] = item.Stats.GetValueOrDefault("damage", 0) + 5;
                item.Stats["defense"] = item.Stats.GetValueOrDefault("defense", 0) + 3;
            }
        }

        private void ApplyUpgradeFailurePenalty(ItemInstance item, ItemUpgradeRequest request)
        {
            // Durability kaybı;
            if (request.FailureDurabilityLoss > 0)
            {
                item.Durability = Math.Max(0, item.Durability - request.FailureDurabilityLoss);
            }

            // Seviye kaybı (kritik başarısızlık)
            Random rand = new Random();
            if (rand.NextDouble() < 0.1) // %10 şans;
            {
                item.Level = Math.Max(1, item.Level - 1);
            }

            // İstatistik kaybı;
            if (request.FailureStatReduction)
            {
                foreach (var statKey in item.Stats.Keys.ToList())
                {
                    item.Stats[statKey] = Math.Max(0, item.Stats[statKey] - 1);
                }
            }
        }

        #endregion;

        #region Private Methods - Template Helpers;

        private string GetUpgradedTemplateId(string baseTemplateId)
        {
            // Örnek: "sword_basic" -> "sword_improved"
            if (baseTemplateId.EndsWith("_basic"))
            {
                return baseTemplateId.Replace("_basic", "_improved");
            }

            if (baseTemplateId.EndsWith("_improved"))
            {
                return baseTemplateId.Replace("_improved", "_advanced");
            }

            return baseTemplateId + "_plus";
        }

        private string GetEnhancedWeaponTemplateId(string weaponTemplateId, string materialTemplateId)
        {
            // Örnek: "sword_basic" + "iron_ingot" -> "sword_iron"
            var weaponName = weaponTemplateId.Split('_')[0];
            var materialName = materialTemplateId.Split('_')[0];

            return $"{weaponName}_{materialName}";
        }

        private string GetEnhancedArmorTemplateId(string armorTemplateId, string materialTemplateId)
        {
            // Örnek: "chestplate_basic" + "leather" -> "chestplate_leather"
            var armorName = armorTemplateId.Split('_')[0];
            var materialName = materialTemplateId.Split('_')[0];

            return $"{armorName}_{materialName}";
        }

        private string GetGreaterPotionTemplateId(string potionTemplateId1, string potionTemplateId2)
        {
            // Örnek: "potion_health" + "potion_health" -> "potion_health_greater"
            if (potionTemplateId1 == potionTemplateId2)
            {
                return potionTemplateId1 + "_greater";
            }

            // Farklı potion'lar -> kombinasyon potion'u;
            return "potion_combo";
        }

        #endregion;

        #region Private Methods - Validation Helpers;

        private void ValidateItemId(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be null or empty", nameof(itemId));
        }

        private void ValidateTemplateId(string templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));
        }

        private void ValidateCategoryId(string categoryId)
        {
            if (string.IsNullOrWhiteSpace(categoryId))
                throw new ArgumentException("Category ID cannot be null or empty", nameof(categoryId));
        }

        private void ValidateOwnerId(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("Owner ID cannot be null or empty", nameof(ownerId));
        }

        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
        }

        private void ValidateQuantity(int quantity)
        {
            if (quantity <= 0)
                throw new ArgumentException("Quantity must be greater than 0", nameof(quantity));

            if (quantity > _configuration.MaxStackSize)
                throw new ArgumentException($"Quantity cannot exceed max stack size of {_configuration.MaxStackSize}", nameof(quantity));
        }

        private void ValidateTemplate(ItemTemplate template)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));

            if (string.IsNullOrWhiteSpace(template.Id))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(template));

            if (string.IsNullOrWhiteSpace(template.Name))
                throw new ArgumentException("Template name cannot be null or empty", nameof(template));

            if (template.MaxStackSize <= 0)
                throw new ArgumentException("Max stack size must be greater than 0", nameof(template));
        }

        private void ValidateUpdateRequest(ItemUpdateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
        }

        private void ValidateUpgradeRequest(ItemUpgradeRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnItemCreated(ItemCreatedEventArgs e)
        {
            ItemCreated?.Invoke(this, e);
        }

        protected virtual void OnItemDestroyed(ItemDestroyedEventArgs e)
        {
            ItemDestroyed?.Invoke(this, e);
        }

        protected virtual void OnItemUpdated(ItemUpdatedEventArgs e)
        {
            ItemUpdated?.Invoke(this, e);
        }

        protected virtual void OnItemUsed(ItemUsedEventArgs e)
        {
            ItemUsed?.Invoke(this, e);
        }

        protected virtual void OnItemCombined(ItemCombinedEventArgs e)
        {
            ItemCombined?.Invoke(this, e);
        }

        protected virtual void OnItemUpgraded(ItemUpgradedEventArgs e)
        {
            ItemUpgraded?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _lock?.Dispose();

                    // Tüm aktif item'ları dispose et;
                    foreach (var item in _activeItems.Values)
                    {
                        item.Dispose();
                    }

                    _activeItems.Clear();

                    _logger.LogInformation("ItemSystem disposed");
                }

                _isDisposed = true;
            }
        }

        ~ItemSystem()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Item sistemi için arayüz.
    /// </summary>
    public interface IItemSystem : IDisposable
    {
        event EventHandler<ItemCreatedEventArgs> ItemCreated;
        event EventHandler<ItemDestroyedEventArgs> ItemDestroyed;
        event EventHandler<ItemUpdatedEventArgs> ItemUpdated;
        event EventHandler<ItemUsedEventArgs> ItemUsed;
        event EventHandler<ItemCombinedEventArgs> ItemCombined;
        event EventHandler<ItemUpgradedEventArgs> ItemUpgraded;

        int ActiveItemCount { get; }
        int TemplateCount { get; }
        int CategoryCount { get; }
        ItemSystemConfiguration Configuration { get; }
        IItemFactory ItemFactory { get; }
        IItemValidator ItemValidator { get; }

        Task InitializeAsync();
        Task LoadItemDatabaseAsync();
        Task<bool> ValidateSystemAsync();
        void ClearSystem();

        Task<ItemInstance> CreateItemAsync(string templateId, string ownerId = null, int quantity = 1, Dictionary<string, object> properties = null);
        Task<ItemInstance> CloneItemAsync(string itemId, string newOwnerId = null, int quantity = 1);
        bool DestroyItem(string itemId, string reason = null);
        Task<ItemInstance> UpdateItemAsync(string itemId, ItemUpdateRequest updateRequest);
        Task<ItemUseResult> UseItemAsync(string itemId, string userId, ItemUseContext context = null);
        Task<ItemCombineResult> CombineItemsAsync(string itemId1, string itemId2, string userId, Dictionary<string, object> combineOptions = null);
        Task<ItemUpgradeResult> UpgradeItemAsync(string itemId, string userId, ItemUpgradeRequest upgradeRequest);

        ItemInstance GetItemById(string itemId);
        IReadOnlyList<ItemInstance> GetItemsByOwner(string ownerId, bool includeEquipped = false);
        IReadOnlyList<ItemInstance> GetItemsByTemplate(string templateId);
        IReadOnlyList<ItemInstance> GetItemsByCategory(string categoryId);
        IReadOnlyList<ItemInstance> GetItemsByRarity(ItemRarity rarity);
        IReadOnlyList<ItemInstance> SearchItems(ItemSearchCriteria criteria);
        ItemStatistics GetStatistics();

        ItemTemplate GetTemplate(string templateId);
        IReadOnlyList<ItemTemplate> GetAllTemplates();
        IReadOnlyList<ItemTemplate> GetTemplatesByCategory(string categoryId);
        bool AddTemplate(ItemTemplate template);
        bool UpdateTemplate(string templateId, ItemTemplate updatedTemplate);
        bool RemoveTemplate(string templateId);
    }

    /// <summary>
    /// Item instance'ı.
    /// </summary>
    public class ItemInstance : IDisposable
    {
        public string InstanceId { get; set; }
        public string TemplateId { get; set; }
        public string OwnerId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Quantity { get; set; }
        public int MaxStackSize { get; set; }
        public int Level { get; set; }
        public int MaxLevel { get; set; }
        public int BaseValue { get; set; }
        public int Value { get; set; }
        public ItemRarity Rarity { get; set; }
        public ItemCategory Category { get; set; }
        public bool IsEquipped { get; set; }
        public bool IsLocked { get; set; }
        public bool IsBound { get; set; }
        public bool IsConsumable { get; set; }
        public bool IsUsable { get; set; }
        public bool CanCombine { get; set; }
        public bool CanUpgrade { get; set; }
        public float Durability { get; set; }
        public float MaxDurability { get; set; }
        public int UsageCount { get; set; }
        public int LevelRequirement { get; set; }
        public string RequiredLocation { get; set; }
        public bool RequiresTarget { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime? CooldownEnd { get; set; }
        public Dictionary<string, float> Stats { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public List<ItemEnchantment> Enchantments { get; set; }
        public List<ItemSocket> Sockets { get; set; }

        public ItemInstance()
        {
            InstanceId = Guid.NewGuid().ToString();
            CreatedDate = DateTime.UtcNow;
            LastUpdated = DateTime.UtcNow;
            Stats = new Dictionary<string, float>();
            Properties = new Dictionary<string, object>();
            Enchantments = new List<ItemEnchantment>();
            Sockets = new List<ItemSocket>();
            MaxStackSize = 1;
            Level = 1;
            MaxLevel = 100;
            Rarity = ItemRarity.Common;
            CanCombine = true;
            CanUpgrade = true;
            IsUsable = true;
        }

        public void Dispose()
        {
            // Kaynakları temizle;
            Stats?.Clear();
            Properties?.Clear();
            Enchantments?.Clear();
            Sockets?.Clear();
        }
    }

    /// <summary>
    /// Item template'i.
    /// </summary>
    public class ItemTemplate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string CategoryId { get; set; }
        public string SubCategoryId { get; set; }
        public int MaxStackSize { get; set; } = 1;
        public int BaseValue { get; set; } = 0;
        public int BaseLevel { get; set; } = 1;
        public int MaxLevel { get; set; } = 100;
        public ItemRarity Rarity { get; set; } = ItemRarity.Common;
        public EquipmentSlot EquipmentSlot { get; set; } = EquipmentSlot.None;
        public bool IsConsumable { get; set; } = false;
        public bool IsUsable { get; set; } = true;
        public bool IsTradable { get; set; } = true;
        public bool IsSellable { get; set; } = true;
        public bool IsDestroyable { get; set; } = true;
        public bool CanCombine { get; set; } = true;
        public bool CanUpgrade { get; set; } = true;
        public int CooldownSeconds { get; set; } = 0;
        public float Weight { get; set; } = 0f;
        public Dictionary<string, float> Stats { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public List<ItemEffect> UseEffects { get; set; }
        public Dictionary<string, float> UpgradeStatIncreases { get; set; }
        public string CombineWithTemplateId { get; set; }
        public string CombineResultId { get; set; }
        public string CombineResultTemplateId { get; set; }
        public int MaxCombineLevelDiff { get; set; } = 10;
        public ItemRarity RequiredCombineRarity { get; set; } = ItemRarity.None;
        public List<string> AllowedCombineCategories { get; set; }
        public List<ItemRequirement> Requirements { get; set; }
        public Func<ItemInstance, string, ItemUseContext, Task<CustomUseResult>> CustomUseHandler { get; set; }

        public ItemTemplate() { }

        public ItemTemplate(string id, string name, string description = null)
        {
            Id = id;
            Name = name;
            Description = description;
            Stats = new Dictionary<string, float>();
            Properties = new Dictionary<string, object>();
            UseEffects = new List<ItemEffect>();
            UpgradeStatIncreases = new Dictionary<string, float>();
            AllowedCombineCategories = new List<string>();
            Requirements = new List<ItemRequirement>();
        }
    }

    /// <summary>
    /// Item kategorisi.
    /// </summary>
    public class ItemCategory;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ParentCategoryId { get; set; }
        public int SortOrder { get; set; }
        public Dictionary<string, object> Properties { get; set; }

        public ItemCategory() { }

        public ItemCategory(string id, string name, string description = null)
        {
            Id = id;
            Name = name;
            Description = description;
            Properties = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Item sistemi konfigürasyonu.
    /// </summary>
    public class ItemSystemConfiguration;
    {
        public int MaxStackSize { get; set; } = DEFAULT_MAX_STACK_SIZE;
        public int MaxActiveItems { get; set; } = 10000;
        public int MaxTemplates { get; set; } = 1000;
        public int MaxCategories { get; set; } = 50;
        public int DefaultInventorySlots { get; set; } = DEFAULT_INVENTORY_SLOTS;
        public bool EnableItemHistory { get; set; } = true;
        public int MaxItemHistory { get; set; } = 1000;
        public bool EnableAutoCleanup { get; set; } = true;
        public TimeSpan InactiveItemTimeout { get; set; } = TimeSpan.FromDays(30);
        public string DefaultItemsPath { get; set; } = "Config/DefaultItems.json";
        public bool ValidateOnCreate { get; set; } = true;
        public bool ValidateOnUpdate { get; set; } = true;
        public bool EnableCaching { get; set; } = true;
        public int CacheSize { get; set; } = 1000;
    }

    /// <summary>
    /// Item rarity seviyeleri.
    /// </summary>
    public enum ItemRarity;
    {
        None = 0,
        Common = 1,
        Uncommon = 2,
        Rare = 3,
        Epic = 4,
        Legendary = 5,
        Mythic = 6;
    }

    /// <summary>
    /// Ekipman slot'ları.
    /// </summary>
    public enum EquipmentSlot;
    {
        None = 0,
        Head = 1,
        Chest = 2,
        Legs = 3,
        Feet = 4,
        MainHand = 5,
        OffHand = 6,
        TwoHand = 7,
        Neck = 8,
        Ring = 9,
        Trinket = 10,
        Back = 11;
    }

    /// <summary>
    /// Item effect tipleri.
    /// </summary>
    public enum EffectType;
    {
        HealthRestore = 0,
        ManaRestore = 1,
        StatBoost = 2,
        Damage = 3,
        Buff = 4,
        Debuff = 5,
        Teleport = 6,
        Summon = 7,
        Transform = 8,
        Craft = 9;
    }

    /// <summary>
    /// Item arama kriterleri.
    /// </summary>
    public class ItemSearchCriteria;
    {
        public string OwnerId { get; set; }
        public string TemplateId { get; set; }
        public string CategoryId { get; set; }
        public ItemRarity? Rarity { get; set; }
        public int? MinLevel { get; set; }
        public int? MaxLevel { get; set; }
        public string NameContains { get; set; }
        public string DescriptionContains { get; set; }
        public ItemSortBy SortBy { get; set; } = ItemSortBy.CreatedDate;
        public bool SortDescending { get; set; } = true;
        public int PageIndex { get; set; } = 0;
        public int PageSize { get; set; } = 50;
    }

    /// <summary>
    /// Item sıralama seçenekleri.
    /// </summary>
    public enum ItemSortBy;
    {
        Name = 0,
        Level = 1,
        Rarity = 2,
        Value = 3,
        CreatedDate = 4;
    }

    /// <summary>
    /// Item istatistikleri.
    /// </summary>
    public class ItemStatistics;
    {
        public int TotalItems { get; set; }
        public int TotalValue { get; set; }
        public double AverageLevel { get; set; }
        public Dictionary<ItemRarity, int> RarityDistribution { get; set; }
        public Dictionary<string, int> CategoryDistribution { get; set; }
        public object MostCommonItem { get; set; }
    }

    // ... (Diğer yardımcı sınıflar, event argümanları, exception'lar, vs.)
    // Kısaltma için bu kısım kısaltıldı, tam versiyonda tüm sınıflar mevcut;

    #endregion;
}
