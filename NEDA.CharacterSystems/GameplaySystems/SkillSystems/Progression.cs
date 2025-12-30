using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;
using NEDA.Services.ProjectService;
using NEDA.CharacterSystems.GameplaySystems.QuestDesigner;
using NEDA.CharacterSystems.GameplaySystems.SkillSystems;
using NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics;

namespace NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics;
{
    /// <summary>
    /// Comprehensive character progression system that manages leveling, experience, skills,
    /// achievements, and overall character development.
    /// </summary>
    public class Progression : IDisposable
    {
        #region Constants;

        private const int DEFAULT_MAX_LEVEL = 100;
        private const int DEFAULT_STARTING_LEVEL = 1;
        private const long DEFAULT_STARTING_EXPERIENCE = 0;
        private const double EXPONENTIAL_GROWTH_BASE = 1.5;
        private const int BASE_EXPERIENCE_REQUIRED = 1000;
        private const int MAX_SKILL_POINTS_PER_LEVEL = 5;
        private const int MAX_ATTRIBUTE_POINTS_PER_LEVEL = 3;

        #endregion;

        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IProjectManager _projectManager;
        private readonly Experience _experienceSystem;
        private readonly SkillTree _skillSystem;
        private readonly QuestManager _questManager;
        private readonly ConcurrentDictionary<string, Achievement> _achievements;
        private readonly ConcurrentDictionary<string, Milestone> _milestones;
        private readonly ConcurrentDictionary<string, ProgressionEvent> _progressionEvents;
        private readonly object _syncLock = new object();
        private bool _disposed = false;
        private bool _isInitialized = false;
        private System.Timers.Timer _periodicSaveTimer;

        #endregion;

        #region Properties;

        /// <summary>
        /// Current character level;
        /// </summary>
        public int CurrentLevel => _experienceSystem?.CurrentLevel ?? DEFAULT_STARTING_LEVEL;

        /// <summary>
        /// Current experience points;
        /// </summary>
        public long CurrentExperience => _experienceSystem?.CurrentExperience ?? DEFAULT_STARTING_EXPERIENCE;

        /// <summary>
        /// Total experience earned;
        /// </summary>
        public long TotalExperienceEarned => _experienceSystem?.TotalExperienceEarned ?? DEFAULT_STARTING_EXPERIENCE;

        /// <summary>
        /// Experience required for next level;
        /// </summary>
        public long ExperienceToNextLevel => _experienceSystem?.ExperienceToNextLevel ?? 0;

        /// <summary>
        /// Progress percentage to next level (0-100)
        /// </summary>
        public double LevelProgressPercentage => _experienceSystem?.ProgressPercentage ?? 0;

        /// <summary>
        /// Available skill points;
        /// </summary>
        public int AvailableSkillPoints { get; private set; }

        /// <summary>
        /// Available attribute points;
        /// </summary>
        public int AvailableAttributePoints { get; private set; }

        /// <summary>
        /// Total skill points earned;
        /// </summary>
        public int TotalSkillPointsEarned { get; private set; }

        /// <summary>
        /// Total attribute points earned;
        /// </summary>
        public int TotalAttributePointsEarned { get; private set; }

        /// <summary>
        /// Character attributes;
        /// </summary>
        public CharacterAttributes Attributes { get; private set; }

        /// <summary>
        /// Progression configuration;
        /// </summary>
        public ProgressionConfiguration Configuration { get; private set; }

        /// <summary>
        /// Progression statistics;
        /// </summary>
        public ProgressionStatistics Statistics { get; private set; }

        /// <summary>
        /// Active progression modifiers;
        /// </summary>
        public List<ProgressionModifier> ActiveModifiers { get; private set; }

        /// <summary>
        /// Achievement progress tracking;
        /// </summary>
        public AchievementProgress AchievementProgress { get; private set; }

        /// <summary>
        /// Character class information;
        /// </summary>
        public CharacterClass CharacterClass { get; private set; }

        /// <summary>
        /// Progression session data;
        /// </summary>
        public ProgressionSessionData SessionData { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Raised when character levels up;
        /// </summary>
        public event EventHandler<LevelUpEventArgs> LevelUp;

        /// <summary>
        /// Raised when experience is gained;
        /// </summary>
        public event EventHandler<ExperienceGainedEventArgs> ExperienceGained;

        /// <summary>
        /// Raised when a skill point is gained;
        /// </summary>
        public event EventHandler<SkillPointGainedEventArgs> SkillPointGained;

        /// <summary>
        /// Raised when an attribute point is gained;
        /// </summary>
        public event EventHandler<AttributePointGainedEventArgs> AttributePointGained;

        /// <summary>
        /// Raised when an attribute is increased;
        /// </summary>
        public event EventHandler<AttributeIncreasedEventArgs> AttributeIncreased;

        /// <summary>
        /// Raised when a skill is learned;
        /// </summary>
        public event EventHandler<SkillLearnedEventArgs> SkillLearned;

        /// <summary>
        /// Raised when a skill is upgraded;
        /// </summary>
        public event EventHandler<SkillUpgradedEventArgs> SkillUpgraded;

        /// <summary>
        /// Raised when an achievement is unlocked;
        /// </summary>
        public event EventHandler<AchievementUnlockedEventArgs> AchievementUnlocked;

        /// <summary>
        /// Raised when a milestone is reached;
        /// </summary>
        public event EventHandler<MilestoneReachedEventArgs> MilestoneReached;

        /// <summary>
        /// Raised when progression modifiers change;
        /// </summary>
        public event EventHandler<ModifiersChangedEventArgs> ModifiersChanged;

        /// <summary>
        /// Raised when character class changes;
        /// </summary>
        public event EventHandler<CharacterClassChangedEventArgs> CharacterClassChanged;

        /// <summary>
        /// Raised when progression resets;
        /// </summary>
        public event EventHandler<ProgressionResetEventArgs> ProgressionReset;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new Progression system;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="projectManager">Project manager for saving/loading</param>
        public Progression(ILogger logger = null, IProjectManager projectManager = null)
        {
            _logger = logger ?? LoggerFactory.CreateLogger(nameof(Progression));
            _projectManager = projectManager;

            InitializeSystems();
            InitializeDefaults();
        }

        /// <summary>
        /// Initializes Progression with custom configuration;
        /// </summary>
        public Progression(ProgressionConfiguration configuration, ILogger logger = null,
                          IProjectManager projectManager = null)
            : this(logger, projectManager)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            ApplyConfiguration();
        }

        /// <summary>
        /// Initializes Progression with existing systems;
        /// </summary>
        public Progression(Experience experienceSystem, SkillTree skillSystem, QuestManager questManager,
                          ILogger logger = null, IProjectManager projectManager = null)
            : this(logger, projectManager)
        {
            _experienceSystem = experienceSystem ?? throw new ArgumentNullException(nameof(experienceSystem));
            _skillSystem = skillSystem ?? throw new ArgumentNullException(nameof(skillSystem));
            _questManager = questManager ?? throw new ArgumentNullException(nameof(questManager));

            WireUpEvents();
        }

        #endregion;

        #region Public Methods - Initialization;

        /// <summary>
        /// Initializes the progression system;
        /// </summary>
        /// <param name="configuration">Progression configuration</param>
        /// <param name="characterClass">Initial character class</param>
        /// <param name="saveData">Saved progression data</param>
        public async Task InitializeAsync(ProgressionConfiguration configuration = null,
                                        CharacterClass characterClass = null,
                                        ProgressionSaveData saveData = null)
        {
            ValidateNotDisposed();

            try
            {
                _logger.LogInformation("Initializing Progression system...");

                // Apply configuration;
                if (configuration != null)
                {
                    Configuration = configuration;
                    ApplyConfiguration();
                }

                // Set character class;
                if (characterClass != null)
                {
                    CharacterClass = characterClass;
                }

                // Load save data;
                if (saveData != null)
                {
                    await LoadSaveDataAsync(saveData);
                }
                else;
                {
                    // Initialize defaults;
                    InitializeCharacter();
                }

                // Initialize systems if not already initialized;
                if (_experienceSystem == null)
                {
                    _experienceSystem = new Experience(DEFAULT_STARTING_LEVEL, DEFAULT_STARTING_EXPERIENCE,
                                                      Configuration.MaxLevel, new Experience.ExperienceConfiguration(),
                                                      _logger);
                }

                if (_skillSystem == null)
                {
                    _skillSystem = new SkillTree(_logger);
                }

                // Wire up events;
                WireUpEvents();

                // Load achievements and milestones;
                await LoadAchievementsAsync();
                await LoadMilestonesAsync();

                // Initialize periodic save timer;
                InitializePeriodicSaveTimer();

                _isInitialized = true;

                _logger.LogInformation($"Progression system initialized. Level: {CurrentLevel}, Exp: {CurrentExperience}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Progression system");
                throw new ProgressionInitializationException("Failed to initialize Progression system", ex);
            }
        }

        #endregion;

        #region Public Methods - Experience and Leveling;

