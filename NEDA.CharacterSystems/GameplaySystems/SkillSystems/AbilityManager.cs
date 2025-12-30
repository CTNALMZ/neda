using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NEDA.API.ClientSDK;
using NEDA.Common.Constants;
using NEDA.Common.Utilities;
using NEDA.Services.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.GameplaySystems.SkillSystems;
{
    /// <summary>
    /// Represents an ability that can be learned and used;
    /// </summary>
    public class Ability;
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public AbilityCategory Category { get; private set; }
        public AbilityType Type { get; private set; }
        public AbilityTargetType TargetType { get; private set; }

        public int Level { get; private set; }
        public int MaxLevel { get; private set; }
        public bool IsActive { get; private set; }
        public bool IsPassive { get; private set; }

        public float BaseValue { get; private set; }
        public float ValuePerLevel { get; private set; }
        public float CurrentValue => BaseValue + (ValuePerLevel * (Level - 1));

        public TimeSpan Cooldown { get; private set; }
        public TimeSpan CastTime { get; private set; }
        public TimeSpan Duration { get; private set; }

        public ResourceCost ResourceCost { get; private set; }
        public List<AbilityEffect> Effects { get; private set; }
        public List<AbilityRequirement> Requirements { get; private set; }
        public List<string> PrerequisiteAbilityIds { get; private set; }

        public Dictionary<string, object> Metadata { get; private set; }
        public Dictionary<string, float> ScalingParameters { get; private set; }

        public DateTime LastUsedTime { get; private set; }
        public DateTime? CooldownEndTime { get; private set; }
        public bool IsOnCooldown => CooldownEndTime.HasValue && DateTime.UtcNow < CooldownEndTime.Value;
        public TimeSpan RemainingCooldown => CooldownEndTime.HasValue ?
            CooldownEndTime.Value - DateTime.UtcNow : TimeSpan.Zero;

        public int UseCount { get; private set; }
        public float TotalDamageDealt { get; private set; }
        public float TotalHealingDone { get; private set; }

        public Ability(
            string id,
            string name,
            AbilityCategory category,
            AbilityType type,
            int maxLevel = 1)
        {
            Id = id ?? Guid.NewGuid().ToString();
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Category = category;
            Type = type;
            MaxLevel = Math.Max(1, maxLevel);
            Level = 1;

            IsPassive = type == AbilityType.Passive;
            IsActive = !IsPassive;

            Effects = new List<AbilityEffect>();
            Requirements = new List<AbilityRequirement>();
            PrerequisiteAbilityIds = new List<string>();
            Metadata = new Dictionary<string, object>();
            ScalingParameters = new Dictionary<string, float>();

            TargetType = AbilityTargetType.Self;
            Cooldown = TimeSpan.Zero;
            CastTime = TimeSpan.Zero;
            Duration = TimeSpan.Zero;

            ResourceCost = new ResourceCost();
            UseCount = 0;
        }

        public void SetLevel(int level)
        {
            Level = Math.Clamp(level, 1, MaxLevel);
        }

        public void LevelUp()
        {
            if (Level < MaxLevel)
            {
                Level++;
            }
        }

        public bool CanLevelUp(int availableSkillPoints, Dictionary<string, int> characterStats)
        {
            if (Level >= MaxLevel)
                return false;

            // Check level requirements;
            if (Requirements.Any(r => !r.IsMet(characterStats)))
                return false;

            // Check available skill points;
            var nextLevelCost = GetLevelUpCost(Level + 1);
            if (availableSkillPoints < nextLevelCost)
                return false;

            // Check prerequisites;
            return true;
        }

        public int GetLevelUpCost(int targetLevel)
        {
            if (targetLevel <= Level || targetLevel > MaxLevel)
                return 0;

            var baseCost = Metadata.TryGetValue("BaseSkillPointCost", out var costObj)
                ? Convert.ToInt32(costObj)
                : 1;

            var multiplier = Metadata.TryGetValue("LevelCostMultiplier", out var multObj)
                ? Convert.ToSingle(multObj)
                : 1.5f;

            return (int)(baseCost * Math.Pow(multiplier, targetLevel - 1));
        }

        public void Use(AbilityContext context)
        {
            if (!CanUse(context))
                throw new AbilityException($"Ability {Id} cannot be used", Id);

            LastUsedTime = DateTime.UtcNow;

            if (Cooldown > TimeSpan.Zero)
            {
                CooldownEndTime = DateTime.UtcNow.Add(Cooldown);
            }

            UseCount++;

            // Apply resource cost;
            if (context.Source != null && ResourceCost != null)
            {
                context.Source.ConsumeResources(ResourceCost);
            }
        }

        public bool CanUse(AbilityContext context)
        {
            if (!IsActive)
                return false;

            if (IsOnCooldown)
                return false;

            if (context.Source == null)
                return false;

            // Check resource requirements;
            if (!context.Source.HasEnoughResources(ResourceCost))
                return false;

            // Check ability-specific requirements;
            foreach (var requirement in Requirements)
            {
                if (!requirement.IsMet(context.Source.Stats))
                    return false;
            }

            // Check target validity;
            if (!IsValidTarget(context.Target, context.Source))
                return false;

            return true;
        }

        private bool IsValidTarget(ICharacter target, ICharacter source)
        {
            if (target == null)
                return TargetType == AbilityTargetType.None;

            return TargetType switch;
            {
                AbilityTargetType.Self => target.Id == source.Id,
                AbilityTargetType.Ally => target.Faction == source.Faction,
                AbilityTargetType.Enemy => target.Faction != source.Faction,
                AbilityTargetType.Any => true,
                AbilityTargetType.Area => true,
                AbilityTargetType.Ground => true,
                _ => true;
            };
        }

        public AbilityData GetData() => new AbilityData(this);

        public void AddEffect(AbilityEffect effect)
        {
            Effects.Add(effect);
        }

        public void AddRequirement(AbilityRequirement requirement)
        {
            Requirements.Add(requirement);
        }

        public void AddPrerequisite(string abilityId)
        {
            if (!PrerequisiteAbilityIds.Contains(abilityId))
            {
                PrerequisiteAbilityIds.Add(abilityId);
            }
        }

        public void SetResourceCost(ResourceType type, float amount)
        {
            ResourceCost = new ResourceCost(type, amount);
        }

        public void SetCooldown(TimeSpan cooldown)
        {
            Cooldown = cooldown;
        }

        public void SetCastTime(TimeSpan castTime)
        {
            CastTime = castTime;
        }

        public void SetDuration(TimeSpan duration)
        {
            Duration = duration;
        }

        public void SetTargetType(AbilityTargetType targetType)
        {
            TargetType = targetType;
        }

        public void SetBaseValue(float value)
        {
            BaseValue = value;
        }

        public void SetValuePerLevel(float value)
        {
            ValuePerLevel = value;
        }

        public void AddScalingParameter(string stat, float multiplier)
        {
            ScalingParameters[stat] = multiplier;
        }

        public float CalculateValue(Dictionary<string, float> characterStats)
        {
            var value = CurrentValue;

            foreach (var scaling in ScalingParameters)
            {
                if (characterStats.TryGetValue(scaling.Key, out var statValue))
                {
                    value += statValue * scaling.Value;
                }
            }

            return value;
        }

        public void ResetCooldown()
        {
            CooldownEndTime = null;
        }

        public void ReduceCooldown(TimeSpan reduction)
        {
            if (CooldownEndTime.HasValue)
            {
                var newEndTime = CooldownEndTime.Value - reduction;
                CooldownEndTime = newEndTime < DateTime.UtcNow ? null : newEndTime;
            }
        }

        public void RecordDamage(float amount)
        {
            TotalDamageDealt += amount;
        }

        public void RecordHealing(float amount)
        {
            TotalHealingDone += amount;
        }
    }

    /// <summary>
    /// Ability categories;
    /// </summary>
    public enum AbilityCategory;
    {
        Combat,
        Magic,
        Stealth,
        Survival,
        Crafting,
        Social,
        Movement,
        Utility,
        Special;
    }

    /// <summary>
    /// Ability types;
    /// </summary>
    public enum AbilityType;
    {
        Active,
        Passive,
        Toggle,
        Channeled,
        Ultimate;
    }

    /// <summary>
    /// Ability target types;
    /// </summary>
    public enum AbilityTargetType;
    {
        None,
        Self,
        Ally,
        Enemy,
        Any,
        Area,
        Ground;
    }

    /// <summary>
    /// Resource types for ability costs;
    /// </summary>
    public enum ResourceType;
    {
        Mana,
        Energy,
        Rage,
        Focus,
        Health,
        Stamina,
        Essence,
        Special;
    }

    /// <summary>
    /// Ability effect types;
    /// </summary>
    public enum EffectType;
    {
        Damage,
        Heal,
        Buff,
        Debuff,
        CrowdControl,
        Movement,
        Resource,
        Summon,
        Transform,
        Custom;
    }

    /// <summary>
    /// Resource cost for abilities;
    /// </summary>
    public class ResourceCost;
    {
        public ResourceType Type { get; }
        public float Amount { get; }
        public float Percentage { get; } // Percentage of max resource;

        public ResourceCost(ResourceType type = ResourceType.Mana, float amount = 0, float percentage = 0)
        {
            Type = type;
            Amount = Math.Max(0, amount);
            Percentage = Math.Clamp(percentage, 0, 100);
        }

        public bool HasCost => Amount > 0 || Percentage > 0;

        public float CalculateCost(float maxResource)
        {
            return Amount + (maxResource * Percentage / 100);
        }
    }

    /// <summary>
    /// Ability effect;
    /// </summary>
    public class AbilityEffect;
    {
        public string Id { get; }
        public EffectType Type { get; }
        public float BaseValue { get; }
        public float ValuePerLevel { get; }
        public TimeSpan Duration { get; }
        public float Chance { get; } // 0-1;
        public int MaxStacks { get; }

        public Dictionary<string, object> Parameters { get; }

        public AbilityEffect(
            EffectType type,
            float baseValue = 0,
            TimeSpan? duration = null,
            float chance = 1.0f)
        {
            Id = Guid.NewGuid().ToString();
            Type = type;
            BaseValue = baseValue;
            ValuePerLevel = 0;
            Duration = duration ?? TimeSpan.Zero;
            Chance = Math.Clamp(chance, 0, 1);
            MaxStacks = 1;
            Parameters = new Dictionary<string, object>();
        }

        public float GetValue(int abilityLevel)
        {
            return BaseValue + (ValuePerLevel * (abilityLevel - 1));
        }
    }

    /// <summary>
    /// Ability requirement;
    /// </summary>
    public class AbilityRequirement;
    {
        public string StatName { get; }
        public ComparisonType Comparison { get; }
        public float RequiredValue { get; }
        public bool IsPercentage { get; }

        public AbilityRequirement(
            string statName,
            ComparisonType comparison,
            float requiredValue,
            bool isPercentage = false)
        {
            StatName = statName ?? throw new ArgumentNullException(nameof(statName));
            Comparison = comparison;
            RequiredValue = requiredValue;
            IsPercentage = isPercentage;
        }

        public bool IsMet(Dictionary<string, int> stats)
        {
            if (!stats.TryGetValue(StatName, out var currentValue))
                return false;

            var required = IsPercentage ?
                Convert.ToInt32(RequiredValue) :
                (int)RequiredValue;

            return Comparison switch;
            {
                ComparisonType.GreaterThan => currentValue > required,
                ComparisonType.GreaterThanOrEqual => currentValue >= required,
                ComparisonType.Equal => currentValue == required,
                ComparisonType.LessThanOrEqual => currentValue <= required,
                ComparisonType.LessThan => currentValue < required,
                _ => false;
            };
        }
    }

    /// <summary>
    /// Comparison types for requirements;
    /// </summary>
    public enum ComparisonType;
    {
        GreaterThan,
        GreaterThanOrEqual,
        Equal,
        LessThanOrEqual,
        LessThan;
    }

    /// <summary>
    /// Context for ability usage;
    /// </summary>
    public class AbilityContext;
    {
        public string AbilityId { get; }
        public ICharacter Source { get; }
        public ICharacter Target { get; }
        public List<ICharacter> AdditionalTargets { get; }
        public Dictionary<string, object> Parameters { get; }
        public DateTime Timestamp { get; }

        public AbilityContext(
            string abilityId,
            ICharacter source,
            ICharacter target = null,
            Dictionary<string, object> parameters = null)
        {
            AbilityId = abilityId ?? throw new ArgumentNullException(nameof(abilityId));
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Target = target;
            AdditionalTargets = new List<ICharacter>();
            Parameters = parameters ?? new Dictionary<string, object>();
            Timestamp = DateTime.UtcNow;
        }

        public void AddTarget(ICharacter target)
        {
            if (target != null && !AdditionalTargets.Contains(target))
            {
                AdditionalTargets.Add(target);
            }
        }
    }

    /// <summary>
    /// Data transfer object for ability;
    /// </summary>
    public class AbilityData;
    {
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public AbilityCategory Category { get; }
        public AbilityType Type { get; }
        public int Level { get; }
        public int MaxLevel { get; }
        public bool IsActive { get; }
        public bool IsPassive { get; }
        public float CurrentValue { get; }
        public TimeSpan Cooldown { get; }
        public TimeSpan RemainingCooldown { get; }
        public bool IsOnCooldown { get; }
        public ResourceCost ResourceCost { get; }
        public List<string> PrerequisiteAbilityIds { get; }
        public int UseCount { get; }
        public float TotalDamageDealt { get; }
        public float TotalHealingDone { get; }

        public AbilityData(Ability ability)
        {
            Id = ability.Id;
            Name = ability.Name;
            Description = ability.Description;
            Category = ability.Category;
            Type = ability.Type;
            Level = ability.Level;
            MaxLevel = ability.MaxLevel;
            IsActive = ability.IsActive;
            IsPassive = ability.IsPassive;
            CurrentValue = ability.CurrentValue;
            Cooldown = ability.Cooldown;
            RemainingCooldown = ability.RemainingCooldown;
            IsOnCooldown = ability.IsOnCooldown;
            ResourceCost = ability.ResourceCost;
            PrerequisiteAbilityIds = ability.PrerequisiteAbilityIds;
            UseCount = ability.UseCount;
            TotalDamageDealt = ability.TotalDamageDealt;
            TotalHealingDone = ability.TotalHealingDone;
        }
    }

    /// <summary>
    /// Character interface for ability system;
    /// </summary>
    public interface ICharacter;
    {
        string Id { get; }
        string Name { get; }
        string Faction { get; }
        Dictionary<string, int> Stats { get; }
        Dictionary<string, float> Resources { get; }
        Dictionary<string, float> MaxResources { get; }

        bool HasEnoughResources(ResourceCost cost);
        void ConsumeResources(ResourceCost cost);
        void AddAbility(string abilityId);
        bool HasAbility(string abilityId);
        Ability GetAbility(string abilityId);
    }

    /// <summary>
    /// Manages character abilities;
    /// </summary>
    public interface IAbilityManager;
    {
        Task<Ability> CreateAbilityAsync(Ability ability);
        Task<Ability> GetAbilityAsync(string abilityId);
        Task<IEnumerable<Ability>> GetAbilitiesByCategoryAsync(AbilityCategory category);
        Task<IEnumerable<Ability>> GetCharacterAbilitiesAsync(string characterId);

        Task<bool> LearnAbilityAsync(string characterId, string abilityId);
        Task<bool> ForgetAbilityAsync(string characterId, string abilityId);
        Task<bool> LevelUpAbilityAsync(string characterId, string abilityId, int skillPoints);

        Task<AbilityUseResult> UseAbilityAsync(AbilityContext context);
        Task<bool> CanUseAbilityAsync(string characterId, string abilityId, string targetId = null);

        Task ResetAbilityCooldownAsync(string characterId, string abilityId);
        Task ReduceAllCooldownsAsync(string characterId, TimeSpan reduction);

        Task<IEnumerable<AbilityData>> GetAbilityDataAsync(string characterId);
        Task<Dictionary<string, float>> GetAbilityCooldownsAsync(string characterId);

        Task SaveCharacterAbilitiesAsync(string characterId);
        Task LoadCharacterAbilitiesAsync(string characterId);

        event EventHandler<AbilityLearnedEventArgs> AbilityLearned;
        event EventHandler<AbilityLeveledUpEventArgs> AbilityLeveledUp;
        event EventHandler<AbilityUsedEventArgs> AbilityUsed;
    }

    /// <summary>
    /// Result of ability usage;
    /// </summary>
    public class AbilityUseResult;
    {
        public bool Success { get; }
        public string AbilityId { get; }
        public string SourceId { get; }
        public string TargetId { get; }
        public float DamageDealt { get; }
        public float HealingDone { get; }
        public List<EffectResult> Effects { get; }
        public string ErrorMessage { get; }
        public DateTime Timestamp { get; }

        public AbilityUseResult(
            bool success,
            string abilityId,
            string sourceId,
            string targetId = null,
            float damageDealt = 0,
            float healingDone = 0,
            string errorMessage = null)
        {
            Success = success;
            AbilityId = abilityId;
            SourceId = sourceId;
            TargetId = targetId;
            DamageDealt = damageDealt;
            HealingDone = healingDone;
            Effects = new List<EffectResult>();
            ErrorMessage = errorMessage;
            Timestamp = DateTime.UtcNow;
        }

        public void AddEffect(EffectResult effect)
        {
            Effects.Add(effect);
        }
    }

    /// <summary>
    /// Effect application result;
    /// </summary>
    public class EffectResult;
    {
        public string EffectId { get; }
        public EffectType Type { get; }
        public float Value { get; }
        public bool Applied { get; }
        public string TargetId { get; }
        public TimeSpan Duration { get; }

        public EffectResult(
            string effectId,
            EffectType type,
            float value,
            bool applied,
            string targetId,
            TimeSpan duration)
        {
            EffectId = effectId;
            Type = type;
            Value = value;
            Applied = applied;
            TargetId = targetId;
            Duration = duration;
        }
    }

    /// <summary>
    /// Implementation of ability manager;
    /// </summary>
    public class AbilityManager : IAbilityManager;
    {
        private readonly ILogger<AbilityManager> _logger;
        private readonly IMemoryCache _cache;
        private readonly IEventBus _eventBus;
        private readonly ICharacterService _characterService;

        private readonly Dictionary<string, Ability> _abilities;
        private readonly Dictionary<string, List<string>> _characterAbilities;
        private readonly Dictionary<string, Dictionary<string, DateTime>> _abilityCooldowns;

        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(30);

        public event EventHandler<AbilityLearnedEventArgs> AbilityLearned;
        public event EventHandler<AbilityLeveledUpEventArgs> AbilityLeveledUp;
        public event EventHandler<AbilityUsedEventArgs> AbilityUsed;

        public AbilityManager(
            ILogger<AbilityManager> logger,
            IMemoryCache cache,
            IEventBus eventBus,
            ICharacterService characterService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _characterService = characterService ?? throw new ArgumentNullException(nameof(characterService));

            _abilities = new Dictionary<string, Ability>();
            _characterAbilities = new Dictionary<string, List<string>>();
            _abilityCooldowns = new Dictionary<string, Dictionary<string, DateTime>>();

            InitializeDefaultAbilities();
        }

        private void InitializeDefaultAbilities()
        {
            // Basic attack ability;
            var basicAttack = new Ability("basic_attack", "Basic Attack", AbilityCategory.Combat, AbilityType.Active)
            {
                Description = "A basic physical attack",
                TargetType = AbilityTargetType.Enemy,
                Cooldown = TimeSpan.FromSeconds(1.5),
                BaseValue = 10,
                ValuePerLevel = 2;
            };
            basicAttack.SetResourceCost(ResourceType.Stamina, 5);
            basicAttack.AddEffect(new AbilityEffect(EffectType.Damage, 10));

            _abilities[basicAttack.Id] = basicAttack;

            // Heal ability;
            var heal = new Ability("heal", "Heal", AbilityCategory.Magic, AbilityType.Active)
            {
                Description = "Heals a target ally",
                TargetType = AbilityTargetType.Ally,
                Cooldown = TimeSpan.FromSeconds(8),
                CastTime = TimeSpan.FromSeconds(1.5),
                BaseValue = 25,
                ValuePerLevel = 5,
                MaxLevel = 10;
            };
            heal.SetResourceCost(ResourceType.Mana, 15);
            heal.AddEffect(new AbilityEffect(EffectType.Heal, 25, TimeSpan.Zero));

            _abilities[heal.Id] = heal;

            // Passive ability example;
            var strengthPassive = new Ability("strength_passive", "Strength Training", AbilityCategory.Combat, AbilityType.Passive)
            {
                Description = "Increases physical damage by 5% per level",
                MaxLevel = 5;
            };
            strengthPassive.AddScalingParameter("Strength", 0.05f);

            _abilities[strengthPassive.Id] = strengthPassive;

            _logger.LogInformation("Initialized {Count} default abilities", _abilities.Count);
        }

        public async Task<Ability> CreateAbilityAsync(Ability ability)
        {
            if (ability == null)
                throw new ArgumentNullException(nameof(ability));

            try
            {
                _logger.LogInformation("Creating ability {AbilityId}: {Name}", ability.Id, ability.Name);

                // Validate ability;
                ValidateAbility(ability);

                // Store ability;
                _abilities[ability.Id] = ability;

                // Clear cache;
                var cacheKey = $"ability_{ability.Id}";
                _cache.Remove(cacheKey);

                _logger.LogDebug("Ability {AbilityId} created successfully", ability.Id);

                // Publish event;
                await _eventBus.PublishAsync(new AbilityCreatedEvent;
                {
                    AbilityId = ability.Id,
                    Name = ability.Name,
                    Category = ability.Category,
                    Type = ability.Type,
                    Timestamp = DateTime.UtcNow;
                });

                return ability;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ability {AbilityId}", ability.Id);
                throw new AbilityManagerException($"Failed to create ability: {ability.Id}", ex);
            }
        }

        private void ValidateAbility(Ability ability)
        {
            if (string.IsNullOrEmpty(ability.Name))
                throw new ArgumentException("Ability name cannot be null or empty");

            if (ability.MaxLevel < 1)
                throw new ArgumentException("Max level must be at least 1");

            if (ability.Cooldown < TimeSpan.Zero)
                throw new ArgumentException("Cooldown cannot be negative");

            if (ability.CastTime < TimeSpan.Zero)
                throw new ArgumentException("Cast time cannot be negative");

            if (ability.Duration < TimeSpan.Zero)
                throw new ArgumentException("Duration cannot be negative");
        }

        public async Task<Ability> GetAbilityAsync(string abilityId)
        {
            if (string.IsNullOrEmpty(abilityId))
                throw new ArgumentException("Ability ID cannot be null or empty", nameof(abilityId));

            // Try cache first;
            var cacheKey = $"ability_{abilityId}";
            if (_cache.TryGetValue<Ability>(cacheKey, out var cachedAbility))
            {
                return cachedAbility;
            }

            // Get from storage;
            if (!_abilities.TryGetValue(abilityId, out var ability))
            {
                _logger.LogWarning("Ability not found: {AbilityId}", abilityId);
                return null;
            }

            // Cache the result;
            _cache.Set(cacheKey, ability, _cacheDuration);

            return ability;
        }

        public async Task<IEnumerable<Ability>> GetAbilitiesByCategoryAsync(AbilityCategory category)
        {
            return _abilities.Values;
                .Where(a => a.Category == category)
                .OrderBy(a => a.Name);
        }

        public async Task<IEnumerable<Ability>> GetCharacterAbilitiesAsync(string characterId)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (!_characterAbilities.TryGetValue(characterId, out var abilityIds))
            {
                return Enumerable.Empty<Ability>();
            }

            var abilities = new List<Ability>();
            foreach (var abilityId in abilityIds)
            {
                var ability = await GetAbilityAsync(abilityId);
                if (ability != null)
                {
                    abilities.Add(ability);
                }
            }

            return abilities.OrderBy(a => a.Category).ThenBy(a => a.Name);
        }

        public async Task<bool> LearnAbilityAsync(string characterId, string abilityId)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (string.IsNullOrEmpty(abilityId))
                throw new ArgumentException("Ability ID cannot be null or empty", nameof(abilityId));

            try
            {
                var ability = await GetAbilityAsync(abilityId);
                if (ability == null)
                {
                    _logger.LogWarning("Ability not found for learning: {AbilityId}", abilityId);
                    return false;
                }

                // Check if already known;
                if (_characterAbilities.ContainsKey(characterId) &&
                    _characterAbilities[characterId].Contains(abilityId))
                {
                    _logger.LogWarning("Character {CharacterId} already knows ability {AbilityId}",
                        characterId, abilityId);
                    return false;
                }

                // Get character for requirement checking;
                var character = await _characterService.GetCharacterAsync(characterId);
                if (character == null)
                {
                    _logger.LogWarning("Character not found: {CharacterId}", characterId);
                    return false;
                }

                // Check prerequisites;
                if (!await CheckPrerequisitesAsync(characterId, ability))
                {
                    _logger.LogWarning("Prerequisites not met for ability {AbilityId}", abilityId);
                    return false;
                }

                // Initialize character abilities list if needed;
                if (!_characterAbilities.ContainsKey(characterId))
                {
                    _characterAbilities[characterId] = new List<string>();
                }

                // Add ability to character;
                _characterAbilities[characterId].Add(abilityId);

                // Initialize cooldown tracking if needed;
                if (!_abilityCooldowns.ContainsKey(characterId))
                {
                    _abilityCooldowns[characterId] = new Dictionary<string, DateTime>();
                }

                _logger.LogInformation("Character {CharacterId} learned ability {AbilityId}",
                    characterId, abilityId);

                // Raise event;
                var eventArgs = new AbilityLearnedEventArgs(characterId, abilityId, DateTime.UtcNow);
                OnAbilityLearned(eventArgs);

                // Publish event bus message;
                await _eventBus.PublishAsync(new AbilityLearnedEvent;
                {
                    CharacterId = characterId,
                    AbilityId = abilityId,
                    AbilityName = ability.Name,
                    Timestamp = DateTime.UtcNow;
                });

                // Save character abilities;
                await SaveCharacterAbilitiesAsync(characterId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to learn ability {AbilityId} for character {CharacterId}",
                    abilityId, characterId);
                throw new AbilityManagerException(
                    $"Failed to learn ability {abilityId} for character {characterId}", ex);
            }
        }

        private async Task<bool> CheckPrerequisitesAsync(string characterId, Ability ability)
        {
            if (ability.PrerequisiteAbilityIds.Count == 0)
                return true;

            var characterAbilities = await GetCharacterAbilitiesAsync(characterId);
            var knownAbilityIds = characterAbilities.Select(a => a.Id).ToHashSet();

            foreach (var prereqId in ability.PrerequisiteAbilityIds)
            {
                if (!knownAbilityIds.Contains(prereqId))
                {
                    return false;
                }

                var prereqAbility = await GetAbilityAsync(prereqId);
                if (prereqAbility == null || prereqAbility.Level < 1)
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> ForgetAbilityAsync(string characterId, string abilityId)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (string.IsNullOrEmpty(abilityId))
                throw new ArgumentException("Ability ID cannot be null or empty", nameof(abilityId));

            try
            {
                if (!_characterAbilities.TryGetValue(characterId, out var abilityIds))
                {
                    return false;
                }

                if (!abilityIds.Contains(abilityId))
                {
                    return false;
                }

                // Check if this ability is a prerequisite for other known abilities;
                var characterAbilities = await GetCharacterAbilitiesAsync(characterId);
                foreach (var ability in characterAbilities)
                {
                    if (ability.PrerequisiteAbilityIds.Contains(abilityId))
                    {
                        _logger.LogWarning("Cannot forget ability {AbilityId} - it's a prerequisite for {DependentAbilityId}",
                            abilityId, ability.Id);
                        return false;
                    }
                }

                // Remove ability;
                abilityIds.Remove(abilityId);

                // Remove cooldown tracking;
                if (_abilityCooldowns.TryGetValue(characterId, out var cooldowns))
                {
                    cooldowns.Remove(abilityId);
                }

                _logger.LogInformation("Character {CharacterId} forgot ability {AbilityId}",
                    characterId, abilityId);

                // Save character abilities;
                await SaveCharacterAbilitiesAsync(characterId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to forget ability {AbilityId} for character {CharacterId}",
                    abilityId, characterId);
                throw new AbilityManagerException(
                    $"Failed to forget ability {abilityId} for character {characterId}", ex);
            }
        }

        public async Task<bool> LevelUpAbilityAsync(string characterId, string abilityId, int skillPoints)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (string.IsNullOrEmpty(abilityId))
                throw new ArgumentException("Ability ID cannot be null or empty", nameof(abilityId));

            try
            {
                var ability = await GetAbilityAsync(abilityId);
                if (ability == null)
                {
                    _logger.LogWarning("Ability not found for level up: {AbilityId}", abilityId);
                    return false;
                }

                // Check if character knows this ability;
                if (!_characterAbilities.ContainsKey(characterId) ||
                    !_characterAbilities[characterId].Contains(abilityId))
                {
                    _logger.LogWarning("Character {CharacterId} doesn't know ability {AbilityId}",
                        characterId, abilityId);
                    return false;
                }

                // Get character for requirement checking;
                var character = await _characterService.GetCharacterAsync(characterId);
                if (character == null)
                {
                    _logger.LogWarning("Character not found: {CharacterId}", characterId);
                    return false;
                }

                // Check if can level up;
                if (!ability.CanLevelUp(skillPoints, character.Stats))
                {
                    _logger.LogWarning("Cannot level up ability {AbilityId} - requirements not met", abilityId);
                    return false;
                }

                var oldLevel = ability.Level;
                ability.LevelUp();

                _logger.LogInformation("Ability {AbilityId} leveled up: {OldLevel} -> {NewLevel}",
                    abilityId, oldLevel, ability.Level);

                // Raise event;
                var eventArgs = new AbilityLeveledUpEventArgs(
                    characterId, abilityId, oldLevel, ability.Level, DateTime.UtcNow);
                OnAbilityLeveledUp(eventArgs);

                // Publish event bus message;
                await _eventBus.PublishAsync(new AbilityLeveledUpEvent;
                {
                    CharacterId = characterId,
                    AbilityId = abilityId,
                    OldLevel = oldLevel,
                    NewLevel = ability.Level,
                    Timestamp = DateTime.UtcNow;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to level up ability {AbilityId} for character {CharacterId}",
                    abilityId, characterId);
                throw new AbilityManagerException(
                    $"Failed to level up ability {abilityId} for character {characterId}", ex);
            }
        }

        public async Task<AbilityUseResult> UseAbilityAsync(AbilityContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                var ability = await GetAbilityAsync(context.AbilityId);
                if (ability == null)
                {
                    return new AbilityUseResult(
                        false, context.AbilityId, context.Source.Id,
                        errorMessage: "Ability not found");
                }

                // Check if character knows this ability;
                if (!_characterAbilities.ContainsKey(context.Source.Id) ||
                    !_characterAbilities[context.Source.Id].Contains(ability.Id))
                {
                    return new AbilityUseResult(
                        false, ability.Id, context.Source.Id,
                        errorMessage: "Character doesn't know this ability");
                }

                // Check if ability can be used;
                if (!ability.CanUse(context))
                {
                    return new AbilityUseResult(
                        false, ability.Id, context.Source.Id,
                        errorMessage: "Ability cannot be used at this time");
                }

                // Use the ability;
                ability.Use(context);

                // Update cooldown tracking;
                if (!_abilityCooldowns.ContainsKey(context.Source.Id))
                {
                    _abilityCooldowns[context.Source.Id] = new Dictionary<string, DateTime>();
                }

                if (ability.Cooldown > TimeSpan.Zero)
                {
                    _abilityCooldowns[context.Source.Id][ability.Id] =
                        DateTime.UtcNow.Add(ability.Cooldown);
                }

                // Calculate ability effects;
                var result = new AbilityUseResult(
                    true, ability.Id, context.Source.Id,
                    context.Target?.Id);

                // Apply effects;
                foreach (var effect in ability.Effects)
                {
                    // Calculate effect value with level scaling;
                    var effectValue = effect.GetValue(ability.Level);

                    // Apply scaling from character stats;
                    effectValue = ability.CalculateValue(context.Source.Resources);

                    // Apply effect based on type;
                    switch (effect.Type)
                    {
                        case EffectType.Damage:
                            if (context.Target != null)
                            {
                                result.DamageDealt += effectValue;
                                ability.RecordDamage(effectValue);

                                // In a real implementation, you would apply damage to target;
                                // target.TakeDamage(effectValue, DamageType.FromAbility(ability));
                            }
                            break;

                        case EffectType.Heal:
                            if (context.Target != null)
                            {
                                result.HealingDone += effectValue;
                                ability.RecordHealing(effectValue);

                                // target.Heal(effectValue);
                            }
                            break;
                    }

                    result.AddEffect(new EffectResult(
                        effect.Id,
                        effect.Type,
                        effectValue,
                        true,
                        context.Target?.Id,
                        effect.Duration));
                }

                _logger.LogInformation(
                    "Ability used: {AbilityId} by {SourceId} on {TargetId}. Damage: {Damage}, Healing: {Healing}",
                    ability.Id, context.Source.Id, context.Target?.Id,
                    result.DamageDealt, result.HealingDone);

                // Raise event;
                var eventArgs = new AbilityUsedEventArgs(
                    context.Source.Id, ability.Id, context.Target?.Id,
                    result.DamageDealt, result.HealingDone, DateTime.UtcNow);
                OnAbilityUsed(eventArgs);

                // Publish event bus message;
                await _eventBus.PublishAsync(new AbilityUsedEvent;
                {
                    CharacterId = context.Source.Id,
                    AbilityId = ability.Id,
                    TargetId = context.Target?.Id,
                    DamageDealt = result.DamageDealt,
                    HealingDone = result.HealingDone,
                    Timestamp = DateTime.UtcNow;
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to use ability {AbilityId}", context.AbilityId);
                return new AbilityUseResult(
                    false, context.AbilityId, context.Source.Id,
                    errorMessage: $"Failed to use ability: {ex.Message}");
            }
        }

        public async Task<bool> CanUseAbilityAsync(string characterId, string abilityId, string targetId = null)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (string.IsNullOrEmpty(abilityId))
                throw new ArgumentException("Ability ID cannot be null or empty", nameof(abilityId));

            try
            {
                var ability = await GetAbilityAsync(abilityId);
                if (ability == null)
                    return false;

                // Check if character knows this ability;
                if (!_characterAbilities.ContainsKey(characterId) ||
                    !_characterAbilities[characterId].Contains(abilityId))
                    return false;

                // Get character;
                var character = await _characterService.GetCharacterAsync(characterId);
                if (character == null)
                    return false;

                // Get target if specified;
                ICharacter target = null;
                if (!string.IsNullOrEmpty(targetId))
                {
                    target = await _characterService.GetCharacterAsync(targetId);
                }

                var context = new AbilityContext(abilityId, character, target);
                return ability.CanUse(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking if ability can be used: {AbilityId}", abilityId);
                return false;
            }
        }

        public async Task ResetAbilityCooldownAsync(string characterId, string abilityId)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            if (string.IsNullOrEmpty(abilityId))
                throw new ArgumentException("Ability ID cannot be null or empty", nameof(abilityId));

            try
            {
                if (_abilityCooldowns.TryGetValue(characterId, out var cooldowns))
                {
                    cooldowns.Remove(abilityId);
                }

                var ability = await GetAbilityAsync(abilityId);
                if (ability != null)
                {
                    ability.ResetCooldown();
                }

                _logger.LogDebug("Reset cooldown for ability {AbilityId} on character {CharacterId}",
                    abilityId, characterId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset cooldown for ability {AbilityId}", abilityId);
                throw new AbilityManagerException(
                    $"Failed to reset cooldown for ability {abilityId}", ex);
            }
        }

        public async Task ReduceAllCooldownsAsync(string characterId, TimeSpan reduction)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty", nameof(characterId));

            try
            {
                if (!_abilityCooldowns.TryGetValue(characterId, out var cooldowns))
                    return;

                var updatedAbilities = new List<string>();

                foreach (var kvp in cooldowns.ToList())
                {
                    var newCooldownEnd = kvp.Value - reduction;
                    if (newCooldownEnd <= DateTime.UtcNow)
                    {
                        cooldowns.Remove(kvp.Key);
                        updatedAbilities.Add(kvp.Key);
                    }
                    else;
                    {
                        cooldowns[kvp.Key] = newCooldownEnd;
                    }
                }

                // Update ability objects;
                foreach (var abilityId in updatedAbilities)
                {
                    var ability = await GetAbilityAsync(abilityId);
                    if (ability != null)
                    {
                        ability.ReduceCooldown(reduction);
                    }
                }

                _logger.LogDebug("Reduced all cooldowns by {Reduction} for character {CharacterId}",
                    reduction, characterId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reduce cooldowns for character {CharacterId}", characterId);
                throw new AbilityManagerException(
                    $"Failed to reduce cooldowns for character {characterId}", ex);
            }
        }

        public async Task<IEnumerable<AbilityData>> GetAbilityDataAsync(string characterId)
        {
            var abilities = await GetCharacterAbilitiesAsync(characterId);
            return abilities.Select(a => a.GetData());
        }

        public async Task<Dictionary<string, float>> GetAbilityCooldownsAsync(string characterId)
        {
            if (!_abilityCooldowns.TryGetValue(characterId, out var cooldowns))
                return new Dictionary<string, float>();

            var result = new Dictionary<string, float>();
            var now = DateTime.UtcNow;

            foreach (var kvp in cooldowns)
            {
                if (kvp.Value > now)
                {
                    var remaining = (float)(kvp.Value - now).TotalSeconds;
                    result[kvp.Key] = remaining;
                }
            }

            return result;
        }

        public async Task SaveCharacterAbilitiesAsync(string characterId)
        {
            if (!_characterAbilities.TryGetValue(characterId, out var abilityIds))
                return;

            try
            {
                // In a real implementation, this would save to a database;
                // For now, we'll just log it;
                _logger.LogInformation("Saved {Count} abilities for character {CharacterId}",
                    abilityIds.Count, characterId);

                // Cache the data;
                var cacheKey = $"character_abilities_{characterId}";
                _cache.Set(cacheKey, abilityIds, TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save abilities for character {CharacterId}", characterId);
                throw new AbilityManagerException(
                    $"Failed to save abilities for character {characterId}", ex);
            }
        }

        public async Task LoadCharacterAbilitiesAsync(string characterId)
        {
            try
            {
                // In a real implementation, this would load from a database;
                // For now, we'll check cache;
                var cacheKey = $"character_abilities_{characterId}";
                if (_cache.TryGetValue<List<string>>(cacheKey, out var abilityIds))
                {
                    _characterAbilities[characterId] = abilityIds;
                    _logger.LogDebug("Loaded {Count} abilities for character {CharacterId} from cache",
                        abilityIds.Count, characterId);
                }
                else;
                {
                    // Load default abilities or from persistent storage;
                    _characterAbilities[characterId] = new List<string> { "basic_attack" };
                    _logger.LogDebug("Loaded default abilities for character {CharacterId}", characterId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load abilities for character {CharacterId}", characterId);
                throw new AbilityManagerException(
                    $"Failed to load abilities for character {characterId}", ex);
            }
        }

        protected virtual void OnAbilityLearned(AbilityLearnedEventArgs e)
        {
            AbilityLearned?.Invoke(this, e);
        }

        protected virtual void OnAbilityLeveledUp(AbilityLeveledUpEventArgs e)
        {
            AbilityLeveledUp?.Invoke(this, e);
        }

        protected virtual void OnAbilityUsed(AbilityUsedEventArgs e)
        {
            AbilityUsed?.Invoke(this, e);
        }

        public async Task<IEnumerable<Ability>> GetAvailableAbilitiesAsync(string characterId, AbilityCategory? category = null)
        {
            var character = await _characterService.GetCharacterAsync(characterId);
            if (character == null)
                return Enumerable.Empty<Ability>();

            var knownAbilityIds = _characterAbilities.ContainsKey(characterId)
                ? _characterAbilities[characterId].ToHashSet()
                : new HashSet<string>();

            var availableAbilities = new List<Ability>();

            foreach (var ability in _abilities.Values)
            {
                // Skip if already known;
                if (knownAbilityIds.Contains(ability.Id))
                    continue;

                // Filter by category if specified;
                if (category.HasValue && ability.Category != category.Value)
                    continue;

                // Check if character meets requirements;
                bool meetsRequirements = true;
                foreach (var requirement in ability.Requirements)
                {
                    if (!requirement.IsMet(character.Stats))
                    {
                        meetsRequirements = false;
                        break;
                    }
                }

                if (!meetsRequirements)
                    continue;

                // Check prerequisites;
                if (!await CheckPrerequisitesAsync(characterId, ability))
                    continue;

                availableAbilities.Add(ability);
            }

            return availableAbilities.OrderBy(a => a.Name);
        }

        public async Task<int> GetTotalSkillPointsSpentAsync(string characterId)
        {
            var abilities = await GetCharacterAbilitiesAsync(characterId);
            return abilities.Sum(a =>
                Enumerable.Range(1, a.Level)
                    .Select(level => a.GetLevelUpCost(level))
                    .Sum());
        }
    }

    /// <summary>
    /// Event arguments for ability learned;
    /// </summary>
    public class AbilityLearnedEventArgs : EventArgs;
    {
        public string CharacterId { get; }
        public string AbilityId { get; }
        public DateTime Timestamp { get; }

        public AbilityLearnedEventArgs(string characterId, string abilityId, DateTime timestamp)
        {
            CharacterId = characterId;
            AbilityId = abilityId;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Event arguments for ability leveled up;
    /// </summary>
    public class AbilityLeveledUpEventArgs : EventArgs;
    {
        public string CharacterId { get; }
        public string AbilityId { get; }
        public int OldLevel { get; }
        public int NewLevel { get; }
        public DateTime Timestamp { get; }

        public AbilityLeveledUpEventArgs(
            string characterId, string abilityId,
            int oldLevel, int newLevel, DateTime timestamp)
        {
            CharacterId = characterId;
            AbilityId = abilityId;
            OldLevel = oldLevel;
            NewLevel = newLevel;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Event arguments for ability used;
    /// </summary>
    public class AbilityUsedEventArgs : EventArgs;
    {
        public string CharacterId { get; }
        public string AbilityId { get; }
        public string TargetId { get; }
        public float DamageDealt { get; }
        public float HealingDone { get; }
        public DateTime Timestamp { get; }

        public AbilityUsedEventArgs(
            string characterId, string abilityId, string targetId,
            float damageDealt, float healingDone, DateTime timestamp)
        {
            CharacterId = characterId;
            AbilityId = abilityId;
            TargetId = targetId;
            DamageDealt = damageDealt;
            HealingDone = healingDone;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Custom exception for ability manager errors;
    /// </summary>
    public class AbilityManagerException : Exception
    {
        public string AbilityId { get; }
        public string CharacterId { get; }

        public AbilityManagerException(string message) : base(message) { }

        public AbilityManagerException(string message, Exception innerException)
            : base(message, innerException) { }

        public AbilityManagerException(string message, string abilityId, string characterId = null, Exception innerException = null)
            : base(message, innerException)
        {
            AbilityId = abilityId;
            CharacterId = characterId;
        }
    }

    public class AbilityException : Exception
    {
        public string AbilityId { get; }

        public AbilityException(string message, string abilityId) : base(message)
        {
            AbilityId = abilityId;
        }
    }

    /// <summary>
    /// Event for ability creation;
    /// </summary>
    public class AbilityCreatedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string AbilityId { get; set; }
        public string Name { get; set; }
        public AbilityCategory Category { get; set; }
        public AbilityType Type { get; set; }
    }

    /// <summary>
    /// Event for ability learned;
    /// </summary>
    public class AbilityLearnedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string CharacterId { get; set; }
        public string AbilityId { get; set; }
        public string AbilityName { get; set; }
    }

    /// <summary>
    /// Event for ability leveled up;
    /// </summary>
    public class AbilityLeveledUpEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string CharacterId { get; set; }
        public string AbilityId { get; set; }
        public int OldLevel { get; set; }
        public int NewLevel { get; set; }
    }

    /// <summary>
    /// Event for ability used;
    /// </summary>
    public class AbilityUsedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string CharacterId { get; set; }
        public string AbilityId { get; set; }
        public string TargetId { get; set; }
        public float DamageDealt { get; set; }
        public float HealingDone { get; set; }
    }

    /// <summary>
    /// Character service interface;
    /// </summary>
    public interface ICharacterService;
    {
        Task<ICharacter> GetCharacterAsync(string characterId);
        Task<bool> CharacterExistsAsync(string characterId);
        Task UpdateCharacterStatsAsync(string characterId, Dictionary<string, int> stats);
    }

    /// <summary>
    /// Factory for creating abilities;
    /// </summary>
    public class AbilityFactory;
    {
        private readonly ILogger<AbilityFactory> _logger;

        public AbilityFactory(ILogger<AbilityFactory> logger)
        {
            _logger = logger;
        }

        public Ability CreateDamageAbility(
            string name,
            AbilityCategory category,
            float baseDamage,
            float damagePerLevel = 0,
            TimeSpan cooldown = default,
            ResourceCost resourceCost = null)
        {
            var ability = new Ability(
                Guid.NewGuid().ToString(),
                name,
                category,
                AbilityType.Active);

            ability.Description = $"Deals {baseDamage} damage to target enemy";
            ability.TargetType = AbilityTargetType.Enemy;
            ability.BaseValue = baseDamage;
            ability.ValuePerLevel = damagePerLevel;
            ability.Cooldown = cooldown > TimeSpan.Zero ? cooldown : TimeSpan.FromSeconds(2);

            if (resourceCost != null)
            {
                ability.ResourceCost = resourceCost;
            }
            else;
            {
                ability.SetResourceCost(ResourceType.Mana, 10);
            }

            ability.AddEffect(new AbilityEffect(EffectType.Damage, baseDamage));

            _logger.LogDebug("Created damage ability: {Name}", name);

            return ability;
        }

        public Ability CreateHealingAbility(
            string name,
            float baseHealing,
            float healingPerLevel = 0,
            TimeSpan cooldown = default,
            ResourceCost resourceCost = null)
        {
            var ability = new Ability(
                Guid.NewGuid().ToString(),
                name,
                AbilityCategory.Magic,
                AbilityType.Active);

            ability.Description = $"Heals target ally for {baseHealing} health";
            ability.TargetType = AbilityTargetType.Ally;
            ability.BaseValue = baseHealing;
            ability.ValuePerLevel = healingPerLevel;
            ability.Cooldown = cooldown > TimeSpan.Zero ? cooldown : TimeSpan.FromSeconds(8);
            ability.CastTime = TimeSpan.FromSeconds(1.5);

            if (resourceCost != null)
            {
                ability.ResourceCost = resourceCost;
            }
            else;
            {
                ability.SetResourceCost(ResourceType.Mana, 20);
            }

            ability.AddEffect(new AbilityEffect(EffectType.Heal, baseHealing));

            _logger.LogDebug("Created healing ability: {Name}", name);

            return ability;
        }

        public Ability CreateBuffAbility(
            string name,
            string statName,
            float increaseAmount,
            TimeSpan duration,
            AbilityTargetType targetType = AbilityTargetType.Self)
        {
            var ability = new Ability(
                Guid.NewGuid().ToString(),
                name,
                AbilityCategory.Utility,
                AbilityType.Active);

            ability.Description = $"Increases {statName} by {increaseAmount} for {duration.TotalSeconds} seconds";
            ability.TargetType = targetType;
            ability.Duration = duration;
            ability.Cooldown = TimeSpan.FromSeconds(30);

            ability.SetResourceCost(ResourceType.Mana, 15);

            var effect = new AbilityEffect(EffectType.Buff, increaseAmount, duration);
            effect.Parameters["StatName"] = statName;
            ability.AddEffect(effect);

            _logger.LogDebug("Created buff ability: {Name}", name);

            return ability;
        }

        public Ability CreatePassiveAbility(
            string name,
            AbilityCategory category,
            string description,
            Dictionary<string, float> scalingParameters = null)
        {
            var ability = new Ability(
                Guid.NewGuid().ToString(),
                name,
                category,
                AbilityType.Passive);

            ability.Description = description;
            ability.MaxLevel = 5;

            if (scalingParameters != null)
            {
                foreach (var scaling in scalingParameters)
                {
                    ability.AddScalingParameter(scaling.Key, scaling.Value);
                }
            }

            _logger.LogDebug("Created passive ability: {Name}", name);

            return ability;
        }
    }

    /// <summary>
    /// Service registration extension;
    /// </summary>
    public static class AbilityManagerExtensions;
    {
        public static IServiceCollection AddAbilityManager(this IServiceCollection services)
        {
            services.AddScoped<IAbilityManager, AbilityManager>();
            services.AddSingleton<AbilityFactory>();

            services.AddMemoryCache();

            // Register event subscribers;
            services.AddTransient<IEventBusSubscriber, AbilityEventSubscriber>();

            return services;
        }
    }

    /// <summary>
    /// Event subscriber for ability events;
    /// </summary>
    public class AbilityEventSubscriber : IEventBusSubscriber;
    {
        private readonly ILogger<AbilityEventSubscriber> _logger;
        private readonly IAbilityManager _abilityManager;

        public AbilityEventSubscriber(
            ILogger<AbilityEventSubscriber> logger,
            IAbilityManager abilityManager)
        {
            _logger = logger;
            _abilityManager = abilityManager;
        }

        public void Subscribe(IEventBus eventBus)
        {
            eventBus.Subscribe<AbilityUsedEvent>(HandleAbilityUsed);
            eventBus.Subscribe<AbilityLearnedEvent>(HandleAbilityLearned);
        }

        private async Task HandleAbilityUsed(AbilityUsedEvent @event)
        {
            _logger.LogInformation("Ability used event: {@Event}", @event);

            // Example: Update combat logs, trigger achievements, etc.
            // This is where you would integrate with other systems;

            await Task.CompletedTask;
        }

        private async Task HandleAbilityLearned(AbilityLearnedEvent @event)
        {
            _logger.LogInformation("Ability learned event: {@Event}", @event);

            // Example: Send notification to player, update UI, etc.

            await Task.CompletedTask;
        }
    }
}
