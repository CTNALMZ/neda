using NEDA.Core.Logging;
using NEDA.CharacterSystems.GameplaySystems;
using NEDA.Services.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics;
{
    /// <summary>
    /// Seviye sistemini yöneten ana sınıf;
    /// </summary>
    public interface ILevelSystem;
    {
        /// <summary>
        /// Kullanıcı için seviye bilgisini alır;
        /// </summary>
        Task<LevelInfo> GetLevelInfoAsync(Guid userId);

        /// <summary>
        /// Deneyim puanı ekler;
        /// </summary>
        Task<LevelUpResult> AddExperienceAsync(Guid userId, int experience, ExperienceSource source);

        /// <summary>
        /// Seviye atlatır;
        /// </summary>
        Task<LevelUpResult> LevelUpAsync(Guid userId);

        /// <summary>
        /// Seviye sıfırlar;
        /// </summary>
        Task ResetLevelAsync(Guid userId);

        /// <summary>
        /// Seviye bonuslarını uygular;
        /// </summary>
        Task<BonusResult> ApplyLevelBonusesAsync(Guid userId);

        /// <summary>
        /// Tüm seviye verilerini yükler;
        /// </summary>
        Task<Dictionary<int, LevelThreshold>> LoadLevelThresholdsAsync();
    }

    /// <summary>
    /// Level sistemi implementasyonu;
    /// </summary>
    public class LevelSystem : ILevelSystem;
    {
        private readonly ILogger<LevelSystem> _logger;
        private readonly IExperienceSystem _experienceSystem;
        private readonly IAchievementSystem _achievementSystem;
        private readonly IEventBus _eventBus;
        private readonly ConcurrentDictionary<Guid, LevelInfo> _levelCache;
        private Dictionary<int, LevelThreshold> _levelThresholds;
        private readonly object _thresholdLock = new object();

        private const int MAX_LEVEL = 100;
        private const int BASE_EXPERIENCE = 1000;
        private const double EXPERIENCE_MULTIPLIER = 1.5;

        public LevelSystem(
            ILogger<LevelSystem> logger,
            IExperienceSystem experienceSystem,
            IAchievementSystem achievementSystem,
            IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _experienceSystem = experienceSystem ?? throw new ArgumentNullException(nameof(experienceSystem));
            _achievementSystem = achievementSystem ?? throw new ArgumentNullException(nameof(achievementSystem));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _levelCache = new ConcurrentDictionary<Guid, LevelInfo>();
            _levelThresholds = new Dictionary<int, LevelThreshold>();

            InitializeLevelThresholds();
            SubscribeToEvents();
        }

        /// <summary>
        /// Level eşik değerlerini başlatır;
        /// </summary>
        private void InitializeLevelThresholds()
        {
            try
            {
                lock (_thresholdLock)
                {
                    _levelThresholds.Clear();

                    for (int level = 1; level <= MAX_LEVEL; level++)
                    {
                        int requiredExp = CalculateRequiredExperience(level);
                        var bonuses = CalculateLevelBonuses(level);

                        _levelThresholds[level] = new LevelThreshold;
                        {
                            Level = level,
                            RequiredExperience = requiredExp,
                            HealthBonus = bonuses.health,
                            DamageBonus = bonuses.damage,
                            DefenseBonus = bonuses.defense,
                            SkillPoints = bonuses.skillPoints,
                            UnlockedFeatures = GetUnlockedFeatures(level)
                        };
                    }

                    _logger.LogInformation("Level thresholds initialized for {MaxLevel} levels", MAX_LEVEL);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize level thresholds");
                throw new LevelSystemException("Level threshold initialization failed", ex);
            }
        }

        /// <summary>
        /// Gerekli deneyim miktarını hesaplar;
        /// </summary>
        private int CalculateRequiredExperience(int level)
        {
            if (level <= 1) return BASE_EXPERIENCE;

            // Exponential experience curve;
            double multiplier = Math.Pow(EXPERIENCE_MULTIPLIER, level - 1);
            return (int)(BASE_EXPERIENCE * multiplier);
        }

        /// <summary>
        /// Level bonuslarını hesaplar;
        /// </summary>
        private (int health, int damage, int defense, int skillPoints) CalculateLevelBonuses(int level)
        {
            int baseBonus = 10;
            double levelMultiplier = 1 + (level * 0.1);

            int health = (int)(baseBonus * levelMultiplier * 5);
            int damage = (int)(baseBonus * levelMultiplier * 2);
            int defense = (int)(baseBonus * levelMultiplier * 1.5);
            int skillPoints = Math.Max(1, level / 10);

            return (health, damage, defense, skillPoints);
        }

        /// <summary>
        /// Açılan özellikleri alır;
        /// </summary>
        private List<string> GetUnlockedFeatures(int level)
        {
            var features = new List<string>();

            if (level >= 5) features.Add("Advanced Skills");
            if (level >= 10) features.Add("Dual Wielding");
            if (level >= 15) features.Add("Mount Riding");
            if (level >= 20) features.Add("Guild Creation");
            if (level >= 25) features.Add("Auction House Access");
            if (level >= 30) features.Add("PvP Arena");
            if (level >= 40) features.Add("Epic Quests");
            if (level >= 50) features.Add("Legendary Items");
            if (level >= 60) features.Add("Raid Participation");
            if (level >= 70) features.Add("Mythic Dungeons");
            if (level >= 80) features.Add("World Bosses");
            if (level >= 90) features.Add("Transcendence");
            if (level >= 100) features.Add("Ascension");

            return features;
        }

        /// <summary>
        /// Event'lara subscribe olur;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<ExperienceGainedEvent>(OnExperienceGained);
            _eventBus.Subscribe<LevelUpEvent>(OnLevelUp);
            _eventBus.Subscribe<UserResetEvent>(OnUserReset);
        }

        public async Task<LevelInfo> GetLevelInfoAsync(Guid userId)
        {
            try
            {
                _logger.LogDebug("Getting level info for user {UserId}", userId);

                // Cache kontrolü;
                if (_levelCache.TryGetValue(userId, out var cachedInfo))
                {
                    return cachedInfo;
                }

                // Veritabanından yükle;
                var levelInfo = await LoadLevelInfoFromDatabaseAsync(userId);

                // Cache'e ekle;
                _levelCache[userId] = levelInfo;

                return levelInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get level info for user {UserId}", userId);
                throw new LevelSystemException($"Failed to get level info for user {userId}", ex);
            }
        }

        public async Task<LevelUpResult> AddExperienceAsync(Guid userId, int experience, ExperienceSource source)
        {
            try
            {
                if (experience <= 0)
                    throw new ArgumentException("Experience must be positive", nameof(experience));

                _logger.LogInformation("Adding {Experience} experience to user {UserId} from source {Source}",
                    experience, userId, source);

                // Mevcut level bilgisini al;
                var levelInfo = await GetLevelInfoAsync(userId);

                // Experience ekle;
                int oldExp = levelInfo.CurrentExperience;
                levelInfo.CurrentExperience += experience;
                levelInfo.TotalExperience += experience;

                // Level atlama kontrolü;
                bool leveledUp = false;
                int levelsGained = 0;

                while (CanLevelUp(levelInfo))
                {
                    await PerformLevelUpAsync(levelInfo);
                    leveledUp = true;
                    levelsGained++;
                }

                // Veritabanını güncelle;
                await SaveLevelInfoAsync(levelInfo);

                // Event publish;
                if (leveledUp)
                {
                    await _eventBus.PublishAsync(new LevelUpEvent;
                    {
                        UserId = userId,
                        NewLevel = levelInfo.CurrentLevel,
                        LevelsGained = levelsGained,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                await _eventBus.PublishAsync(new ExperienceGainedEvent;
                {
                    UserId = userId,
                    ExperienceGained = experience,
                    Source = source,
                    NewTotal = levelInfo.TotalExperience,
                    Timestamp = DateTime.UtcNow;
                });

                // Achievement kontrolü;
                await CheckExperienceAchievementsAsync(userId, levelInfo);

                var result = new LevelUpResult;
                {
                    Success = true,
                    OldLevel = levelInfo.CurrentLevel - levelsGained,
                    NewLevel = levelInfo.CurrentLevel,
                    LevelsGained = levelsGained,
                    OldExperience = oldExp,
                    NewExperience = levelInfo.CurrentExperience,
                    TotalExperience = levelInfo.TotalExperience,
                    UnlockedFeatures = GetNewlyUnlockedFeatures(levelInfo, levelsGained)
                };

                _logger.LogInformation("Successfully added experience. User {UserId} is now level {Level}",
                    userId, levelInfo.CurrentLevel);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add experience for user {UserId}", userId);
                throw new LevelSystemException($"Failed to add experience for user {userId}", ex);
            }
        }

        public async Task<LevelUpResult> LevelUpAsync(Guid userId)
        {
            try
            {
                _logger.LogInformation("Manual level up requested for user {UserId}", userId);

                var levelInfo = await GetLevelInfoAsync(userId);

                if (!CanLevelUp(levelInfo))
                {
                    throw new LevelSystemException($"User {userId} cannot level up. Required XP: {GetRequiredExpForNextLevel(levelInfo.CurrentLevel)}");
                }

                int oldLevel = levelInfo.CurrentLevel;

                await PerformLevelUpAsync(levelInfo);
                await SaveLevelInfoAsync(levelInfo);

                // Event publish;
                await _eventBus.PublishAsync(new LevelUpEvent;
                {
                    UserId = userId,
                    NewLevel = levelInfo.CurrentLevel,
                    LevelsGained = 1,
                    Timestamp = DateTime.UtcNow;
                });

                var result = new LevelUpResult;
                {
                    Success = true,
                    OldLevel = oldLevel,
                    NewLevel = levelInfo.CurrentLevel,
                    LevelsGained = 1,
                    OldExperience = levelInfo.CurrentExperience,
                    NewExperience = 0, // Experience reset after level up;
                    TotalExperience = levelInfo.TotalExperience,
                    UnlockedFeatures = GetNewlyUnlockedFeatures(levelInfo, 1)
                };

                _logger.LogInformation("User {UserId} leveled up from {OldLevel} to {NewLevel}",
                    userId, oldLevel, levelInfo.CurrentLevel);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to level up user {UserId}", userId);
                throw new LevelSystemException($"Failed to level up user {userId}", ex);
            }
        }

        public async Task ResetLevelAsync(Guid userId)
        {
            try
            {
                _logger.LogWarning("Resetting level for user {UserId}", userId);

                var levelInfo = await GetLevelInfoAsync(userId);
                int oldLevel = levelInfo.CurrentLevel;

                // Reset level info;
                levelInfo.CurrentLevel = 1;
                levelInfo.CurrentExperience = 0;
                levelInfo.TotalExperience = 0;
                levelInfo.LastLevelUp = DateTime.UtcNow;

                // Cache'i temizle;
                _levelCache.TryRemove(userId, out _);

                // Veritabanını güncelle;
                await SaveLevelInfoAsync(levelInfo);

                // Event publish;
                await _eventBus.PublishAsync(new UserResetEvent;
                {
                    UserId = userId,
                    ResetType = ResetType.Level,
                    OldLevel = oldLevel,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Successfully reset level for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset level for user {UserId}", userId);
                throw new LevelSystemException($"Failed to reset level for user {userId}", ex);
            }
        }

        public async Task<BonusResult> ApplyLevelBonusesAsync(Guid userId)
        {
            try
            {
                _logger.LogDebug("Applying level bonuses for user {UserId}", userId);

                var levelInfo = await GetLevelInfoAsync(userId);
                var threshold = GetLevelThreshold(levelInfo.CurrentLevel);

                var bonuses = new CharacterBonuses;
                {
                    Health = threshold.HealthBonus,
                    Damage = threshold.DamageBonus,
                    Defense = threshold.DefenseBonus,
                    SkillPoints = threshold.SkillPoints,
                    CriticalChance = levelInfo.CurrentLevel * 0.1f,
                    CriticalDamage = 1.0f + (levelInfo.CurrentLevel * 0.05f)
                };

                // Apply bonuses to character;
                await ApplyCharacterBonusesAsync(userId, bonuses);

                var result = new BonusResult;
                {
                    Success = true,
                    BonusesApplied = bonuses,
                    UnlockedFeatures = threshold.UnlockedFeatures,
                    Level = levelInfo.CurrentLevel;
                };

                _logger.LogInformation("Applied level bonuses for user {UserId} at level {Level}",
                    userId, levelInfo.CurrentLevel);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply level bonuses for user {UserId}", userId);
                throw new LevelSystemException($"Failed to apply level bonuses for user {userId}", ex);
            }
        }

        public async Task<Dictionary<int, LevelThreshold>> LoadLevelThresholdsAsync()
        {
            try
            {
                _logger.LogDebug("Loading level thresholds");

                if (!_levelThresholds.Any())
                {
                    InitializeLevelThresholds();
                }

                return _levelThresholds.ToDictionary(x => x.Key, x => x.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load level thresholds");
                throw new LevelSystemException("Failed to load level thresholds", ex);
            }
        }

        /// <summary>
        /// Level atlamaya uygun olup olmadığını kontrol eder;
        /// </summary>
        private bool CanLevelUp(LevelInfo levelInfo)
        {
            if (levelInfo.CurrentLevel >= MAX_LEVEL)
                return false;

            int requiredExp = GetRequiredExpForNextLevel(levelInfo.CurrentLevel);
            return levelInfo.CurrentExperience >= requiredExp;
        }

        /// <summary>
        /// Level atlama işlemini gerçekleştirir;
        /// </summary>
        private async Task PerformLevelUpAsync(LevelInfo levelInfo)
        {
            int requiredExp = GetRequiredExpForNextLevel(levelInfo.CurrentLevel);

            levelInfo.CurrentLevel++;
            levelInfo.CurrentExperience -= requiredExp;
            levelInfo.LastLevelUp = DateTime.UtcNow;
            levelInfo.TimesLeveledUp++;

            // Apply bonuses;
            await ApplyLevelBonusesAsync(levelInfo.UserId);

            _logger.LogDebug("User {UserId} leveled up to {NewLevel}",
                levelInfo.UserId, levelInfo.CurrentLevel);
        }

        /// <summary>
        /// Sonraki level için gerekli deneyimi alır;
        /// </summary>
        private int GetRequiredExpForNextLevel(int currentLevel)
        {
            if (currentLevel >= MAX_LEVEL)
                return int.MaxValue;

            return _levelThresholds[currentLevel + 1].RequiredExperience;
        }

        /// <summary>
        /// Level eşiğini alır;
        /// </summary>
        private LevelThreshold GetLevelThreshold(int level)
        {
            if (!_levelThresholds.TryGetValue(level, out var threshold))
            {
                throw new LevelSystemException($"Level threshold not found for level {level}");
            }

            return threshold;
        }

        /// <summary>
        /// Yeni açılan özellikleri alır;
        /// </summary>
        private List<string> GetNewlyUnlockedFeatures(LevelInfo levelInfo, int levelsGained)
        {
            var newFeatures = new List<string>();

            for (int i = 0; i < levelsGained; i++)
            {
                int checkLevel = levelInfo.CurrentLevel - i;
                if (_levelThresholds.TryGetValue(checkLevel, out var threshold))
                {
                    newFeatures.AddRange(threshold.UnlockedFeatures);
                }
            }

            return newFeatures.Distinct().ToList();
        }

        /// <summary>
        /// Karakter bonuslarını uygular;
        /// </summary>
        private async Task ApplyCharacterBonusesAsync(Guid userId, CharacterBonuses bonuses)
        {
            // Bu metod gerçek implementasyonda karakter servisi ile entegre çalışır;
            await Task.CompletedTask;
            _logger.LogDebug("Applied character bonuses for user {UserId}", userId);
        }

        /// <summary>
        /// Deneyim achievement'larını kontrol eder;
        /// </summary>
        private async Task CheckExperienceAchievementsAsync(Guid userId, LevelInfo levelInfo)
        {
            try
            {
                await _achievementSystem.CheckAchievementAsync(userId, "FirstExperience");

                if (levelInfo.TotalExperience >= 1000)
                    await _achievementSystem.CheckAchievementAsync(userId, "ThousandXP");

                if (levelInfo.TotalExperience >= 10000)
                    await _achievementSystem.CheckAchievementAsync(userId, "TenThousandXP");

                if (levelInfo.TotalExperience >= 100000)
                    await _achievementSystem.CheckAchievementAsync(userId, "HundredThousandXP");

                if (levelInfo.CurrentLevel >= 10)
                    await _achievementSystem.CheckAchievementAsync(userId, "Level10");

                if (levelInfo.CurrentLevel >= 50)
                    await _achievementSystem.CheckAchievementAsync(userId, "Level50");

                if (levelInfo.CurrentLevel >= MAX_LEVEL)
                    await _achievementSystem.CheckAchievementAsync(userId, "MaxLevel");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check experience achievements for user {UserId}", userId);
            }
        }

        /// <summary>
        /// Level bilgisini veritabanından yükler;
        /// </summary>
        private async Task<LevelInfo> LoadLevelInfoFromDatabaseAsync(Guid userId)
        {
            // Bu metod gerçek implementasyonda veritabanı erişimi yapar;
            // Şimdilik mock data dönüyoruz;
            await Task.Delay(10); // Simulate DB access;

            return new LevelInfo;
            {
                UserId = userId,
                CurrentLevel = 1,
                CurrentExperience = 0,
                TotalExperience = 0,
                LastLevelUp = DateTime.UtcNow,
                TimesLeveledUp = 0,
                CreatedDate = DateTime.UtcNow.AddDays(-1),
                UpdatedDate = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Level bilgisini kaydeder;
        /// </summary>
        private async Task SaveLevelInfoAsync(LevelInfo levelInfo)
        {
            // Bu metod gerçek implementasyonda veritabanına kayıt yapar;
            await Task.Delay(10); // Simulate DB access;

            levelInfo.UpdatedDate = DateTime.UtcNow;
            _levelCache[levelInfo.UserId] = levelInfo;
        }

        /// <summary>
        /// Experience kazanma event handler'ı;
        /// </summary>
        private async Task OnExperienceGained(ExperienceGainedEvent @event)
        {
            try
            {
                _logger.LogDebug("Experience gained event received: {UserId} +{Experience}",
                    @event.UserId, @event.ExperienceGained);

                // Additional processing can be added here;
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling experience gained event");
            }
        }

        /// <summary>
        /// Level up event handler'ı;
        /// </summary>
        private async Task OnLevelUp(LevelUpEvent @event)
        {
            try
            {
                _logger.LogInformation("Level up event received: {UserId} -> Level {NewLevel}",
                    @event.UserId, @event.NewLevel);

                // Additional processing can be added here;
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling level up event");
            }
        }

        /// <summary>
        /// User reset event handler'ı;
        /// </summary>
        private async Task OnUserReset(UserResetEvent @event)
        {
            try
            {
                _logger.LogWarning("User reset event received: {UserId}", @event.UserId);

                // Cache'i temizle;
                _levelCache.TryRemove(@event.UserId, out _);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling user reset event");
            }
        }
    }

    /// <summary>
    /// Level bilgi modeli;
    /// </summary>
    public class LevelInfo;
    {
        public Guid UserId { get; set; }
        public int CurrentLevel { get; set; }
        public int CurrentExperience { get; set; }
        public int TotalExperience { get; set; }
        public DateTime LastLevelUp { get; set; }
        public int TimesLeveledUp { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime UpdatedDate { get; set; }
    }

    /// <summary>
    /// Level eşik değerleri;
    /// </summary>
    public class LevelThreshold;
    {
        public int Level { get; set; }
        public int RequiredExperience { get; set; }
        public int HealthBonus { get; set; }
        public int DamageBonus { get; set; }
        public int DefenseBonus { get; set; }
        public int SkillPoints { get; set; }
        public List<string> UnlockedFeatures { get; set; } = new List<string>();
    }

    /// <summary>
    /// Level up sonucu;
    /// </summary>
    public class LevelUpResult;
    {
        public bool Success { get; set; }
        public int OldLevel { get; set; }
        public int NewLevel { get; set; }
        public int LevelsGained { get; set; }
        public int OldExperience { get; set; }
        public int NewExperience { get; set; }
        public int TotalExperience { get; set; }
        public List<string> UnlockedFeatures { get; set; } = new List<string>();
        public string Message => Success;
            ? $"Leveled up from {OldLevel} to {NewLevel}!"
            : "Level up failed";
    }

    /// <summary>
    /// Bonus uygulama sonucu;
    /// </summary>
    public class BonusResult;
    {
        public bool Success { get; set; }
        public CharacterBonuses BonusesApplied { get; set; }
        public List<string> UnlockedFeatures { get; set; }
        public int Level { get; set; }
    }

    /// <summary>
    /// Karakter bonusları;
    /// </summary>
    public class CharacterBonuses;
    {
        public int Health { get; set; }
        public int Damage { get; set; }
        public int Defense { get; set; }
        public int SkillPoints { get; set; }
        public float CriticalChance { get; set; }
        public float CriticalDamage { get; set; }
    }

    /// <summary>
    /// Deneyim kaynağı enum'u;
    /// </summary>
    public enum ExperienceSource;
    {
        Quest,
        Combat,
        Exploration,
        Crafting,
        Social,
        Achievement,
        Purchase,
        Admin;
    }

    /// <summary>
    /// Reset tipi enum'u;
    /// </summary>
    public enum ResetType;
    {
        Level,
        Skill,
        Character,
        Account;
    }

    /// <summary>
    /// Deneyim kazanma event'i;
    /// </summary>
    public class ExperienceGainedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public int ExperienceGained { get; set; }
        public ExperienceSource Source { get; set; }
        public int NewTotal { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Level up event'i;
    /// </summary>
    public class LevelUpEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public int NewLevel { get; set; }
        public int LevelsGained { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// User reset event'i;
    /// </summary>
    public class UserResetEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public ResetType ResetType { get; set; }
        public int OldLevel { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Level sistemi exception'ı;
    /// </summary>
    public class LevelSystemException : Exception
    {
        public LevelSystemException(string message) : base(message) { }
        public LevelSystemException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