        /// <summary>
        /// Adds experience points to the character;
        /// </summary>
        /// <param name="amount">Amount of experience to add</param>
        /// <param name="source">Source of the experience</param>
        /// <param name="tags">Additional metadata tags</param>
        /// <returns>Experience gain result</returns>
        public async Task<ExperienceGainResult> AddExperienceAsync(long amount, string source = null,
                                                                  Dictionary<string, object> tags = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNegative(amount, nameof(amount));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Adding {amount} experience from source: {source ?? "Unknown"}");

                    // Apply active modifiers;
                    var modifiedAmount = ApplyExperienceModifiers(amount, source);

                    // Add experience;
                    var result = _experienceSystem.AddExperience(modifiedAmount, source, tags);

                    // Update statistics;
                    UpdateStatisticsForExperience(result);

                    // Check for level up rewards;
                    if (result.LeveledUp)
                    {
                        await ProcessLevelUpRewardsAsync(result.PreviousLevel, result.NewLevel);
                    }

                    // Check for milestones;
                    await CheckExperienceMilestonesAsync();

                    // Update session data;
                    SessionData.TotalSessionExperience += result.ExperienceAdded;
                    SessionData.LastExperienceGain = DateTime.UtcNow;

                    // Raise event;
                    OnExperienceGained(result, source);

                    _logger.LogDebug($"Experience added: {result.ExperienceAdded}, Level: {CurrentLevel}, Total: {CurrentExperience}");

                    return result;
                }
                catch (Exception ex) when (!(ex is ProgressionOperationException))
                {
                    _logger.LogError(ex, $"Error adding experience: {amount}");
                    throw new ProgressionOperationException($"Failed to add experience: {amount}", ex);
                }
            }
        }

        /// <summary>
        /// Forces a level up (for debugging/testing)
        /// </summary>
        /// <param name="levels">Number of levels to gain</param>
        /// <returns>Level up result</returns>
        public async Task<LevelUpResult> ForceLevelUpAsync(int levels = 1)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNegativeOrZero(levels, nameof(levels));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogWarning($"Forcing level up by {levels} levels");

                    var previousLevel = CurrentLevel;
                    var results = new List<LevelUpResult>();

                    for (int i = 0; i < levels; i++)
                    {
                        if (CurrentLevel >= Configuration.MaxLevel)
                        {
                            _logger.LogWarning($"Max level ({Configuration.MaxLevel}) reached");
                            break;
                        }

                        // Calculate experience needed for next level;
                        var expForCurrentLevel = _experienceSystem.CalculateExperienceForLevel(CurrentLevel);
                        var expForNextLevel = _experienceSystem.CalculateExperienceForLevel(CurrentLevel + 1);
                        var expNeeded = expForNextLevel - expForCurrentLevel;

                        // Add required experience;
                        var expResult = _experienceSystem.AddExperience(expNeeded, "ForceLevelUp",
                                                                       new Dictionary<string, object> { { "forced", true } });

                        // Process level up rewards;
                        var levelUpResult = ProcessLevelUpRewards(CurrentLevel - 1, CurrentLevel);
                        results.Add(levelUpResult);

                        // Raise event;
                        OnLevelUp(CurrentLevel - 1, CurrentLevel, levelUpResult);
                    }

                    var finalResult = new LevelUpResult;
                    {
                        PreviousLevel = previousLevel,
                        NewLevel = CurrentLevel,
                        LevelsGained = CurrentLevel - previousLevel,
                        SkillPointsGained = results.Sum(r => r.SkillPointsGained),
                        AttributePointsGained = results.Sum(r => r.AttributePointsGained),
                        Rewards = results.SelectMany(r => r.Rewards).ToList(),
                        IsForced = true;
                    };

                    _logger.LogInformation($"Forced level up complete: {previousLevel} -> {CurrentLevel}");

                    return finalResult;
                }
                catch (Exception ex) when (!(ex is ProgressionOperationException))
                {
                    _logger.LogError(ex, $"Error forcing level up: {levels}");
                    throw new ProgressionOperationException($"Failed to force level up: {levels}", ex);
                }
            }
        }

        /// <summary>
        /// Gets detailed level progress information;
        /// </summary>
        /// <returns>Level progress details</returns>
        public LevelProgressDetails GetLevelProgressDetails()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var experienceForCurrentLevel = _experienceSystem.CalculateExperienceForLevel(CurrentLevel);
            var experienceForNextLevel = _experienceSystem.CalculateExperienceForLevel(CurrentLevel + 1);

            return new LevelProgressDetails;
            {
                CurrentLevel = CurrentLevel,
                CurrentExperience = CurrentExperience,
                ExperienceForCurrentLevel = experienceForCurrentLevel,
                ExperienceForNextLevel = experienceForNextLevel,
                ExperienceInCurrentLevel = CurrentExperience - experienceForCurrentLevel,
                ExperienceToNextLevel = experienceForNextLevel - experienceForCurrentLevel,
                ProgressPercentage = LevelProgressPercentage,
                IsMaxLevel = CurrentLevel >= Configuration.MaxLevel,
                NextLevelRewards = GetLevelUpRewards(CurrentLevel + 1)
            };
        }

        /// <summary>
        /// Calculates experience required for a specific level;
        /// </summary>
        /// <param name="level">Target level</param>
        /// <returns>Experience required</returns>
        public long CalculateExperienceForLevel(int level)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNegativeOrZero(level, nameof(level));

            return _experienceSystem.CalculateExperienceForLevel(level);
        }

        /// <summary>
        /// Calculates total experience from level 1 to target level;
        /// </summary>
        /// <param name="level">Target level</param>
        /// <returns>Total cumulative experience</returns>
        public long CalculateCumulativeExperience(int level)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNegativeOrZero(level, nameof(level));

            return _experienceSystem.CalculateCumulativeExperience(level);
        }

        #endregion;

        #region Public Methods - Skill Points;

        /// <summary>
        /// Adds skill points;
        /// </summary>
        /// <param name="amount">Number of skill points to add</param>
        /// <param name="source">Source of skill points</param>
        /// <returns>Skill point gain result</returns>
        public SkillPointGainResult AddSkillPoints(int amount, string source = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNegative(amount, nameof(amount));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Adding {amount} skill points from source: {source ?? "Unknown"}");

                    var previousSkillPoints = AvailableSkillPoints;
                    AvailableSkillPoints += amount;
                    TotalSkillPointsEarned += amount;

                    // Update statistics;
                    Statistics.TotalSkillPointsEarned += amount;
                    Statistics.LastSkillPointGain = DateTime.UtcNow;

                    var result = new SkillPointGainResult;
                    {
                        SkillPointsAdded = amount,
                        PreviousSkillPoints = previousSkillPoints,
                        NewSkillPoints = AvailableSkillPoints,
                        Source = source,
                        Timestamp = DateTime.UtcNow;
                    };

                    // Raise event;
                    OnSkillPointGained(amount, AvailableSkillPoints, source);

                    _logger.LogDebug($"Skill points added: {amount}, Total: {AvailableSkillPoints}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error adding skill points: {amount}");
                    throw new ProgressionOperationException($"Failed to add skill points: {amount}", ex);
                }
            }
        }

        /// <summary>
        /// Spends skill points;
        /// </summary>
        /// <param name="amount">Number of skill points to spend</param>
        /// <param name="purpose">Purpose of spending</param>
        /// <returns>True if successful</returns>
        public bool SpendSkillPoints(int amount, string purpose = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNegative(amount, nameof(amount));

            lock (_syncLock)
            {
                try
                {
                    if (AvailableSkillPoints < amount)
                    {
                        _logger.LogWarning($"Insufficient skill points. Required: {amount}, Available: {AvailableSkillPoints}");
                        return false;
                    }

                    _logger.LogInformation($"Spending {amount} skill points for purpose: {purpose ?? "Unknown"}");

                    AvailableSkillPoints -= amount;

                    // Update statistics;
                    Statistics.TotalSkillPointsSpent += amount;
                    Statistics.LastSkillPointSpend = DateTime.UtcNow;

                    _logger.LogDebug($"Skill points spent: {amount}, Remaining: {AvailableSkillPoints}");

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error spending skill points: {amount}");
                    throw new ProgressionOperationException($"Failed to spend skill points: {amount}", ex);
                }
            }
        }

        /// <summary>
        /// Gets skill point information;
        /// </summary>
        /// <returns>Skill point details</returns>
        public SkillPointInfo GetSkillPointInfo()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            return new SkillPointInfo;
            {
                AvailableSkillPoints = AvailableSkillPoints,
                TotalSkillPointsEarned = TotalSkillPointsEarned,
                SkillPointsSpent = TotalSkillPointsEarned - AvailableSkillPoints,
                SkillPointsPerLevel = Configuration.SkillPointsPerLevel,
                NextSkillPointAtLevel = GetNextSkillPointLevel(),
                SkillPointProgress = CalculateSkillPointProgress()
            };
        }

        #endregion;

        #region Public Methods - Attribute Points;

        /// <summary>
        /// Adds attribute points;
        /// </summary>
        /// <param name="amount">Number of attribute points to add</param>
        /// <param name="source">Source of attribute points</param>
        /// <returns>Attribute point gain result</returns>
        public AttributePointGainResult AddAttributePoints(int amount, string source = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNegative(amount, nameof(amount));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Adding {amount} attribute points from source: {source ?? "Unknown"}");

                    var previousAttributePoints = AvailableAttributePoints;
                    AvailableAttributePoints += amount;
                    TotalAttributePointsEarned += amount;

                    // Update statistics;
                    Statistics.TotalAttributePointsEarned += amount;
                    Statistics.LastAttributePointGain = DateTime.UtcNow;

                    var result = new AttributePointGainResult;
                    {
                        AttributePointsAdded = amount,
                        PreviousAttributePoints = previousAttributePoints,
                        NewAttributePoints = AvailableAttributePoints,
                        Source = source,
                        Timestamp = DateTime.UtcNow;
                    };

                    // Raise event;
                    OnAttributePointGained(amount, AvailableAttributePoints, source);

                    _logger.LogDebug($"Attribute points added: {amount}, Total: {AvailableAttributePoints}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error adding attribute points: {amount}");
                    throw new ProgressionOperationException($"Failed to add attribute points: {amount}", ex);
                }
            }
        }

        /// <summary>
        /// Spends attribute points to increase an attribute;
        /// </summary>
        /// <param name="attribute">Attribute to increase</param>
        /// <param name="amount">Number of points to spend</param>
        /// <returns>Attribute increase result</returns>
        public AttributeIncreaseResult IncreaseAttribute(string attribute, int amount = 1)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(attribute, nameof(attribute));
            ArgumentValidator.ThrowIfNegative(amount, nameof(amount));

            lock (_syncLock)
            {
                try
                {
                    if (AvailableAttributePoints < amount)
                    {
                        throw new ProgressionOperationException($"Insufficient attribute points. Required: {amount}, Available: {AvailableAttributePoints}");
                    }

                    _logger.LogInformation($"Increasing attribute '{attribute}' by {amount} points");

                    // Get current attribute value;
                    var currentValue = Attributes.GetAttributeValue(attribute);
                    if (currentValue == null)
                    {
                        throw new ProgressionOperationException($"Unknown attribute: {attribute}");
                    }

                    // Check attribute cap;
                    var attributeCap = GetAttributeCap(attribute);
                    if (currentValue.Value + amount > attributeCap)
                    {
                        throw new ProgressionOperationException($"Attribute '{attribute}' would exceed cap of {attributeCap}");
                    }

                    // Spend attribute points;
                    AvailableAttributePoints -= amount;

                    // Increase attribute;
                    var newValue = Attributes.IncreaseAttribute(attribute, amount);

                    // Update statistics;
                    Statistics.TotalAttributePointsSpent += amount;
                    Statistics.LastAttributeIncrease = DateTime.UtcNow;

                    var result = new AttributeIncreaseResult;
                    {
                        Attribute = attribute,
                        AmountIncreased = amount,
                        PreviousValue = currentValue.Value,
                        NewValue = newValue.Value,
                        RemainingAttributePoints = AvailableAttributePoints,
                        Timestamp = DateTime.UtcNow;
                    };

                    // Raise event;
                    OnAttributeIncreased(attribute, amount, currentValue.Value, newValue.Value);

                    _logger.LogDebug($"Attribute increased: {attribute} ({currentValue.Value} -> {newValue.Value})");

                    return result;
                }
                catch (Exception ex) when (!(ex is ProgressionOperationException))
                {
                    _logger.LogError(ex, $"Error increasing attribute: {attribute}");
                    throw new ProgressionOperationException($"Failed to increase attribute: {attribute}", ex);
                }
            }
        }

        /// <summary>
        /// Resets all attributes to base values and refunds attribute points;
        /// </summary>
        /// <param name="cost">Cost in currency to reset</param>
        /// <returns>Attribute reset result</returns>
        public async Task<AttributeResetResult> ResetAttributesAsync(int cost = 0)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Resetting attributes (Cost: {cost})");

                    // Calculate refund;
                    var spentPoints = TotalAttributePointsEarned - AvailableAttributePoints;

                    // Reset attributes to base values;
                    var previousAttributes = Attributes.Clone();
                    Attributes.ResetToBaseValues();

                    // Refund attribute points;
                    AvailableAttributePoints = TotalAttributePointsEarned;

                    // Update statistics;
                    Statistics.AttributeResets++;
                    Statistics.LastAttributeReset = DateTime.UtcNow;

                    var result = new AttributeResetResult;
                    {
                        SpentPointsRefunded = spentPoints,
                        PreviousAttributes = previousAttributes,
                        NewAttributes = Attributes.Clone(),
                        NewAttributePoints = AvailableAttributePoints,
                        ResetCost = cost,
                        Timestamp = DateTime.UtcNow;
                    };

                    _logger.LogInformation($"Attributes reset. Refunded {spentPoints} attribute points");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resetting attributes");
                    throw new ProgressionOperationException("Failed to reset attributes", ex);
                }
            }
        }

        /// <summary>
        /// Gets attribute point information;
        /// </summary>
        /// <returns>Attribute point details</returns>
        public AttributePointInfo GetAttributePointInfo()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            return new AttributePointInfo;
            {
                AvailableAttributePoints = AvailableAttributePoints,
                TotalAttributePointsEarned = TotalAttributePointsEarned,
                AttributePointsSpent = TotalAttributePointsEarned - AvailableAttributePoints,
                AttributePointsPerLevel = Configuration.AttributePointsPerLevel,
                NextAttributePointAtLevel = GetNextAttributePointLevel(),
                AttributePointProgress = CalculateAttributePointProgress()
            };
        }

        #endregion;

        #region Public Methods - Skills;

        /// <summary>
        /// Learns a new skill;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="skillPointsCost">Skill points cost</param>
        /// <returns>Skill learning result</returns>
        public async Task<SkillLearningResult> LearnSkillAsync(string skillId, int skillPointsCost = 1)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            try
            {
                _logger.LogInformation($"Learning skill: {skillId} (Cost: {skillPointsCost})");

                // Check prerequisites;
                if (!await CanLearnSkillAsync(skillId))
                {
                    throw new ProgressionOperationException($"Cannot learn skill '{skillId}'. Prerequisites not met.");
                }

                // Spend skill points;
                if (!SpendSkillPoints(skillPointsCost, $"Learn skill: {skillId}"))
                {
                    throw new ProgressionOperationException($"Insufficient skill points to learn skill '{skillId}'");
                }

                // Learn skill through skill system;
                var skillResult = await _skillSystem.LearnSkillAsync(skillId);

                // Update statistics;
                Statistics.SkillsLearned++;
                Statistics.LastSkillLearned = DateTime.UtcNow;

                // Check for achievements;
                await CheckSkillAchievementsAsync();

                var result = new SkillLearningResult;
                {
                    Skill = skillResult.Skill,
                    SkillPointsCost = skillPointsCost,
                    PreviousSkillPoints = AvailableSkillPoints + skillPointsCost,
                    NewSkillPoints = AvailableSkillPoints,
                    Timestamp = DateTime.UtcNow;
                };

                // Raise event;
                OnSkillLearned(skillResult.Skill, skillPointsCost);

                _logger.LogInformation($"Skill learned: {skillResult.Skill.Name} (ID: {skillId})");

                return result;
            }
            catch (Exception ex) when (!(ex is ProgressionOperationException))
            {
                _logger.LogError(ex, $"Error learning skill: {skillId}");
                throw new ProgressionOperationException($"Failed to learn skill: {skillId}", ex);
            }
        }

        /// <summary>
        /// Upgrades an existing skill;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="skillPointsCost">Skill points cost</param>
        /// <returns>Skill upgrade result</returns>
        public async Task<SkillUpgradeResult> UpgradeSkillAsync(string skillId, int skillPointsCost = 1)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            try
            {
                _logger.LogInformation($"Upgrading skill: {skillId} (Cost: {skillPointsCost})");

                // Check if skill exists and can be upgraded;
                var skill = _skillSystem.GetSkill(skillId);
                if (skill == null)
                {
                    throw new ProgressionOperationException($"Skill not found: {skillId}");
                }

                if (!skill.CanUpgrade)
                {
                    throw new ProgressionOperationException($"Skill '{skillId}' cannot be upgraded");
                }

                // Spend skill points;
                if (!SpendSkillPoints(skillPointsCost, $"Upgrade skill: {skillId}"))
                {
                    throw new ProgressionOperationException($"Insufficient skill points to upgrade skill '{skillId}'");
                }

                // Upgrade skill through skill system;
                var upgradeResult = await _skillSystem.UpgradeSkillAsync(skillId);

                // Update statistics;
                Statistics.SkillUpgrades++;
                Statistics.LastSkillUpgrade = DateTime.UtcNow;

                var result = new SkillUpgradeResult;
                {
                    Skill = upgradeResult.Skill,
                    PreviousLevel = upgradeResult.PreviousLevel,
                    NewLevel = upgradeResult.NewLevel,
                    SkillPointsCost = skillPointsCost,
                    PreviousSkillPoints = AvailableSkillPoints + skillPointsCost,
                    NewSkillPoints = AvailableSkillPoints,
                    Timestamp = DateTime.UtcNow;
                };

                // Raise event;
                OnSkillUpgraded(upgradeResult.Skill, upgradeResult.PreviousLevel, upgradeResult.NewLevel, skillPointsCost);

                _logger.LogInformation($"Skill upgraded: {skill.Name} to level {upgradeResult.NewLevel}");

                return result;
            }
            catch (Exception ex) when (!(ex is ProgressionOperationException))
            {
                _logger.LogError(ex, $"Error upgrading skill: {skillId}");
                throw new ProgressionOperationException($"Failed to upgrade skill: {skillId}", ex);
            }
        }

        /// <summary>
        /// Gets all learned skills;
        /// </summary>
        /// <returns>List of learned skills</returns>
        public List<Skill> GetLearnedSkills()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            return _skillSystem.GetAllSkills().Where(s => s.IsLearned).ToList();
        }

        /// <summary>
        /// Gets skill mastery progress;
        /// </summary>
        /// <returns>Skill mastery information</returns>
        public SkillMasteryInfo GetSkillMasteryInfo()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var skills = _skillSystem.GetAllSkills();
            var learnedSkills = skills.Where(s => s.IsLearned).ToList();

            return new SkillMasteryInfo;
            {
                TotalSkills = skills.Count,
                LearnedSkills = learnedSkills.Count,
                MaxSkillLevel = learnedSkills.Any() ? learnedSkills.Max(s => s.CurrentLevel) : 0,
                AverageSkillLevel = learnedSkills.Any() ? learnedSkills.Average(s => s.CurrentLevel) : 0,
                TotalSkillLevels = learnedSkills.Sum(s => s.CurrentLevel),
                MasteryPercentage = skills.Count > 0 ? (learnedSkills.Count * 100 / skills.Count) : 0;
            };
        }

        #endregion;

        #region Public Methods - Achievements and Milestones;

        /// <summary>
        /// Unlocks an achievement;
        /// </summary>
        /// <param name="achievementId">Achievement identifier</param>
        /// <param name="progressData">Progress data</param>
        /// <returns>Achievement unlock result</returns>
        public async Task<AchievementUnlockResult> UnlockAchievementAsync(string achievementId,
                                                                        Dictionary<string, object> progressData = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(achievementId, nameof(achievementId));

            lock (_syncLock)
            {
                try
                {
                    if (!_achievements.TryGetValue(achievementId, out var achievement))
                    {
                        throw new ProgressionOperationException($"Achievement not found: {achievementId}");
                    }

                    if (achievement.IsUnlocked)
                    {
                        _logger.LogWarning($"Achievement already unlocked: {achievementId}");
                        return new AchievementUnlockResult;
                        {
                            Achievement = achievement,
                            AlreadyUnlocked = true;
                        };
                    }

                    _logger.LogInformation($"Unlocking achievement: {achievement.Name} (ID: {achievementId})");

                    // Unlock achievement;
                    achievement.IsUnlocked = true;
                    achievement.UnlockedTime = DateTime.UtcNow;
                    achievement.ProgressData = progressData ?? achievement.ProgressData;

                    // Update achievement progress;
                    AchievementProgress.UnlockedAchievements++;
                    AchievementProgress.LastAchievementUnlocked = DateTime.UtcNow;

                    // Update category progress;
                    if (!AchievementProgress.CategoryProgress.ContainsKey(achievement.Category))
                    {
                        AchievementProgress.CategoryProgress[achievement.Category] = new AchievementCategoryProgress();
                    }

                    var categoryProgress = AchievementProgress.CategoryProgress[achievement.Category];
                    categoryProgress.Unlocked++;

                    // Apply rewards;
                    var rewards = ApplyAchievementRewards(achievement);

                    var result = new AchievementUnlockResult;
                    {
                        Achievement = achievement,
                        Rewards = rewards,
                        UnlockedTime = DateTime.UtcNow;
                    };

                    // Update statistics;
                    Statistics.AchievementsUnlocked++;
                    Statistics.LastAchievementUnlocked = DateTime.UtcNow;

                    // Raise event;
                    OnAchievementUnlocked(achievement, rewards);

                    // Check for milestone achievements;
                    await CheckAchievementMilestonesAsync();

                    _logger.LogInformation($"Achievement unlocked: {achievement.Name}");

                    return result;
                }
                catch (Exception ex) when (!(ex is ProgressionOperationException))
                {
                    _logger.LogError(ex, $"Error unlocking achievement: {achievementId}");
                    throw new ProgressionOperationException($"Failed to unlock achievement: {achievementId}", ex);
                }
            }
        }

        /// <summary>
        /// Updates achievement progress;
        /// </summary>
        /// <param name="achievementId">Achievement identifier</param>
        /// <param name="progressAmount">Progress amount to add</param>
        /// <param name="progressData">Progress data</param>
        /// <returns>Achievement progress result</returns>
        public async Task<AchievementProgressResult> UpdateAchievementProgressAsync(string achievementId,
                                                                                  int progressAmount = 1,
                                                                                  Dictionary<string, object> progressData = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(achievementId, nameof(achievementId));

            lock (_syncLock)
            {
                try
                {
                    if (!_achievements.TryGetValue(achievementId, out var achievement))
                    {
                        _logger.LogWarning($"Achievement not found: {achievementId}");
                        return null;
                    }

                    if (achievement.IsUnlocked)
                    {
                        _logger.LogDebug($"Achievement already unlocked: {achievementId}");
                        return new AchievementProgressResult;
                        {
                            Achievement = achievement,
                            AlreadyUnlocked = true;
                        };
                    }

                    // Update progress;
                    var previousProgress = achievement.CurrentProgress;
                    achievement.CurrentProgress = Math.Min(achievement.TargetProgress,
                                                         achievement.CurrentProgress + progressAmount);
                    achievement.ProgressData = progressData ?? achievement.ProgressData;
                    achievement.LastProgressUpdate = DateTime.UtcNow;

                    var result = new AchievementProgressResult;
                    {
                        Achievement = achievement,
                        PreviousProgress = previousProgress,
                        NewProgress = achievement.CurrentProgress,
                        ProgressPercentage = (double)achievement.CurrentProgress / achievement.TargetProgress * 100,
                        IsComplete = achievement.CurrentProgress >= achievement.TargetProgress,
                        ProgressData = progressData;
                    };

                    // Check if achievement is now complete;
                    if (result.IsComplete && !achievement.IsUnlocked)
                    {
                        await UnlockAchievementAsync(achievementId, progressData);
                    }

                    _logger.LogDebug($"Achievement progress updated: {achievement.Name} ({previousProgress} -> {achievement.CurrentProgress})");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error updating achievement progress: {achievementId}");
                    throw new ProgressionOperationException($"Failed to update achievement progress: {achievementId}", ex);
                }
            }
        }

        /// <summary>
        /// Gets all unlocked achievements;
        /// </summary>
        /// <returns>List of unlocked achievements</returns>
        public List<Achievement> GetUnlockedAchievements()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            return _achievements.Values.Where(a => a.IsUnlocked).ToList();
        }

        /// <summary>
        /// Gets achievements by category;
        /// </summary>
        /// <param name="category">Category name</param>
        /// <returns>List of achievements in category</returns>
        public List<Achievement> GetAchievementsByCategory(string category)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(category, nameof(category));

            return _achievements.Values.Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Checks and processes milestones;
        /// </summary>
        public async Task CheckMilestonesAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                await CheckLevelMilestonesAsync();
                await CheckExperienceMilestonesAsync();
                await CheckSkillMilestonesAsync();
                await CheckAchievementMilestonesAsync();
                await CheckTimePlayedMilestonesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking milestones");
            }
        }

        /// <summary>
        /// Gets milestone progress;
        /// </summary>
        /// <param name="milestoneId">Milestone identifier</param>
        /// <returns>Milestone progress</returns>
        public MilestoneProgress GetMilestoneProgress(string milestoneId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(milestoneId, nameof(milestoneId));

            if (!_milestones.TryGetValue(milestoneId, out var milestone))
            {
                return null;
            }

            var progress = CalculateMilestoneProgress(milestone);

            return new MilestoneProgress;
            {
                Milestone = milestone,
                CurrentProgress = progress.CurrentProgress,
                TargetProgress = progress.TargetProgress,
                ProgressPercentage = progress.ProgressPercentage,
                IsReached = progress.IsReached,
                LastUpdated = DateTime.UtcNow;
            };
        }

        #endregion;

        #region Public Methods - Character Class;

        /// <summary>
        /// Changes character class;
        /// </summary>
        /// <param name="newClass">New character class</param>
        /// <param name="cost">Cost to change class</param>
        /// <returns>Class change result</returns>
        public async Task<ClassChangeResult> ChangeClassAsync(CharacterClass newClass, int cost = 0)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(newClass, nameof(newClass));

            lock (_syncLock)
            {
                try
                {
                    if (CharacterClass?.Id == newClass.Id)
                    {
                        throw new ProgressionOperationException($"Character is already class: {newClass.Name}");
                    }

                    _logger.LogInformation($"Changing class from {CharacterClass?.Name ?? "None"} to {newClass.Name} (Cost: {cost})");

                    var previousClass = CharacterClass;
                    CharacterClass = newClass;

                    // Apply class bonuses;
                    ApplyClassBonuses(newClass);

                    // Update statistics;
                    Statistics.ClassChanges++;
                    Statistics.LastClassChange = DateTime.UtcNow;

                    var result = new ClassChangeResult;
                    {
                        PreviousClass = previousClass,
                        NewClass = newClass,
                        ChangeCost = cost,
                        Timestamp = DateTime.UtcNow,
                        BonusesApplied = GetClassBonuses(newClass)
                    };

                    // Raise event;
                    OnCharacterClassChanged(previousClass, newClass, cost);

                    _logger.LogInformation($"Class changed to {newClass.Name}");

                    return result;
                }
                catch (Exception ex) when (!(ex is ProgressionOperationException))
                {
                    _logger.LogError(ex, $"Error changing class to: {newClass.Name}");
                    throw new ProgressionOperationException($"Failed to change class to: {newClass.Name}", ex);
                }
            }
        }

        /// <summary>
        /// Gets available classes for current character;
        /// </summary>
        /// <returns>List of available classes</returns>
        public async Task<List<CharacterClass>> GetAvailableClassesAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            // This would typically load from database or configuration;
            // For now, return a basic list;
            var availableClasses = new List<CharacterClass>();

            // Add current class if exists;
            if (CharacterClass != null)
            {
                availableClasses.Add(CharacterClass);
            }

            // Check unlock conditions for other classes;
            // This is a simplified implementation;
            if (CurrentLevel >= 10)
            {
                availableClasses.Add(new CharacterClass;
                {
                    Id = "advanced_class",
                    Name = "Advanced Class",
                    Description = "An advanced character class",
                    BaseAttributes = new CharacterAttributes(),
                    ClassBonuses = new ClassBonuses()
                });
            }

            return await Task.FromResult(availableClasses);
        }

        #endregion;

        #region Public Methods - Modifiers;

        /// <summary>
        /// Adds a progression modifier;
        /// </summary>
        /// <param name="modifier">Modifier to add</param>
        /// <returns>Modifier addition result</returns>
        public ModifierAdditionResult AddModifier(ProgressionModifier modifier)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(modifier, nameof(modifier));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Adding progression modifier: {modifier.Name} (Type: {modifier.ModifierType})");

                    // Check for duplicate modifiers;
                    var existingModifier = ActiveModifiers.FirstOrDefault(m => m.Id == modifier.Id);
                    if (existingModifier != null)
                    {
                        _logger.LogWarning($"Modifier already active: {modifier.Id}");
                        return new ModifierAdditionResult;
                        {
                            Modifier = existingModifier,
                            AlreadyActive = true;
                        };
                    }

                    // Add modifier;
                    ActiveModifiers.Add(modifier);
                    modifier.ActivatedTime = DateTime.UtcNow;

                    // Apply modifier effects;
                    ApplyModifierEffects(modifier);

                    var result = new ModifierAdditionResult;
                    {
                        Modifier = modifier,
                        ActivatedTime = DateTime.UtcNow;
                    };

                    // Schedule removal if duration is specified;
                    if (modifier.Duration > TimeSpan.Zero)
                    {
                        ScheduleModifierRemoval(modifier);
                    }

                    // Update statistics;
                    Statistics.ModifiersApplied++;

                    // Raise event;
                    OnModifiersChanged(ModifierChangeType.Added, modifier);

                    _logger.LogDebug($"Modifier added: {modifier.Name}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error adding modifier: {modifier.Name}");
                    throw new ProgressionOperationException($"Failed to add modifier: {modifier.Name}", ex);
                }
            }
        }

        /// <summary>
        /// Removes a progression modifier;
        /// </summary>
        /// <param name="modifierId">Modifier identifier</param>
        /// <returns>True if successful</returns>
        public bool RemoveModifier(string modifierId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(modifierId, nameof(modifierId));

            lock (_syncLock)
            {
                try
                {
                    var modifier = ActiveModifiers.FirstOrDefault(m => m.Id == modifierId);
                    if (modifier == null)
                    {
                        _logger.LogWarning($"Modifier not found: {modifierId}");
                        return false;
                    }

                    _logger.LogInformation($"Removing progression modifier: {modifier.Name}");

                    // Remove modifier effects;
                    RemoveModifierEffects(modifier);

                    // Remove from active modifiers;
                    ActiveModifiers.Remove(modifier);
                    modifier.DeactivatedTime = DateTime.UtcNow;

                    // Update statistics;
                    Statistics.ModifiersRemoved++;

                    // Raise event;
                    OnModifiersChanged(ModifierChangeType.Removed, modifier);

                    _logger.LogDebug($"Modifier removed: {modifier.Name}");

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error removing modifier: {modifierId}");
                    throw new ProgressionOperationException($"Failed to remove modifier: {modifierId}", ex);
                }
            }
        }

        /// <summary>
        /// Clears all active modifiers;
        /// </summary>
        public void ClearAllModifiers()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Clearing all progression modifiers ({ActiveModifiers.Count} modifiers)");

                    // Remove effects of all modifiers;
                    foreach (var modifier in ActiveModifiers.ToList())
                    {
                        RemoveModifierEffects(modifier);
                        modifier.DeactivatedTime = DateTime.UtcNow;
                    }

                    // Clear list;
                    var removedCount = ActiveModifiers.Count;
                    ActiveModifiers.Clear();

                    // Update statistics;
                    Statistics.ModifiersCleared++;

                    // Raise event;
                    OnModifiersChanged(ModifierChangeType.Cleared, null);

                    _logger.LogDebug($"Cleared {removedCount} modifiers");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error clearing modifiers");
                    throw new ProgressionOperationException("Failed to clear modifiers", ex);
                }
            }
        }

        /// <summary>
        /// Calculates total modifier effects for a specific modifier type;
        /// </summary>
        /// <param name="modifierType">Modifier type</param>
        /// <returns>Total modifier value</returns>
        public double CalculateTotalModifierEffect(ModifierType modifierType)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            return ActiveModifiers;
                .Where(m => m.ModifierType == modifierType && m.IsActive)
                .Sum(m => m.Value);
        }

        #endregion;

        #region Public Methods - Utility;

        /// <summary>
        /// Saves current progression state;
        /// </summary>
        /// <returns>Save data</returns>
        public async Task<ProgressionSaveData> SaveAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                _logger.LogInformation("Saving progression state...");

                var saveData = new ProgressionSaveData;
                {
                    CurrentLevel = CurrentLevel,
                    CurrentExperience = CurrentExperience,
                    TotalExperienceEarned = TotalExperienceEarned,
                    AvailableSkillPoints = AvailableSkillPoints,
                    AvailableAttributePoints = AvailableAttributePoints,
                    TotalSkillPointsEarned = TotalSkillPointsEarned,
                    TotalAttributePointsEarned = TotalAttributePointsEarned,
                    Attributes = Attributes.Clone(),
                    Configuration = Configuration.Clone(),
                    Statistics = Statistics.Clone(),
                    AchievementProgress = AchievementProgress.Clone(),
                    CharacterClass = CharacterClass?.Clone(),
                    ActiveModifiers = ActiveModifiers.Select(m => m.Clone()).ToList(),
                    SessionData = SessionData.Clone(),
                    SavedTime = DateTime.UtcNow;
                };

                // Save to project manager if available;
                if (_projectManager != null)
                {
                    await _projectManager.SaveDataAsync("progression_system", saveData);
                }

                SessionData.LastSaveTime = DateTime.UtcNow;

                _logger.LogDebug("Progression state saved");

                return saveData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving progression state");
                throw new ProgressionOperationException("Failed to save progression state", ex);
            }
        }

        /// <summary>
        /// Resets progression to initial state;
        /// </summary>
        /// <param name="keepAchievements">Whether to keep achievements</param>
        /// <returns>Reset result</returns>
        public async Task<ProgressionResetResult> ResetProgressionAsync(bool keepAchievements = false)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                _logger.LogWarning("Resetting progression...");

                // Store previous state;
                var previousState = await SaveAsync();

                // Reset experience system;
                _experienceSystem.Dispose();

                // Reset skill system;
                _skillSystem.ResetSkills();

                // Reset attributes;
                Attributes.ResetToBaseValues();

                // Reset points;
                AvailableSkillPoints = 0;
                AvailableAttributePoints = 0;
                TotalSkillPointsEarned = 0;
                TotalAttributePointsEarned = 0;

                // Reset statistics (keep some if requested)
                var statisticsToKeep = keepAchievements ? Statistics.AchievementsUnlocked : 0;
                Statistics = new ProgressionStatistics();
                if (keepAchievements)
                {
                    Statistics.AchievementsUnlocked = statisticsToKeep;
                }

                // Reset session data;
                SessionData = new ProgressionSessionData();

                // Clear modifiers;
                ClearAllModifiers();

                // Re-initialize;
                await InitializeAsync(Configuration, CharacterClass);

                var result = new ProgressionResetResult;
                {
                    PreviousState = previousState,
                    NewState = await SaveAsync(),
                    KeepAchievements = keepAchievements,
                    ResetTime = DateTime.UtcNow;
                };

                // Raise event;
                OnProgressionReset(result, keepAchievements);

                _logger.LogInformation("Progression reset complete");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting progression");
                throw new ProgressionOperationException("Failed to reset progression", ex);
            }
        }

        /// <summary>
        /// Gets comprehensive progression report;
        /// </summary>
        /// <returns>Progression report</returns>
        public async Task<ProgressionReport> GenerateProgressionReportAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                var report = new ProgressionReport;
                {
                    CharacterLevel = CurrentLevel,
                    TotalExperience = TotalExperienceEarned,
                    LevelProgress = GetLevelProgressDetails(),
                    SkillPointInfo = GetSkillPointInfo(),
                    AttributePointInfo = GetAttributePointInfo(),
                    SkillMasteryInfo = GetSkillMasteryInfo(),
                    AchievementProgress = AchievementProgress.Clone(),
                    Statistics = Statistics.Clone(),
                    ActiveModifiers = ActiveModifiers.Select(m => m.Clone()).ToList(),
                    SessionData = SessionData.Clone(),
                    GeneratedTime = DateTime.UtcNow;
                };

                // Calculate time to next level (estimate)
                report.EstimatedTimeToNextLevel = await EstimateTimeToNextLevelAsync();

                // Calculate efficiency metrics;
                report.ExperiencePerHour = CalculateExperiencePerHour();
                report.LevelingEfficiency = CalculateLevelingEfficiency();

                return await Task.FromResult(report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating progression report");
                throw new ProgressionOperationException("Failed to generate progression report", ex);
            }
        }

        /// <summary>
        /// Validates progression state;
        /// </summary>
        /// <returns>Validation result</returns>
        public async Task<ValidationResult> ValidateProgressionStateAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var result = new ValidationResult();

            try
            {
                // Validate level and experience consistency;
                var expectedExpForLevel = _experienceSystem.CalculateExperienceForLevel(CurrentLevel);
                if (CurrentExperience < expectedExpForLevel)
                {
                    result.Errors.Add($"Experience ({CurrentExperience}) is less than required for level {CurrentLevel} ({expectedExpForLevel})");
                }

                // Validate skill points;
                var expectedSkillPoints = CalculateExpectedSkillPoints();
                if (TotalSkillPointsEarned != expectedSkillPoints)
                {
                    result.Warnings.Add($"Skill points mismatch. Expected: {expectedSkillPoints}, Actual: {TotalSkillPointsEarned}");
                }

                // Validate attribute points;
                var expectedAttributePoints = CalculateExpectedAttributePoints();
                if (TotalAttributePointsEarned != expectedAttributePoints)
                {
                    result.Warnings.Add($"Attribute points mismatch. Expected: {expectedAttributePoints}, Actual: {TotalAttributePointsEarned}");
                }

                // Validate attribute values;
                foreach (var attribute in Attributes.GetAllAttributes())
                {
                    var cap = GetAttributeCap(attribute.Name);
                    if (attribute.Value > cap)
                    {
                        result.Errors.Add($"Attribute '{attribute.Name}' exceeds cap ({attribute.Value} > {cap})");
                    }
                }

                result.IsValid = !result.Errors.Any();

                _logger.LogDebug($"Progression state validation completed. Valid: {result.IsValid}, Errors: {result.Errors.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating progression state");
                result.Errors.Add($"Validation error: {ex.Message}");
                result.IsValid = false;
            }

            return await Task.FromResult(result);
        }

        #endregion;

        #region Private Methods;

        private void InitializeSystems()
        {
            _achievements = new ConcurrentDictionary<string, Achievement>();
            _milestones = new ConcurrentDictionary<string, Milestone>();
            _progressionEvents = new ConcurrentDictionary<string, ProgressionEvent>();
        }

        private void InitializeDefaults()
        {
            Configuration = new ProgressionConfiguration;
            {
                MaxLevel = DEFAULT_MAX_LEVEL,
                ExponentialGrowthBase = EXPONENTIAL_GROWTH_BASE,
                BaseExperienceRequired = BASE_EXPERIENCE_REQUIRED,
                SkillPointsPerLevel = 1,
                AttributePointsPerLevel = 1,
                EnableAchievements = true,
                EnableMilestones = true,
                EnableModifiers = true,
                AutoSaveIntervalMinutes = 10,
                ExperienceMultiplier = 1.0;
            };

            Attributes = new CharacterAttributes();
            Statistics = new ProgressionStatistics();
            ActiveModifiers = new List<ProgressionModifier>();
            AchievementProgress = new AchievementProgress();
            SessionData = new ProgressionSessionData();
        }

        private void ApplyConfiguration()
        {
            // Apply configuration to experience system if it exists;
            if (_experienceSystem != null)
            {
                // Configuration would be applied here;
            }
        }

        private void InitializeCharacter()
        {
            // Set base attributes based on class or defaults;
            if (CharacterClass != null)
            {
                Attributes = CharacterClass.BaseAttributes.Clone();
            }
            else;
            {
                Attributes = new CharacterAttributes;
                {
                    Strength = new Attribute("Strength", 10),
                    Dexterity = new Attribute("Dexterity", 10),
                    Intelligence = new Attribute("Intelligence", 10),
                    Constitution = new Attribute("Constitution", 10),
                    Wisdom = new Attribute("Wisdom", 10),
                    Charisma = new Attribute("Charisma", 10)
                };
            }

            // Initialize points;
            AvailableSkillPoints = 0;
            AvailableAttributePoints = 0;
            TotalSkillPointsEarned = 0;
            TotalAttributePointsEarned = 0;

            _logger.LogDebug("Character initialized with base attributes");
        }

        private void InitializePeriodicSaveTimer()
        {
            if (Configuration.AutoSaveIntervalMinutes <= 0)
                return;

            var intervalMs = Configuration.AutoSaveIntervalMinutes * 60 * 1000;
            _periodicSaveTimer = new System.Timers.Timer(intervalMs);
            _periodicSaveTimer.Elapsed += async (sender, e) => await OnPeriodicSaveTimerElapsedAsync();
            _periodicSaveTimer.AutoReset = true;
            _periodicSaveTimer.Start();

            _logger.LogDebug($"Periodic save timer initialized ({Configuration.AutoSaveIntervalMinutes} minute interval)");
        }

        private async Task OnPeriodicSaveTimerElapsedAsync()
        {
            try
            {
                if (_disposed || !_isInitialized)
                    return;

                await SaveAsync();
                _logger.LogTrace("Auto-save completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic save timer");
            }
        }

        private void WireUpEvents()
        {
            if (_experienceSystem != null)
            {
                _experienceSystem.ExperienceGained += (sender, e) =>
                {
                    // Forward experience gained event;
                    OnExperienceGained(e.Result, e.Result.Source);
                };

                _experienceSystem.LevelUp += (sender, e) =>
                {
                    // Process level up;
                    var levelUpResult = ProcessLevelUpRewards(e.PreviousLevel, e.NewLevel);
                    OnLevelUp(e.PreviousLevel, e.NewLevel, levelUpResult);
                };
            }

            if (_skillSystem != null)
            {
                // Wire up skill system events if needed;
            }

            if (_questManager != null)
            {
                // Wire up quest manager events if needed;
            }
        }

        private async Task LoadSaveDataAsync(ProgressionSaveData saveData)
        {
            // Load basic data;
            AvailableSkillPoints = saveData.AvailableSkillPoints;
            AvailableAttributePoints = saveData.AvailableAttributePoints;
            TotalSkillPointsEarned = saveData.TotalSkillPointsEarned;
            TotalAttributePointsEarned = saveData.TotalAttributePointsEarned;

            // Load attributes;
            if (saveData.Attributes != null)
            {
                Attributes = saveData.Attributes;
            }

            // Load configuration;
            if (saveData.Configuration != null)
            {
                Configuration = saveData.Configuration;
            }

            // Load statistics;
            if (saveData.Statistics != null)
            {
                Statistics = saveData.Statistics;
            }

            // Load achievement progress;
            if (saveData.AchievementProgress != null)
            {
                AchievementProgress = saveData.AchievementProgress;
            }

            // Load character class;
            if (saveData.CharacterClass != null)
            {
                CharacterClass = saveData.CharacterClass;
            }

            // Load active modifiers;
            if (saveData.ActiveModifiers != null)
            {
                ActiveModifiers = saveData.ActiveModifiers.ToList();
            }

            // Load session data;
            if (saveData.SessionData != null)
            {
                SessionData = saveData.SessionData;
            }

            _logger.LogInformation($"Loaded progression save data. Level: {saveData.CurrentLevel}, Exp: {saveData.CurrentExperience}");
        }

        private async Task LoadAchievementsAsync()
        {
            // This would typically load from database or configuration;
            // For now, create some default achievements;

            var defaultAchievements = new[]
            {
                new Achievement;
                {
                    Id = "first_level",
                    Name = "First Step",
                    Description = "Reach level 2",
                    Category = "Leveling",
                    Type = AchievementType.Level,
                    TargetProgress = 2,
                    Rewards = new AchievementRewards { Experience = 100 }
                },
                new Achievement;
                {
                    Id = "skill_master",
                    Name = "Skill Master",
                    Description = "Learn 10 skills",
                    Category = "Skills",
                    Type = AchievementType.Skill,
                    TargetProgress = 10,
                    Rewards = new AchievementRewards { SkillPoints = 5 }
                }
            };

            foreach (var achievement in defaultAchievements)
            {
                _achievements.TryAdd(achievement.Id, achievement);
            }

            await Task.CompletedTask;
        }

        private async Task LoadMilestonesAsync()
        {
            // This would typically load from database or configuration;
            // For now, create some default milestones;

            var defaultMilestones = new[]
            {
                new Milestone;
                {
                    Id = "level_10",
                    Name = "Level 10",
                    Description = "Reach level 10",
                    Type = MilestoneType.Level,
                    TargetValue = 10,
                    Rewards = new MilestoneRewards { AttributePoints = 2 }
                },
                new Milestone;
                {
                    Id = "exp_100k",
                    Name = "100k Experience",
                    Description = "Earn 100,000 experience points",
                    Type = MilestoneType.Experience,
                    TargetValue = 100000,
                    Rewards = new MilestoneRewards { Experience = 5000 }
                }
            };

            foreach (var milestone in defaultMilestones)
            {
                _milestones.TryAdd(milestone.Id, milestone);
            }

            await Task.CompletedTask;
        }

        private long ApplyExperienceModifiers(long baseAmount, string source)
        {
            var modifiedAmount = (double)baseAmount;

            // Apply global experience multiplier;
            modifiedAmount *= Configuration.ExperienceMultiplier;

            // Apply active modifiers;
            var experienceModifiers = ActiveModifiers;
                .Where(m => m.ModifierType == ModifierType.ExperienceGain && m.IsActive)
                .ToList();

            foreach (var modifier in experienceModifiers)
            {
                modifiedAmount *= (1 + modifier.Value);
            }

            // Apply source-specific modifiers;
            if (!string.IsNullOrEmpty(source))
            {
                var sourceModifiers = ActiveModifiers;
                    .Where(m => m.ModifierType == ModifierType.SourceSpecific &&
                               m.Source == source && m.IsActive)
                    .ToList();

                foreach (var modifier in sourceModifiers)
                {
                    modifiedAmount *= (1 + modifier.Value);
                }
            }

            return (long)modifiedAmount;
        }

        private LevelUpResult ProcessLevelUpRewards(int previousLevel, int newLevel)
        {
            var rewards = new List<LevelUpReward>();

            // Calculate skill points gained;
            var skillPointsGained = CalculateSkillPointsForLevelUp(previousLevel, newLevel);
            if (skillPointsGained > 0)
            {
                AddSkillPoints(skillPointsGained, $"Level up: {newLevel}");
                rewards.Add(new LevelUpReward;
                {
                    Type = RewardType.SkillPoints,
                    Amount = skillPointsGained,
                    Description = $"{skillPointsGained} skill points"
                });
            }

            // Calculate attribute points gained;
            var attributePointsGained = CalculateAttributePointsForLevelUp(previousLevel, newLevel);
            if (attributePointsGained > 0)
            {
                AddAttributePoints(attributePointsGained, $"Level up: {newLevel}");
                rewards.Add(new LevelUpReward;
                {
                    Type = RewardType.AttributePoints,
                    Amount = attributePointsGained,
                    Description = $"{attributePointsGained} attribute points"
                });
            }

            // Apply class-specific level up rewards;
            var classRewards = GetClassLevelUpRewards(newLevel);
            rewards.AddRange(classRewards);

            // Update statistics;
            Statistics.LevelUps++;
            Statistics.LastLevelUp = DateTime.UtcNow;

            return new LevelUpResult;
            {
                PreviousLevel = previousLevel,
                NewLevel = newLevel,
                LevelsGained = newLevel - previousLevel,
                SkillPointsGained = skillPointsGained,
                AttributePointsGained = attributePointsGained,
                Rewards = rewards,
                Timestamp = DateTime.UtcNow;
            };
        }

        private async Task ProcessLevelUpRewardsAsync(int previousLevel, int newLevel)
        {
            var result = ProcessLevelUpRewards(previousLevel, newLevel);

            // Check for level-based achievements;
            await UpdateAchievementProgressAsync("first_level", 1);

            // Check for level milestones;
            await CheckLevelMilestonesAsync();
        }

        private int CalculateSkillPointsForLevelUp(int previousLevel, int newLevel)
        {
            var totalPoints = 0;

            for (int level = previousLevel + 1; level <= newLevel; level++)
            {
                // Base skill points per level;
                totalPoints += Configuration.SkillPointsPerLevel;

                // Additional points at certain levels;
                if (level % 5 == 0)
                {
                    totalPoints += 1; // Bonus point every 5 levels;
                }

                if (level % 10 == 0)
                {
                    totalPoints += 2; // Extra bonus every 10 levels;
                }
            }

            return totalPoints;
        }

        private int CalculateAttributePointsForLevelUp(int previousLevel, int newLevel)
        {
            var totalPoints = 0;

            for (int level = previousLevel + 1; level <= newLevel; level++)
            {
                // Base attribute points per level;
                totalPoints += Configuration.AttributePointsPerLevel;

                // Additional points at certain levels;
                if (level % 10 == 0)
                {
                    totalPoints += 1; // Bonus point every 10 levels;
                }
            }

            return totalPoints;
        }

        private List<LevelUpReward> GetClassLevelUpRewards(int level)
        {
            var rewards = new List<LevelUpReward>();

            if (CharacterClass?.ClassBonuses == null)
                return rewards;

            // Check for level-specific class rewards;
            if (CharacterClass.ClassBonuses.LevelRewards.TryGetValue(level, out var levelRewards))
            {
                rewards.AddRange(levelRewards);
            }

            return rewards;
        }

        private List<LevelUpReward> GetLevelUpRewards(int level)
        {
            var rewards = new List<LevelUpReward>();

            // Skill points;
            var skillPoints = CalculateSkillPointsForLevelUp(level - 1, level);
            if (skillPoints > 0)
            {
                rewards.Add(new LevelUpReward;
                {
                    Type = RewardType.SkillPoints,
                    Amount = skillPoints,
                    Description = $"{skillPoints} skill points"
                });
            }

            // Attribute points;
            var attributePoints = CalculateAttributePointsForLevelUp(level - 1, level);
            if (attributePoints > 0)
            {
                rewards.Add(new LevelUpReward;
                {
                    Type = RewardType.AttributePoints,
                    Amount = attributePoints,
                    Description = $"{attributePoints} attribute points"
                });
            }

            // Class rewards;
            rewards.AddRange(GetClassLevelUpRewards(level));

            return rewards;
        }

        private int GetNextSkillPointLevel()
        {
            var currentLevel = CurrentLevel;

            for (int level = currentLevel + 1; level <= Configuration.MaxLevel; level++)
            {
                var skillPoints = CalculateSkillPointsForLevelUp(level - 1, level);
                if (skillPoints > 0)
                {
                    return level;
                }
            }

            return -1; // No more skill points;
        }

        private int GetNextAttributePointLevel()
        {
            var currentLevel = CurrentLevel;

            for (int level = currentLevel + 1; level <= Configuration.MaxLevel; level++)
            {
                var attributePoints = CalculateAttributePointsForLevelUp(level - 1, level);
                if (attributePoints > 0)
                {
                    return level;
                }
            }

            return -1; // No more attribute points;
        }

        private double CalculateSkillPointProgress()
        {
            var nextSkillPointLevel = GetNextSkillPointLevel();
            if (nextSkillPointLevel == -1)
                return 100;

            var progress = (double)(CurrentLevel % 5) / 5 * 100;
            return progress;
        }

        private double CalculateAttributePointProgress()
        {
            var nextAttributePointLevel = GetNextAttributePointLevel();
            if (nextAttributePointLevel == -1)
                return 100;

            var progress = (double)(CurrentLevel % 10) / 10 * 100;
            return progress;
        }

        private async Task<bool> CanLearnSkillAsync(string skillId)
        {
            // Check level requirement;
            var skill = _skillSystem.GetSkillTemplate(skillId);
            if (skill == null)
                return false;

            if (CurrentLevel < skill.RequiredLevel)
                return false;

            // Check attribute requirements;
            foreach (var requirement in skill.AttributeRequirements)
            {
                var attributeValue = Attributes.GetAttributeValue(requirement.Key)?.Value ?? 0;
                if (attributeValue < requirement.Value)
                    return false;
            }

            // Check prerequisite skills;
            foreach (var prerequisiteId in skill.PrerequisiteSkillIds)
            {
                var prerequisiteSkill = _skillSystem.GetSkill(prerequisiteId);
                if (prerequisiteSkill == null || !prerequisiteSkill.IsLearned)
                    return false;
            }

            return await Task.FromResult(true);
        }

        private async Task CheckSkillAchievementsAsync()
        {
            var learnedSkills = GetLearnedSkills();

            // Check for skill count achievements;
            await UpdateAchievementProgressAsync("skill_master", learnedSkills.Count);
        }

        private AchievementRewards ApplyAchievementRewards(Achievement achievement)
        {
            var rewards = achievement.Rewards ?? new AchievementRewards();

            // Apply experience reward;
            if (rewards.Experience > 0)
            {
                AddExperienceAsync(rewards.Experience, $"Achievement: {achievement.Name}").Wait();
            }

            // Apply skill points reward;
            if (rewards.SkillPoints > 0)
            {
                AddSkillPoints(rewards.SkillPoints, $"Achievement: {achievement.Name}");
            }

            // Apply attribute points reward;
            if (rewards.AttributePoints > 0)
            {
                AddAttributePoints(rewards.AttributePoints, $"Achievement: {achievement.Name}");
            }

            return rewards;
        }

        private async Task CheckLevelMilestonesAsync()
        {
            foreach (var milestone in _milestones.Values.Where(m => m.Type == MilestoneType.Level))
            {
                if (CurrentLevel >= milestone.TargetValue && !milestone.IsReached)
                {
                    await ReachMilestoneAsync(milestone);
                }
            }
        }

        private async Task CheckExperienceMilestonesAsync()
        {
            foreach (var milestone in _milestones.Values.Where(m => m.Type == MilestoneType.Experience))
            {
                if (TotalExperienceEarned >= milestone.TargetValue && !milestone.IsReached)
                {
                    await ReachMilestoneAsync(milestone);
                }
            }
        }

        private async Task CheckSkillMilestonesAsync()
        {
            // Implement skill milestone checking;
            await Task.CompletedTask;
        }

        private async Task CheckAchievementMilestonesAsync()
        {
            foreach (var milestone in _milestones.Values.Where(m => m.Type == MilestoneType.Achievement))
            {
                if (AchievementProgress.UnlockedAchievements >= milestone.TargetValue && !milestone.IsReached)
                {
                    await ReachMilestoneAsync(milestone);
                }
            }
        }

        private async Task CheckTimePlayedMilestonesAsync()
        {
            // Implement time played milestone checking;
            await Task.CompletedTask;
        }

        private async Task ReachMilestoneAsync(Milestone milestone)
        {
            milestone.IsReached = true;
            milestone.ReachedTime = DateTime.UtcNow;

            // Apply rewards;
            ApplyMilestoneRewards(milestone);

            // Raise event;
            OnMilestoneReached(milestone);

            _logger.LogInformation($"Milestone reached: {milestone.Name}");

            await Task.CompletedTask;
        }

        private void ApplyMilestoneRewards(Milestone milestone)
        {
            var rewards = milestone.Rewards;
            if (rewards == null)
                return;

            // Apply experience reward;
            if (rewards.Experience > 0)
            {
                AddExperienceAsync(rewards.Experience, $"Milestone: {milestone.Name}").Wait();
            }

            // Apply skill points reward;
            if (rewards.SkillPoints > 0)
            {
                AddSkillPoints(rewards.SkillPoints, $"Milestone: {milestone.Name}");
            }

            // Apply attribute points reward;
            if (rewards.AttributePoints > 0)
            {
                AddAttributePoints(rewards.AttributePoints, $"Milestone: {milestone.Name}");
            }
        }

        private MilestoneProgress CalculateMilestoneProgress(Milestone milestone)
        {
            double currentProgress = 0;
            double targetProgress = milestone.TargetValue;

            switch (milestone.Type)
            {
                case MilestoneType.Level:
                    currentProgress = CurrentLevel;
                    break;

                case MilestoneType.Experience:
                    currentProgress = TotalExperienceEarned;
                    break;

                case MilestoneType.Skill:
                    // Calculate based on skills;
                    break;

                case MilestoneType.Achievement:
                    currentProgress = AchievementProgress.UnlockedAchievements;
                    break;

                case MilestoneType.TimePlayed:
                    // Calculate based on time played;
                    break;
            }

            var progressPercentage = targetProgress > 0 ? (currentProgress / targetProgress * 100) : 0;

            return new MilestoneProgress;
            {
                Milestone = milestone,
                CurrentProgress = currentProgress,
                TargetProgress = targetProgress,
                ProgressPercentage = progressPercentage,
                IsReached = milestone.IsReached;
            };
        }

        private void ApplyClassBonuses(CharacterClass characterClass)
        {
            if (characterClass?.ClassBonuses == null)
                return;

            // Apply attribute bonuses;
            foreach (var bonus in characterClass.ClassBonuses.AttributeBonuses)
            {
                Attributes.IncreaseAttribute(bonus.Key, bonus.Value);
            }

            // Apply skill bonuses;
            foreach (var skillBonus in characterClass.ClassBonuses.SkillBonuses)
            {
                // This would apply skill bonuses through the skill system;
            }
        }

        private ClassBonuses GetClassBonuses(CharacterClass characterClass)
        {
            return characterClass?.ClassBonuses ?? new ClassBonuses();
        }

        private int GetAttributeCap(string attributeName)
        {
            // Base cap;
            var baseCap = 100;

            // Apply class bonuses to cap;
            if (CharacterClass?.ClassBonuses?.AttributeCaps != null)
            {
                if (CharacterClass.ClassBonuses.AttributeCaps.TryGetValue(attributeName, out var classCap))
                {
                    baseCap = classCap;
                }
            }

            // Apply modifier bonuses to cap;
            var capModifiers = ActiveModifiers;
                .Where(m => m.ModifierType == ModifierType.AttributeCap &&
                           m.TargetAttribute == attributeName && m.IsActive)
                .ToList();

            foreach (var modifier in capModifiers)
            {
                baseCap += (int)modifier.Value;
            }

            return baseCap;
        }

        private void ApplyModifierEffects(ProgressionModifier modifier)
        {
            // Apply modifier effects based on type;
            switch (modifier.ModifierType)
            {
                case ModifierType.ExperienceGain:
                    // Already handled in experience calculation;
                    break;

                case ModifierType.SkillPointGain:
                    // Would apply to skill point calculations;
                    break;

                case ModifierType.AttributePointGain:
                    // Would apply to attribute point calculations;
                    break;

                case ModifierType.AttributeBonus:
                    if (!string.IsNullOrEmpty(modifier.TargetAttribute))
                    {
                        Attributes.IncreaseAttribute(modifier.TargetAttribute, (int)modifier.Value);
                    }
                    break;
            }

            modifier.IsActive = true;
        }

        private void RemoveModifierEffects(ProgressionModifier modifier)
        {
            // Remove modifier effects based on type;
            switch (modifier.ModifierType)
            {
                case ModifierType.AttributeBonus:
                    if (!string.IsNullOrEmpty(modifier.TargetAttribute))
                    {
                        Attributes.DecreaseAttribute(modifier.TargetAttribute, (int)modifier.Value);
                    }
                    break;
            }

            modifier.IsActive = false;
        }

        private void ScheduleModifierRemoval(ProgressionModifier modifier)
        {
            Task.Delay(modifier.Duration).ContinueWith(t =>
            {
                if (!_disposed && _isInitialized)
                {
                    RemoveModifier(modifier.Id);
                }
            });
        }

        private int CalculateExpectedSkillPoints()
        {
            var expectedPoints = 0;

            for (int level = 1; level <= CurrentLevel; level++)
            {
                expectedPoints += CalculateSkillPointsForLevelUp(level - 1, level);
            }

            // Add points from other sources (achievements, milestones, etc.)
            expectedPoints += Statistics.SkillPointsFromAchievements;
            expectedPoints += Statistics.SkillPointsFromMilestones;

            return expectedPoints;
        }

        private int CalculateExpectedAttributePoints()
        {
            var expectedPoints = 0;

            for (int level = 1; level <= CurrentLevel; level++)
            {
                expectedPoints += CalculateAttributePointsForLevelUp(level - 1, level);
            }

            // Add points from other sources (achievements, milestones, etc.)
            expectedPoints += Statistics.AttributePointsFromAchievements;
            expectedPoints += Statistics.AttributePointsFromMilestones;

            return expectedPoints;
        }

        private async Task<TimeSpan> EstimateTimeToNextLevelAsync()
        {
            if (CurrentLevel >= Configuration.MaxLevel)
                return TimeSpan.Zero;

            // Calculate experience per minute based on recent activity;
            var recentExp = SessionData.ExperienceLastHour;
            var expPerMinute = recentExp > 0 ? recentExp / 60.0 : 100.0; // Default 100 exp/minute;

            // Calculate experience needed for next level;
            var expNeeded = ExperienceToNextLevel;

            // Calculate estimated time;
            var minutesNeeded = expNeeded / expPerMinute;
            var estimatedTime = TimeSpan.FromMinutes(minutesNeeded);

            return await Task.FromResult(estimatedTime);
        }

        private double CalculateExperiencePerHour()
        {
            var sessionDuration = DateTime.UtcNow - SessionData.SessionStartTime;
            if (sessionDuration.TotalHours < 0.1) // Less than 6 minutes;
                return 0;

            return SessionData.TotalSessionExperience / sessionDuration.TotalHours;
        }

        private double CalculateLevelingEfficiency()
        {
            if (CurrentLevel <= 1)
                return 0;

            var totalExp = TotalExperienceEarned;
            var averageExpPerLevel = totalExp / (CurrentLevel - 1);

            // Lower average exp per level = more efficient;
            var efficiency = 100.0 / Math.Max(1, averageExpPerLevel / 1000.0);

            return Math.Min(efficiency, 100);
        }

        private void UpdateStatisticsForExperience(Experience.ExperienceGainResult result)
        {
            Statistics.TotalExperienceGained += result.ExperienceAdded;
            Statistics.ExperienceGainCount++;

            // Update session data for experience per hour calculation;
            var now = DateTime.UtcNow;
            var oneHourAgo = now.AddHours(-1);

            // Remove experience older than 1 hour;
            SessionData.ExperienceLastHour = SessionData.RecentExperience;
                .Where(e => e.Timestamp > oneHourAgo)
                .Sum(e => e.Amount);

            // Add new experience;
            SessionData.RecentExperience.Add(new ExperienceRecord;
            {
                Amount = result.ExperienceAdded,
                Timestamp = now,
                Source = result.Source;
            });

            // Keep only last 1000 records;
            if (SessionData.RecentExperience.Count > 1000)
            {
                SessionData.RecentExperience.RemoveRange(0, SessionData.RecentExperience.Count - 1000);
            }
        }

        private void ValidateNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Progression));
            }
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new ProgressionNotInitializedException("Progression system must be initialized before use");
            }
        }

        #endregion;

        #region Event Invokers;

        private void OnLevelUp(int previousLevel, int newLevel, LevelUpResult result)
        {
            LevelUp?.Invoke(this, new LevelUpEventArgs(previousLevel, newLevel, result));
        }

        private void OnExperienceGained(Experience.ExperienceGainResult result, string source)
        {
            ExperienceGained?.Invoke(this, new ExperienceGainedEventArgs(result, source));
        }

        private void OnSkillPointGained(int amount, int total, string source)
        {
            SkillPointGained?.Invoke(this, new SkillPointGainedEventArgs(amount, total, source));
        }

        private void OnAttributePointGained(int amount, int total, string source)
        {
            AttributePointGained?.Invoke(this, new AttributePointGainedEventArgs(amount, total, source));
        }

        private void OnAttributeIncreased(string attribute, int amount, int previousValue, int newValue)
        {
            AttributeIncreased?.Invoke(this, new AttributeIncreasedEventArgs(attribute, amount, previousValue, newValue));
        }

        private void OnSkillLearned(Skill skill, int cost)
        {
            SkillLearned?.Invoke(this, new SkillLearnedEventArgs(skill, cost));
        }

        private void OnSkillUpgraded(Skill skill, int previousLevel, int newLevel, int cost)
        {
            SkillUpgraded?.Invoke(this, new SkillUpgradedEventArgs(skill, previousLevel, newLevel, cost));
        }

        private void OnAchievementUnlocked(Achievement achievement, AchievementRewards rewards)
        {
            AchievementUnlocked?.Invoke(this, new AchievementUnlockedEventArgs(achievement, rewards));
        }

        private void OnMilestoneReached(Milestone milestone)
        {
            MilestoneReached?.Invoke(this, new MilestoneReachedEventArgs(milestone));
        }

        private void OnModifiersChanged(ModifierChangeType changeType, ProgressionModifier modifier)
        {
            ModifiersChanged?.Invoke(this, new ModifiersChangedEventArgs(changeType, modifier));
        }

        private void OnCharacterClassChanged(CharacterClass previousClass, CharacterClass newClass, int cost)
        {
            CharacterClassChanged?.Invoke(this, new CharacterClassChangedEventArgs(previousClass, newClass, cost));
        }

        private void OnProgressionReset(ProgressionResetResult result, bool keepAchievements)
        {
            ProgressionReset?.Invoke(this, new ProgressionResetEventArgs(result, keepAchievements));
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
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    _experienceSystem?.Dispose();

                    if (_periodicSaveTimer != null)
                    {
                        _periodicSaveTimer.Stop();
                        _periodicSaveTimer.Dispose();
                        _periodicSaveTimer = null;
                    }

                    // Clear collections;
                    _achievements.Clear();
                    _milestones.Clear();
                    _progressionEvents.Clear();
                    ActiveModifiers.Clear();

                    // Unsubscribe from events;
                    LevelUp = null;
                    ExperienceGained = null;
                    SkillPointGained = null;
                    AttributePointGained = null;
                    AttributeIncreased = null;
                    SkillLearned = null;
                    SkillUpgraded = null;
                    AchievementUnlocked = null;
                    MilestoneReached = null;
                    ModifiersChanged = null;
                    CharacterClassChanged = null;
                    ProgressionReset = null;
                }

                _disposed = true;
                _logger.LogInformation("Progression system disposed");
            }
        }

        ~Progression()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes and Enums;

        /// <summary>
        /// Progression configuration;
        /// </summary>
        public class ProgressionConfiguration : ICloneable;
        {
            public int MaxLevel { get; set; } = DEFAULT_MAX_LEVEL;
            public double ExponentialGrowthBase { get; set; } = EXPONENTIAL_GROWTH_BASE;
            public int BaseExperienceRequired { get; set; } = BASE_EXPERIENCE_REQUIRED;
            public int SkillPointsPerLevel { get; set; } = 1;
            public int AttributePointsPerLevel { get; set; } = 1;
            public double ExperienceMultiplier { get; set; } = 1.0;
            public bool EnableAchievements { get; set; } = true;
            public bool EnableMilestones { get; set; } = true;
            public bool EnableModifiers { get; set; } = true;
            public int AutoSaveIntervalMinutes { get; set; } = 10;
            public bool AllowAttributeResets { get; set; } = true;
            public bool AllowClassChanges { get; set; } = true;
            public int AttributeResetCost { get; set; } = 1000;
            public int ClassChangeCost { get; set; } = 5000;
            public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();

            public object Clone()
            {
                return new ProgressionConfiguration;
                {
                    MaxLevel = this.MaxLevel,
                    ExponentialGrowthBase = this.ExponentialGrowthBase,
                    BaseExperienceRequired = this.BaseExperienceRequired,
                    SkillPointsPerLevel = this.SkillPointsPerLevel,
                    AttributePointsPerLevel = this.AttributePointsPerLevel,
                    ExperienceMultiplier = this.ExperienceMultiplier,
                    EnableAchievements = this.EnableAchievements,
                    EnableMilestones = this.EnableMilestones,
                    EnableModifiers = this.EnableModifiers,
                    AutoSaveIntervalMinutes = this.AutoSaveIntervalMinutes,
                    AllowAttributeResets = this.AllowAttributeResets,
                    AllowClassChanges = this.AllowClassChanges,
                    AttributeResetCost = this.AttributeResetCost,
                    ClassChangeCost = this.ClassChangeCost,
                    AdvancedSettings = new Dictionary<string, object>(this.AdvancedSettings)
                };
            }
        }

        /// <summary>
        /// Progression statistics;
        /// </summary>
        public class ProgressionStatistics : ICloneable;
        {
            // Leveling statistics;
            public int LevelUps { get; set; }
            public DateTime LastLevelUp { get; set; }
            public long TotalExperienceGained { get; set; }
            public int ExperienceGainCount { get; set; }

            // Skill statistics;
            public int SkillsLearned { get; set; }
            public int SkillUpgrades { get; set; }
            public int TotalSkillPointsEarned { get; set; }
            public int TotalSkillPointsSpent { get; set; }
            public int SkillPointsFromAchievements { get; set; }
            public int SkillPointsFromMilestones { get; set; }
            public DateTime LastSkillLearned { get; set; }
            public DateTime LastSkillUpgrade { get; set; }
            public DateTime LastSkillPointGain { get; set; }
            public DateTime LastSkillPointSpend { get; set; }

            // Attribute statistics;
            public int TotalAttributePointsEarned { get; set; }
            public int TotalAttributePointsSpent { get; set; }
            public int AttributePointsFromAchievements { get; set; }
            public int AttributePointsFromMilestones { get; set; }
            public int AttributeResets { get; set; }
            public DateTime LastAttributePointGain { get; set; }
            public DateTime LastAttributeIncrease { get; set; }
            public DateTime LastAttributeReset { get; set; }

            // Achievement statistics;
            public int AchievementsUnlocked { get; set; }
            public DateTime LastAchievementUnlocked { get; set; }

            // Class statistics;
            public int ClassChanges { get; set; }
            public DateTime LastClassChange { get; set; }

            // Modifier statistics;
            public int ModifiersApplied { get; set; }
            public int ModifiersRemoved { get; set; }
            public int ModifiersCleared { get; set; }

            // Session statistics;
            public DateTime FirstPlayed { get; set; } = DateTime.UtcNow;
            public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

            public object Clone()
            {
                return new ProgressionStatistics;
                {
                    LevelUps = this.LevelUps,
                    LastLevelUp = this.LastLevelUp,
                    TotalExperienceGained = this.TotalExperienceGained,
                    ExperienceGainCount = this.ExperienceGainCount,
                    SkillsLearned = this.SkillsLearned,
                    SkillUpgrades = this.SkillUpgrades,
                    TotalSkillPointsEarned = this.TotalSkillPointsEarned,
                    TotalSkillPointsSpent = this.TotalSkillPointsSpent,
                    SkillPointsFromAchievements = this.SkillPointsFromAchievements,
                    SkillPointsFromMilestones = this.SkillPointsFromMilestones,
                    LastSkillLearned = this.LastSkillLearned,
                    LastSkillUpgrade = this.LastSkillUpgrade,
                    LastSkillPointGain = this.LastSkillPointGain,
                    LastSkillPointSpend = this.LastSkillPointSpend,
                    TotalAttributePointsEarned = this.TotalAttributePointsEarned,
                    TotalAttributePointsSpent = this.TotalAttributePointsSpent,
                    AttributePointsFromAchievements = this.AttributePointsFromAchievements,
                    AttributePointsFromMilestones = this.AttributePointsFromMilestones,
                    AttributeResets = this.AttributeResets,
                    LastAttributePointGain = this.LastAttributePointGain,
                    LastAttributeIncrease = this.LastAttributeIncrease,
                    LastAttributeReset = this.LastAttributeReset,
                    AchievementsUnlocked = this.AchievementsUnlocked,
                    LastAchievementUnlocked = this.LastAchievementUnlocked,
                    ClassChanges = this.ClassChanges,
                    LastClassChange = this.LastClassChange,
                    ModifiersApplied = this.ModifiersApplied,
                    ModifiersRemoved = this.ModifiersRemoved,
                    ModifiersCleared = this.ModifiersCleared,
                    FirstPlayed = this.FirstPlayed,
                    LastUpdated = this.LastUpdated;
                };
            }
        }

        /// <summary>
        /// Achievement progress tracking;
        /// </summary>
        public class AchievementProgress : ICloneable;
        {
            public int UnlockedAchievements { get; set; }
            public DateTime LastAchievementUnlocked { get; set; }
            public Dictionary<string, AchievementCategoryProgress> CategoryProgress { get; set; } = new Dictionary<string, AchievementCategoryProgress>();
            public Dictionary<string, int> TypeProgress { get; set; } = new Dictionary<string, int>();

            public object Clone()
            {
                return new AchievementProgress;
                {
                    UnlockedAchievements = this.UnlockedAchievements,
                    LastAchievementUnlocked = this.LastAchievementUnlocked,
                    CategoryProgress = new Dictionary<string, AchievementCategoryProgress>(this.CategoryProgress),
                    TypeProgress = new Dictionary<string, int>(this.TypeProgress)
                };
            }
        }

        /// <summary>
        /// Achievement category progress;
        /// </summary>
        public class AchievementCategoryProgress;
        {
            public int Total { get; set; }
            public int Unlocked { get; set; }
            public int InProgress { get; set; }
            public double CompletionPercentage => Total > 0 ? (double)Unlocked / Total * 100 : 0;
        }

        /// <summary>
        /// Progression session data;
        /// </summary>
        public class ProgressionSessionData : ICloneable;
        {
            public DateTime SessionStartTime { get; set; } = DateTime.UtcNow;
            public DateTime LastSaveTime { get; set; } = DateTime.UtcNow;
            public DateTime LastExperienceGain { get; set; }
            public long TotalSessionExperience { get; set; }
            public long ExperienceLastHour { get; set; }
            public List<ExperienceRecord> RecentExperience { get; set; } = new List<ExperienceRecord>();
            public int SessionLevelUps { get; set; }
            public int SessionSkillsLearned { get; set; }
            public int SessionAchievementsUnlocked { get; set; }

            public object Clone()
            {
                return new ProgressionSessionData;
                {
                    SessionStartTime = this.SessionStartTime,
                    LastSaveTime = this.LastSaveTime,
                    LastExperienceGain = this.LastExperienceGain,
                    TotalSessionExperience = this.TotalSessionExperience,
                    ExperienceLastHour = this.ExperienceLastHour,
                    RecentExperience = new List<ExperienceRecord>(this.RecentExperience),
                    SessionLevelUps = this.SessionLevelUps,
                    SessionSkillsLearned = this.SessionSkillsLearned,
                    SessionAchievementsUnlocked = this.SessionAchievementsUnlocked;
                };
            }
        }

        /// <summary>
        /// Experience record;
        /// </summary>
        public class ExperienceRecord;
        {
            public long Amount { get; set; }
            public DateTime Timestamp { get; set; }
            public string Source { get; set; }
            public Dictionary<string, object> Tags { get; set; }
        }

        /// <summary>
        /// Progression save data;
        /// </summary>
        public class ProgressionSaveData;
        {
            public int CurrentLevel { get; set; }
            public long CurrentExperience { get; set; }
            public long TotalExperienceEarned { get; set; }
            public int AvailableSkillPoints { get; set; }
            public int AvailableAttributePoints { get; set; }
            public int TotalSkillPointsEarned { get; set; }
            public int TotalAttributePointsEarned { get; set; }
            public CharacterAttributes Attributes { get; set; }
            public ProgressionConfiguration Configuration { get; set; }
            public ProgressionStatistics Statistics { get; set; }
            public AchievementProgress AchievementProgress { get; set; }
            public CharacterClass CharacterClass { get; set; }
            public List<ProgressionModifier> ActiveModifiers { get; set; }
            public ProgressionSessionData SessionData { get; set; }
            public DateTime SavedTime { get; set; }
        }

        /// <summary>
        /// Level progress details;
        /// </summary>
        public class LevelProgressDetails;
        {
            public int CurrentLevel { get; set; }
            public long CurrentExperience { get; set; }
            public long ExperienceForCurrentLevel { get; set; }
            public long ExperienceForNextLevel { get; set; }
            public long ExperienceInCurrentLevel { get; set; }
            public long ExperienceToNextLevel { get; set; }
            public double ProgressPercentage { get; set; }
            public bool IsMaxLevel { get; set; }
            public List<LevelUpReward> NextLevelRewards { get; set; } = new List<LevelUpReward>();
        }

        /// <summary>
        /// Level up result;
        /// </summary>
        public class LevelUpResult;
        {
            public int PreviousLevel { get; set; }
            public int NewLevel { get; set; }
            public int LevelsGained { get; set; }
            public int SkillPointsGained { get; set; }
            public int AttributePointsGained { get; set; }
            public List<LevelUpReward> Rewards { get; set; } = new List<LevelUpReward>();
            public bool IsForced { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Level up reward;
        /// </summary>
        public class LevelUpReward;
        {
            public RewardType Type { get; set; }
            public int Amount { get; set; }
            public string Description { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
        }

        /// <summary>
        /// Skill point information;
        /// </summary>
        public class SkillPointInfo;
        {
            public int AvailableSkillPoints { get; set; }
            public int TotalSkillPointsEarned { get; set; }
            public int SkillPointsSpent { get; set; }
            public int SkillPointsPerLevel { get; set; }
            public int NextSkillPointAtLevel { get; set; }
            public double SkillPointProgress { get; set; }
        }

        /// <summary>
        /// Skill point gain result;
        /// </summary>
        public class SkillPointGainResult;
        {
            public int SkillPointsAdded { get; set; }
            public int PreviousSkillPoints { get; set; }
            public int NewSkillPoints { get; set; }
            public string Source { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Attribute point information;
        /// </summary>
        public class AttributePointInfo;
        {
            public int AvailableAttributePoints { get; set; }
            public int TotalAttributePointsEarned { get; set; }
            public int AttributePointsSpent { get; set; }
            public int AttributePointsPerLevel { get; set; }
            public int NextAttributePointAtLevel { get; set; }
            public double AttributePointProgress { get; set; }
        }

        /// <summary>
        /// Attribute point gain result;
        /// </summary>
        public class AttributePointGainResult;
        {
            public int AttributePointsAdded { get; set; }
            public int PreviousAttributePoints { get; set; }
            public int NewAttributePoints { get; set; }
            public string Source { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Attribute increase result;
        /// </summary>
        public class AttributeIncreaseResult;
        {
            public string Attribute { get; set; }
            public int AmountIncreased { get; set; }
            public int PreviousValue { get; set; }
            public int NewValue { get; set; }
            public int RemainingAttributePoints { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Attribute reset result;
        /// </summary>
        public class AttributeResetResult;
        {
            public int SpentPointsRefunded { get; set; }
            public CharacterAttributes PreviousAttributes { get; set; }
            public CharacterAttributes NewAttributes { get; set; }
            public int NewAttributePoints { get; set; }
            public int ResetCost { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Skill learning result;
        /// </summary>
        public class SkillLearningResult;
        {
            public Skill Skill { get; set; }
            public int SkillPointsCost { get; set; }
            public int PreviousSkillPoints { get; set; }
            public int NewSkillPoints { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Skill upgrade result;
        /// </summary>
        public class SkillUpgradeResult;
        {
            public Skill Skill { get; set; }
            public int PreviousLevel { get; set; }
            public int NewLevel { get; set; }
            public int SkillPointsCost { get; set; }
            public int PreviousSkillPoints { get; set; }
            public int NewSkillPoints { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Skill mastery information;
        /// </summary>
        public class SkillMasteryInfo;
        {
            public int TotalSkills { get; set; }
            public int LearnedSkills { get; set; }
            public int MaxSkillLevel { get; set; }
            public double AverageSkillLevel { get; set; }
            public int TotalSkillLevels { get; set; }
            public int MasteryPercentage { get; set; }
        }

        /// <summary>
        /// Achievement;
        /// </summary>
        public class Achievement;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string Category { get; set; }
            public AchievementType Type { get; set; }
            public int TargetProgress { get; set; }
            public int CurrentProgress { get; set; }
            public bool IsUnlocked { get; set; }
            public AchievementRewards Rewards { get; set; }
            public DateTime? UnlockedTime { get; set; }
            public DateTime? LastProgressUpdate { get; set; }
            public Dictionary<string, object> ProgressData { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
        }

        /// <summary>
        /// Achievement rewards;
        /// </summary>
        public class AchievementRewards;
        {
            public int Experience { get; set; }
            public int SkillPoints { get; set; }
            public int AttributePoints { get; set; }
            public List<string> Unlocks { get; set; } = new List<string>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Achievement unlock result;
        /// </summary>
        public class AchievementUnlockResult;
        {
            public Achievement Achievement { get; set; }
            public AchievementRewards Rewards { get; set; }
            public bool AlreadyUnlocked { get; set; }
            public DateTime? UnlockedTime { get; set; }
        }

        /// <summary>
        /// Achievement progress result;
        /// </summary>
        public class AchievementProgressResult;
        {
            public Achievement Achievement { get; set; }
            public int PreviousProgress { get; set; }
            public int NewProgress { get; set; }
            public double ProgressPercentage { get; set; }
            public bool IsComplete { get; set; }
            public bool AlreadyUnlocked { get; set; }
            public Dictionary<string, object> ProgressData { get; set; }
        }

        /// <summary>
        /// Milestone;
        /// </summary>
        public class Milestone;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public MilestoneType Type { get; set; }
            public double TargetValue { get; set; }
            public bool IsReached { get; set; }
            public MilestoneRewards Rewards { get; set; }
            public DateTime? ReachedTime { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
        }

        /// <summary>
        /// Milestone rewards;
        /// </summary>
        public class MilestoneRewards;
        {
            public int Experience { get; set; }
            public int SkillPoints { get; set; }
            public int AttributePoints { get; set; }
            public List<string> Unlocks { get; set; } = new List<string>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Milestone progress;
        /// </summary>
        public class MilestoneProgress;
        {
            public Milestone Milestone { get; set; }
            public double CurrentProgress { get; set; }
            public double TargetProgress { get; set; }
            public double ProgressPercentage { get; set; }
            public bool IsReached { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        /// <summary>
        /// Character class;
        /// </summary>
        public class CharacterClass : ICloneable;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public CharacterAttributes BaseAttributes { get; set; }
            public ClassBonuses ClassBonuses { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

            public object Clone()
            {
                return new CharacterClass;
                {
                    Id = this.Id,
                    Name = this.Name,
                    Description = this.Description,
                    BaseAttributes = this.BaseAttributes?.Clone(),
                    ClassBonuses = this.ClassBonuses?.Clone(),
                    Metadata = new Dictionary<string, object>(this.Metadata)
                };
            }
        }

        /// <summary>
        /// Class bonuses;
        /// </summary>
        public class ClassBonuses : ICloneable;
        {
            public Dictionary<string, int> AttributeBonuses { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> AttributeCaps { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, SkillBonus> SkillBonuses { get; set; } = new Dictionary<string, SkillBonus>();
            public Dictionary<int, List<LevelUpReward>> LevelRewards { get; set; } = new Dictionary<int, List<LevelUpReward>>();

            public object Clone()
            {
                return new ClassBonuses;
                {
                    AttributeBonuses = new Dictionary<string, int>(this.AttributeBonuses),
                    AttributeCaps = new Dictionary<string, int>(this.AttributeCaps),
                    SkillBonuses = new Dictionary<string, SkillBonus>(this.SkillBonuses),
                    LevelRewards = new Dictionary<int, List<LevelUpReward>>(this.LevelRewards)
                };
            }
        }

        /// <summary>
        /// Skill bonus;
        /// </summary>
        public class SkillBonus;
        {
            public int LevelBonus { get; set; }
            public double DamageMultiplier { get; set; } = 1.0;
            public double CooldownReduction { get; set; } = 0;
            public Dictionary<string, object> AdditionalEffects { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Class change result;
        /// </summary>
        public class ClassChangeResult;
        {
            public CharacterClass PreviousClass { get; set; }
            public CharacterClass NewClass { get; set; }
            public int ChangeCost { get; set; }
            public DateTime Timestamp { get; set; }
            public ClassBonuses BonusesApplied { get; set; }
        }

        /// <summary>
        /// Progression modifier;
        /// </summary>
        public class ProgressionModifier : ICloneable;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public ModifierType ModifierType { get; set; }
            public double Value { get; set; }
            public string TargetAttribute { get; set; }
            public string Source { get; set; }
            public TimeSpan Duration { get; set; }
            public bool IsActive { get; set; }
            public DateTime? ActivatedTime { get; set; }
            public DateTime? DeactivatedTime { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

            public object Clone()
            {
                return new ProgressionModifier;
                {
                    Id = this.Id,
                    Name = this.Name,
                    Description = this.Description,
                    ModifierType = this.ModifierType,
                    Value = this.Value,
                    TargetAttribute = this.TargetAttribute,
                    Source = this.Source,
                    Duration = this.Duration,
                    IsActive = this.IsActive,
                    ActivatedTime = this.ActivatedTime,
                    DeactivatedTime = this.DeactivatedTime,
                    Parameters = new Dictionary<string, object>(this.Parameters)
                };
            }
        }

        /// <summary>
        /// Modifier addition result;
        /// </summary>
        public class ModifierAdditionResult;
        {
            public ProgressionModifier Modifier { get; set; }
            public bool AlreadyActive { get; set; }
            public DateTime? ActivatedTime { get; set; }
        }

        /// <summary>
        /// Progression event;
        /// </summary>
        public class ProgressionEvent;
        {
            public string Id { get; set; }
            public string Type { get; set; }
            public string Description { get; set; }
            public DateTime Timestamp { get; set; }
            public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Progression report;
        /// </summary>
        public class ProgressionReport;
        {
            public int CharacterLevel { get; set; }
            public long TotalExperience { get; set; }
            public LevelProgressDetails LevelProgress { get; set; }
            public SkillPointInfo SkillPointInfo { get; set; }
            public AttributePointInfo AttributePointInfo { get; set; }
            public SkillMasteryInfo SkillMasteryInfo { get; set; }
            public AchievementProgress AchievementProgress { get; set; }
            public ProgressionStatistics Statistics { get; set; }
            public List<ProgressionModifier> ActiveModifiers { get; set; }
            public ProgressionSessionData SessionData { get; set; }
            public TimeSpan? EstimatedTimeToNextLevel { get; set; }
            public double ExperiencePerHour { get; set; }
            public double LevelingEfficiency { get; set; }
            public DateTime GeneratedTime { get; set; }
        }

        /// <summary>
        /// Progression reset result;
        /// </summary>
        public class ProgressionResetResult;
        {
            public ProgressionSaveData PreviousState { get; set; }
            public ProgressionSaveData NewState { get; set; }
            public bool KeepAchievements { get; set; }
            public DateTime ResetTime { get; set; }
        }

        /// <summary>
        /// Validation result;
        /// </summary>
        public class ValidationResult;
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
        }

        /// <summary>
        /// Experience gain result (from Experience system)
        /// </summary>
        public class ExperienceGainResult;
        {
            public long ExperienceAdded { get; set; }
            public long OriginalAmount { get; set; }
            public long MultipliedAmount { get; set; }
            public bool LeveledUp { get; set; }
            public int PreviousLevel { get; set; }
            public int NewLevel { get; set; }
            public long PreviousExperience { get; set; }
            public long NewExperience { get; set; }
            public string Source { get; set; }
            public Dictionary<string, object> Tags { get; set; }
            public string Reason { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Reward types;
        /// </summary>
        public enum RewardType;
        {
            Experience,
            SkillPoints,
            AttributePoints,
            Item,
            Currency,
            Unlock,
            Custom;
        }

        /// <summary>
        /// Achievement types;
        /// </summary>
        public enum AchievementType;
        {
            Level,
            Skill,
            Quest,
            Combat,
            Exploration,
            Collection,
            Crafting,
            Social,
            Time,
            Hidden;
        }

        /// <summary>
        /// Milestone types;
        /// </summary>
        public enum MilestoneType;
        {
            Level,
            Experience,
            Skill,
            Achievement,
            TimePlayed,
            Custom;
        }

        /// <summary>
        /// Modifier types;
        /// </summary>
        public enum ModifierType;
        {
            ExperienceGain,
            SkillPointGain,
            AttributePointGain,
            AttributeBonus,
            SkillBonus,
            SourceSpecific,
            Global,
            AttributeCap;
        }

        /// <summary>
        /// Modifier change types;
        /// </summary>
        public enum ModifierChangeType;
        {
            Added,
            Removed,
            Updated,
            Cleared;
        }

        #endregion;

        #region Event Args Classes;

        public class LevelUpEventArgs : EventArgs;
        {
            public int PreviousLevel { get; }
            public int NewLevel { get; }
            public LevelUpResult Result { get; }

            public LevelUpEventArgs(int previousLevel, int newLevel, LevelUpResult result)
            {
                PreviousLevel = previousLevel;
                NewLevel = newLevel;
                Result = result ?? throw new ArgumentNullException(nameof(result));
            }
        }

        public class ExperienceGainedEventArgs : EventArgs;
        {
            public ExperienceGainResult Result { get; }
            public string Source { get; }

            public ExperienceGainedEventArgs(ExperienceGainResult result, string source)
            {
                Result = result ?? throw new ArgumentNullException(nameof(result));
                Source = source;
            }
        }

        public class SkillPointGainedEventArgs : EventArgs;
        {
            public int Amount { get; }
            public int Total { get; }
            public string Source { get; }

            public SkillPointGainedEventArgs(int amount, int total, string source)
            {
                Amount = amount;
                Total = total;
                Source = source;
            }
        }

        public class AttributePointGainedEventArgs : EventArgs;
        {
            public int Amount { get; }
            public int Total { get; }
            public string Source { get; }

            public AttributePointGainedEventArgs(int amount, int total, string source)
            {
                Amount = amount;
                Total = total;
                Source = source;
            }
        }

        public class AttributeIncreasedEventArgs : EventArgs;
        {
            public string Attribute { get; }
            public int Amount { get; }
            public int PreviousValue { get; }
            public int NewValue { get; }

            public AttributeIncreasedEventArgs(string attribute, int amount, int previousValue, int newValue)
            {
                Attribute = attribute ?? throw new ArgumentNullException(nameof(attribute));
                Amount = amount;
                PreviousValue = previousValue;
                NewValue = newValue;
            }
        }

        public class SkillLearnedEventArgs : EventArgs;
        {
            public Skill Skill { get; }
            public int Cost { get; }

            public SkillLearnedEventArgs(Skill skill, int cost)
            {
                Skill = skill ?? throw new ArgumentNullException(nameof(skill));
                Cost = cost;
            }
        }

        public class SkillUpgradedEventArgs : EventArgs;
        {
            public Skill Skill { get; }
            public int PreviousLevel { get; }
            public int NewLevel { get; }
            public int Cost { get; }

            public SkillUpgradedEventArgs(Skill skill, int previousLevel, int newLevel, int cost)
            {
                Skill = skill ?? throw new ArgumentNullException(nameof(skill));
                PreviousLevel = previousLevel;
                NewLevel = newLevel;
                Cost = cost;
            }
        }

        public class AchievementUnlockedEventArgs : EventArgs;
        {
            public Achievement Achievement { get; }
            public AchievementRewards Rewards { get; }

            public AchievementUnlockedEventArgs(Achievement achievement, AchievementRewards rewards)
            {
                Achievement = achievement ?? throw new ArgumentNullException(nameof(achievement));
                Rewards = rewards;
            }
        }

        public class MilestoneReachedEventArgs : EventArgs;
        {
            public Milestone Milestone { get; }

            public MilestoneReachedEventArgs(Milestone milestone)
            {
                Milestone = milestone ?? throw new ArgumentNullException(nameof(milestone));
            }
        }

        public class ModifiersChangedEventArgs : EventArgs;
        {
            public ModifierChangeType ChangeType { get; }
            public ProgressionModifier Modifier { get; }

            public ModifiersChangedEventArgs(ModifierChangeType changeType, ProgressionModifier modifier)
            {
                ChangeType = changeType;
                Modifier = modifier;
            }
        }

        public class CharacterClassChangedEventArgs : EventArgs;
        {
            public CharacterClass PreviousClass { get; }
            public CharacterClass NewClass { get; }
            public int Cost { get; }

            public CharacterClassChangedEventArgs(CharacterClass previousClass, CharacterClass newClass, int cost)
            {
                PreviousClass = previousClass;
                NewClass = newClass ?? throw new ArgumentNullException(nameof(newClass));
                Cost = cost;
            }
        }

        public class ProgressionResetEventArgs : EventArgs;
        {
            public ProgressionResetResult Result { get; }
            public bool KeepAchievements { get; }

            public ProgressionResetEventArgs(ProgressionResetResult result, bool keepAchievements)
            {
                Result = result ?? throw new ArgumentNullException(nameof(result));
                KeepAchievements = keepAchievements;
            }
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Character attributes;
    /// </summary>
    public class CharacterAttributes : ICloneable;
    {
        public Attribute Strength { get; set; } = new Attribute("Strength", 10);
        public Attribute Dexterity { get; set; } = new Attribute("Dexterity", 10);
        public Attribute Intelligence { get; set; } = new Attribute("Intelligence", 10);
        public Attribute Constitution { get; set; } = new Attribute("Constitution", 10);
        public Attribute Wisdom { get; set; } = new Attribute("Wisdom", 10);
        public Attribute Charisma { get; set; } = new Attribute("Charisma", 10);

        private readonly Dictionary<string, Attribute> _customAttributes = new Dictionary<string, Attribute>();

        public Attribute GetAttributeValue(string attributeName)
        {
            switch (attributeName.ToLower())
            {
                case "strength": return Strength;
                case "dexterity": return Dexterity;
                case "intelligence": return Intelligence;
                case "constitution": return Constitution;
                case "wisdom": return Wisdom;
                case "charisma": return Charisma;
                default:
                    _customAttributes.TryGetValue(attributeName, out var customAttribute);
                    return customAttribute;
            }
        }

        public Attribute IncreaseAttribute(string attributeName, int amount)
        {
            var attribute = GetAttributeValue(attributeName);
            if (attribute != null)
            {
                attribute.Value += amount;
                return attribute;
            }

            // Create new custom attribute;
            attribute = new Attribute(attributeName, amount);
            _customAttributes[attributeName] = attribute;
            return attribute;
        }

        public Attribute DecreaseAttribute(string attributeName, int amount)
        {
            var attribute = GetAttributeValue(attributeName);
            if (attribute != null)
            {
                attribute.Value = Math.Max(0, attribute.Value - amount);
            }
            return attribute;
        }

        public List<Attribute> GetAllAttributes()
        {
            var attributes = new List<Attribute>
            {
                Strength,
                Dexterity,
                Intelligence,
                Constitution,
                Wisdom,
                Charisma;
            };

            attributes.AddRange(_customAttributes.Values);
            return attributes;
        }

        public void ResetToBaseValues()
        {
            Strength.Value = Strength.BaseValue;
            Dexterity.Value = Dexterity.BaseValue;
            Intelligence.Value = Intelligence.BaseValue;
            Constitution.Value = Constitution.BaseValue;
            Wisdom.Value = Wisdom.BaseValue;
            Charisma.Value = Charisma.BaseValue;

            foreach (var attribute in _customAttributes.Values)
            {
                attribute.Value = attribute.BaseValue;
            }
        }

        public object Clone()
        {
            var clone = new CharacterAttributes;
            {
                Strength = this.Strength.Clone(),
                Dexterity = this.Dexterity.Clone(),
                Intelligence = this.Intelligence.Clone(),
                Constitution = this.Constitution.Clone(),
                Wisdom = this.Wisdom.Clone(),
                Charisma = this.Charisma.Clone()
            };

            foreach (var kvp in _customAttributes)
            {
                clone._customAttributes[kvp.Key] = kvp.Value.Clone();
            }

            return clone;
        }
    }

    /// <summary>
    /// Attribute;
    /// </summary>
    public class Attribute : ICloneable;
    {
        public string Name { get; set; }
        public int BaseValue { get; set; }
        public int Value { get; set; }
        public int MinValue { get; set; } = 0;
        public int MaxValue { get; set; } = 100;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public Attribute(string name, int baseValue)
        {
            Name = name;
            BaseValue = baseValue;
            Value = baseValue;
        }

        public object Clone()
        {
            return new Attribute(Name, BaseValue)
            {
                Value = this.Value,
                MinValue = this.MinValue,
                MaxValue = this.MaxValue,
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    #endregion;

    #region Custom Exceptions;

    [Serializable]
    public class ProgressionOperationException : Exception
    {
        public ProgressionOperationException() { }
        public ProgressionOperationException(string message) : base(message) { }
        public ProgressionOperationException(string message, Exception inner) : base(message, inner) { }
        protected ProgressionOperationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class ProgressionInitializationException : ProgressionOperationException;
    {
        public ProgressionInitializationException() { }
        public ProgressionInitializationException(string message) : base(message) { }
        public ProgressionInitializationException(string message, Exception inner) : base(message, inner) { }
        protected ProgressionInitializationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class ProgressionNotInitializedException : ProgressionOperationException;
    {
        public ProgressionNotInitializedException() { }
        public ProgressionNotInitializedException(string message) : base(message) { }
        public ProgressionNotInitializedException(string message, Exception inner) : base(message, inner) { }
        protected ProgressionNotInitializedException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    #endregion;
}
