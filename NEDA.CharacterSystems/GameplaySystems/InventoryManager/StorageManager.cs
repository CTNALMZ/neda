// NEDA.CharacterSystems/GameplaySystems/InventoryManager/StorageManager.cs;

using NEDA.Animation.SequenceEditor.KeyframeEditing;
using NEDA.Brain.DecisionMaking.OptimizationEngine;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.MemorySystem.LongTermMemory;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.CharacterSystems.CharacterCreator.MorphTargets;
using NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
using NEDA.Communication.EventBus;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Services.FileService;
using NEDA.Services.NotificationService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.GameplaySystems.InventoryManager;
{
    /// <summary>
    /// Depolama yönetim sistemi - Envanter, sandık, depolama alanları, item organizasyonu; 
    /// ve depolama optimizasyonunu yönetir;
    /// </summary>
    public class StorageManager : IStorageManager, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IAppConfig _appConfig;
        private readonly IFileManager _fileManager;
        private readonly INotificationManager _notificationManager;
        private readonly IEventBus _eventBus;
        private readonly IMetricsCollector _metricsCollector;

        private readonly Dictionary<string, StorageContainer> _containers;
        private readonly Dictionary<string, StorageGrid> _storageGrids;
        private readonly Dictionary<string, StorageProfile> _storageProfiles;
        private readonly StorageOptimizer _storageOptimizer;
        private readonly ItemOrganizer _itemOrganizer;
        private readonly StorageSecurity _storageSecurity;

        private bool _isInitialized;
        private readonly string _storageDatabasePath;
        private readonly string _storageTemplatesPath;
        private readonly string _itemDatabasePath;
        private readonly SemaphoreSlim _storageLock;
        private readonly Timer _autoSaveTimer;
        private readonly Timer _cleanupTimer;
        private readonly JsonSerializerOptions _jsonOptions;

        private const int AUTO_SAVE_INTERVAL = 300000; // 5 dakika;
        private const int CLEANUP_INTERVAL = 3600000; // 1 saat;
        private const int MAX_STORAGE_SLOTS = 1000;
        private const int MAX_STACK_SIZE = 999;

        // Eventler;
        public event EventHandler<StorageCreatedEventArgs> StorageCreated;
        public event EventHandler<ItemStoredEventArgs> ItemStored;
        public event EventHandler<ItemRetrievedEventArgs> ItemRetrieved;
        public event EventHandler<StorageModifiedEventArgs> StorageModified;
        public event EventHandler<StorageOptimizedEventArgs> StorageOptimized;
        public event EventHandler<StorageSecurityEventEventArgs> StorageSecurityEvent;

        /// <summary>
        /// StorageManager constructor;
        /// </summary>
        public StorageManager(
            ILogger<StorageManager> logger,
            IAppConfig appConfig,
            IFileManager fileManager,
            INotificationManager notificationManager,
            IEventBus eventBus,
            IMetricsCollector metricsCollector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));

            _containers = new Dictionary<string, StorageContainer>();
            _storageGrids = new Dictionary<string, StorageGrid>();
            _storageProfiles = new Dictionary<string, StorageProfile>();
            _storageOptimizer = new StorageOptimizer();
            _itemOrganizer = new ItemOrganizer();
            _storageSecurity = new StorageSecurity();

            _storageDatabasePath = Path.Combine(
                _appConfig.GetValue<string>("GameplaySystems:StorageDatabasePath") ?? "Data/Storage",
                "Storage.db");
            _storageTemplatesPath = Path.Combine(
                _appConfig.GetValue<string>("GameplaySystems:StorageTemplatesPath") ?? "Data/Storage/Templates",
                "Templates");
            _itemDatabasePath = Path.Combine(
                _appConfig.GetValue<string>("GameplaySystems:ItemDatabasePath") ?? "Data/Items",
                "Items.json");

            _storageLock = new SemaphoreSlim(1, 1);

            // JSON serialization ayarları;
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters =
                {
                    new JsonStringEnumConverter(),
                    new StorageSlotConverter(),
                    new ItemPropertyConverter()
                }
            };

            // Otomatik kaydetme timer'ı;
            _autoSaveTimer = new Timer(AutoSaveCallback, null, AUTO_SAVE_INTERVAL, AUTO_SAVE_INTERVAL);

            // Temizlik timer'ı;
            _cleanupTimer = new Timer(CleanupCallback, null, CLEANUP_INTERVAL, CLEANUP_INTERVAL);

            _logger.LogInformation("StorageManager initialized");
        }

        /// <summary>
        /// StorageManager'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Initializing StorageManager...");

                // Dizinleri oluştur;
                await EnsureDirectoriesExistAsync(cancellationToken);

                // Storage database'ini yükle;
                await LoadStorageDatabaseAsync(cancellationToken);

                // Storage template'lerini yükle;
                await LoadStorageTemplatesAsync(cancellationToken);

                // Item database'ini yükle;
                await LoadItemDatabaseAsync(cancellationToken);

                // Optimizer'ı başlat;
                await _storageOptimizer.InitializeAsync(cancellationToken);

                // Organizer'ı başlat;
                await _itemOrganizer.InitializeAsync(cancellationToken);

                // Security'yi başlat;
                await _storageSecurity.InitializeAsync(cancellationToken);

                // Event bus subscription'ları;
                await SubscribeToEventsAsync(cancellationToken);

                _isInitialized = true;
                _logger.LogInformation($"StorageManager initialized successfully with {_containers.Count} containers");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize StorageManager");
                throw new StorageManagerException("Failed to initialize StorageManager", ex);
            }
        }

        /// <summary>
        /// Yeni depolama konteyneri oluşturur;
        /// </summary>
        public async Task<StorageContainer> CreateStorageContainerAsync(
            string containerId,
            CreateStorageOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= CreateStorageOptions.Default;

                if (_containers.ContainsKey(containerId))
                {
                    if (options.OverwriteExisting)
                    {
                        _logger.LogWarning($"Container {containerId} already exists, overwriting");
                        await RemoveContainerInternalAsync(containerId, cancellationToken);
                    }
                    else;
                    {
                        throw new ContainerAlreadyExistsException($"Container with ID {containerId} already exists");
                    }
                }

                _logger.LogInformation($"Creating storage container: {containerId}");

                // Template'den container oluştur;
                StorageContainer container;
                if (!string.IsNullOrEmpty(options.TemplateId))
                {
                    container = await CreateContainerFromTemplateAsync(
                        containerId,
                        options.TemplateId,
                        options,
                        cancellationToken);
                }
                else;
                {
                    // Custom container oluştur;
                    container = new StorageContainer;
                    {
                        Id = containerId,
                        Name = options.Name ?? $"Container_{containerId}",
                        OwnerId = options.OwnerId,
                        ContainerType = options.ContainerType,
                        MaxSlots = options.MaxSlots,
                        MaxWeight = options.MaxWeight,
                        MaxVolume = options.MaxVolume,
                        Slots = new StorageSlot[options.MaxSlots],
                        CreatedAt = DateTime.UtcNow,
                        LastModified = DateTime.UtcNow,
                        AccessPermissions = options.AccessPermissions ?? new AccessPermissions(),
                        SecuritySettings = options.SecuritySettings ?? new StorageSecuritySettings(),
                        Metadata = options.Metadata ?? new Dictionary<string, object>()
                    };

                    // Slot'ları başlat;
                    for (int i = 0; i < container.Slots.Length; i++)
                    {
                        container.Slots[i] = new StorageSlot;
                        {
                            SlotIndex = i,
                            IsLocked = false,
                            IsReserved = false;
                        };
                    }
                }

                // Container'ı validasyon'dan geçir;
                ValidateContainer(container);

                // Security ayarlarını uygula;
                await _storageSecurity.ApplySecuritySettingsAsync(container, cancellationToken);

                // Container'ı kaydet;
                _containers[containerId] = container;
                await SaveContainerAsync(container, cancellationToken);

                // Storage grid oluştur (eğer grid-based storage ise)
                if (options.CreateStorageGrid)
                {
                    await CreateStorageGridAsync(containerId, options.GridOptions, cancellationToken);
                }

                _logger.LogInformation($"Storage container created: {container.Name} ({containerId}), slots: {container.MaxSlots}");

                // Event tetikle;
                StorageCreated?.Invoke(this, new StorageCreatedEventArgs;
                {
                    ContainerId = containerId,
                    Container = container,
                    Options = options,
                    Timestamp = DateTime.UtcNow;
                });

                // Event bus'a publish et;
                await _eventBus.PublishAsync(new StorageCreatedEvent;
                {
                    ContainerId = containerId,
                    OwnerId = container.OwnerId,
                    ContainerType = container.ContainerType,
                    SlotCount = container.MaxSlots,
                    CreatedAt = DateTime.UtcNow;
                }, cancellationToken);

                // Metrics kaydı;
                _metricsCollector.RecordMetric("storage_container_created", 1, new Dictionary<string, string>
                {
                    ["container_type"] = container.ContainerType.ToString(),
                    ["owner_id"] = container.OwnerId ?? "system"
                });

                return container;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create storage container {containerId}");
                throw new ContainerCreationException($"Failed to create storage container: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Item'ı depolama konteynerine ekler;
        /// </summary>
        public async Task<StorageResult> StoreItemAsync(
            string containerId,
            GameItem item,
            StoreItemOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            if (item == null)
                throw new ArgumentNullException(nameof(item));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= StoreItemOptions.Default;

                // Container'ı bul;
                if (!_containers.TryGetValue(containerId, out var container))
                {
                    throw new ContainerNotFoundException($"Container {containerId} not found");
                }

                // Access kontrolü;
                if (!await CheckAccessAsync(container, options.AccessorId, StorageAction.Store, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for storing item in container {containerId}");
                }

                _logger.LogDebug($"Storing item {item.Id} in container {containerId}");

                // Item validasyonu;
                ValidateItemForStorage(item, container, options);

                // Stackable item kontrolü;
                if (item.IsStackable && options.StackIfPossible)
                {
                    var stackResult = await TryStackItemAsync(container, item, options, cancellationToken);
                    if (stackResult.Success)
                    {
                        _logger.LogDebug($"Item stacked: {item.Id}, quantity: {stackResult.QuantityStored}");
                        return stackResult;
                    }
                }

                // Boş slot bul;
                var slotIndex = await FindAvailableSlotAsync(container, item, options, cancellationToken);
                if (slotIndex == -1)
                {
                    // Otomatik optimizasyon deneyebilir;
                    if (options.AutoOptimize)
                    {
                        var optimizationResult = await OptimizeContainerAsync(
                            containerId,
                            new OptimizationOptions { Priority = OptimizationPriority.High },
                            cancellationToken);

                        if (optimizationResult.Success)
                        {
                            // Tekrar slot ara;
                            slotIndex = await FindAvailableSlotAsync(container, item, options, cancellationToken);
                        }
                    }

                    if (slotIndex == -1)
                    {
                        throw new NoAvailableSlotsException($"No available slots in container {containerId} for item {item.Id}");
                    }
                }

                // Slot'u hazırla;
                var slot = container.Slots[slotIndex];
                if (!PrepareSlotForItem(slot, item, options))
                {
                    throw new SlotPreparationException($"Failed to prepare slot {slotIndex} for item {item.Id}");
                }

                // Item'ı slot'a yerleştir;
                slot.Item = item.Clone();
                slot.Item.InstanceId = Guid.NewGuid().ToString();
                slot.Quantity = options.Quantity ?? 1;
                slot.LastModified = DateTime.UtcNow;
                slot.IsOccupied = true;

                // Container istatistiklerini güncelle;
                UpdateContainerStats(container, item, slot.Quantity);

                // Weight ve volume kontrolü;
                if (!CheckWeightAndVolume(container, item, slot.Quantity))
                {
                    // Geri al;
                    slot.Item = null;
                    slot.Quantity = 0;
                    slot.IsOccupied = false;
                    throw new CapacityExceededException($"Capacity exceeded in container {containerId}");
                }

                // Security logging;
                await _storageSecurity.LogStorageActionAsync(
                    container,
                    StorageAction.Store,
                    item.Id,
                    options.AccessorId,
                    cancellationToken);

                // Container'ı kaydet;
                container.LastModified = DateTime.UtcNow;
                await SaveContainerAsync(container, cancellationToken);

                var result = new StorageResult;
                {
                    Success = true,
                    ContainerId = containerId,
                    ItemId = item.Id,
                    InstanceId = slot.Item.InstanceId,
                    SlotIndex = slotIndex,
                    QuantityStored = slot.Quantity,
                    RemainingQuantity = options.Quantity.HasValue ? 0 : 1,
                    StorageTime = DateTime.UtcNow,
                    StorageMetadata = new Dictionary<string, object>
                    {
                        ["slot_type"] = slot.SlotType.ToString(),
                        ["item_quality"] = item.Quality,
                        ["stacked"] = false;
                    }
                };

                _logger.LogInformation($"Item stored: {item.Name} ({item.Id}) in container {containerId}, slot {slotIndex}, quantity: {slot.Quantity}");

                // Event tetikle;
                ItemStored?.Invoke(this, new ItemStoredEventArgs;
                {
                    ContainerId = containerId,
                    Item = item,
                    SlotIndex = slotIndex,
                    Quantity = slot.Quantity,
                    Result = result,
                    AccessorId = options.AccessorId,
                    Timestamp = DateTime.UtcNow;
                });

                // Event bus'a publish et;
                await _eventBus.PublishAsync(new ItemStoredEvent;
                {
                    ContainerId = containerId,
                    ItemId = item.Id,
                    InstanceId = slot.Item.InstanceId,
                    SlotIndex = slotIndex,
                    Quantity = slot.Quantity,
                    ItemType = item.ItemType,
                    StoredAt = DateTime.UtcNow,
                    AccessorId = options.AccessorId;
                }, cancellationToken);

                // Metrics kaydı;
                _metricsCollector.RecordMetric("item_stored", slot.Quantity, new Dictionary<string, string>
                {
                    ["container_id"] = containerId,
                    ["item_type"] = item.ItemType,
                    ["item_rarity"] = item.Rarity.ToString()
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store item {item?.Id} in container {containerId}");
                throw new ItemStorageException($"Failed to store item: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Item'ı depolama konteynerinden alır;
        /// </summary>
        public async Task<RetrievalResult> RetrieveItemAsync(
            string containerId,
            string itemInstanceId,
            RetrieveItemOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            if (string.IsNullOrEmpty(itemInstanceId))
                throw new ArgumentException("Item instance ID cannot be null or empty", nameof(itemInstanceId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= RetrieveItemOptions.Default;

                // Container'ı bul;
                if (!_containers.TryGetValue(containerId, out var container))
                {
                    throw new ContainerNotFoundException($"Container {containerId} not found");
                }

                // Access kontrolü;
                if (!await CheckAccessAsync(container, options.AccessorId, StorageAction.Retrieve, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for retrieving item from container {containerId}");
                }

                // Item'ı bul;
                var (slotIndex, slot) = FindItemSlot(container, itemInstanceId);
                if (slot == null || slot.Item == null)
                {
                    throw new ItemNotFoundException($"Item instance {itemInstanceId} not found in container {containerId}");
                }

                var item = slot.Item;

                _logger.LogDebug($"Retrieving item {item.Id} (instance {itemInstanceId}) from container {containerId}, slot {slotIndex}");

                // Quantity kontrolü;
                int retrieveQuantity = options.Quantity ?? slot.Quantity;
                if (retrieveQuantity > slot.Quantity)
                {
                    throw new InsufficientQuantityException(
                        $"Requested quantity {retrieveQuantity} exceeds available quantity {slot.Quantity}");
                }

                // Slot lock kontrolü;
                if (slot.IsLocked && !options.ForceRetrieve)
                {
                    throw new SlotLockedException($"Slot {slotIndex} is locked");
                }

                // Item'ı slot'tan al;
                var retrievedItem = item.Clone();
                retrievedItem.InstanceId = itemInstanceId;

                // Partial retrieval;
                if (retrieveQuantity < slot.Quantity)
                {
                    // Partial quantity al;
                    slot.Quantity -= retrieveQuantity;
                    slot.LastModified = DateTime.UtcNow;

                    // Stackable item ise, quantity güncelle;
                    if (slot.Item.IsStackable)
                    {
                        // Stack için özel işlem;
                    }
                }
                else;
                {
                    // Tüm quantity'yi al, slot'u temizle;
                    slot.Item = null;
                    slot.Quantity = 0;
                    slot.IsOccupied = false;
                    slot.LastModified = DateTime.UtcNow;
                }

                // Container istatistiklerini güncelle;
                UpdateContainerStatsAfterRetrieval(container, item, retrieveQuantity);

                // Security logging;
                await _storageSecurity.LogStorageActionAsync(
                    container,
                    StorageAction.Retrieve,
                    item.Id,
                    options.AccessorId,
                    cancellationToken);

                // Container'ı kaydet;
                container.LastModified = DateTime.UtcNow;
                await SaveContainerAsync(container, cancellationToken);

                var result = new RetrievalResult;
                {
                    Success = true,
                    ContainerId = containerId,
                    Item = retrievedItem,
                    OriginalInstanceId = itemInstanceId,
                    SlotIndex = slotIndex,
                    QuantityRetrieved = retrieveQuantity,
                    RemainingQuantity = slot.Quantity,
                    RetrievalTime = DateTime.UtcNow,
                    RetrievalMetadata = new Dictionary<string, object>
                    {
                        ["slot_index"] = slotIndex,
                        ["partial_retrieval"] = retrieveQuantity < slot.Quantity,
                        ["item_condition"] = item.Condition;
                    }
                };

                _logger.LogInformation($"Item retrieved: {item.Name} ({item.Id}) from container {containerId}, quantity: {retrieveQuantity}");

                // Event tetikle;
                ItemRetrieved?.Invoke(this, new ItemRetrievedEventArgs;
                {
                    ContainerId = containerId,
                    Item = retrievedItem,
                    SlotIndex = slotIndex,
                    Quantity = retrieveQuantity,
                    Result = result,
                    AccessorId = options.AccessorId,
                    Timestamp = DateTime.UtcNow;
                });

                // Event bus'a publish et;
                await _eventBus.PublishAsync(new ItemRetrievedEvent;
                {
                    ContainerId = containerId,
                    ItemId = item.Id,
                    InstanceId = itemInstanceId,
                    SlotIndex = slotIndex,
                    Quantity = retrieveQuantity,
                    ItemType = item.ItemType,
                    RetrievedAt = DateTime.UtcNow,
                    AccessorId = options.AccessorId;
                }, cancellationToken);

                // Metrics kaydı;
                _metricsCollector.RecordMetric("item_retrieved", retrieveQuantity, new Dictionary<string, string>
                {
                    ["container_id"] = containerId,
                    ["item_type"] = item.ItemType,
                    ["retrieval_type"] = options.Quantity.HasValue ? "partial" : "full"
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to retrieve item instance {itemInstanceId} from container {containerId}");
                throw new ItemRetrievalException($"Failed to retrieve item: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Item'ları taşır veya transfer eder;
        /// </summary>
        public async Task<TransferResult> TransferItemAsync(
            string sourceContainerId,
            string targetContainerId,
            string itemInstanceId,
            TransferOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(sourceContainerId))
                throw new ArgumentException("Source container ID cannot be null or empty", nameof(sourceContainerId));

            if (string.IsNullOrEmpty(targetContainerId))
                throw new ArgumentException("Target container ID cannot be null or empty", nameof(targetContainerId));

            if (string.IsNullOrEmpty(itemInstanceId))
                throw new ArgumentException("Item instance ID cannot be null or empty", nameof(itemInstanceId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= TransferOptions.Default;

                _logger.LogDebug($"Transferring item {itemInstanceId} from {sourceContainerId} to {targetContainerId}");

                // Source container'ı bul;
                if (!_containers.TryGetValue(sourceContainerId, out var sourceContainer))
                {
                    throw new ContainerNotFoundException($"Source container {sourceContainerId} not found");
                }

                // Target container'ı bul;
                if (!_containers.TryGetValue(targetContainerId, out var targetContainer))
                {
                    throw new ContainerNotFoundException($"Target container {targetContainerId} not found");
                }

                // Access kontrolü;
                if (!await CheckAccessAsync(sourceContainer, options.AccessorId, StorageAction.Transfer, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for transferring from container {sourceContainerId}");
                }

                if (!await CheckAccessAsync(targetContainer, options.AccessorId, StorageAction.Store, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for storing in container {targetContainerId}");
                }

                // Item'ı source'ta bul;
                var (sourceSlotIndex, sourceSlot) = FindItemSlot(sourceContainer, itemInstanceId);
                if (sourceSlot == null || sourceSlot.Item == null)
                {
                    throw new ItemNotFoundException($"Item instance {itemInstanceId} not found in source container {sourceContainerId}");
                }

                var item = sourceSlot.Item;
                int transferQuantity = options.Quantity ?? sourceSlot.Quantity;

                // Target'ta yer kontrolü;
                var targetSlotIndex = await FindAvailableSlotAsync(
                    targetContainer,
                    item,
                    new StoreItemOptions;
                    {
                        AccessorId = options.AccessorId,
                        StackIfPossible = options.StackInTarget;
                    },
                    cancellationToken);

                if (targetSlotIndex == -1)
                {
                    throw new NoAvailableSlotsException($"No available slots in target container {targetContainerId}");
                }

                // Transfer'i gerçekleştir;
                var targetSlot = targetContainer.Slots[targetSlotIndex];

                // Stack kontrolü;
                bool wasStacked = false;
                if (item.IsStackable && options.StackInTarget && targetSlot.Item != null &&
                    targetSlot.Item.Id == item.Id && targetSlot.Quantity < MAX_STACK_SIZE)
                {
                    // Stack yap;
                    int stackSpace = MAX_STACK_SIZE - targetSlot.Quantity;
                    int stackAmount = Math.Min(transferQuantity, stackSpace);

                    targetSlot.Quantity += stackAmount;
                    sourceSlot.Quantity -= stackAmount;

                    if (sourceSlot.Quantity == 0)
                    {
                        sourceSlot.Item = null;
                        sourceSlot.IsOccupied = false;
                    }

                    wasStacked = true;
                    transferQuantity = stackAmount;
                }
                else;
                {
                    // Yeni slot'a taşı;
                    targetSlot.Item = item.Clone();
                    targetSlot.Item.InstanceId = Guid.NewGuid().ToString(); // Yeni instance ID;
                    targetSlot.Quantity = transferQuantity;
                    targetSlot.IsOccupied = true;
                    targetSlot.LastModified = DateTime.UtcNow;

                    // Source'tan kaldır;
                    sourceSlot.Quantity -= transferQuantity;
                    if (sourceSlot.Quantity == 0)
                    {
                        sourceSlot.Item = null;
                        sourceSlot.IsOccupied = false;
                    }
                }

                // Container istatistiklerini güncelle;
                UpdateContainerStatsAfterRetrieval(sourceContainer, item, transferQuantity);
                UpdateContainerStats(targetContainer, item, transferQuantity);

                // Security logging;
                await _storageSecurity.LogStorageActionAsync(
                    sourceContainer,
                    StorageAction.Transfer,
                    item.Id,
                    options.AccessorId,
                    cancellationToken);

                await _storageSecurity.LogStorageActionAsync(
                    targetContainer,
                    StorageAction.Store,
                    item.Id,
                    options.AccessorId,
                    cancellationToken);

                // Container'ları kaydet;
                sourceContainer.LastModified = DateTime.UtcNow;
                targetContainer.LastModified = DateTime.UtcNow;

                await SaveContainerAsync(sourceContainer, cancellationToken);
                await SaveContainerAsync(targetContainer, cancellationToken);

                var result = new TransferResult;
                {
                    Success = true,
                    SourceContainerId = sourceContainerId,
                    TargetContainerId = targetContainerId,
                    ItemId = item.Id,
                    OriginalInstanceId = itemInstanceId,
                    NewInstanceId = wasStacked ? targetSlot.Item?.InstanceId : targetSlot.Item?.InstanceId,
                    SourceSlotIndex = sourceSlotIndex,
                    TargetSlotIndex = targetSlotIndex,
                    QuantityTransferred = transferQuantity,
                    WasStacked = wasStacked,
                    TransferTime = DateTime.UtcNow,
                    TransferMetadata = new Dictionary<string, object>
                    {
                        ["transfer_type"] = wasStacked ? "stacked" : "moved",
                        ["item_quality"] = item.Quality,
                        ["cross_container"] = sourceContainerId != targetContainerId;
                    }
                };

                _logger.LogInformation($"Item transferred: {item.Name} ({item.Id}) from {sourceContainerId} to {targetContainerId}, quantity: {transferQuantity}");

                // Event tetikle;
                StorageModified?.Invoke(this, new StorageModifiedEventArgs;
                {
                    SourceContainerId = sourceContainerId,
                    TargetContainerId = targetContainerId,
                    Item = item,
                    SourceSlotIndex = sourceSlotIndex,
                    TargetSlotIndex = targetSlotIndex,
                    Quantity = transferQuantity,
                    ActionType = StorageAction.Transfer,
                    Result = result,
                    AccessorId = options.AccessorId,
                    Timestamp = DateTime.UtcNow;
                });

                // Event bus'a publish et;
                await _eventBus.PublishAsync(new ItemTransferredEvent;
                {
                    SourceContainerId = sourceContainerId,
                    TargetContainerId = targetContainerId,
                    ItemId = item.Id,
                    OriginalInstanceId = itemInstanceId,
                    NewInstanceId = result.NewInstanceId,
                    Quantity = transferQuantity,
                    WasStacked = wasStacked,
                    TransferredAt = DateTime.UtcNow,
                    AccessorId = options.AccessorId;
                }, cancellationToken);

                // Metrics kaydı;
                _metricsCollector.RecordMetric("item_transferred", transferQuantity, new Dictionary<string, string>
                {
                    ["source_container"] = sourceContainerId,
                    ["target_container"] = targetContainerId,
                    ["item_type"] = item.ItemType,
                    ["transfer_type"] = wasStacked ? "stacked" : "moved"
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to transfer item {itemInstanceId} from {sourceContainerId} to {targetContainerId}");
                throw new TransferException($"Failed to transfer item: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Depolama konteynerini optimize eder;
        /// </summary>
        public async Task<OptimizationResult> OptimizeContainerAsync(
            string containerId,
            OptimizationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= OptimizationOptions.Default;

                // Container'ı bul;
                if (!_containers.TryGetValue(containerId, out var container))
                {
                    throw new ContainerNotFoundException($"Container {containerId} not found");
                }

                _logger.LogInformation($"Optimizing container: {containerId}");

                // Optimizasyon öncesi snapshot al;
                var beforeSnapshot = CreateContainerSnapshot(container);

                // Optimizasyon stratejisini belirle;
                var strategy = DetermineOptimizationStrategy(container, options);

                // Optimizasyonu uygula;
                var optimizationResult = await _storageOptimizer.OptimizeAsync(
                    container,
                    strategy,
                    cancellationToken);

                if (optimizationResult.Success)
                {
                    // Optimize edilmiş container'ı uygula;
                    ApplyOptimizationResult(container, optimizationResult);

                    // Container'ı kaydet;
                    container.LastModified = DateTime.UtcNow;
                    container.LastOptimized = DateTime.UtcNow;
                    await SaveContainerAsync(container, cancellationToken);

                    // Optimizasyon sonrası snapshot al;
                    var afterSnapshot = CreateContainerSnapshot(container);

                    var result = new OptimizationResult;
                    {
                        Success = true,
                        ContainerId = containerId,
                        Strategy = strategy,
                        OptimizationDetails = optimizationResult,
                        BeforeSnapshot = beforeSnapshot,
                        AfterSnapshot = afterSnapshot,
                        SpaceRecovered = optimizationResult.SpaceRecovered,
                        ItemsReorganized = optimizationResult.ItemsMoved,
                        OptimizationTime = DateTime.UtcNow,
                        OptimizationMetadata = new Dictionary<string, object>
                        {
                            ["strategy"] = strategy.StrategyType.ToString(),
                            ["items_moved"] = optimizationResult.ItemsMoved,
                            ["stacks_created"] = optimizationResult.StacksCreated,
                            ["fragmentation_reduced"] = optimizationResult.FragmentationReduction;
                        }
                    };

                    _logger.LogInformation($"Container optimized: {containerId}, space recovered: {result.SpaceRecovered}, items reorganized: {result.ItemsReorganized}");

                    // Event tetikle;
                    StorageOptimized?.Invoke(this, new StorageOptimizedEventArgs;
                    {
                        ContainerId = containerId,
                        Container = container,
                        Result = result,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Event bus'a publish et;
                    await _eventBus.PublishAsync(new StorageOptimizedEvent;
                    {
                        ContainerId = containerId,
                        Strategy = strategy.StrategyType.ToString(),
                        SpaceRecovered = result.SpaceRecovered,
                        ItemsReorganized = result.ItemsReorganized,
                        OptimizedAt = DateTime.UtcNow;
                    }, cancellationToken);

                    // Metrics kaydı;
                    _metricsCollector.RecordMetric("container_optimized", 1, new Dictionary<string, string>
                    {
                        ["container_id"] = containerId,
                        ["strategy"] = strategy.StrategyType.ToString(),
                        ["space_recovered"] = result.SpaceRecovered.ToString()
                    });

                    return result;
                }
                else;
                {
                    return new OptimizationResult;
                    {
                        Success = false,
                        ContainerId = containerId,
                        ErrorMessage = optimizationResult.ErrorMessage,
                        OptimizationTime = DateTime.UtcNow;
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to optimize container {containerId}");
                throw new OptimizationException($"Failed to optimize container: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Depolama konteyneri bilgilerini getirir;
        /// </summary>
        public async Task<ContainerInfo> GetContainerInfoAsync(
            string containerId,
            InfoOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= InfoOptions.Default;

                // Container'ı bul;
                if (!_containers.TryGetValue(containerId, out var container))
                {
                    throw new ContainerNotFoundException($"Container {containerId} not found");
                }

                // Access kontrolü;
                if (!await CheckAccessAsync(container, options.AccessorId, StorageAction.View, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for viewing container {containerId}");
                }

                // Container bilgilerini topla;
                var info = new ContainerInfo;
                {
                    ContainerId = containerId,
                    Container = container,
                    Statistics = CalculateContainerStatistics(container),
                    ItemSummary = await GenerateItemSummaryAsync(container, cancellationToken),
                    CapacityInfo = CalculateCapacityInfo(container),
                    SecurityInfo = await _storageSecurity.GetSecurityInfoAsync(container, cancellationToken),
                    GeneratedAt = DateTime.UtcNow;
                };

                // Detaylı bilgiler;
                if (options.IncludeDetailedInfo)
                {
                    info.DetailedSlots = GetDetailedSlotInfo(container, options);
                    info.CategorizedItems = CategorizeItems(container);
                    info.OptimizationSuggestions = await GetOptimizationSuggestionsAsync(container, cancellationToken);
                }

                return info;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get container info for {containerId}");
                throw new InfoRetrievalException($"Failed to get container info: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Depolama konteyneri arar;
        /// </summary>
        public async Task<SearchResult> SearchContainerAsync(
            string containerId,
            SearchCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                // Container'ı bul;
                if (!_containers.TryGetValue(containerId, out var container))
                {
                    throw new ContainerNotFoundException($"Container {containerId} not found");
                }

                // Access kontrolü;
                if (!await CheckAccessAsync(container, criteria.AccessorId, StorageAction.Search, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for searching container {containerId}");
                }

                _logger.LogDebug($"Searching container {containerId} with criteria: {criteria}");

                // Arama kriterlerine göre item'ları filtrele;
                var searchResults = new List<SearchResultItem>();
                var totalMatches = 0;

                for (int i = 0; i < container.Slots.Length; i++)
                {
                    var slot = container.Slots[i];
                    if (!slot.IsOccupied || slot.Item == null)
                        continue;

                    var item = slot.Item;

                    // Item'ı arama kriterlerine göre kontrol et;
                    if (MatchesSearchCriteria(item, slot, criteria))
                    {
                        totalMatches++;

                        // Paging kontrolü;
                        if (criteria.Skip.HasValue && totalMatches <= criteria.Skip.Value)
                            continue;

                        if (criteria.Take.HasValue && searchResults.Count >= criteria.Take.Value)
                            break;

                        searchResults.Add(new SearchResultItem;
                        {
                            SlotIndex = i,
                            Item = item,
                            InstanceId = item.InstanceId,
                            Quantity = slot.Quantity,
                            SlotMetadata = new Dictionary<string, object>
                            {
                                ["slot_type"] = slot.SlotType.ToString(),
                                ["is_locked"] = slot.IsLocked,
                                ["is_reserved"] = slot.IsReserved,
                                ["last_modified"] = slot.LastModified;
                            }
                        });
                    }
                }

                var result = new SearchResult;
                {
                    Success = true,
                    ContainerId = containerId,
                    SearchCriteria = criteria,
                    TotalMatches = totalMatches,
                    ReturnedMatches = searchResults.Count,
                    Results = searchResults,
                    SearchTime = DateTime.UtcNow,
                    SearchMetadata = new Dictionary<string, object>
                    {
                        ["search_type"] = criteria.SearchType.ToString(),
                        ["has_filters"] = criteria.HasFilters,
                        ["execution_time_ms"] = 0 // Buraya performans metrikleri eklenebilir;
                    }
                };

                _logger.LogDebug($"Search completed for container {containerId}: {totalMatches} matches found");

                // Metrics kaydı;
                _metricsCollector.RecordMetric("container_searched", 1, new Dictionary<string, string>
                {
                    ["container_id"] = containerId,
                    ["search_type"] = criteria.SearchType.ToString(),
                    ["matches_found"] = totalMatches.ToString()
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to search container {containerId}");
                throw new SearchException($"Failed to search container: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Item'ları kategorilere göre organize eder;
        /// </summary>
        public async Task<OrganizationResult> OrganizeItemsAsync(
            string containerId,
            OrganizationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= OrganizationOptions.Default;

                // Container'ı bul;
                if (!_containers.TryGetValue(containerId, out var container))
                {
                    throw new ContainerNotFoundException($"Container {containerId} not found");
                }

                // Access kontrolü;
                if (!await CheckAccessAsync(container, options.AccessorId, StorageAction.Organize, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for organizing container {containerId}");
                }

                _logger.LogInformation($"Organizing container: {containerId} with strategy: {options.OrganizationStrategy}");

                // Organizasyon öncesi snapshot al;
                var beforeSnapshot = CreateContainerSnapshot(container);

                // Organizasyon stratejisini uygula;
                var organizationResult = await _itemOrganizer.OrganizeAsync(
                    container,
                    options,
                    cancellationToken);

                if (organizationResult.Success)
                {
                    // Organize edilmiş container'ı uygula;
                    container.Slots = organizationResult.OrganizedSlots;
                    container.LastModified = DateTime.UtcNow;
                    container.LastOrganized = DateTime.UtcNow;

                    // Container'ı kaydet;
                    await SaveContainerAsync(container, cancellationToken);

                    // Organizasyon sonrası snapshot al;
                    var afterSnapshot = CreateContainerSnapshot(container);

                    var result = new OrganizationResult;
                    {
                        Success = true,
                        ContainerId = containerId,
                        OrganizationStrategy = options.OrganizationStrategy,
                        OriginalSnapshot = beforeSnapshot,
                        OrganizedSnapshot = afterSnapshot,
                        ItemsMoved = organizationResult.ItemsMoved,
                        CategoriesApplied = organizationResult.CategoriesApplied,
                        OrganizationTime = DateTime.UtcNow,
                        OrganizationMetadata = new Dictionary<string, object>
                        {
                            ["strategy"] = options.OrganizationStrategy.ToString(),
                            ["custom_rules"] = options.CustomRules?.Count ?? 0,
                            ["sort_order"] = options.SortOrder.ToString(),
                            ["group_by_category"] = options.GroupByCategory;
                        }
                    };

                    _logger.LogInformation($"Container organized: {containerId}, items moved: {result.ItemsMoved}");

                    // Event tetikle;
                    StorageModified?.Invoke(this, new StorageModifiedEventArgs;
                    {
                        ContainerId = containerId,
                        Item = null,
                        ActionType = StorageAction.Organize,
                        Result = result,
                        AccessorId = options.AccessorId,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Event bus'a publish et;
                    await _eventBus.PublishAsync(new ItemsOrganizedEvent;
                    {
                        ContainerId = containerId,
                        Strategy = options.OrganizationStrategy.ToString(),
                        ItemsMoved = result.ItemsMoved,
                        OrganizedAt = DateTime.UtcNow,
                        AccessorId = options.AccessorId;
                    }, cancellationToken);

                    // Metrics kaydı;
                    _metricsCollector.RecordMetric("container_organized", 1, new Dictionary<string, string>
                    {
                        ["container_id"] = containerId,
                        ["strategy"] = options.OrganizationStrategy.ToString(),
                        ["items_moved"] = result.ItemsMoved.ToString()
                    });

                    return result;
                }
                else;
                {
                    return new OrganizationResult;
                    {
                        Success = false,
                        ContainerId = containerId,
                        ErrorMessage = organizationResult.ErrorMessage,
                        OrganizationTime = DateTime.UtcNow;
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to organize container {containerId}");
                throw new OrganizationException($"Failed to organize container: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Depolama konteynerini siler;
        /// </summary>
        public async Task<DeletionResult> DeleteContainerAsync(
            string containerId,
            DeletionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(containerId))
                throw new ArgumentException("Container ID cannot be null or empty", nameof(containerId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= DeletionOptions.Default;

                // Container'ı bul;
                if (!_containers.TryGetValue(containerId, out var container))
                {
                    throw new ContainerNotFoundException($"Container {containerId} not found");
                }

                // Access kontrolü;
                if (!await CheckAccessAsync(container, options.AccessorId, StorageAction.Delete, cancellationToken))
                {
                    throw new AccessDeniedException($"Access denied for deleting container {containerId}");
                }

                // Silme onayı için item kontrolü;
                if (!options.ForceDelete && container.OccupiedSlots > 0)
                {
                    throw new ContainerNotEmptyException($"Container {containerId} is not empty. Use ForceDelete to delete anyway.");
                }

                _logger.LogInformation($"Deleting container: {containerId}");

                // Item'ları yedekle (eğer yapılandırılmışsa)
                if (options.BackupItems)
                {
                    await BackupContainerItemsAsync(container, options, cancellationToken);
                }

                // Container'ı kaldır;
                var deletionResult = await RemoveContainerInternalAsync(containerId, cancellationToken);

                // Storage grid'i de sil (eğer varsa)
                if (_storageGrids.ContainsKey(containerId))
                {
                    _storageGrids.Remove(containerId);
                }

                // Storage profile'ı sil (eğer varsa)
                if (_storageProfiles.ContainsKey(containerId))
                {
                    _storageProfiles.Remove(containerId);
                }

                // Container dosyasını sil;
                var containerFilePath = GetContainerFilePath(containerId);
                if (File.Exists(containerFilePath))
                {
                    File.Delete(containerFilePath);
                }

                var result = new DeletionResult;
                {
                    Success = true,
                    ContainerId = containerId,
                    ContainerName = container.Name,
                    ItemsBackedUp = options.BackupItems,
                    ItemsDeleted = container.OccupiedSlots,
                    DeletionTime = DateTime.UtcNow,
                    DeletionMetadata = new Dictionary<string, object>
                    {
                        ["owner_id"] = container.OwnerId,
                        ["container_type"] = container.ContainerType.ToString(),
                        ["force_delete"] = options.ForceDelete,
                        ["backup_path"] = options.BackupItems ? GetBackupPath(containerId) : null;
                    }
                };

                _logger.LogInformation($"Container deleted: {container.Name} ({containerId})");

                // Security event tetikle;
                StorageSecurityEvent?.Invoke(this, new StorageSecurityEventEventArgs;
                {
                    ContainerId = containerId,
                    EventType = SecurityEventType.ContainerDeleted,
                    Details = result,
                    Timestamp = DateTime.UtcNow;
                });

                // Event bus'a publish et;
                await _eventBus.PublishAsync(new ContainerDeletedEvent;
                {
                    ContainerId = containerId,
                    OwnerId = container.OwnerId,
                    ItemsDeleted = container.OccupiedSlots,
                    DeletedAt = DateTime.UtcNow,
                    DeletedBy = options.AccessorId;
                }, cancellationToken);

                // Metrics kaydı;
                _metricsCollector.RecordMetric("container_deleted", 1, new Dictionary<string, string>
                {
                    ["container_type"] = container.ContainerType.ToString(),
                    ["items_backed_up"] = options.BackupItems.ToString(),
                    ["force_delete"] = options.ForceDelete.ToString()
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete container {containerId}");
                throw new DeletionException($"Failed to delete container: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Backup alır;
        /// </summary>
        public async Task<BackupResult> BackupStorageAsync(
            BackupOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                options ??= BackupOptions.Default;

                _logger.LogInformation("Starting storage backup...");

                var backupTimestamp = DateTime.UtcNow;
                var backupId = $"backup_{backupTimestamp:yyyyMMdd_HHmmss}";
                var backupPath = Path.Combine(
                    _appConfig.GetValue<string>("Backup:StoragePath") ?? "Backups/Storage",
                    backupId);

                // Backup dizinini oluştur;
                Directory.CreateDirectory(backupPath);

                var backupResult = new BackupResult;
                {
                    BackupId = backupId,
                    BackupPath = backupPath,
                    StartTime = backupTimestamp,
                    ContainerCount = _containers.Count,
                    Options = options;
                };

                var backedUpContainers = new List<string>();
                var failedBackups = new List<string>();

                // Container'ları yedekle;
                foreach (var containerPair in _containers)
                {
                    try
                    {
                        var containerId = containerPair.Key;
                        var container = containerPair.Value;

                        // Container'ı JSON olarak kaydet;
                        var containerBackupPath = Path.Combine(backupPath, $"{containerId}.json");
                        var containerJson = JsonSerializer.Serialize(container, _jsonOptions);
                        await File.WriteAllTextAsync(containerBackupPath, containerJson, cancellationToken);

                        backedUpContainers.Add(containerId);

                        _logger.LogDebug($"Backed up container: {containerId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to backup container: {containerPair.Key}");
                        failedBackups.Add(containerPair.Key);
                    }
                }

                // Metadata dosyası oluştur;
                var metadata = new;
                {
                    BackupId = backupId,
                    BackupTime = backupTimestamp,
                    TotalContainers = _containers.Count,
                    SuccessfullyBackedUp = backedUpContainers.Count,
                    FailedBackups = failedBackups.Count,
                    Options = options,
                    SystemInfo = new;
                    {
                        AppVersion = _appConfig.GetValue<string>("Application:Version") ?? "1.0.0",
                        BackupVersion = "1.0"
                    }
                };

                var metadataPath = Path.Combine(backupPath, "metadata.json");
                var metadataJson = JsonSerializer.Serialize(metadata, _jsonOptions);
                await File.WriteAllTextAsync(metadataPath, metadataJson, cancellationToken);

                backupResult.EndTime = DateTime.UtcNow;
                backupResult.Success = failedBackups.Count == 0;
                backupResult.BackedUpContainers = backedUpContainers;
                backupResult.FailedContainers = failedBackups;
                backupResult.TotalSize = await CalculateBackupSizeAsync(backupPath, cancellationToken);

                _logger.LogInformation($"Storage backup completed: {backupId}, containers: {backedUpContainers.Count}");

                // Event bus'a publish et;
                await _eventBus.PublishAsync(new StorageBackupCompletedEvent;
                {
                    BackupId = backupId,
                    ContainerCount = backedUpContainers.Count,
                    BackupSize = backupResult.TotalSize,
                    CompletedAt = DateTime.UtcNow,
                    Success = backupResult.Success;
                }, cancellationToken);

                // Metrics kaydı;
                _metricsCollector.RecordMetric("storage_backup_completed", 1, new Dictionary<string, string>
                {
                    ["backup_id"] = backupId,
                    ["container_count"] = backedUpContainers.Count.ToString(),
                    ["backup_size_mb"] = (backupResult.TotalSize / (1024 * 1024)).ToString("F2")
                });

                return backupResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backup storage");
                throw new BackupException($"Failed to backup storage: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <summary>
        /// Storage durumunu kontrol eder;
        /// </summary>
        public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var healthResult = new HealthCheckResult;
                {
                    ServiceName = nameof(StorageManager),
                    CheckTime = DateTime.UtcNow,
                    Checks = new Dictionary<string, HealthCheckDetail>()
                };

                // Container sayısı kontrolü;
                healthResult.Checks["container_count"] = new HealthCheckDetail;
                {
                    Status = HealthStatus.Healthy,
                    Message = $"Total containers: {_containers.Count}",
                    Duration = TimeSpan.Zero;
                };

                // Database bağlantısı kontrolü;
                var dbCheckStart = DateTime.UtcNow;
                var dbExists = await _fileManager.FileExistsAsync(_storageDatabasePath, cancellationToken);
                healthResult.Checks["database_connection"] = new HealthCheckDetail;
                {
                    Status = dbExists ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                    Message = dbExists ? "Storage database accessible" : "Storage database not found",
                    Duration = DateTime.UtcNow - dbCheckStart;
                };

                // Storage kapasitesi kontrolü;
                var capacityCheckStart = DateTime.UtcNow;
                var totalSlots = _containers.Values.Sum(c => c.MaxSlots);
                var usedSlots = _containers.Values.Sum(c => c.OccupiedSlots);
                var usagePercentage = totalSlots > 0 ? (usedSlots * 100.0 / totalSlots) : 0;

                healthResult.Checks["storage_capacity"] = new HealthCheckDetail;
                {
                    Status = usagePercentage < 90 ? HealthStatus.Healthy :
                            usagePercentage < 95 ? HealthStatus.Degraded : HealthStatus.Unhealthy,
                    Message = $"Storage usage: {usagePercentage:F1}% ({usedSlots}/{totalSlots} slots)",
                    Duration = DateTime.UtcNow - capacityCheckStart;
                };

                // Optimizer sağlık kontrolü;
                var optimizerCheckStart = DateTime.UtcNow;
                try
                {
                    var optimizerHealth = await _storageOptimizer.CheckHealthAsync(cancellationToken);
                    healthResult.Checks["optimizer"] = new HealthCheckDetail;
                    {
                        Status = optimizerHealth.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                        Message = optimizerHealth.Message,
                        Duration = DateTime.UtcNow - optimizerCheckStart;
                    };
                }
                catch (Exception ex)
                {
                    healthResult.Checks["optimizer"] = new HealthCheckDetail;
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = $"Optimizer health check failed: {ex.Message}",
                        Duration = DateTime.UtcNow - optimizerCheckStart;
                    };
                }

                // Genel durum belirleme;
                var unhealthyChecks = healthResult.Checks.Values.Count(c => c.Status == HealthStatus.Unhealthy);
                var degradedChecks = healthResult.Checks.Values.Count(c => c.Status == HealthStatus.Degraded);

                if (unhealthyChecks > 0)
                {
                    healthResult.OverallStatus = HealthStatus.Unhealthy;
                    healthResult.Message = $"{unhealthyChecks} unhealthy component(s) found";
                }
                else if (degradedChecks > 0)
                {
                    healthResult.OverallStatus = HealthStatus.Degraded;
                    healthResult.Message = $"{degradedChecks} degraded component(s) found";
                }
                else;
                {
                    healthResult.OverallStatus = HealthStatus.Healthy;
                    healthResult.Message = "All components healthy";
                }

                return healthResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                return new HealthCheckResult;
                {
                    ServiceName = nameof(StorageManager),
                    OverallStatus = HealthStatus.Unhealthy,
                    Message = $"Health check failed: {ex.Message}",
                    CheckTime = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Storage istatistiklerini getirir;
        /// </summary>
        public async Task<StorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                await _storageLock.WaitAsync(cancellationToken);

                var stats = new StorageStatistics;
                {
                    GeneratedAt = DateTime.UtcNow,
                    ContainerStatistics = new ContainerStatistics;
                    {
                        TotalContainers = _containers.Count,
                        ContainerTypes = _containers.Values;
                            .GroupBy(c => c.ContainerType)
                            .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                        AverageSlotsPerContainer = _containers.Count > 0 ?
                            _containers.Values.Average(c => c.MaxSlots) : 0,
                        AverageUsagePercentage = _containers.Count > 0 ?
                            _containers.Values.Average(c => c.MaxSlots > 0 ?
                                (c.OccupiedSlots * 100.0 / c.MaxSlots) : 0) : 0;
                    },
                    ItemStatistics = new ItemStatistics;
                    {
                        TotalItems = _containers.Values.Sum(c => c.OccupiedSlots),
                        ItemTypes = CalculateItemTypeDistribution(),
                        RarityDistribution = CalculateRarityDistribution(),
                        AverageQuantityPerSlot = CalculateAverageQuantity(),
                        StackedItemsCount = CalculateStackedItemsCount()
                    },
                    PerformanceStatistics = new PerformanceStatistics;
                    {
                        LastOptimizationTime = _containers.Values.Max(c => c.LastOptimized ?? DateTime.MinValue),
                        LastOrganizationTime = _containers.Values.Max(c => c.LastOrganized ?? DateTime.MinValue),
                        AverageStorageTime = CalculateAverageStorageTime(),
                        AccessFrequency = CalculateAccessFrequency()
                    },
                    CapacityStatistics = new CapacityStatistics;
                    {
                        TotalSlots = _containers.Values.Sum(c => c.MaxSlots),
                        UsedSlots = _containers.Values.Sum(c => c.OccupiedSlots),
                        AvailableSlots = _containers.Values.Sum(c => c.MaxSlots) - _containers.Values.Sum(c => c.OccupiedSlots),
                        TotalWeight = _containers.Values.Sum(c => c.CurrentWeight),
                        TotalVolume = _containers.Values.Sum(c => c.CurrentVolume),
                        WeightUsagePercentage = CalculateWeightUsagePercentage(),
                        VolumeUsagePercentage = CalculateVolumeUsagePercentage()
                    }
                };

                // Gelişmiş analizler;
                stats.Analysis = new StorageAnalysis;
                {
                    FragmentationLevel = CalculateFragmentationLevel(),
                    OptimizationPotential = await CalculateOptimizationPotentialAsync(cancellationToken),
                    StorageHotspots = IdentifyStorageHotspots(),
                    Recommendations = await GenerateRecommendationsAsync(cancellationToken)
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get storage statistics");
                throw new StatisticsException($"Failed to get storage statistics: {ex.Message}", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        #region Yardımcı Metodlar;

        private async Task EnsureDirectoriesExistAsync(CancellationToken cancellationToken)
        {
            try
            {
                var directories = new[]
                {
                    Path.GetDirectoryName(_storageDatabasePath),
                    _storageTemplatesPath,
                    Path.GetDirectoryName(_itemDatabasePath),
                    "Backups/Storage",
                    "Logs/Storage"
                };

                foreach (var directory in directories)
                {
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        _logger.LogDebug($"Created directory: {directory}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create directories");
                throw new StorageManagerException("Failed to create required directories", ex);
            }
        }

        private async Task LoadStorageDatabaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(_storageDatabasePath))
                {
                    _logger.LogInformation("Storage database not found, creating new database");
                    await _fileManager.CreateFileAsync(_storageDatabasePath, "{}", cancellationToken);
                    return;
                }

                var databaseJson = await _fileManager.ReadAllTextAsync(_storageDatabasePath, cancellationToken);
                var database = JsonSerializer.Deserialize<StorageDatabase>(databaseJson, _jsonOptions);

                if (database?.Containers != null)
                {
                    foreach (var container in database.Containers)
                    {
                        _containers[container.Id] = container;
                    }

                    _logger.LogInformation($"Loaded {database.Containers.Count} containers from database");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load storage database");
                throw new StorageManagerException("Failed to load storage database", ex);
            }
        }

        private async Task LoadStorageTemplatesAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(_storageTemplatesPath))
                {
                    _logger.LogInformation("Storage templates directory not found");
                    return;
                }

                var templateFiles = Directory.GetFiles(_storageTemplatesPath, "*.json");
                foreach (var templateFile in templateFiles)
                {
                    try
                    {
                        var templateJson = await File.ReadAllTextAsync(templateFile, cancellationToken);
                        var template = JsonSerializer.Deserialize<StorageTemplate>(templateJson, _jsonOptions);

                        if (template != null)
                        {
                            // Template'leri özel bir dictionary'de saklayabiliriz;
                            // Bu örnekte sadece logluyoruz;
                            _logger.LogDebug($"Loaded template: {template.TemplateId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to load template: {templateFile}");
                    }
                }

                _logger.LogInformation($"Loaded {templateFiles.Length} storage templates");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load storage templates");
                // Template yükleme hatası kritik değil, uygulama çalışmaya devam edebilir;
            }
        }

        private async Task LoadItemDatabaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(_itemDatabasePath))
                {
                    _logger.LogWarning("Item database not found");
                    return;
                }

                var itemDatabaseJson = await File.ReadAllTextAsync(_itemDatabasePath, cancellationToken);
                var itemDatabase = JsonSerializer.Deserialize<ItemDatabase>(itemDatabaseJson, _jsonOptions);

                if (itemDatabase?.Items != null)
                {
                    _logger.LogInformation($"Loaded {itemDatabase.Items.Count} items from database");
                    // Item database'ini bir servis aracılığıyla yönetmek daha iyi olabilir;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load item database");
                // Item database yükleme hatası kritik değil, uygulama çalışmaya devam edebilir;
            }
        }

        private async Task SubscribeToEventsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Item-related events;
                await _eventBus.SubscribeAsync<ItemCreatedEvent>(async (eventData, token) =>
                {
                    _logger.LogDebug($"Item created event received: {eventData.ItemId}");
                    // Item oluşturulduğunda yapılacak işlemler;
                }, cancellationToken);

                await _eventBus.SubscribeAsync<ItemDeletedEvent>(async (eventData, token) =>
                {
                    _logger.LogDebug($"Item deleted event received: {eventData.ItemId}");
                    // Item silindiğinde depolamadan da kaldır;
                    await RemoveItemFromAllContainersAsync(eventData.ItemId, token);
                }, cancellationToken);

                // Player-related events;
                await _eventBus.SubscribeAsync<PlayerJoinedEvent>(async (eventData, token) =>
                {
                    _logger.LogDebug($"Player joined event received: {eventData.PlayerId}");
                    // Oyuncu katıldığında depolama alanlarını hazırla;
                    await InitializePlayerStorageAsync(eventData.PlayerId, token);
                }, cancellationToken);

                await _eventBus.SubscribeAsync<PlayerLeftEvent>(async (eventData, token) =>
                {
                    _logger.LogDebug($"Player left event received: {eventData.PlayerId}");
                    // Oyuncu ayrıldığında depolama alanlarını temizle/yedekle;
                    await CleanupPlayerStorageAsync(eventData.PlayerId, token);
                }, cancellationToken);

                _logger.LogInformation("Event subscriptions completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to events");
                throw new StorageManagerException("Failed to subscribe to events", ex);
            }
        }

        private async Task<StorageContainer> CreateContainerFromTemplateAsync(
            string containerId,
            string templateId,
            CreateStorageOptions options,
            CancellationToken cancellationToken)
        {
            // Template dosyasını oku;
            var templatePath = Path.Combine(_storageTemplatesPath, $"{templateId}.json");
            if (!File.Exists(templatePath))
            {
                throw new TemplateNotFoundException($"Template {templateId} not found");
            }

            var templateJson = await File.ReadAllTextAsync(templatePath, cancellationToken);
            var template = JsonSerializer.Deserialize<StorageTemplate>(templateJson, _jsonOptions);

            // Template'den container oluştur;
            var container = new StorageContainer;
            {
                Id = containerId,
                Name = options.Name ?? template.DefaultName,
                OwnerId = options.OwnerId,
                ContainerType = template.ContainerType,
                MaxSlots = options.MaxSlots ?? template.DefaultSlots,
                MaxWeight = options.MaxWeight ?? template.DefaultWeight,
                MaxVolume = options.MaxVolume ?? template.DefaultVolume,
                Slots = new StorageSlot[options.MaxSlots ?? template.DefaultSlots],
                CreatedAt = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                AccessPermissions = options.AccessPermissions ?? template.DefaultPermissions,
                SecuritySettings = options.SecuritySettings ?? template.DefaultSecurity,
                Metadata = MergeMetadata(template.DefaultMetadata, options.Metadata)
            };

            // Slot'ları template'e göre başlat;
            for (int i = 0; i < container.Slots.Length; i++)
            {
                container.Slots[i] = new StorageSlot;
                {
                    SlotIndex = i,
                    SlotType = GetSlotTypeFromTemplate(template, i),
                    IsLocked = template.LockedSlots?.Contains(i) ?? false,
                    IsReserved = false,
                    Restrictions = template.SlotRestrictions?.GetValueOrDefault(i)
                };
            }

            return container;
        }

        private async Task CreateStorageGridAsync(
            string containerId,
            GridOptions gridOptions,
            CancellationToken cancellationToken)
        {
            if (gridOptions == null)
                return;

            var grid = new StorageGrid;
            {
                ContainerId = containerId,
                Width = gridOptions.Width,
                Height = gridOptions.Height,
                CellSize = gridOptions.CellSize,
                Cells = new StorageGridCell[gridOptions.Width * gridOptions.Height]
            };

            // Grid hücrelerini başlat;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    var index = y * grid.Width + x;
                    grid.Cells[index] = new StorageGridCell;
                    {
                        X = x,
                        Y = y,
                        IsOccupied = false,
                        ItemInstanceId = null,
                        SlotIndex = -1;
                    };
                }
            }

            _storageGrids[containerId] = grid;
            _logger.LogDebug($"Created storage grid for container {containerId}: {grid.Width}x{grid.Height}");
        }

        private void ValidateContainer(StorageContainer container)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container));

            if (string.IsNullOrEmpty(container.Id))
                throw new ValidationException("Container ID cannot be null or empty");

            if (container.MaxSlots <= 0)
                throw new ValidationException("Container must have at least 1 slot");

            if (container.MaxSlots > MAX_STORAGE_SLOTS)
                throw new ValidationException($"Container cannot have more than {MAX_STORAGE_SLOTS} slots");

            if (container.Slots == null || container.Slots.Length != container.MaxSlots)
                throw new ValidationException("Container slots array must match MaxSlots");

            // Slot index'lerini kontrol et;
            for (int i = 0; i < container.Slots.Length; i++)
            {
                if (container.Slots[i].SlotIndex != i)
                {
                    throw new ValidationException($"Slot index mismatch at position {i}");
                }
            }
        }

        private void ValidateItemForStorage(GameItem item, StorageContainer container, StoreItemOptions options)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            // Item boyutu kontrolü;
            if (item.Width * item.Height > 1 && !container.SupportsMultiSlotItems)
            {
                throw new ItemSizeException($"Container does not support multi-slot items");
            }

            // Item tipi kısıtlamaları;
            if (container.RestrictedItemTypes?.Contains(item.ItemType) == true)
            {
                throw new ItemRestrictionException($"Item type {item.ItemType} is restricted in this container");
            }

            // Minimum/Maximum quality kontrolü;
            if (container.MinQuality > 0 && item.Quality < container.MinQuality)
            {
                throw new QualityRestrictionException($"Item quality {item.Quality} is below minimum {container.MinQuality}");
            }

            if (container.MaxQuality > 0 && item.Quality > container.MaxQuality)
            {
                throw new QualityRestrictionException($"Item quality {item.Quality} is above maximum {container.MaxQuality}");
            }

            // Stack size kontrolü;
            if (item.IsStackable && options.Quantity.HasValue)
            {
                if (options.Quantity.Value > MAX_STACK_SIZE)
                {
                    throw new StackSizeException($"Stack size cannot exceed {MAX_STACK_SIZE}");
                }

                if (options.Quantity.Value <= 0)
                {
                    throw new StackSizeException("Stack size must be positive");
                }
            }
        }

        private async Task<bool> CheckAccessAsync(
            StorageContainer container,
            string accessorId,
            StorageAction action,
            CancellationToken cancellationToken)
        {
            if (container == null)
                return false;

            // Owner her zaman erişebilir;
            if (container.OwnerId == accessorId)
                return true;

            // Sistem kontrolleri için;
            if (accessorId == "system" || accessorId == "admin")
                return true;

            // Access permissions kontrolü;
            return await _storageSecurity.CheckAccessAsync(
                container,
                accessorId,
                action,
                cancellationToken);
        }

        private async Task<StorageResult> TryStackItemAsync(
            StorageContainer container,
            GameItem item,
            StoreItemOptions options,
            CancellationToken cancellationToken)
        {
            // Aynı item'ın olduğu slot'ları bul;
            for (int i = 0; i < container.Slots.Length; i++)
            {
                var slot = container.Slots[i];
                if (!slot.IsOccupied || slot.Item == null)
                    continue;

                if (slot.Item.Id == item.Id && slot.Quantity < MAX_STACK_SIZE)
                {
                    // Stack yapılabilir;
                    int availableSpace = MAX_STACK_SIZE - slot.Quantity;
                    int stackAmount = Math.Min(options.Quantity ?? 1, availableSpace);

                    slot.Quantity += stackAmount;
                    slot.LastModified = DateTime.UtcNow;

                    // Container istatistiklerini güncelle;
                    UpdateContainerStats(container, item, stackAmount);

                    // Security logging;
                    await _storageSecurity.LogStorageActionAsync(
                        container,
                        StorageAction.Store,
                        item.Id,
                        options.AccessorId,
                        cancellationToken);

                    container.LastModified = DateTime.UtcNow;
                    await SaveContainerAsync(container, cancellationToken);

                    return new StorageResult;
                    {
                        Success = true,
                        ContainerId = container.Id,
                        ItemId = item.Id,
                        InstanceId = slot.Item.InstanceId,
                        SlotIndex = i,
                        QuantityStored = stackAmount,
                        RemainingQuantity = (options.Quantity ?? 1) - stackAmount,
                        StorageTime = DateTime.UtcNow,
                        StorageMetadata = new Dictionary<string, object>
                        {
                            ["stacked"] = true,
                            ["stack_size"] = slot.Quantity,
                            ["existing_stack"] = true;
                        }
                    };
                }
            }

            return new StorageResult;
            {
                Success = false,
                ErrorMessage = "No available stack found"
            };
        }

        private async Task<int> FindAvailableSlotAsync(
            StorageContainer container,
            GameItem item,
            StoreItemOptions options,
            CancellationToken cancellationToken)
        {
            // Öncelik sırasına göre slot arama;
            var searchStrategies = new List<Func<StorageContainer, GameItem, StoreItemOptions, int>>
            {
                FindFirstEmptySlot,
                FindReservedSlot,
                FindCompatibleSlot;
            };

            foreach (var strategy in searchStrategies)
            {
                var slotIndex = strategy(container, item, options);
                if (slotIndex != -1)
                {
                    return slotIndex;
                }
            }

            return -1;
        }

        private int FindFirstEmptySlot(StorageContainer container, GameItem item, StoreItemOptions options)
        {
            for (int i = 0; i < container.Slots.Length; i++)
            {
                var slot = container.Slots[i];
                if (!slot.IsOccupied && !slot.IsLocked && !slot.IsReserved)
                {
                    // Slot tipi kontrolü;
                    if (IsSlotCompatible(slot, item))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private bool IsSlotCompatible(StorageSlot slot, GameItem item)
        {
            // Slot tipi kısıtlamaları;
            if (slot.SlotType != SlotType.General && slot.SlotType.ToString() != item.ItemType)
            {
                return false;
            }

            // Slot kısıtlamaları;
            if (slot.Restrictions != null)
            {
                if (slot.Restrictions.MinQuality > 0 && item.Quality < slot.Restrictions.MinQuality)
                    return false;

                if (slot.Restrictions.MaxQuality > 0 && item.Quality > slot.Restrictions.MaxQuality)
                    return false;

                if (slot.Restrictions.AllowedItemTypes != null &&
                    !slot.Restrictions.AllowedItemTypes.Contains(item.ItemType))
                    return false;

                if (slot.Restrictions.BlockedItemTypes != null &&
                    slot.Restrictions.BlockedItemTypes.Contains(item.ItemType))
                    return false;
            }

            return true;
        }

        private bool PrepareSlotForItem(StorageSlot slot, GameItem item, StoreItemOptions options)
        {
            try
            {
                // Slot'u temizle (eğer gerekliyse)
                if (slot.IsOccupied && options.OverwriteOccupiedSlot)
                {
                    slot.Item = null;
                    slot.Quantity = 0;
                }

                // Slot ayarlarını yap;
                slot.SlotType = DetermineSlotType(item);
                slot.IsReserved = options.ReserveSlot;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateContainerStats(StorageContainer container, GameItem item, int quantity)
        {
            container.OccupiedSlots++;
            container.CurrentWeight += item.Weight * quantity;
            container.CurrentVolume += item.Volume * quantity;
            container.TotalItemsStored++;

            // Item type istatistiklerini güncelle;
            if (!container.ItemTypeCounts.ContainsKey(item.ItemType))
            {
                container.ItemTypeCounts[item.ItemType] = 0;
            }
            container.ItemTypeCounts[item.ItemType] += quantity;
        }

        private void UpdateContainerStatsAfterRetrieval(StorageContainer container, GameItem item, int quantity)
        {
            if (quantity > 0)
            {
                container.OccupiedSlots = Math.Max(0, container.OccupiedSlots - 1);
                container.CurrentWeight = Math.Max(0, container.CurrentWeight - (item.Weight * quantity));
                container.CurrentVolume = Math.Max(0, container.CurrentVolume - (item.Volume * quantity));

                // Item type istatistiklerini güncelle;
                if (container.ItemTypeCounts.ContainsKey(item.ItemType))
                {
                    container.ItemTypeCounts[item.ItemType] = Math.Max(0,
                        container.ItemTypeCounts[item.ItemType] - quantity);

                    if (container.ItemTypeCounts[item.ItemType] == 0)
                    {
                        container.ItemTypeCounts.Remove(item.ItemType);
                    }
                }
            }
        }

        private bool CheckWeightAndVolume(StorageContainer container, GameItem item, int quantity)
        {
            var newWeight = container.CurrentWeight + (item.Weight * quantity);
            var newVolume = container.CurrentVolume + (item.Volume * quantity);

            return (container.MaxWeight <= 0 || newWeight <= container.MaxWeight) &&
                   (container.MaxVolume <= 0 || newVolume <= container.MaxVolume);
        }

        private (int SlotIndex, StorageSlot Slot) FindItemSlot(StorageContainer container, string itemInstanceId)
        {
            for (int i = 0; i < container.Slots.Length; i++)
            {
                var slot = container.Slots[i];
                if (slot.IsOccupied && slot.Item != null && slot.Item.InstanceId == itemInstanceId)
                {
                    return (i, slot);
                }
            }

            return (-1, null);
        }

        private bool MatchesSearchCriteria(GameItem item, StorageSlot slot, SearchCriteria criteria)
        {
            if (item == null)
                return false;

            // Item ID kontrolü;
            if (!string.IsNullOrEmpty(criteria.ItemId) && item.Id != criteria.ItemId)
                return false;

            // Item tipi kontrolü;
            if (criteria.ItemTypes != null && criteria.ItemTypes.Count > 0 &&
                !criteria.ItemTypes.Contains(item.ItemType))
                return false;

            // İsim arama;
            if (!string.IsNullOrEmpty(criteria.NameContains) &&
                !item.Name.Contains(criteria.NameContains, StringComparison.OrdinalIgnoreCase))
                return false;

            // Rarity kontrolü;
            if (criteria.MinRarity.HasValue && item.Rarity < criteria.MinRarity.Value)
                return false;

            if (criteria.MaxRarity.HasValue && item.Rarity > criteria.MaxRarity.Value)
                return false;

            // Quality kontrolü;
            if (criteria.MinQuality.HasValue && item.Quality < criteria.MinQuality.Value)
                return false;

            if (criteria.MaxQuality.HasValue && item.Quality > criteria.MaxQuality.Value)
                return false;

            // Meta data arama;
            if (criteria.MetadataFilters != null)
            {
                foreach (var filter in criteria.MetadataFilters)
                {
                    if (!item.Metadata.ContainsKey(filter.Key) ||
                        !filter.Value.Equals(item.Metadata[filter.Key]))
                    {
                        return false;
                    }
                }
            }

            // Slot özellikleri kontrolü;
            if (criteria.SlotFilters != null)
            {
                if (criteria.SlotFilters.OnlyOccupied && !slot.IsOccupied)
                    return false;

                if (criteria.SlotFilters.OnlyEmpty && slot.IsOccupied)
                    return false;

                if (criteria.SlotFilters.OnlyLocked && !slot.IsLocked)
                    return false;

                if (criteria.SlotFilters.OnlyUnlocked && slot.IsLocked)
                    return false;
            }

            return true;
        }

        private ContainerSnapshot CreateContainerSnapshot(StorageContainer container)
        {
            return new ContainerSnapshot;
            {
                Timestamp = DateTime.UtcNow,
                OccupiedSlots = container.OccupiedSlots,
                CurrentWeight = container.CurrentWeight,
                CurrentVolume = container.CurrentVolume,
                ItemCountByType = new Dictionary<string, int>(container.ItemTypeCounts),
                SlotOccupancy = container.Slots.Select(s => s.IsOccupied).ToArray()
            };
        }

        private OptimizationStrategy DetermineOptimizationStrategy(StorageContainer container, OptimizationOptions options)
        {
            var strategy = new OptimizationStrategy;
            {
                StrategyType = options.Strategy,
                Priority = options.Priority,
                Parameters = new Dictionary<string, object>()
            };

            // Container durumuna göre stratejiyi ayarla;
            var fragmentation = CalculateFragmentation(container);

            if (fragmentation > 0.7)
            {
                strategy.StrategyType = OptimizationStrategyType.Compact;
                strategy.Parameters["aggressive"] = true;
            }
            else if (container.OccupiedSlots > container.MaxSlots * 0.9)
            {
                strategy.StrategyType = OptimizationStrategyType.Stack;
                strategy.Parameters["max_stack_size"] = MAX_STACK_SIZE;
            }
            else;
            {
                strategy.StrategyType = options.Strategy;
            }

            // Özel parametreler;
            if (options.CustomParameters != null)
            {
                foreach (var param in options.CustomParameters)
                {
                    strategy.Parameters[param.Key] = param.Value;
                }
            }

            return strategy;
        }

        private void ApplyOptimizationResult(StorageContainer container, OptimizationResult optimizationResult)
        {
            if (optimizationResult.OptimizedSlots != null)
            {
                container.Slots = optimizationResult.OptimizedSlots;
            }

            // İstatistikleri güncelle;
            container.OccupiedSlots = optimizationResult.FinalOccupiedSlots;
            container.CurrentWeight = optimizationResult.FinalWeight;
            container.CurrentVolume = optimizationResult.FinalVolume;

            // Item type count'ları yeniden hesapla;
            container.ItemTypeCounts.Clear();
            foreach (var slot in container.Slots)
            {
                if (slot.IsOccupied && slot.Item != null)
                {
                    var itemType = slot.Item.ItemType;
                    if (!container.ItemTypeCounts.ContainsKey(itemType))
                    {
                        container.ItemTypeCounts[itemType] = 0;
                    }
                    container.ItemTypeCounts[itemType] += slot.Quantity;
                }
            }
        }

        private ContainerStatistics CalculateContainerStatistics(StorageContainer container)
        {
            return new ContainerStatistics;
            {
                TotalSlots = container.MaxSlots,
                OccupiedSlots = container.OccupiedSlots,
                AvailableSlots = container.MaxSlots - container.OccupiedSlots,
                UsagePercentage = container.MaxSlots > 0 ?
                    (container.OccupiedSlots * 100.0 / container.MaxSlots) : 0,
                WeightUsage = container.MaxWeight > 0 ?
                    (container.CurrentWeight * 100.0 / container.MaxWeight) : 0,
                VolumeUsage = container.MaxVolume > 0 ?
                    (container.CurrentVolume * 100.0 / container.MaxVolume) : 0,
                ItemTypeDistribution = new Dictionary<string, int>(container.ItemTypeCounts),
                AverageItemQuality = CalculateAverageItemQuality(container),
                MostCommonItemType = container.ItemTypeCounts.OrderByDescending(kv => kv.Value)
                    .FirstOrDefault().Key;
            };
        }

        private async Task<ItemSummary> GenerateItemSummaryAsync(StorageContainer container, CancellationToken cancellationToken)
        {
            var summary = new ItemSummary;
            {
                TotalItems = container.OccupiedSlots,
                TotalQuantity = container.Slots.Where(s => s.IsOccupied).Sum(s => s.Quantity),
                UniqueItems = container.Slots.Count(s => s.IsOccupied && s.Item != null),
                EstimatedValue = await CalculateContainerValueAsync(container, cancellationToken),
                HeaviestItem = FindHeaviestItem(container),
                MostValuableItem = await FindMostValuableItemAsync(container, cancellationToken),
                ItemConditionSummary = CalculateItemConditionSummary(container)
            };

            return summary;
        }

        private CapacityInfo CalculateCapacityInfo(StorageContainer container)
        {
            return new CapacityInfo;
            {
                MaxSlots = container.MaxSlots,
                UsedSlots = container.OccupiedSlots,
                AvailableSlots = container.MaxSlots - container.OccupiedSlots,
                MaxWeight = container.MaxWeight,
                CurrentWeight = container.CurrentWeight,
                WeightRemaining = Math.Max(0, container.MaxWeight - container.CurrentWeight),
                MaxVolume = container.MaxVolume,
                CurrentVolume = container.CurrentVolume,
                VolumeRemaining = Math.Max(0, container.MaxVolume - container.CurrentVolume),
                FragmentationLevel = CalculateFragmentation(container),
                CanAcceptMultiSlotItems = container.SupportsMultiSlotItems,
                SlotRestrictions = container.Slots.Where(s => s.Restrictions != null)
                    .ToDictionary(s => s.SlotIndex, s => s.Restrictions)
            };
        }

        private async Task<List<OptimizationSuggestion>> GetOptimizationSuggestionsAsync(
            StorageContainer container,
            CancellationToken cancellationToken)
        {
            var suggestions = new List<OptimizationSuggestion>();

            // Fragmentation kontrolü;
            var fragmentation = CalculateFragmentation(container);
            if (fragmentation > 0.5)
            {
                suggestions.Add(new OptimizationSuggestion;
                {
                    Type = SuggestionType.Optimize,
                    Priority = SuggestionPriority.High,
                    Message = $"High fragmentation detected ({fragmentation:P0}). Consider optimizing.",
                    Details = new Dictionary<string, object>
                    {
                        ["fragmentation_level"] = fragmentation,
                        ["estimated_space_recovery"] = CalculateEstimatedSpaceRecovery(container)
                    }
                });
            }

            // Stackable items kontrolü;
            var stackableItems = container.Slots;
                .Where(s => s.IsOccupied && s.Item?.IsStackable == true && s.Quantity < MAX_STACK_SIZE)
                .ToList();

            if (stackableItems.Count > 1)
            {
                suggestions.Add(new OptimizationSuggestion;
                {
                    Type = SuggestionType.Stack,
                    Priority = SuggestionPriority.Medium,
                    Message = $"{stackableItems.Count} stackable items could be combined.",
                    Details = new Dictionary<string, object>
                    {
                        ["stackable_items"] = stackableItems.Count,
                        ["potential_slots_saved"] = stackableItems.Count - 1;
                    }
                });
            }

            // Weight/volume limit kontrolü;
            if (container.MaxWeight > 0 && container.CurrentWeight > container.MaxWeight * 0.9)
            {
                suggestions.Add(new OptimizationSuggestion;
                {
                    Type = SuggestionType.Cleanup,
                    Priority = SuggestionPriority.High,
                    Message = $"Container weight is at {container.CurrentWeight / container.MaxWeight:P0} capacity.",
                    Details = new Dictionary<string, object>
                    {
                        ["current_weight"] = container.CurrentWeight,
                        ["max_weight"] = container.MaxWeight,
                        ["usage_percentage"] = container.CurrentWeight / container.MaxWeight;
                    }
                });
            }

            return suggestions;
        }

        private async Task RemoveItemFromAllContainersAsync(string itemId, CancellationToken cancellationToken)
        {
            foreach (var containerPair in _containers)
            {
                var container = containerPair.Value;
                for (int i = 0; i < container.Slots.Length; i++)
                {
                    var slot = container.Slots[i];
                    if (slot.IsOccupied && slot.Item != null && slot.Item.Id == itemId)
                    {
                        // Item'ı kaldır;
                        slot.Item = null;
                        slot.Quantity = 0;
                        slot.IsOccupied = false;
                        slot.LastModified = DateTime.UtcNow;

                        // İstatistikleri güncelle;
                        UpdateContainerStatsAfterRetrieval(container, slot.Item, slot.Quantity);
                    }
                }

                // Container'ı kaydet;
                container.LastModified = DateTime.UtcNow;
                await SaveContainerAsync(container, cancellationToken);
            }

            _logger.LogInformation($"Removed item {itemId} from all containers");
        }

        private async Task InitializePlayerStorageAsync(string playerId, CancellationToken cancellationToken)
        {
            try
            {
                // Player inventory oluştur;
                var inventoryOptions = new CreateStorageOptions;
                {
                    Name = $"{playerId}_Inventory",
                    OwnerId = playerId,
                    ContainerType = ContainerType.PlayerInventory,
                    MaxSlots = 50,
                    MaxWeight = 100,
                    MaxVolume = 50,
                    AccessPermissions = new AccessPermissions;
                    {
                        Owner = playerId,
                        AllowPublicView = false,
                        AllowPublicStore = false;
                    }
                };

                await CreateStorageContainerAsync(
                    $"player_{playerId}_inventory",
                    inventoryOptions,
                    cancellationToken);

                // Player bank oluştur;
                var bankOptions = new CreateStorageOptions;
                {
                    Name = $"{playerId}_Bank",
                    OwnerId = playerId,
                    ContainerType = ContainerType.Bank,
                    MaxSlots = 100,
                    MaxWeight = 1000,
                    MaxVolume = 500,
                    AccessPermissions = new AccessPermissions;
                    {
                        Owner = playerId,
                        AllowPublicView = false,
                        AllowPublicStore = false;
                    }
                };

                await CreateStorageContainerAsync(
                    $"player_{playerId}_bank",
                    bankOptions,
                    cancellationToken);

                _logger.LogInformation($"Player storage initialized: {playerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to initialize player storage: {playerId}");
                // Hata durumunda uygulama devam edebilir;
            }
        }

        private async Task CleanupPlayerStorageAsync(string playerId, CancellationToken cancellationToken)
        {
            try
            {
                // Player'a ait container'ları bul;
                var playerContainers = _containers.Values;
                    .Where(c => c.OwnerId == playerId)
                    .ToList();

                foreach (var container in playerContainers)
                {
                    // Container'ı yedekle ve sil;
                    await DeleteContainerAsync(
                        container.Id,
                        new DeletionOptions;
                        {
                            AccessorId = "system",
                            BackupItems = true,
                            ForceDelete = true;
                        },
                        cancellationToken);
                }

                _logger.LogInformation($"Player storage cleaned up: {playerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to cleanup player storage: {playerId}");
            }
        }

        private async Task SaveContainerAsync(StorageContainer container, CancellationToken cancellationToken)
        {
            try
            {
                var containerJson = JsonSerializer.Serialize(container, _jsonOptions);
                var filePath = GetContainerFilePath(container.Id);

                await _fileManager.WriteAllTextAsync(filePath, containerJson, cancellationToken);

                // Database'e de kaydet (cache için)
                await SaveToDatabaseAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save container {container.Id}");
                throw new SaveException($"Failed to save container: {ex.Message}", ex);
            }
        }

        private async Task SaveToDatabaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                var database = new StorageDatabase;
                {
                    Version = "1.0",
                    LastUpdated = DateTime.UtcNow,
                    Containers = _containers.Values.ToList()
                };

                var databaseJson = JsonSerializer.Serialize(database, _jsonOptions);
                await _fileManager.WriteAllTextAsync(_storageDatabasePath, databaseJson, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save to database");
                // Database kayıt hatası kritik değil, container dosyaları hala mevcut;
            }
        }

        private string GetContainerFilePath(string containerId)
        {
            var containerDir = Path.Combine(
                Path.GetDirectoryName(_storageDatabasePath) ?? "Data/Storage",
                "Containers");

            Directory.CreateDirectory(containerDir);
            return Path.Combine(containerDir, $"{containerId}.json");
        }

        private async Task<DeletionResult> RemoveContainerInternalAsync(string containerId, CancellationToken cancellationToken)
        {
            if (_containers.TryGetValue(containerId, out var container))
            {
                _containers.Remove(containerId);

                // Container dosyasını sil;
                var containerFilePath = GetContainerFilePath(containerId);
                if (File.Exists(containerFilePath))
                {
                    File.Delete(containerFilePath);
                }

                return new DeletionResult;
                {
                    Success = true,
                    ContainerId = containerId,
                    ContainerName = container.Name;
                };
            }

            return new DeletionResult;
            {
                Success = false,
                ContainerId = containerId,
                ErrorMessage = "Container not found"
            };
        }

        private async Task BackupContainerItemsAsync(StorageContainer container, DeletionOptions options, CancellationToken cancellationToken)
        {
            try
            {
                var backupPath = GetBackupPath(container.Id);
                Directory.CreateDirectory(backupPath);

                var backupData = new;
                {
                    ContainerId = container.Id,
                    ContainerName = container.Name,
                    OwnerId = container.OwnerId,
                    BackupTime = DateTime.UtcNow,
                    Items = container.Slots;
                        .Where(s => s.IsOccupied && s.Item != null)
                        .Select(s => new;
                        {
                            SlotIndex = s.SlotIndex,
                            Item = s.Item,
                            Quantity = s.Quantity,
                            StoredAt = s.LastModified;
                        })
                        .ToList()
                };

                var backupJson = JsonSerializer.Serialize(backupData, _jsonOptions);
                var backupFilePath = Path.Combine(backupPath, $"{container.Id}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");

                await File.WriteAllTextAsync(backupFilePath, backupJson, cancellationToken);

                _logger.LogInformation($"Container items backed up: {container.Id} to {backupFilePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to backup container items: {container.Id}");
                // Backup hatası silme işlemini durdurmamalı;
            }
        }

        private string GetBackupPath(string containerId)
        {
            var backupBasePath = _appConfig.GetValue<string>("Backup:StoragePath") ?? "Backups/Storage";
            return Path.Combine(backupBasePath, "DeletedContainers", containerId);
        }

        private async Task<long> CalculateBackupSizeAsync(string backupPath, CancellationToken cancellationToken)
        {
            try
            {
                var files = Directory.GetFiles(backupPath, "*", SearchOption.AllDirectories);
                long totalSize = 0;

                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    totalSize += fileInfo.Length;
                }

                return totalSize;
            }
            catch
            {
                return 0;
            }
        }

        private float CalculateFragmentation(StorageContainer container)
        {
            if (container.MaxSlots == 0 || container.OccupiedSlots == 0)
                return 0;

            int gapCount = 0;
            bool inGap = false;

            for (int i = 0; i < container.Slots.Length; i++)
            {
                if (!container.Slots[i].IsOccupied)
                {
                    if (!inGap)
                    {
                        inGap = true;
                        gapCount++;
                    }
                }
                else;
                {
                    inGap = false;
                }
            }

            return (float)gapCount / container.MaxSlots;
        }

        private Dictionary<string, object> MergeMetadata(
            Dictionary<string, object> defaultMetadata,
            Dictionary<string, object> customMetadata)
        {
            var merged = new Dictionary<string, object>();

            if (defaultMetadata != null)
            {
                foreach (var kvp in defaultMetadata)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            if (customMetadata != null)
            {
                foreach (var kvp in customMetadata)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            return merged;
        }

        private SlotType GetSlotTypeFromTemplate(StorageTemplate template, int slotIndex)
        {
            if (template.SlotTypes != null && template.SlotTypes.TryGetValue(slotIndex, out var slotType))
            {
                return slotType;
            }

            return SlotType.General;
        }

        private SlotType DetermineSlotType(GameItem item)
        {
            // Item tipine göre slot tipi belirle;
            // Bu mantık oyunun ihtiyaçlarına göre genişletilebilir;
            return item.ItemType switch;
            {
                "Weapon" => SlotType.Weapon,
                "Armor" => SlotType.Armor,
                "Consumable" => SlotType.Consumable,
                "Resource" => SlotType.Resource,
                "QuestItem" => SlotType.Quest,
                _ => SlotType.General;
            };
        }

        private async Task<double> CalculateContainerValueAsync(StorageContainer container, CancellationToken cancellationToken)
        {
            double totalValue = 0;

            foreach (var slot in container.Slots)
            {
                if (slot.IsOccupied && slot.Item != null)
                {
                    // Item değeri hesaplama;
                    totalValue += slot.Item.BaseValue * slot.Quantity * (slot.Item.Quality / 100.0);
                }
            }

            return totalValue;
        }

        private GameItem FindHeaviestItem(StorageContainer container)
        {
            GameItem heaviest = null;
            double maxWeight = 0;

            foreach (var slot in container.Slots)
            {
                if (slot.IsOccupied && slot.Item != null && slot.Item.Weight > maxWeight)
                {
                    maxWeight = slot.Item.Weight;
                    heaviest = slot.Item;
                }
            }

            return heaviest;
        }

        private async Task<GameItem> FindMostValuableItemAsync(StorageContainer container, CancellationToken cancellationToken)
        {
            GameItem mostValuable = null;
            double maxValue = 0;

            foreach (var slot in container.Slots)
            {
                if (slot.IsOccupied && slot.Item != null)
                {
                    var itemValue = slot.Item.BaseValue * (slot.Item.Quality / 100.0) * slot.Quantity;
                    if (itemValue > maxValue)
                    {
                        maxValue = itemValue;
                        mostValuable = slot.Item;
                    }
                }
            }

            return mostValuable;
        }

        private Dictionary<string, int> CalculateItemConditionSummary(StorageContainer container)
        {
            var conditionSummary = new Dictionary<string, int>();

            foreach (var slot in container.Slots)
            {
                if (slot.IsOccupied && slot.Item != null)
                {
                    var condition = GetConditionCategory(slot.Item.Condition);
                    if (!conditionSummary.ContainsKey(condition))
                    {
                        conditionSummary[condition] = 0;
                    }
                    conditionSummary[condition] += slot.Quantity;
                }
            }

            return conditionSummary;
        }

        private string GetConditionCategory(float condition)
        {
            return condition switch;
            {
                >= 80 => "Excellent",
                >= 60 => "Good",
                >= 40 => "Fair",
                >= 20 => "Poor",
                _ => "Broken"
            };
        }

        private int CalculateEstimatedSpaceRecovery(StorageContainer container)
        {
            // Stackable items'ları say;
            var stackableItems = container.Slots;
                .Where(s => s.IsOccupied && s.Item?.IsStackable == true && s.Quantity < MAX_STACK_SIZE)
                .GroupBy(s => s.Item.Id)
                .ToList();

            int potentialSavings = 0;

            foreach (var group in stackableItems)
            {
                var totalQuantity = group.Sum(s => s.Quantity);
                var requiredSlots = (int)Math.Ceiling((double)totalQuantity / MAX_STACK_SIZE);
                var currentSlots = group.Count();

                potentialSavings += currentSlots - requiredSlots;
            }

            return potentialSavings;
        }

        private float CalculateAverageItemQuality(StorageContainer container)
        {
            var occupiedSlots = container.Slots.Where(s => s.IsOccupied && s.Item != null).ToList();
            if (occupiedSlots.Count == 0)
                return 0;

            return occupiedSlots.Average(s => s.Item.Quality);
        }

        private Dictionary<string, int> CalculateItemTypeDistribution()
        {
            var distribution = new Dictionary<string, int>();

            foreach (var container in _containers.Values)
            {
                foreach (var kvp in container.ItemTypeCounts)
                {
                    if (!distribution.ContainsKey(kvp.Key))
                    {
                        distribution[kvp.Key] = 0;
                    }
                    distribution[kvp.Key] += kvp.Value;
                }
            }

            return distribution;
        }

        private Dictionary<Rarity, int> CalculateRarityDistribution()
        {
            var distribution = new Dictionary<Rarity, int>();

            foreach (var container in _containers.Values)
            {
                foreach (var slot in container.Slots)
                {
                    if (slot.IsOccupied && slot.Item != null)
                    {
                        var rarity = slot.Item.Rarity;
                        if (!distribution.ContainsKey(rarity))
                        {
                            distribution[rarity] = 0;
                        }
                        distribution[rarity] += slot.Quantity;
                    }
                }
            }

            return distribution;
        }

        private double CalculateAverageQuantity()
        {
            var occupiedSlots = _containers.Values;
                .SelectMany(c => c.Slots)
                .Where(s => s.IsOccupied)
                .ToList();

            if (occupiedSlots.Count == 0)
                return 0;

            return occupiedSlots.Average(s => s.Quantity);
        }

        private int CalculateStackedItemsCount()
        {
            return _containers.Values;
                .SelectMany(c => c.Slots)
                .Count(s => s.IsOccupied && s.Item?.IsStackable == true && s.Quantity > 1);
        }

        private TimeSpan CalculateAverageStorageTime()
        {
            var now = DateTime.UtcNow;
            var storageTimes = new List<TimeSpan>();

            foreach (var container in _containers.Values)
            {
                foreach (var slot in container.Slots)
                {
                    if (slot.IsOccupied && slot.LastModified.HasValue)
                    {
                        storageTimes.Add(now - slot.LastModified.Value);
                    }
                }
            }

            if (storageTimes.Count == 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(storageTimes.Average(ts => ts.TotalSeconds));
        }

        private Dictionary<string, int> CalculateAccessFrequency()
        {
            // Bu metod, access log'larından veya event history'den hesaplanabilir;
            // Şimdilik basit bir implementasyon;
            return new Dictionary<string, int>
            {
                ["store"] = 0,
                ["retrieve"] = 0,
                ["transfer"] = 0,
                ["optimize"] = 0;
            };
        }

        private double CalculateWeightUsagePercentage()
        {
            var totalMaxWeight = _containers.Values.Where(c => c.MaxWeight > 0).Sum(c => c.MaxWeight);
            var totalCurrentWeight = _containers.Values.Sum(c => c.CurrentWeight);

            if (totalMaxWeight == 0)
                return 0;

            return (totalCurrentWeight * 100.0) / totalMaxWeight;
        }

        private double CalculateVolumeUsagePercentage()
        {
            var totalMaxVolume = _containers.Values.Where(c => c.MaxVolume > 0).Sum(c => c.MaxVolume);
            var totalCurrentVolume = _containers.Values.Sum(c => c.CurrentVolume);

            if (totalMaxVolume == 0)
                return 0;

            return (totalCurrentVolume * 100.0) / totalMaxVolume;
        }

        private float CalculateFragmentationLevel()
        {
            if (_containers.Count == 0)
                return 0;

            return _containers.Values.Average(CalculateFragmentation);
        }

        private async Task<OptimizationPotential> CalculateOptimizationPotentialAsync(CancellationToken cancellationToken)
        {
            var potential = new OptimizationPotential();

            foreach (var container in _containers.Values)
            {
                // Stack optimization potential;
                var stackableItems = container.Slots;
                    .Where(s => s.IsOccupied && s.Item?.IsStackable == true && s.Quantity < MAX_STACK_SIZE)
                    .GroupBy(s => s.Item.Id)
                    .ToList();

                foreach (var group in stackableItems)
                {
                    var totalQuantity = group.Sum(s => s.Quantity);
                    var requiredSlots = (int)Math.Ceiling((double)totalQuantity / MAX_STACK_SIZE);
                    var currentSlots = group.Count();

                    if (currentSlots > requiredSlots)
                    {
                        potential.TotalSlotsRecoverable += currentSlots - requiredSlots;
                        potential.ContainersWithPotential.Add(container.Id);
                    }
                }

                // Fragmentation potential;
                var fragmentation = CalculateFragmentation(container);
                if (fragmentation > 0.3)
                {
                    potential.FragmentedContainers++;
                }
            }

            potential.OverallScore = CalculateOptimizationScore(potential);

            return potential;
        }

        private List<StorageHotspot> IdentifyStorageHotspots()
        {
            var hotspots = new List<StorageHotspot>();

            // En çok item içeren container'lar;
            var topContainers = _containers.Values;
                .OrderByDescending(c => c.OccupiedSlots)
                .Take(5)
                .Select(c => new StorageHotspot;
                {
                    ContainerId = c.Id,
                    ContainerName = c.Name,
                    MetricType = HotspotMetricType.ItemCount,
                    MetricValue = c.OccupiedSlots,
                    UsagePercentage = (c.OccupiedSlots * 100.0) / c.MaxSlots;
                });

            hotspots.AddRange(topContainers);

            // En ağır container'lar;
            var heaviestContainers = _containers.Values;
                .Where(c => c.MaxWeight > 0)
                .OrderByDescending(c => c.CurrentWeight)
                .Take(3)
                .Select(c => new StorageHotspot;
                {
                    ContainerId = c.Id,
                    ContainerName = c.Name,
                    MetricType = HotspotMetricType.Weight,
                    MetricValue = c.CurrentWeight,
                    UsagePercentage = (c.CurrentWeight * 100.0) / c.MaxWeight;
                });

            hotspots.AddRange(heaviestContainers);

            return hotspots;
        }

        private async Task<List<StorageRecommendation>> GenerateRecommendationsAsync(CancellationToken cancellationToken)
        {
            var recommendations = new List<StorageRecommendation>();

            // Capacity recommendations;
            foreach (var container in _containers.Values)
            {
                if (container.OccupiedSlots > container.MaxSlots * 0.9)
                {
                    recommendations.Add(new StorageRecommendation;
                    {
                        Type = RecommendationType.ExpandCapacity,
                        Priority = RecommendationPriority.High,
                        TargetContainerId = container.Id,
                        Message = $"Container '{container.Name}' is at {container.OccupiedSlots * 100.0 / container.MaxSlots:F1}% capacity",
                        Action = $"Consider expanding container slots from {container.MaxSlots} to {container.MaxSlots + 20}"
                    });
                }

                if (container.MaxWeight > 0 && container.CurrentWeight > container.MaxWeight * 0.9)
                {
                    recommendations.Add(new StorageRecommendation;
                    {
                        Type = RecommendationType.ManageWeight,
                        Priority = RecommendationPriority.High,
                        TargetContainerId = container.Id,
                        Message = $"Container '{container.Name}' weight is at {container.CurrentWeight * 100.0 / container.MaxWeight:F1}%",
                        Action = "Remove heavy items or upgrade container weight capacity"
                    });
                }
            }

            // Optimization recommendations;
            var optimizationPotential = await CalculateOptimizationPotentialAsync(cancellationToken);
            if (optimizationPotential.TotalSlotsRecoverable > 10)
            {
                recommendations.Add(new StorageRecommendation;
                {
                    Type = RecommendationType.Optimize,
                    Priority = RecommendationPriority.Medium,
                    Message = $"Optimization could recover approximately {optimizationPotential.TotalSlotsRecoverable} slots",
                    Action = "Run global optimization on affected containers"
                });
            }

            // Security recommendations;
            var insecureContainers = _containers.Values;
                .Where(c => c.SecuritySettings.EncryptionLevel == EncryptionLevel.None &&
                           c.ContainerType != ContainerType.PlayerInventory)
                .ToList();

            if (insecureContainers.Any())
            {
                recommendations.Add(new StorageRecommendation;
                {
                    Type = RecommendationType.ImproveSecurity,
                    Priority = RecommendationPriority.Low,
                    Message = $"{insecureContainers.Count} containers have no encryption",
                    Action = "Enable encryption for sensitive containers"
                });
            }

            return recommendations.OrderByDescending(r => r.Priority).ToList();
        }

        private float CalculateOptimizationScore(OptimizationPotential potential)
        {
            float score = 0;

            // Slots recoverable score;
            if (potential.TotalSlotsRecoverable > 0)
            {
                score += Math.Min(potential.TotalSlotsRecoverable / 10.0f, 5.0f);
            }

            // Fragmented containers score;
            if (potential.FragmentedContainers > 0)
            {
                score += Math.Min(potential.FragmentedContainers / 5.0f, 5.0f);
            }

            // Overall score (0-10 scale)
            return Math.Min(score, 10.0f);
        }

        private void AutoSaveCallback(object state)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _storageLock.WaitAsync();
                        await SaveToDatabaseAsync(CancellationToken.None);
                        _logger.LogDebug("Auto-save completed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-save failed");
                    }
                    finally
                    {
                        _storageLock.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-save callback failed");
            }
        }

        private void CleanupCallback(object state)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _storageLock.WaitAsync();

                        // Eski backup'ları temizle;
                        await CleanupOldBackupsAsync();

                        // Geçici dosyaları temizle;
                        await CleanupTempFilesAsync();

                        _logger.LogDebug("Cleanup completed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Cleanup failed");
                    }
                    finally
                    {
                        _storageLock.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup callback failed");
            }
        }

        private async Task CleanupOldBackupsAsync()
        {
            try
            {
                var backupPath = _appConfig.GetValue<string>("Backup:StoragePath") ?? "Backups/Storage";
                if (!Directory.Exists(backupPath))
                    return;

                var backupDaysToKeep = _appConfig.GetValue<int>("Backup:DaysToKeep") ?? 30;
                var cutoffDate = DateTime.UtcNow.AddDays(-backupDaysToKeep);

                var backupDirs = Directory.GetDirectories(backupPath);
                foreach (var dir in backupDirs)
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.CreationTimeUtc < cutoffDate)
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                            _logger.LogInformation($"Deleted old backup: {dir}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to delete old backup: {dir}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old backups");
            }
        }

        private async Task CleanupTempFilesAsync()
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "NEDA_Storage");
                if (Directory.Exists(tempPath))
                {
                    // 24 saatten eski temp dosyalarını sil;
                    var cutoffDate = DateTime.UtcNow.AddHours(-24);
                    var tempFiles = Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories);

                    foreach (var file in tempFiles)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            if (fileInfo.LastWriteTimeUtc < cutoffDate)
                            {
                                File.Delete(file);
                            }
                        }
                        catch
                        {
                            // Ignore deletion errors;
                        }
                    }

                    // Boş dizinleri sil;
                    CleanupEmptyDirectories(tempPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup temp files");
            }
        }

        private void CleanupEmptyDirectories(string path)
        {
            try
            {
                var directories = Directory.GetDirectories(path);
                foreach (var dir in directories)
                {
                    CleanupEmptyDirectories(dir);

                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors;
            }
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new StorageManagerNotInitializedException("StorageManager is not initialized. Call InitializeAsync first.");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Timer'ları durdur ve dispose et;
                    _autoSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                    _autoSaveTimer?.Dispose();
                    _cleanupTimer?.Dispose();

                    // Lock'ı serbest bırak;
                    _storageLock?.Dispose();

                    // Son bir kayıt yap;
                    try
                    {
                        _ = SaveToDatabaseAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Son kayıt hatası göz ardı edilebilir;
                    }

                    _logger.LogInformation("StorageManager disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~StorageManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public enum ContainerType;
    {
        PlayerInventory,
        Bank,
        GuildStorage,
        Warehouse,
        ShopInventory,
        QuestStorage,
        Temporary,
        System;
    }

    public enum SlotType;
    {
        General,
        Weapon,
        Armor,
        Consumable,
        Resource,
        Quest,
        Special;
    }

    public enum StorageAction;
    {
        Store,
        Retrieve,
        Transfer,
        View,
        Search,
        Organize,
        Optimize,
        Delete;
    }

    public enum OptimizationStrategyType;
    {
        Compact,
        Stack,
        Categorize,
        Custom;
    }

    public enum OptimizationPriority;
    {
        Low,
        Medium,
        High;
    }

    public enum SecurityEventType;
    {
        UnauthorizedAccess,
        ContainerDeleted,
        SecuritySettingsChanged,
        EncryptionEnabled;
    }

    public enum EncryptionLevel;
    {
        None,
        Basic,
        Advanced,
        Military;
    }

    public enum SuggestionType;
    {
        Optimize,
        Stack,
        Cleanup,
        Upgrade;
    }

    public enum SuggestionPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum HotspotMetricType;
    {
        ItemCount,
        Weight,
        Value,
        AccessFrequency;
    }

    public enum RecommendationType;
    {
        ExpandCapacity,
        Optimize,
        ManageWeight,
        ImproveSecurity,
        Backup;
    }

    public enum RecommendationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum HealthStatus;
    {
        Healthy,
        Degraded,
        Unhealthy;
    }

    public class StorageContainer;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string OwnerId { get; set; }
        public ContainerType ContainerType { get; set; }
        public int MaxSlots { get; set; }
        public int OccupiedSlots { get; set; }
        public double MaxWeight { get; set; }
        public double CurrentWeight { get; set; }
        public double MaxVolume { get; set; }
        public double CurrentVolume { get; set; }
        public StorageSlot[] Slots { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime? LastOptimized { get; set; }
        public DateTime? LastOrganized { get; set; }
        public AccessPermissions AccessPermissions { get; set; }
        public StorageSecuritySettings SecuritySettings { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public Dictionary<string, int> ItemTypeCounts { get; set; } = new Dictionary<string, int>();
        public int TotalItemsStored { get; set; }
        public bool SupportsMultiSlotItems { get; set; } = false;
        public List<string> RestrictedItemTypes { get; set; }
        public int MinQuality { get; set; } = 0;
        public int MaxQuality { get; set; } = 100;
    }

    public class StorageSlot;
    {
        public int SlotIndex { get; set; }
        public SlotType SlotType { get; set; } = SlotType.General;
        public GameItem Item { get; set; }
        public string ItemInstanceId => Item?.InstanceId;
        public int Quantity { get; set; } = 1;
        public bool IsOccupied { get; set; } = false;
        public bool IsLocked { get; set; } = false;
        public bool IsReserved { get; set; } = false;
        public DateTime? LastModified { get; set; }
        public SlotRestrictions Restrictions { get; set; }
    }

    public class StorageGrid;
    {
        public string ContainerId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int CellSize { get; set; }
        public StorageGridCell[] Cells { get; set; }
    }

    public class StorageGridCell;
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool IsOccupied { get; set; }
        public string ItemInstanceId { get; set; }
        public int SlotIndex { get; set; }
    }

    public class CreateStorageOptions;
    {
        public static CreateStorageOptions Default => new CreateStorageOptions();

        public string Name { get; set; }
        public string OwnerId { get; set; }
        public ContainerType ContainerType { get; set; } = ContainerType.Temporary;
        public int? MaxSlots { get; set; }
        public double? MaxWeight { get; set; }
        public double? MaxVolume { get; set; }
        public string TemplateId { get; set; }
        public bool OverwriteExisting { get; set; } = false;
        public bool CreateStorageGrid { get; set; } = false;
        public GridOptions GridOptions { get; set; }
        public AccessPermissions AccessPermissions { get; set; }
        public StorageSecuritySettings SecuritySettings { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class StoreItemOptions;
    {
        public static StoreItemOptions Default => new StoreItemOptions();

        public string AccessorId { get; set; }
        public int? Quantity { get; set; }
        public bool StackIfPossible { get; set; } = true;
        public bool AutoOptimize { get; set; } = false;
        public bool ReserveSlot { get; set; } = false;
        public bool OverwriteOccupiedSlot { get; set; } = false;
        public Dictionary<string, object> StorageMetadata { get; set; }
    }

    public class RetrieveItemOptions;
    {
        public static RetrieveItemOptions Default => new RetrieveItemOptions();

        public string AccessorId { get; set; }
        public int? Quantity { get; set; }
        public bool ForceRetrieve { get; set; } = false;
        public bool LeaveEmptySlots { get; set; } = false;
    }

    public class TransferOptions;
    {
        public static TransferOptions Default => new TransferOptions();

        public string AccessorId { get; set; }
        public int? Quantity { get; set; }
        public bool StackInTarget { get; set; } = true;
        public bool ForceTransfer { get; set; } = false;
    }

    public class OptimizationOptions;
    {
        public static OptimizationOptions Default => new OptimizationOptions();

        public OptimizationStrategyType Strategy { get; set; } = OptimizationStrategyType.Compact;
        public OptimizationPriority Priority { get; set; } = OptimizationPriority.Medium;
        public Dictionary<string, object> CustomParameters { get; set; }
        public bool PreviewOnly { get; set; } = false;
    }

    public class OrganizationOptions;
    {
        public static OrganizationOptions Default => new OrganizationOptions();

        public string AccessorId { get; set; }
        public OrganizationStrategy OrganizationStrategy { get; set; } = OrganizationStrategy.ByType;
        public SortOrder SortOrder { get; set; } = SortOrder.Ascending;
        public bool GroupByCategory { get; set; } = true;
        public bool GroupByRarity { get; set; } = false;
        public Dictionary<string, object> CustomRules { get; set; }
    }

    public class DeletionOptions;
    {
        public static DeletionOptions Default => new DeletionOptions();

        public string AccessorId { get; set; }
        public bool ForceDelete { get; set; } = false;
        public bool BackupItems { get; set; } = true;
        public string BackupLocation { get; set; }
    }

    public class BackupOptions;
    {
        public static BackupOptions Default => new BackupOptions();

        public string Location { get; set; }
        public bool Compress { get; set; } = true;
        public bool IncludeMetadata { get; set; } = true;
        public string EncryptionKey { get; set; }
        public int? MaxBackupAgeDays { get; set; } = 30;
    }

    public class SearchCriteria;
    {
        public string AccessorId { get; set; }
        public string ItemId { get; set; }
        public List<string> ItemTypes { get; set; }
        public string NameContains { get; set; }
        public Rarity? MinRarity { get; set; }
        public Rarity? MaxRarity { get; set; }
        public int? MinQuality { get; set; }
        public int? MaxQuality { get; set; }
        public Dictionary<string, object> MetadataFilters { get; set; }
        public SlotFilters SlotFilters { get; set; }
        public SearchType SearchType { get; set; } = SearchType.Standard;
        public int? Skip { get; set; }
        public int? Take { get; set; }

        public bool HasFilters =>
            !string.IsNullOrEmpty(ItemId) ||
            (ItemTypes != null && ItemTypes.Count > 0) ||
            !string.IsNullOrEmpty(NameContains) ||
            MinRarity.HasValue ||
            MaxRarity.HasValue ||
            MinQuality.HasValue ||
            MaxQuality.HasValue ||
            (MetadataFilters != null && MetadataFilters.Count > 0) ||
            SlotFilters != null;
    }

    public class InfoOptions;
    {
        public static InfoOptions Default => new InfoOptions();

        public string AccessorId { get; set; }
        public bool IncludeDetailedInfo { get; set; } = false;
        public bool IncludeSecurityInfo { get; set; } = true;
        public bool IncludeStatistics { get; set; } = true;
    }

    // Results and Events;
    public class StorageResult;
    {
        public bool Success { get; set; }
        public string ContainerId { get; set; }
        public string ItemId { get; set; }
        public string InstanceId { get; set; }
        public int SlotIndex { get; set; }
        public int QuantityStored { get; set; }
        public int RemainingQuantity { get; set; }
        public DateTime StorageTime { get; set; }
        public Dictionary<string, object> StorageMetadata { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class RetrievalResult;
    {
        public bool Success { get; set; }
        public string ContainerId { get; set; }
        public GameItem Item { get; set; }
        public string OriginalInstanceId { get; set; }
        public int SlotIndex { get; set; }
        public int QuantityRetrieved { get; set; }
        public int RemainingQuantity { get; set; }
        public DateTime RetrievalTime { get; set; }
        public Dictionary<string, object> RetrievalMetadata { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class TransferResult;
    {
        public bool Success { get; set; }
        public string SourceContainerId { get; set; }
        public string TargetContainerId { get; set; }
        public string ItemId { get; set; }
        public string OriginalInstanceId { get; set; }
        public string NewInstanceId { get; set; }
        public int SourceSlotIndex { get; set; }
        public int TargetSlotIndex { get; set; }
        public int QuantityTransferred { get; set; }
        public bool WasStacked { get; set; }
        public DateTime TransferTime { get; set; }
        public Dictionary<string, object> TransferMetadata { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class OptimizationResult;
    {
        public bool Success { get; set; }
        public string ContainerId { get; set; }
        public OptimizationStrategy Strategy { get; set; }
        public OptimizationDetails OptimizationDetails { get; set; }
        public ContainerSnapshot BeforeSnapshot { get; set; }
        public ContainerSnapshot AfterSnapshot { get; set; }
        public int SpaceRecovered { get; set; }
        public int ItemsReorganized { get; set; }
        public DateTime OptimizationTime { get; set; }
        public Dictionary<string, object> OptimizationMetadata { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class OrganizationResult;
    {
        public bool Success { get; set; }
        public string ContainerId { get; set; }
        public OrganizationStrategy OrganizationStrategy { get; set; }
        public ContainerSnapshot OriginalSnapshot { get; set; }
        public ContainerSnapshot OrganizedSnapshot { get; set; }
        public int ItemsMoved { get; set; }
        public int CategoriesApplied { get; set; }
        public DateTime OrganizationTime { get; set; }
        public Dictionary<string, object> OrganizationMetadata { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class SearchResult;
    {
        public bool Success { get; set; }
        public string ContainerId { get; set; }
        public SearchCriteria SearchCriteria { get; set; }
        public int TotalMatches { get; set; }
        public int ReturnedMatches { get; set; }
        public List<SearchResultItem> Results { get; set; }
        public DateTime SearchTime { get; set; }
        public Dictionary<string, object> SearchMetadata { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class DeletionResult;
    {
        public bool Success { get; set; }
        public string ContainerId { get; set; }
        public string ContainerName { get; set; }
        public bool ItemsBackedUp { get; set; }
        public int ItemsDeleted { get; set; }
        public DateTime DeletionTime { get; set; }
        public Dictionary<string, object> DeletionMetadata { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class BackupResult;
    {
        public string BackupId { get; set; }
        public string BackupPath { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int ContainerCount { get; set; }
        public List<string> BackedUpContainers { get; set; }
        public List<string> FailedContainers { get; set; }
        public long TotalSize { get; set; }
        public BackupOptions Options { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class ContainerInfo;
    {
        public string ContainerId { get; set; }
        public StorageContainer Container { get; set; }
        public ContainerStatistics Statistics { get; set; }
        public ItemSummary ItemSummary { get; set; }
        public CapacityInfo CapacityInfo { get; set; }
        public SecurityInfo SecurityInfo { get; set; }
        public List<DetailedSlotInfo> DetailedSlots { get; set; }
        public Dictionary<string, List<CategorizedItem>> CategorizedItems { get; set; }
        public List<OptimizationSuggestion> OptimizationSuggestions { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    public class StorageStatistics;
    {
        public DateTime GeneratedAt { get; set; }
        public ContainerStatistics ContainerStatistics { get; set; }
        public ItemStatistics ItemStatistics { get; set; }
        public PerformanceStatistics PerformanceStatistics { get; set; }
        public CapacityStatistics CapacityStatistics { get; set; }
        public StorageAnalysis Analysis { get; set; }
    }

    public class HealthCheckResult;
    {
        public string ServiceName { get; set; }
        public HealthStatus OverallStatus { get; set; }
        public string Message { get; set; }
        public DateTime CheckTime { get; set; }
        public Dictionary<string, HealthCheckDetail> Checks { get; set; }
    }

    // Exceptions;
    public class StorageManagerException : Exception
    {
        public StorageManagerException(string message) : base(message) { }
        public StorageManagerException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContainerNotFoundException : StorageManagerException;
    {
        public ContainerNotFoundException(string message) : base(message) { }
    }

    public class ContainerAlreadyExistsException : StorageManagerException;
    {
        public ContainerAlreadyExistsException(string message) : base(message) { }
    }

    public class ContainerCreationException : StorageManagerException;
    {
        public ContainerCreationException(string message) : base(message) { }
        public ContainerCreationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ItemStorageException : StorageManagerException;
    {
        public ItemStorageException(string message) : base(message) { }
        public ItemStorageException(string message, Exception inner) : base(message, inner) { }
    }

    public class ItemRetrievalException : StorageManagerException;
    {
        public ItemRetrievalException(string message) : base(message) { }
        public ItemRetrievalException(string message, Exception inner) : base(message, inner) { }
    }

    public class NoAvailableSlotsException : StorageManagerException;
    {
        public NoAvailableSlotsException(string message) : base(message) { }
    }

    public class CapacityExceededException : StorageManagerException;
    {
        public CapacityExceededException(string message) : base(message) { }
    }

    public class AccessDeniedException : StorageManagerException;
    {
        public AccessDeniedException(string message) : base(message) { }
    }

    public class ItemNotFoundException : StorageManagerException;
    {
        public ItemNotFoundException(string message) : base(message) { }
    }

    public class InsufficientQuantityException : StorageManagerException;
    {
        public InsufficientQuantityException(string message) : base(message) { }
    }

    public class SlotLockedException : StorageManagerException;
    {
        public SlotLockedException(string message) : base(message) { }
    }

    public class TransferException : StorageManagerException;
    {
        public TransferException(string message) : base(message) { }
        public TransferException(string message, Exception inner) : base(message, inner) { }
    }

    public class OptimizationException : StorageManagerException;
    {
        public OptimizationException(string message) : base(message) { }
        public OptimizationException(string message, Exception inner) : base(message, inner) { }
    }

    public class OrganizationException : StorageManagerException;
    {
        public OrganizationException(string message) : base(message) { }
        public OrganizationException(string message, Exception inner) : base(message, inner) { }
    }

    public class DeletionException : StorageManagerException;
    {
        public DeletionException(string message) : base(message) { }
        public DeletionException(string message, Exception inner) : base(message, inner) { }
    }

    public class BackupException : StorageManagerException;
    {
        public BackupException(string message) : base(message) { }
        public BackupException(string message, Exception inner) : base(message, inner) { }
    }

    public class SearchException : StorageManagerException;
    {
        public SearchException(string message) : base(message) { }
        public SearchException(string message, Exception inner) : base(message, inner) { }
    }

    public class InfoRetrievalException : StorageManagerException;
    {
        public InfoRetrievalException(string message) : base(message) { }
        public InfoRetrievalException(string message, Exception inner) : base(message, inner) { }
    }

    public class StatisticsException : StorageManagerException;
    {
        public StatisticsException(string message) : base(message) { }
        public StatisticsException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContainerNotEmptyException : StorageManagerException;
    {
        public ContainerNotEmptyException(string message) : base(message) { }
    }

    public class StorageManagerNotInitializedException : StorageManagerException;
    {
        public StorageManagerNotInitializedException(string message) : base(message) { }
    }

    public class TemplateNotFoundException : StorageManagerException;
    {
        public TemplateNotFoundException(string message) : base(message) { }
    }

    public class ValidationException : StorageManagerException;
    {
        public ValidationException(string message) : base(message) { }
    }

    public class ItemSizeException : StorageManagerException;
    {
        public ItemSizeException(string message) : base(message) { }
    }

    public class ItemRestrictionException : StorageManagerException;
    {
        public ItemRestrictionException(string message) : base(message) { }
    }

    public class QualityRestrictionException : StorageManagerException;
    {
        public QualityRestrictionException(string message) : base(message) { }
    }

    public class StackSizeException : StorageManagerException;
    {
        public StackSizeException(string message) : base(message) { }
    }

    public class SlotPreparationException : StorageManagerException;
    {
        public SlotPreparationException(string message) : base(message) { }
    }

    public class SaveException : StorageManagerException;
    {
        public SaveException(string message) : base(message) { }
        public SaveException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
