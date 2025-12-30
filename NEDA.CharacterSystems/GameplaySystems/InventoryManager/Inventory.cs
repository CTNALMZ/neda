using NEDA.AI.NaturalLanguage;
using NEDA.Brain.KnowledgeBase.ProblemSolutions;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.CharacterSystems.GameplaySystems.ItemSystem;
using NEDA.Configuration;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.GameplaySystems.InventoryManager;
{
    /// <summary>
    /// Envanter yönetim sistemi - öğe depolama, organizasyon ve yönetim;
    /// </summary>
    public class Inventory : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ItemSystem _itemSystem;
        private readonly StorageManager _storageManager;
        private readonly FileService _fileService;
        private readonly AppConfig _config;

        private readonly ConcurrentDictionary<string, InventoryContainer> _containers;
        private readonly ConcurrentDictionary<string, InventorySlot> _slots;
        private readonly ConcurrentDictionary<string, ItemInstance> _items;
        private readonly InventoryValidator _validator;
        private readonly InventoryOrganizer _organizer;
        private readonly InventoryOptimizer _optimizer;
        private bool _isInitialized;
        private readonly object _inventoryLock = new object();

        public event EventHandler<InventoryEvent> ItemAdded;
        public event EventHandler<InventoryEvent> ItemRemoved;
        public event EventHandler<InventoryEvent> ItemMoved;
        public event EventHandler<InventoryEvent> ContainerAdded;
        public event EventHandler<InventoryEvent> ContainerRemoved;
        public event EventHandler<InventoryWeightEvent> WeightChanged;

        public Inventory(ILogger logger, ItemSystem itemSystem,
                        StorageManager storageManager, FileService fileService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _itemSystem = itemSystem ?? throw new ArgumentNullException(nameof(itemSystem));
            _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));

            _config = ConfigurationManager.GetSection<InventoryConfig>("Inventory");
            _containers = new ConcurrentDictionary<string, InventoryContainer>();
            _slots = new ConcurrentDictionary<string, InventorySlot>();
            _items = new ConcurrentDictionary<string, ItemInstance>();
            _validator = new InventoryValidator();
            _organizer = new InventoryOrganizer();
            _optimizer = new InventoryOptimizer();

            InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("Inventory başlatılıyor...");

                // Temel bileşenleri başlat;
                await InitializeCoreComponentsAsync();

                // Varsayılan konteynırları oluştur;
                await CreateDefaultContainersAsync();

                // Slot yapısını oluştur;
                await InitializeSlotsAsync();

                // Envanter kurallarını yükle;
                await LoadInventoryRulesAsync();

                // Önbelleği temizle;
                await ClearOldCacheAsync();

                _isInitialized = true;
                _logger.LogInformation("Inventory başlatma tamamlandı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Inventory başlatma hatası: {ex.Message}", ex);
                throw new InventoryInitializationException(
                    "Inventory başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Öğe ekler;
        /// </summary>
        public async Task<AddItemResult> AddItemAsync(AddItemRequest request)
        {
            ValidateInventoryState();
            ValidateAddRequest(request);

            try
            {
                _logger.LogDebug($"Öğe ekleniyor: {request.ItemId} x{request.Quantity}");

                // Öğeyi ItemSystem'den al;
                var itemDefinition = await _itemSystem.GetItemAsync(request.ItemId);
                if (itemDefinition == null)
                {
                    throw new ItemNotFoundException($"Öğe bulunamadı: {request.ItemId}");
                }

                // Miktar kontrolü;
                if (request.Quantity <= 0)
                {
                    throw new InvalidQuantityException("Miktar 0'dan büyük olmalıdır");
                }

                // Öğe örneklerini oluştur;
                var itemInstances = await CreateItemInstancesAsync(itemDefinition, request);

                // Konteynır seçimi;
                var targetContainer = await SelectContainerForItemsAsync(itemInstances, request);
                if (targetContainer == null)
                {
                    throw new NoSpaceException("Yeterli alan yok");
                }

                // Slot yerleşimi;
                var placementResults = await PlaceItemsInContainerAsync(
                    itemInstances, targetContainer, request);

                // Eklenen öğeleri kaydet;
                var addedItems = new List<ItemInstance>();
                foreach (var placement in placementResults.Where(p => p.Success))
                {
                    var itemInstance = placement.ItemInstance;

                    // Envantere ekle;
                    _items[itemInstance.InstanceId] = itemInstance;

                    // Slot'a yerleştir;
                    await PlaceItemInSlotAsync(itemInstance, placement.Slot);

                    addedItems.Add(itemInstance);

                    // Event tetikle;
                    OnItemAdded(new InventoryEvent;
                    {
                        EventType = InventoryEventType.ItemAdded,
                        ItemInstance = itemInstance,
                        ContainerId = targetContainer.ContainerId,
                        SlotId = placement.Slot.SlotId,
                        Timestamp = DateTime.UtcNow,
                        Data = new Dictionary<string, object>
                        {
                            ["quantity"] = itemInstance.Quantity,
                            ["stackSize"] = itemDefinition.MaxStackSize,
                            ["position"] = placement.Slot.Position;
                        }
                    });
                }

                // Ağırlık güncellemesi;
                await UpdateContainerWeightAsync(targetContainer);

                // İstatistikleri güncelle;
                UpdateInventoryStatistics(addedItems);

                var result = new AddItemResult;
                {
                    Success = addedItems.Any(),
                    AddedItems = addedItems,
                    TotalQuantity = addedItems.Sum(i => i.Quantity),
                    ContainerId = targetContainer.ContainerId,
                    PlacementResults = placementResults,
                    Metadata = new Dictionary<string, object>
                    {
                        ["stackCount"] = addedItems.Count,
                        ["remainingSpace"] = targetContainer.RemainingCapacity,
                        ["autoStacked"] = request.AutoStack;
                    }
                };

                if (!result.Success)
                {
                    _logger.LogWarning($"Öğe eklenemedi: {request.ItemId}");
                    throw new ItemAddException("Öğe eklenemedi");
                }

                _logger.LogDebug($"{result.TotalQuantity} adet öğe eklendi: {request.ItemId}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Öğe ekleme hatası: {ex.Message}", ex);
                throw new ItemAddException($"Öğe eklenemedi: {request.ItemId}", ex);
            }
        }

        /// <summary>
        /// Öğe kaldırır;
        /// </summary>
        public async Task<RemoveItemResult> RemoveItemAsync(RemoveItemRequest request)
        {
            ValidateInventoryState();
            ValidateRemoveRequest(request);

            try
            {
                _logger.LogDebug($"Öğe kaldırılıyor: {request.InstanceId} x{request.Quantity}");

                // Öğe örneğini bul;
                if (!_items.TryGetValue(request.InstanceId, out var itemInstance))
                {
                    throw new ItemInstanceNotFoundException($"Öğe örneği bulunamadı: {request.InstanceId}");
                }

                // Miktar kontrolü;
                if (request.Quantity <= 0 || request.Quantity > itemInstance.Quantity)
                {
                    throw new InvalidQuantityException($"Geçersiz miktar: {request.Quantity}");
                }

                // Slot'tan kaldır;
                var slot = await GetItemSlotAsync(itemInstance);
                if (slot == null)
                {
                    throw new ItemNotInSlotException($"Öğe slot'ta bulunamadı: {request.InstanceId}");
                }

                // Kısmi kaldırma kontrolü;
                var removeResult = await ProcessItemRemovalAsync(
                    itemInstance, slot, request);

                // Envanterden kaldır;
                if (removeResult.FullyRemoved)
                {
                    _items.TryRemove(itemInstance.InstanceId, out _);
                    slot.ItemInstance = null;
                    slot.IsOccupied = false;
                }
                else;
                {
                    // Miktarı güncelle;
                    itemInstance.Quantity -= removeResult.RemovedQuantity;
                    itemInstance.UpdatedAt = DateTime.UtcNow;
                }

                // Konteynır ağırlığını güncelle;
                if (slot.ContainerId != null)
                {
                    await UpdateContainerWeightAsync(slot.ContainerId);
                }

                // Event tetikle;
                OnItemRemoved(new InventoryEvent;
                {
                    EventType = InventoryEventType.ItemRemoved,
                    ItemInstance = itemInstance,
                    ContainerId = slot.ContainerId,
                    SlotId = slot.SlotId,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["removedQuantity"] = removeResult.RemovedQuantity,
                        ["remainingQuantity"] = itemInstance.Quantity,
                        ["fullyRemoved"] = removeResult.FullyRemoved;
                    }
                });

                var result = new RemoveItemResult;
                {
                    Success = true,
                    ItemInstance = itemInstance,
                    RemovedQuantity = removeResult.RemovedQuantity,
                    RemainingQuantity = itemInstance.Quantity,
                    FullyRemoved = removeResult.FullyRemoved,
                    ContainerId = slot.ContainerId,
                    SlotId = slot.SlotId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["removeReason"] = request.Reason,
                        ["destroyItem"] = request.DestroyItem,
                        ["transferTo"] = request.TransferTo;
                    }
                };

                _logger.LogDebug($"{removeResult.RemovedQuantity} adet öğe kaldırıldı: {request.InstanceId}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Öğe kaldırma hatası: {ex.Message}", ex);
                throw new ItemRemoveException($"Öğe kaldırılamadı: {request.InstanceId}", ex);
            }
        }

        /// <summary>
        /// Öğe taşır;
        /// </summary>
        public async Task<MoveItemResult> MoveItemAsync(MoveItemRequest request)
        {
            ValidateInventoryState();
            ValidateMoveRequest(request);

            try
            {
                _logger.LogDebug($"Öğe taşınıyor: {request.InstanceId} -> {request.TargetContainerId}");

                // Öğe örneğini bul;
                if (!_items.TryGetValue(request.InstanceId, out var itemInstance))
                {
                    throw new ItemInstanceNotFoundException($"Öğe örneği bulunamadı: {request.InstanceId}");
                }

                // Kaynak slot'u bul;
                var sourceSlot = await GetItemSlotAsync(itemInstance);
                if (sourceSlot == null)
                {
                    throw new ItemNotInSlotException($"Öğe slot'ta bulunamadı: {request.InstanceId}");
                }

                // Hedef konteynırı bul;
                var targetContainer = await GetContainerAsync(request.TargetContainerId);
                if (targetContainer == null)
                {
                    throw new ContainerNotFoundException($"Konteynır bulunamadı: {request.TargetContainerId}");
                }

                // Taşıma validasyonu;
                var validationResult = await ValidateMoveAsync(
                    itemInstance, sourceSlot, targetContainer, request);
                if (!validationResult.IsValid)
                {
                    throw new MoveValidationException(
                        $"Taşıma validasyon hatası: {string.Join(", ", validationResult.Errors)}");
                }

                // Hedef slot'u bul veya oluştur;
                var targetSlot = await FindOrCreateTargetSlotAsync(
                    itemInstance, targetContainer, sourceSlot, request);

                if (targetSlot == null)
                {
                    throw new NoSpaceException("Hedefte yeterli alan yok");
                }

                // Taşıma işlemini gerçekleştir;
                var moveOperation = await ExecuteMoveAsync(
                    itemInstance, sourceSlot, targetSlot, request);

                // Event tetikle;
                OnItemMoved(new InventoryEvent;
                {
                    EventType = InventoryEventType.ItemMoved,
                    ItemInstance = itemInstance,
                    SourceContainerId = sourceSlot.ContainerId,
                    TargetContainerId = targetContainer.ContainerId,
                    SourceSlotId = sourceSlot.SlotId,
                    TargetSlotId = targetSlot.SlotId,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["moveType"] = request.MoveType,
                        ["quantity"] = moveOperation.MovedQuantity,
                        ["splitItem"] = moveOperation.WasSplit;
                    }
                });

                // Ağırlık güncellemeleri;
                if (sourceSlot.ContainerId != null)
                {
                    await UpdateContainerWeightAsync(sourceSlot.ContainerId);
                }
                await UpdateContainerWeightAsync(targetContainer.ContainerId);

                var result = new MoveItemResult;
                {
                    Success = true,
                    ItemInstance = itemInstance,
                    SourceContainerId = sourceSlot.ContainerId,
                    TargetContainerId = targetContainer.ContainerId,
                    SourceSlotId = sourceSlot.SlotId,
                    TargetSlotId = targetSlot.SlotId,
                    MovedQuantity = moveOperation.MovedQuantity,
                    WasSplit = moveOperation.WasSplit,
                    Metadata = new Dictionary<string, object>
                    {
                        ["moveType"] = request.MoveType,
                        ["autoStack"] = request.AutoStack,
                        ["preservePosition"] = request.PreservePosition;
                    }
                };

                _logger.LogDebug($"Öğe taşındı: {request.InstanceId}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Öğe taşıma hatası: {ex.Message}", ex);
                throw new MoveItemException($"Öğe taşınamadı: {request.InstanceId}", ex);
            }
        }

        /// <summary>
        /// Öğe takar (equip)
        /// </summary>
        public async Task<EquipItemResult> EquipItemAsync(EquipItemRequest request)
        {
            ValidateInventoryState();
            ValidateEquipRequest(request);

            try
            {
                _logger.LogDebug($"Öğe takılıyor: {request.InstanceId} -> {request.EquipSlot}");

                // Öğe örneğini bul;
                if (!_items.TryGetValue(request.InstanceId, out var itemInstance))
                {
                    throw new ItemInstanceNotFoundException($"Öğe örneği bulunamadı: {request.InstanceId}");
                }

                // Öğe tanımını al;
                var itemDefinition = await _itemSystem.GetItemAsync(itemInstance.ItemId);
                if (itemDefinition == null)
                {
                    throw new ItemNotFoundException($"Öğe tanımı bulunamadı: {itemInstance.ItemId}");
                }

                // Takılabilirlik kontrolü;
                if (!itemDefinition.IsEquippable)
                {
                    throw new NotEquippableException($"Öğe takılamaz: {itemInstance.ItemId}");
                }

                // Equip slot uyumluluğu;
                if (!await CanEquipInSlotAsync(itemDefinition, request.EquipSlot))
                {
                    throw new SlotIncompatibleException(
                        $"Öğe bu slot'a takılamaz: {request.EquipSlot}");
                }

                // Gereksinim kontrolü;
                var requirementCheck = await CheckEquipRequirementsAsync(itemInstance, request);
                if (!requirementCheck.MeetsRequirements)
                {
                    throw new RequirementsNotMetException(
                        $"Gereksinimler karşılanmıyor: {string.Join(", ", requirementCheck.FailedRequirements)}");
                }

                // Mevcut ekipmanı kontrol et;
                var currentlyEquipped = await GetEquippedItemInSlotAsync(request.EquipSlot);
                if (currentlyEquipped != null)
                {
                    // Mevcut ekipmanı çıkar;
                    await UnequipItemAsync(new UnequipItemRequest;
                    {
                        InstanceId = currentlyEquipped.InstanceId,
                        Reason = "Replacing with new item",
                        AutoStore = true;
                    });
                }

                // Öğeyi tak;
                var equipSlot = await GetOrCreateEquipSlotAsync(request.EquipSlot);
                await PlaceItemInSlotAsync(itemInstance, equipSlot);

                // Ekipman durumunu güncelle;
                itemInstance.IsEquipped = true;
                itemInstance.EquipSlot = request.EquipSlot;
                itemInstance.EquippedAt = DateTime.UtcNow;
                itemInstance.EquippedBy = request.EquippedBy;
                itemInstance.UpdatedAt = DateTime.UtcNow;

                // Özellikleri uygula;
                await ApplyEquipEffectsAsync(itemInstance, request);

                // Event tetikle;
                OnItemEquipped(new InventoryEvent;
                {
                    EventType = InventoryEventType.ItemEquipped,
                    ItemInstance = itemInstance,
                    SlotId = equipSlot.SlotId,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["equipSlot"] = request.EquipSlot,
                        ["characterId"] = request.EquippedBy,
                        ["requirements"] = requirementCheck;
                    }
                });

                var result = new EquipItemResult;
                {
                    Success = true,
                    ItemInstance = itemInstance,
                    EquipSlot = request.EquipSlot,
                    CharacterId = request.EquippedBy,
                    AppliedEffects = await GetEquipEffectsAsync(itemInstance),
                    Metadata = new Dictionary<string, object>
                    {
                        ["replacedItem"] = currentlyEquipped?.ItemId,
                        ["requirementCheck"] = requirementCheck,
                        ["equipTime"] = DateTime.UtcNow;
                    }
                };

                _logger.LogDebug($"Öğe takıldı: {itemInstance.ItemId} -> {request.EquipSlot}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Öğe takma hatası: {ex.Message}", ex);
                throw new EquipItemException($"Öğe takılamadı: {request.InstanceId}", ex);
            }
        }

        /// <summary>
        /// Öğe çıkarır (unequip)
        /// </summary>
        public async Task<UnequipItemResult> UnequipItemAsync(UnequipItemRequest request)
        {
            ValidateInventoryState();
            ValidateUnequipRequest(request);

            try
            {
                _logger.LogDebug($"Öğe çıkarılıyor: {request.InstanceId}");

                // Öğe örneğini bul;
                if (!_items.TryGetValue(request.InstanceId, out var itemInstance))
                {
                    throw new ItemInstanceNotFoundException($"Öğe örneği bulunamadı: {request.InstanceId}");
                }

                // Takılı olup olmadığını kontrol et;
                if (!itemInstance.IsEquipped || string.IsNullOrEmpty(itemInstance.EquipSlot))
                {
                    throw new NotEquippedException($"Öğe takılı değil: {request.InstanceId}");
                }

                // Slot'u bul;
                var equipSlot = await GetItemSlotAsync(itemInstance);
                if (equipSlot == null)
                {
                    throw new ItemNotInSlotException($"Öğe slot'ta bulunamadı: {request.InstanceId}");
                }

                // Özellikleri kaldır;
                await RemoveEquipEffectsAsync(itemInstance, request);

                // Öğeyi çıkar;
                itemInstance.IsEquipped = false;
                itemInstance.EquipSlot = null;
                itemInstance.UnequippedAt = DateTime.UtcNow;
                itemInstance.UnequippedBy = request.UnequippedBy;
                itemInstance.UpdatedAt = DateTime.UtcNow;

                // Slot'u boşalt;
                equipSlot.ItemInstance = null;
                equipSlot.IsOccupied = false;

                // Envantere geri koy;
                if (request.AutoStore)
                {
                    var storeRequest = new AddItemRequest;
                    {
                        ItemId = itemInstance.ItemId,
                        Quantity = itemInstance.Quantity,
                        ContainerId = request.TargetContainerId,
                        AutoStack = true;
                    };

                    await AddItemAsync(storeRequest);
                }

                // Event tetikle;
                OnItemUnequipped(new InventoryEvent;
                {
                    EventType = InventoryEventType.ItemUnequipped,
                    ItemInstance = itemInstance,
                    SlotId = equipSlot.SlotId,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["autoStore"] = request.AutoStore,
                        ["characterId"] = request.UnequippedBy,
                        ["targetContainer"] = request.TargetContainerId;
                    }
                });

                var result = new UnequipItemResult;
                {
                    Success = true,
                    ItemInstance = itemInstance,
                    CharacterId = request.UnequippedBy,
                    WasStored = request.AutoStore,
                    RemovedEffects = await GetUnequipEffectsAsync(itemInstance),
                    Metadata = new Dictionary<string, object>
                    {
                        ["unequipReason"] = request.Reason,
                        ["storageContainer"] = request.TargetContainerId,
                        ["unequipTime"] = DateTime.UtcNow;
                    }
                };

                _logger.LogDebug($"Öğe çıkarıldı: {itemInstance.ItemId}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Öğe çıkarma hatası: {ex.Message}", ex);
                throw new UnequipItemException($"Öğe çıkarılamadı: {request.InstanceId}", ex);
            }
        }

        /// <summary>
        /// Öğe kullanır;
        /// </summary>
        public async Task<UseItemResult> UseItemAsync(UseItemRequest request)
        {
            ValidateInventoryState();
            ValidateUseRequest(request);

            try
            {
                _logger.LogDebug($"Öğe kullanılıyor: {request.InstanceId}");

                // Öğe örneğini bul;
                if (!_items.TryGetValue(request.InstanceId, out var itemInstance))
                {
                    throw new ItemInstanceNotFoundException($"Öğe örneği bulunamadı: {request.InstanceId}");
                }

                // Öğe tanımını al;
                var itemDefinition = await _itemSystem.GetItemAsync(itemInstance.ItemId);
                if (itemDefinition == null)
                {
                    throw new ItemNotFoundException($"Öğe tanımı bulunamadı: {itemInstance.ItemId}");
                }

                // Kullanılabilirlik kontrolü;
                if (!itemDefinition.IsUsable)
                {
                    throw new NotUsableException($"Öğe kullanılamaz: {itemInstance.ItemId}");
                }

                // Kullanım gereksinimleri;
                var requirementCheck = await CheckUseRequirementsAsync(itemInstance, request);
                if (!requirementCheck.MeetsRequirements)
                {
                    throw new RequirementsNotMetException(
                        $"Kullanım gereksinimleri karşılanmıyor: {string.Join(", ", requirementCheck.FailedRequirements)}");
                }

                // Kullanım işlemini gerçekleştir;
                var useOperation = await ExecuteItemUseAsync(itemInstance, itemDefinition, request);

                // Tüketilebilir öğe kontrolü;
                if (itemDefinition.IsConsumable && useOperation.Consumed)
                {
                    // Miktarı azalt veya kaldır;
                    if (itemInstance.Quantity > 1)
                    {
                        itemInstance.Quantity--;
                        itemInstance.UpdatedAt = DateTime.UtcNow;
                    }
                    else;
                    {
                        // Öğeyi kaldır;
                        await RemoveItemAsync(new RemoveItemRequest;
                        {
                            InstanceId = request.InstanceId,
                            Quantity = 1,
                            Reason = "Consumed",
                            DestroyItem = true;
                        });
                    }
                }

                // Cooldown uygula;
                if (itemDefinition.CooldownTime > TimeSpan.Zero)
                {
                    await ApplyCooldownAsync(itemInstance, itemDefinition.CooldownTime);
                }

                // Kullanım geçmişine ekle;
                await AddToUseHistoryAsync(itemInstance, request, useOperation);

                // Event tetikle;
                OnItemUsed(new InventoryEvent;
                {
                    EventType = InventoryEventType.ItemUsed,
                    ItemInstance = itemInstance,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["user"] = request.UserId,
                        ["target"] = request.TargetId,
                        ["consumed"] = useOperation.Consumed,
                        ["effects"] = useOperation.AppliedEffects;
                    }
                });

                var result = new UseItemResult;
                {
                    Success = true,
                    ItemInstance = itemInstance,
                    UserId = request.UserId,
                    TargetId = request.TargetId,
                    Consumed = useOperation.Consumed,
                    AppliedEffects = useOperation.AppliedEffects,
                    CooldownUntil = itemInstance.CooldownUntil,
                    Metadata = new Dictionary<string, object>
                    {
                        ["usageType"] = request.UsageType,
                        ["quantityUsed"] = 1,
                        ["requirementCheck"] = requirementCheck;
                    }
                };

                _logger.LogDebug($"Öğe kullanıldı: {itemInstance.ItemId}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Öğe kullanma hatası: {ex.Message}", ex);
                throw new UseItemException($"Öğe kullanılamadı: {request.InstanceId}", ex);
            }
        }

        /// <summary>
        /// Envanteri düzenler;
        /// </summary>
        public async Task<OrganizeResult> OrganizeInventoryAsync(OrganizeRequest request)
        {
            ValidateInventoryState();
            ValidateOrganizeRequest(request);

            try
            {
                _logger.LogDebug($"Envanter düzenleniyor: {request.ContainerId}");

                var container = await GetContainerAsync(request.ContainerId);
                if (container == null)
                {
                    throw new ContainerNotFoundException($"Konteynır bulunamadı: {request.ContainerId}");
                }

                // Organizasyon stratejisini belirle;
                var strategy = await DetermineOrganizationStrategyAsync(container, request);

                // Öğeleri düzenle;
                var organizationResult = await _organizer.OrganizeContainerAsync(
                    container, strategy, request);

                // Slot'ları güncelle;
                await UpdateContainerSlotsAsync(container, organizationResult);

                // Event tetikle;
                OnInventoryOrganized(new InventoryEvent;
                {
                    EventType = InventoryEventType.InventoryOrganized,
                    ContainerId = container.ContainerId,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["strategy"] = strategy.StrategyType,
                        ["movedItems"] = organizationResult.MovedItems,
                        ["emptySlots"] = organizationResult.EmptySlotsCreated;
                    }
                });

                var result = new OrganizeResult;
                {
                    Success = true,
                    ContainerId = container.ContainerId,
                    StrategyUsed = strategy.StrategyType,
                    ItemsMoved = organizationResult.MovedItems,
                    EmptySlotsCreated = organizationResult.EmptySlotsCreated,
                    OrganizationTime = organizationResult.OrganizationTime,
                    Metadata = new Dictionary<string, object>
                    {
                        ["optimizationLevel"] = organizationResult.OptimizationLevel,
                        ["sortCriteria"] = request.SortCriteria,
                        ["groupSimilar"] = request.GroupSimilarItems;
                    }
                };

                _logger.LogDebug($"Envanter düzenlendi: {container.ContainerId}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter düzenleme hatası: {ex.Message}", ex);
                throw new OrganizeException("Envanter düzenlenemedi", ex);
            }
        }

        /// <summary>
        /// Envanteri optimize eder;
        /// </summary>
        public async Task<OptimizeResult> OptimizeInventoryAsync(OptimizeRequest request)
        {
            ValidateInventoryState();
            ValidateOptimizeRequest(request);

            try
            {
                _logger.LogDebug($"Envanter optimize ediliyor");

                var optimizationResult = new OptimizationResult();

                foreach (var containerId in request.ContainerIds)
                {
                    var container = await GetContainerAsync(containerId);
                    if (container == null) continue;

                    // Konteynırı optimize et;
                    var containerResult = await _optimizer.OptimizeContainerAsync(
                        container, request.OptimizationType);

                    optimizationResult.ContainerResults[containerId] = containerResult;

                    // Slot'ları güncelle;
                    await UpdateContainerAfterOptimizationAsync(container, containerResult);
                }

                // Toplam optimizasyon istatistikleri;
                var totalSavings = CalculateTotalOptimizationSavings(optimizationResult);

                var result = new OptimizeResult;
                {
                    Success = true,
                    OptimizationType = request.OptimizationType,
                    ContainerResults = optimizationResult.ContainerResults,
                    TotalSpaceSaved = totalSavings.SpaceSaved,
                    TotalWeightReduced = totalSavings.WeightReduced,
                    OptimizationTime = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["containersOptimized"] = optimizationResult.ContainerResults.Count,
                        ["itemsRearranged"] = totalSavings.ItemsRearranged,
                        ["stacksOptimized"] = totalSavings.StacksOptimized;
                    }
                };

                _logger.LogInformation($"Envanter optimize edildi: {totalSavings.SpaceSaved} slot kazanıldı");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter optimizasyon hatası: {ex.Message}", ex);
                throw new OptimizeException("Envanter optimize edilemedi", ex);
            }
        }

        /// <summary>
        /// Envanteri arar;
        /// </summary>
        public async Task<SearchResult> SearchInventoryAsync(SearchRequest request)
        {
            ValidateInventoryState();
            ValidateSearchRequest(request);

            try
            {
                _logger.LogDebug($"Envanter aranıyor: {request.Query}");

                var searchResults = new List<ItemSearchResult>();

                // Tüm konteynırlarda ara;
                foreach (var container in _containers.Values)
                {
                    if (request.ContainerIds != null &&
                        !request.ContainerIds.Contains(container.ContainerId))
                    {
                        continue;
                    }

                    // Konteynırda ara;
                    var containerResults = await SearchContainerAsync(container, request);
                    searchResults.AddRange(containerResults);
                }

                // Sonuçları sırala;
                var sortedResults = SortSearchResults(searchResults, request.SortBy, request.SortDirection);

                // Sayfalama;
                var pagedResults = ApplyPagination(sortedResults, request.PageNumber, request.PageSize);

                var result = new SearchResult;
                {
                    Success = true,
                    Query = request.Query,
                    TotalResults = searchResults.Count,
                    PageNumber = request.PageNumber,
                    PageSize = request.PageSize,
                    Results = pagedResults.ToList(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["searchTime"] = DateTime.UtcNow,
                        ["searchFilters"] = request.Filters,
                        ["searchScope"] = request.SearchScope;
                    }
                };

                _logger.LogDebug($"{searchResults.Count} sonuç bulundu");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter arama hatası: {ex.Message}", ex);
                throw new SearchException("Envanter aranamadı", ex);
            }
        }

        /// <summary>
        /// Envanteri temizler;
        /// </summary>
        public async Task<CleanupResult> CleanupInventoryAsync(CleanupRequest request)
        {
            ValidateInventoryState();
            ValidateCleanupRequest(request);

            try
            {
                _logger.LogDebug($"Envanter temizleniyor");

                var cleanupResults = new Dictionary<string, ContainerCleanupResult>();
                var removedItems = new List<ItemInstance>();

                foreach (var containerId in request.ContainerIds)
                {
                    var container = await GetContainerAsync(containerId);
                    if (container == null) continue;

                    // Konteynırı temizle;
                    var containerResult = await CleanupContainerAsync(container, request);
                    cleanupResults[containerId] = containerResult;

                    removedItems.AddRange(containerResult.RemovedItems);

                    // Slot'ları güncelle;
                    await UpdateContainerAfterCleanupAsync(container, containerResult);
                }

                // Toplam temizlik istatistikleri;
                var totalStats = CalculateTotalCleanupStats(cleanupResults);

                var result = new CleanupResult;
                {
                    Success = true,
                    CleanupType = request.CleanupType,
                    ContainerResults = cleanupResults,
                    TotalItemsRemoved = totalStats.ItemsRemoved,
                    TotalSpaceFreed = totalStats.SpaceFreed,
                    TotalValueRemoved = totalStats.ValueRemoved,
                    Metadata = new Dictionary<string, object>
                    {
                        ["containersCleaned"] = cleanupResults.Count,
                        ["criteria"] = request.Criteria,
                        ["autoSell"] = request.AutoSell;
                    }
                };

                _logger.LogInformation($"Envanter temizlendi: {totalStats.ItemsRemoved} öğe kaldırıldı");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter temizleme hatası: {ex.Message}", ex);
                throw new CleanupException("Envanter temizlenemedi", ex);
            }
        }

        /// <summary>
        /// Envanteri kaydeder;
        /// </summary>
        public async Task<SaveResult> SaveInventoryAsync(SaveRequest request)
        {
            ValidateInventoryState();

            try
            {
                _logger.LogDebug($"Envanter kaydediliyor");

                // Envanter durumunu topla;
                var inventoryState = await GetInventoryStateAsync();

                // Serialize et;
                var serializedData = await SerializeInventoryAsync(inventoryState, request.Format);

                // Şifrele;
                if (request.Encrypt)
                {
                    serializedData = await EncryptInventoryDataAsync(serializedData, request);
                }

                // Sıkıştır;
                if (request.Compress)
                {
                    serializedData = await CompressInventoryDataAsync(serializedData);
                }

                // Dosyaya kaydet;
                var savePath = await SaveToFileAsync(serializedData, request);

                // Yedekleme geçmişine ekle;
                await AddToBackupHistoryAsync(savePath, request);

                var result = new SaveResult;
                {
                    Success = true,
                    SavePath = savePath,
                    Format = request.Format,
                    TotalItems = inventoryState.TotalItems,
                    TotalContainers = inventoryState.TotalContainers,
                    FileSize = serializedData.Length,
                    SaveTime = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["encrypted"] = request.Encrypt,
                        ["compressed"] = request.Compress,
                        ["backupType"] = request.BackupType;
                    }
                };

                _logger.LogInformation($"Envanter kaydedildi: {savePath}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter kaydetme hatası: {ex.Message}", ex);
                throw new SaveException("Envanter kaydedilemedi", ex);
            }
        }

        /// <summary>
        /// Envanteri yükler;
        /// </summary>
        public async Task<LoadResult> LoadInventoryAsync(LoadRequest request)
        {
            ValidateInventoryState();

            try
            {
                _logger.LogDebug($"Envanter yükleniyor: {request.SavePath}");

                // Dosyadan oku;
                var serializedData = await LoadFromFileAsync(request.SavePath);

                // Sıkıştırmayı aç;
                if (request.Decompress)
                {
                    serializedData = await DecompressInventoryDataAsync(serializedData);
                }

                // Şifreyi çöz;
                if (request.Decrypt)
                {
                    serializedData = await DecryptInventoryDataAsync(serializedData, request);
                }

                // Deserialize et;
                var inventoryState = await DeserializeInventoryAsync(serializedData, request.Format);

                // Envanteri yükle;
                await LoadInventoryStateAsync(inventoryState, request);

                // Yükleme geçmişine ekle;
                await AddToLoadHistoryAsync(request.SavePath, request);

                var result = new LoadResult;
                {
                    Success = true,
                    LoadPath = request.SavePath,
                    Format = request.Format,
                    TotalItemsLoaded = inventoryState.TotalItems,
                    TotalContainersLoaded = inventoryState.TotalContainers,
                    LoadTime = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["decrypted"] = request.Decrypt,
                        ["decompressed"] = request.Decompress,
                        ["mergeExisting"] = request.MergeWithExisting;
                    }
                };

                _logger.LogInformation($"Envanter yüklendi: {inventoryState.TotalItems} öğe yüklendi");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter yükleme hatası: {ex.Message}", ex);
                throw new LoadException("Envanter yüklenemedi", ex);
            }
        }

        /// <summary>
        /// Envanter istatistiklerini alır;
        /// </summary>
        public async Task<InventoryStats> GetInventoryStatsAsync(StatsRequest request = null)
        {
            ValidateInventoryState();

            try
            {
                var stats = new InventoryStats;
                {
                    Timestamp = DateTime.UtcNow,
                    TotalItems = _items.Count,
                    TotalContainers = _containers.Count,
                    TotalSlots = _slots.Count,
                    OccupiedSlots = _slots.Values.Count(s => s.IsOccupied),
                    EmptySlots = _slots.Values.Count(s => !s.IsOccupied)
                };

                // Konteynır istatistikleri;
                stats.ContainerStats = new Dictionary<string, ContainerStats>();
                foreach (var container in _containers.Values)
                {
                    var containerSlots = _slots.Values;
                        .Where(s => s.ContainerId == container.ContainerId)
                        .ToList();

                    stats.ContainerStats[container.ContainerId] = new ContainerStats;
                    {
                        ContainerId = container.ContainerId,
                        ContainerName = container.Name,
                        TotalSlots = containerSlots.Count,
                        OccupiedSlots = containerSlots.Count(s => s.IsOccupied),
                        TotalWeight = container.TotalWeight,
                        MaxWeight = container.MaxWeight,
                        TotalValue = await CalculateContainerValueAsync(container.ContainerId),
                        ItemCount = containerSlots.Count(s => s.IsOccupied)
                    };
                }

                // Öğe kategorilerine göre istatistikler;
                stats.CategoryStats = await CalculateCategoryStatsAsync();

                // Kullanım istatistikleri;
                stats.UsageStats = await CalculateUsageStatsAsync(request?.TimeRange ?? TimeSpan.FromDays(30));

                // Performans metrikleri;
                stats.PerformanceMetrics = await CalculatePerformanceMetricsAsync();

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter istatistikleri alma hatası: {ex.Message}", ex);
                throw new StatsException("Envanter istatistikleri alınamadı", ex);
            }
        }

        /// <summary>
        /// Envanteri doğrular;
        /// </summary>
        public async Task<ValidationResult> ValidateInventoryAsync(ValidationRequest request)
        {
            ValidateInventoryState();

            try
            {
                _logger.LogDebug($"Envanter doğrulanıyor");

                var validationResults = new List<ValidationIssue>();
                var totalErrors = 0;
                var totalWarnings = 0;

                // 1. Konteynır doğrulama;
                var containerValidation = await ValidateContainersAsync();
                validationResults.AddRange(containerValidation);
                totalErrors += containerValidation.Count(i => i.Severity == ValidationSeverity.Error);
                totalWarnings += containerValidation.Count(i => i.Severity == ValidationSeverity.Warning);

                // 2. Slot doğrulama;
                var slotValidation = await ValidateSlotsAsync();
                validationResults.AddRange(slotValidation);
                totalErrors += slotValidation.Count(i => i.Severity == ValidationSeverity.Error);
                totalWarnings += slotValidation.Count(i => i.Severity == ValidationSeverity.Warning);

                // 3. Öğe doğrulama;
                var itemValidation = await ValidateItemsAsync();
                validationResults.AddRange(itemValidation);
                totalErrors += itemValidation.Count(i => i.Severity == ValidationSeverity.Error);
                totalWarnings += itemValidation.Count(i => i.Severity == ValidationSeverity.Warning);

                // 4. Bütünlük kontrolü;
                var integrityValidation = await ValidateIntegrityAsync();
                validationResults.AddRange(integrityValidation);
                totalErrors += integrityValidation.Count(i => i.Severity == ValidationSeverity.Error);
                totalWarnings += integrityValidation.Count(i => i.Severity == ValidationSeverity.Warning);

                // 5. Performans kontrolü;
                var performanceValidation = await ValidatePerformanceAsync();
                validationResults.AddRange(performanceValidation);
                totalErrors += performanceValidation.Count(i => i.Severity == ValidationSeverity.Error);
                totalWarnings += performanceValidation.Count(i => i.Severity == ValidationSeverity.Warning);

                var result = new ValidationResult;
                {
                    IsValid = totalErrors == 0,
                    TotalErrors = totalErrors,
                    TotalWarnings = totalWarnings,
                    ValidationTime = DateTime.UtcNow,
                    Issues = validationResults,
                    Recommendations = await GenerateValidationRecommendationsAsync(validationResults)
                };

                if (!result.IsValid)
                {
                    _logger.LogWarning($"Envanter doğrulama başarısız: {totalErrors} hata, {totalWarnings} uyarı");
                }
                else;
                {
                    _logger.LogDebug($"Envanter doğrulama başarılı");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter doğrulama hatası: {ex.Message}", ex);
                throw new ValidationException("Envanter doğrulanamadı", ex);
            }
        }

        /// <summary>
        /// Envanteri sıfırlar;
        /// </summary>
        public async Task<ResetResult> ResetInventoryAsync(ResetRequest request)
        {
            try
            {
                _logger.LogWarning($"Envanter sıfırlanıyor. Tip: {request.ResetType}");

                var removedItems = new List<ItemInstance>();
                var removedContainers = new List<InventoryContainer>();

                lock (_inventoryLock)
                {
                    switch (request.ResetType)
                    {
                        case ResetType.Full:
                            // Tüm öğeleri kaldır;
                            removedItems.AddRange(_items.Values);
                            _items.Clear();

                            // Tüm slot'ları temizle;
                            foreach (var slot in _slots.Values)
                            {
                                slot.ItemInstance = null;
                                slot.IsOccupied = false;
                            }

                            // Konteynırları temizle (varsayılanları koru)
                            var defaultContainers = _containers.Values;
                                .Where(c => c.IsDefaultContainer)
                                .ToList();

                            removedContainers.AddRange(_containers.Values;
                                .Where(c => !c.IsDefaultContainer));

                            _containers.Clear();
                            foreach (var container in defaultContainers)
                            {
                                _containers[container.ContainerId] = container;
                            }
                            break;

                        case ResetType.ItemsOnly:
                            // Sadece öğeleri kaldır;
                            removedItems.AddRange(_items.Values);
                            _items.Clear();

                            // Slot'ları temizle;
                            foreach (var slot in _slots.Values)
                            {
                                slot.ItemInstance = null;
                                slot.IsOccupied = false;
                            }
                            break;

                        case ResetType.ContainersOnly:
                            // Sadece özel konteynırları kaldır;
                            removedContainers.AddRange(_containers.Values;
                                .Where(c => !c.IsDefaultContainer));

                            foreach (var container in removedContainers)
                            {
                                _containers.TryRemove(container.ContainerId, out _);

                                // Bu konteynırdaki slot'ları kaldır;
                                var containerSlots = _slots.Values;
                                    .Where(s => s.ContainerId == container.ContainerId)
                                    .ToList();

                                foreach (var slot in containerSlots)
                                {
                                    _slots.TryRemove(slot.SlotId, out _);
                                }
                            }
                            break;

                        case ResetType.Custom:
                            // Özel sıfırlama mantığı;
                            await ExecuteCustomResetAsync(request);
                            break;
                    }
                }

                // Event tetikle;
                OnInventoryReset(new InventoryEvent;
                {
                    EventType = InventoryEventType.InventoryReset,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["resetType"] = request.ResetType,
                        ["itemsRemoved"] = removedItems.Count,
                        ["containersRemoved"] = removedContainers.Count,
                        ["resetReason"] = request.Reason;
                    }
                });

                var result = new ResetResult;
                {
                    Success = true,
                    ResetType = request.ResetType,
                    ItemsRemoved = removedItems.Count,
                    ContainersRemoved = removedContainers.Count,
                    ResetTime = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["backupCreated"] = request.CreateBackup,
                        ["reason"] = request.Reason,
                        ["confirmed"] = request.ConfirmationToken != null;
                    }
                };

                _logger.LogInformation($"Envanter sıfırlandı: {removedItems.Count} öğe, {removedContainers.Count} konteynır kaldırıldı");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter sıfırlama hatası: {ex.Message}", ex);
                throw new ResetException("Envanter sıfırlanamadı", ex);
            }
        }

        /// <summary>
        /// Envanteri yedekler;
        /// </summary>
        public async Task<BackupResult> BackupInventoryAsync(BackupRequest request)
        {
            ValidateInventoryState();

            try
            {
                _logger.LogDebug($"Envanter yedekleniyor: {request.BackupType}");

                // Yedekleme dizinini hazırla;
                var backupDirectory = await PrepareBackupDirectoryAsync(request);

                // Envanter durumunu al;
                var inventoryState = await GetInventoryStateAsync();

                // Yedekleme dosyasını oluştur;
                var backupResult = await CreateBackupFileAsync(
                    inventoryState, backupDirectory, request);

                // Yedekleme geçmişine ekle;
                await AddBackupToHistoryAsync(backupResult, request);

                // Eski yedekleri temizle;
                if (request.CleanupOldBackups)
                {
                    await CleanupOldBackupsAsync(backupDirectory, request.MaxBackupAge);
                }

                var result = new BackupResult;
                {
                    Success = true,
                    BackupType = request.BackupType,
                    BackupPath = backupResult.FilePath,
                    FileSize = backupResult.FileSize,
                    BackupTime = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["compressed"] = backupResult.Compressed,
                        ["encrypted"] = backupResult.Encrypted,
                        ["incremental"] = backupResult.IsIncremental,
                        ["itemsBackedUp"] = inventoryState.TotalItems;
                    }
                };

                _logger.LogInformation($"Envanter yedeklendi: {backupResult.FilePath}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter yedekleme hatası: {ex.Message}", ex);
                throw new BackupException("Envanter yedeklenemedi", ex);
            }
        }

        /// <summary>
        /// Envanteri geri yükler;
        /// </summary>
        public async Task<RestoreResult> RestoreInventoryAsync(RestoreRequest request)
        {
            try
            {
                _logger.LogDebug($"Envanter geri yükleniyor: {request.BackupPath}");

                // Yedek dosyasını doğrula;
                await ValidateBackupFileAsync(request.BackupPath, request);

                // Mevcut envanteri yedekle (isteğe bağlı)
                if (request.BackupCurrent)
                {
                    await BackupInventoryAsync(new BackupRequest;
                    {
                        BackupType = BackupType.Automatic,
                        Description = $"Pre-restore backup for {request.BackupPath}",
                        Compress = true;
                    });
                }

                // Yedekten yükle;
                var loadRequest = new LoadRequest;
                {
                    SavePath = request.BackupPath,
                    Format = SerializationFormat.Json,
                    Decrypt = request.Decrypt,
                    Decompress = request.Decompress,
                    MergeWithExisting = request.MergeWithExisting,
                    RestoreMode = true;
                };

                var loadResult = await LoadInventoryAsync(loadRequest);

                // Geri yükleme geçmişine ekle;
                await AddRestoreToHistoryAsync(request, loadResult);

                var result = new RestoreResult;
                {
                    Success = true,
                    BackupPath = request.BackupPath,
                    RestoreTime = DateTime.UtcNow,
                    ItemsRestored = loadResult.TotalItemsLoaded,
                    ContainersRestored = loadResult.TotalContainersLoaded,
                    Metadata = new Dictionary<string, object>
                    {
                        ["backupCurrent"] = request.BackupCurrent,
                        ["mergeExisting"] = request.MergeWithExisting,
                        ["restorePoint"] = request.RestorePoint;
                    }
                };

                _logger.LogInformation($"Envanter geri yüklendi: {loadResult.TotalItemsLoaded} öğe");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter geri yükleme hatası: {ex.Message}", ex);
                throw new RestoreException("Envanter geri yüklenemedi", ex);
            }
        }

        /// <summary>
        /// Envanteri izler (monitoring)
        /// </summary>
        public async Task<MonitoringResult> MonitorInventoryAsync(MonitoringRequest request)
        {
            ValidateInventoryState();

            try
            {
                _logger.LogDebug($"Envanter izleniyor: {request.MonitoringType}");

                var monitoringData = new MonitoringData;
                {
                    Timestamp = DateTime.UtcNow,
                    MonitoringType = request.MonitoringType;
                };

                switch (request.MonitoringType)
                {
                    case MonitoringType.Performance:
                        monitoringData.PerformanceMetrics = await CollectPerformanceMetricsAsync();
                        break;

                    case MonitoringType.Usage:
                        monitoringData.UsageMetrics = await CollectUsageMetricsAsync(request.TimeRange);
                        break;

                    case MonitoringType.Storage:
                        monitoringData.StorageMetrics = await CollectStorageMetricsAsync();
                        break;

                    case MonitoringType.Health:
                        monitoringData.HealthMetrics = await CollectHealthMetricsAsync();
                        break;

                    case MonitoringType.Comprehensive:
                        monitoringData.PerformanceMetrics = await CollectPerformanceMetricsAsync();
                        monitoringData.UsageMetrics = await CollectUsageMetricsAsync(request.TimeRange);
                        monitoringData.StorageMetrics = await CollectStorageMetricsAsync();
                        monitoringData.HealthMetrics = await CollectHealthMetricsAsync();
                        break;
                }

                // Anomalileri kontrol et;
                monitoringData.Anomalies = await DetectAnomaliesAsync(monitoringData);

                // Öneriler oluştur;
                monitoringData.Recommendations = await GenerateMonitoringRecommendationsAsync(monitoringData);

                // Veritabanına kaydet (isteğe bağlı)
                if (request.StoreResults)
                {
                    await StoreMonitoringResultsAsync(monitoringData, request);
                }

                var result = new MonitoringResult;
                {
                    Success = true,
                    MonitoringType = request.MonitoringType,
                    Data = monitoringData,
                    CollectedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["timeRange"] = request.TimeRange,
                        ["storeResults"] = request.StoreResults,
                        ["anomaliesDetected"] = monitoringData.Anomalies?.Count ?? 0;
                    }
                };

                _logger.LogDebug($"Envanter izleme tamamlandı: {request.MonitoringType}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Envanter izleme hatası: {ex.Message}", ex);
                throw new MonitoringException("Envanter izlenemedi", ex);
            }
        }

        #region Event Handlers;

        protected virtual void OnItemAdded(InventoryEvent e)
        {
            ItemAdded?.Invoke(this, e);

            // Ağırlık değişikliğini de tetikle;
            if (e.ItemInstance != null && e.ContainerId != null)
            {
                OnWeightChanged(new InventoryWeightEvent;
                {
                    ContainerId = e.ContainerId,
                    ItemInstance = e.ItemInstance,
                    WeightChange = e.ItemInstance.TotalWeight,
                    NewTotalWeight = CalculateContainerWeight(e.ContainerId),
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        protected virtual void OnItemRemoved(InventoryEvent e)
        {
            ItemRemoved?.Invoke(this, e);
        }

        protected virtual void OnItemMoved(InventoryEvent e)
        {
            ItemMoved?.Invoke(this, e);
        }

        protected virtual void OnContainerAdded(InventoryEvent e)
        {
            ContainerAdded?.Invoke(this, e);
        }

        protected virtual void OnContainerRemoved(InventoryEvent e)
        {
            ContainerRemoved?.Invoke(this, e);
        }

        protected virtual void OnWeightChanged(InventoryWeightEvent e)
        {
            WeightChanged?.Invoke(this, e);
        }

        protected virtual void OnItemEquipped(InventoryEvent e)
        {
            // Özel equip event handler'ı;
        }

        protected virtual void OnItemUnequipped(InventoryEvent e)
        {
            // Özel unequip event handler'ı;
        }

        protected virtual void OnItemUsed(InventoryEvent e)
        {
            // Özel use event handler'ı;
        }

        protected virtual void OnInventoryOrganized(InventoryEvent e)
        {
            // Özel organize event handler'ı;
        }

        protected virtual void OnInventoryReset(InventoryEvent e)
        {
            // Özel reset event handler'ı;
        }

        #endregion;

        #region Helper Methods;

        private void ValidateInventoryState()
        {
            if (!_isInitialized)
            {
                throw new InventoryNotInitializedException("Inventory başlatılmamış");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Inventory));
            }
        }

        private void ValidateAddRequest(AddItemRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.ItemId))
                throw new ArgumentException("ItemId boş olamaz");

            if (request.Quantity <= 0)
                throw new ArgumentException("Quantity 0'dan büyük olmalıdır");
        }

        private void ValidateRemoveRequest(RemoveItemRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.InstanceId))
                throw new ArgumentException("InstanceId boş olamaz");
        }

        private void ValidateMoveRequest(MoveItemRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.InstanceId))
                throw new ArgumentException("InstanceId boş olamaz");

            if (string.IsNullOrEmpty(request.TargetContainerId))
                throw new ArgumentException("TargetContainerId boş olamaz");
        }

        private void ValidateEquipRequest(EquipItemRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.InstanceId))
                throw new ArgumentException("InstanceId boş olamaz");

            if (string.IsNullOrEmpty(request.EquipSlot))
                throw new ArgumentException("EquipSlot boş olamaz");
        }

        private void ValidateUnequipRequest(UnequipItemRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.InstanceId))
                throw new ArgumentException("InstanceId boş olamaz");
        }

        private void ValidateUseRequest(UseItemRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.InstanceId))
                throw new ArgumentException("InstanceId boş olamaz");

            if (string.IsNullOrEmpty(request.UserId))
                throw new ArgumentException("UserId boş olamaz");
        }

        private void ValidateOrganizeRequest(OrganizeRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.ContainerId))
                throw new ArgumentException("ContainerId boş olamaz");
        }

        private void ValidateOptimizeRequest(OptimizeRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (request.ContainerIds == null || request.ContainerIds.Count == 0)
                throw new ArgumentException("En az bir ContainerId belirtilmelidir");
        }

        private void ValidateSearchRequest(SearchRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.Query) &&
                (request.Filters == null || request.Filters.Count == 0))
                throw new ArgumentException("Query veya Filters belirtilmelidir");
        }

        private void ValidateCleanupRequest(CleanupRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (request.ContainerIds == null || request.ContainerIds.Count == 0)
                throw new ArgumentException("En az bir ContainerId belirtilmelidir");

            if (request.Criteria == null || request.Criteria.Count == 0)
                throw new ArgumentException("En az bir kriter belirtilmelidir");
        }

        private async Task<List<ItemInstance>> CreateItemInstancesAsync(
            ItemDefinition itemDefinition, AddItemRequest request)
        {
            var instances = new List<ItemInstance>();
            var remainingQuantity = request.Quantity;

            while (remainingQuantity > 0)
            {
                var stackSize = Math.Min(remainingQuantity, itemDefinition.MaxStackSize);

                var itemInstance = new ItemInstance;
                {
                    InstanceId = Guid.NewGuid().ToString(),
                    ItemId = itemDefinition.ItemId,
                    Quantity = stackSize,
                    Quality = request.Quality ?? itemDefinition.DefaultQuality,
                    Durability = request.Durability ?? itemDefinition.MaxDurability,
                    MaxDurability = itemDefinition.MaxDurability,
                    Weight = itemDefinition.Weight * stackSize,
                    BaseWeight = itemDefinition.Weight,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    Tags = itemDefinition.Tags?.ToList() ?? new List<string>(),
                    Category = itemDefinition.Category,
                    Rarity = itemDefinition.Rarity,
                    IsSoulbound = request.IsSoulbound ?? itemDefinition.IsSoulbound,
                    BoundTo = request.BoundTo,
                    ExpiresAt = request.ExpiresAt,
                    CustomData = request.CustomData;
                };

                // Özel özellikleri uygula;
                await ApplyItemPropertiesAsync(itemInstance, itemDefinition, request);

                instances.Add(itemInstance);
                remainingQuantity -= stackSize;
            }

            return instances;
        }

        private async Task<InventoryContainer> SelectContainerForItemsAsync(
            List<ItemInstance> itemInstances, AddItemRequest request)
        {
            // Özel konteynır belirtilmişse;
            if (!string.IsNullOrEmpty(request.ContainerId))
            {
                var container = await GetContainerAsync(request.ContainerId);
                if (container != null && await CanAddToContainerAsync(container, itemInstances))
                {
                    return container;
                }
            }

            // Uygun konteynır ara;
            var suitableContainers = new List<InventoryContainer>();

            foreach (var container in _containers.Values)
            {
                if (await CanAddToContainerAsync(container, itemInstances))
                {
                    suitableContainers.Add(container);
                }
            }

            // En uygun konteynırı seç;
            return await SelectBestContainerAsync(suitableContainers, itemInstances, request);
        }

        private async Task<bool> CanAddToContainerAsync(
            InventoryContainer container, List<ItemInstance> itemInstances)
        {
            // Ağırlık kontrolü;
            var totalWeight = itemInstances.Sum(i => i.TotalWeight);
            if (container.CurrentWeight + totalWeight > container.MaxWeight)
                return false;

            // Slot kontrolü;
            var requiredSlots = await CalculateRequiredSlotsAsync(container, itemInstances);
            if (container.RemainingSlots < requiredSlots)
                return false;

            // Özel kurallar kontrolü;
            if (!await _validator.CanAddToContainerAsync(container, itemInstances))
                return false;

            return true;
        }

        private async Task<int> CalculateRequiredSlotsAsync(
            InventoryContainer container, List<ItemInstance> itemInstances)
        {
            var availableSlots = await GetAvailableSlotsAsync(container.ContainerId);
            var requiredSlots = 0;

            foreach (var itemInstance in itemInstances)
            {
                // Stack kontrolü;
                var existingStack = await FindStackForItemAsync(
                    container.ContainerId, itemInstance.ItemId, itemInstance);

                if (existingStack != null && existingStack.RemainingSpace > 0)
                {
                    // Mevcut stack'e eklenebilir;
                    continue;
                }

                // Yeni slot gerekiyor;
                requiredSlots++;
            }

            return requiredSlots;
        }

        private async Task<List<PlacementResult>> PlaceItemsInContainerAsync(
            List<ItemInstance> itemInstances,
            InventoryContainer container,
            AddItemRequest request)
        {
            var results = new List<PlacementResult>();

            foreach (var itemInstance in itemInstances)
            {
                PlacementResult result;

                if (request.AutoStack)
                {
                    // Stack yapmaya çalış;
                    result = await TryStackItemAsync(itemInstance, container);
                }
                else;
                {
                    // Yeni slota yerleştir;
                    result = await PlaceInNewSlotAsync(itemInstance, container);
                }

                results.Add(result);

                if (!result.Success && request.PartialPlacement)
                {
                    // Kısmi yerleştirmeye devam et;
                    continue;
                }
                else if (!result.Success)
                {
                    // Başarısız oldu, diğerlerini de iptal et;
                    break;
                }
            }

            return results;
        }

        private async Task<PlacementResult> TryStackItemAsync(
            ItemInstance itemInstance, InventoryContainer container)
        {
            // Mevcut stack'i bul;
            var existingStack = await FindStackForItemAsync(
                container.ContainerId, itemInstance.ItemId, itemInstance);

            if (existingStack != null && existingStack.RemainingSpace > 0)
            {
                // Stack'e ekle;
                var addedQuantity = Math.Min(
                    itemInstance.Quantity,
                    existingStack.RemainingSpace);

                if (addedQuantity > 0)
                {
                    existingStack.ItemInstance.Quantity += addedQuantity;
                    existingStack.ItemInstance.TotalWeight =
                        existingStack.ItemInstance.BaseWeight * existingStack.ItemInstance.Quantity;
                    existingStack.ItemInstance.UpdatedAt = DateTime.UtcNow;

                    itemInstance.Quantity -= addedQuantity;

                    return new PlacementResult;
                    {
                        Success = true,
                        ItemInstance = existingStack.ItemInstance,
                        Slot = existingStack,
                        AddedQuantity = addedQuantity,
                        WasStacked = true;
                    };
                }
            }

            // Stack yapılamadı, yeni slota yerleştir;
            return await PlaceInNewSlotAsync(itemInstance, container);
        }

        private async Task<PlacementResult> PlaceInNewSlotAsync(
            ItemInstance itemInstance, InventoryContainer container)
        {
            var availableSlot = await FindAvailableSlotAsync(container.ContainerId);
            if (availableSlot == null)
            {
                return new PlacementResult;
                {
                    Success = false,
                    Error = "Yeterli slot yok"
                };
            }

            // Slot'a yerleştir;
            availableSlot.ItemInstance = itemInstance;
            availableSlot.IsOccupied = true;
            availableSlot.LastUpdated = DateTime.UtcNow;

            return new PlacementResult;
            {
                Success = true,
                ItemInstance = itemInstance,
                Slot = availableSlot,
                AddedQuantity = itemInstance.Quantity,
                WasStacked = false;
            };
        }

        private async Task PlaceItemInSlotAsync(ItemInstance itemInstance, InventorySlot slot)
        {
            slot.ItemInstance = itemInstance;
            slot.IsOccupied = true;
            slot.LastUpdated = DateTime.UtcNow;

            // Slot ID'sini item instance'a kaydet;
            itemInstance.SlotId = slot.SlotId;
            itemInstance.ContainerId = slot.ContainerId;
            itemInstance.UpdatedAt = DateTime.UtcNow;

            // Slot'u güncelle;
            _slots[slot.SlotId] = slot;
        }

        private async Task UpdateContainerWeightAsync(string containerId)
        {
            var container = await GetContainerAsync(containerId);
            if (container == null) return;

            var oldWeight = container.CurrentWeight;
            container.CurrentWeight = CalculateContainerWeight(containerId);
            container.LastWeightUpdate = DateTime.UtcNow;

            // Event tetikle;
            if (Math.Abs(container.CurrentWeight - oldWeight) > 0.001f)
            {
                OnWeightChanged(new InventoryWeightEvent;
                {
                    ContainerId = containerId,
                    WeightChange = container.CurrentWeight - oldWeight,
                    NewTotalWeight = container.CurrentWeight,
                    OldTotalWeight = oldWeight,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private float CalculateContainerWeight(string containerId)
        {
            return _slots.Values;
                .Where(s => s.ContainerId == containerId && s.IsOccupied && s.ItemInstance != null)
                .Sum(s => s.ItemInstance.TotalWeight);
        }

        private void UpdateInventoryStatistics(List<ItemInstance> addedItems)
        {
            // İstatistikleri güncelle;
            // Burada inventory istatistiklerini takip edebilirsiniz;
        }

        #endregion;

        #region Private Methods;

        private async Task InitializeCoreComponentsAsync()
        {
            // Temel bileşenleri başlat;
            await Task.CompletedTask;
        }

        private async Task CreateDefaultContainersAsync()
        {
            // Varsayılan konteynırları oluştur;
            var defaultContainers = new[]
            {
                new InventoryContainer;
                {
                    ContainerId = "main",
                    Name = "Ana Envanter",
                    Type = ContainerType.Main,
                    MaxWeight = 100,
                    MaxSlots = 30,
                    IsDefaultContainer = true,
                    CreatedAt = DateTime.UtcNow;
                },
                new InventoryContainer;
                {
                    ContainerId = "equipment",
                    Name = "Ekipman",
                    Type = ContainerType.Equipment,
                    MaxWeight = 50,
                    MaxSlots = 10,
                    IsDefaultContainer = true,
                    CreatedAt = DateTime.UtcNow;
                },
                new InventoryContainer;
                {
                    ContainerId = "quest",
                    Name = "Görev Eşyaları",
                    Type = ContainerType.Quest,
                    MaxWeight = 20,
                    MaxSlots = 10,
                    IsDefaultContainer = true,
                    CreatedAt = DateTime.UtcNow;
                }
            };

            foreach (var container in defaultContainers)
            {
                _containers[container.ContainerId] = container;
            }
        }

        private async Task InitializeSlotsAsync()
        {
            // Her konteynır için slot'lar oluştur;
            foreach (var container in _containers.Values)
            {
                for (int i = 0; i < container.MaxSlots; i++)
                {
                    var slot = new InventorySlot;
                    {
                        SlotId = $"{container.ContainerId}_slot_{i}",
                        ContainerId = container.ContainerId,
                        SlotIndex = i,
                        Position = i,
                        IsOccupied = false,
                        CreatedAt = DateTime.UtcNow;
                    };

                    _slots[slot.SlotId] = slot;
                }
            }
        }

        private async Task LoadInventoryRulesAsync()
        {
            // Envanter kurallarını yükle;
            await Task.CompletedTask;
        }

        private async Task ClearOldCacheAsync()
        {
            // Eski önbelleği temizle;
            await Task.CompletedTask;
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
                    // Yönetilen kaynakları serbest bırak;
                    _containers?.Clear();
                    _slots?.Clear();
                    _items?.Clear();
                }

                _disposed = true;
            }
        }

        ~Inventory()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public enum ContainerType;
    {
        Main,
        Equipment,
        Quest,
        Storage,
        Temporary,
        Guild,
        Bank,
        Special;
    }

    public enum InventoryEventType;
    {
        ItemAdded,
        ItemRemoved,
        ItemMoved,
        ItemEquipped,
        ItemUnequipped,
        ItemUsed,
        ContainerAdded,
        ContainerRemoved,
        InventoryOrganized,
        InventoryReset,
        WeightChanged;
    }

    public enum ResetType;
    {
        Full,
        ItemsOnly,
        ContainersOnly,
        Custom;
    }

    public enum MonitoringType;
    {
        Performance,
        Usage,
        Storage,
        Health,
        Comprehensive;
    }

    public enum SerializationFormat;
    {
        Json,
        Binary,
        Xml;
    }

    public enum BackupType;
    {
        Full,
        Incremental,
        Differential,
        Automatic;
    }

    public class InventoryContainer;
    {
        public string ContainerId { get; set; }
        public string Name { get; set; }
        public ContainerType Type { get; set; }
        public float MaxWeight { get; set; }
        public float CurrentWeight { get; set; }
        public int MaxSlots { get; set; }
        public int UsedSlots { get; set; }
        public int RemainingSlots => MaxSlots - UsedSlots;
        public float RemainingCapacity => MaxWeight - CurrentWeight;
        public bool IsDefaultContainer { get; set; }
        public bool IsLocked { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime LastWeightUpdate { get; set; }
    }

    public class InventorySlot;
    {
        public string SlotId { get; set; }
        public string ContainerId { get; set; }
        public int SlotIndex { get; set; }
        public int Position { get; set; }
        public ItemInstance ItemInstance { get; set; }
        public bool IsOccupied => ItemInstance != null;
        public bool IsLocked { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class ItemInstance;
    {
        public string InstanceId { get; set; }
        public string ItemId { get; set; }
        public int Quantity { get; set; }
        public float Quality { get; set; }
        public float Durability { get; set; }
        public float MaxDurability { get; set; }
        public float Weight { get; set; }
        public float BaseWeight { get; set; }
        public float TotalWeight => Weight * Quantity;
        public string SlotId { get; set; }
        public string ContainerId { get; set; }
        public bool IsEquipped { get; set; }
        public string EquipSlot { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? EquippedAt { get; set; }
        public DateTime? UnequippedAt { get; set; }
        public DateTime? CooldownUntil { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string EquippedBy { get; set; }
        public string UnequippedBy { get; set; }
        public string BoundTo { get; set; }
        public bool IsSoulbound { get; set; }
        public string Category { get; set; }
        public string Rarity { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public Dictionary<string, object> CustomData { get; set; }
    }

    public class AddItemRequest;
    {
        public string ItemId { get; set; }
        public int Quantity { get; set; }
        public string ContainerId { get; set; }
        public float? Quality { get; set; }
        public float? Durability { get; set; }
        public bool? IsSoulbound { get; set; }
        public string BoundTo { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool AutoStack { get; set; } = true;
        public bool PartialPlacement { get; set; } = true;
        public Dictionary<string, object> Metadata { get; set; }
        public Dictionary<string, object> CustomData { get; set; }
    }

    public class AddItemResult;
    {
        public bool Success { get; set; }
        public List<ItemInstance> AddedItems { get; set; }
        public int TotalQuantity { get; set; }
        public string ContainerId { get; set; }
        public List<PlacementResult> PlacementResults { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class PlacementResult;
    {
        public bool Success { get; set; }
        public ItemInstance ItemInstance { get; set; }
        public InventorySlot Slot { get; set; }
        public int AddedQuantity { get; set; }
        public bool WasStacked { get; set; }
        public string Error { get; set; }
    }

    public class RemoveItemRequest;
    {
        public string InstanceId { get; set; }
        public int Quantity { get; set; }
        public string Reason { get; set; }
        public bool DestroyItem { get; set; }
        public string TransferTo { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class RemoveItemResult;
    {
        public bool Success { get; set; }
        public ItemInstance ItemInstance { get; set; }
        public int RemovedQuantity { get; set; }
        public int RemainingQuantity { get; set; }
        public bool FullyRemoved { get; set; }
        public string ContainerId { get; set; }
        public string SlotId { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    // Diğer request/result sınıfları...

    public class InventoryEvent;
    {
        public InventoryEventType EventType { get; set; }
        public ItemInstance ItemInstance { get; set; }
        public string ContainerId { get; set; }
        public string SlotId { get; set; }
        public string SourceContainerId { get; set; }
        public string TargetContainerId { get; set; }
        public string SourceSlotId { get; set; }
        public string TargetSlotId { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    public class InventoryWeightEvent;
    {
        public string ContainerId { get; set; }
        public ItemInstance ItemInstance { get; set; }
        public float WeightChange { get; set; }
        public float NewTotalWeight { get; set; }
        public float OldTotalWeight { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class InventoryStats;
    {
        public DateTime Timestamp { get; set; }
        public int TotalItems { get; set; }
        public int TotalContainers { get; set; }
        public int TotalSlots { get; set; }
        public int OccupiedSlots { get; set; }
        public int EmptySlots { get; set; }
        public Dictionary<string, ContainerStats> ContainerStats { get; set; }
        public Dictionary<string, CategoryStats> CategoryStats { get; set; }
        public UsageStats UsageStats { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class ContainerStats;
    {
        public string ContainerId { get; set; }
        public string ContainerName { get; set; }
        public int TotalSlots { get; set; }
        public int OccupiedSlots { get; set; }
        public float TotalWeight { get; set; }
        public float MaxWeight { get; set; }
        public decimal TotalValue { get; set; }
        public int ItemCount { get; set; }
    }

    // Diğer yardımcı sınıflar...

    #endregion;

    #region Exception Classes;

    public class InventoryException : Exception
    {
        public InventoryException(string message) : base(message) { }
        public InventoryException(string message, Exception inner) : base(message, inner) { }
    }

    public class InventoryInitializationException : InventoryException;
    {
        public InventoryInitializationException(string message) : base(message) { }
        public InventoryInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ItemNotFoundException : InventoryException;
    {
        public ItemNotFoundException(string message) : base(message) { }
        public ItemNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class ItemInstanceNotFoundException : InventoryException;
    {
        public ItemInstanceNotFoundException(string message) : base(message) { }
        public ItemInstanceNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class InvalidQuantityException : InventoryException;
    {
        public InvalidQuantityException(string message) : base(message) { }
        public InvalidQuantityException(string message, Exception inner) : base(message, inner) { }
    }

    public class NoSpaceException : InventoryException;
    {
        public NoSpaceException(string message) : base(message) { }
        public NoSpaceException(string message, Exception inner) : base(message, inner) { }
    }

    public class ItemAddException : InventoryException;
    {
        public ItemAddException(string message) : base(message) { }
        public ItemAddException(string message, Exception inner) : base(message, inner) { }
    }

    public class ItemRemoveException : InventoryException;
    {
        public ItemRemoveException(string message) : base(message) { }
        public ItemRemoveException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContainerNotFoundException : InventoryException;
    {
        public ContainerNotFoundException(string message) : base(message) { }
        public ContainerNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class ItemNotInSlotException : InventoryException;
    {
        public ItemNotInSlotException(string message) : base(message) { }
        public ItemNotInSlotException(string message, Exception inner) : base(message, inner) { }
    }

    public class MoveValidationException : InventoryException;
    {
        public MoveValidationException(string message) : base(message) { }
        public MoveValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class MoveItemException : InventoryException;
    {
        public MoveItemException(string message) : base(message) { }
        public MoveItemException(string message, Exception inner) : base(message, inner) { }
    }

    public class NotEquippableException : InventoryException;
    {
        public NotEquippableException(string message) : base(message) { }
        public NotEquippableException(string message, Exception inner) : base(message, inner) { }
    }

    public class SlotIncompatibleException : InventoryException;
    {
        public SlotIncompatibleException(string message) : base(message) { }
        public SlotIncompatibleException(string message, Exception inner) : base(message, inner) { }
    }

    public class RequirementsNotMetException : InventoryException;
    {
        public RequirementsNotMetException(string message) : base(message) { }
        public RequirementsNotMetException(string message, Exception inner) : base(message, inner) { }
    }

    public class EquipItemException : InventoryException;
    {
        public EquipItemException(string message) : base(message) { }
        public EquipItemException(string message, Exception inner) : base(message, inner) { }
    }

    public class NotEquippedException : InventoryException;
    {
        public NotEquippedException(string message) : base(message) { }
        public NotEquippedException(string message, Exception inner) : base(message, inner) { }
    }

    public class UnequipItemException : InventoryException;
    {
        public UnequipItemException(string message) : base(message) { }
        public UnequipItemException(string message, Exception inner) : base(message, inner) { }
    }

    public class NotUsableException : InventoryException;
    {
        public NotUsableException(string message) : base(message) { }
        public NotUsableException(string message, Exception inner) : base(message, inner) { }
    }

    public class UseItemException : InventoryException;
    {
        public UseItemException(string message) : base(message) { }
        public UseItemException(string message, Exception inner) : base(message, inner) { }
    }

    public class OrganizeException : InventoryException;
    {
        public OrganizeException(string message) : base(message) { }
        public OrganizeException(string message, Exception inner) : base(message, inner) { }
    }

    public class OptimizeException : InventoryException;
    {
        public OptimizeException(string message) : base(message) { }
        public OptimizeException(string message, Exception inner) : base(message, inner) { }
    }

    public class SearchException : InventoryException;
    {
        public SearchException(string message) : base(message) { }
        public SearchException(string message, Exception inner) : base(message, inner) { }
    }

    public class CleanupException : InventoryException;
    {
        public CleanupException(string message) : base(message) { }
        public CleanupException(string message, Exception inner) : base(message, inner) { }
    }

    public class SaveException : InventoryException;
    {
        public SaveException(string message) : base(message) { }
        public SaveException(string message, Exception inner) : base(message, inner) { }
    }

    public class LoadException : InventoryException;
    {
        public LoadException(string message) : base(message) { }
        public LoadException(string message, Exception inner) : base(message, inner) { }
    }

    public class StatsException : InventoryException;
    {
        public StatsException(string message) : base(message) { }
        public StatsException(string message, Exception inner) : base(message, inner) { }
    }

    public class ValidationException : InventoryException;
    {
        public ValidationException(string message) : base(message) { }
        public ValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ResetException : InventoryException;
    {
        public ResetException(string message) : base(message) { }
        public ResetException(string message, Exception inner) : base(message, inner) { }
    }

    public class BackupException : InventoryException;
    {
        public BackupException(string message) : base(message) { }
        public BackupException(string message, Exception inner) : base(message, inner) { }
    }

    public class RestoreException : InventoryException;
    {
        public RestoreException(string message) : base(message) { }
        public RestoreException(string message, Exception inner) : base(message, inner) { }
    }

    public class MonitoringException : InventoryException;
    {
        public MonitoringException(string message) : base(message) { }
        public MonitoringException(string message, Exception inner) : base(message, inner) { }
    }

    public class InventoryNotInitializedException : InventoryException;
    {
        public InventoryNotInitializedException(string message) : base(message) { }
        public InventoryNotInitializedException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
