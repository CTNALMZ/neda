using NEDA.Core.Logging;
using NEDA.Services.EventBus;
using NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.GameplaySystems.SkillSystems;
{
    /// <summary>
    /// Skill Tree sistemini yöneten ana interface;
    /// </summary>
    public interface ISkillTree;
    {
        /// <summary>
        /// Kullanıcının skill tree'sini getirir;
        /// </summary>
        Task<SkillTreeInfo> GetSkillTreeAsync(Guid userId, string treeType);

        /// <summary>
        /// Skill açar;
        /// </summary>
        Task<SkillUnlockResult> UnlockSkillAsync(Guid userId, string skillId);

        /// <summary>
        /// Skill upgrade eder;
        /// </summary>
        Task<SkillUpgradeResult> UpgradeSkillAsync(Guid userId, string skillId);

        /// <summary>
        /// Skill resetler;
        /// </summary>
        Task<SkillResetResult> ResetSkillTreeAsync(Guid userId, string treeType);

        /// <summary>
        /// Skill point ekler;
        /// </summary>
        Task AddSkillPointsAsync(Guid userId, int points);

        /// <summary>
        /// Aktif skill'leri getirir;
        /// </summary>
        Task<List<ActiveSkill>> GetActiveSkillsAsync(Guid userId);

        /// <summary>
        /// Pasif skill'leri getirir;
        /// </summary>
        Task<List<PassiveSkill>> GetPassiveSkillsAsync(Guid userId);

        /// <summary>
        /// Skill tree template'larını getirir;
        /// </summary>
        Task<Dictionary<string, SkillTreeTemplate>> GetSkillTreeTemplatesAsync();

        /// <summary>
        /// Skill kullanır;
        /// </summary>
        Task<SkillUseResult> UseSkillAsync(Guid userId, string skillId, SkillUseContext context);

        /// <summary>
        /// Skill synergy'lerini hesaplar;
        /// </summary>
        Task<List<SkillSynergy>> CalculateSynergiesAsync(Guid userId);
    }

    /// <summary>
    /// Skill Tree implementasyonu;
    /// </summary>
    public class SkillTree : ISkillTree;
    {
        private readonly ILogger<SkillTree> _logger;
        private readonly ILevelSystem _levelSystem;
        private readonly IExperienceSystem _experienceSystem;
        private readonly IEventBus _eventBus;
        private readonly ISkillRepository _skillRepository;
        private readonly ConcurrentDictionary<string, SkillTreeTemplate> _treeTemplates;
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, SkillTreeInfo>> _userSkillTrees;
        private readonly ConcurrentDictionary<string, SkillDefinition> _skillDefinitions;

        private const int MAX_SKILL_POINTS = 1000;
        private const int SKILL_RESET_COST_MULTIPLIER = 100;
        private readonly TimeSpan SKILL_COOLDOWN = TimeSpan.FromSeconds(30);

        public SkillTree(
            ILogger<SkillTree> logger,
            ILevelSystem levelSystem,
            IExperienceSystem experienceSystem,
            IEventBus eventBus,
            ISkillRepository skillRepository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _levelSystem = levelSystem ?? throw new ArgumentNullException(nameof(levelSystem));
            _experienceSystem = experienceSystem ?? throw new ArgumentNullException(nameof(experienceSystem));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _skillRepository = skillRepository ?? throw new ArgumentNullException(nameof(skillRepository));

            _treeTemplates = new ConcurrentDictionary<string, SkillTreeTemplate>();
            _userSkillTrees = new ConcurrentDictionary<Guid, ConcurrentDictionary<string, SkillTreeInfo>>();
            _skillDefinitions = new ConcurrentDictionary<string, SkillDefinition>();

            InitializeSkillDefinitions();
            InitializeSkillTreeTemplates();
            SubscribeToEvents();
        }

        /// <summary>
        /// Skill tanımlarını başlatır;
        /// </summary>
        private void InitializeSkillDefinitions()
        {
            try
            {
                // Combat Skills;
                _skillDefinitions["SLASH"] = new SkillDefinition;
                {
                    SkillId = "SLASH",
                    Name = "Slash",
                    Description = "Basic melee attack",
                    SkillType = SkillType.Active,
                    Category = SkillCategory.Combat,
                    MaxLevel = 10,
                    BaseCost = 1,
                    CostIncreasePerLevel = 1,
                    BaseCooldown = 2.0,
                    CooldownReductionPerLevel = 0.1,
                    Requirements = new SkillRequirements;
                    {
                        MinimumLevel = 1,
                        RequiredSkills = new List<string>(),
                        RequiredStats = new Dictionary<string, int> { { "STR", 5 } }
                    },
                    Effects = new List<SkillEffect>
                    {
                        new SkillEffect;
                        {
                            Type = EffectType.Damage,
                            BaseValue = 10,
                            ValueIncreasePerLevel = 5,
                            Target = EffectTarget.SingleEnemy,
                            Duration = 0;
                        }
                    }
                };

                _skillDefinitions["FIREBALL"] = new SkillDefinition;
                {
                    SkillId = "FIREBALL",
                    Name = "Fireball",
                    Description = "Launches a fiery projectile",
                    SkillType = SkillType.Active,
                    Category = SkillCategory.Magic,
                    MaxLevel = 15,
                    BaseCost = 3,
                    CostIncreasePerLevel = 2,
                    BaseCooldown = 5.0,
                    CooldownReductionPerLevel = 0.15,
                    Requirements = new SkillRequirements;
                    {
                        MinimumLevel = 5,
                        RequiredSkills = new List<string> { "BASIC_MAGIC" },
                        RequiredStats = new Dictionary<string, int> { { "INT", 10 } }
                    },
                    Effects = new List<SkillEffect>
                    {
                        new SkillEffect;
                        {
                            Type = EffectType.Damage,
                            BaseValue = 25,
                            ValueIncreasePerLevel = 8,
                            Target = EffectTarget.SingleEnemy,
                            Duration = 0;
                        },
                        new SkillEffect;
                        {
                            Type = EffectType.Burn,
                            BaseValue = 5,
                            ValueIncreasePerLevel = 2,
                            Target = EffectTarget.SingleEnemy,
                            Duration = 5;
                        }
                    }
                };

                _skillDefinitions["HEAL"] = new SkillDefinition;
                {
                    SkillId = "HEAL",
                    Name = "Heal",
                    Description = "Restores health",
                    SkillType = SkillType.Active,
                    Category = SkillCategory.Support,
                    MaxLevel = 12,
                    BaseCost = 4,
                    CostIncreasePerLevel = 1,
                    BaseCooldown = 8.0,
                    CooldownReductionPerLevel = 0.2,
                    Requirements = new SkillRequirements;
                    {
                        MinimumLevel = 3,
                        RequiredSkills = new List<string>(),
                        RequiredStats = new Dictionary<string, int> { { "WIS", 8 } }
                    },
                    Effects = new List<SkillEffect>
                    {
                        new SkillEffect;
                        {
                            Type = EffectType.Heal,
                            BaseValue = 20,
                            ValueIncreasePerLevel = 10,
                            Target = EffectTarget.SingleAlly,
                            Duration = 0;
                        }
                    }
                };

                _skillDefinitions["IRON_SKIN"] = new SkillDefinition;
                {
                    SkillId = "IRON_SKIN",
                    Name = "Iron Skin",
                    Description = "Increases defense temporarily",
                    SkillType = SkillType.Passive,
                    Category = SkillCategory.Defense,
                    MaxLevel = 8,
                    BaseCost = 2,
                    CostIncreasePerLevel = 1,
                    Requirements = new SkillRequirements;
                    {
                        MinimumLevel = 2,
                        RequiredSkills = new List<string>(),
                        RequiredStats = new Dictionary<string, int> { { "VIT", 12 } }
                    },
                    Effects = new List<SkillEffect>
                    {
                        new SkillEffect;
                        {
                            Type = EffectType.DefenseBoost,
                            BaseValue = 5,
                            ValueIncreasePerLevel = 3,
                            Target = EffectTarget.Self,
                            Duration = 0;
                        }
                    }
                };

                // Passive Skills;
                _skillDefinitions["CRITICAL_STRIKE"] = new SkillDefinition;
                {
                    SkillId = "CRITICAL_STRIKE",
                    Name = "Critical Strike",
                    Description = "Increases critical hit chance",
                    SkillType = SkillType.Passive,
                    Category = SkillCategory.Combat,
                    MaxLevel = 10,
                    BaseCost = 3,
                    CostIncreasePerLevel = 2,
                    Requirements = new SkillRequirements;
                    {
                        MinimumLevel = 10,
                        RequiredSkills = new List<string> { "SLASH", "PRECISION" },
                        RequiredStats = new Dictionary<string, int> { { "DEX", 15 }, { "STR", 10 } }
                    },
                    Effects = new List<SkillEffect>
                    {
                        new SkillEffect;
                        {
                            Type = EffectType.CriticalChance,
                            BaseValue = 2,
                            ValueIncreasePerLevel = 1,
                            Target = EffectTarget.Self,
                            Duration = 0;
                        }
                    }
                };

                _logger.LogInformation("Initialized {Count} skill definitions", _skillDefinitions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize skill definitions");
                throw new SkillTreeException("Skill definition initialization failed", ex);
            }
        }

        /// <summary>
        /// Skill tree template'larını başlatır;
        /// </summary>
        private void InitializeSkillTreeTemplates()
        {
            try
            {
                var warriorTree = new SkillTreeTemplate;
                {
                    TreeId = "WARRIOR",
                    Name = "Warrior Skill Tree",
                    Description = "Melee combat specialization",
                    Category = "Combat",
                    MaxTier = 5,
                    RootSkillId = "SLASH",
                    SkillNodes = new Dictionary<string, SkillNode>
                    {
                        ["SLASH"] = new SkillNode;
                        {
                            SkillId = "SLASH",
                            Tier = 1,
                            Position = new SkillPosition { X = 0, Y = 0 },
                            Connections = new List<string> { "HEAVY_SLASH", "WHIRLWIND" }
                        },
                        ["HEAVY_SLASH"] = new SkillNode;
                        {
                            SkillId = "HEAVY_SLASH",
                            Tier = 2,
                            Position = new SkillPosition { X = -1, Y = 1 },
                            Connections = new List<string> { "EXECUTE" }
                        },
                        ["WHIRLWIND"] = new SkillNode;
                        {
                            SkillId = "WHIRLWIND",
                            Tier = 2,
                            Position = new SkillPosition { X = 1, Y = 1 },
                            Connections = new List<string> { "BLADESTORM" }
                        },
                        ["EXECUTE"] = new SkillNode;
                        {
                            SkillId = "EXECUTE",
                            Tier = 3,
                            Position = new SkillPosition { X = -2, Y = 2 },
                            Connections = new List<string> { "MORTAL_STRIKE" }
                        },
                        ["BLADESTORM"] = new SkillNode;
                        {
                            SkillId = "BLADESTORM",
                            Tier = 3,
                            Position = new SkillPosition { X = 2, Y = 2 },
                            Connections = new List<string> { "BERSERKER_RAGE" }
                        }
                    },
                    Requirements = new TreeRequirements;
                    {
                        MinimumLevel = 1,
                        RequiredClass = "Warrior",
                        RequiredStats = new Dictionary<string, int> { { "STR", 10 } }
                    }
                };

                var mageTree = new SkillTreeTemplate;
                {
                    TreeId = "MAGE",
                    Name = "Mage Skill Tree",
                    Description = "Magic and spellcasting specialization",
                    Category = "Magic",
                    MaxTier = 5,
                    RootSkillId = "FIREBALL",
                    SkillNodes = new Dictionary<string, SkillNode>
                    {
                        ["FIREBALL"] = new SkillNode;
                        {
                            SkillId = "FIREBALL",
                            Tier = 1,
                            Position = new SkillPosition { X = 0, Y = 0 },
                            Connections = new List<string> { "FIRE_NOVA", "ICE_BOLT" }
                        },
                        ["FIRE_NOVA"] = new SkillNode;
                        {
                            SkillId = "FIRE_NOVA",
                            Tier = 2,
                            Position = new SkillPosition { X = -1, Y = 1 },
                            Connections = new List<string> { "METEOR" }
                        },
                        ["ICE_BOLT"] = new SkillNode;
                        {
                            SkillId = "ICE_BOLT",
                            Tier = 2,
                            Position = new SkillPosition { X = 1, Y = 1 },
                            Connections = new List<string> { "BLIZZARD" }
                        },
                        ["METEOR"] = new SkillNode;
                        {
                            SkillId = "METEOR",
                            Tier = 3,
                            Position = new SkillPosition { X = -2, Y = 2 },
                            Connections = new List<string> { "ARMAGEDDON" }
                        },
                        ["BLIZZARD"] = new SkillNode;
                        {
                            SkillId = "BLIZZARD",
                            Tier = 3,
                            Position = new SkillPosition { X = 2, Y = 2 },
                            Connections = new List<string> { "ABSOLUTE_ZERO" }
                        }
                    },
                    Requirements = new TreeRequirements;
                    {
                        MinimumLevel = 1,
                        RequiredClass = "Mage",
                        RequiredStats = new Dictionary<string, int> { { "INT", 10 } }
                    }
                };

                _treeTemplates["WARRIOR"] = warriorTree;
                _treeTemplates["MAGE"] = mageTree;

                _logger.LogInformation("Initialized {Count} skill tree templates", _treeTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize skill tree templates");
                throw new SkillTreeException("Skill tree template initialization failed", ex);
            }
        }

        /// <summary>
        /// Event'lara subscribe olur;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<LevelUpEvent>(OnLevelUp);
            _eventBus.Subscribe<SkillPointsEarnedEvent>(OnSkillPointsEarned);
            _eventBus.Subscribe<SkillUsedEvent>(OnSkillUsed);
            _eventBus.Subscribe<SkillResetEvent>(OnSkillReset);
        }

        public async Task<SkillTreeInfo> GetSkillTreeAsync(Guid userId, string treeType)
        {
            try
            {
                _logger.LogDebug("Getting skill tree {TreeType} for user {UserId}", treeType, userId);

                // Cache kontrolü;
                if (_userSkillTrees.TryGetValue(userId, out var userTrees) &&
                    userTrees.TryGetValue(treeType, out var cachedTree))
                {
                    return cachedTree;
                }

                // Template kontrolü;
                if (!_treeTemplates.TryGetValue(treeType, out var template))
                {
                    throw new SkillTreeException($"Skill tree template not found: {treeType}");
                }

                // Veritabanından yükle veya yeni oluştur;
                var skillTree = await LoadOrCreateSkillTreeAsync(userId, treeType, template);

                // Cache'e ekle;
                if (!_userSkillTrees.TryGetValue(userId, out userTrees))
                {
                    userTrees = new ConcurrentDictionary<string, SkillTreeInfo>();
                    _userSkillTrees[userId] = userTrees;
                }
                userTrees[treeType] = skillTree;

                return skillTree;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get skill tree {TreeType} for user {UserId}", treeType, userId);
                throw new SkillTreeException($"Failed to get skill tree {treeType} for user {userId}", ex);
            }
        }

        public async Task<SkillUnlockResult> UnlockSkillAsync(Guid userId, string skillId)
        {
            try
            {
                _logger.LogInformation("Unlocking skill {SkillId} for user {UserId}", skillId, userId);

                // Skill tanımını kontrol et;
                if (!_skillDefinitions.TryGetValue(skillId, out var skillDefinition))
                {
                    throw new SkillTreeException($"Skill definition not found: {skillId}");
                }

                // Skill tree'yi bul;
                var treeType = GetTreeTypeForSkill(skillId);
                var skillTree = await GetSkillTreeAsync(userId, treeType);

                // Validasyon;
                var validationResult = await ValidateSkillUnlockAsync(userId, skillId, skillTree, skillDefinition);
                if (!validationResult.IsValid)
                {
                    return new SkillUnlockResult;
                    {
                        Success = false,
                        SkillId = skillId,
                        ErrorMessage = validationResult.ErrorMessage,
                        ValidationErrors = validationResult.ValidationErrors;
                    };
                }

                // Skill point kontrolü;
                var skillCost = CalculateSkillCost(skillDefinition, 1); // Level 1 için cost;
                if (skillTree.AvailableSkillPoints < skillCost)
                {
                    throw new SkillTreeException($"Insufficient skill points. Required: {skillCost}, Available: {skillTree.AvailableSkillPoints}");
                }

                // Skill'i aç;
                var unlockedSkill = new UnlockedSkill;
                {
                    SkillId = skillId,
                    CurrentLevel = 1,
                    IsUnlocked = true,
                    UnlockDate = DateTime.UtcNow,
                    LastUsed = null;
                };

                skillTree.UnlockedSkills[skillId] = unlockedSkill;
                skillTree.AvailableSkillPoints -= skillCost;
                skillTree.TotalSkillPointsSpent += skillCost;

                // Parent bağlantılarını güncelle;
                UpdateSkillConnections(skillTree, skillId);

                // Veritabanını güncelle;
                await SaveSkillTreeAsync(skillTree);

                // Event publish;
                await _eventBus.PublishAsync(new SkillUnlockedEvent;
                {
                    UserId = userId,
                    SkillId = skillId,
                    SkillName = skillDefinition.Name,
                    TreeType = treeType,
                    Cost = skillCost,
                    UnlockTime = DateTime.UtcNow;
                });

                var result = new SkillUnlockResult;
                {
                    Success = true,
                    SkillId = skillId,
                    SkillName = skillDefinition.Name,
                    NewLevel = 1,
                    SkillPointsSpent = skillCost,
                    RemainingSkillPoints = skillTree.AvailableSkillPoints,
                    UnlockTime = DateTime.UtcNow;
                };

                _logger.LogInformation("Skill {SkillId} unlocked for user {UserId}. Cost: {Cost}, Remaining points: {Remaining}",
                    skillId, userId, skillCost, skillTree.AvailableSkillPoints);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unlock skill {SkillId} for user {UserId}", skillId, userId);
                throw new SkillTreeException($"Failed to unlock skill {skillId} for user {userId}", ex);
            }
        }

        public async Task<SkillUpgradeResult> UpgradeSkillAsync(Guid userId, string skillId)
        {
            try
            {
                _logger.LogInformation("Upgrading skill {SkillId} for user {UserId}", skillId, userId);

                // Skill tanımını kontrol et;
                if (!_skillDefinitions.TryGetValue(skillId, out var skillDefinition))
                {
                    throw new SkillTreeException($"Skill definition not found: {skillId}");
                }

                // Skill tree'yi bul;
                var treeType = GetTreeTypeForSkill(skillId);
                var skillTree = await GetSkillTreeAsync(userId, treeType);

                // Skill'in açık olup olmadığını kontrol et;
                if (!skillTree.UnlockedSkills.TryGetValue(skillId, out var unlockedSkill) || !unlockedSkill.IsUnlocked)
                {
                    throw new SkillTreeException($"Skill {skillId} is not unlocked");
                }

                // Max level kontrolü;
                if (unlockedSkill.CurrentLevel >= skillDefinition.MaxLevel)
                {
                    throw new SkillTreeException($"Skill {skillId} is already at max level ({skillDefinition.MaxLevel})");
                }

                // Skill point kontrolü;
                var upgradeCost = CalculateSkillCost(skillDefinition, unlockedSkill.CurrentLevel + 1);
                if (skillTree.AvailableSkillPoints < upgradeCost)
                {
                    throw new SkillTreeException($"Insufficient skill points for upgrade. Required: {upgradeCost}, Available: {skillTree.AvailableSkillPoints}");
                }

                // Skill'i upgrade et;
                unlockedSkill.CurrentLevel++;
                skillTree.AvailableSkillPoints -= upgradeCost;
                skillTree.TotalSkillPointsSpent += upgradeCost;

                // Veritabanını güncelle;
                await SaveSkillTreeAsync(skillTree);

                // Event publish;
                await _eventBus.PublishAsync(new SkillUpgradedEvent;
                {
                    UserId = userId,
                    SkillId = skillId,
                    SkillName = skillDefinition.Name,
                    NewLevel = unlockedSkill.CurrentLevel,
                    OldLevel = unlockedSkill.CurrentLevel - 1,
                    Cost = upgradeCost,
                    UpgradeTime = DateTime.UtcNow;
                });

                var result = new SkillUpgradeResult;
                {
                    Success = true,
                    SkillId = skillId,
                    SkillName = skillDefinition.Name,
                    OldLevel = unlockedSkill.CurrentLevel - 1,
                    NewLevel = unlockedSkill.CurrentLevel,
                    SkillPointsSpent = upgradeCost,
                    RemainingSkillPoints = skillTree.AvailableSkillPoints,
                    UpgradeTime = DateTime.UtcNow,
                    NewEffects = CalculateSkillEffects(skillDefinition, unlockedSkill.CurrentLevel)
                };

                _logger.LogInformation("Skill {SkillId} upgraded to level {Level} for user {UserId}. Cost: {Cost}",
                    skillId, unlockedSkill.CurrentLevel, userId, upgradeCost);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upgrade skill {SkillId} for user {UserId}", skillId, userId);
                throw new SkillTreeException($"Failed to upgrade skill {skillId} for user {userId}", ex);
            }
        }

        public async Task<SkillResetResult> ResetSkillTreeAsync(Guid userId, string treeType)
        {
            try
            {
                _logger.LogInformation("Resetting skill tree {TreeType} for user {UserId}", treeType, userId);

                var skillTree = await GetSkillTreeAsync(userId, treeType);

                // Reset cost hesapla;
                var resetCost = CalculateResetCost(skillTree);

                // Currency kontrolü (gerçek implementasyonda currency sistemi ile entegre)
                if (!await CanAffordResetAsync(userId, resetCost))
                {
                    throw new SkillTreeException($"Cannot afford skill tree reset. Cost: {resetCost}");
                }

                // Skill points'leri geri döndür;
                int pointsRefunded = skillTree.TotalSkillPointsSpent;
                skillTree.AvailableSkillPoints += pointsRefunded;
                skillTree.TotalSkillPointsSpent = 0;

                // Tüm skill'leri kapat;
                foreach (var skill in skillTree.UnlockedSkills.Values)
                {
                    skill.IsUnlocked = false;
                    skill.CurrentLevel = 0;
                    skill.UnlockDate = null;
                }

                // Cache'i temizle;
                if (_userSkillTrees.TryGetValue(userId, out var userTrees))
                {
                    userTrees.TryRemove(treeType, out _);
                }

                // Veritabanını güncelle;
                await SaveSkillTreeAsync(skillTree);

                // Event publish;
                await _eventBus.PublishAsync(new SkillTreeResetEvent;
                {
                    UserId = userId,
                    TreeType = treeType,
                    PointsRefunded = pointsRefunded,
                    ResetCost = resetCost,
                    ResetTime = DateTime.UtcNow;
                });

                var result = new SkillResetResult;
                {
                    Success = true,
                    TreeType = treeType,
                    PointsRefunded = pointsRefunded,
                    ResetCost = resetCost,
                    NewAvailablePoints = skillTree.AvailableSkillPoints,
                    ResetTime = DateTime.UtcNow;
                };

                _logger.LogInformation("Skill tree {TreeType} reset for user {UserId}. Points refunded: {Points}",
                    treeType, userId, pointsRefunded);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset skill tree {TreeType} for user {UserId}", treeType, userId);
                throw new SkillTreeException($"Failed to reset skill tree {treeType} for user {userId}", ex);
            }
        }

        public async Task AddSkillPointsAsync(Guid userId, int points)
        {
            try
            {
                if (points <= 0)
                    throw new ArgumentException("Points must be positive", nameof(points));

                _logger.LogInformation("Adding {Points} skill points to user {UserId}", points, userId);

                // Tüm tree'leri güncelle;
                if (_userSkillTrees.TryGetValue(userId, out var userTrees))
                {
                    foreach (var skillTree in userTrees.Values)
                    {
                        skillTree.AvailableSkillPoints += points;
                        skillTree.TotalSkillPointsEarned += points;
                        await SaveSkillTreeAsync(skillTree);
                    }
                }

                // Event publish;
                await _eventBus.PublishAsync(new SkillPointsAddedEvent;
                {
                    UserId = userId,
                    PointsAdded = points,
                    TotalPoints = GetTotalSkillPoints(userId),
                    AdditionTime = DateTime.UtcNow;
                });

                _logger.LogDebug("Added {Points} skill points to user {UserId}", points, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add skill points to user {UserId}", userId);
                throw new SkillTreeException($"Failed to add skill points to user {userId}", ex);
            }
        }

        public async Task<List<ActiveSkill>> GetActiveSkillsAsync(Guid userId)
        {
            try
            {
                _logger.LogDebug("Getting active skills for user {UserId}", userId);

                var activeSkills = new List<ActiveSkill>();

                if (_userSkillTrees.TryGetValue(userId, out var userTrees))
                {
                    foreach (var skillTree in userTrees.Values)
                    {
                        foreach (var unlockedSkill in skillTree.UnlockedSkills.Values)
                        {
                            if (!unlockedSkill.IsUnlocked || unlockedSkill.CurrentLevel == 0)
                                continue;

                            if (_skillDefinitions.TryGetValue(unlockedSkill.SkillId, out var definition) &&
                                definition.SkillType == SkillType.Active)
                            {
                                var effects = CalculateSkillEffects(definition, unlockedSkill.CurrentLevel);
                                var cooldown = CalculateSkillCooldown(definition, unlockedSkill.CurrentLevel);

                                activeSkills.Add(new ActiveSkill;
                                {
                                    SkillId = unlockedSkill.SkillId,
                                    Name = definition.Name,
                                    Description = definition.Description,
                                    CurrentLevel = unlockedSkill.CurrentLevel,
                                    MaxLevel = definition.MaxLevel,
                                    Cooldown = cooldown,
                                    RemainingCooldown = CalculateRemainingCooldown(unlockedSkill),
                                    Effects = effects,
                                    Category = definition.Category,
                                    Requirements = definition.Requirements,
                                    LastUsed = unlockedSkill.LastUsed;
                                });
                            }
                        }
                    }
                }

                return await Task.FromResult(activeSkills);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get active skills for user {UserId}", userId);
                throw new SkillTreeException($"Failed to get active skills for user {userId}", ex);
            }
        }

        public async Task<List<PassiveSkill>> GetPassiveSkillsAsync(Guid userId)
        {
            try
            {
                _logger.LogDebug("Getting passive skills for user {UserId}", userId);

                var passiveSkills = new List<PassiveSkill>();

                if (_userSkillTrees.TryGetValue(userId, out var userTrees))
                {
                    foreach (var skillTree in userTrees.Values)
                    {
                        foreach (var unlockedSkill in skillTree.UnlockedSkills.Values)
                        {
                            if (!unlockedSkill.IsUnlocked || unlockedSkill.CurrentLevel == 0)
                                continue;

                            if (_skillDefinitions.TryGetValue(unlockedSkill.SkillId, out var definition) &&
                                definition.SkillType == SkillType.Passive)
                            {
                                var effects = CalculateSkillEffects(definition, unlockedSkill.CurrentLevel);

                                passiveSkills.Add(new PassiveSkill;
                                {
                                    SkillId = unlockedSkill.SkillId,
                                    Name = definition.Name,
                                    Description = definition.Description,
                                    CurrentLevel = unlockedSkill.CurrentLevel,
                                    MaxLevel = definition.MaxLevel,
                                    Effects = effects,
                                    Category = definition.Category,
                                    Requirements = definition.Requirements,
                                    IsActive = true;
                                });
                            }
                        }
                    }
                }

                return await Task.FromResult(passiveSkills);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get passive skills for user {UserId}", userId);
                throw new SkillTreeException($"Failed to get passive skills for user {userId}", ex);
            }
        }

        public async Task<Dictionary<string, SkillTreeTemplate>> GetSkillTreeTemplatesAsync()
        {
            try
            {
                _logger.LogDebug("Getting skill tree templates");

                return await Task.FromResult(_treeTemplates.ToDictionary(x => x.Key, x => x.Value));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get skill tree templates");
                throw new SkillTreeException("Failed to get skill tree templates", ex);
            }
        }

        public async Task<SkillUseResult> UseSkillAsync(Guid userId, string skillId, SkillUseContext context)
        {
            try
            {
                _logger.LogInformation("User {UserId} using skill {SkillId}", userId, skillId);

                // Skill tanımını kontrol et;
                if (!_skillDefinitions.TryGetValue(skillId, out var skillDefinition))
                {
                    throw new SkillTreeException($"Skill definition not found: {skillId}");
                }

                // Active skill kontrolü;
                if (skillDefinition.SkillType != SkillType.Active)
                {
                    throw new SkillTreeException($"Skill {skillId} is not an active skill");
                }

                // Skill tree'yi bul;
                var treeType = GetTreeTypeForSkill(skillId);
                var skillTree = await GetSkillTreeAsync(userId, treeType);

                // Skill'in açık ve level'ını kontrol et;
                if (!skillTree.UnlockedSkills.TryGetValue(skillId, out var unlockedSkill) ||
                    !unlockedSkill.IsUnlocked ||
                    unlockedSkill.CurrentLevel == 0)
                {
                    throw new SkillTreeException($"Skill {skillId} is not unlocked or level 0");
                }

                // Cooldown kontrolü;
                if (IsSkillOnCooldown(unlockedSkill))
                {
                    var remaining = CalculateRemainingCooldown(unlockedSkill);
                    throw new SkillTreeException($"Skill {skillId} is on cooldown. Remaining: {remaining.TotalSeconds}s");
                }

                // Skill'i kullan;
                var effects = CalculateSkillEffects(skillDefinition, unlockedSkill.CurrentLevel);
                var cooldown = CalculateSkillCooldown(skillDefinition, unlockedSkill.CurrentLevel);

                // Cooldown'u ayarla;
                unlockedSkill.LastUsed = DateTime.UtcNow;

                // Veritabanını güncelle;
                await SaveSkillTreeAsync(skillTree);

                // Event publish;
                await _eventBus.PublishAsync(new SkillUsedEvent;
                {
                    UserId = userId,
                    SkillId = skillId,
                    SkillName = skillDefinition.Name,
                    SkillLevel = unlockedSkill.CurrentLevel,
                    Effects = effects,
                    Cooldown = cooldown,
                    UseTime = DateTime.UtcNow,
                    Context = context;
                });

                var result = new SkillUseResult;
                {
                    Success = true,
                    SkillId = skillId,
                    SkillName = skillDefinition.Name,
                    SkillLevel = unlockedSkill.CurrentLevel,
                    EffectsApplied = effects,
                    CooldownApplied = cooldown,
                    NextAvailableTime = DateTime.UtcNow.AddSeconds(cooldown),
                    UseTime = DateTime.UtcNow;
                };

                _logger.LogInformation("Skill {SkillId} used successfully by user {UserId}. Level: {Level}, Cooldown: {Cooldown}s",
                    skillId, userId, unlockedSkill.CurrentLevel, cooldown);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to use skill {SkillId} for user {UserId}", skillId, userId);
                throw new SkillTreeException($"Failed to use skill {skillId} for user {userId}", ex);
            }
        }

        public async Task<List<SkillSynergy>> CalculateSynergiesAsync(Guid userId)
        {
            try
            {
                _logger.LogDebug("Calculating skill synergies for user {UserId}", userId);

                var synergies = new List<SkillSynergy>();
                var activeSkills = await GetActiveSkillsAsync(userId);
                var passiveSkills = await GetPassiveSkillsAsync(userId);

                // Elemental synergies;
                var elementalSkills = activeSkills.Where(s =>
                    s.Category == SkillCategory.Magic &&
                    s.Name.Contains("Fire", StringComparison.OrdinalIgnoreCase)).ToList();

                if (elementalSkills.Count >= 2)
                {
                    synergies.Add(new SkillSynergy;
                    {
                        Name = "Elemental Master",
                        Description = "Using multiple elemental skills increases potency",
                        Type = SynergyType.Elemental,
                        BonusValue = 0.15m,
                        RequiredSkills = elementalSkills.Take(2).Select(s => s.SkillId).ToList(),
                        IsActive = true;
                    });
                }

                // Combat synergies;
                var combatSkills = activeSkills.Where(s =>
                    s.Category == SkillCategory.Combat &&
                    s.CurrentLevel >= 3).ToList();

                if (combatSkills.Count >= 3)
                {
                    synergies.Add(new SkillSynergy;
                    {
                        Name = "Combat Veteran",
                        Description = "Mastery of multiple combat skills increases damage",
                        Type = SynergyType.Combat,
                        BonusValue = 0.20m,
                        RequiredSkills = combatSkills.Take(3).Select(s => s.SkillId).ToList(),
                        IsActive = true;
                    });
                }

                // Support synergies;
                var supportSkills = activeSkills.Where(s =>
                    s.Category == SkillCategory.Support).ToList();

                var defenseSkills = passiveSkills.Where(s =>
                    s.Category == SkillCategory.Defense).ToList();

                if (supportSkills.Any() && defenseSkills.Any())
                {
                    synergies.Add(new SkillSynergy;
                    {
                        Name = "Guardian",
                        Description = "Combining support and defense skills increases effectiveness",
                        Type = SynergyType.Defensive,
                        BonusValue = 0.25m,
                        RequiredSkills = supportSkills.Take(1).Select(s => s.SkillId)
                            .Concat(defenseSkills.Take(1).Select(s => s.SkillId)).ToList(),
                        IsActive = true;
                    });
                }

                return await Task.FromResult(synergies);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to calculate synergies for user {UserId}", userId);
                throw new SkillTreeException($"Failed to calculate synergies for user {userId}", ex);
            }
        }

        /// <summary>
        /// Skill açma validasyonunu yapar;
        /// </summary>
        private async Task<SkillValidationResult> ValidateSkillUnlockAsync(
            Guid userId, string skillId, SkillTreeInfo skillTree, SkillDefinition skillDefinition)
        {
            var result = new SkillValidationResult;
            {
                SkillId = skillId,
                ValidationTime = DateTime.UtcNow;
            };

            // Level kontrolü;
            var levelInfo = await _levelSystem.GetLevelInfoAsync(userId);
            if (levelInfo.CurrentLevel < skillDefinition.Requirements.MinimumLevel)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Minimum level {skillDefinition.Requirements.MinimumLevel} required";
                result.ValidationErrors.Add("INSUFFICIENT_LEVEL");
            }

            // Required skills kontrolü;
            foreach (var requiredSkill in skillDefinition.Requirements.RequiredSkills)
            {
                if (!skillTree.UnlockedSkills.TryGetValue(requiredSkill, out var required) ||
                    !required.IsUnlocked ||
                    required.CurrentLevel < 1)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Required skill {requiredSkill} not unlocked";
                    result.ValidationErrors.Add($"MISSING_SKILL_{requiredSkill}");
                }
            }

            // Tree template kontrolü;
            if (_treeTemplates.TryGetValue(skillTree.TreeType, out var template))
            {
                if (template.SkillNodes.TryGetValue(skillId, out var node))
                {
                    // Parent kontrolü;
                    var hasParent = false;
                    foreach (var parentNode in template.SkillNodes.Values)
                    {
                        if (parentNode.Connections.Contains(skillId))
                        {
                            if (skillTree.UnlockedSkills.TryGetValue(parentNode.SkillId, out var parent) &&
                                parent.IsUnlocked && parent.CurrentLevel >= 1)
                            {
                                hasParent = true;
                                break;
                            }
                        }
                    }

                    if (!hasParent && skillId != template.RootSkillId)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Required parent skill not unlocked";
                        result.ValidationErrors.Add("MISSING_PARENT_SKILL");
                    }
                }
            }

            // Max skill points kontrolü;
            if (skillTree.TotalSkillPointsSpent >= MAX_SKILL_POINTS)
            {
                result.IsValid = false;
                result.ErrorMessage = "Maximum skill points reached";
                result.ValidationErrors.Add("MAX_SKILL_POINTS_REACHED");
            }

            result.IsValid = !result.ValidationErrors.Any();
            return result;
        }

        /// <summary>
        /// Skill cost hesaplar;
        /// </summary>
        private int CalculateSkillCost(SkillDefinition definition, int targetLevel)
        {
            if (targetLevel <= 1)
                return definition.BaseCost;

            int cost = definition.BaseCost;
            for (int i = 2; i <= targetLevel; i++)
            {
                cost += definition.CostIncreasePerLevel;
            }

            return cost;
        }

        /// <summary>
        /// Skill cooldown hesaplar;
        /// </summary>
        private double CalculateSkillCooldown(SkillDefinition definition, int level)
        {
            if (definition.SkillType != SkillType.Active)
                return 0;

            double reduction = definition.CooldownReductionPerLevel * (level - 1);
            return Math.Max(0.5, definition.BaseCooldown - reduction);
        }

        /// <summary>
        /// Skill effect'lerini hesaplar;
        /// </summary>
        private List<SkillEffect> CalculateSkillEffects(SkillDefinition definition, int level)
        {
            var effects = new List<SkillEffect>();

            foreach (var baseEffect in definition.Effects)
            {
                var effect = new SkillEffect;
                {
                    Type = baseEffect.Type,
                    BaseValue = baseEffect.BaseValue + (baseEffect.ValueIncreasePerLevel * (level - 1)),
                    ValueIncreasePerLevel = baseEffect.ValueIncreasePerLevel,
                    Target = baseEffect.Target,
                    Duration = baseEffect.Duration;
                };
                effects.Add(effect);
            }

            return effects;
        }

        /// <summary>
        /// Reset cost hesaplar;
        /// </summary>
        private int CalculateResetCost(SkillTreeInfo skillTree)
        {
            return skillTree.TotalSkillPointsSpent * SKILL_RESET_COST_MULTIPLIER;
        }

        /// <summary>
        /// Cooldown'da olup olmadığını kontrol eder;
        /// </summary>
        private bool IsSkillOnCooldown(UnlockedSkill skill)
        {
            if (!skill.LastUsed.HasValue || skill.LastUsed.Value == DateTime.MinValue)
                return false;

            var timeSinceUse = DateTime.UtcNow - skill.LastUsed.Value;
            return timeSinceUse < SKILL_COOLDOWN;
        }

        /// <summary>
        /// Kalan cooldown'u hesaplar;
        /// </summary>
        private TimeSpan CalculateRemainingCooldown(UnlockedSkill skill)
        {
            if (!skill.LastUsed.HasValue || !IsSkillOnCooldown(skill))
                return TimeSpan.Zero;

            var timeSinceUse = DateTime.UtcNow - skill.LastUsed.Value;
            return SKILL_COOLDOWN - timeSinceUse;
        }

        /// <summary>
        /// Skill bağlantılarını günceller;
        /// </summary>
        private void UpdateSkillConnections(SkillTreeInfo skillTree, string unlockedSkillId)
        {
            if (_treeTemplates.TryGetValue(skillTree.TreeType, out var template))
            {
                if (template.SkillNodes.TryGetValue(unlockedSkillId, out var node))
                {
                    // Child skill'leri available yap;
                    foreach (var childSkillId in node.Connections)
                    {
                        if (!skillTree.UnlockedSkills.ContainsKey(childSkillId))
                        {
                            skillTree.UnlockedSkills[childSkillId] = new UnlockedSkill;
                            {
                                SkillId = childSkillId,
                                IsUnlocked = false,
                                CurrentLevel = 0;
                            };
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Skill tree'yi yükler veya oluşturur;
        /// </summary>
        private async Task<SkillTreeInfo> LoadOrCreateSkillTreeAsync(Guid userId, string treeType, SkillTreeTemplate template)
        {
            try
            {
                var skillTree = await _skillRepository.LoadSkillTreeAsync(userId, treeType);

                if (skillTree == null)
                {
                    skillTree = new SkillTreeInfo;
                    {
                        UserId = userId,
                        TreeType = treeType,
                        AvailableSkillPoints = 0,
                        TotalSkillPointsEarned = 0,
                        TotalSkillPointsSpent = 0,
                        CreatedDate = DateTime.UtcNow,
                        UpdatedDate = DateTime.UtcNow,
                        UnlockedSkills = new ConcurrentDictionary<string, UnlockedSkill>()
                    };

                    // Root skill'i ekle;
                    if (!string.IsNullOrEmpty(template.RootSkillId))
                    {
                        skillTree.UnlockedSkills[template.RootSkillId] = new UnlockedSkill;
                        {
                            SkillId = template.RootSkillId,
                            IsUnlocked = false,
                            CurrentLevel = 0;
                        };
                    }

                    await _skillRepository.SaveSkillTreeAsync(skillTree);
                    _logger.LogDebug("Created new skill tree {TreeType} for user {UserId}", treeType, userId);
                }

                return skillTree;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load or create skill tree for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Skill tree'yi kaydeder;
        /// </summary>
        private async Task SaveSkillTreeAsync(SkillTreeInfo skillTree)
        {
            try
            {
                skillTree.UpdatedDate = DateTime.UtcNow;
                await _skillRepository.SaveSkillTreeAsync(skillTree);

                // Cache'i güncelle;
                if (_userSkillTrees.TryGetValue(skillTree.UserId, out var userTrees))
                {
                    userTrees[skillTree.TreeType] = skillTree;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save skill tree for user {UserId}", skillTree.UserId);
                throw;
            }
        }

        /// <summary>
        /// Tree type'ı skill ID'den bulur;
        /// </summary>
        private string GetTreeTypeForSkill(string skillId)
        {
            foreach (var template in _treeTemplates.Values)
            {
                if (template.SkillNodes.ContainsKey(skillId))
                    return template.TreeId;
            }

            // Default tree;
            return "WARRIOR";
        }

        /// <summary>
        /// Toplam skill points'i getirir;
        /// </summary>
        private int GetTotalSkillPoints(Guid userId)
        {
            if (_userSkillTrees.TryGetValue(userId, out var userTrees))
            {
                return userTrees.Values.Sum(t => t.AvailableSkillPoints);
            }
            return 0;
        }

        /// <summary>
        /// Reset'i karşılayıp karşılayamayacağını kontrol eder;
        /// </summary>
        private async Task<bool> CanAffordResetAsync(Guid userId, int cost)
        {
            // Gerçek implementasyonda currency sistemi ile entegre;
            await Task.CompletedTask;
            return true; // Şimdilik her zaman true;
        }

        #region Event Handlers;

        private async Task OnLevelUp(LevelUpEvent @event)
        {
            try
            {
                _logger.LogDebug("Level up event received for user {UserId}. Adding skill points", @event.UserId);

                // Level başına skill point ver;
                int pointsPerLevel = 1;
                if (@event.NewLevel >= 10) pointsPerLevel = 2;
                if (@event.NewLevel >= 30) pointsPerLevel = 3;
                if (@event.NewLevel >= 50) pointsPerLevel = 5;
                if (@event.NewLevel >= 70) pointsPerLevel = 8;
                if (@event.NewLevel >= 90) pointsPerLevel = 10;

                int pointsToAdd = pointsPerLevel * @event.LevelsGained;
                await AddSkillPointsAsync(@event.UserId, pointsToAdd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling level up event");
            }
        }

        private async Task OnSkillPointsEarned(SkillPointsEarnedEvent @event)
        {
            try
            {
                await AddSkillPointsAsync(@event.UserId, @event.Points);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling skill points earned event");
            }
        }

        private async Task OnSkillUsed(SkillUsedEvent @event)
        {
            try
            {
                // Skill kullanım analytics'i;
                _logger.LogDebug("Skill {SkillId} used by user {UserId}", @event.SkillId, @event.UserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling skill used event");
            }
        }

        private async Task OnSkillReset(SkillResetEvent @event)
        {
            try
            {
                await ResetSkillTreeAsync(@event.UserId, @event.TreeType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling skill reset event");
            }
        }

        #endregion;
    }

    /// <summary>
    /// Skill Tree bilgi modeli;
    /// </summary>
    public class SkillTreeInfo;
    {
        public Guid UserId { get; set; }
        public string TreeType { get; set; }
        public int AvailableSkillPoints { get; set; }
        public int TotalSkillPointsEarned { get; set; }
        public int TotalSkillPointsSpent { get; set; }
        public ConcurrentDictionary<string, UnlockedSkill> UnlockedSkills { get; set; } = new ConcurrentDictionary<string, UnlockedSkill>();
        public DateTime CreatedDate { get; set; }
        public DateTime UpdatedDate { get; set; }
    }

    /// <summary>
    /// Skill Tree template;
    /// </summary>
    public class SkillTreeTemplate;
    {
        public string TreeId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public int MaxTier { get; set; }
        public string RootSkillId { get; set; }
        public Dictionary<string, SkillNode> SkillNodes { get; set; } = new Dictionary<string, SkillNode>();
        public TreeRequirements Requirements { get; set; }
    }

    /// <summary>
    /// Skill node;
    /// </summary>
    public class SkillNode;
    {
        public string SkillId { get; set; }
        public int Tier { get; set; }
        public SkillPosition Position { get; set; }
        public List<string> Connections { get; set; } = new List<string>();
    }

    /// <summary>
    /// Skill position;
    /// </summary>
    public class SkillPosition;
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    /// <summary>
    /// Unlocked skill;
    /// </summary>
    public class UnlockedSkill;
    {
        public string SkillId { get; set; }
        public int CurrentLevel { get; set; }
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockDate { get; set; }
        public DateTime? LastUsed { get; set; }
    }

    /// <summary>
    /// Skill definition;
    /// </summary>
    public class SkillDefinition;
    {
        public string SkillId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public SkillType SkillType { get; set; }
        public SkillCategory Category { get; set; }
        public int MaxLevel { get; set; }
        public int BaseCost { get; set; }
        public int CostIncreasePerLevel { get; set; }
        public double BaseCooldown { get; set; }
        public double CooldownReductionPerLevel { get; set; }
        public SkillRequirements Requirements { get; set; }
        public List<SkillEffect> Effects { get; set; } = new List<SkillEffect>();
    }

    /// <summary>
    /// Skill requirements;
    /// </summary>
    public class SkillRequirements;
    {
        public int MinimumLevel { get; set; }
        public List<string> RequiredSkills { get; set; } = new List<string>();
        public Dictionary<string, int> RequiredStats { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Tree requirements;
    /// </summary>
    public class TreeRequirements;
    {
        public int MinimumLevel { get; set; }
        public string RequiredClass { get; set; }
        public Dictionary<string, int> RequiredStats { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Skill effect;
    /// </summary>
    public class SkillEffect;
    {
        public EffectType Type { get; set; }
        public double BaseValue { get; set; }
        public double ValueIncreasePerLevel { get; set; }
        public EffectTarget Target { get; set; }
        public double Duration { get; set; }
    }

    /// <summary>
    /// Active skill;
    /// </summary>
    public class ActiveSkill;
    {
        public string SkillId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int CurrentLevel { get; set; }
        public int MaxLevel { get; set; }
        public double Cooldown { get; set; }
        public TimeSpan RemainingCooldown { get; set; }
        public List<SkillEffect> Effects { get; set; } = new List<SkillEffect>();
        public SkillCategory Category { get; set; }
        public SkillRequirements Requirements { get; set; }
        public DateTime? LastUsed { get; set; }
    }

    /// <summary>
    /// Passive skill;
    /// </summary>
    public class PassiveSkill;
    {
        public string SkillId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int CurrentLevel { get; set; }
        public int MaxLevel { get; set; }
        public List<SkillEffect> Effects { get; set; } = new List<SkillEffect>();
        public SkillCategory Category { get; set; }
        public SkillRequirements Requirements { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Skill synergy;
    /// </summary>
    public class SkillSynergy;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public SynergyType Type { get; set; }
        public decimal BonusValue { get; set; }
        public List<string> RequiredSkills { get; set; } = new List<string>();
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Skill unlock result;
    /// </summary>
    public class SkillUnlockResult;
    {
        public bool Success { get; set; }
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public int NewLevel { get; set; }
        public int SkillPointsSpent { get; set; }
        public int RemainingSkillPoints { get; set; }
        public DateTime UnlockTime { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Skill upgrade result;
    /// </summary>
    public class SkillUpgradeResult;
    {
        public bool Success { get; set; }
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public int OldLevel { get; set; }
        public int NewLevel { get; set; }
        public int SkillPointsSpent { get; set; }
        public int RemainingSkillPoints { get; set; }
        public List<SkillEffect> NewEffects { get; set; } = new List<SkillEffect>();
        public DateTime UpgradeTime { get; set; }
    }

    /// <summary>
    /// Skill reset result;
    /// </summary>
    public class SkillResetResult;
    {
        public bool Success { get; set; }
        public string TreeType { get; set; }
        public int PointsRefunded { get; set; }
        public int ResetCost { get; set; }
        public int NewAvailablePoints { get; set; }
        public DateTime ResetTime { get; set; }
    }

    /// <summary>
    /// Skill validation result;
    /// </summary>
    public class SkillValidationResult;
    {
        public string SkillId { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
        public DateTime ValidationTime { get; set; }
    }

    /// <summary>
    /// Skill use result;
    /// </summary>
    public class SkillUseResult;
    {
        public bool Success { get; set; }
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public int SkillLevel { get; set; }
        public List<SkillEffect> EffectsApplied { get; set; } = new List<SkillEffect>();
        public double CooldownApplied { get; set; }
        public DateTime NextAvailableTime { get; set; }
        public DateTime UseTime { get; set; }
    }

    /// <summary>
    /// Skill use context;
    /// </summary>
    public class SkillUseContext;
    {
        public string TargetId { get; set; }
        public string Location { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Skill type enum;
    /// </summary>
    public enum SkillType;
    {
        Active,
        Passive,
        Ultimate,
        Aura;
    }

    /// <summary>
    /// Skill category enum;
    /// </summary>
    public enum SkillCategory;
    {
        Combat,
        Magic,
        Support,
        Defense,
        Utility,
        Crafting,
        Social;
    }

    /// <summary>
    /// Effect type enum;
    /// </summary>
    public enum EffectType;
    {
        Damage,
        Heal,
        Buff,
        Debuff,
        Stun,
        Slow,
        DefenseBoost,
        AttackBoost,
        CriticalChance,
        CriticalDamage,
        Burn,
        Freeze,
        Poison;
    }

    /// <summary>
    /// Effect target enum;
    /// </summary>
    public enum EffectTarget;
    {
        Self,
        SingleAlly,
        SingleEnemy,
        AreaAlly,
        AreaEnemy,
        AllAllies,
        AllEnemies;
    }

    /// <summary>
    /// Synergy type enum;
    /// </summary>
    public enum SynergyType;
    {
        Elemental,
        Combat,
        Defensive,
        Supportive,
        Utility;
    }

    #region Events;

    public class SkillUnlockedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public string TreeType { get; set; }
        public int Cost { get; set; }
        public DateTime UnlockTime { get; set; }
    }

    public class SkillUpgradedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public int NewLevel { get; set; }
        public int OldLevel { get; set; }
        public int Cost { get; set; }
        public DateTime UpgradeTime { get; set; }
    }

    public class SkillTreeResetEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public string TreeType { get; set; }
        public int PointsRefunded { get; set; }
        public int ResetCost { get; set; }
        public DateTime ResetTime { get; set; }
    }

    public class SkillPointsAddedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public int PointsAdded { get; set; }
        public int TotalPoints { get; set; }
        public DateTime AdditionTime { get; set; }
    }

    public class SkillUsedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public int SkillLevel { get; set; }
        public List<SkillEffect> Effects { get; set; }
        public double Cooldown { get; set; }
        public DateTime UseTime { get; set; }
        public SkillUseContext Context { get; set; }
    }

    // External events;
    public class SkillPointsEarnedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public int Points { get; set; }
        public string Source { get; set; }
        public DateTime EarnedTime { get; set; }
    }

    public class SkillResetEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public string TreeType { get; set; }
        public DateTime ResetTime { get; set; }
    }

    #endregion;

    /// <summary>
    /// Skill tree exception;
    /// </summary>
    public class SkillTreeException : Exception
    {
        public SkillTreeException(string message) : base(message) { }
        public SkillTreeException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Skill repository interface;
    /// </summary>
    public interface ISkillRepository;
    {
        Task<SkillTreeInfo> LoadSkillTreeAsync(Guid userId, string treeType);
        Task SaveSkillTreeAsync(SkillTreeInfo skillTree);
    }
}
