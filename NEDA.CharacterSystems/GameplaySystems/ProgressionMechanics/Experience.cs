using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;

namespace NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics;
{
    /// <summary>
    /// Experience management system for character progression.
    /// Handles experience points, level calculations, and progression mechanics.
    /// </summary>
    public class Experience : IDisposable
    {
        #region Constants and Configuration;

        private const int DEFAULT_STARTING_LEVEL = 1;
        private const int DEFAULT_STARTING_EXPERIENCE = 0;
        private const double EXPONENTIAL_GROWTH_FACTOR = 1.5;
        private const int BASE_EXPERIENCE_REQUIRED = 100;

        #endregion;

        #region Private Fields;

        private readonly ILogger _logger;
        private readonly object _syncLock = new object();
        private bool _disposed = false;

        #endregion;

        #region Properties;

        /// <summary>
        /// Current level of the character;
        /// </summary>
        public int CurrentLevel { get; private set; }

        /// <summary>
        /// Current experience points;
        /// </summary>
        public long CurrentExperience { get; private set; }

        /// <summary>
        /// Total experience earned over lifetime;
        /// </summary>
        public long TotalExperienceEarned { get; private set; }

        /// <summary>
        /// Experience required for next level;
        /// </summary>
        public long ExperienceToNextLevel { get; private set; }

        /// <summary>
        /// Experience progress percentage to next level (0-100)
        /// </summary>
        public double ProgressPercentage =>
            ExperienceToNextLevel > 0 ? (double)(CurrentExperience - ExperienceForLevel(CurrentLevel)) / ExperienceToNextLevel * 100 : 0;

        /// <summary>
        /// Maximum level cap (0 for unlimited)
        /// </summary>
        public int MaxLevel { get; private set; }

        /// <summary>
        /// Experience multiplier for bonuses;
        /// </summary>
        public double ExperienceMultiplier { get; private set; } = 1.0;

        /// <summary>
        /// List of level up achievements;
        /// </summary>
        public List<LevelAchievement> Achievements { get; private set; }

