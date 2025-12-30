using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NEDA.CharacterSystems.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Services.Inventory;

namespace NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
{
    /// <summary>
    /// Karakter ekipman yönetim sistemi;
    /// </summary>
    public class GearEquipper : IGearEquipper;
    {
        #region Fields;

        private readonly ILogger _logger;
        private readonly IInventoryService _inventoryService;
        private readonly IOutfitManager _outfitManager;
        private readonly Dictionary<string, EquippedGear> _equippedGearByCharacter;
        private readonly object _lock = new object();
        private readonly GearValidationRules _validationRules;

        #endregion;

        #region Properties;

        /// <summary>
        /// Maksimum ekipman slot sayısı;
        /// </summary>
        public int MaxSlots { get; private set; } = 10;

        /// <summary>
        /// Ekipman yükleme durumu;
        /// </summary>
        public EquipStatus Status { get; private set; }

        /// <summary>
        /// Kullanılabilir ekipman slotları;
        /// </summary>
        public IReadOnlyDictionary<GearSlotType, GearSlot> AvailableSlots { get; private set; }

        #endregion;

        #region Constructors;

        /// <summary>
        /// GearEquipper sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public GearEquipper(
            ILogger logger,
            IInventoryService inventoryService,
            IOutfitManager outfitManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
            _outfitManager = outfitManager ?? throw new ArgumentNullException(nameof(outfitManager));

            _equippedGearByCharacter = new Dictionary<string, EquippedGear>();
            _validationRules = new GearValidationRules();

            InitializeSlots();
            Status = EquipStatus.Ready;

            _logger.Info("GearEquipper initialized successfully.");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Karaktere ekipman giydirir;
        /// </summary>
        /// <param name="characterId">Karakter ID</param>
        /// <param name="itemId">Ekipman item ID</param>
        /// <param name="slotType">Ekipman slot tipi</param>
        /// <returns>Ekipman işlemi sonucu</returns>
        public async Task<EquipResult> EquipItemAsync(string characterId, string itemId, GearSlotType slotType)
        {
            try
            {
                ValidateParameters(characterId, itemId, slotType);

                _logger.Debug($"Attempting to equip item {itemId} to character {characterId} in slot {slotType}");

                lock (_lock)
                {
                    if (Status != EquipStatus.Ready)
                        throw new InvalidOperationException($"GearEquipper is not ready. Current status: {Status}");
                }

                // Inventory'den item'ı al;
                var inventoryItem = await _inventoryService.GetItemAsync(characterId, itemId);
                if (inventoryItem == null)
                {
                    _logger.Warning($"Item {itemId} not found in character {characterId}'s inventory");
                    return EquipResult.Failure(EquipError.ItemNotFound, $"Item {itemId} not found");
                }

                // Item'ın ekipman olup olmadığını kontrol et;
                if (!inventoryItem.IsEquippable)
                {
                    _logger.Warning($"Item {itemId} is not equippable");
                    return EquipResult.Failure(EquipError.ItemNotEquippable, $"Item {itemId} is not equippable");
                }

                // Slot uyumluluğunu kontrol et;
                if (!IsSlotCompatible(slotType, inventoryItem))
                {
                    _logger.Warning($"Item {itemId} is not compatible with slot {slotType}");
                    return EquipResult.Failure(EquipError.SlotIncompatible,
                        $"Item {itemId} cannot be equipped in {slotType} slot");
                }

                // Karakter gereksinimlerini kontrol et;
                var requirementsResult = await CheckRequirementsAsync(characterId, inventoryItem);
                if (!requirementsResult.IsMet)
                {
                    _logger.Warning($"Character {characterId} does not meet requirements for item {itemId}");
                    return EquipResult.Failure(EquipError.RequirementsNotMet,
                        $"Requirements not met: {string.Join(", ", requirementsResult.FailedRequirements)}");
                }

                // Stat çakışmalarını kontrol et;
                var conflictResult = await CheckStatConflictsAsync(characterId, inventoryItem, slotType);
                if (conflictResult.HasConflicts)
                {
                    _logger.Warning($"Stat conflicts detected for item {itemId}: {conflictResult.ConflictDetails}");
                    return EquipResult.Failure(EquipError.StatConflict,
                        $"Stat conflicts: {conflictResult.ConflictDetails}");
                }

                // Eski ekipmanı çıkar (varsa)
                var unequipResult = await UnequipSlotAsync(characterId, slotType, true);
                if (!unequipResult.Success && !unequipResult.IsSlotEmpty)
                {
                    _logger.Warning($"Failed to unequip existing item from slot {slotType}: {unequipResult.ErrorMessage}");
                    return EquipResult.Failure(EquipError.UnequipFailed,
                        $"Failed to clear slot: {unequipResult.ErrorMessage}");
                }

                // Ekipmanı giydir;
                var equippedGear = GetOrCreateEquippedGear(characterId);
                var gearItem = new GearItem;
                {
                    ItemId = itemId,
                    SlotType = slotType,
                    ItemData = inventoryItem,
                    EquipTime = DateTime.UtcNow,
                    Durability = inventoryItem.MaxDurability,
                    Enchantments = inventoryItem.Enchantments?.ToList() ?? new List<ItemEnchantment>()
                };

                equippedGear.EquipItem(slotType, gearItem);

                // Inventory'den item'ı kaldır;
                await _inventoryService.RemoveItemAsync(characterId, itemId, 1);

                // Karakter statlarını güncelle;
                await ApplyItemStatsAsync(characterId, gearItem);

                // Görsel outfit'i güncelle;
                await _outfitManager.UpdateOutfitAsync(characterId, slotType, inventoryItem.ModelId,
                    inventoryItem.TextureId, inventoryItem.MaterialId);

                _logger.Info($"Successfully equipped item {itemId} to character {characterId} in slot {slotType}");

                var result = EquipResult.Success(gearItem);
                result.AppliedStats = await CalculateAppliedStatsAsync(characterId, gearItem);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error equipping item {itemId} to character {characterId}: {ex.Message}", ex);
                return EquipResult.Failure(EquipError.InternalError, $"Internal error: {ex.Message}");
            }
        }

