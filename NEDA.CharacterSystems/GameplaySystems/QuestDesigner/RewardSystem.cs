using NEDA.CharacterSystems.GameplaySystems.InventoryManager;
using NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics;
using NEDA.CharacterSystems.GameplaySystems.SkillSystems;
using NEDA.Core.Logging;
using NEDA.Services.EventBus;
using NEDA.Services.NotificationService;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.GameplaySystems.QuestDesigner;
{
    /// <summary>
    /// Ödül sistemini yöneten ana interface;
    /// </summary>
    public interface IRewardSystem;
    {
        /// <summary>
        /// Ödül dağıtır;
        /// </summary>
        Task<RewardDistributionResult> DistributeRewardAsync(Guid userId, RewardPackage reward);

        /// <summary>
        /// Toplu ödül dağıtır;
        /// </summary>
        Task<BatchRewardResult> DistributeBatchRewardsAsync(Dictionary<Guid, RewardPackage> rewards);

        /// <summary>
        /// Kullanıcının ödül geçmişini getirir;
        /// </summary>
        Task<List<RewardHistory>> GetRewardHistoryAsync(Guid userId, DateTime? startDate = null, DateTime? endDate = null);

        /// <summary>
        /// Ödül kategorilerini getirir;
        /// </summary>
        Task<List<RewardCategory>> GetRewardCategoriesAsync();

        /// <summary>
        /// Özel ödül oluşturur;
        /// </summary>
        Task<RewardPackage> CreateCustomRewardAsync(RewardTemplate template);

        /// <summary>
        /// Ödülün geçerliliğini doğrular;
        /// </summary>
        Task<RewardValidationResult> ValidateRewardAsync(Guid userId, RewardPackage reward);

        /// <summary>
        /// Ödül takasını yönetir;
        /// </summary>
        Task<ExchangeResult> ExchangeRewardsAsync(Guid userId, ExchangeRequest request);

        /// <summary>
        /// Bonus ödül uygular;
        /// </summary>
        Task<BonusRewardResult> ApplyBonusRewardAsync(Guid userId, BonusType bonusType);
    }

    /// <summary>
    /// Reward sistemi implementasyonu;
    /// </summary>
    public class RewardSystem : IRewardSystem;
    {
        private readonly ILogger<RewardSystem> _logger;
        private readonly IInventorySystem _inventorySystem;
        private readonly ISkillSystem _skillSystem;
        private readonly IExperienceSystem _experienceSystem;
        private readonly ICurrencySystem _currencySystem;
        private readonly INotificationService _notificationService;
        private readonly IEventBus _eventBus;
        private readonly ConcurrentDictionary<string, RewardTemplate> _rewardTemplates;
        private readonly ConcurrentDictionary<Guid, List<RewardHistory>> _rewardHistoryCache;
        private readonly IRewardRepository _rewardRepository;

        private const int MAX_DAILY_REWARDS = 20;
        private const int REWARD_EXPIRY_DAYS = 30;
        private readonly TimeSpan REWARD_CLAIM_COOLDOWN = TimeSpan.FromMinutes(5);

        public RewardSystem(
            ILogger<RewardSystem> logger,
            IInventorySystem inventorySystem,
            ISkillSystem skillSystem,
            IExperienceSystem experienceSystem,
            ICurrencySystem currencySystem,
            INotificationService notificationService,
            IEventBus eventBus,
            IRewardRepository rewardRepository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _inventorySystem = inventorySystem ?? throw new ArgumentNullException(nameof(inventorySystem));
            _skillSystem = skillSystem ?? throw new ArgumentNullException(nameof(skillSystem));
            _experienceSystem = experienceSystem ?? throw new ArgumentNullException(nameof(experienceSystem));
            _currencySystem = currencySystem ?? throw new ArgumentNullException(nameof(currencySystem));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _rewardRepository = rewardRepository ?? throw new ArgumentNullException(nameof(rewardRepository));

            _rewardTemplates = new ConcurrentDictionary<string, RewardTemplate>();
            _rewardHistoryCache = new ConcurrentDictionary<Guid, List<RewardHistory>>();

            InitializeRewardTemplates();
            SubscribeToEvents();
        }

        /// <summary>
        /// Ödül şablonlarını başlatır;
        /// </summary>
        private void InitializeRewardTemplates()
        {
            try
            {
                // Basic Reward Templates;
                var templates = new[]
                {
                    new RewardTemplate;
                    {
                        TemplateId = "QUEST_COMPLETION_BASIC",
                        Name = "Basic Quest Completion",
                        Description = "Reward for completing a basic quest",
                        RewardType = RewardType.Quest,
                        BaseRewards = new RewardPackage;
                        {
                            Experience = 100,
                            Gold = 50,
                            Items = new List<RewardItem>
                            {
                                new RewardItem { ItemId = "POTION_MINOR_HEAL", Quantity = 3 }
                            }
                        },
                        MinimumLevel = 1,
                        MaximumLevel = 10,
                        Weight = 1.0m;
                    },
                    new RewardTemplate;
                    {
                        TemplateId = "QUEST_COMPLETION_ADVANCED",
                        Name = "Advanced Quest Completion",
                        Description = "Reward for completing an advanced quest",
                        RewardType = RewardType.Quest,
                        BaseRewards = new RewardPackage;
                        {
                            Experience = 500,
                            Gold = 200,
                            SkillPoints = 2,
                            Items = new List<RewardItem>
                            {
                                new RewardItem { ItemId = "POTION_MAJOR_HEAL", Quantity = 2 },
                                new RewardItem { ItemId = "SCROLL_TELEPORT", Quantity = 1 }
                            }
                        },
                        MinimumLevel = 11,
                        MaximumLevel = 30,
                        Weight = 0.7m;
                    },
                    new RewardTemplate;
                    {
                        TemplateId = "DAILY_LOGIN",
                        Name = "Daily Login Reward",
                        Description = "Reward for daily login",
                        RewardType = RewardType.Daily,
                        BaseRewards = new RewardPackage;
                        {
                            Experience = 50,
                            Gold = 100,
                            PremiumCurrency = 10,
                            Items = new List<RewardItem>
                            {
                                new RewardItem { ItemId = "CHEST_DAILY", Quantity = 1 }
                            }
                        },
                        MinimumLevel = 1,
                        MaximumLevel = 100,
                        Weight = 1.0m;
                    },
                    new RewardTemplate;
                    {
                        TemplateId = "ACHIEVEMENT_COMMON",
                        Name = "Common Achievement",
                        Description = "Reward for common achievement",
                        RewardType = RewardType.Achievement,
                        BaseRewards = new RewardPackage;
                        {
                            Experience = 200,
                            Gold = 100,
                            SkillPoints = 1;
                        },
                        MinimumLevel = 1,
                        MaximumLevel = 100,
                        Weight = 1.0m;
                    },
                    new RewardTemplate;
                    {
                        TemplateId = "PVP_VICTORY",
                        Name = "PvP Victory",
                        Description = "Reward for winning a PvP match",
                        RewardType = RewardType.PvP,
                        BaseRewards = new RewardPackage;
                        {
                            Experience = 300,
                            Gold = 150,
                            HonorPoints = 25,
                            Items = new List<RewardItem>
                            {
                                new RewardItem { ItemId = "TOKEN_PVP", Quantity = 1 }
                            }
                        },
                        MinimumLevel = 10,
                        MaximumLevel = 100,
                        Weight = 0.8m;
                    }
                };

                foreach (var template in templates)
                {
                    _rewardTemplates[template.TemplateId] = template;
                }

                _logger.LogInformation("Initialized {Count} reward templates", _rewardTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize reward templates");
                throw new RewardSystemException("Reward template initialization failed", ex);
            }
        }

        /// <summary>
        /// Event'lara subscribe olur;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<QuestCompletedEvent>(OnQuestCompleted);
            _eventBus.Subscribe<AchievementUnlockedEvent>(OnAchievementUnlocked);
            _eventBus.Subscribe<DailyLoginEvent>(OnDailyLogin);
            _eventBus.Subscribe<PvpMatchEndedEvent>(OnPvpMatchEnded);
            _eventBus.Subscribe<SeasonEndedEvent>(OnSeasonEnded);
        }

        public async Task<RewardDistributionResult> DistributeRewardAsync(Guid userId, RewardPackage reward)
        {
            try
            {
                _logger.LogInformation("Distributing reward to user {UserId}", userId);

                // Validasyon;
                var validationResult = await ValidateRewardAsync(userId, reward);
                if (!validationResult.IsValid)
                {
                    throw new RewardSystemException($"Reward validation failed: {validationResult.ErrorMessage}");
                }

                // Cooldown kontrolü;
                if (await IsInCooldownAsync(userId))
                {
                    throw new RewardSystemException($"User {userId} is in reward cooldown");
                }

                // Günlük limit kontrolü;
                if (!await CanReceiveMoreRewardsTodayAsync(userId))
                {
                    throw new RewardSystemException($"User {userId} has reached daily reward limit");
                }

                var result = new RewardDistributionResult;
                {
                    UserId = userId,
                    RewardId = reward.RewardId,
                    DistributionTime = DateTime.UtcNow;
                };

                // Experience ödülü;
                if (reward.Experience > 0)
                {
                    var expResult = await _experienceSystem.AddExperienceAsync(userId, reward.Experience, ExperienceSource.Reward);
                    result.AppliedRewards.Experience = reward.Experience;
                    result.Messages.Add($"Gained {reward.Experience} experience");
                }

                // Skill point ödülü;
                if (reward.SkillPoints > 0)
                {
                    var skillResult = await _skillSystem.AddSkillPointsAsync(userId, reward.SkillPoints);
                    result.AppliedRewards.SkillPoints = reward.SkillPoints;
                    result.Messages.Add($"Gained {reward.SkillPoints} skill points");
                }

                // Item ödülleri;
                if (reward.Items != null && reward.Items.Any())
                {
                    var itemResults = new List<ItemDistributionResult>();

                    foreach (var rewardItem in reward.Items)
                    {
                        var itemResult = await _inventorySystem.AddItemAsync(userId, rewardItem.ItemId, rewardItem.Quantity);

                        itemResults.Add(new ItemDistributionResult;
                        {
                            ItemId = rewardItem.ItemId,
                            Quantity = rewardItem.Quantity,
                            Success = itemResult.Success,
                            Message = itemResult.Message;
                        });

                        if (itemResult.Success)
                        {
                            result.AppliedRewards.Items.Add(rewardItem);
                            result.Messages.Add($"Received {rewardItem.Quantity}x {rewardItem.ItemId}");
                        }
                    }

                    result.ItemDistributionResults = itemResults;
                }

                // Currency ödülleri;
                if (reward.Gold > 0)
                {
                    await _currencySystem.AddGoldAsync(userId, reward.Gold);
                    result.AppliedRewards.Gold = reward.Gold;
                    result.Messages.Add($"Received {reward.Gold} gold");
                }

                if (reward.PremiumCurrency > 0)
                {
                    await _currencySystem.AddPremiumCurrencyAsync(userId, reward.PremiumCurrency);
                    result.AppliedRewards.PremiumCurrency = reward.PremiumCurrency;
                    result.Messages.Add($"Received {reward.PremiumCurrency} premium currency");
                }

                // Özel currency'ler;
                if (reward.CustomCurrencies != null)
                {
                    foreach (var currency in reward.CustomCurrencies)
                    {
                        await _currencySystem.AddCustomCurrencyAsync(userId, currency.CurrencyType, currency.Amount);
                        result.AppliedRewards.CustomCurrencies.Add(currency);
                        result.Messages.Add($"Received {currency.Amount} {currency.CurrencyType}");
                    }
                }

                // Geçmişe kaydet;
                await AddToRewardHistoryAsync(userId, reward, result);

                // Notification gönder;
                await SendRewardNotificationAsync(userId, reward, result);

                // Event publish;
                await _eventBus.PublishAsync(new RewardDistributedEvent;
                {
                    UserId = userId,
                    RewardPackage = reward,
                    DistributionTime = DateTime.UtcNow,
                    Success = true;
                });

                result.Success = true;
                result.TotalValue = CalculateRewardValue(reward);

                _logger.LogInformation("Successfully distributed reward to user {UserId}. Total value: {Value}",
                    userId, result.TotalValue);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to distribute reward to user {UserId}", userId);

                // Event publish (failure)
                await _eventBus.PublishAsync(new RewardDistributionFailedEvent;
                {
                    UserId = userId,
                    RewardPackage = reward,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });

                throw new RewardSystemException($"Failed to distribute reward to user {userId}", ex);
            }
        }

        public async Task<BatchRewardResult> DistributeBatchRewardsAsync(Dictionary<Guid, RewardPackage> rewards)
        {
            try
            {
                _logger.LogInformation("Distributing batch rewards to {Count} users", rewards.Count);

                var results = new List<RewardDistributionResult>();
                var failedUsers = new List<Guid>();

                foreach (var kvp in rewards)
                {
                    try
                    {
                        var result = await DistributeRewardAsync(kvp.Key, kvp.Value);
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to distribute reward to user {UserId}", kvp.Key);
                        failedUsers.Add(kvp.Key);
                    }
                }

                var batchResult = new BatchRewardResult;
                {
                    TotalUsers = rewards.Count,
                    SuccessfulDistributions = results.Count,
                    FailedDistributions = failedUsers.Count,
                    FailedUserIds = failedUsers,
                    IndividualResults = results,
                    TotalValue = results.Sum(r => r.TotalValue)
                };

                _logger.LogInformation("Batch distribution completed: {Success}/{Total} successful",
                    batchResult.SuccessfulDistributions, batchResult.TotalUsers);

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch reward distribution failed");
                throw new RewardSystemException("Batch reward distribution failed", ex);
            }
        }

        public async Task<List<RewardHistory>> GetRewardHistoryAsync(Guid userId, DateTime? startDate = null, DateTime? endDate = null)
        {
            try
            {
                _logger.LogDebug("Getting reward history for user {UserId}", userId);

                // Cache kontrolü;
                if (_rewardHistoryCache.TryGetValue(userId, out var cachedHistory))
                {
                    return FilterHistoryByDate(cachedHistory, startDate, endDate);
                }

                // Veritabanından yükle;
                var history = await _rewardRepository.GetRewardHistoryAsync(userId);

                // Cache'e ekle;
                _rewardHistoryCache[userId] = history;

                return FilterHistoryByDate(history, startDate, endDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get reward history for user {UserId}", userId);
                throw new RewardSystemException($"Failed to get reward history for user {userId}", ex);
            }
        }

        public async Task<List<RewardCategory>> GetRewardCategoriesAsync()
        {
            try
            {
                _logger.LogDebug("Getting reward categories");

                var categories = new List<RewardCategory>
                {
                    new RewardCategory;
                    {
                        CategoryId = "QUEST",
                        Name = "Quest Rewards",
                        Description = "Rewards from completing quests",
                        Icon = "quest_icon",
                        Color = "#4CAF50"
                    },
                    new RewardCategory;
                    {
                        CategoryId = "DAILY",
                        Name = "Daily Rewards",
                        Description = "Daily login and daily quest rewards",
                        Icon = "daily_icon",
                        Color = "#2196F3"
                    },
                    new RewardCategory;
                    {
                        CategoryId = "ACHIEVEMENT",
                        Name = "Achievement Rewards",
                        Description = "Rewards from unlocking achievements",
                        Icon = "achievement_icon",
                        Color = "#FF9800"
                    },
                    new RewardCategory;
                    {
                        CategoryId = "PVP",
                        Name = "PvP Rewards",
                        Description = "Rewards from player vs player activities",
                        Icon = "pvp_icon",
                        Color = "#F44336"
                    },
                    new RewardCategory;
                    {
                        CategoryId = "SEASONAL",
                        Name = "Seasonal Rewards",
                        Description = "Seasonal event and battle pass rewards",
                        Icon = "seasonal_icon",
                        Color = "#9C27B0"
                    },
                    new RewardCategory;
                    {
                        CategoryId = "REFERRAL",
                        Name = "Referral Rewards",
                        Description = "Rewards for referring new players",
                        Icon = "referral_icon",
                        Color = "#00BCD4"
                    },
                    new RewardCategory;
                    {
                        CategoryId = "COMPENSATION",
                        Name = "Compensation",
                        Description = "Compensation for bugs or maintenance",
                        Icon = "compensation_icon",
                        Color = "#607D8B"
                    }
                };

                return await Task.FromResult(categories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get reward categories");
                throw new RewardSystemException("Failed to get reward categories", ex);
            }
        }

        public async Task<RewardPackage> CreateCustomRewardAsync(RewardTemplate template)
        {
            try
            {
                _logger.LogInformation("Creating custom reward from template {TemplateId}", template.TemplateId);

                // Template'den ödül oluştur;
                var reward = new RewardPackage;
                {
                    RewardId = Guid.NewGuid(),
                    TemplateId = template.TemplateId,
                    Name = template.Name,
                    Description = template.Description,
                    RewardType = template.RewardType,
                    Experience = template.BaseRewards.Experience,
                    Gold = template.BaseRewards.Gold,
                    SkillPoints = template.BaseRewards.SkillPoints,
                    PremiumCurrency = template.BaseRewards.PremiumCurrency,
                    HonorPoints = template.BaseRewards.HonorPoints,
                    Reputation = template.BaseRewards.Reputation,
                    Items = template.BaseRewards.Items?.Select(i => new RewardItem;
                    {
                        ItemId = i.ItemId,
                        Quantity = i.Quantity,
                        Quality = i.Quality;
                    }).ToList(),
                    CustomCurrencies = template.BaseRewards.CustomCurrencies?.Select(c => new CustomCurrency;
                    {
                        CurrencyType = c.CurrencyType,
                        Amount = c.Amount;
                    }).ToList(),
                    CreatedDate = DateTime.UtcNow,
                    ExpiryDate = DateTime.UtcNow.AddDays(template.ExpiryDays)
                };

                // Level scaling uygula;
                reward = ApplyLevelScaling(reward, template);

                // Rarity bonus uygula;
                if (template.Rarity != RewardRarity.Common)
                {
                    reward = ApplyRarityBonus(reward, template.Rarity);
                }

                // Randomization uygula;
                if (template.HasRandomization)
                {
                    reward = ApplyRandomization(reward, template);
                }

                _logger.LogDebug("Created custom reward {RewardId} from template {TemplateId}",
                    reward.RewardId, template.TemplateId);

                return await Task.FromResult(reward);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create custom reward from template {TemplateId}", template.TemplateId);
                throw new RewardSystemException($"Failed to create custom reward from template {template.TemplateId}", ex);
            }
        }

        public async Task<RewardValidationResult> ValidateRewardAsync(Guid userId, RewardPackage reward)
        {
            try
            {
                _logger.LogDebug("Validating reward {RewardId} for user {UserId}", reward.RewardId, userId);

                var result = new RewardValidationResult;
                {
                    RewardId = reward.RewardId,
                    UserId = userId,
                    ValidationTime = DateTime.UtcNow;
                };

                // Expiry kontrolü;
                if (reward.ExpiryDate.HasValue && reward.ExpiryDate < DateTime.UtcNow)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Reward has expired";
                    result.ValidationErrors.Add("EXPIRED");
                    return result;
                }

                // Max limit kontrolü;
                if (reward.Experience > 1000000)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Experience reward exceeds maximum limit";
                    result.ValidationErrors.Add("EXPERIENCE_LIMIT_EXCEEDED");
                }

                if (reward.Gold > 1000000)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Gold reward exceeds maximum limit";
                    result.ValidationErrors.Add("GOLD_LIMIT_EXCEEDED");
                }

                // Duplicate claim kontrolü;
                if (await IsRewardAlreadyClaimedAsync(userId, reward.RewardId))
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Reward has already been claimed";
                    result.ValidationErrors.Add("ALREADY_CLAIMED");
                }

                // Item availability kontrolü;
                if (reward.Items != null)
                {
                    foreach (var item in reward.Items)
                    {
                        if (!await _inventorySystem.IsItemAvailableAsync(item.ItemId))
                        {
                            result.IsValid = false;
                            result.ErrorMessage = $"Item {item.ItemId} is not available";
                            result.ValidationErrors.Add($"ITEM_UNAVAILABLE_{item.ItemId}");
                        }
                    }
                }

                result.IsValid = !result.ValidationErrors.Any();

                _logger.LogDebug("Reward validation for {RewardId}: {IsValid}",
                    reward.RewardId, result.IsValid);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate reward {RewardId}", reward.RewardId);
                throw new RewardSystemException($"Failed to validate reward {reward.RewardId}", ex);
            }
        }

        public async Task<ExchangeResult> ExchangeRewardsAsync(Guid userId, ExchangeRequest request)
        {
            try
            {
                _logger.LogInformation("Processing exchange request for user {UserId}", userId);

                var result = new ExchangeResult;
                {
                    UserId = userId,
                    ExchangeId = Guid.NewGuid(),
                    RequestTime = DateTime.UtcNow;
                };

                // Required items kontrolü;
                foreach (var requiredItem in request.RequiredItems)
                {
                    var itemCount = await _inventorySystem.GetItemCountAsync(userId, requiredItem.ItemId);
                    if (itemCount < requiredItem.Quantity)
                    {
                        throw new RewardSystemException(
                            $"Insufficient items for exchange. Required: {requiredItem.Quantity}x {requiredItem.ItemId}, Available: {itemCount}");
                    }
                }

                // Required currency kontrolü;
                foreach (var requiredCurrency in request.RequiredCurrencies)
                {
                    var currencyBalance = await _currencySystem.GetBalanceAsync(userId, requiredCurrency.CurrencyType);
                    if (currencyBalance < requiredCurrency.Amount)
                    {
                        throw new RewardSystemException(
                            $"Insufficient currency for exchange. Required: {requiredCurrency.Amount} {requiredCurrency.CurrencyType}");
                    }
                }

                // Required items'i kaldır;
                foreach (var requiredItem in request.RequiredItems)
                {
                    await _inventorySystem.RemoveItemAsync(userId, requiredItem.ItemId, requiredItem.Quantity);
                    result.CostItems.Add(requiredItem);
                }

                // Required currency'i kaldır;
                foreach (var requiredCurrency in request.RequiredCurrencies)
                {
                    await _currencySystem.RemoveCurrencyAsync(userId, requiredCurrency.CurrencyType, requiredCurrency.Amount);
                    result.CostCurrencies.Add(requiredCurrency);
                }

                // Ödül dağıt;
                var rewardResult = await DistributeRewardAsync(userId, request.Reward);
                result.RewardDistributionResult = rewardResult;
                result.Success = true;
                result.ExchangeTime = DateTime.UtcNow;

                // Event publish;
                await _eventBus.PublishAsync(new RewardExchangedEvent;
                {
                    UserId = userId,
                    ExchangeId = result.ExchangeId,
                    RequiredItems = request.RequiredItems,
                    RequiredCurrencies = request.RequiredCurrencies,
                    Reward = request.Reward,
                    ExchangeTime = DateTime.UtcNow;
                });

                _logger.LogInformation("Exchange completed for user {UserId}. Exchange ID: {ExchangeId}",
                    userId, result.ExchangeId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process exchange for user {UserId}", userId);
                throw new RewardSystemException($"Failed to process exchange for user {userId}", ex);
            }
        }

        public async Task<BonusRewardResult> ApplyBonusRewardAsync(Guid userId, BonusType bonusType)
        {
            try
            {
                _logger.LogInformation("Applying bonus reward of type {BonusType} to user {UserId}",
                    bonusType, userId);

                var bonusTemplate = GetBonusTemplate(bonusType);
                var baseReward = await CreateCustomRewardAsync(bonusTemplate);

                // Bonus multiplier uygula;
                var bonusReward = ApplyBonusMultiplier(baseReward, bonusTemplate.BonusMultiplier);

                // Ödül dağıt;
                var distributionResult = await DistributeRewardAsync(userId, bonusReward);

                var result = new BonusRewardResult;
                {
                    UserId = userId,
                    BonusType = bonusType,
                    BaseReward = baseReward,
                    BonusReward = bonusReward,
                    DistributionResult = distributionResult,
                    BonusMultiplier = bonusTemplate.BonusMultiplier,
                    AppliedTime = DateTime.UtcNow;
                };

                // Event publish;
                await _eventBus.PublishAsync(new BonusRewardAppliedEvent;
                {
                    UserId = userId,
                    BonusType = bonusType,
                    Reward = bonusReward,
                    Multiplier = bonusTemplate.BonusMultiplier,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Bonus reward applied to user {UserId}. Total value: {Value}",
                    userId, distributionResult.TotalValue);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply bonus reward to user {UserId}", userId);
                throw new RewardSystemException($"Failed to apply bonus reward to user {userId}", ex);
            }
        }

        /// <summary>
        /// Level scaling uygular;
        /// </summary>
        private RewardPackage ApplyLevelScaling(RewardPackage reward, RewardTemplate template)
        {
            // Bu metod gerçek implementasyonda user level'ına göre scaling yapar;
            // Şimdilik basit bir scaling uyguluyoruz;
            var scaledReward = reward.Clone();

            if (template.MinimumLevel > 1)
            {
                double scaleFactor = 1.0 + (template.MinimumLevel * 0.1);
                scaledReward.Experience = (int)(scaledReward.Experience * scaleFactor);
                scaledReward.Gold = (int)(scaledReward.Gold * scaleFactor);
                scaledReward.SkillPoints = (int)(scaledReward.SkillPoints * scaleFactor);
            }

            return scaledReward;
        }

        /// <summary>
        /// Rarity bonus uygular;
        /// </summary>
        private RewardPackage ApplyRarityBonus(RewardPackage reward, RewardRarity rarity)
        {
            var bonusReward = reward.Clone();
            double multiplier = GetRarityMultiplier(rarity);

            bonusReward.Experience = (int)(bonusReward.Experience * multiplier);
            bonusReward.Gold = (int)(bonusReward.Gold * multiplier);
            bonusReward.SkillPoints = (int)(bonusReward.SkillPoints * multiplier);

            if (bonusReward.PremiumCurrency > 0)
            {
                bonusReward.PremiumCurrency = (int)(bonusReward.PremiumCurrency * multiplier);
            }

            return bonusReward;
        }

        /// <summary>
        /// Randomization uygular;
        /// </summary>
        private RewardPackage ApplyRandomization(RewardPackage reward, RewardTemplate template)
        {
            var random = new Random();
            var randomizedReward = reward.Clone();

            // Experience randomization;
            if (template.ExperienceRandomizationRange > 0)
            {
                int min = (int)(reward.Experience * (1 - template.ExperienceRandomizationRange));
                int max = (int)(reward.Experience * (1 + template.ExperienceRandomizationRange));
                randomizedReward.Experience = random.Next(min, max + 1);
            }

            // Gold randomization;
            if (template.GoldRandomizationRange > 0)
            {
                int min = (int)(reward.Gold * (1 - template.GoldRandomizationRange));
                int max = (int)(reward.Gold * (1 + template.GoldRandomizationRange));
                randomizedReward.Gold = random.Next(min, max + 1);
            }

            // Item randomization;
            if (template.ItemRandomization && reward.Items != null)
            {
                foreach (var item in randomizedReward.Items)
                {
                    if (item.Quantity > 1)
                    {
                        int min = Math.Max(1, (int)(item.Quantity * 0.5));
                        int max = (int)(item.Quantity * 1.5);
                        item.Quantity = random.Next(min, max + 1);
                    }
                }
            }

            return randomizedReward;
        }

        /// <summary>
        /// Bonus multiplier uygular;
        /// </summary>
        private RewardPackage ApplyBonusMultiplier(RewardPackage reward, decimal multiplier)
        {
            var bonusReward = reward.Clone();

            bonusReward.Experience = (int)(bonusReward.Experience * (double)multiplier);
            bonusReward.Gold = (int)(bonusReward.Gold * (double)multiplier);
            bonusReward.SkillPoints = (int)(bonusReward.SkillPoints * (double)multiplier);

            if (bonusReward.PremiumCurrency > 0)
            {
                bonusReward.PremiumCurrency = (int)(bonusReward.PremiumCurrency * (double)multiplier);
            }

            return bonusReward;
        }

        /// <summary>
        /// Ödül değerini hesaplar;
        /// </summary>
        private decimal CalculateRewardValue(RewardPackage reward)
        {
            decimal value = 0;

            value += reward.Experience * 0.01m; // 100 XP = 1 value unit;
            value += reward.Gold * 0.001m; // 1000 gold = 1 value unit;
            value += reward.SkillPoints * 10m;
            value += reward.PremiumCurrency * 1m;
            value += reward.HonorPoints * 0.1m;
            value += reward.Reputation * 0.05m;

            if (reward.Items != null)
            {
                foreach (var item in reward.Items)
                {
                    // Gerçek implementasyonda item değeri database'den alınır;
                    value += item.Quantity * 5m;
                }
            }

            if (reward.CustomCurrencies != null)
            {
                foreach (var currency in reward.CustomCurrencies)
                {
                    value += currency.Amount * 0.5m;
                }
            }

            return value;
        }

        /// <summary>
        /// Cooldown'da olup olmadığını kontrol eder;
        /// </summary>
        private async Task<bool> IsInCooldownAsync(Guid userId)
        {
            var history = await GetRewardHistoryAsync(userId);
            var lastReward = history.OrderByDescending(h => h.ReceivedTime).FirstOrDefault();

            if (lastReward == null) return false;

            var timeSinceLastReward = DateTime.UtcNow - lastReward.ReceivedTime;
            return timeSinceLastReward < REWARD_CLAIM_COOLDOWN;
        }

        /// <summary>
        /// Daha fazla ödül alıp alamayacağını kontrol eder;
        /// </summary>
        private async Task<bool> CanReceiveMoreRewardsTodayAsync(Guid userId)
        {
            var history = await GetRewardHistoryAsync(userId);
            var today = DateTime.UtcNow.Date;
            var todayRewards = history.Count(h => h.ReceivedTime.Date == today);

            return todayRewards < MAX_DAILY_REWARDS;
        }

        /// <summary>
        /// Ödülün daha önce alınıp alınmadığını kontrol eder;
        /// </summary>
        private async Task<bool> IsRewardAlreadyClaimedAsync(Guid userId, Guid rewardId)
        {
            var history = await GetRewardHistoryAsync(userId);
            return history.Any(h => h.RewardId == rewardId);
        }

        /// <summary>
        /// Geçmişi tarihe göre filtreler;
        /// </summary>
        private List<RewardHistory> FilterHistoryByDate(List<RewardHistory> history, DateTime? startDate, DateTime? endDate)
        {
            if (!startDate.HasValue && !endDate.HasValue)
                return history;

            return history.Where(h =>
                (!startDate.HasValue || h.ReceivedTime >= startDate.Value) &&
                (!endDate.HasValue || h.ReceivedTime <= endDate.Value)
            ).ToList();
        }

        /// <summary>
        /// Rarity multiplier'ı alır;
        /// </summary>
        private double GetRarityMultiplier(RewardRarity rarity)
        {
            return rarity switch;
            {
                RewardRarity.Common => 1.0,
                RewardRarity.Uncommon => 1.3,
                RewardRarity.Rare => 1.7,
                RewardRarity.Epic => 2.2,
                RewardRarity.Legendary => 3.0,
                RewardRarity.Mythic => 4.0,
                _ => 1.0;
            };
        }

        /// <summary>
        /// Bonus template'ı alır;
        /// </summary>
        private RewardTemplate GetBonusTemplate(BonusType bonusType)
        {
            var templateId = bonusType switch;
            {
                BonusType.FirstPurchase => "BONUS_FIRST_PURCHASE",
                BonusType.StreakBonus => "BONUS_STREAK",
                BonusType.VIPBonus => "BONUS_VIP",
                BonusType.EventBonus => "BONUS_EVENT",
                BonusType.ReferralBonus => "BONUS_REFERRAL",
                BonusType.Anniversary => "BONUS_ANNIVERSARY",
                _ => "BONUS_DEFAULT"
            };

            if (!_rewardTemplates.TryGetValue(templateId, out var template))
            {
                template = _rewardTemplates.Values.First();
            }

            return template;
        }

        /// <summary>
        /// Geçmişe ödül ekler;
        /// </summary>
        private async Task AddToRewardHistoryAsync(Guid userId, RewardPackage reward, RewardDistributionResult result)
        {
            var history = new RewardHistory;
            {
                HistoryId = Guid.NewGuid(),
                UserId = userId,
                RewardId = reward.RewardId,
                TemplateId = reward.TemplateId,
                RewardType = reward.RewardType,
                Experience = result.AppliedRewards.Experience,
                Gold = result.AppliedRewards.Gold,
                SkillPoints = result.AppliedRewards.SkillPoints,
                PremiumCurrency = result.AppliedRewards.PremiumCurrency,
                Items = result.AppliedRewards.Items,
                CustomCurrencies = result.AppliedRewards.CustomCurrencies,
                TotalValue = result.TotalValue,
                ReceivedTime = DateTime.UtcNow,
                DistributionResult = result.Success ? "SUCCESS" : "FAILED"
            };

            await _rewardRepository.AddRewardHistoryAsync(history);

            // Cache'i güncelle;
            if (_rewardHistoryCache.TryGetValue(userId, out var cachedHistory))
            {
                cachedHistory.Add(history);
            }
        }

        /// <summary>
        /// Ödül notification'ı gönderir;
        /// </summary>
        private async Task SendRewardNotificationAsync(Guid userId, RewardPackage reward, RewardDistributionResult result)
        {
            try
            {
                var notification = new RewardNotification;
                {
                    UserId = userId,
                    Title = "Reward Received!",
                    Message = $"You have received a reward: {reward.Name}",
                    RewardDetails = result.Messages,
                    TotalValue = result.TotalValue,
                    SentTime = DateTime.UtcNow;
                };

                await _notificationService.SendNotificationAsync(userId, notification);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send reward notification to user {UserId}", userId);
            }
        }

        #region Event Handlers;

        private async Task OnQuestCompleted(QuestCompletedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing quest completed event for user {UserId}", @event.UserId);

                var template = GetQuestRewardTemplate(@event.QuestDifficulty, @event.QuestLevel);
                var reward = await CreateCustomRewardAsync(template);

                await DistributeRewardAsync(@event.UserId, reward);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling quest completed event");
            }
        }

        private async Task OnAchievementUnlocked(AchievementUnlockedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing achievement unlocked event for user {UserId}", @event.UserId);

                var template = GetAchievementRewardTemplate(@event.AchievementRarity);
                var reward = await CreateCustomRewardAsync(template);

                await DistributeRewardAsync(@event.UserId, reward);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling achievement unlocked event");
            }
        }

        private async Task OnDailyLogin(DailyLoginEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing daily login event for user {UserId}", @event.UserId);

                var template = _rewardTemplates["DAILY_LOGIN"];
                var reward = await CreateCustomRewardAsync(template);

                // Streak bonus uygula;
                if (@event.LoginStreak > 1)
                {
                    reward = ApplyBonusMultiplier(reward, 1.0m + (@event.LoginStreak * 0.05m));
                }

                await DistributeRewardAsync(@event.UserId, reward);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling daily login event");
            }
        }

        private async Task OnPvpMatchEnded(PvpMatchEndedEvent @event)
        {
            try
            {
                if (@event.IsWinner)
                {
                    _logger.LogDebug("Processing PvP victory event for user {UserId}", @event.UserId);

                    var template = _rewardTemplates["PVP_VICTORY"];
                    var reward = await CreateCustomRewardAsync(template);

                    await DistributeRewardAsync(@event.UserId, reward);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling PvP match ended event");
            }
        }

        private async Task OnSeasonEnded(SeasonEndedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing season ended event for user {UserId}", @event.UserId);

                // Season rank'a göre ödül ver;
                var reward = CreateSeasonReward(@event.SeasonRank, @event.Ranking);
                await DistributeRewardAsync(@event.UserId, reward);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling season ended event");
            }
        }

        private RewardTemplate GetQuestRewardTemplate(string difficulty, int questLevel)
        {
            return difficulty switch;
            {
                "EASY" => _rewardTemplates["QUEST_COMPLETION_BASIC"],
                "MEDIUM" => _rewardTemplates["QUEST_COMPLETION_ADVANCED"],
                "HARD" => AdjustTemplateForDifficulty(_rewardTemplates["QUEST_COMPLETION_ADVANCED"], 2.0m),
                "EPIC" => AdjustTemplateForDifficulty(_rewardTemplates["QUEST_COMPLETION_ADVANCED"], 3.5m),
                "LEGENDARY" => AdjustTemplateForDifficulty(_rewardTemplates["QUEST_COMPLETION_ADVANCED"], 5.0m),
                _ => _rewardTemplates["QUEST_COMPLETION_BASIC"]
            };
        }

        private RewardTemplate GetAchievementRewardTemplate(string rarity)
        {
            return rarity switch;
            {
                "COMMON" => _rewardTemplates["ACHIEVEMENT_COMMON"],
                "RARE" => AdjustTemplateForRarity(_rewardTemplates["ACHIEVEMENT_COMMON"], RewardRarity.Rare),
                "EPIC" => AdjustTemplateForRarity(_rewardTemplates["ACHIEVEMENT_COMMON"], RewardRarity.Epic),
                "LEGENDARY" => AdjustTemplateForRarity(_rewardTemplates["ACHIEVEMENT_COMMON"], RewardRarity.Legendary),
                _ => _rewardTemplates["ACHIEVEMENT_COMMON"]
            };
        }

        private RewardTemplate AdjustTemplateForDifficulty(RewardTemplate template, decimal multiplier)
        {
            var adjusted = template.Clone();
            adjusted.BaseRewards.Experience = (int)(adjusted.BaseRewards.Experience * multiplier);
            adjusted.BaseRewards.Gold = (int)(adjusted.BaseRewards.Gold * multiplier);
            adjusted.BaseRewards.SkillPoints = (int)(adjusted.BaseRewards.SkillPoints * multiplier);
            return adjusted;
        }

        private RewardTemplate AdjustTemplateForRarity(RewardTemplate template, RewardRarity rarity)
        {
            var adjusted = template.Clone();
            adjusted.Rarity = rarity;
            return adjusted;
        }

        private RewardPackage CreateSeasonReward(string seasonRank, int ranking)
        {
            var reward = new RewardPackage;
            {
                RewardId = Guid.NewGuid(),
                Name = $"Season {seasonRank} Rewards",
                Description = $"Rewards for achieving {seasonRank} rank in season",
                RewardType = RewardType.Seasonal,
                CreatedDate = DateTime.UtcNow,
                ExpiryDate = DateTime.UtcNow.AddDays(REWARD_EXPIRY_DAYS)
            };

            // Ranking'e göre ödül miktarlarını belirle;
            switch (seasonRank)
            {
                case "BRONZE":
                    reward.Experience = 1000;
                    reward.Gold = 500;
                    reward.PremiumCurrency = 50;
                    break;
                case "SILVER":
                    reward.Experience = 2500;
                    reward.Gold = 1250;
                    reward.PremiumCurrency = 100;
                    reward.SkillPoints = 2;
                    break;
                case "GOLD":
                    reward.Experience = 5000;
                    reward.Gold = 2500;
                    reward.PremiumCurrency = 200;
                    reward.SkillPoints = 5;
                    break;
                case "PLATINUM":
                    reward.Experience = 10000;
                    reward.Gold = 5000;
                    reward.PremiumCurrency = 500;
                    reward.SkillPoints = 10;
                    break;
                case "DIAMOND":
                    reward.Experience = 20000;
                    reward.Gold = 10000;
                    reward.PremiumCurrency = 1000;
                    reward.SkillPoints = 20;
                    break;
                case "MASTER":
                    reward.Experience = 50000;
                    reward.Gold = 25000;
                    reward.PremiumCurrency = 2500;
                    reward.SkillPoints = 50;
                    break;
                case "GRANDMASTER":
                    reward.Experience = 100000;
                    reward.Gold = 50000;
                    reward.PremiumCurrency = 5000;
                    reward.SkillPoints = 100;
                    break;
            }

            // Top ranking için ekstra ödül;
            if (ranking <= 100)
            {
                reward.CustomCurrencies = new List<CustomCurrency>
                {
                    new CustomCurrency { CurrencyType = "TOKEN_ELITE", Amount = ranking <= 10 ? 10 : 5 }
                };

                if (ranking <= 10)
                {
                    reward.Items = new List<RewardItem>
                    {
                        new RewardItem { ItemId = "TITLE_TOP10", Quantity = 1, Quality = "EPIC" }
                    };
                }
            }

            return reward;
        }

        #endregion;
    }

    /// <summary>
    /// Ödül paketi modeli;
    /// </summary>
    public class RewardPackage;
    {
        public Guid RewardId { get; set; }
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public RewardType RewardType { get; set; }
        public int Experience { get; set; }
        public int Gold { get; set; }
        public int SkillPoints { get; set; }
        public int PremiumCurrency { get; set; }
        public int HonorPoints { get; set; }
        public int Reputation { get; set; }
        public List<RewardItem> Items { get; set; } = new List<RewardItem>();
        public List<CustomCurrency> CustomCurrencies { get; set; } = new List<CustomCurrency>();
        public DateTime CreatedDate { get; set; }
        public DateTime? ExpiryDate { get; set; }

        public RewardPackage Clone()
        {
            return new RewardPackage;
            {
                RewardId = Guid.NewGuid(),
                TemplateId = this.TemplateId,
                Name = this.Name,
                Description = this.Description,
                RewardType = this.RewardType,
                Experience = this.Experience,
                Gold = this.Gold,
                SkillPoints = this.SkillPoints,
                PremiumCurrency = this.PremiumCurrency,
                HonorPoints = this.HonorPoints,
                Reputation = this.Reputation,
                Items = this.Items?.Select(i => i.Clone()).ToList(),
                CustomCurrencies = this.CustomCurrencies?.Select(c => c.Clone()).ToList(),
                CreatedDate = DateTime.UtcNow,
                ExpiryDate = this.ExpiryDate;
            };
        }
    }

    /// <summary>
    /// Ödül item modeli;
    /// </summary>
    public class RewardItem;
    {
        public string ItemId { get; set; }
        public int Quantity { get; set; }
        public string Quality { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public RewardItem Clone()
        {
            return new RewardItem;
            {
                ItemId = this.ItemId,
                Quantity = this.Quantity,
                Quality = this.Quality,
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    /// <summary>
    /// Custom currency modeli;
    /// </summary>
    public class CustomCurrency;
    {
        public string CurrencyType { get; set; }
        public int Amount { get; set; }

        public CustomCurrency Clone()
        {
            return new CustomCurrency;
            {
                CurrencyType = this.CurrencyType,
                Amount = this.Amount;
            };
        }
    }

    /// <summary>
    /// Ödül şablonu;
    /// </summary>
    public class RewardTemplate;
    {
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public RewardType RewardType { get; set; }
        public RewardRarity Rarity { get; set; } = RewardRarity.Common;
        public RewardPackage BaseRewards { get; set; }
        public int MinimumLevel { get; set; } = 1;
        public int MaximumLevel { get; set; } = 100;
        public int ExpiryDays { get; set; } = 7;
        public decimal Weight { get; set; } = 1.0m;
        public decimal BonusMultiplier { get; set; } = 1.0m;
        public bool HasRandomization { get; set; }
        public double ExperienceRandomizationRange { get; set; }
        public double GoldRandomizationRange { get; set; }
        public bool ItemRandomization { get; set; }

        public RewardTemplate Clone()
        {
            return new RewardTemplate;
            {
                TemplateId = this.TemplateId,
                Name = this.Name,
                Description = this.Description,
                RewardType = this.RewardType,
                Rarity = this.Rarity,
                BaseRewards = this.BaseRewards?.Clone(),
                MinimumLevel = this.MinimumLevel,
                MaximumLevel = this.MaximumLevel,
                ExpiryDays = this.ExpiryDays,
                Weight = this.Weight,
                BonusMultiplier = this.BonusMultiplier,
                HasRandomization = this.HasRandomization,
                ExperienceRandomizationRange = this.ExperienceRandomizationRange,
                GoldRandomizationRange = this.GoldRandomizationRange,
                ItemRandomization = this.ItemRandomization;
            };
        }
    }

    /// <summary>
    /// Ödül kategorisi;
    /// </summary>
    public class RewardCategory;
    {
        public string CategoryId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }
        public string Color { get; set; }
    }

    /// <summary>
    /// Ödül geçmişi;
    /// </summary>
    public class RewardHistory;
    {
        public Guid HistoryId { get; set; }
        public Guid UserId { get; set; }
        public Guid RewardId { get; set; }
        public string TemplateId { get; set; }
        public RewardType RewardType { get; set; }
        public int Experience { get; set; }
        public int Gold { get; set; }
        public int SkillPoints { get; set; }
        public int PremiumCurrency { get; set; }
        public List<RewardItem> Items { get; set; } = new List<RewardItem>();
        public List<CustomCurrency> CustomCurrencies { get; set; } = new List<CustomCurrency>();
        public decimal TotalValue { get; set; }
        public DateTime ReceivedTime { get; set; }
        public string DistributionResult { get; set; }
    }

    /// <summary>
    /// Ödül dağıtım sonucu;
    /// </summary>
    public class RewardDistributionResult;
    {
        public Guid UserId { get; set; }
        public Guid RewardId { get; set; }
        public bool Success { get; set; }
        public DateTime DistributionTime { get; set; }
        public decimal TotalValue { get; set; }
        public AppliedRewards AppliedRewards { get; set; } = new AppliedRewards();
        public List<ItemDistributionResult> ItemDistributionResults { get; set; } = new List<ItemDistributionResult>();
        public List<string> Messages { get; set; } = new List<string>();
    }

    /// <summary>
    /// Uygulanan ödüller;
    /// </summary>
    public class AppliedRewards;
    {
        public int Experience { get; set; }
        public int Gold { get; set; }
        public int SkillPoints { get; set; }
        public int PremiumCurrency { get; set; }
        public List<RewardItem> Items { get; set; } = new List<RewardItem>();
        public List<CustomCurrency> CustomCurrencies { get; set; } = new List<CustomCurrency>();
    }

    /// <summary>
    /// Item dağıtım sonucu;
    /// </summary>
    public class ItemDistributionResult;
    {
        public string ItemId { get; set; }
        public int Quantity { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Toplu ödül sonucu;
    /// </summary>
    public class BatchRewardResult;
    {
        public int TotalUsers { get; set; }
        public int SuccessfulDistributions { get; set; }
        public int FailedDistributions { get; set; }
        public List<Guid> FailedUserIds { get; set; } = new List<Guid>();
        public List<RewardDistributionResult> IndividualResults { get; set; } = new List<RewardDistributionResult>();
        public decimal TotalValue { get; set; }
        public DateTime CompletionTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Ödül validasyon sonucu;
    /// </summary>
    public class RewardValidationResult;
    {
        public Guid RewardId { get; set; }
        public Guid UserId { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
        public DateTime ValidationTime { get; set; }
    }

    /// <summary>
    /// Takas isteği;
    /// </summary>
    public class ExchangeRequest;
    {
        public Guid RequestId { get; set; }
        public List<RewardItem> RequiredItems { get; set; } = new List<RewardItem>();
        public List<CustomCurrency> RequiredCurrencies { get; set; } = new List<CustomCurrency>();
        public RewardPackage Reward { get; set; }
        public DateTime RequestTime { get; set; }
    }

    /// <summary>
    /// Takas sonucu;
    /// </summary>
    public class ExchangeResult;
    {
        public Guid UserId { get; set; }
        public Guid ExchangeId { get; set; }
        public bool Success { get; set; }
        public DateTime RequestTime { get; set; }
        public DateTime? ExchangeTime { get; set; }
        public List<RewardItem> CostItems { get; set; } = new List<RewardItem>();
        public List<CustomCurrency> CostCurrencies { get; set; } = new List<CustomCurrency>();
        public RewardDistributionResult RewardDistributionResult { get; set; }
    }

    /// <summary>
    /// Bonus ödül sonucu;
    /// </summary>
    public class BonusRewardResult;
    {
        public Guid UserId { get; set; }
        public BonusType BonusType { get; set; }
        public RewardPackage BaseReward { get; set; }
        public RewardPackage BonusReward { get; set; }
        public RewardDistributionResult DistributionResult { get; set; }
        public decimal BonusMultiplier { get; set; }
        public DateTime AppliedTime { get; set; }
    }

    /// <summary>
    /// Ödül tipi enum'u;
    /// </summary>
    public enum RewardType;
    {
        Quest,
        Daily,
        Achievement,
        PvP,
        Seasonal,
        Referral,
        Compensation,
        Special,
        Bonus;
    }

    /// <summary>
    /// Ödül rarity enum'u;
    /// </summary>
    public enum RewardRarity;
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary,
        Mythic;
    }

    /// <summary>
    /// Bonus tipi enum'u;
    /// </summary>
    public enum BonusType;
    {
        FirstPurchase,
        StreakBonus,
        VIPBonus,
        EventBonus,
        ReferralBonus,
        Anniversary,
        Loyalty;
    }

    #region Events;

    public class RewardDistributedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public RewardPackage RewardPackage { get; set; }
        public DateTime DistributionTime { get; set; }
        public bool Success { get; set; }
    }

    public class RewardDistributionFailedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public RewardPackage RewardPackage { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class RewardExchangedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public Guid ExchangeId { get; set; }
        public List<RewardItem> RequiredItems { get; set; }
        public List<CustomCurrency> RequiredCurrencies { get; set; }
        public RewardPackage Reward { get; set; }
        public DateTime ExchangeTime { get; set; }
    }

    public class BonusRewardAppliedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public BonusType BonusType { get; set; }
        public RewardPackage Reward { get; set; }
        public decimal Multiplier { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // External events (diğer sistemlerden gelen)
    public class QuestCompletedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public Guid QuestId { get; set; }
        public string QuestName { get; set; }
        public string QuestDifficulty { get; set; }
        public int QuestLevel { get; set; }
        public DateTime CompletionTime { get; set; }
    }

    public class AchievementUnlockedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public Guid AchievementId { get; set; }
        public string AchievementName { get; set; }
        public string AchievementRarity { get; set; }
        public DateTime UnlockTime { get; set; }
    }

    public class DailyLoginEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public int LoginStreak { get; set; }
        public DateTime LoginTime { get; set; }
    }

    public class PvpMatchEndedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public bool IsWinner { get; set; }
        public int RankingChange { get; set; }
        public DateTime MatchEndTime { get; set; }
    }

    public class SeasonEndedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public string SeasonId { get; set; }
        public string SeasonRank { get; set; }
        public int Ranking { get; set; }
        public DateTime SeasonEndTime { get; set; }
    }

    #endregion;

    /// <summary>
    /// Reward sistemi exception'ı;
    /// </summary>
    public class RewardSystemException : Exception
    {
        public RewardSystemException(string message) : base(message) { }
        public RewardSystemException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Reward repository interface'i;
    /// </summary>
    public interface IRewardRepository;
    {
        Task<List<RewardHistory>> GetRewardHistoryAsync(Guid userId);
        Task AddRewardHistoryAsync(RewardHistory history);
    }

    /// <summary>
    /// Notification interface'i;
    /// </summary>
    public interface RewardNotification;
    {
        Guid UserId { get; set; }
        string Title { get; set; }
        string Message { get; set; }
        List<string> RewardDetails { get; set; }
        decimal TotalValue { get; set; }
        DateTime SentTime { get; set; }
    }
}