        /// <summary>
        /// Configuration for experience calculations;
        /// </summary>
        public ExperienceConfiguration Configuration { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Raised when experience points are gained;
        /// </summary>
        public event EventHandler<ExperienceGainedEventArgs> ExperienceGained;

        /// <summary>
        /// Raised when a level up occurs;
        /// </summary>
        public event EventHandler<LevelUpEventArgs> LevelUp;

        /// <summary>
        /// Raised when maximum level is reached;
        /// </summary>
        public event EventHandler<MaxLevelReachedEventArgs> MaxLevelReached;

        /// <summary>
        /// Raised when experience multiplier changes;
        /// </summary>
        public event EventHandler<MultiplierChangedEventArgs> MultiplierChanged;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of Experience system with default configuration;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        public Experience(ILogger logger = null)
        {
            _logger = logger ?? LoggerFactory.CreateLogger(nameof(Experience));
            Initialize(DEFAULT_STARTING_LEVEL, DEFAULT_STARTING_EXPERIENCE, 0, new ExperienceConfiguration());
        }

        /// <summary>
        /// Initializes a new instance of Experience system with specific configuration;
        /// </summary>
        /// <param name="startingLevel">Starting level</param>
        /// <param name="startingExperience">Starting experience</param>
        /// <param name="maxLevel">Maximum level (0 for unlimited)</param>
        /// <param name="configuration">Experience configuration</param>
        /// <param name="logger">Logger instance</param>
        public Experience(int startingLevel, long startingExperience, int maxLevel,
                         ExperienceConfiguration configuration, ILogger logger = null)
        {
            _logger = logger ?? LoggerFactory.CreateLogger(nameof(Experience));
            Initialize(startingLevel, startingExperience, maxLevel, configuration);
        }

        /// <summary>
        /// Static factory method for creating experience system from save data;
        /// </summary>
        /// <param name="saveData">Saved experience data</param>
        /// <param name="logger">Logger instance</param>
        /// <returns>Configured Experience instance</returns>
        public static Experience CreateFromSave(ExperienceSaveData saveData, ILogger logger = null)
        {
            ArgumentValidator.ThrowIfNull(saveData, nameof(saveData));

            var experience = new Experience(
                saveData.CurrentLevel,
                saveData.CurrentExperience,
                saveData.MaxLevel,
                saveData.Configuration,
                logger);

            experience.TotalExperienceEarned = saveData.TotalExperienceEarned;
            experience.ExperienceMultiplier = saveData.ExperienceMultiplier;

            return experience;
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Adds experience points to the character;
        /// </summary>
        /// <param name="amount">Amount of experience to add</param>
        /// <param name="source">Source of the experience</param>
        /// <param name="tags">Additional metadata tags</param>
        /// <returns>Experience gain result</returns>
        public ExperienceGainResult AddExperience(long amount, string source = null, Dictionary<string, object> tags = null)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNegative(amount, nameof(amount));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Adding {amount} experience from source: {source ?? "Unknown"}");

                    // Apply multiplier;
                    var modifiedAmount = (long)(amount * ExperienceMultiplier);
                    var originalAmount = amount;

                    // Check if at max level;
                    if (MaxLevel > 0 && CurrentLevel >= MaxLevel)
                    {
                        _logger.LogWarning($"Max level ({MaxLevel}) already reached. Experience not added.");
                        return new ExperienceGainResult;
                        {
                            ExperienceAdded = 0,
                            OriginalAmount = originalAmount,
                            MultipliedAmount = modifiedAmount,
                            LeveledUp = false,
                            NewLevel = CurrentLevel,
                            Reason = "Max level reached"
                        };
                    }

                    // Store previous values;
                    var previousLevel = CurrentLevel;
                    var previousExperience = CurrentExperience;

                    // Add experience;
                    CurrentExperience += modifiedAmount;
                    TotalExperienceEarned += modifiedAmount;

                    // Check for level up;
                    var leveledUp = false;
                    while (ShouldLevelUp())
                    {
                        PerformLevelUp();
                        leveledUp = true;

                        // Break if max level reached;
                        if (MaxLevel > 0 && CurrentLevel >= MaxLevel)
                        {
                            _logger.LogInformation($"Max level {MaxLevel} reached");
                            OnMaxLevelReached();
                            break;
                        }
                    }

                    // Update next level requirement;
                    UpdateExperienceToNextLevel();

                    // Create result;
                    var result = new ExperienceGainResult;
                    {
                        ExperienceAdded = modifiedAmount,
                        OriginalAmount = originalAmount,
                        MultipliedAmount = modifiedAmount,
                        LeveledUp = leveledUp,
                        PreviousLevel = previousLevel,
                        NewLevel = CurrentLevel,
                        PreviousExperience = previousExperience,
                        NewExperience = CurrentExperience,
                        Source = source,
                        Tags = tags;
                    };

                    // Raise events;
                    OnExperienceGained(result);
                    if (leveledUp)
                    {
                        OnLevelUp(previousLevel, CurrentLevel);
                    }

                    _logger.LogDebug($"Experience added successfully. Total: {CurrentExperience}, Level: {CurrentLevel}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error adding experience: {amount}");
                    throw new ExperienceOperationException("Failed to add experience", ex);
                }
            }
        }

        /// <summary>
        /// Sets the experience multiplier;
        /// </summary>
        /// <param name="multiplier">New multiplier value</param>
        /// <param name="duration">Duration in seconds (0 for permanent)</param>
        /// <param name="source">Source of the multiplier</param>
        public void SetMultiplier(double multiplier, double duration = 0, string source = null)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNegative(multiplier, nameof(multiplier));