        /// <summary>
        /// Karakterden ekipmanı çıkarır;
        /// </summary>
        public async Task<UnequipResult> UnequipItemAsync(string characterId, string itemId, GearSlotType slotType)
        {
            try
            {
                ValidateParameters(characterId, itemId, slotType);

                _logger.Debug($"Attempting to unequip item {itemId} from character {characterId}");

                var equippedGear = GetEquippedGear(characterId);
                if (equippedGear == null)
                {
                    return UnequipResult.Failure(UnequipError.NoEquippedGear,
                        $"Character {characterId} has no equipped gear");
                }

                var gearItem = equippedGear.GetItemInSlot(slotType);
                if (gearItem == null || gearItem.ItemId != itemId)
                {
                    return UnequipResult.Failure(UnequipError.ItemNotEquipped,
                        $"Item {itemId} is not equipped in slot {slotType}");
                }

                // Statları geri al;
                await RemoveItemStatsAsync(characterId, gearItem);

                // Ekipmanı çıkar;
                equippedGear.UnequipItem(slotType);

                // Inventory'e geri ekle;
                await _inventoryService.AddItemAsync(characterId, gearItem.ItemData, 1);

                // Outfit'i güncelle;
                await _outfitManager.ClearSlotAsync(characterId, slotType);

                _logger.Info($"Successfully unequipped item {itemId} from character {characterId}");

                return UnequipResult.Success(gearItem);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error unequipping item {itemId}: {ex.Message}", ex);
                return UnequipResult.Failure(UnequipError.InternalError, $"Internal error: {ex.Message}");
            }
        }

