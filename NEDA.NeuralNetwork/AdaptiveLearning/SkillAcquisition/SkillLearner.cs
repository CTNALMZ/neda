using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common;
using NEDA.Core.Common.Extensions;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.NeuralNetwork.AdaptiveLearning;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.NeuralNetwork.PatternRecognition;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.AdaptiveLearning.SelfImprovement;
{
    /// <summary>
    /// Configuration options for SkillLearner;
    /// </summary>
    public class SkillLearnerOptions;
    {
        public const string SectionName = "SkillLearner";

        /// <summary>
        /// Gets or sets the learning rate for skill acquisition;
        /// </summary>
        public double LearningRate { get; set; } = 0.01;

        /// <summary>
        /// Gets or sets the skill decay rate (forgetting curve)
        /// </summary>
        public double DecayRate { get; set; } = 0.05;

        /// <summary>
        /// Gets or sets the skill retention rate;
        /// </summary>
        public double RetentionRate { get; set; } = 0.85;

        /// <summary>
        /// Gets or sets the minimum proficiency threshold;
        /// </summary>
        public double ProficiencyThreshold { get; set; } = 0.7;

        /// <summary>
        /// Gets or sets the mastery threshold;
        /// </summary>
        public double MasteryThreshold { get; set; } = 0.95;

        /// <summary>
        /// Gets or sets the maximum skills to maintain;
        /// </summary>
        public int MaxSkills { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the skill categories to focus on;
        /// </summary>
        public List<string> SkillCategories { get; set; } = new()
        {
            "Cognitive",
            "Motor",
            "Social",
            "Technical",
            "Creative",
            "Analytical"
        };

        /// <summary>
        /// Gets or sets the learning strategies to use;
        /// </summary>
        public List<SkillLearningStrategy> Strategies { get; set; } = new()
        {
            SkillLearningStrategy.DeliberatePractice,
            SkillLearningStrategy.SpacedRepetition,
            SkillLearningStrategy.InterleavedPractice,
            SkillLearningStrategy.FeedbackDriven;
        };

        /// <summary>
        /// Gets or sets the practice session duration in minutes;
        /// </summary>
        public int PracticeSessionDuration { get; set; } = 30;

        /// <summary>
        /// Gets or sets the maximum practice sessions per day;
        /// </summary>
        public int MaxSessionsPerDay { get; set; } = 10;

        /// <summary>
        /// Gets or sets whether to enable skill transfer;
        /// </summary>
        public bool EnableSkillTransfer { get; set; } = true;

        /// <summary>
        /// Gets or sets the skill difficulty levels;
        /// </summary>
        public Dictionary<string, double> DifficultyLevels { get; set; } = new()
        {
            ["Beginner"] = 0.3,
            ["Intermediate"] = 0.6,
            ["Advanced"] = 0.8,
            ["Expert"] = 1.0;
        };
    }

    /// <summary>
    /// Skill learning strategies;
    /// </summary>
    public enum SkillLearningStrategy;
    {
        DeliberatePractice,
        SpacedRepetition,
        InterleavedPractice,
        FeedbackDriven,
        Imitation,
        Guided,
        SelfDirected,
        Collaborative;
    }

    /// <summary>
    /// Skill proficiency levels;
    /// </summary>
    public enum SkillProficiency;
    {
        Novice = 0,
        Beginner = 1,
        Competent = 2,
        Proficient = 3,
        Expert = 4,
        Master = 5;
    }

    /// <summary>
    /// Represents a skill that can be learned;
    /// </summary>
    public class Skill;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public double Proficiency { get; set; }
        public double Confidence { get; set; }
        public SkillProficiency Level { get; set; }
        public DateTime FirstLearned { get; set; }
        public DateTime LastPracticed { get; set; }
        public int PracticeCount { get; set; }
        public double Difficulty { get; set; }
        public double Importance { get; set; }
        public Dictionary<string, double> Prerequisites { get; set; } = new();
        public List<string> Dependencies { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<SkillLearningStrategy> EffectiveStrategies { get; set; } = new();

        public override bool Equals(object obj)
        {
            return obj is Skill skill && Id == skill.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }

    /// <summary>
    /// Represents a practice session;
    /// </summary>
    public class PracticeSession;
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public string SkillId { get; set; }
        public SkillLearningStrategy Strategy { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double InitialProficiency { get; set; }
        public double FinalProficiency { get; set; }
        public double ProficiencyGain { get; set; }
        public int PracticeAttempts { get; set; }
        public int SuccessfulAttempts { get; set; }
        public double SuccessRate { get; set; }
        public double DifficultyAdjusted { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Insights { get; set; } = new();
        public Dictionary<string, double> Metrics { get; set; } = new();
        public string SessionReport { get; set; }
    }

    /// <summary>
    /// Represents skill learning progress;
    /// </summary>
    public class SkillProgress;
    {
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public double CurrentProficiency { get; set; }
        public double TargetProficiency { get; set; }
        public double ProgressPercentage { get; set; }
        public int SessionsCompleted { get; set; }
        public int TotalPracticeTime { get; set; } // in minutes;
        public DateTime NextPracticeDue { get; set; }
        public double EstimatedMasteryTime { get; set; } // in hours;
        public List<double> ProficiencyHistory { get; set; } = new();
        public Dictionary<string, double> StrategyEffectiveness { get; set; } = new();
    }

    /// <summary>
    /// Interface for SkillLearner;
    /// </summary>
    public interface ISkillLearner : IDisposable
    {
        /// <summary>
        /// Gets the learner's unique identifier;
        /// </summary>
        string LearnerId { get; }

        /// <summary>
        /// Initializes the skill learner;
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Learns a new skill;
        /// </summary>
        Task<Skill> LearnSkillAsync(string skillName, string category, SkillLearningStrategy strategy, CancellationToken cancellationToken = default);

        /// <summary>
        /// Practices an existing skill;
        /// </summary>
        Task<PracticeSession> PracticeSkillAsync(string skillId, SkillLearningStrategy strategy, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a skill by ID;
        /// </summary>
        Task<Skill> GetSkillAsync(string skillId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets all skills;
        /// </summary>
        Task<List<Skill>> GetAllSkillsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets skills by category;
        /// </summary>
        Task<List<Skill>> GetSkillsByCategoryAsync(string category, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets skills by proficiency level;
        /// </summary>
        Task<List<Skill>> GetSkillsByProficiencyAsync(SkillProficiency minLevel, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates skill proficiency;
        /// </summary>
        Task UpdateSkillProficiencyAsync(string skillId, double newProficiency, CancellationToken cancellationToken = default);

        /// <summary>
        /// Forgets a skill (intentional decay)
        /// </summary>
        Task ForgetSkillAsync(string skillId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets skill progress;
        /// </summary>
        Task<SkillProgress> GetSkillProgressAsync(string skillId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets overall learning progress;
        /// </summary>
        Task<OverallProgress> GetOverallProgressAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets recommended skills to learn;
        /// </summary>
        Task<List<SkillRecommendation>> GetSkillRecommendationsAsync(int limit = 10, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets practice schedule;
        /// </summary>
        Task<PracticeSchedule> GetPracticeScheduleAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves the skill learner state;
        /// </summary>
        Task SaveAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads the skill learner state;
        /// </summary>
        Task LoadAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Resets the skill learner;
        /// </summary>
        Task ResetAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Evaluates skill transfer potential;
        /// </summary>
        Task<SkillTransferEvaluation> EvaluateSkillTransferAsync(string sourceSkillId, string targetSkillId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Overall learning progress;
    /// </summary>
    public class OverallProgress;
    {
        public int TotalSkills { get; set; }
        public int MasteredSkills { get; set; }
        public int LearningSkills { get; set; }
        public double AverageProficiency { get; set; }
        public double OverallConfidence { get; set; }
        public Dictionary<string, int> SkillsByCategory { get; set; } = new();
        public Dictionary<string, double> CategoryProficiency { get; set; } = new();
        public List<string> TopSkills { get; set; } = new();
        public List<string> SkillsNeedingPractice { get; set; } = new();
        public TimeSpan TotalPracticeTime { get; set; }
        public int TotalSessions { get; set; }
    }

    /// <summary>
    /// Skill recommendation;
    /// </summary>
    public class SkillRecommendation;
    {
        public Skill Skill { get; set; }
        public double RecommendationScore { get; set; }
        public string Reason { get; set; }
        public double ExpectedDifficulty { get; set; }
        public double ExpectedLearningTime { get; set; } // in hours;
        public List<string> Prerequisites { get; set; } = new();
    }

    /// <summary>
    /// Practice schedule;
    /// </summary>
    public class PracticeSchedule;
    {
        public DateTime ScheduleDate { get; set; }
        public List<PracticeSlot> Slots { get; set; } = new();
        public double EstimatedImprovement { get; set; }
        public TimeSpan TotalPracticeTime { get; set; }
    }

    /// <summary>
    /// Practice time slot;
    /// </summary>
    public class PracticeSlot;
    {
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public SkillLearningStrategy Strategy { get; set; }
        public double ExpectedGain { get; set; }
        public string FocusArea { get; set; }
    }

    /// <summary>
    /// Skill transfer evaluation;
    /// </summary>
    public class SkillTransferEvaluation;
    {
        public string SourceSkillId { get; set; }
        public string TargetSkillId { get; set; }
        public double TransferPotential { get; set; }
        public List<string> TransferableComponents { get; set; } = new();
        public double ExpectedAcceleration { get; set; }
        public List<string> Recommendations { get; set; } = new();
        public Dictionary<string, double> ComponentMatches { get; set; } = new();
    }

    /// <summary>
    /// Skill repository for managing skills;
    /// </summary>
    public class SkillRepository;
    {
        private readonly ConcurrentDictionary<string, Skill> _skills;
        private readonly ConcurrentDictionary<string, List<PracticeSession>> _practiceHistory;
        private readonly ConcurrentDictionary<string, List<double>> _proficiencyHistory;
        private readonly int _maxSkills;

        public SkillRepository(int maxSkills = 1000)
        {
            _maxSkills = maxSkills;
            _skills = new ConcurrentDictionary<string, Skill>();
            _practiceHistory = new ConcurrentDictionary<string, List<PracticeSession>>();
            _proficiencyHistory = new ConcurrentDictionary<string, List<double>>();
        }

        public void AddSkill(Skill skill)
        {
            if (_skills.Count >= _maxSkills)
            {
                RemoveLeastImportantSkill();
            }

            _skills[skill.Id] = skill;
            _proficiencyHistory[skill.Id] = new List<double> { skill.Proficiency };
        }

        public Skill GetSkill(string id)
        {
            return _skills.TryGetValue(id, out var skill) ? skill : null;
        }

        public IEnumerable<Skill> GetAllSkills()
        {
            return _skills.Values;
        }

        public IEnumerable<Skill> GetSkillsByCategory(string category)
        {
            return _skills.Values.Where(s => s.Category == category);
        }

        public IEnumerable<Skill> GetSkillsByProficiency(SkillProficiency minLevel)
        {
            return _skills.Values.Where(s => s.Level >= minLevel);
        }

        public bool RemoveSkill(string id)
        {
            return _skills.TryRemove(id, out _);
        }

        public void UpdateSkillProficiency(string id, double proficiency)
        {
            if (_skills.TryGetValue(id, out var skill))
            {
                skill.Proficiency = proficiency;
                skill.Level = CalculateProficiencyLevel(proficiency);
                skill.Confidence = CalculateConfidence(skill);

                // Update history;
                if (_proficiencyHistory.TryGetValue(id, out var history))
                {
                    history.Add(proficiency);
                    if (history.Count > 100)
                    {
                        history.RemoveAt(0);
                    }
                }
            }
        }

        public void AddPracticeSession(PracticeSession session)
        {
            _practiceHistory.AddOrUpdate(
                session.SkillId,
                new List<PracticeSession> { session },
                (_, list) =>
                {
                    list.Add(session);
                    if (list.Count > 100)
                    {
                        list.RemoveAt(0);
                    }
                    return list;
                });
        }

        public List<PracticeSession> GetPracticeHistory(string skillId, int limit = 50)
        {
            return _practiceHistory.TryGetValue(skillId, out var sessions)
                ? sessions.TakeLast(limit).ToList()
                : new List<PracticeSession>();
        }

        public List<double> GetProficiencyHistory(string skillId, int limit = 50)
        {
            return _proficiencyHistory.TryGetValue(skillId, out var history)
                ? history.TakeLast(limit).ToList()
                : new List<double>();
        }

        public int SkillCount => _skills.Count;

        private void RemoveLeastImportantSkill()
        {
            var leastImportant = _skills.Values;
                .OrderBy(s => s.Importance * s.Proficiency)
                .ThenBy(s => s.LastPracticed)
                .FirstOrDefault();

            if (leastImportant != null)
            {
                RemoveSkill(leastImportant.Id);
            }
        }

        private SkillProficiency CalculateProficiencyLevel(double proficiency)
        {
            return proficiency switch;
            {
                < 0.2 => SkillProficiency.Novice,
                < 0.4 => SkillProficiency.Beginner,
                < 0.6 => SkillProficiency.Competent,
                < 0.8 => SkillProficiency.Proficient,
                < 0.95 => SkillProficiency.Expert,
                _ => SkillProficiency.Master;
            };
        }

        private double CalculateConfidence(Skill skill)
        {
            var practiceFactor = Math.Min(1.0, Math.Log(1 + skill.PracticeCount) / 10.0);
            var recencyFactor = 1.0 / (1.0 + (DateTime.UtcNow - skill.LastPracticed).TotalDays / 30);
            var consistencyFactor = CalculateConsistency(skill.Id);

            return (practiceFactor * 0.4) + (recencyFactor * 0.3) + (consistencyFactor * 0.3);
        }

        private double CalculateConsistency(string skillId)
        {
            if (_practiceHistory.TryGetValue(skillId, out var sessions) && sessions.Count >= 3)
            {
                var successRates = sessions.Select(s => s.SuccessRate).ToList();
                var average = successRates.Average();
                var variance = successRates.Select(r => Math.Pow(r - average, 2)).Average();
                return 1.0 / (1.0 + variance);
            }

            return 0.5;
        }
    }

    /// <summary>
    /// Skill learning system that acquires and improves skills through practice;
    /// </summary>
    public class SkillLearner : ISkillLearner;
    {
        private readonly ILogger<SkillLearner> _logger;
        private readonly SkillLearnerOptions _options;
        private readonly ICompetencyBuilder _competencyBuilder;
        private readonly IAbilityTrainer _abilityTrainer;
        private readonly IAdaptiveEngine _adaptiveEngine;
        private readonly SkillRepository _skillRepository;
        private readonly ConcurrentQueue<PracticeSession> _recentSessions;
        private readonly SemaphoreSlim _learningLock;
        private readonly Random _random;

        private volatile bool _disposed;
        private string _learnerId;
        private int _sessionsToday;
        private DateTime _lastSessionReset;

        /// <summary>
        /// Gets the learner's unique identifier;
        /// </summary>
        public string LearnerId => _learnerId;

        /// <summary>
        /// Initializes a new instance of the SkillLearner class;
        /// </summary>
        public SkillLearner(
            ILogger<SkillLearner> logger,
            IOptions<SkillLearnerOptions> options,
            ICompetencyBuilder competencyBuilder,
            IAbilityTrainer abilityTrainer,
            IAdaptiveEngine adaptiveEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _competencyBuilder = competencyBuilder ?? throw new ArgumentNullException(nameof(competencyBuilder));
            _abilityTrainer = abilityTrainer ?? throw new ArgumentNullException(nameof(abilityTrainer));
            _adaptiveEngine = adaptiveEngine ?? throw new ArgumentNullException(nameof(adaptiveEngine));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _learnerId = $"SkillLearner_{Guid.NewGuid():N}";
            _skillRepository = new SkillRepository(_options.MaxSkills);
            _recentSessions = new ConcurrentQueue<PracticeSession>();
            _learningLock = new SemaphoreSlim(1, 1);
            _random = new Random();
            _lastSessionReset = DateTime.UtcNow.Date;

            _logger.LogInformation("SkillLearner {LearnerId} initialized with {MaxSkills} max skills",
                _learnerId, _options.MaxSkills);
        }

        /// <inheritdoc/>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                // Initialize foundational skills;
                await InitializeFoundationalSkillsAsync(cancellationToken);

                // Initialize learning strategies;
                await InitializeLearningStrategiesAsync(cancellationToken);

                _logger.LogInformation("SkillLearner {LearnerId} initialization completed", _learnerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SkillLearner");
                throw new NEDAException($"Initialization failed: {ex.Message}", ErrorCodes.InitializationFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<Skill> LearnSkillAsync(string skillName, string category, SkillLearningStrategy strategy, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                // Check if skill already exists;
                var existingSkill = _skillRepository.GetAllSkills()
                    .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

                if (existingSkill != null)
                {
                    _logger.LogWarning("Skill {SkillName} already exists with proficiency {Proficiency}",
                        skillName, existingSkill.Proficiency);
                    return existingSkill;
                }

                // Check prerequisites;
                var prerequisites = await CheckPrerequisitesAsync(skillName, category, cancellationToken);

                // Create new skill;
                var skill = new Skill;
                {
                    Name = skillName,
                    Description = $"Skill: {skillName} in category {category}",
                    Category = category,
                    Proficiency = 0.1, // Initial proficiency;
                    Confidence = 0.5,
                    Level = SkillProficiency.Novice,
                    FirstLearned = DateTime.UtcNow,
                    LastPracticed = DateTime.UtcNow,
                    PracticeCount = 0,
                    Difficulty = CalculateSkillDifficulty(skillName, category),
                    Importance = CalculateSkillImportance(skillName, category),
                    Prerequisites = prerequisites,
                    EffectiveStrategies = DetermineEffectiveStrategies(skillName, category, strategy)
                };

                // Initial learning session;
                var session = await PerformInitialLearningAsync(skill, strategy, cancellationToken);

                skill.Proficiency = session.FinalProficiency;
                skill.Confidence = CalculateConfidenceAfterLearning(session);
                skill.Level = CalculateProficiencyLevel(skill.Proficiency);
                skill.PracticeCount = 1;

                // Save skill;
                _skillRepository.AddSkill(skill);
                _skillRepository.AddPracticeSession(session);
                _recentSessions.Enqueue(session);

                _logger.LogInformation("Learned new skill: {SkillName}, Proficiency: {Proficiency:P2}",
                    skillName, skill.Proficiency);

                return skill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to learn skill {SkillName}", skillName);
                throw new NEDAException($"Failed to learn skill: {ex.Message}", ErrorCodes.SkillLearningFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<PracticeSession> PracticeSkillAsync(string skillId, SkillLearningStrategy strategy, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                if (!CanPracticeToday())
                {
                    throw new InvalidOperationException($"Maximum daily practice sessions ({_options.MaxSessionsPerDay}) reached");
                }

                var skill = _skillRepository.GetSkill(skillId);
                if (skill == null)
                {
                    throw new KeyNotFoundException($"Skill not found: {skillId}");
                }

                _sessionsToday++;

                // Apply skill decay before practice;
                ApplySkillDecay(skill);

                // Perform practice session;
                var session = await PerformPracticeSessionAsync(skill, strategy, cancellationToken);

                // Update skill;
                skill.Proficiency = session.FinalProficiency;
                skill.Confidence = CalculateConfidenceAfterPractice(skill, session);
                skill.Level = CalculateProficiencyLevel(skill.Proficiency);
                skill.PracticeCount++;
                skill.LastPracticed = DateTime.UtcNow;

                // Save session and update skill;
                _skillRepository.UpdateSkillProficiency(skillId, skill.Proficiency);
                _skillRepository.AddPracticeSession(session);
                _recentSessions.Enqueue(session);

                // Apply skill transfer if enabled;
                if (_options.EnableSkillTransfer)
                {
                    await ApplySkillTransferAsync(skill, session, cancellationToken);
                }

                _logger.LogInformation("Practice session completed: {SkillName}, Gain: {Gain:P2}, New Proficiency: {Proficiency:P2}",
                    skill.Name, session.ProficiencyGain, skill.Proficiency);

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Practice session failed for skill {SkillId}", skillId);
                throw new NEDAException($"Practice failed: {ex.Message}", ErrorCodes.PracticeFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public Task<Skill> GetSkillAsync(string skillId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_skillRepository.GetSkill(skillId));
        }

        /// <inheritdoc/>
        public Task<List<Skill>> GetAllSkillsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_skillRepository.GetAllSkills().ToList());
        }

        /// <inheritdoc/>
        public Task<List<Skill>> GetSkillsByCategoryAsync(string category, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_skillRepository.GetSkillsByCategory(category).ToList());
        }

        /// <inheritdoc/>
        public Task<List<Skill>> GetSkillsByProficiencyAsync(SkillProficiency minLevel, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_skillRepository.GetSkillsByProficiency(minLevel).ToList());
        }

        /// <inheritdoc/>
        public async Task UpdateSkillProficiencyAsync(string skillId, double newProficiency, CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                _skillRepository.UpdateSkillProficiency(skillId, newProficiency);
                _logger.LogDebug("Updated proficiency for skill {SkillId} to {Proficiency}", skillId, newProficiency);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task ForgetSkillAsync(string skillId, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var skill = _skillRepository.GetSkill(skillId);
                if (skill == null)
                {
                    return;
                }

                // Apply intentional forgetting;
                var newProficiency = Math.Max(0, skill.Proficiency - _options.DecayRate * 2);
                _skillRepository.UpdateSkillProficiency(skillId, newProficiency);

                _logger.LogInformation("Skill {SkillName} partially forgotten, new proficiency: {Proficiency:P2}",
                    skill.Name, newProficiency);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<SkillProgress> GetSkillProgressAsync(string skillId, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var skill = _skillRepository.GetSkill(skillId);
            if (skill == null)
            {
                throw new KeyNotFoundException($"Skill not found: {skillId}");
            }

            var history = _skillRepository.GetProficiencyHistory(skillId);
            var sessions = _skillRepository.GetPracticeHistory(skillId);

            var progress = new SkillProgress;
            {
                SkillId = skill.Id,
                SkillName = skill.Name,
                CurrentProficiency = skill.Proficiency,
                TargetProficiency = _options.MasteryThreshold,
                ProgressPercentage = skill.Proficiency / _options.MasteryThreshold,
                SessionsCompleted = sessions.Count,
                TotalPracticeTime = sessions.Sum(s => (int)(s.EndTime - s.StartTime).TotalMinutes),
                NextPracticeDue = CalculateNextPracticeDue(skill),
                EstimatedMasteryTime = CalculateEstimatedMasteryTime(skill, sessions),
                ProficiencyHistory = history,
                StrategyEffectiveness = CalculateStrategyEffectiveness(sessions)
            };

            return progress;
        }

        /// <inheritdoc/>
        public async Task<OverallProgress> GetOverallProgressAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var skills = _skillRepository.GetAllSkills().ToList();
            var sessions = _recentSessions.ToList();

            var overallProgress = new OverallProgress;
            {
                TotalSkills = skills.Count,
                MasteredSkills = skills.Count(s => s.Proficiency >= _options.MasteryThreshold),
                LearningSkills = skills.Count(s => s.Proficiency < _options.ProficiencyThreshold),
                AverageProficiency = skills.Count > 0 ? skills.Average(s => s.Proficiency) : 0,
                OverallConfidence = skills.Count > 0 ? skills.Average(s => s.Confidence) : 0,
                SkillsByCategory = skills.GroupBy(s => s.Category ?? "Uncategorized")
                    .ToDictionary(g => g.Key, g => g.Count()),
                CategoryProficiency = skills.GroupBy(s => s.Category ?? "Uncategorized")
                    .ToDictionary(g => g.Key, g => g.Average(s => s.Proficiency)),
                TopSkills = skills.OrderByDescending(s => s.Proficiency * s.Importance)
                    .Take(5)
                    .Select(s => s.Name)
                    .ToList(),
                SkillsNeedingPractice = skills;
                    .Where(s => (DateTime.UtcNow - s.LastPracticed).TotalDays > 7)
                    .OrderBy(s => s.LastPracticed)
                    .Take(10)
                    .Select(s => s.Name)
                    .ToList(),
                TotalPracticeTime = TimeSpan.FromMinutes(sessions.Sum(s => (s.EndTime - s.StartTime).TotalMinutes)),
                TotalSessions = sessions.Count;
            };

            return overallProgress;
        }

        /// <inheritdoc/>
        public async Task<List<SkillRecommendation>> GetSkillRecommendationsAsync(int limit = 10, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var recommendations = new List<SkillRecommendation>();
            var existingSkills = _skillRepository.GetAllSkills().ToList();

            // Get potential new skills;
            var potentialSkills = GetPotentialSkills()
                .Where(s => !existingSkills.Any(es => es.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var skill in potentialSkills.Take(limit))
            {
                var recommendation = new SkillRecommendation;
                {
                    Skill = skill,
                    RecommendationScore = CalculateRecommendationScore(skill, existingSkills),
                    Reason = GetRecommendationReason(skill, existingSkills),
                    ExpectedDifficulty = skill.Difficulty,
                    ExpectedLearningTime = CalculateExpectedLearningTime(skill),
                    Prerequisites = skill.Dependencies;
                };

                recommendations.Add(recommendation);
            }

            return recommendations.OrderByDescending(r => r.RecommendationScore).Take(limit).ToList();
        }

        /// <inheritdoc/>
        public async Task<PracticeSchedule> GetPracticeScheduleAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var schedule = new PracticeSchedule;
            {
                ScheduleDate = DateTime.UtcNow.Date,
                EstimatedImprovement = 0,
                TotalPracticeTime = TimeSpan.Zero;
            };

            var skills = _skillRepository.GetAllSkills()
                .Where(s => s.Proficiency < _options.MasteryThreshold)
                .OrderByDescending(s => s.Importance)
                .ThenBy(s => s.LastPracticed)
                .Take(_options.MaxSessionsPerDay)
                .ToList();

            var startTime = DateTime.UtcNow.Date.AddHours(9); // Start at 9 AM;
            foreach (var skill in skills)
            {
                var slot = new PracticeSlot;
                {
                    SkillId = skill.Id,
                    SkillName = skill.Name,
                    StartTime = startTime,
                    EndTime = startTime.AddMinutes(_options.PracticeSessionDuration),
                    Strategy = DeterminePracticeStrategy(skill),
                    ExpectedGain = CalculateExpectedGain(skill),
                    FocusArea = DetermineFocusArea(skill)
                };

                schedule.Slots.Add(slot);
                schedule.EstimatedImprovement += slot.ExpectedGain;
                schedule.TotalPracticeTime += slot.EndTime - slot.StartTime;

                startTime = slot.EndTime.AddMinutes(15); // 15-minute break between sessions;
            }

            return schedule;
        }

        /// <inheritdoc/>
        public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var learnerData = new SkillLearnerData;
                {
                    LearnerId = _learnerId,
                    Options = _options,
                    Skills = _skillRepository.GetAllSkills().ToList(),
                    RecentSessions = _recentSessions.ToList(),
                    SessionsToday = _sessionsToday,
                    LastSessionReset = _lastSessionReset,
                    Timestamp = DateTime.UtcNow;
                };

                var json = JsonConvert.SerializeObject(learnerData, Formatting.Indented);
                await System.IO.File.WriteAllTextAsync(path, json, cancellationToken);

                _logger.LogInformation("SkillLearner saved to {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save SkillLearner");
                throw new NEDAException($"Save failed: {ex.Message}", ErrorCodes.SaveFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                if (!System.IO.File.Exists(path))
                    throw new FileNotFoundException($"Learner file not found: {path}");

                var json = await System.IO.File.ReadAllTextAsync(path, cancellationToken);
                var learnerData = JsonConvert.DeserializeObject<SkillLearnerData>(json);

                if (learnerData == null)
                    throw new InvalidOperationException("Failed to deserialize learner data");

                // Restore learner state;
                _learnerId = learnerData.LearnerId;
                _sessionsToday = learnerData.SessionsToday;
                _lastSessionReset = learnerData.LastSessionReset;

                // Restore skills;
                foreach (var skill in learnerData.Skills)
                {
                    _skillRepository.AddSkill(skill);
                }

                // Restore recent sessions;
                foreach (var session in learnerData.RecentSessions)
                {
                    _recentSessions.Enqueue(session);
                }

                _logger.LogInformation("SkillLearner loaded from {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load SkillLearner");
                throw new NEDAException($"Load failed: {ex.Message}", ErrorCodes.LoadFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                // Clear all skills;
                var skills = _skillRepository.GetAllSkills().ToList();
                foreach (var skill in skills)
                {
                    _skillRepository.RemoveSkill(skill.Id);
                }

                // Clear recent sessions;
                while (_recentSessions.TryDequeue(out _)) { }

                // Reset counters;
                _sessionsToday = 0;
                _lastSessionReset = DateTime.UtcNow.Date;

                _logger.LogInformation("SkillLearner {LearnerId} reset", _learnerId);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<SkillTransferEvaluation> EvaluateSkillTransferAsync(string sourceSkillId, string targetSkillId, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var sourceSkill = _skillRepository.GetSkill(sourceSkillId);
            var targetSkill = _skillRepository.GetSkill(targetSkillId);

            if (sourceSkill == null || targetSkill == null)
            {
                throw new KeyNotFoundException("Source or target skill not found");
            }

            var evaluation = new SkillTransferEvaluation;
            {
                SourceSkillId = sourceSkillId,
                TargetSkillId = targetSkillId,
                TransferPotential = CalculateTransferPotential(sourceSkill, targetSkill),
                TransferableComponents = IdentifyTransferableComponents(sourceSkill, targetSkill),
                ExpectedAcceleration = CalculateExpectedAcceleration(sourceSkill, targetSkill),
                Recommendations = GenerateTransferRecommendations(sourceSkill, targetSkill),
                ComponentMatches = CalculateComponentMatches(sourceSkill, targetSkill)
            };

            return evaluation;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            _learningLock?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private async Task InitializeFoundationalSkillsAsync(CancellationToken cancellationToken)
        {
            var foundationalSkills = new[]
            {
                new Skill;
                {
                    Name = "Learning How to Learn",
                    Description = "Meta-cognitive skill for effective learning",
                    Category = "Cognitive",
                    Proficiency = 0.5,
                    Confidence = 0.8,
                    Level = SkillProficiency.Competent,
                    FirstLearned = DateTime.UtcNow,
                    LastPracticed = DateTime.UtcNow,
                    PracticeCount = 5,
                    Difficulty = 0.4,
                    Importance = 1.0,
                    EffectiveStrategies = new List<SkillLearningStrategy>
                    {
                        SkillLearningStrategy.SelfDirected,
                        SkillLearningStrategy.FeedbackDriven;
                    }
                },
                new Skill;
                {
                    Name = "Pattern Recognition",
                    Description = "Ability to identify patterns in data",
                    Category = "Analytical",
                    Proficiency = 0.6,
                    Confidence = 0.7,
                    Level = SkillProficiency.Proficient,
                    FirstLearned = DateTime.UtcNow,
                    LastPracticed = DateTime.UtcNow,
                    PracticeCount = 8,
                    Difficulty = 0.6,
                    Importance = 0.9,
                    EffectiveStrategies = new List<SkillLearningStrategy>
                    {
                        SkillLearningStrategy.DeliberatePractice,
                        SkillLearningStrategy.InterleavedPractice;
                    }
                },
                new Skill;
                {
                    Name = "Feedback Integration",
                    Description = "Ability to incorporate feedback for improvement",
                    Category = "Social",
                    Proficiency = 0.4,
                    Confidence = 0.6,
                    Level = SkillProficiency.Beginner,
                    FirstLearned = DateTime.UtcNow,
                    LastPracticed = DateTime.UtcNow,
                    PracticeCount = 3,
                    Difficulty = 0.5,
                    Importance = 0.8,
                    EffectiveStrategies = new List<SkillLearningStrategy>
                    {
                        SkillLearningStrategy.FeedbackDriven,
                        SkillLearningStrategy.Collaborative;
                    }
                }
            };

            foreach (var skill in foundationalSkills)
            {
                _skillRepository.AddSkill(skill);
            }

            await Task.Delay(10, cancellationToken);
        }

        private async Task InitializeLearningStrategiesAsync(CancellationToken cancellationToken)
        {
            // Initialize adaptive engine and competency builder;
            if (_adaptiveEngine != null)
            {
                await _adaptiveEngine.InitializeAsync(cancellationToken);
            }

            if (_competencyBuilder != null)
            {
                await _competencyBuilder.InitializeAsync(cancellationToken);
            }

            await Task.Delay(10, cancellationToken);
        }

        private async Task<Dictionary<string, double>> CheckPrerequisitesAsync(string skillName, string category, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);

            var prerequisites = new Dictionary<string, double>();
            var existingSkills = _skillRepository.GetAllSkills().ToList();

            // Check for related skills that could serve as prerequisites;
            foreach (var skill in existingSkills)
            {
                if (IsRelatedSkill(skill, skillName, category))
                {
                    prerequisites[skill.Name] = skill.Proficiency;
                }
            }

            return prerequisites;
        }

        private double CalculateSkillDifficulty(string skillName, string category)
        {
            var baseDifficulty = _random.NextDouble() * 0.5 + 0.3; // 0.3 to 0.8;

            // Adjust based on category;
            var categoryAdjustment = category switch;
            {
                "Technical" => 0.2,
                "Analytical" => 0.15,
                "Creative" => 0.1,
                "Social" => 0.05,
                _ => 0.0;
            };

            // Adjust based on skill name length (proxy for complexity)
            var complexityAdjustment = Math.Min(0.2, skillName.Length / 100.0);

            return Math.Min(1.0, baseDifficulty + categoryAdjustment + complexityAdjustment);
        }

        private double CalculateSkillImportance(string skillName, string category)
        {
            var baseImportance = 0.5;

            // Adjust based on category;
            var categoryImportance = category switch;
            {
                "Cognitive" => 0.3,
                "Technical" => 0.25,
                "Analytical" => 0.2,
                "Social" => 0.15,
                "Creative" => 0.1,
                "Motor" => 0.05,
                _ => 0.0;
            };

            // Adjust based on demand (simulated)
            var demandAdjustment = _random.NextDouble() * 0.3;

            return Math.Min(1.0, baseImportance + categoryImportance + demandAdjustment);
        }

        private List<SkillLearningStrategy> DetermineEffectiveStrategies(string skillName, string category, SkillLearningStrategy preferredStrategy)
        {
            var strategies = new List<SkillLearningStrategy> { preferredStrategy };

            // Add complementary strategies based on skill type;
            switch (category)
            {
                case "Technical":
                case "Analytical":
                    strategies.AddRange(new[]
                    {
                        SkillLearningStrategy.DeliberatePractice,
                        SkillLearningStrategy.SpacedRepetition;
                    });
                    break;

                case "Social":
                case "Creative":
                    strategies.AddRange(new[]
                    {
                        SkillLearningStrategy.Imitation,
                        SkillLearningStrategy.Collaborative;
                    });
                    break;

                case "Cognitive":
                    strategies.AddRange(new[]
                    {
                        SkillLearningStrategy.InterleavedPractice,
                        SkillLearningStrategy.SelfDirected;
                    });
                    break;
            }

            return strategies.Distinct().ToList();
        }

        private async Task<PracticeSession> PerformInitialLearningAsync(Skill skill, SkillLearningStrategy strategy, CancellationToken cancellationToken)
        {
            var session = new PracticeSession;
            {
                SkillId = skill.Id,
                Strategy = strategy,
                StartTime = DateTime.UtcNow,
                InitialProficiency = 0;
            };

            try
            {
                // Simulate learning process;
                await Task.Delay(100, cancellationToken);

                // Calculate proficiency gain based on strategy and difficulty;
                var baseGain = GetStrategyEffectiveness(strategy);
                var difficultyAdjustment = 1.0 - skill.Difficulty;
                var proficiencyGain = baseGain * difficultyAdjustment * _options.LearningRate;

                session.FinalProficiency = proficiencyGain;
                session.ProficiencyGain = proficiencyGain;
                session.PracticeAttempts = 10;
                session.SuccessfulAttempts = (int)(8 * proficiencyGain);
                session.SuccessRate = session.SuccessfulAttempts / (double)session.PracticeAttempts;
                session.DifficultyAdjusted = skill.Difficulty;
                session.Insights = GenerateInitialInsights(skill, strategy);
                session.SessionReport = GenerateInitialSessionReport(session, skill);
            }
            catch (Exception ex)
            {
                session.Errors.Add($"Learning failed: {ex.Message}");
                session.FinalProficiency = 0.05; // Minimal learning;
            }

            session.EndTime = DateTime.UtcNow;
            return session;
        }

        private async Task<PracticeSession> PerformPracticeSessionAsync(Skill skill, SkillLearningStrategy strategy, CancellationToken cancellationToken)
        {
            var session = new PracticeSession;
            {
                SkillId = skill.Id,
                Strategy = strategy,
                StartTime = DateTime.UtcNow,
                InitialProficiency = skill.Proficiency;
            };

            try
            {
                // Simulate practice session;
                await Task.Delay(50, cancellationToken);

                // Calculate proficiency gain;
                var baseGain = GetStrategyEffectiveness(strategy);
                var difficultyAdjustment = 1.0 - (skill.Difficulty * 0.5); // Less impact for practice;
                var retentionAdjustment = CalculateRetentionFactor(skill);
                var proficiencyGain = baseGain * difficultyAdjustment * retentionAdjustment * _options.LearningRate;

                // Cap gain based on current proficiency (diminishing returns)
                var diminishingReturns = 1.0 - (skill.Proficiency * 0.3);
                proficiencyGain *= diminishingReturns;

                session.FinalProficiency = Math.Min(1.0, skill.Proficiency + proficiencyGain);
                session.ProficiencyGain = proficiencyGain;
                session.PracticeAttempts = 20;
                session.SuccessfulAttempts = (int)(15 * proficiencyGain * (1 + skill.Confidence));
                session.SuccessRate = session.SuccessfulAttempts / (double)session.PracticeAttempts;
                session.DifficultyAdjusted = skill.Difficulty;
                session.Insights = GeneratePracticeInsights(skill, strategy, proficiencyGain);
                session.SessionReport = GeneratePracticeSessionReport(session, skill);
            }
            catch (Exception ex)
            {
                session.Errors.Add($"Practice failed: {ex.Message}");
                session.FinalProficiency = skill.Proficiency;
            }

            session.EndTime = DateTime.UtcNow;
            return session;
        }

        private void ApplySkillDecay(Skill skill)
        {
            var daysSincePractice = (DateTime.UtcNow - skill.LastPracticed).TotalDays;
            if (daysSincePractice > 1)
            {
                var decayAmount = _options.DecayRate * Math.Log(1 + daysSincePractice) * (1 - skill.RetentionRate);
                skill.Proficiency = Math.Max(0, skill.Proficiency - decayAmount);
                skill.Confidence = Math.Max(0.1, skill.Confidence - decayAmount * 0.5);
            }
        }

        private double CalculateRetentionFactor(Skill skill)
        {
            var daysSincePractice = (DateTime.UtcNow - skill.LastPracticed).TotalDays;
            var retention = Math.Pow(_options.RetentionRate, daysSincePractice);
            return Math.Max(0.1, retention);
        }

        private double GetStrategyEffectiveness(SkillLearningStrategy strategy)
        {
            return strategy switch;
            {
                SkillLearningStrategy.DeliberatePractice => 0.15,
                SkillLearningStrategy.SpacedRepetition => 0.12,
                SkillLearningStrategy.InterleavedPractice => 0.10,
                SkillLearningStrategy.FeedbackDriven => 0.14,
                SkillLearningStrategy.Imitation => 0.08,
                SkillLearningStrategy.Guided => 0.11,
                SkillLearningStrategy.SelfDirected => 0.09,
                SkillLearningStrategy.Collaborative => 0.07,
                _ => 0.05;
            };
        }

        private double CalculateConfidenceAfterLearning(PracticeSession session)
        {
            var baseConfidence = 0.5;
            var successConfidence = session.SuccessRate * 0.3;
            var gainConfidence = session.ProficiencyGain * 0.2;

            return Math.Min(1.0, baseConfidence + successConfidence + gainConfidence);
        }

        private double CalculateConfidenceAfterPractice(Skill skill, PracticeSession session)
        {
            var baseConfidence = skill.Confidence;
            var successConfidence = session.SuccessRate * 0.2;
            var gainConfidence = session.ProficiencyGain * 0.3;
            var consistencyBonus = skill.PracticeCount > 5 ? 0.1 : 0;

            var newConfidence = baseConfidence * 0.5 + successConfidence + gainConfidence + consistencyBonus;
            return Math.Min(1.0, newConfidence);
        }

        private SkillProficiency CalculateProficiencyLevel(double proficiency)
        {
            return proficiency switch;
            {
                < 0.2 => SkillProficiency.Novice,
                < 0.4 => SkillProficiency.Beginner,
                < 0.6 => SkillProficiency.Competent,
                < 0.8 => SkillProficiency.Proficient,
                < 0.95 => SkillProficiency.Expert,
                _ => SkillProficiency.Master;
            };
        }

        private async Task ApplySkillTransferAsync(Skill sourceSkill, PracticeSession session, CancellationToken cancellationToken)
        {
            if (!_options.EnableSkillTransfer)
                return;

            var relatedSkills = _skillRepository.GetAllSkills()
                .Where(s => s.Id != sourceSkill.Id && IsRelatedSkill(s, sourceSkill.Name, sourceSkill.Category))
                .ToList();

            foreach (var targetSkill in relatedSkills)
            {
                var transferFactor = CalculateTransferFactor(sourceSkill, targetSkill);
                if (transferFactor > 0.1) // Only apply significant transfers;
                {
                    var transferGain = session.ProficiencyGain * transferFactor * 0.3;
                    var newProficiency = Math.Min(1.0, targetSkill.Proficiency + transferGain);

                    _skillRepository.UpdateSkillProficiency(targetSkill.Id, newProficiency);

                    _logger.LogDebug("Skill transfer: {Source} -> {Target}, Gain: {Gain:P4}",
                        sourceSkill.Name, targetSkill.Name, transferGain);
                }
            }

            await Task.Delay(10, cancellationToken);
        }

        private bool IsRelatedSkill(Skill skill, string targetName, string targetCategory)
        {
            // Check category match;
            if (skill.Category == targetCategory)
                return true;

            // Check name similarity (simple version)
            var nameSimilarity = CalculateStringSimilarity(skill.Name, targetName);
            return nameSimilarity > 0.3;
        }

        private double CalculateStringSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0;

            var aWords = a.ToLowerInvariant().Split(' ');
            var bWords = b.ToLowerInvariant().Split(' ');

            var commonWords = aWords.Intersect(bWords).Count();
            var totalWords = Math.Max(aWords.Length, bWords.Length);

            return commonWords / (double)totalWords;
        }

        private double CalculateTransferFactor(Skill source, Skill target)
        {
            var categoryFactor = source.Category == target.Category ? 0.3 : 0.1;
            var proficiencyFactor = source.Proficiency * 0.4;
            var confidenceFactor = source.Confidence * 0.3;

            return categoryFactor + proficiencyFactor + confidenceFactor;
        }

        private DateTime CalculateNextPracticeDue(Skill skill)
        {
            var daysSincePractice = (DateTime.UtcNow - skill.LastPracticed).TotalDays;
            var optimalInterval = CalculateOptimalInterval(skill);

            return skill.LastPracticed.AddDays(optimalInterval);
        }

        private double CalculateOptimalInterval(Skill skill)
        {
            // Spaced repetition algorithm (simplified)
            var baseInterval = 1.0;
            var proficiencyFactor = skill.Proficiency * 2.0;
            var confidenceFactor = skill.Confidence * 1.5;
            var practiceFactor = Math.Log(1 + skill.PracticeCount);

            return baseInterval * proficiencyFactor * confidenceFactor * practiceFactor;
        }

        private double CalculateEstimatedMasteryTime(Skill skill, List<PracticeSession> sessions)
        {
            if (sessions.Count < 2)
                return 100; // Default estimate;

            var recentGains = sessions;
                .Select(s => s.ProficiencyGain)
                .Where(g => g > 0)
                .ToList();

            if (recentGains.Count == 0)
                return 100;

            var averageGain = recentGains.Average();
            var remainingProficiency = _options.MasteryThreshold - skill.Proficiency;

            return remainingProficiency / averageGain * _options.PracticeSessionDuration / 60.0; // Convert to hours;
        }

        private Dictionary<string, double> CalculateStrategyEffectiveness(List<PracticeSession> sessions)
        {
            var effectiveness = new Dictionary<string, double>();
            var groupedSessions = sessions.GroupBy(s => s.Strategy);

            foreach (var group in groupedSessions)
            {
                if (group.Count() >= 3)
                {
                    effectiveness[group.Key.ToString()] = group.Average(s => s.ProficiencyGain);
                }
            }

            return effectiveness;
        }

        private List<Skill> GetPotentialSkills()
        {
            var potentialSkills = new List<Skill>();

            // Generate some example skills based on categories;
            foreach (var category in _options.SkillCategories)
            {
                for (int i = 1; i <= 3; i++)
                {
                    potentialSkills.Add(new Skill;
                    {
                        Name = $"{category} Skill {i}",
                        Category = category,
                        Difficulty = _random.NextDouble() * 0.5 + 0.3,
                        Importance = _random.NextDouble() * 0.5 + 0.3,
                        Dependencies = new List<string>()
                    });
                }
            }

            return potentialSkills;
        }

        private double CalculateRecommendationScore(Skill skill, List<Skill> existingSkills)
        {
            var importanceScore = skill.Importance * 0.4;
            var difficultyScore = (1 - skill.Difficulty) * 0.3;
            var relatednessScore = CalculateAverageRelatedness(skill, existingSkills) * 0.3;

            return importanceScore + difficultyScore + relatednessScore;
        }

        private double CalculateAverageRelatedness(Skill skill, List<Skill> existingSkills)
        {
            if (existingSkills.Count == 0)
                return 0.5;

            var relatednessScores = existingSkills;
                .Select(es => IsRelatedSkill(es, skill.Name, skill.Category) ? 0.8 : 0.2)
                .ToList();

            return relatednessScores.Average();
        }

        private string GetRecommendationReason(Skill skill, List<Skill> existingSkills)
        {
            var reasons = new List<string>();

            if (skill.Importance > 0.7)
                reasons.Add("High importance");

            if (skill.Difficulty < 0.4)
                reasons.Add("Low difficulty");

            var relatedSkills = existingSkills.Count(es => IsRelatedSkill(es, skill.Name, skill.Category));
            if (relatedSkills > 0)
                reasons.Add($"Related to {relatedSkills} existing skill(s)");

            return reasons.Any() ? string.Join(", ", reasons) : "Balanced skill for development";
        }

        private double CalculateExpectedLearningTime(Skill skill)
        {
            var baseTime = 10.0; // hours;
            var difficultyAdjustment = skill.Difficulty * 20.0;
            var importanceAdjustment = (1 - skill.Importance) * 5.0;

            return baseTime + difficultyAdjustment - importanceAdjustment;
        }

        private SkillLearningStrategy DeterminePracticeStrategy(Skill skill)
        {
            // Use most effective strategy or rotate if multiple;
            if (skill.EffectiveStrategies.Any())
            {
                var index = skill.PracticeCount % skill.EffectiveStrategies.Count;
                return skill.EffectiveStrategies[index];
            }

            // Default strategy;
            return _options.Strategies[_random.Next(_options.Strategies.Count)];
        }

        private double CalculateExpectedGain(Skill skill)
        {
            var baseGain = 0.05;
            var proficiencyFactor = 1.0 - (skill.Proficiency * 0.5);
            var confidenceFactor = skill.Confidence;
            var practiceFactor = 1.0 / Math.Max(1, Math.Log(1 + skill.PracticeCount));

            return baseGain * proficiencyFactor * confidenceFactor * practiceFactor;
        }

        private string DetermineFocusArea(Skill skill)
        {
            var areas = new[] { "Fundamentals", "Speed", "Accuracy", "Complexity", "Adaptation" };

            if (skill.Proficiency < 0.4)
                return areas[0]; // Fundamentals;

            if (skill.PracticeCount < 5)
                return areas[1]; // Speed;

            return areas[_random.Next(2, areas.Length)];
        }

        private bool CanPracticeToday()
        {
            // Reset daily counter if it's a new day;
            if (DateTime.UtcNow.Date > _lastSessionReset.Date)
            {
                _sessionsToday = 0;
                _lastSessionReset = DateTime.UtcNow.Date;
            }

            return _sessionsToday < _options.MaxSessionsPerDay;
        }

        private List<string> GenerateInitialInsights(Skill skill, SkillLearningStrategy strategy)
        {
            var insights = new List<string>
            {
                $"Initial learning of {skill.Name} using {strategy} strategy",
                $"Skill difficulty: {skill.Difficulty:P2}",
                $"Effective strategies identified: {string.Join(", ", skill.EffectiveStrategies)}"
            };

            if (skill.Prerequisites.Any())
            {
                insights.Add($"Prerequisites: {string.Join(", ", skill.Prerequisites.Keys)}");
            }

            return insights;
        }

        private List<string> GeneratePracticeInsights(Skill skill, SkillLearningStrategy strategy, double gain)
        {
            var insights = new List<string>
            {
                $"Practice session for {skill.Name} using {strategy}",
                $"Proficiency gain: {gain:P4}",
                $"Current level: {skill.Level}"
            };

            if (gain > 0.05)
            {
                insights.Add("Significant improvement detected");
            }
            else if (gain < 0.01)
            {
                insights.Add("Minimal improvement - consider strategy change");
            }

            return insights;
        }

        private string GenerateInitialSessionReport(PracticeSession session, Skill skill)
        {
            return $@"
Initial Learning Session Report;
==============================
Skill: {skill.Name}
Category: {skill.Category}
Strategy: {session.Strategy}

Results:
- Initial Proficiency: {session.InitialProficiency:P2}
- Final Proficiency: {session.FinalProficiency:P2}
- Proficiency Gain: {session.ProficiencyGain:P4}
- Practice Attempts: {session.PracticeAttempts}
- Successful Attempts: {session.SuccessfulAttempts}
- Success Rate: {session.SuccessRate:P2}

Skill Characteristics:
- Difficulty: {skill.Difficulty:P2}
- Importance: {skill.Importance:P2}
- Effective Strategies: {string.Join(", ", skill.EffectiveStrategies)}
";
        }

        private string GeneratePracticeSessionReport(PracticeSession session, Skill skill)
        {
            return $@"
Practice Session Report;
======================
Skill: {skill.Name}
Category: {skill.Category}
Strategy: {session.Strategy}
Duration: {(session.EndTime - session.StartTime).TotalMinutes:F1} minutes;

Results:
- Initial Proficiency: {session.InitialProficiency:P2}
- Final Proficiency: {session.FinalProficiency:P2}
- Proficiency Gain: {session.ProficiencyGain:P4}
- Practice Attempts: {session.PracticeAttempts}
- Successful Attempts: {session.SuccessfulAttempts}
- Success Rate: {session.SuccessRate:P2}

Skill Status:
- Current Proficiency: {skill.Proficiency:P2}
- Confidence: {skill.Confidence:P2}
- Level: {skill.Level}
- Practice Count: {skill.PracticeCount}
";
        }

        private List<string> IdentifyTransferableComponents(Skill source, Skill target)
        {
            var components = new List<string>();

            if (source.Category == target.Category)
                components.Add("Category knowledge");

            if (CalculateStringSimilarity(source.Name, target.Name) > 0.4)
                components.Add("Conceptual understanding");

            if (source.EffectiveStrategies.Intersect(target.EffectiveStrategies).Any())
                components.Add("Learning strategies");

            return components;
        }

        private double CalculateExpectedAcceleration(Skill source, Skill target)
        {
            var transferPotential = CalculateTransferPotential(source, target);
            var sourceProficiency = source.Proficiency;

            return transferPotential * sourceProficiency * 0.5;
        }

        private double CalculateTransferPotential(Skill source, Skill target)
        {
            var categoryMatch = source.Category == target.Category ? 0.3 : 0.1;
            var strategyOverlap = source.EffectiveStrategies.Intersect(target.EffectiveStrategies).Count() /
                                  (double)Math.Max(1, target.EffectiveStrategies.Count) * 0.4;
            var nameSimilarity = CalculateStringSimilarity(source.Name, target.Name) * 0.3;

            return categoryMatch + strategyOverlap + nameSimilarity;
        }

        private List<string> GenerateTransferRecommendations(Skill source, Skill target)
        {
            var recommendations = new List<string>();

            if (source.Proficiency > 0.7)
            {
                recommendations.Add($"Apply expertise from {source.Name} to accelerate {target.Name} learning");
            }

            if (source.EffectiveStrategies.Intersect(target.EffectiveStrategies).Any())
            {
                recommendations.Add($"Use successful learning strategies from {source.Name}");
            }

            if (source.Category == target.Category)
            {
                recommendations.Add($"Leverage category-specific knowledge transfer");
            }

            return recommendations;
        }

        private Dictionary<string, double> CalculateComponentMatches(Skill source, Skill target)
        {
            return new Dictionary<string, double>
            {
                ["Category"] = source.Category == target.Category ? 1.0 : 0.0,
                ["Name Similarity"] = CalculateStringSimilarity(source.Name, target.Name),
                ["Strategy Overlap"] = source.EffectiveStrategies.Intersect(target.EffectiveStrategies).Count() /
                                      (double)Math.Max(1, source.EffectiveStrategies.Union(target.EffectiveStrategies).Count()),
                ["Difficulty Ratio"] = Math.Min(source.Difficulty, target.Difficulty) /
                                      Math.Max(source.Difficulty, target.Difficulty)
            };
        }

        #endregion;

        #region Internal Classes;

        private class SkillLearnerData;
        {
            public string LearnerId { get; set; }
            public SkillLearnerOptions Options { get; set; }
            public List<Skill> Skills { get; set; }
            public List<PracticeSession> RecentSessions { get; set; }
            public int SessionsToday { get; set; }
            public DateTime LastSessionReset { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion;

        #region Error Codes;

        private static class ErrorCodes;
        {
            public const string InitializationFailed = "SKILL_LEARNER_001";
            public const string SkillLearningFailed = "SKILL_LEARNER_002";
            public const string PracticeFailed = "SKILL_LEARNER_003";
            public const string SaveFailed = "SKILL_LEARNER_004";
            public const string LoadFailed = "SKILL_LEARNER_005";
        }

        #endregion;
    }

    /// <summary>
    /// Factory for creating SkillLearner instances;
    /// </summary>
    public interface ISkillLearnerFactory;
    {
        /// <summary>
        /// Creates a new SkillLearner instance;
        /// </summary>
        ISkillLearner CreateLearner(string profile = "default");

        /// <summary>
        /// Creates a SkillLearner with custom configuration;
        /// </summary>
        ISkillLearner CreateLearner(SkillLearnerOptions options);
    }

    /// <summary>
    /// Implementation of SkillLearner factory;
    /// </summary>
    public class SkillLearnerFactory : ISkillLearnerFactory;
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SkillLearnerFactory> _logger;

        public SkillLearnerFactory(IServiceProvider serviceProvider, ILogger<SkillLearnerFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public ISkillLearner CreateLearner(string profile = "default")
        {
            _logger.LogInformation("Creating SkillLearner with profile: {Profile}", profile);

            return profile.ToLower() switch;
            {
                "fast" => CreateFastLearner(),
                "deep" => CreateDeepLearner(),
                "broad" => CreateBroadLearner(),
                "specialized" => CreateSpecializedLearner(),
                _ => CreateDefaultLearner()
            };
        }

        public ISkillLearner CreateLearner(SkillLearnerOptions options)
        {
            // Create with custom options using dependency injection;
            return _serviceProvider.GetRequiredService<ISkillLearner>();
        }

        private ISkillLearner CreateDefaultLearner()
        {
            return _serviceProvider.GetRequiredService<ISkillLearner>();
        }

        private ISkillLearner CreateFastLearner()
        {
            var options = new SkillLearnerOptions;
            {
                LearningRate = 0.02,
                PracticeSessionDuration = 20,
                MaxSessionsPerDay = 15,
                Strategies = new List<SkillLearningStrategy>
                {
                    SkillLearningStrategy.DeliberatePractice,
                    SkillLearningStrategy.SpacedRepetition;
                },
                ProficiencyThreshold = 0.6,
                MasteryThreshold = 0.9;
            };

            return CreateLearner(options);
        }

        private ISkillLearner CreateDeepLearner()
        {
            var options = new SkillLearnerOptions;
            {
                LearningRate = 0.005,
                PracticeSessionDuration = 45,
                MaxSessionsPerDay = 5,
                Strategies = new List<SkillLearningStrategy>
                {
                    SkillLearningStrategy.DeliberatePractice,
                    SkillLearningStrategy.FeedbackDriven,
                    SkillLearningStrategy.Guided;
                },
                ProficiencyThreshold = 0.8,
                MasteryThreshold = 0.98,
                RetentionRate = 0.95;
            };

            return CreateLearner(options);
        }

        private ISkillLearner CreateBroadLearner()
        {
            var options = new SkillLearnerOptions;
            {
                LearningRate = 0.01,
                PracticeSessionDuration = 25,
                MaxSessionsPerDay = 8,
                MaxSkills = 5000,
                SkillCategories = new List<string>
                {
                    "Cognitive", "Technical", "Social", "Creative",
                    "Analytical", "Motor", "Language", "Artistic"
                },
                Strategies = Enum.GetValues<SkillLearningStrategy>().ToList(),
                EnableSkillTransfer = true;
            };

            return CreateLearner(options);
        }

        private ISkillLearner CreateSpecializedLearner()
        {
            var options = new SkillLearnerOptions;
            {
                LearningRate = 0.015,
                PracticeSessionDuration = 40,
                MaxSessionsPerDay = 6,
                SkillCategories = new List<string> { "Technical", "Analytical" },
                Strategies = new List<SkillLearningStrategy>
                {
                    SkillLearningStrategy.DeliberatePractice,
                    SkillLearningStrategy.InterleavedPractice,
                    SkillLearningStrategy.SelfDirected;
                },
                ProficiencyThreshold = 0.75,
                MasteryThreshold = 0.97,
                DifficultyLevels = new Dictionary<string, double>
                {
                    ["Beginner"] = 0.4,
                    ["Intermediate"] = 0.7,
                    ["Advanced"] = 0.9,
                    ["Expert"] = 1.0;
                }
            };

            return CreateLearner(options);
        }
    }
}