            lock (_syncLock)
            {
                try
                {
                    var previousMultiplier = ExperienceMultiplier;
                    ExperienceMultiplier = multiplier;

                    _logger.LogInformation($"Experience multiplier changed from {previousMultiplier} to {multiplier} (Source: {source})");

                    // If duration is specified, schedule reset;
                    if (duration > 0)
                    {
                        ScheduleMultiplierReset(duration, previousMultiplier, source);
                    }

                    OnMultiplierChanged(previousMultiplier, multiplier, source, duration);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error setting multiplier: {multiplier}");
                    throw new ExperienceOperationException("Failed to set experience multiplier", ex);
                }
            }
        }

        /// <summary>
        /// Resets experience multiplier to default (1.0)
        /// </summary>
        public void ResetMultiplier()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                var previousMultiplier = ExperienceMultiplier;
                ExperienceMultiplier = 1.0;

                _logger.LogInformation($"Experience multiplier reset to 1.0 from {previousMultiplier}");
                OnMultiplierChanged(previousMultiplier, 1.0, "System", 0);
            }
        }

        /// <summary>
        /// Calculates experience required for a specific level;
        /// </summary>
        /// <param name="level">Target level</param>
        /// <returns>Experience required</returns>
        public long CalculateExperienceForLevel(int level)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNegativeOrZero(level, nameof(level));

            return ExperienceForLevel(level);
        }

        /// <summary>
        /// Calculates total experience from level 1 to target level;
        /// </summary>
        /// <param name="level">Target level</param>
        /// <returns>Total cumulative experience</returns>
        public long CalculateCumulativeExperience(int level)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNegativeOrZero(level, nameof(level));

            long total = 0;
            for (int i = 1; i <= level; i++)
            {
                total += ExperienceForLevel(i);
            }

            return total;
        }

        /// <summary>
        /// Gets remaining experience needed for next level;
        /// </summary>
        /// <returns>Remaining experience</returns>
        public long GetRemainingExperience()
        {
            ValidateNotDisposed();

            if (MaxLevel > 0 && CurrentLevel >= MaxLevel)
                return 0;

            var experienceForCurrentLevel = ExperienceForLevel(CurrentLevel);
            return ExperienceToNextLevel - (CurrentExperience - experienceForCurrentLevel);
        }

        /// <summary>
        /// Gets the current level progress information;
        /// </summary>
        /// <returns>Level progress details</returns>
        public LevelProgress GetLevelProgress()
        {
            ValidateNotDisposed();

            var experienceForCurrentLevel = ExperienceForLevel(CurrentLevel);
            var experienceForNextLevel = ExperienceForLevel(CurrentLevel + 1);
            var currentLevelExperience = CurrentExperience - experienceForCurrentLevel;
            var nextLevelExperienceRequired = experienceForNextLevel - experienceForCurrentLevel;

            return new LevelProgress;
            {
                CurrentLevel = CurrentLevel,
                CurrentExperience = currentLevelExperience,
                ExperienceToNextLevel = nextLevelExperienceRequired,
                ProgressPercentage = ProgressPercentage,
                IsMaxLevel = MaxLevel > 0 && CurrentLevel >= MaxLevel,
                CanLevelUp = ShouldLevelUp()
            };
        }

        /// <summary>
        /// Saves the current experience state;
        /// </summary>
        /// <returns>Save data object</returns>
        public ExperienceSaveData Save()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return new ExperienceSaveData;
                {
                    CurrentLevel = CurrentLevel,
                    CurrentExperience = CurrentExperience,
                    TotalExperienceEarned = TotalExperienceEarned,
                    MaxLevel = MaxLevel,
                    ExperienceMultiplier = ExperienceMultiplier,
                    Configuration = Configuration.Clone(),
                    LastUpdated = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Awards an achievement for reaching a level milestone;
        /// </summary>
        /// <param name="achievement">Achievement to award</param>
        public void AwardAchievement(LevelAchievement achievement)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNull(achievement, nameof(achievement));

            lock (_syncLock)
            {
                if (Achievements.Any(a => a.Level == achievement.Level && a.Type == achievement.Type))
                {
                    _logger.LogWarning($"Achievement already awarded for level {achievement.Level}, type: {achievement.Type}");
                    return;
                }

                Achievements.Add(achievement);
                Achievements = Achievements.OrderBy(a => a.Level).ToList();

                _logger.LogInformation($"Achievement awarded: {achievement.Name} at level {achievement.Level}");
            }
        }

        /// <summary>
        /// Gets achievements for a specific level;
        /// </summary>
        /// <param name="level">Level to check</param>
        /// <returns>List of achievements</returns>
        public List<LevelAchievement> GetAchievementsForLevel(int level)
        {
            ValidateNotDisposed();

            return Achievements;
                .Where(a => a.Level == level)
                .OrderBy(a => a.Type)
                .ToList();
        }

        /// <summary>
        /// Checks if level has any achievements;
        /// </summary>
        /// <param name="level">Level to check</param>
        /// <returns>True if achievements exist</returns>
        public bool HasAchievementsForLevel(int level)
        {
            ValidateNotDisposed();

            return Achievements.Any(a => a.Level == level);
        }

        #endregion;

        #region Private Methods;

        private void Initialize(int startingLevel, long startingExperience, int maxLevel,
                               ExperienceConfiguration configuration)
        {
            try
            {
                ArgumentValidator.ThrowIfNegativeOrZero(startingLevel, nameof(startingLevel));
                ArgumentValidator.ThrowIfNegative(startingExperience, nameof(startingExperience));
                ArgumentValidator.ThrowIfNegative(maxLevel, nameof(maxLevel));
                ArgumentValidator.ThrowIfNull(configuration, nameof(configuration));

                Configuration = configuration.Clone();
                CurrentLevel = startingLevel;
                CurrentExperience = startingExperience;
                MaxLevel = maxLevel;
                ExperienceMultiplier = 1.0;
                TotalExperienceEarned = startingExperience;
                Achievements = new List<LevelAchievement>();

                // Validate experience matches level;
                var minExpForLevel = ExperienceForLevel(startingLevel);
                if (startingExperience < minExpForLevel)
                {
                    _logger.LogWarning($"Starting experience ({startingExperience}) is less than minimum for level {startingLevel} ({minExpForLevel}). Adjusting...");
                    CurrentExperience = minExpForLevel;
                }

                UpdateExperienceToNextLevel();

                _logger.LogInformation($"Experience system initialized. Level: {CurrentLevel}, Exp: {CurrentExperience}, MaxLevel: {MaxLevel}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Experience system");
                throw new ExperienceInitializationException("Failed to initialize Experience system", ex);
            }
        }

        private long ExperienceForLevel(int level)
        {
            if (Configuration.UseCustomFormula && Configuration.CustomFormula != null)
            {
                return Configuration.CustomFormula(level);
            }

            // Default exponential formula: Base * (Level^GrowthFactor)
            return (long)(BASE_EXPERIENCE_REQUIRED * Math.Pow(level, EXPONENTIAL_GROWTH_FACTOR));
        }

        private bool ShouldLevelUp()
        {
            if (MaxLevel > 0 && CurrentLevel >= MaxLevel)
                return false;

            var nextLevelExperience = ExperienceForLevel(CurrentLevel + 1);
            return CurrentExperience >= nextLevelExperience;
        }

        private void PerformLevelUp()
        {
            var previousLevel = CurrentLevel;
            CurrentLevel++;

            // Check for overflow;
            if (CurrentLevel < previousLevel)
            {
                throw new ExperienceOverflowException($"Level overflow occurred from {previousLevel}");
            }

            _logger.LogInformation($"Level up! {previousLevel} -> {CurrentLevel}");

            // Apply level up bonuses from configuration;
            ApplyLevelUpBonuses(previousLevel, CurrentLevel);
        }

        private void ApplyLevelUpBonuses(int previousLevel, int newLevel)
        {
            // Apply any configured level up bonuses;
            if (Configuration.LevelUpBonuses != null)
            {
                foreach (var bonus in Configuration.LevelUpBonuses)
                {
                    if (bonus.ShouldApply(previousLevel, newLevel))
                    {
                        bonus.Apply(this);
                        _logger.LogDebug($"Applied level up bonus: {bonus.Name}");
                    }
                }
            }
        }

        private void UpdateExperienceToNextLevel()
        {
            if (MaxLevel > 0 && CurrentLevel >= MaxLevel)
            {
                ExperienceToNextLevel = 0;
            }
            else;
            {
                var nextLevelExp = ExperienceForLevel(CurrentLevel + 1);
                var currentLevelExp = ExperienceForLevel(CurrentLevel);
                ExperienceToNextLevel = nextLevelExp - currentLevelExp;
            }
        }

        private async void ScheduleMultiplierReset(double duration, double previousMultiplier, string source)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(duration));

                lock (_syncLock)
                {
                    if (Math.Abs(ExperienceMultiplier - previousMultiplier) < 0.001)
                    {
                        ExperienceMultiplier = 1.0;
                        _logger.LogInformation($"Temporary multiplier from {source} expired. Reset to 1.0");
                        OnMultiplierChanged(previousMultiplier, 1.0, "Expired: " + source, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in multiplier reset scheduler for source: {source}");
            }
        }

        private void OnExperienceGained(ExperienceGainResult result)
        {
            ExperienceGained?.Invoke(this, new ExperienceGainedEventArgs(result));
        }

        private void OnLevelUp(int previousLevel, int newLevel)
        {
            LevelUp?.Invoke(this, new LevelUpEventArgs(previousLevel, newLevel, CurrentExperience));
        }

        private void OnMaxLevelReached()
        {
            MaxLevelReached?.Invoke(this, new MaxLevelReachedEventArgs(CurrentLevel, CurrentExperience));
        }

        private void OnMultiplierChanged(double previous, double current, string source, double duration)
        {
            MultiplierChanged?.Invoke(this, new MultiplierChangedEventArgs(previous, current, source, duration));
        }

        private void ValidateNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Experience));
            }
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
                    // Clean up managed resources;
                    Achievements?.Clear();
                    Achievements = null;

                    // Unsubscribe from events;
                    ExperienceGained = null;
                    LevelUp = null;
                    MaxLevelReached = null;
                    MultiplierChanged = null;
                }

                _disposed = true;
                _logger.LogInformation("Experience system disposed");
            }
        }

        ~Experience()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes and Enums;

        /// <summary>
        /// Experience gain result;
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
        /// Level progress information;
        /// </summary>
        public class LevelProgress;
        {
            public int CurrentLevel { get; set; }
            public long CurrentExperience { get; set; }
            public long ExperienceToNextLevel { get; set; }
            public double ProgressPercentage { get; set; }
            public bool IsMaxLevel { get; set; }
            public bool CanLevelUp { get; set; }
        }

        /// <summary>
        /// Level achievement;
        /// </summary>
        public class LevelAchievement;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public int Level { get; set; }
            public AchievementType Type { get; set; }
            public DateTime AwardedDate { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
        }

        /// <summary>
        /// Experience save data;
        /// </summary>
        public class ExperienceSaveData;
        {
            public int CurrentLevel { get; set; }
            public long CurrentExperience { get; set; }
            public long TotalExperienceEarned { get; set; }
            public int MaxLevel { get; set; }
            public double ExperienceMultiplier { get; set; }
            public ExperienceConfiguration Configuration { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        /// <summary>
        /// Experience configuration;
        /// </summary>
        public class ExperienceConfiguration : ICloneable;
        {
            public bool UseCustomFormula { get; set; } = false;
            public Func<int, long> CustomFormula { get; set; }
            public double ExponentialFactor { get; set; } = EXPONENTIAL_GROWTH_FACTOR;
            public int BaseExperience { get; set; } = BASE_EXPERIENCE_REQUIRED;
            public List<LevelUpBonus> LevelUpBonuses { get; set; } = new List<LevelUpBonus>();
            public bool AllowNegativeExperience { get; set; } = false;
            public int MaxLevelCap { get; set; } = 0;

            public object Clone()
            {
                return new ExperienceConfiguration;
                {
                    UseCustomFormula = this.UseCustomFormula,
                    CustomFormula = this.CustomFormula,
                    ExponentialFactor = this.ExponentialFactor,
                    BaseExperience = this.BaseExperience,
                    LevelUpBonuses = this.LevelUpBonuses?.Select(b => b.Clone()).ToList(),
                    AllowNegativeExperience = this.AllowNegativeExperience,
                    MaxLevelCap = this.MaxLevelCap;
                };
            }
        }

        /// <summary>
        /// Level up bonus;
        /// </summary>
        public abstract class LevelUpBonus;
        {
            public string Name { get; set; }
            public string Description { get; set; }

            public abstract bool ShouldApply(int previousLevel, int newLevel);
            public abstract void Apply(Experience experience);
            public abstract LevelUpBonus Clone();
        }

        /// <summary>
        /// Achievement types;
        /// </summary>
        public enum AchievementType;
        {
            Milestone,
            SpeedRun,
            Completionist,
            Hidden,
            Special,
            Seasonal;
        }

        #endregion;

        #region Event Args Classes;

        public class ExperienceGainedEventArgs : EventArgs;
        {
            public ExperienceGainResult Result { get; }

            public ExperienceGainedEventArgs(ExperienceGainResult result)
            {
                Result = result ?? throw new ArgumentNullException(nameof(result));
            }
        }

        public class LevelUpEventArgs : EventArgs;
        {
            public int PreviousLevel { get; }
            public int NewLevel { get; }
            public long CurrentExperience { get; }

            public LevelUpEventArgs(int previousLevel, int newLevel, long currentExperience)
            {
                PreviousLevel = previousLevel;
                NewLevel = newLevel;
                CurrentExperience = currentExperience;
            }
        }

        public class MaxLevelReachedEventArgs : EventArgs;
        {
            public int MaxLevel { get; }
            public long TotalExperience { get; }

            public MaxLevelReachedEventArgs(int maxLevel, long totalExperience)
            {
                MaxLevel = maxLevel;
                TotalExperience = totalExperience;
            }
        }

        public class MultiplierChangedEventArgs : EventArgs;
        {
            public double PreviousMultiplier { get; }
            public double NewMultiplier { get; }
            public string Source { get; }
            public double Duration { get; }

            public MultiplierChangedEventArgs(double previous, double current, string source, double duration)
            {
                PreviousMultiplier = previous;
                NewMultiplier = current;
                Source = source;
                Duration = duration;
            }
        }

        #endregion;
    }

    #region Custom Exceptions;

    [Serializable]
    public class ExperienceOperationException : Exception
    {
        public ExperienceOperationException() { }
        public ExperienceOperationException(string message) : base(message) { }
        public ExperienceOperationException(string message, Exception inner) : base(message, inner) { }
        protected ExperienceOperationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class ExperienceInitializationException : Exception
    {
        public ExperienceInitializationException() { }
        public ExperienceInitializationException(string message) : base(message) { }
        public ExperienceInitializationException(string message, Exception inner) : base(message, inner) { }
        protected ExperienceInitializationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class ExperienceOverflowException : Exception
    {
        public ExperienceOverflowException() { }
        public ExperienceOverflowException(string message) : base(message) { }
        public ExperienceOverflowException(string message, Exception inner) : base(message, inner) { }
        protected ExperienceOverflowException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    #endregion;
}
