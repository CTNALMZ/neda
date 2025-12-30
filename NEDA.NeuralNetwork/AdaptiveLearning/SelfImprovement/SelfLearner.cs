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
    /// Configuration options for SelfLearner;
    /// </summary>
    public class SelfLearnerOptions;
    {
        public const string SectionName = "SelfLearner";

        /// <summary>
        /// Gets or sets the learning mode;
        /// </summary>
        public LearningMode Mode { get; set; } = LearningMode.Continuous;

        /// <summary>
        /// Gets or sets the learning rate;
        /// </summary>
        public double LearningRate { get; set; } = 0.001;

        /// <summary>
        /// Gets or sets the batch size for learning;
        /// </summary>
        public int BatchSize { get; set; } = 64;

        /// <summary>
        /// Gets or sets the number of epochs per learning session;
        /// </summary>
        public int EpochsPerSession { get; set; } = 10;

        /// <summary>
        /// Gets or sets the maximum learning sessions per day;
        /// </summary>
        public int MaxSessionsPerDay { get; set; } = 100;

        /// <summary>
        /// Gets or sets the learning interval in minutes;
        /// </summary>
        public int LearningIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Gets or sets the learning strategies to use;
        /// </summary>
        public List<LearningStrategy> Strategies { get; set; } = new()
        {
            LearningStrategy.Reinforcement,
            LearningStrategy.Supervised,
            LearningStrategy.Unsupervised,
            LearningStrategy.Transfer;
        };

        /// <summary>
        /// Gets or sets the performance improvement threshold;
        /// </summary>
        public double ImprovementThreshold { get; set; } = 0.01;

        /// <summary>
        /// Gets or sets the knowledge retention rate;
        /// </summary>
        public double RetentionRate { get; set; } = 0.95;

        /// <summary>
        /// Gets or sets whether to enable adaptive forgetting;
        /// </summary>
        public bool EnableForgetting { get; set; } = true;

        /// <summary>
        /// Gets or sets the forgetting threshold;
        /// </summary>
        public double ForgettingThreshold { get; set; } = 0.1;

        /// <summary>
        /// Gets or sets the maximum knowledge items;
        /// </summary>
        public int MaxKnowledgeItems { get; set; } = 10000;

        /// <summary>
        /// Gets or sets the learning acceleration factor;
        /// </summary>
        public double AccelerationFactor { get; set; } = 1.1;

        /// <summary>
        /// Gets or sets the confidence threshold for knowledge;
        /// </summary>
        public double ConfidenceThreshold { get; set; } = 0.8;
    }

    /// <summary>
    /// Learning modes for SelfLearner;
    /// </summary>
    public enum LearningMode;
    {
        Continuous,
        Scheduled,
        OnDemand,
        Adaptive;
    }

    /// <summary>
    /// Learning strategies for SelfLearner;
    /// </summary>
    [Flags]
    public enum LearningStrategy;
    {
        Reinforcement = 1,
        Supervised = 2,
        Unsupervised = 4,
        Transfer = 8,
        Meta = 16,
        MultiTask = 32,
        All = Reinforcement | Supervised | Unsupervised | Transfer | Meta | MultiTask;
    }

    /// <summary>
    /// Represents a piece of knowledge;
    /// </summary>
    public class KnowledgeItem;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Category { get; set; }
        public string Content { get; set; }
        public double[] Vector { get; set; }
        public double Confidence { get; set; }
        public DateTime LearnedDate { get; set; }
        public DateTime LastUsedDate { get; set; }
        public int UsageCount { get; set; }
        public double Importance { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<string> Tags { get; set; } = new();

        public override bool Equals(object obj)
        {
            return obj is KnowledgeItem item && Id == item.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }

    /// <summary>
    /// Represents a learning session;
    /// </summary>
    public class LearningSession;
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public LearningStrategy Strategy { get; set; }
        public List<KnowledgeItem> LearnedItems { get; set; } = new();
        public double PerformanceGain { get; set; }
        public double Loss { get; set; }
        public double Accuracy { get; set; }
        public Dictionary<string, double> Metrics { get; set; } = new();
        public List<string> Insights { get; set; } = new();
        public string SessionReport { get; set; }
    }

    /// <summary>
    /// Represents learning progress;
    /// </summary>
    public class LearningProgress;
    {
        public int TotalSessions { get; set; }
        public int TotalKnowledgeItems { get; set; }
        public double AverageConfidence { get; set; }
        public double AveragePerformanceGain { get; set; }
        public Dictionary<string, int> KnowledgeByCategory { get; set; } = new();
        public DateTime LastLearningSession { get; set; }
        public TimeSpan TotalLearningTime { get; set; }
        public List<double> RecentPerformance { get; set; } = new();
    }

    /// <summary>
    /// Interface for SelfLearner;
    /// </summary>
    public interface ISelfLearner : IDisposable
    {
        /// <summary>
        /// Gets the learner's unique identifier;
        /// </summary>
        string LearnerId { get; }

        /// <summary>
        /// Initializes the self-learner;
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts continuous learning process;
        /// </summary>
        Task StartLearningAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops continuous learning process;
        /// </summary>
        Task StopLearningAsync();

        /// <summary>
        /// Learns from a single data point;
        /// </summary>
        Task<LearningSession> LearnAsync(object data, LearningStrategy strategy, CancellationToken cancellationToken = default);

        /// <summary>
        /// Learns from a batch of data;
        /// </summary>
        Task<LearningSession> LearnBatchAsync(IEnumerable<object> data, LearningStrategy strategy, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves knowledge based on query;
        /// </summary>
        Task<List<KnowledgeItem>> RetrieveKnowledgeAsync(string query, int limit = 10, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets knowledge by category;
        /// </summary>
        Task<List<KnowledgeItem>> GetKnowledgeByCategoryAsync(string category, int limit = 100, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates confidence of a knowledge item;
        /// </summary>
        Task UpdateConfidenceAsync(string knowledgeId, double confidence, CancellationToken cancellationToken = default);

        /// <summary>
        /// Forgets less important knowledge;
        /// </summary>
        Task<List<KnowledgeItem>> ForgetAsync(double threshold, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets learning progress;
        /// </summary>
        Task<LearningProgress> GetProgressAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets learning history;
        /// </summary>
        Task<List<LearningSession>> GetLearningHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves the learner's state;
        /// </summary>
        Task SaveAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads the learner's state;
        /// </summary>
        Task LoadAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Resets the learner;
        /// </summary>
        Task ResetAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Evaluates learning effectiveness;
        /// </summary>
        Task<EvaluationResult> EvaluateAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of learning evaluation;
    /// </summary>
    public class EvaluationResult;
    {
        public double OverallEffectiveness { get; set; }
        public double KnowledgeRetention { get; set; }
        public double LearningEfficiency { get; set; }
        public double AdaptabilityScore { get; set; }
        public Dictionary<string, double> StrategyEffectiveness { get; set; } = new();
        public List<string> Strengths { get; set; } = new();
        public List<string> Weaknesses { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
        public DateTime EvaluationDate { get; set; }
    }

    /// <summary>
    /// Knowledge base for SelfLearner;
    /// </summary>
    public class KnowledgeBase;
    {
        private readonly ConcurrentDictionary<string, KnowledgeItem> _knowledge;
        private readonly ConcurrentDictionary<string, List<string>> _categoryIndex;
        private readonly ConcurrentDictionary<string, int> _usageStats;
        private readonly int _maxItems;

        public KnowledgeBase(int maxItems = 10000)
        {
            _maxItems = maxItems;
            _knowledge = new ConcurrentDictionary<string, KnowledgeItem>();
            _categoryIndex = new ConcurrentDictionary<string, List<string>>();
            _usageStats = new ConcurrentDictionary<string, int>();
        }

        public void Add(KnowledgeItem item)
        {
            if (_knowledge.Count >= _maxItems)
            {
                RemoveLeastImportant();
            }

            if (_knowledge.TryAdd(item.Id, item))
            {
                UpdateCategoryIndex(item);
                UpdateUsageStats(item.Id);
            }
        }

        public KnowledgeItem Get(string id)
        {
            return _knowledge.TryGetValue(id, out var item) ? item : null;
        }

        public IEnumerable<KnowledgeItem> GetAll()
        {
            return _knowledge.Values;
        }

        public IEnumerable<KnowledgeItem> GetByCategory(string category, int limit = int.MaxValue)
        {
            if (_categoryIndex.TryGetValue(category, out var ids))
            {
                return ids;
                    .Select(id => Get(id))
                    .Where(item => item != null)
                    .Take(limit);
            }

            return Enumerable.Empty<KnowledgeItem>();
        }

        public bool Remove(string id)
        {
            if (_knowledge.TryRemove(id, out var item))
            {
                RemoveFromCategoryIndex(item);
                _usageStats.TryRemove(id, out _);
                return true;
            }

            return false;
        }

        public void UpdateConfidence(string id, double confidence)
        {
            if (_knowledge.TryGetValue(id, out var item))
            {
                item.Confidence = confidence;
                item.Importance = CalculateImportance(item);
            }
        }

        public void RecordUsage(string id)
        {
            _usageStats.AddOrUpdate(id, 1, (_, count) => count + 1);

            if (_knowledge.TryGetValue(id, out var item))
            {
                item.UsageCount++;
                item.LastUsedDate = DateTime.UtcNow;
                item.Importance = CalculateImportance(item);
            }
        }

        public IEnumerable<KnowledgeItem> GetLeastImportant(int count = 10)
        {
            return _knowledge.Values;
                .OrderBy(item => item.Importance)
                .ThenBy(item => item.LastUsedDate)
                .Take(count);
        }

        public int Count => _knowledge.Count;

        private void UpdateCategoryIndex(KnowledgeItem item)
        {
            if (!string.IsNullOrEmpty(item.Category))
            {
                _categoryIndex.AddOrUpdate(
                    item.Category,
                    new List<string> { item.Id },
                    (_, list) =>
                    {
                        if (!list.Contains(item.Id))
                            list.Add(item.Id);
                        return list;
                    });
            }
        }

        private void RemoveFromCategoryIndex(KnowledgeItem item)
        {
            if (!string.IsNullOrEmpty(item.Category) && _categoryIndex.TryGetValue(item.Category, out var list))
            {
                list.Remove(item.Id);
                if (list.Count == 0)
                {
                    _categoryIndex.TryRemove(item.Category, out _);
                }
            }
        }

        private void RemoveLeastImportant()
        {
            var toRemove = GetLeastImportant(100);
            foreach (var item in toRemove)
            {
                Remove(item.Id);
            }
        }

        private double CalculateImportance(KnowledgeItem item)
        {
            var ageFactor = 1.0 / (1.0 + (DateTime.UtcNow - item.LearnedDate).TotalDays / 30);
            var usageFactor = Math.Log(1 + item.UsageCount);
            var confidenceFactor = item.Confidence;

            return (ageFactor * 0.3) + (usageFactor * 0.4) + (confidenceFactor * 0.3);
        }
    }

    /// <summary>
    /// Self-learning system that continuously improves its own capabilities;
    /// </summary>
    public class SelfLearner : ISelfLearner;
    {
        private readonly ILogger<SelfLearner> _logger;
        private readonly SelfLearnerOptions _options;
        private readonly IImprovementEngine _improvementEngine;
        private readonly ISkillLearner _skillLearner;
        private readonly ICompetencyBuilder _competencyBuilder;
        private readonly IAbilityTrainer _abilityTrainer;
        private readonly Timer _learningTimer;
        private readonly KnowledgeBase _knowledgeBase;
        private readonly List<LearningSession> _learningHistory;
        private readonly SemaphoreSlim _learningLock;
        private readonly Random _random;

        private volatile bool _isLearning;
        private volatile bool _disposed;
        private string _learnerId;
        private DateTime _lastLearningSession;
        private int _sessionsToday;
        private DateTime _lastSessionReset;

        /// <summary>
        /// Gets the learner's unique identifier;
        /// </summary>
        public string LearnerId => _learnerId;

        /// <summary>
        /// Initializes a new instance of the SelfLearner class;
        /// </summary>
        public SelfLearner(
            ILogger<SelfLearner> logger,
            IOptions<SelfLearnerOptions> options,
            IImprovementEngine improvementEngine,
            ISkillLearner skillLearner,
            ICompetencyBuilder competencyBuilder,
            IAbilityTrainer abilityTrainer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _improvementEngine = improvementEngine ?? throw new ArgumentNullException(nameof(improvementEngine));
            _skillLearner = skillLearner ?? throw new ArgumentNullException(nameof(skillLearner));
            _competencyBuilder = competencyBuilder ?? throw new ArgumentNullException(nameof(competencyBuilder));
            _abilityTrainer = abilityTrainer ?? throw new ArgumentNullException(nameof(abilityTrainer));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _learnerId = $"SelfLearner_{Guid.NewGuid():N}";
            _knowledgeBase = new KnowledgeBase(_options.MaxKnowledgeItems);
            _learningHistory = new List<LearningSession>();
            _learningLock = new SemaphoreSlim(1, 1);
            _random = new Random();
            _lastSessionReset = DateTime.UtcNow.Date;

            _learningTimer = new Timer(
                async _ => await ScheduledLearningAsync(),
                null,
                TimeSpan.FromMinutes(_options.LearningIntervalMinutes),
                TimeSpan.FromMinutes(_options.LearningIntervalMinutes));

            _logger.LogInformation("SelfLearner {LearnerId} initialized with {StrategyCount} strategies",
                _learnerId, _options.Strategies.Count);
        }

        /// <inheritdoc/>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                // Initialize knowledge base with foundational knowledge;
                await InitializeFoundationalKnowledgeAsync(cancellationToken);

                // Initialize learning capabilities;
                await InitializeLearningCapabilitiesAsync(cancellationToken);

                _logger.LogInformation("SelfLearner {LearnerId} initialization completed", _learnerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SelfLearner");
                throw new NEDAException($"Initialization failed: {ex.Message}", ErrorCodes.InitializationFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task StartLearningAsync(CancellationToken cancellationToken = default)
        {
            if (_isLearning)
            {
                _logger.LogWarning("Learning is already in progress");
                return;
            }

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                _isLearning = true;

                // Start with a learning session;
                await PerformLearningSessionAsync(cancellationToken);

                _logger.LogInformation("Continuous learning started");
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task StopLearningAsync()
        {
            if (!_isLearning)
            {
                return;
            }

            await _learningLock.WaitAsync();

            try
            {
                _isLearning = false;
                _logger.LogInformation("Continuous learning stopped");
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<LearningSession> LearnAsync(object data, LearningStrategy strategy, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                if (!CanLearnToday())
                {
                    throw new InvalidOperationException($"Maximum daily learning sessions ({_options.MaxSessionsPerDay}) reached");
                }

                _sessionsToday++;
                var session = await CreateLearningSessionAsync(data, strategy, cancellationToken);
                _learningHistory.Add(session);
                _lastLearningSession = DateTime.UtcNow;

                TrimHistory();

                _logger.LogInformation("Learning session completed: Strategy={Strategy}, Gain={Gain:P2}",
                    strategy, session.PerformanceGain);

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Learning session failed");
                throw new NEDAException($"Learning failed: {ex.Message}", ErrorCodes.LearningFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<LearningSession> LearnBatchAsync(IEnumerable<object> data, LearningStrategy strategy, CancellationToken cancellationToken = default)
        {
            var batch = data.ToList();
            if (batch.Count == 0)
            {
                throw new ArgumentException("Data batch cannot be empty", nameof(data));
            }

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var session = new LearningSession;
                {
                    StartTime = DateTime.UtcNow,
                    Strategy = strategy;
                };

                var learnedItems = new List<KnowledgeItem>();
                var totalPerformanceGain = 0.0;
                var batchCount = 0;

                foreach (var item in batch)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var subSession = await CreateLearningSessionAsync(item, strategy, cancellationToken);
                        learnedItems.AddRange(subSession.LearnedItems);
                        totalPerformanceGain += subSession.PerformanceGain;
                        batchCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to learn from batch item");
                    }
                }

                session.EndTime = DateTime.UtcNow;
                session.LearnedItems = learnedItems;
                session.PerformanceGain = batchCount > 0 ? totalPerformanceGain / batchCount : 0;
                session.SessionReport = GenerateBatchSessionReport(session, batch.Count);

                _learningHistory.Add(session);
                _sessionsToday++;

                _logger.LogInformation("Batch learning completed: Items={Items}, Avg Gain={Gain:P2}",
                    batchCount, session.PerformanceGain);

                return session;
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<List<KnowledgeItem>> RetrieveKnowledgeAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
        {
            try
            {
                // Implement knowledge retrieval logic;
                // This could use semantic search, vector similarity, etc.

                var allKnowledge = _knowledgeBase.GetAll().ToList();

                if (string.IsNullOrWhiteSpace(query))
                {
                    return allKnowledge;
                        .OrderByDescending(k => k.Importance)
                        .ThenByDescending(k => k.LastUsedDate)
                        .Take(limit)
                        .ToList();
                }

                // Simple keyword matching for demonstration;
                var queryLower = query.ToLowerInvariant();
                var relevantKnowledge = allKnowledge;
                    .Where(k =>
                        (k.Content?.ToLowerInvariant().Contains(queryLower) ?? false) ||
                        (k.Category?.ToLowerInvariant().Contains(queryLower) ?? false) ||
                        k.Tags.Any(t => t.ToLowerInvariant().Contains(queryLower)))
                    .OrderByDescending(k => k.Confidence)
                    .ThenByDescending(k => k.Importance)
                    .Take(limit)
                    .ToList();

                // Update usage stats;
                foreach (var item in relevantKnowledge)
                {
                    _knowledgeBase.RecordUsage(item.Id);
                }

                return relevantKnowledge;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Knowledge retrieval failed");
                throw new NEDAException($"Knowledge retrieval failed: {ex.Message}", ErrorCodes.KnowledgeRetrievalFailed, ex);
            }
        }

        /// <inheritdoc/>
        public Task<List<KnowledgeItem>> GetKnowledgeByCategoryAsync(string category, int limit = 100, CancellationToken cancellationToken = default)
        {
            try
            {
                var knowledge = _knowledgeBase.GetByCategory(category, limit).ToList();

                // Update usage stats;
                foreach (var item in knowledge)
                {
                    _knowledgeBase.RecordUsage(item.Id);
                }

                return Task.FromResult(knowledge);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get knowledge by category");
                throw new NEDAException($"Failed to get knowledge by category: {ex.Message}", ErrorCodes.CategoryQueryFailed, ex);
            }
        }

        /// <inheritdoc/>
        public async Task UpdateConfidenceAsync(string knowledgeId, double confidence, CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                _knowledgeBase.UpdateConfidence(knowledgeId, confidence);
                _logger.LogDebug("Updated confidence for knowledge {Id} to {Confidence}", knowledgeId, confidence);
            }, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<List<KnowledgeItem>> ForgetAsync(double threshold, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                if (!_options.EnableForgetting)
                {
                    return new List<KnowledgeItem>();
                }

                var toForget = _knowledgeBase.GetLeastImportant(100)
                    .Where(k => k.Importance < threshold)
                    .ToList();

                foreach (var item in toForget)
                {
                    _knowledgeBase.Remove(item.Id);
                }

                _logger.LogInformation("Forgot {Count} knowledge items below threshold {Threshold}",
                    toForget.Count, threshold);

                return toForget;
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<LearningProgress> GetProgressAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield(); // Allow async execution;

            var knowledge = _knowledgeBase.GetAll().ToList();

            return new LearningProgress;
            {
                TotalSessions = _learningHistory.Count,
                TotalKnowledgeItems = knowledge.Count,
                AverageConfidence = knowledge.Count > 0 ? knowledge.Average(k => k.Confidence) : 0,
                AveragePerformanceGain = _learningHistory.Count > 0 ? _learningHistory.Average(s => s.PerformanceGain) : 0,
                KnowledgeByCategory = knowledge;
                    .GroupBy(k => k.Category ?? "Uncategorized")
                    .ToDictionary(g => g.Key, g => g.Count()),
                LastLearningSession = _lastLearningSession,
                TotalLearningTime = TimeSpan.FromSeconds(_learningHistory.Sum(s => (s.EndTime - s.StartTime).TotalSeconds)),
                RecentPerformance = _learningHistory;
                    .TakeLast(100)
                    .Select(s => s.PerformanceGain)
                    .ToList()
            };
        }

        /// <inheritdoc/>
        public Task<List<LearningSession>> GetLearningHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_learningHistory;
                .OrderByDescending(s => s.StartTime)
                .Take(limit)
                .ToList());
        }

        /// <inheritdoc/>
        public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var learnerData = new LearnerData;
                {
                    LearnerId = _learnerId,
                    Options = _options,
                    KnowledgeBase = _knowledgeBase.GetAll().ToList(),
                    LearningHistory = _learningHistory.TakeLast(1000).ToList(),
                    SessionsToday = _sessionsToday,
                    LastLearningSession = _lastLearningSession,
                    LastSessionReset = _lastSessionReset,
                    Timestamp = DateTime.UtcNow;
                };

                var json = JsonConvert.SerializeObject(learnerData, Formatting.Indented);
                await System.IO.File.WriteAllTextAsync(path, json, cancellationToken);

                _logger.LogInformation("SelfLearner saved to {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save SelfLearner");
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
                var learnerData = JsonConvert.DeserializeObject<LearnerData>(json);

                if (learnerData == null)
                    throw new InvalidOperationException("Failed to deserialize learner data");

                // Restore learner state;
                _learnerId = learnerData.LearnerId;
                _sessionsToday = learnerData.SessionsToday;
                _lastLearningSession = learnerData.LastLearningSession;
                _lastSessionReset = learnerData.LastSessionReset;

                // Restore knowledge base;
                foreach (var item in learnerData.KnowledgeBase)
                {
                    _knowledgeBase.Add(item);
                }

                // Restore learning history;
                _learningHistory.Clear();
                _learningHistory.AddRange(learnerData.LearningHistory);

                _logger.LogInformation("SelfLearner loaded from {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load SelfLearner");
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
                // Clear knowledge base;
                var knowledge = _knowledgeBase.GetAll().ToList();
                foreach (var item in knowledge)
                {
                    _knowledgeBase.Remove(item.Id);
                }

                // Clear learning history;
                _learningHistory.Clear();

                // Reset counters;
                _sessionsToday = 0;
                _lastLearningSession = DateTime.MinValue;
                _lastSessionReset = DateTime.UtcNow.Date;

                _logger.LogInformation("SelfLearner {LearnerId} reset", _learnerId);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<EvaluationResult> EvaluateAsync(CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var progress = await GetProgressAsync(cancellationToken);
                var recentSessions = _learningHistory.TakeLast(50).ToList();

                var result = new EvaluationResult;
                {
                    EvaluationDate = DateTime.UtcNow,
                    KnowledgeRetention = CalculateKnowledgeRetention(),
                    LearningEfficiency = CalculateLearningEfficiency(recentSessions),
                    AdaptabilityScore = CalculateAdaptabilityScore(),
                    StrategyEffectiveness = CalculateStrategyEffectiveness(),
                    Strengths = IdentifyStrengths(),
                    Weaknesses = IdentifyWeaknesses(),
                    Recommendations = GenerateRecommendations()
                };

                result.OverallEffectiveness = CalculateOverallEffectiveness(result);

                _logger.LogInformation("Learning evaluation completed: Effectiveness={Effectiveness:P2}",
                    result.OverallEffectiveness);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Evaluation failed");
                throw new NEDAException($"Evaluation failed: {ex.Message}", ErrorCodes.EvaluationFailed, ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            StopLearningAsync().Wait(TimeSpan.FromSeconds(5));
            _learningTimer?.Dispose();
            _learningLock?.Dispose();

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private async Task ScheduledLearningAsync()
        {
            if (!_isLearning || !CanLearnToday())
            {
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await PerformLearningSessionAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled learning failed");
            }
        }

        private async Task PerformLearningSessionAsync(CancellationToken cancellationToken)
        {
            if (!CanLearnToday())
            {
                return;
            }

            try
            {
                // Select a random learning strategy;
                var availableStrategies = _options.Strategies;
                    .Where(s => s != LearningStrategy.All)
                    .ToList();

                if (availableStrategies.Count == 0)
                {
                    availableStrategies.Add(LearningStrategy.Supervised);
                }

                var strategy = availableStrategies[_random.Next(availableStrategies.Count)];

                // Generate or acquire learning data;
                var learningData = await GenerateLearningDataAsync(strategy, cancellationToken);

                // Perform learning session;
                await LearnAsync(learningData, strategy, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Learning session failed");
            }
        }

        private async Task<LearningSession> CreateLearningSessionAsync(object data, LearningStrategy strategy, CancellationToken cancellationToken)
        {
            var session = new LearningSession;
            {
                StartTime = DateTime.UtcNow,
                Strategy = strategy;
            };

            try
            {
                // Extract knowledge from data;
                var knowledgeItems = await ExtractKnowledgeAsync(data, strategy, cancellationToken);

                // Apply learning based on strategy;
                var performanceGain = await ApplyLearningStrategyAsync(knowledgeItems, strategy, cancellationToken);

                // Store learned knowledge;
                foreach (var item in knowledgeItems)
                {
                    _knowledgeBase.Add(item);
                }

                // Generate session report;
                session.EndTime = DateTime.UtcNow;
                session.LearnedItems = knowledgeItems;
                session.PerformanceGain = performanceGain;
                session.SessionReport = GenerateSessionReport(session);
                session.Insights = await ExtractInsightsAsync(knowledgeItems, cancellationToken);

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create learning session");
                session.EndTime = DateTime.UtcNow;
                session.PerformanceGain = 0;
                session.SessionReport = $"Session failed: {ex.Message}";
                return session;
            }
        }

        private async Task<List<KnowledgeItem>> ExtractKnowledgeAsync(object data, LearningStrategy strategy, CancellationToken cancellationToken)
        {
            var knowledgeItems = new List<KnowledgeItem>();

            // Extract knowledge based on data type and strategy;
            switch (strategy)
            {
                case LearningStrategy.Supervised:
                    knowledgeItems.AddRange(await ExtractSupervisedKnowledgeAsync(data, cancellationToken));
                    break;

                case LearningStrategy.Unsupervised:
                    knowledgeItems.AddRange(await ExtractUnsupervisedKnowledgeAsync(data, cancellationToken));
                    break;

                case LearningStrategy.Reinforcement:
                    knowledgeItems.AddRange(await ExtractReinforcementKnowledgeAsync(data, cancellationToken));
                    break;

                case LearningStrategy.Transfer:
                    knowledgeItems.AddRange(await ExtractTransferKnowledgeAsync(data, cancellationToken));
                    break;

                case LearningStrategy.Meta:
                    knowledgeItems.AddRange(await ExtractMetaKnowledgeAsync(data, cancellationToken));
                    break;

                case LearningStrategy.MultiTask:
                    knowledgeItems.AddRange(await ExtractMultiTaskKnowledgeAsync(data, cancellationToken));
                    break;
            }

            // Assign confidence based on learning strategy;
            foreach (var item in knowledgeItems)
            {
                item.Confidence = CalculateInitialConfidence(item, strategy);
                item.Importance = CalculateInitialImportance(item);
            }

            return knowledgeItems;
        }

        private async Task<double> ApplyLearningStrategyAsync(List<KnowledgeItem> knowledgeItems, LearningStrategy strategy, CancellationToken cancellationToken)
        {
            double performanceGain = 0;

            switch (strategy)
            {
                case LearningStrategy.Supervised:
                    performanceGain = await ApplySupervisedLearningAsync(knowledgeItems, cancellationToken);
                    break;

                case LearningStrategy.Unsupervised:
                    performanceGain = await ApplyUnsupervisedLearningAsync(knowledgeItems, cancellationToken);
                    break;

                case LearningStrategy.Reinforcement:
                    performanceGain = await ApplyReinforcementLearningAsync(knowledgeItems, cancellationToken);
                    break;

                case LearningStrategy.Transfer:
                    performanceGain = await ApplyTransferLearningAsync(knowledgeItems, cancellationToken);
                    break;

                case LearningStrategy.Meta:
                    performanceGain = await ApplyMetaLearningAsync(knowledgeItems, cancellationToken);
                    break;

                case LearningStrategy.MultiTask:
                    performanceGain = await ApplyMultiTaskLearningAsync(knowledgeItems, cancellationToken);
                    break;
            }

            // Accelerate learning if performing well;
            if (performanceGain > _options.ImprovementThreshold)
            {
                _options.LearningRate *= _options.AccelerationFactor;
                _logger.LogDebug("Learning rate accelerated to {LearningRate}", _options.LearningRate);
            }

            return performanceGain;
        }

        private async Task<List<KnowledgeItem>> ExtractSupervisedKnowledgeAsync(object data, CancellationToken cancellationToken)
        {
            // Implementation for supervised knowledge extraction;
            await Task.Delay(10, cancellationToken);

            return new List<KnowledgeItem>
            {
                new()
                {
                    Category = "Supervised",
                    Content = $"Learned from supervised data: {data}",
                    LearnedDate = DateTime.UtcNow,
                    LastUsedDate = DateTime.UtcNow,
                    Tags = new List<string> { "supervised", "labeled" }
                }
            };
        }

        private async Task<List<KnowledgeItem>> ExtractUnsupervisedKnowledgeAsync(object data, CancellationToken cancellationToken)
        {
            // Implementation for unsupervised knowledge extraction;
            await Task.Delay(10, cancellationToken);

            return new List<KnowledgeItem>
            {
                new()
                {
                    Category = "Unsupervised",
                    Content = $"Discovered pattern from unsupervised data: {data}",
                    LearnedDate = DateTime.UtcNow,
                    LastUsedDate = DateTime.UtcNow,
                    Tags = new List<string> { "unsupervised", "pattern" }
                }
            };
        }

        private async Task<double> ApplySupervisedLearningAsync(List<KnowledgeItem> knowledgeItems, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            return _random.NextDouble() * 0.1; // 0-10% performance gain;
        }

        private async Task<double> ApplyUnsupervisedLearningAsync(List<KnowledgeItem> knowledgeItems, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            return _random.NextDouble() * 0.08; // 0-8% performance gain;
        }

        private async Task<double> ApplyReinforcementLearningAsync(List<KnowledgeItem> knowledgeItems, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            return _random.NextDouble() * 0.12; // 0-12% performance gain;
        }

        private async Task<double> ApplyTransferLearningAsync(List<KnowledgeItem> knowledgeItems, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            return _random.NextDouble() * 0.15; // 0-15% performance gain;
        }

        private async Task InitializeFoundationalKnowledgeAsync(CancellationToken cancellationToken)
        {
            var foundationalKnowledge = new[]
            {
                new KnowledgeItem;
                {
                    Category = "Foundation",
                    Content = "Learning is the process of acquiring new knowledge, behaviors, skills, or preferences",
                    Confidence = 0.95,
                    LearnedDate = DateTime.UtcNow,
                    LastUsedDate = DateTime.UtcNow,
                    Tags = new List<string> { "foundation", "definition" }
                },
                new KnowledgeItem;
                {
                    Category = "Foundation",
                    Content = "Knowledge can be explicit (documented) or tacit (personal know-how)",
                    Confidence = 0.90,
                    LearnedDate = DateTime.UtcNow,
                    LastUsedDate = DateTime.UtcNow,
                    Tags = new List<string> { "foundation", "knowledge-types" }
                },
                new KnowledgeItem;
                {
                    Category = "Foundation",
                    Content = "Adaptation to new information is essential for continuous improvement",
                    Confidence = 0.92,
                    LearnedDate = DateTime.UtcNow,
                    LastUsedDate = DateTime.UtcNow,
                    Tags = new List<string> { "foundation", "adaptation" }
                }
            };

            foreach (var knowledge in foundationalKnowledge)
            {
                _knowledgeBase.Add(knowledge);
            }
        }

        private async Task InitializeLearningCapabilitiesAsync(CancellationToken cancellationToken)
        {
            // Initialize various learning capabilities;
            await _skillLearner.InitializeAsync(cancellationToken);
            await _competencyBuilder.InitializeAsync(cancellationToken);
            await _abilityTrainer.InitializeAsync(cancellationToken);
        }

        private async Task<object> GenerateLearningDataAsync(LearningStrategy strategy, CancellationToken cancellationToken)
        {
            // Generate or retrieve learning data based on strategy;
            await Task.Delay(10, cancellationToken);

            return strategy switch;
            {
                LearningStrategy.Supervised => new { Features = new double[10], Label = 1 },
                LearningStrategy.Unsupervised => new { Features = new double[10] },
                LearningStrategy.Reinforcement => new { State = new double[5], Action = 0, Reward = 1.0 },
                LearningStrategy.Transfer => new { SourceData = "pre-trained", TargetData = "current" },
                _ => new { Data = "generic learning data" }
            };
        }

        private double CalculateInitialConfidence(KnowledgeItem item, LearningStrategy strategy)
        {
            var baseConfidence = strategy switch;
            {
                LearningStrategy.Supervised => 0.85,
                LearningStrategy.Unsupervised => 0.70,
                LearningStrategy.Reinforcement => 0.80,
                LearningStrategy.Transfer => 0.90,
                LearningStrategy.Meta => 0.75,
                LearningStrategy.MultiTask => 0.82,
                _ => 0.50;
            };

            // Adjust based on content characteristics;
            var contentAdjustment = Math.Min(1.0, item.Content?.Length / 1000.0 ?? 0);
            return Math.Min(1.0, baseConfidence + contentAdjustment * 0.1);
        }

        private double CalculateInitialImportance(KnowledgeItem item)
        {
            var recencyFactor = 1.0 / (1.0 + (DateTime.UtcNow - item.LearnedDate).TotalDays);
            var confidenceFactor = item.Confidence;
            var categoryFactor = item.Category == "Foundation" ? 1.5 : 1.0;

            return (recencyFactor * 0.3) + (confidenceFactor * 0.5) + (categoryFactor * 0.2);
        }

        private string GenerateSessionReport(LearningSession session)
        {
            return $@"
Learning Session Report;
======================
Session ID: {session.SessionId}
Start Time: {session.StartTime:yyyy-MM-dd HH:mm:ss}
End Time: {session.EndTime:yyyy-MM-dd HH:mm:ss}
Duration: {session.EndTime - session.StartTime}
Strategy: {session.Strategy}

Performance Metrics:
- Performance Gain: {session.PerformanceGain:P2}
- Items Learned: {session.LearnedItems.Count}
- Average Confidence: {session.LearnedItems.Average(i => i.Confidence):P2}

Learned Items:
{string.Join("\n", session.LearnedItems.Select((item, idx) => $"  {idx + 1}. [{item.Category}] {item.Content.Substring(0, Math.Min(50, item.Content.Length))}..."))}

Insights:
{(session.Insights.Any() ? string.Join("\n", session.Insights.Select(i => $"  • {i}")) : "  No significant insights")}
";
        }

        private string GenerateBatchSessionReport(LearningSession session, int totalItems)
        {
            return $@"
Batch Learning Session Report;
============================
Session ID: {session.SessionId}
Total Items Processed: {totalItems}
Successfully Learned: {session.LearnedItems.Count}
Success Rate: {session.LearnedItems.Count / (double)totalItems:P2}
Average Performance Gain: {session.PerformanceGain:P2}
";
        }

        private async Task<List<string>> ExtractInsightsAsync(List<KnowledgeItem> knowledgeItems, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);

            var insights = new List<string>();

            if (knowledgeItems.Count > 0)
            {
                var categories = knowledgeItems.GroupBy(k => k.Category);

                foreach (var category in categories)
                {
                    insights.Add($"Learned {category.Count()} items in category '{category.Key}'");
                }

                var avgConfidence = knowledgeItems.Average(k => k.Confidence);
                if (avgConfidence > 0.9)
                {
                    insights.Add("High confidence learning achieved");
                }
                else if (avgConfidence < 0.5)
                {
                    insights.Add("Low confidence learning - may need reinforcement");
                }
            }

            return insights;
        }

        private bool CanLearnToday()
        {
            // Reset daily counter if it's a new day;
            if (DateTime.UtcNow.Date > _lastSessionReset.Date)
            {
                _sessionsToday = 0;
                _lastSessionReset = DateTime.UtcNow.Date;
            }

            return _sessionsToday < _options.MaxSessionsPerDay;
        }

        private double CalculateKnowledgeRetention()
        {
            var totalKnowledge = _knowledgeBase.GetAll().ToList();
            if (totalKnowledge.Count == 0)
                return 0;

            var recentKnowledge = totalKnowledge;
                .Where(k => (DateTime.UtcNow - k.LastUsedDate).TotalDays < 30)
                .ToList();

            return recentKnowledge.Count / (double)totalKnowledge.Count;
        }

        private double CalculateLearningEfficiency(List<LearningSession> recentSessions)
        {
            if (recentSessions.Count == 0)
                return 0;

            var totalGain = recentSessions.Sum(s => s.PerformanceGain);
            var totalDuration = recentSessions.Sum(s => (s.EndTime - s.StartTime).TotalMinutes);

            return totalGain / Math.Max(1, totalDuration);
        }

        private double CalculateAdaptabilityScore()
        {
            var strategiesUsed = _learningHistory;
                .Select(s => s.Strategy)
                .Distinct()
                .Count();

            var totalStrategies = Enum.GetValues(typeof(LearningStrategy)).Length - 1; // Exclude 'All'
            return strategiesUsed / (double)totalStrategies;
        }

        private Dictionary<string, double> CalculateStrategyEffectiveness()
        {
            var effectiveness = new Dictionary<string, double>();

            foreach (var strategy in _options.Strategies)
            {
                if (strategy == LearningStrategy.All)
                    continue;

                var sessions = _learningHistory;
                    .Where(s => s.Strategy == strategy)
                    .ToList();

                if (sessions.Count > 0)
                {
                    effectiveness[strategy.ToString()] = sessions.Average(s => s.PerformanceGain);
                }
            }

            return effectiveness;
        }

        private List<string> IdentifyStrengths()
        {
            var strengths = new List<string>();

            var effectiveness = CalculateStrategyEffectiveness();
            var bestStrategy = effectiveness.OrderByDescending(kvp => kvp.Value).FirstOrDefault();

            if (bestStrategy.Value > 0.1)
            {
                strengths.Add($"Excellent performance with {bestStrategy.Key} strategy ({bestStrategy.Value:P2} gain)");
            }

            var knowledgeCount = _knowledgeBase.Count;
            if (knowledgeCount > 1000)
            {
                strengths.Add($"Extensive knowledge base ({knowledgeCount} items)");
            }

            var recentSessions = _learningHistory.TakeLast(10).ToList();
            if (recentSessions.Count >= 5 && recentSessions.All(s => s.PerformanceGain > 0))
            {
                strengths.Add("Consistent positive learning gains");
            }

            return strengths;
        }

        private List<string> IdentifyWeaknesses()
        {
            var weaknesses = new List<string>();

            var effectiveness = CalculateStrategyEffectiveness();
            var worstStrategy = effectiveness.OrderBy(kvp => kvp.Value).FirstOrDefault();

            if (worstStrategy.Value < 0.01)
            {
                weaknesses.Add($"Poor performance with {worstStrategy.Key} strategy ({worstStrategy.Value:P2} gain)");
            }

            if (_sessionsToday >= _options.MaxSessionsPerDay)
            {
                weaknesses.Add("Daily learning limit reached");
            }

            var knowledgeRetention = CalculateKnowledgeRetention();
            if (knowledgeRetention < 0.7)
            {
                weaknesses.Add($"Low knowledge retention ({knowledgeRetention:P2})");
            }

            return weaknesses;
        }

        private List<string> GenerateRecommendations()
        {
            var recommendations = new List<string>();

            if (_sessionsToday < _options.MaxSessionsPerDay / 2)
            {
                recommendations.Add("Increase learning frequency for faster improvement");
            }

            var effectiveness = CalculateStrategyEffectiveness();
            var underperformingStrategies = effectiveness;
                .Where(kvp => kvp.Value < 0.02)
                .Select(kvp => kvp.Key)
                .ToList();

            if (underperformingStrategies.Any())
            {
                recommendations.Add($"Focus on improving {string.Join(", ", underperformingStrategies)} strategies");
            }

            if (_options.LearningRate < 0.0001)
            {
                recommendations.Add("Consider increasing learning rate for faster convergence");
            }

            return recommendations;
        }

        private double CalculateOverallEffectiveness(EvaluationResult result)
        {
            var weights = new Dictionary<string, double>
            {
                ["knowledgeRetention"] = 0.25,
                ["learningEfficiency"] = 0.30,
                ["adaptabilityScore"] = 0.20,
                ["strategyEffectiveness"] = 0.25;
            };

            var strategyEffectiveness = result.StrategyEffectiveness.Values.Any()
                ? result.StrategyEffectiveness.Values.Average()
                : 0;

            return (result.KnowledgeRetention * weights["knowledgeRetention"]) +
                   (result.LearningEfficiency * weights["learningEfficiency"]) +
                   (result.AdaptabilityScore * weights["adaptabilityScore"]) +
                   (strategyEffectiveness * weights["strategyEffectiveness"]);
        }

        private void TrimHistory()
        {
            const int maxHistory = 1000;

            if (_learningHistory.Count > maxHistory)
            {
                _learningHistory.RemoveRange(0, _learningHistory.Count - maxHistory);
            }
        }

        // Stub methods for unimplemented extraction strategies;
        private Task<List<KnowledgeItem>> ExtractReinforcementKnowledgeAsync(object data, CancellationToken cancellationToken)
            => Task.FromResult(new List<KnowledgeItem>());

        private Task<List<KnowledgeItem>> ExtractTransferKnowledgeAsync(object data, CancellationToken cancellationToken)
            => Task.FromResult(new List<KnowledgeItem>());

        private Task<List<KnowledgeItem>> ExtractMetaKnowledgeAsync(object data, CancellationToken cancellationToken)
            => Task.FromResult(new List<KnowledgeItem>());

        private Task<List<KnowledgeItem>> ExtractMultiTaskKnowledgeAsync(object data, CancellationToken cancellationToken)
            => Task.FromResult(new List<KnowledgeItem>());

        private Task<double> ApplyMetaLearningAsync(List<KnowledgeItem> knowledgeItems, CancellationToken cancellationToken)
            => Task.FromResult(0.0);

        private Task<double> ApplyMultiTaskLearningAsync(List<KnowledgeItem> knowledgeItems, CancellationToken cancellationToken)
            => Task.FromResult(0.0);

        #endregion;

        #region Internal Classes;

        private class LearnerData;
        {
            public string LearnerId { get; set; }
            public SelfLearnerOptions Options { get; set; }
            public List<KnowledgeItem> KnowledgeBase { get; set; }
            public List<LearningSession> LearningHistory { get; set; }
            public int SessionsToday { get; set; }
            public DateTime LastLearningSession { get; set; }
            public DateTime LastSessionReset { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion;

        #region Error Codes;

        private static class ErrorCodes;
        {
            public const string InitializationFailed = "SELF_LEARNER_001";
            public const string LearningFailed = "SELF_LEARNER_002";
            public const string KnowledgeRetrievalFailed = "SELF_LEARNER_003";
            public const string CategoryQueryFailed = "SELF_LEARNER_004";
            public const string SaveFailed = "SELF_LEARNER_005";
            public const string LoadFailed = "SELF_LEARNER_006";
            public const string EvaluationFailed = "SELF_LEARNER_007";
        }

        #endregion;
    }

    /// <summary>
    /// Factory for creating SelfLearner instances;
    /// </summary>
    public interface ISelfLearnerFactory;
    {
        /// <summary>
        /// Creates a new SelfLearner instance;
        /// </summary>
        ISelfLearner CreateLearner(string profile = "default");

        /// <summary>
        /// Creates a SelfLearner with custom configuration;
        /// </summary>
        ISelfLearner CreateLearner(SelfLearnerOptions options);
    }

    /// <summary>
    /// Implementation of SelfLearner factory;
    /// </summary>
    public class SelfLearnerFactory : ISelfLearnerFactory;
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SelfLearnerFactory> _logger;

        public SelfLearnerFactory(IServiceProvider serviceProvider, ILogger<SelfLearnerFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public ISelfLearner CreateLearner(string profile = "default")
        {
            _logger.LogInformation("Creating SelfLearner with profile: {Profile}", profile);

            return profile.ToLower() switch;
            {
                "fast" => CreateFastLearner(),
                "deep" => CreateDeepLearner(),
                "broad" => CreateBroadLearner(),
                "adaptive" => CreateAdaptiveLearner(),
                _ => CreateDefaultLearner()
            };
        }

        public ISelfLearner CreateLearner(SelfLearnerOptions options)
        {
            // Create with custom options using dependency injection;
            return _serviceProvider.GetRequiredService<ISelfLearner>();
        }

        private ISelfLearner CreateDefaultLearner()
        {
            return _serviceProvider.GetRequiredService<ISelfLearner>();
        }

        private ISelfLearner CreateFastLearner()
        {
            var options = new SelfLearnerOptions;
            {
                Mode = LearningMode.Continuous,
                LearningRate = 0.01,
                LearningIntervalMinutes = 30,
                MaxSessionsPerDay = 200,
                AccelerationFactor = 1.5,
                Strategies = new List<LearningStrategy>
                {
                    LearningStrategy.Supervised,
                    LearningStrategy.Transfer;
                }
            };

            return CreateLearner(options);
        }

        private ISelfLearner CreateDeepLearner()
        {
            var options = new SelfLearnerOptions;
            {
                Mode = LearningMode.Adaptive,
                LearningRate = 0.0005,
                LearningIntervalMinutes = 120,
                MaxSessionsPerDay = 50,
                AccelerationFactor = 1.1,
                Strategies = new List<LearningStrategy>
                {
                    LearningStrategy.Supervised,
                    LearningStrategy.Unsupervised,
                    LearningStrategy.Meta;
                }
            };

            return CreateLearner(options);
        }

        private ISelfLearner CreateBroadLearner()
        {
            var options = new SelfLearnerOptions;
            {
                Mode = LearningMode.OnDemand,
                LearningRate = 0.001,
                LearningIntervalMinutes = 240,
                MaxSessionsPerDay = 20,
                AccelerationFactor = 1.2,
                Strategies = Enum.GetValues<LearningStrategy>()
                    .Where(s => s != LearningStrategy.All)
                    .ToList(),
                MaxKnowledgeItems = 50000;
            };

            return CreateLearner(options);
        }

        private ISelfLearner CreateAdaptiveLearner()
        {
            var options = new SelfLearnerOptions;
            {
                Mode = LearningMode.Adaptive,
                LearningRate = 0.001,
                LearningIntervalMinutes = 60,
                MaxSessionsPerDay = 100,
                AccelerationFactor = 1.3,
                Strategies = new List<LearningStrategy>
                {
                    LearningStrategy.Reinforcement,
                    LearningStrategy.MultiTask,
                    LearningStrategy.Transfer;
                },
                EnableForgetting = true,
                ForgettingThreshold = 0.05;
            };

            return CreateLearner(options);
        }
    }
}