        /// <summary>
        /// Slot'tan ekipmanı çıkarır;
        /// </summary>
        public async Task<UnequipResult> UnequipSlotAsync(string characterId, GearSlotType slotType, bool silent = false)
        {
            try
            {
                var equippedGear = GetEquippedGear(characterId);
                if (equippedGear == null)
                    return UnequipResult.EmptySlot();

                var gearItem = equippedGear.GetItemInSlot(slotType);
                if (gearItem == null)
                    return UnequipResult.EmptySlot();

                if (!silent)
                {
                    return await UnequipItemAsync(characterId, gearItem.ItemId, slotType);
                }

                // Silent mode - sadece statları geri al;
                await RemoveItemStatsAsync(characterId, gearItem);
                equippedGear.UnequipItem(slotType);

                return UnequipResult.Success(gearItem);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error unequipping slot {slotType}: {ex.Message}", ex);
                return UnequipResult.Failure(UnequipError.InternalError, $"Internal error: {ex.Message}");
            }
        }

        /// <summary>
        /// Karakterin tüm ekipmanını çıkarır;
        /// </summary>
        public async Task<BulkUnequipResult> UnequipAllAsync(string characterId)
        {
            var result = new BulkUnequipResult();

            try
            {
                var equippedGear = GetEquippedGear(characterId);
                if (equippedGear == null || !equippedGear.HasAnyEquipment)
                {
                    return BulkUnequipResult.Empty();
                }

                var equippedItems = equippedGear.GetAllEquippedItems().ToList();

                foreach (var item in equippedItems)
                {
                    var unequipResult = await UnequipItemAsync(characterId, item.ItemId, item.SlotType);
                    result.AddResult(item.SlotType, unequipResult);
                }

                result.IsSuccess = !result.HasFailures;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error unequipping all gear from {characterId}: {ex.Message}", ex);
                return BulkUnequipResult.Failure($"Failed to unequip all: {ex.Message}");
            }
        }

        /// <summary>
        /// Karakterin ekipman durumunu getirir;
        /// </summary>
        public EquippedGear GetCharacterGear(string characterId)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            lock (_lock)
            {
                return _equippedGearByCharacter.TryGetValue(characterId, out var gear)
                    ? gear.Clone()
                    : new EquippedGear(characterId);
            }
        }

        /// <summary>
        /// Slot'taki ekipmanı getirir;
        /// </summary>
        public GearItem GetItemInSlot(string characterId, GearSlotType slotType)
        {
            var gear = GetEquippedGear(characterId);
            return gear?.GetItemInSlot(slotType);
        }

        /// <summary>
        /// Ekipmanın statlarını hesaplar;
        /// </summary>
        public async Task<GearStats> CalculateTotalStatsAsync(string characterId)
        {
            var gear = GetEquippedGear(characterId);
            if (gear == null || !gear.HasAnyEquipment)
                return new GearStats();

            var totalStats = new GearStats();
            var equippedItems = gear.GetAllEquippedItems();

            foreach (var item in equippedItems)
            {
                var itemStats = await CalculateItemStatsAsync(characterId, item);
                totalStats.Add(itemStats);
            }

            // Set bonuslarını uygula;
            var setBonuses = await CalculateSetBonusesAsync(characterId, equippedItems);
            totalStats.Add(setBonuses);

            return totalStats;
        }

        /// <summary>
        /// Ekipman dayanıklılığını günceller;
        /// </summary>
        public async Task UpdateDurabilityAsync(string characterId, GearSlotType slotType, int damage)
        {
            try
            {
                var gearItem = GetItemInSlot(characterId, slotType);
                if (gearItem == null)
                    return;

                gearItem.Durability = Math.Max(0, gearItem.Durability - damage);

                if (gearItem.Durability <= 0)
                {
                    _logger.Warning($"Item {gearItem.ItemId} in slot {slotType} has broken");
                    await UnequipSlotAsync(characterId, slotType);

                    // Broken item event'i tetikle;
                    OnItemBroken?.Invoke(this, new ItemBrokenEventArgs;
                    {
                        CharacterId = characterId,
                        ItemId = gearItem.ItemId,
                        SlotType = slotType;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error updating durability: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Ekipmanı tamir eder;
        /// </summary>
        public async Task<RepairResult> RepairItemAsync(string characterId, GearSlotType slotType, int repairAmount)
        {
            try
            {
                var gearItem = GetItemInSlot(characterId, slotType);
                if (gearItem == null)
                    return RepairResult.Failure(RepairError.ItemNotFound);

                if (gearItem.Durability >= gearItem.ItemData.MaxDurability)
                    return RepairResult.Failure(RepairError.FullyRepaired);

                gearItem.Durability = Math.Min(
                    gearItem.ItemData.MaxDurability,
                    gearItem.Durability + repairAmount;
                );

                _logger.Info($"Repaired item {gearItem.ItemId} to {gearItem.Durability}/{gearItem.ItemData.MaxDurability} durability");

                return RepairResult.Success(gearItem.Durability, gearItem.ItemData.MaxDurability);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error repairing item: {ex.Message}", ex);
                return RepairResult.Failure(RepairError.InternalError);
            }
        }

        /// <summary>
        /// Ekipman büyüsü ekler;
        /// </summary>
        public async Task<EnchantResult> EnchantItemAsync(string characterId, GearSlotType slotType, Enchantment enchantment)
        {
            try
            {
                var gearItem = GetItemInSlot(characterId, slotType);
                if (gearItem == null)
                    return EnchantResult.Failure(EnchantError.ItemNotFound);

                if (gearItem.Enchantments.Count >= gearItem.ItemData.MaxEnchantments)
                    return EnchantResult.Failure(EnchantError.MaxEnchantmentsReached);

                // Büyü uyumluluğunu kontrol et;
                if (!_validationRules.IsEnchantmentCompatible(gearItem.ItemData, enchantment))
                    return EnchantResult.Failure(EnchantError.IncompatibleEnchantment);

                gearItem.Enchantments.Add(new ItemEnchantment;
                {
                    EnchantmentId = enchantment.Id,
                    Name = enchantment.Name,
                    Power = enchantment.Power,
                    AppliedDate = DateTime.UtcNow,
                    Duration = enchantment.Duration,
                    StatModifiers = enchantment.StatModifiers.ToList()
                });

                // Büyülü statları uygula;
                await ApplyEnchantmentStatsAsync(characterId, gearItem, enchantment);

                _logger.Info($"Enchanted item {gearItem.ItemId} with {enchantment.Name}");

                return EnchantResult.Success(gearItem.Enchantments);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error enchanting item: {ex.Message}", ex);
                return EnchantResult.Failure(EnchantError.InternalError);
            }
        }

        #endregion;

        #region Private Methods;

        private void InitializeSlots()
        {
            var slots = new Dictionary<GearSlotType, GearSlot>();

            foreach (GearSlotType slotType in Enum.GetValues(typeof(GearSlotType)))
            {
                slots[slotType] = new GearSlot;
                {
                    SlotType = slotType,
                    IsOccupied = false,
                    CompatibleItemTypes = GetCompatibleItemTypes(slotType),
                    RequiredLevel = 1,
                    IsLocked = false;
                };
            }

            AvailableSlots = slots;
        }

        private List<ItemType> GetCompatibleItemTypes(GearSlotType slotType)
        {
            return slotType switch;
            {
                GearSlotType.Head => new List<ItemType> { ItemType.Helmet, ItemType.Hat, ItemType.Crown },
                GearSlotType.Chest => new List<ItemType> { ItemType.Chestplate, ItemType.Robe, ItemType.Vest },
                GearSlotType.Hands => new List<ItemType> { ItemType.Gloves, ItemType.Gauntlets },
                GearSlotType.Legs => new List<ItemType> { ItemType.Leggings, ItemType.Pants },
                GearSlotType.Feet => new List<ItemType> { ItemType.Boots, ItemType.Shoes },
                GearSlotType.Weapon => new List<ItemType> { ItemType.Sword, ItemType.Bow, ItemType.Staff, ItemType.Dagger },
                GearSlotType.Shield => new List<ItemType> { ItemType.Shield, ItemType.Buckler },
                GearSlotType.Necklace => new List<ItemType> { ItemType.Amulet, ItemType.Necklace },
                GearSlotType.Ring => new List<ItemType> { ItemType.Ring },
                GearSlotType.Trinket => new List<ItemType> { ItemType.Trinket, ItemType.Talisman },
                _ => new List<ItemType>()
            };
        }

        private EquippedGear GetOrCreateEquippedGear(string characterId)
        {
            lock (_lock)
            {
                if (!_equippedGearByCharacter.TryGetValue(characterId, out var gear))
                {
                    gear = new EquippedGear(characterId);
                    _equippedGearByCharacter[characterId] = gear;
                }
                return gear;
            }
        }

        private EquippedGear GetEquippedGear(string characterId)
        {
            lock (_lock)
            {
                return _equippedGearByCharacter.TryGetValue(characterId, out var gear) ? gear : null;
            }
        }

        private bool IsSlotCompatible(GearSlotType slotType, InventoryItem item)
        {
            if (!AvailableSlots.TryGetValue(slotType, out var slot))
                return false;

            return slot.CompatibleItemTypes.Contains(item.Type);
        }

        private async Task<RequirementCheckResult> CheckRequirementsAsync(string characterId, InventoryItem item)
        {
            var result = new RequirementCheckResult();

            // Seviye kontrolü;
            if (item.RequiredLevel > 0) // CharacterService'den karakter seviyesi kontrolü yapılmalı;
            {
                // Burada karakter seviyesi kontrolü yapılacak;
                // result.AddRequirement("Level", item.RequiredLevel, actualLevel);
            }

            // Yetenek kontrolü;
            if (item.RequiredSkills?.Any() == true)
            {
                // Skill kontrolü;
            }

            // Irk/Sınıf kontrolü;
            if (item.RestrictedRaces?.Any() == true || item.RestrictedClasses?.Any() == true)
            {
                // Karakter bilgileri kontrolü;
            }

            return await Task.FromResult(result);
        }

        private async Task<StatConflictResult> CheckStatConflictsAsync(string characterId, InventoryItem newItem, GearSlotType slotType)
        {
            var result = new StatConflictResult();
            var currentGear = GetEquippedGear(characterId);

            if (currentGear == null)
                return result;

            // Aynı türde iki ekipman kontrolü (örneğin iki kılıç)
            if (slotType == GearSlotType.Weapon)
            {
                var otherWeapon = currentGear.GetItemInSlot(GearSlotType.Weapon);
                if (otherWeapon != null && !CanDualWield(newItem, otherWeapon.ItemData))
                {
                    result.AddConflict("Cannot dual wield these weapon types");
                }
            }

            // Stat çakışmalarını kontrol et;
            var newItemStats = await CalculateItemStatsAsync(characterId,
                new GearItem { ItemData = newItem, SlotType = slotType });

            var currentStats = await CalculateTotalStatsAsync(characterId);

            // Özel çakışma kurallarını kontrol et;
            foreach (var rule in _validationRules.StatConflictRules)
            {
                if (rule.HasConflict(currentStats, newItemStats))
                {
                    result.AddConflict(rule.ConflictMessage);
                }
            }

            return result;
        }

        private bool CanDualWield(InventoryItem item1, InventoryItem item2)
        {
            // Çift silah kullanım kuralları;
            if (item1.WeightClass == WeightClass.Heavy || item2.WeightClass == WeightClass.Heavy)
                return false;

            if (item1.Type == ItemType.Shield || item2.Type == ItemType.Shield)
                return false;

            return item1.DualWieldable && item2.DualWieldable;
        }

        private async Task ApplyItemStatsAsync(string characterId, GearItem gearItem)
        {
            // Character stat service ile iletişim kurulacak;
            var stats = await CalculateItemStatsAsync(characterId, gearItem);

            // Stats'ı karaktere uygula;
            // await _characterService.ApplyStatModifiersAsync(characterId, stats);

            _logger.Debug($"Applied stats from item {gearItem.ItemId}: {stats}");
        }

        private async Task RemoveItemStatsAsync(string characterId, GearItem gearItem)
        {
            var stats = await CalculateItemStatsAsync(characterId, gearItem);

            // Stats'ı karakterden geri al;
            // await _characterService.RemoveStatModifiersAsync(characterId, stats);

            _logger.Debug($"Removed stats from item {gearItem.ItemId}: {stats}");
        }

        private async Task<GearStats> CalculateItemStatsAsync(string characterId, GearItem gearItem)
        {
            var stats = new GearStats();

            if (gearItem?.ItemData == null)
                return stats;

            // Temel item statları;
            stats.Add(gearItem.ItemData.BaseStats);

            // Dayanıklılık modifikatörü;
            var durabilityMultiplier = (float)gearItem.Durability / gearItem.ItemData.MaxDurability;
            stats.ApplyDurabilityMultiplier(durabilityMultiplier);

            // Büyü statları;
            foreach (var enchantment in gearItem.Enchantments)
            {
                if (enchantment.IsActive)
                {
                    stats.Add(enchantment.StatModifiers);
                }
            }

            // Set bonusları;
            var setBonus = await CalculateItemSetBonusAsync(characterId, gearItem);
            if (setBonus != null)
            {
                stats.Add(setBonus);
            }

            return stats;
        }

        private async Task<GearStats> CalculateAppliedStatsAsync(string characterId, GearItem gearItem)
        {
            var itemStats = await CalculateItemStatsAsync(characterId, gearItem);
            var totalStats = await CalculateTotalStatsAsync(characterId);

            // Sadece bu item'ın katkısını hesapla;
            var appliedStats = new GearStats();

            // Statların yüzdesel katkısını hesapla;
            foreach (var stat in itemStats.GetAllStats())
            {
                var totalValue = totalStats.GetStatValue(stat.Key);
                if (totalValue > 0)
                {
                    var contribution = (float)stat.Value / totalValue;
                    appliedStats.SetStat(stat.Key, stat.Value, contribution);
                }
            }

            return appliedStats;
        }

        private async Task<GearStats> CalculateSetBonusesAsync(string characterId, IEnumerable<GearItem> equippedItems)
        {
            var setBonuses = new GearStats();
            var itemsBySet = equippedItems;
                .Where(i => i.ItemData?.SetId != null)
                .GroupBy(i => i.ItemData.SetId);

            foreach (var setGroup in itemsBySet)
            {
                var setItems = setGroup.ToList();
                var setId = setGroup.Key;

                // Set bonuslarını al (ItemSetService'den)
                // var setDefinition = await _itemSetService.GetSetAsync(setId);

                // if (setDefinition != null)
                // {
                //     var applicableBonuses = setDefinition.GetBonusesForItemCount(setItems.Count);
                //     foreach (var bonus in applicableBonuses)
                //     {
                //         setBonuses.Add(bonus.Stats);
                //     }
                // }
            }

            return await Task.FromResult(setBonuses);
        }

        private async Task<GearStats> CalculateItemSetBonusAsync(string characterId, GearItem gearItem)
        {
            if (string.IsNullOrEmpty(gearItem.ItemData?.SetId))
                return null;

            // Set bonus hesaplama;
            return await Task.FromResult<GearStats>(null);
        }

        private async Task ApplyEnchantmentStatsAsync(string characterId, GearItem gearItem, Enchantment enchantment)
        {
            var stats = new GearStats();
            stats.Add(enchantment.StatModifiers);

            // Büyü statlarını uygula;
            // await _characterService.ApplyStatModifiersAsync(characterId, stats);

            _logger.Debug($"Applied enchantment {enchantment.Name} stats to item {gearItem.ItemId}");
        }

        private void ValidateParameters(string characterId, string itemId, GearSlotType slotType)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (string.IsNullOrEmpty(itemId))
                throw new ArgumentException("Item ID cannot be null or empty", nameof(itemId));

            if (!Enum.IsDefined(typeof(GearSlotType), slotType))
                throw new ArgumentException($"Invalid slot type: {slotType}", nameof(slotType));
        }

        #endregion;

        #region Events;

        /// <summary>
        /// Ekipman değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<GearChangedEventArgs> OnGearChanged;

        /// <summary>
        /// Ekipman kırıldığında tetiklenir;
        /// </summary>
        public event EventHandler<ItemBrokenEventArgs> OnItemBroken;

        /// <summary>
        /// Ekipman büyülendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ItemEnchantedEventArgs> OnItemEnchanted;

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _processingCts?.Dispose();
                    _equippedGearByCharacter.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public enum GearSlotType;
    {
        Head,
        Chest,
        Hands,
        Legs,
        Feet,
        Weapon,
        Shield,
        Necklace,
        Ring,
        Trinket;
    }

    public enum EquipStatus;
    {
        Ready,
        Processing,
        Error,
        Maintenance;
    }

    public enum EquipError;
    {
        None,
        ItemNotFound,
        ItemNotEquippable,
        SlotIncompatible,
        RequirementsNotMet,
        StatConflict,
        UnequipFailed,
        InternalError,
        SlotLocked,
        WeightExceeded;
    }

    public enum UnequipError;
    {
        None,
        ItemNotEquipped,
        NoEquippedGear,
        InternalError,
        SlotEmpty;
    }

    public enum RepairError;
    {
        None,
        ItemNotFound,
        FullyRepaired,
        CannotRepair,
        InsufficientMaterials,
        InternalError;
    }

    public enum EnchantError;
    {
        None,
        ItemNotFound,
        MaxEnchantmentsReached,
        IncompatibleEnchantment,
        InsufficientResources,
        InternalError;
    }

    public class GearItem;
    {
        public string ItemId { get; set; }
        public GearSlotType SlotType { get; set; }
        public InventoryItem ItemData { get; set; }
        public DateTime EquipTime { get; set; }
        public int Durability { get; set; }
        public List<ItemEnchantment> Enchantments { get; set; } = new List<ItemEnchantment>();

        public bool IsBroken => Durability <= 0;
        public float DurabilityPercentage => ItemData != null ? (float)Durability / ItemData.MaxDurability * 100 : 0;
    }

    public class EquipResult;
    {
        public bool Success { get; set; }
        public EquipError Error { get; set; }
        public string ErrorMessage { get; set; }
        public GearItem EquippedItem { get; set; }
        public GearStats AppliedStats { get; set; }

        public static EquipResult Success(GearItem item) => new EquipResult;
        {
            Success = true,
            EquippedItem = item;
        };

        public static EquipResult Failure(EquipError error, string message) => new EquipResult;
        {
            Success = false,
            Error = error,
            ErrorMessage = message;
        };
    }

    public class UnequipResult;
    {
        public bool Success { get; set; }
        public UnequipError Error { get; set; }
        public string ErrorMessage { get; set; }
        public GearItem UnequippedItem { get; set; }
        public bool IsSlotEmpty => UnequippedItem == null;

        public static UnequipResult Success(GearItem item) => new UnequipResult;
        {
            Success = true,
            UnequippedItem = item;
        };

        public static UnequipResult Failure(UnequipError error, string message) => new UnequipResult;
        {
            Success = false,
            Error = error,
            ErrorMessage = message;
        };

        public static UnequipResult EmptySlot() => new UnequipResult;
        {
            Success = true,
            Error = UnequipError.SlotEmpty;
        };
    }

    public class GearChangedEventArgs : EventArgs;
    {
        public string CharacterId { get; set; }
        public GearSlotType SlotType { get; set; }
        public string OldItemId { get; set; }
        public string NewItemId { get; set; }
        public DateTime ChangeTime { get; set; }
    }

    public class ItemBrokenEventArgs : EventArgs;
    {
        public string CharacterId { get; set; }
        public string ItemId { get; set; }
        public GearSlotType SlotType { get; set; }
    }

    public class ItemEnchantedEventArgs : EventArgs;
    {
        public string CharacterId { get; set; }
        public string ItemId { get; set; }
        public string EnchantmentName { get; set; }
        public int EnchantmentLevel { get; set; }
    }

    #endregion;
}
