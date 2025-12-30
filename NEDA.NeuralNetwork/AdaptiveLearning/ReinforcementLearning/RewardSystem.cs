using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Monitoring.MetricsCollector;
using NEDA.AI.MachineLearning;
using NEDA.AI.ReinforcementLearning;

namespace NEDA.NeuralNetwork.ReinforcementLearning;
{
    /// <summary>
    /// Advanced reward system for reinforcement learning with multi-objective optimization,
    /// intrinsic motivation, and adaptive reward shaping;
    /// </summary>
    public interface IRewardSystem : IDisposable
    {
        /// <summary>
        /// Current reward configuration;
        /// </summary>
        RewardConfiguration Configuration { get; }

        /// <summary>
        /// Reward history for analysis and debugging;
        /// </summary>
        IReadOnlyList<RewardRecord> RewardHistory { get; }

        /// <summary>
        /// Current reward statistics;
        /// </summary>
        RewardStatistics Statistics { get; }

        /// <summary>
        /// Initialize the reward system;
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculate reward for a given state-action pair;
        /// </summary>
        Task<RewardCalculation> CalculateRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculate intrinsic reward for exploration;
        /// </summary>
        Task<IntrinsicReward> CalculateIntrinsicRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Apply reward shaping to improve learning efficiency;
        /// </summary>
        Task<ShapedReward> ApplyRewardShapingAsync(
            string agentId,
            RewardCalculation baseReward,
            EnvironmentState state,
            EnvironmentState nextState,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Update reward model based on feedback;
        /// </summary>
        Task UpdateModelAsync(
            string agentId,
            RewardFeedback feedback,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Get reward components for debugging and analysis;
        /// </summary>
        Task<RewardBreakdown> GetRewardBreakdownAsync(
            string agentId,
            string episodeId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Register a custom reward component;
        /// </summary>
        void RegisterRewardComponent(IRewardComponent component);

        /// <summary>
        /// Set reward weights dynamically;
        /// </summary>
        void SetRewardWeights(string agentId, RewardWeights weights);

        /// <summary>
        /// Event raised when reward is calculated;
        /// </summary>
        event EventHandler<RewardCalculatedEventArgs> RewardCalculated;

        /// <summary>
        /// Event raised when reward model is updated;
        /// </summary>
        event EventHandler<ModelUpdatedEventArgs> ModelUpdated;
    }

    /// <summary>
    /// Main implementation of advanced reward system;
    /// </summary>
    public class RewardSystem : IRewardSystem;
    {
        private readonly ILogger<RewardSystem> _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly ISettingsManager _settingsManager;
        private readonly IConfiguration _configuration;
        private readonly IRewardModelStore _modelStore;
        private readonly ConcurrentDictionary<string, AgentContext> _agentContexts;
        private readonly ConcurrentDictionary<string, IRewardComponent> _rewardComponents;
        private readonly ConcurrentQueue<RewardRecord> _rewardHistory;
        private readonly ConcurrentDictionary<string, RewardStatistics> _agentStatistics;
        private readonly SemaphoreSlim _calculationLock;
        private readonly Random _random;
        private readonly int _maxHistorySize = 10000;
        private readonly TimeSpan _modelUpdateInterval = TimeSpan.FromMinutes(5);
        private DateTime _lastModelUpdate;
        private bool _initialized;
        private CancellationTokenSource _updateCts;

        /// <summary>
        /// Reward calculation result;
        /// </summary>
        public class RewardCalculation;
        {
            public string AgentId { get; set; }
            public string EpisodeId { get; set; }
            public int Step { get; set; }
            public double TotalReward { get; set; }
            public Dictionary<string, double> ComponentRewards { get; set; }
            public RewardType RewardType { get; set; }
            public DateTime CalculatedAt { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
            public double Confidence { get; set; }
            public bool IsShaped { get; set; }
        }

        /// <summary>
        /// Intrinsic reward calculation;
        /// </summary>
        public class IntrinsicReward;
        {
            public string AgentId { get; set; }
            public double CuriosityReward { get; set; }
            public double SurpriseReward { get; set; }
            public double EmpowermentReward { get; set; }
            public double NoveltyReward { get; set; }
            public double TotalIntrinsic { get; set; }
            public IntrinsicMotivationType MotivationType { get; set; }
            public Dictionary<string, double> Components { get; set; }
        }

        /// <summary>
        /// Shaped reward result;
        /// </summary>
        public class ShapedReward;
        {
            public RewardCalculation BaseReward { get; set; }
            public double ShapingBonus { get; set; }
            public double PotentialDifference { get; set; }
            public double ShapedTotal { get; set; }
            public ShapingMethod Method { get; set; }
            public bool IsNormalized { get; set; }
        }

        /// <summary>
        /// Reward configuration;
        /// </summary>
        public class RewardConfiguration;
        {
            public RewardStrategy Strategy { get; set; } = RewardStrategy.Adaptive;
            public double ExtrinsicWeight { get; set; } = 0.7;
            public double IntrinsicWeight { get; set; } = 0.3;
            public bool EnableRewardShaping { get; set; } = true;
            public bool EnableRewardNormalization { get; set; } = true;
            public double DiscountFactor { get; set; } = 0.99;
            public double LearningRate { get; set; } = 0.01;
            public double ExplorationBonus { get; set; } = 0.1;
            public double ExploitationBonus { get; set; } = 0.05;
            public RewardNormalizationMethod NormalizationMethod { get; set; } = RewardNormalizationMethod.RunningStats;
            public Dictionary<string, double> ComponentWeights { get; set; } = new();
            public Dictionary<string, RewardComponentConfig> ComponentConfigs { get; set; } = new();
            public TimeSpan StatisticsUpdateInterval { get; set; } = TimeSpan.FromSeconds(30);
            public int HistoryBufferSize { get; set; } = 10000;
            public bool EnableMultiObjective { get; set; } = true;
        }

        /// <summary>
        /// Reward component configuration;
        /// </summary>
        public class RewardComponentConfig;
        {
            public bool Enabled { get; set; } = true;
            public double Weight { get; set; } = 1.0;
            public double MinValue { get; set; } = double.MinValue;
            public double MaxValue { get; set; } = double.MaxValue;
            public bool Normalize { get; set; } = true;
            public double ClipValue { get; set; } = 10.0;
            public Dictionary<string, object> Parameters { get; set; } = new();
        }

        /// <summary>
        /// Reward statistics;
        /// </summary>
        public class RewardStatistics;
        {
            public string AgentId { get; set; }
            public DateTime WindowStart { get; set; }
            public DateTime WindowEnd { get; set; }
            public int TotalSteps { get; set; }
            public int TotalEpisodes { get; set; }
            public double MeanReward { get; set; }
            public double StdDevReward { get; set; }
            public double MinReward { get; set; }
            public double MaxReward { get; set; }
            public double TotalExtrinsic { get; set; }
            public double TotalIntrinsic { get; set; }
            public Dictionary<string, double> ComponentMeans { get; set; } = new();
            public Dictionary<string, double> ComponentStdDevs { get; set; } = new();
            public double SharpeRatio { get; set; }
            public double LearningProgress { get; set; }
            public Dictionary<string, double> Correlations { get; set; } = new();
        }

        /// <summary>
        /// Reward record for history;
        /// </summary>
        public class RewardRecord;
        {
            public string RecordId { get; set; }
            public string AgentId { get; set; }
            public string EpisodeId { get; set; }
            public int Step { get; set; }
            public double Reward { get; set; }
            public Dictionary<string, double> Components { get; set; }
            public RewardType Type { get; set; }
            public DateTime Timestamp { get; set; }
            public EnvironmentState State { get; set; }
            public AgentAction Action { get; set; }
            public EnvironmentState NextState { get; set; }
            public bool IsTerminal { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
        }

        /// <summary>
        /// Reward feedback for model updates;
        /// </summary>
        public class RewardFeedback;
        {
            public string AgentId { get; set; }
            public string EpisodeId { get; set; }
            public List<RewardRecord> EpisodeRecords { get; set; }
            public double EpisodeReturn { get; set; }
            public double ExpectedReturn { get; set; }
            public double PerformanceGap { get; set; }
            public Dictionary<string, double> ImportanceWeights { get; set; }
            public FeedbackType Type { get; set; }
            public Dictionary<string, object> Context { get; set; }
        }

        /// <summary>
        /// Reward weights for multi-objective optimization;
        /// </summary>
        public class RewardWeights;
        {
            public string AgentId { get; set; }
            public DateTime UpdatedAt { get; set; }
            public Dictionary<string, double> ComponentWeights { get; set; } = new();
            public Dictionary<string, double> ObjectiveWeights { get; set; } = new();
            public double ExplorationWeight { get; set; } = 0.3;
            public double ExploitationWeight { get; set; } = 0.7;
            public double RiskWeight { get; set; } = 0.5;
            public Dictionary<string, double> AdaptiveFactors { get; set; } = new();
        }

        /// <summary>
        /// Detailed reward breakdown;
        /// </summary>
        public class RewardBreakdown;
        {
            public string AgentId { get; set; }
            public string EpisodeId { get; set; }
            public int TotalSteps { get; set; }
            public double TotalReward { get; set; }
            public Dictionary<string, double> StepRewards { get; set; }
            public Dictionary<string, double> ComponentTotals { get; set; }
            public Dictionary<string, List<double>> ComponentTrajectories { get; set; }
            public Dictionary<string, double> Correlations { get; set; }
            public Dictionary<string, double> ContributionPercentages { get; set; }
            public List<string> DominantComponents { get; set; }
            public Dictionary<string, object> Analysis { get; set; }
        }

        /// <summary>
        /// Event args for reward calculated event;
        /// </summary>
        public class RewardCalculatedEventArgs : EventArgs;
        {
            public string AgentId { get; set; }
            public string EpisodeId { get; set; }
            public int Step { get; set; }
            public double Reward { get; set; }
            public Dictionary<string, double> Components { get; set; }
            public DateTime CalculatedAt { get; set; }
        }

        /// <summary>
        /// Event args for model updated event;
        /// </summary>
        public class ModelUpdatedEventArgs : EventArgs;
        {
            public string AgentId { get; set; }
            public RewardModel Model { get; set; }
            public double Improvement { get; set; }
            public DateTime UpdatedAt { get; set; }
            public Dictionary<string, object> Metrics { get; set; }
        }

        // Enums;
        public enum RewardType { Extrinsic, Intrinsic, Mixed, Shaped, Sparse, Dense }
        public enum RewardStrategy { Fixed, Adaptive, MultiObjective, Curriculum, Meta }
        public enum RewardNormalizationMethod { None, MinMax, ZScore, RunningStats, Adaptive }
        public enum IntrinsicMotivationType { Curiosity, Surprise, Empowerment, Novelty, Competence }
        public enum ShapingMethod { PotentialBased, Dynamic, Optimal, Heuristic }
        public enum FeedbackType { Human, Synthetic, Automatic, Comparative }

        // Properties;
        public RewardConfiguration Configuration { get; private set; }
        public IReadOnlyList<RewardRecord> RewardHistory => _rewardHistory.ToList().AsReadOnly();
        public RewardStatistics Statistics => CalculateGlobalStatistics();

        // Events;
        public event EventHandler<RewardCalculatedEventArgs> RewardCalculated;
        public event EventHandler<ModelUpdatedEventArgs> ModelUpdated;

        /// <summary>
        /// Constructor;
        /// </summary>
        public RewardSystem(
            ILogger<RewardSystem> logger,
            IMetricsCollector metricsCollector,
            ISettingsManager settingsManager,
            IConfiguration configuration,
            IRewardModelStore modelStore = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _modelStore = modelStore ?? new InMemoryRewardModelStore();

            _agentContexts = new ConcurrentDictionary<string, AgentContext>();
            _rewardComponents = new ConcurrentDictionary<string, IRewardComponent>();
            _rewardHistory = new ConcurrentQueue<RewardRecord>();
            _agentStatistics = new ConcurrentDictionary<string, RewardStatistics>();
            _calculationLock = new SemaphoreSlim(1, 10); // Allow up to 10 concurrent calculations;
            _random = new Random();

            Configuration = LoadConfiguration();

            // Register default reward components;
            RegisterDefaultComponents();

            _logger.LogInformation("RewardSystem initialized with {Strategy} strategy", Configuration.Strategy);
        }

        /// <summary>
        /// Initialize the reward system;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized)
            {
                _logger.LogWarning("RewardSystem is already initialized");
                return;
            }

            try
            {
                _logger.LogInformation("Initializing RewardSystem...");

                // Load reward models from store;
                await LoadModelsAsync(cancellationToken);

                // Initialize statistics;
                await InitializeStatisticsAsync(cancellationToken);

                // Start periodic model updates;
                _updateCts = new CancellationTokenSource();
                _ = Task.Run(() => PeriodicModelUpdateAsync(_updateCts.Token));

                _initialized = true;
                _logger.LogInformation("RewardSystem initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize RewardSystem");
                throw;
            }
        }

        /// <summary>
        /// Calculate reward for a given state-action pair;
        /// </summary>
        public async Task<RewardCalculation> CalculateRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(agentId))
                throw new ArgumentNullException(nameof(agentId));

            if (state == null)
                throw new ArgumentNullException(nameof(state));

            if (action == null)
                throw new ArgumentNullException(nameof(action));

            await _calculationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("Calculating reward for agent {AgentId} at step {Step}",
                    agentId, state.Step);

                // Get or create agent context;
                var context = GetOrCreateAgentContext(agentId);

                // Calculate extrinsic reward components;
                var extrinsicRewards = await CalculateExtrinsicRewardsAsync(
                    agentId, state, action, nextState, isTerminal, cancellationToken);

                // Calculate intrinsic reward if enabled;
                IntrinsicReward intrinsicReward = null;
                if (Configuration.IntrinsicWeight > 0)
                {
                    intrinsicReward = await CalculateIntrinsicRewardAsync(
                        agentId, state, action, cancellationToken);
                }

                // Combine rewards;
                var combinedReward = CombineRewards(
                    extrinsicRewards,
                    intrinsicReward,
                    Configuration);

                // Apply reward shaping if enabled;
                ShapedReward shapedReward = null;
                if (Configuration.EnableRewardShaping && !isTerminal)
                {
                    shapedReward = await ApplyRewardShapingAsync(
                        agentId, combinedReward, state, nextState, cancellationToken);
                    combinedReward = shapedReward.BaseReward;
                    combinedReward.TotalReward = shapedReward.ShapedTotal;
                    combinedReward.IsShaped = true;
                }

                // Normalize reward if enabled;
                if (Configuration.EnableRewardNormalization)
                {
                    combinedReward.TotalReward = NormalizeReward(
                        agentId, combinedReward.TotalReward, context);
                }

                // Clip reward to prevent explosions;
                combinedReward.TotalReward = ClipReward(combinedReward.TotalReward, Configuration);

                // Create reward record;
                var record = CreateRewardRecord(
                    agentId,
                    state.EpisodeId,
                    state.Step,
                    combinedReward,
                    state, action, nextState, isTerminal);

                // Update context;
                UpdateAgentContext(context, record, state, action);

                // Update statistics;
                UpdateStatistics(agentId, record);

                // Raise event;
                RewardCalculated?.Invoke(this, new RewardCalculatedEventArgs;
                {
                    AgentId = agentId,
                    EpisodeId = state.EpisodeId,
                    Step = state.Step,
                    Reward = combinedReward.TotalReward,
                    Components = combinedReward.ComponentRewards,
                    CalculatedAt = DateTime.UtcNow;
                });

                // Log for debugging;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Reward calculated for {AgentId}: Total={Total}, Components={Components}",
                        agentId, combinedReward.TotalReward,
                        string.Join(", ", combinedReward.ComponentRewards.Select(c => $"{c.Key}:{c.Value}")));
                }

                return combinedReward;
            }
            finally
            {
                _calculationLock.Release();
            }
        }

        /// <summary>
        /// Calculate intrinsic reward for exploration;
        /// </summary>
        public async Task<IntrinsicReward> CalculateIntrinsicRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            CancellationToken cancellationToken = default)
        {
            var context = GetOrCreateAgentContext(agentId);

            // Calculate different types of intrinsic motivation;
            var curiosity = await CalculateCuriosityRewardAsync(agentId, state, action, context, cancellationToken);
            var surprise = await CalculateSurpriseRewardAsync(agentId, state, action, context, cancellationToken);
            var empowerment = await CalculateEmpowermentRewardAsync(agentId, state, action, context, cancellationToken);
            var novelty = await CalculateNoveltyRewardAsync(agentId, state, action, context, cancellationToken);

            // Combine intrinsic rewards;
            var totalIntrinsic =
                curiosity * Configuration.ComponentConfigs["curiosity"]?.Weight ?? 0.25 +
                surprise * Configuration.ComponentConfigs["surprise"]?.Weight ?? 0.25 +
                empowerment * Configuration.ComponentConfigs["empowerment"]?.Weight ?? 0.25 +
                novelty * Configuration.ComponentConfigs["novelty"]?.Weight ?? 0.25;

            return new IntrinsicReward;
            {
                AgentId = agentId,
                CuriosityReward = curiosity,
                SurpriseReward = surprise,
                EmpowermentReward = empowerment,
                NoveltyReward = novelty,
                TotalIntrinsic = totalIntrinsic,
                MotivationType = DetermineMotivationType(context),
                Components = new Dictionary<string, double>
                {
                    ["curiosity"] = curiosity,
                    ["surprise"] = surprise,
                    ["empowerment"] = empowerment,
                    ["novelty"] = novelty;
                }
            };
        }

        /// <summary>
        /// Apply reward shaping to improve learning efficiency;
        /// </summary>
        public async Task<ShapedReward> ApplyRewardShapingAsync(
            string agentId,
            RewardCalculation baseReward,
            EnvironmentState state,
            EnvironmentState nextState,
            CancellationToken cancellationToken = default)
        {
            var context = GetOrCreateAgentContext(agentId);

            // Calculate potential-based shaping;
            var currentPotential = await CalculateStatePotentialAsync(agentId, state, context, cancellationToken);
            var nextPotential = await CalculateStatePotentialAsync(agentId, nextState, context, cancellationToken);
            var potentialDifference = nextPotential - currentPotential;

            // Apply shaping bonus;
            var shapingBonus = potentialDifference * Configuration.DiscountFactor;

            // Calculate shaped total;
            var shapedTotal = baseReward.TotalReward + shapingBonus;

            // Apply adaptive shaping based on learning progress;
            if (context.LearningProgress > 0.7)
            {
                // Reduce shaping as agent learns;
                shapingBonus *= (1 - context.LearningProgress);
                shapedTotal = baseReward.TotalReward + shapingBonus;
            }

            return new ShapedReward;
            {
                BaseReward = baseReward,
                ShapingBonus = shapingBonus,
                PotentialDifference = potentialDifference,
                ShapedTotal = shapedTotal,
                Method = DetermineShapingMethod(context),
                IsNormalized = Configuration.EnableRewardNormalization;
            };
        }

        /// <summary>
        /// Update reward model based on feedback;
        /// </summary>
        public async Task UpdateModelAsync(
            string agentId,
            RewardFeedback feedback,
            CancellationToken cancellationToken = default)
        {
            if (feedback == null)
                throw new ArgumentNullException(nameof(feedback));

            await _calculationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Updating reward model for agent {AgentId}", agentId);

                var context = GetOrCreateAgentContext(agentId);

                // Analyze feedback;
                var analysis = AnalyzeFeedback(feedback, context);

                // Update component weights;
                UpdateComponentWeights(context, analysis);

                // Update reward model;
                await UpdateRewardModelAsync(agentId, feedback, analysis, cancellationToken);

                // Update statistics;
                UpdateFeedbackStatistics(agentId, feedback);

                // Raise event;
                ModelUpdated?.Invoke(this, new ModelUpdatedEventArgs;
                {
                    AgentId = agentId,
                    Model = context.RewardModel,
                    Improvement = analysis.Improvement,
                    UpdatedAt = DateTime.UtcNow,
                    Metrics = analysis.Metrics;
                });

                _logger.LogDebug("Reward model updated for agent {AgentId}: Improvement={Improvement}",
                    agentId, analysis.Improvement);
            }
            finally
            {
                _calculationLock.Release();
            }
        }

        /// <summary>
        /// Get reward components for debugging and analysis;
        /// </summary>
        public async Task<RewardBreakdown> GetRewardBreakdownAsync(
            string agentId,
            string episodeId,
            CancellationToken cancellationToken = default)
        {
            if (!_agentContexts.TryGetValue(agentId, out var context))
                throw new KeyNotFoundException($"Agent {agentId} not found");

            // Get episode records;
            var episodeRecords = _rewardHistory;
                .Where(r => r.AgentId == agentId && r.EpisodeId == episodeId)
                .OrderBy(r => r.Step)
                .ToList();

            if (!episodeRecords.Any())
                return new RewardBreakdown;
                {
                    AgentId = agentId,
                    EpisodeId = episodeId,
                    TotalSteps = 0,
                    TotalReward = 0;
                };

            // Calculate breakdown;
            var breakdown = new RewardBreakdown;
            {
                AgentId = agentId,
                EpisodeId = episodeId,
                TotalSteps = episodeRecords.Count,
                TotalReward = episodeRecords.Sum(r => r.Reward),
                StepRewards = episodeRecords.ToDictionary(
                    r => r.Step.ToString(),
                    r => r.Reward),
                ComponentTotals = new Dictionary<string, double>(),
                ComponentTrajectories = new Dictionary<string, List<double>>(),
                Correlations = new Dictionary<string, double>(),
                ContributionPercentages = new Dictionary<string, double>(),
                DominantComponents = new List<string>(),
                Analysis = new Dictionary<string, object>()
            };

            // Calculate component totals;
            foreach (var record in episodeRecords)
            {
                if (record.Components != null)
                {
                    foreach (var component in record.Components)
                    {
                        if (!breakdown.ComponentTotals.ContainsKey(component.Key))
                        {
                            breakdown.ComponentTotals[component.Key] = 0;
                            breakdown.ComponentTrajectories[component.Key] = new List<double>();
                        }

                        breakdown.ComponentTotals[component.Key] += component.Value;
                        breakdown.ComponentTrajectories[component.Key].Add(component.Value);
                    }
                }
            }

            // Calculate contribution percentages;
            var totalComponentSum = breakdown.ComponentTotals.Values.Sum();
            if (totalComponentSum > 0)
            {
                foreach (var component in breakdown.ComponentTotals)
                {
                    breakdown.ContributionPercentages[component.Key] =
                        component.Value / totalComponentSum * 100;
                }

                // Identify dominant components (>20% contribution)
                breakdown.DominantComponents = breakdown.ContributionPercentages;
                    .Where(c => c.Value > 20)
                    .Select(c => c.Key)
                    .ToList();
            }

            // Calculate correlations;
            breakdown.Correlations = await CalculateCorrelationsAsync(
                breakdown.ComponentTrajectories, cancellationToken);

            // Add analysis metrics;
            breakdown.Analysis["mean_reward"] = episodeRecords.Average(r => r.Reward);
            breakdown.Analysis["std_reward"] = CalculateStandardDeviation(
                episodeRecords.Select(r => r.Reward));
            breakdown.Analysis["max_reward"] = episodeRecords.Max(r => r.Reward);
            breakdown.Analysis["min_reward"] = episodeRecords.Min(r => r.Reward);
            breakdown.Analysis["reward_variance"] = CalculateVariance(
                episodeRecords.Select(r => r.Reward));

            return breakdown;
        }

        /// <summary>
        /// Register a custom reward component;
        /// </summary>
        public void RegisterRewardComponent(IRewardComponent component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));

            if (_rewardComponents.TryAdd(component.ComponentId, component))
            {
                _logger.LogInformation("Registered reward component: {ComponentId}", component.ComponentId);

                // Update configuration;
                if (!Configuration.ComponentConfigs.ContainsKey(component.ComponentId))
                {
                    Configuration.ComponentConfigs[component.ComponentId] = new RewardComponentConfig;
                    {
                        Enabled = true,
                        Weight = 1.0,
                        Normalize = true;
                    };
                }
            }
        }

        /// <summary>
        /// Set reward weights dynamically;
        /// </summary>
        public void SetRewardWeights(string agentId, RewardWeights weights)
        {
            if (!_agentContexts.TryGetValue(agentId, out var context))
                throw new KeyNotFoundException($"Agent {agentId} not found");

            context.RewardWeights = weights;

            _logger.LogInformation("Updated reward weights for agent {AgentId}", agentId);
        }

        /// <summary>
        /// Calculate extrinsic rewards from all components;
        /// </summary>
        private async Task<Dictionary<string, double>> CalculateExtrinsicRewardsAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken)
        {
            var rewards = new Dictionary<string, double>();

            foreach (var component in _rewardComponents.Values)
            {
                try
                {
                    var config = Configuration.ComponentConfigs.GetValueOrDefault(component.ComponentId);
                    if (config == null || !config.Enabled)
                        continue;

                    var reward = await component.CalculateRewardAsync(
                        agentId, state, action, nextState, isTerminal, cancellationToken);

                    // Apply component-specific normalization;
                    if (config.Normalize)
                    {
                        reward = NormalizeComponentReward(component.ComponentId, reward, agentId);
                    }

                    // Clip if configured;
                    if (config.ClipValue > 0)
                    {
                        reward = Math.Clamp(reward, -config.ClipValue, config.ClipValue);
                    }

                    // Apply weight;
                    reward *= config.Weight;

                    rewards[component.ComponentId] = reward;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to calculate reward from component {ComponentId}",
                        component.ComponentId);
                    rewards[component.ComponentId] = 0;
                }
            }

            return rewards;
        }

        /// <summary>
        /// Calculate curiosity-based intrinsic reward;
        /// </summary>
        private async Task<double> CalculateCuriosityRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            // Curiosity: reward for visiting novel states;
            var stateNovelty = await CalculateStateNoveltyAsync(agentId, state, context, cancellationToken);
            var actionNovelty = CalculateActionNovelty(agentId, action, context);

            // Combine novelty scores;
            var curiosity = (stateNovelty + actionNovelty) / 2.0;

            // Scale by configuration;
            curiosity *= Configuration.ExplorationBonus;

            return curiosity;
        }

        /// <summary>
        /// Calculate surprise-based intrinsic reward;
        /// </summary>
        private async Task<double> CalculateSurpriseRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            // Surprise: reward for unexpected outcomes;
            var predictionError = await CalculatePredictionErrorAsync(
                agentId, state, action, context, cancellationToken);

            // Higher prediction error = more surprise;
            var surprise = Math.Min(predictionError, 1.0);

            // Apply diminishing returns for repeated surprises;
            surprise *= (1.0 - context.SurpriseHistory.GetValueOrDefault(state.StateHash, 0.0));

            return surprise;
        }

        /// <summary>
        /// Calculate empowerment-based intrinsic reward;
        /// </summary>
        private async Task<double> CalculateEmpowermentRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            // Empowerment: reward for reaching states with high controllability;
            var controllability = await CalculateStateControllabilityAsync(
                agentId, state, context, cancellationToken);

            // Empowerment is higher for states with more possible future states;
            var empowerment = controllability * Configuration.ExplorationBonus;

            return empowerment;
        }

        /// <summary>
        /// Calculate novelty-based intrinsic reward;
        /// </summary>
        private async Task<double> CalculateNoveltyRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            // Novelty: reward for visiting completely new states;
            var isNovel = await IsStateNovelAsync(agentId, state, context, cancellationToken);

            if (isNovel)
            {
                var novelty = Configuration.ExplorationBonus * 2.0; // Bonus for novel states;

                // Store novel state;
                context.NovelStates.Add(state.StateHash);

                return novelty;
            }

            return 0;
        }

        /// <summary>
        /// Combine extrinsic and intrinsic rewards;
        /// </summary>
        private RewardCalculation CombineRewards(
            Dictionary<string, double> extrinsicRewards,
            IntrinsicReward intrinsicReward,
            RewardConfiguration config)
        {
            var totalExtrinsic = extrinsicRewards.Values.Sum();
            var totalIntrinsic = intrinsicReward?.TotalIntrinsic ?? 0;

            // Apply weights;
            var weightedExtrinsic = totalExtrinsic * config.ExtrinsicWeight;
            var weightedIntrinsic = totalIntrinsic * config.IntrinsicWeight;

            var totalReward = weightedExtrinsic + weightedIntrinsic;

            // Combine component dictionaries;
            var allComponents = new Dictionary<string, double>(extrinsicRewards);
            if (intrinsicReward?.Components != null)
            {
                foreach (var component in intrinsicReward.Components)
                {
                    allComponents[$"intrinsic_{component.Key}"] = component.Value * config.IntrinsicWeight;
                }
            }

            return new RewardCalculation;
            {
                TotalReward = totalReward,
                ComponentRewards = allComponents,
                RewardType = intrinsicReward != null ? RewardType.Mixed : RewardType.Extrinsic,
                CalculatedAt = DateTime.UtcNow,
                Confidence = CalculateRewardConfidence(extrinsicRewards, intrinsicReward),
                Metadata = new Dictionary<string, object>
                {
                    ["extrinsic_total"] = totalExtrinsic,
                    ["intrinsic_total"] = totalIntrinsic,
                    ["extrinsic_weighted"] = weightedExtrinsic,
                    ["intrinsic_weighted"] = weightedIntrinsic;
                }
            };
        }

        /// <summary>
        /// Normalize reward based on agent's history;
        /// </summary>
        private double NormalizeReward(string agentId, double reward, AgentContext context)
        {
            switch (Configuration.NormalizationMethod)
            {
                case RewardNormalizationMethod.RunningStats:
                    return NormalizeWithRunningStats(reward, context.RunningStats);

                case RewardNormalizationMethod.ZScore:
                    return NormalizeWithZScore(reward, context.RewardHistory);

                case RewardNormalizationMethod.MinMax:
                    return NormalizeWithMinMax(reward, context.RewardHistory);

                case RewardNormalizationMethod.Adaptive:
                    return NormalizeAdaptive(reward, context);

                default:
                    return reward;
            }
        }

        /// <summary>
        /// Clip reward to prevent explosion;
        /// </summary>
        private double ClipReward(double reward, RewardConfiguration config)
        {
            // Dynamic clipping based on reward statistics;
            var clipValue = 10.0; // Default;

            if (config.ComponentConfigs.TryGetValue("global_clip", out var clipConfig))
            {
                clipValue = clipConfig.ClipValue;
            }

            return Math.Clamp(reward, -clipValue, clipValue);
        }

        /// <summary>
        /// Create reward record for history;
        /// </summary>
        private RewardRecord CreateRewardRecord(
            string agentId,
            string episodeId,
            int step,
            RewardCalculation calculation,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal)
        {
            var record = new RewardRecord;
            {
                RecordId = Guid.NewGuid().ToString(),
                AgentId = agentId,
                EpisodeId = episodeId,
                Step = step,
                Reward = calculation.TotalReward,
                Components = calculation.ComponentRewards,
                Type = calculation.RewardType,
                Timestamp = DateTime.UtcNow,
                State = state,
                Action = action,
                NextState = nextState,
                IsTerminal = isTerminal,
                Metadata = calculation.Metadata;
            };

            // Add to history;
            _rewardHistory.Enqueue(record);
            while (_rewardHistory.Count > _maxHistorySize)
                _rewardHistory.TryDequeue(out _);

            return record;
        }

        /// <summary>
        /// Update agent context with new reward;
        /// </summary>
        private void UpdateAgentContext(
            AgentContext context,
            RewardRecord record,
            EnvironmentState state,
            AgentAction action)
        {
            // Update running statistics;
            context.RunningStats.Update(record.Reward);

            // Update reward history;
            context.RewardHistory.Add(record.Reward);
            if (context.RewardHistory.Count > 1000)
                context.RewardHistory.RemoveAt(0);

            // Update state visit counts;
            var stateHash = state.StateHash;
            context.StateVisits[stateHash] = context.StateVisits.GetValueOrDefault(stateHash, 0) + 1;

            // Update action statistics;
            var actionKey = $"{stateHash}_{action.ActionId}";
            context.ActionRewards[actionKey] = record.Reward;

            // Update surprise history;
            if (record.Components.ContainsKey("surprise"))
            {
                context.SurpriseHistory[stateHash] = record.Components["surprise"];
            }
        }

        /// <summary>
        /// Update reward statistics;
        /// </summary>
        private void UpdateStatistics(string agentId, RewardRecord record)
        {
            if (!_agentStatistics.TryGetValue(agentId, out var stats))
            {
                stats = new RewardStatistics;
                {
                    AgentId = agentId,
                    WindowStart = DateTime.UtcNow,
                    WindowEnd = DateTime.UtcNow;
                };
                _agentStatistics[agentId] = stats;
            }

            stats.TotalSteps++;
            stats.WindowEnd = DateTime.UtcNow;

            // Update component statistics;
            foreach (var component in record.Components)
            {
                if (!stats.ComponentMeans.ContainsKey(component.Key))
                {
                    stats.ComponentMeans[component.Key] = 0;
                    stats.ComponentStdDevs[component.Key] = 0;
                }
            }
        }

        /// <summary>
        /// Calculate global statistics;
        /// </summary>
        private RewardStatistics CalculateGlobalStatistics()
        {
            var allRecords = _rewardHistory.ToList();
            if (!allRecords.Any())
                return new RewardStatistics();

            var stats = new RewardStatistics;
            {
                WindowStart = allRecords.Min(r => r.Timestamp),
                WindowEnd = allRecords.Max(r => r.Timestamp),
                TotalSteps = allRecords.Count,
                TotalEpisodes = allRecords.Select(r => r.EpisodeId).Distinct().Count(),
                MeanReward = allRecords.Average(r => r.Reward),
                StdDevReward = CalculateStandardDeviation(allRecords.Select(r => r.Reward)),
                MinReward = allRecords.Min(r => r.Reward),
                MaxReward = allRecords.Max(r => r.Reward),
                TotalExtrinsic = allRecords.Where(r => r.Type == RewardType.Extrinsic).Sum(r => r.Reward),
                TotalIntrinsic = allRecords.Where(r => r.Type == RewardType.Intrinsic).Sum(r => r.Reward)
            };

            // Calculate Sharpe ratio (risk-adjusted return)
            if (stats.StdDevReward > 0)
                stats.SharpeRatio = stats.MeanReward / stats.StdDevReward;

            return stats;
        }

        /// <summary>
        /// Load configuration from settings;
        /// </summary>
        private RewardConfiguration LoadConfiguration()
        {
            try
            {
                var config = _settingsManager.GetSection<RewardConfiguration>("RewardSystem");
                if (config != null)
                    return config;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load RewardSystem configuration, using defaults");
            }

            // Default configuration;
            return new RewardConfiguration;
            {
                Strategy = RewardStrategy.Adaptive,
                ExtrinsicWeight = 0.7,
                IntrinsicWeight = 0.3,
                EnableRewardShaping = true,
                EnableRewardNormalization = true,
                DiscountFactor = 0.99,
                LearningRate = 0.01,
                ExplorationBonus = 0.1,
                ExploitationBonus = 0.05,
                NormalizationMethod = RewardNormalizationMethod.RunningStats,
                ComponentWeights = new Dictionary<string, double>
                {
                    ["goal"] = 1.0,
                    ["time"] = -0.01,
                    ["energy"] = -0.001,
                    ["damage"] = -0.1;
                },
                HistoryBufferSize = 10000,
                EnableMultiObjective = true;
            };
        }

        /// <summary>
        /// Register default reward components;
        /// </summary>
        private void RegisterDefaultComponents()
        {
            RegisterRewardComponent(new GoalRewardComponent());
            RegisterRewardComponent(new TimeRewardComponent());
            RegisterRewardComponent(new EnergyRewardComponent());
            RegisterRewardComponent(new DamageRewardComponent());
            RegisterRewardComponent(new SafetyRewardComponent());
            RegisterRewardComponent(new EfficiencyRewardComponent());
        }

        /// <summary>
        /// Get or create agent context;
        /// </summary>
        private AgentContext GetOrCreateAgentContext(string agentId)
        {
            return _agentContexts.GetOrAdd(agentId, id => new AgentContext;
            {
                AgentId = id,
                CreatedAt = DateTime.UtcNow,
                RewardModel = new RewardModel(),
                RewardWeights = new RewardWeights;
                {
                    AgentId = id,
                    UpdatedAt = DateTime.UtcNow;
                },
                RunningStats = new RunningStatistics(),
                RewardHistory = new List<double>(),
                StateVisits = new Dictionary<string, int>(),
                ActionRewards = new Dictionary<string, double>(),
                NovelStates = new HashSet<string>(),
                SurpriseHistory = new Dictionary<string, double>()
            });
        }

        /// <summary>
        /// Calculate state novelty;
        /// </summary>
        private async Task<double> CalculateStateNoveltyAsync(
            string agentId,
            EnvironmentState state,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            var visits = context.StateVisits.GetValueOrDefault(state.StateHash, 0);

            // Novelty decreases with more visits;
            var novelty = 1.0 / (1.0 + Math.Sqrt(visits));

            return await Task.FromResult(novelty);
        }

        /// <summary>
        /// Calculate action novelty;
        /// </summary>
        private double CalculateActionNovelty(string agentId, AgentAction action, AgentContext context)
        {
            // Simple novelty based on action frequency;
            var actionKey = action.ActionId;
            var actionCount = context.ActionRewards.Count(kv => kv.Key.Contains(actionKey));

            return 1.0 / (1.0 + Math.Sqrt(actionCount));
        }

        /// <summary>
        /// Calculate prediction error;
        /// </summary>
        private async Task<double> CalculatePredictionErrorAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            // This would use a prediction model in a real implementation;
            // For now, return random error for demonstration;
            return await Task.FromResult(_random.NextDouble() * 0.5);
        }

        /// <summary>
        /// Calculate state controllability;
        /// </summary>
        private async Task<double> CalculateStateControllabilityAsync(
            string agentId,
            EnvironmentState state,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            // States with more possible actions are more controllable;
            var possibleActions = state.AvailableActions?.Count ?? 1;
            var controllability = Math.Min(possibleActions / 10.0, 1.0);

            return await Task.FromResult(controllability);
        }

        /// <summary>
        /// Check if state is novel;
        /// </summary>
        private async Task<bool> IsStateNovelAsync(
            string agentId,
            EnvironmentState state,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            return await Task.FromResult(!context.NovelStates.Contains(state.StateHash));
        }

        /// <summary>
        /// Calculate state potential for shaping;
        /// </summary>
        private async Task<double> CalculateStatePotentialAsync(
            string agentId,
            EnvironmentState state,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            // Potential function based on distance to goal;
            if (state.Features.TryGetValue("distance_to_goal", out var distanceObj) &&
                distanceObj is double distance)
            {
                return -distance; // Negative potential for farther states;
            }

            return await Task.FromResult(0.0);
        }

        /// <summary>
        /// Determine intrinsic motivation type;
        /// </summary>
        private IntrinsicMotivationType DetermineMotivationType(AgentContext context)
        {
            // Analyze recent rewards to determine dominant motivation;
            var recentRewards = context.RewardHistory.TakeLast(100).ToList();
            if (!recentRewards.Any())
                return IntrinsicMotivationType.Curiosity;

            // Simple heuristic based on variance;
            var variance = CalculateVariance(recentRewards);

            if (variance > 0.5)
                return IntrinsicMotivationType.Surprise;
            else if (context.NovelStates.Count > 10)
                return IntrinsicMotivationType.Novelty;
            else;
                return IntrinsicMotivationType.Curiosity;
        }

        /// <summary>
        /// Determine shaping method;
        /// </summary>
        private ShapingMethod DetermineShapingMethod(AgentContext context)
        {
            if (context.LearningProgress < 0.3)
                return ShapingMethod.Heuristic; // Early learning;
            else if (context.LearningProgress < 0.7)
                return ShapingMethod.PotentialBased; // Mid learning;
            else;
                return ShapingMethod.Dynamic; // Advanced learning;
        }

        /// <summary>
        /// Normalize with running statistics;
        /// </summary>
        private double NormalizeWithRunningStats(double reward, RunningStatistics stats)
        {
            if (stats.Count == 0 || stats.StdDev == 0)
                return reward;

            return (reward - stats.Mean) / stats.StdDev;
        }

        /// <summary>
        /// Normalize with Z-score;
        /// </summary>
        private double NormalizeWithZScore(double reward, List<double> history)
        {
            if (history.Count < 2)
                return reward;

            var mean = history.Average();
            var stdDev = CalculateStandardDeviation(history);

            if (stdDev == 0)
                return reward - mean;

            return (reward - mean) / stdDev;
        }

        /// <summary>
        /// Normalize with min-max;
        /// </summary>
        private double NormalizeWithMinMax(double reward, List<double> history)
        {
            if (history.Count < 2)
                return reward;

            var min = history.Min();
            var max = history.Max();

            if (max == min)
                return 0;

            return (reward - min) / (max - min);
        }

        /// <summary>
        /// Adaptive normalization;
        /// </summary>
        private double NormalizeAdaptive(double reward, AgentContext context)
        {
            // Combine multiple normalization methods;
            var zScore = NormalizeWithZScore(reward, context.RewardHistory);
            var running = NormalizeWithRunningStats(reward, context.RunningStats);

            // Weight based on history size;
            var weight = Math.Min(context.RewardHistory.Count / 100.0, 1.0);

            return weight * running + (1 - weight) * zScore;
        }

        /// <summary>
        /// Normalize component reward;
        /// </summary>
        private double NormalizeComponentReward(string componentId, double reward, string agentId)
        {
            if (!_agentContexts.TryGetValue(agentId, out var context))
                return reward;

            // Get component-specific normalization stats;
            if (!context.ComponentStats.TryGetValue(componentId, out var stats))
            {
                stats = new RunningStatistics();
                context.ComponentStats[componentId] = stats;
            }

            stats.Update(reward);

            if (stats.Count < 10 || stats.StdDev == 0)
                return reward;

            // Normalize to unit variance;
            return (reward - stats.Mean) / stats.StdDev;
        }

        /// <summary>
        /// Calculate reward confidence;
        /// </summary>
        private double CalculateRewardConfidence(
            Dictionary<string, double> extrinsicRewards,
            IntrinsicReward intrinsicReward)
        {
            // Confidence based on:
            // 1. Number of components;
            // 2. Variance of component rewards;
            // 3. Agreement between components;

            var componentCount = extrinsicRewards.Count;
            if (componentCount == 0)
                return 0.0;

            var values = extrinsicRewards.Values.ToList();
            var mean = values.Average();
            var variance = CalculateVariance(values);

            // Higher variance = lower confidence;
            var varianceConfidence = 1.0 / (1.0 + variance);

            // More components = higher confidence (up to a point)
            var countConfidence = Math.Min(componentCount / 10.0, 1.0);

            return (varianceConfidence + countConfidence) / 2.0;
        }

        /// <summary>
        /// Calculate standard deviation;
        /// </summary>
        private double CalculateStandardDeviation(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count < 2)
                return 0.0;

            var mean = list.Average();
            var sum = list.Sum(v => Math.Pow(v - mean, 2));
            return Math.Sqrt(sum / (list.Count - 1));
        }

        /// <summary>
        /// Calculate variance;
        /// </summary>
        private double CalculateVariance(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count < 2)
                return 0.0;

            var mean = list.Average();
            var sum = list.Sum(v => Math.Pow(v - mean, 2));
            return sum / (list.Count - 1);
        }

        /// <summary>
        /// Load models from store;
        /// </summary>
        private async Task LoadModelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var models = await _modelStore.LoadAllAsync(cancellationToken);

                foreach (var model in models)
                {
                    var context = GetOrCreateAgentContext(model.AgentId);
                    context.RewardModel = model;
                }

                _logger.LogInformation("Loaded {Count} reward models", models.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load reward models");
            }
        }

        /// <summary>
        /// Initialize statistics;
        /// </summary>
        private async Task InitializeStatisticsAsync(CancellationToken cancellationToken)
        {
            // Initialize from historical data if available;
            var historicalStats = await _metricsCollector.GetHistoricalMetricsAsync(
                "reward_system",
                DateTime.UtcNow.AddDays(-7),
                DateTime.UtcNow,
                cancellationToken);

            if (historicalStats.Any())
            {
                _logger.LogDebug("Initialized statistics from {Count} historical records",
                    historicalStats.Count);
            }
        }

        /// <summary>
        /// Periodic model update;
        /// </summary>
        private async Task PeriodicModelUpdateAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_modelUpdateInterval, cancellationToken);

                    if (DateTime.UtcNow - _lastModelUpdate > _modelUpdateInterval)
                    {
                        await UpdateAllModelsAsync(cancellationToken);
                        _lastModelUpdate = DateTime.UtcNow;
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Periodic model update failed");
                }
            }
        }

        /// <summary>
        /// Update all agent models;
        /// </summary>
        private async Task UpdateAllModelsAsync(CancellationToken cancellationToken)
        {
            foreach (var agentId in _agentContexts.Keys)
            {
                try
                {
                    var recentRewards = _rewardHistory;
                        .Where(r => r.AgentId == agentId &&
                                   r.Timestamp > DateTime.UtcNow.AddHours(-1))
                        .ToList();

                    if (recentRewards.Any())
                    {
                        var feedback = new RewardFeedback;
                        {
                            AgentId = agentId,
                            EpisodeId = "periodic_update",
                            EpisodeRecords = recentRewards,
                            EpisodeReturn = recentRewards.Sum(r => r.Reward),
                            Type = FeedbackType.Automatic,
                            Context = new Dictionary<string, object>
                            {
                                ["update_type"] = "periodic",
                                ["window_start"] = recentRewards.Min(r => r.Timestamp),
                                ["window_end"] = recentRewards.Max(r => r.Timestamp)
                            }
                        };

                        await UpdateModelAsync(agentId, feedback, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update model for agent {AgentId}", agentId);
                }
            }
        }

        /// <summary>
        /// Analyze feedback;
        /// </summary>
        private FeedbackAnalysis AnalyzeFeedback(RewardFeedback feedback, AgentContext context)
        {
            var analysis = new FeedbackAnalysis;
            {
                AgentId = feedback.AgentId,
                Timestamp = DateTime.UtcNow,
                EpisodeReturn = feedback.EpisodeReturn,
                ExpectedReturn = feedback.ExpectedReturn,
                PerformanceGap = feedback.PerformanceGap,
                Improvement = 0,
                Metrics = new Dictionary<string, object>()
            };

            // Analyze component contributions;
            if (feedback.EpisodeRecords != null)
            {
                var componentContributions = new Dictionary<string, double>();

                foreach (var record in feedback.EpisodeRecords)
                {
                    if (record.Components != null)
                    {
                        foreach (var component in record.Components)
                        {
                            if (!componentContributions.ContainsKey(component.Key))
                                componentContributions[component.Key] = 0;

                            componentContributions[component.Key] += component.Value;
                        }
                    }
                }

                analysis.Metrics["component_contributions"] = componentContributions;

                // Identify underperforming components;
                var total = componentContributions.Values.Sum();
                if (total != 0)
                {
                    var underperforming = componentContributions;
                        .Where(c => Math.Abs(c.Value / total) < 0.05) // Less than 5% contribution;
                        .Select(c => c.Key)
                        .ToList();

                    analysis.Metrics["underperforming_components"] = underperforming;
                }
            }

            return analysis;
        }

        /// <summary>
        /// Update component weights;
        /// </summary>
        private void UpdateComponentWeights(AgentContext context, FeedbackAnalysis analysis)
        {
            var weights = context.RewardWeights;

            // Update based on performance gap;
            if (analysis.PerformanceGap > 0.1) // Significant gap;
            {
                // Increase exploration;
                weights.ExplorationWeight = Math.Min(weights.ExplorationWeight * 1.1, 0.8);
                weights.ExploitationWeight = 1.0 - weights.ExplorationWeight;
            }
            else if (analysis.PerformanceGap < -0.1) // Better than expected;
            {
                // Increase exploitation;
                weights.ExploitationWeight = Math.Min(weights.ExploitationWeight * 1.1, 0.8);
                weights.ExplorationWeight = 1.0 - weights.ExploitationWeight;
            }

            weights.UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Update reward model;
        /// </summary>
        private async Task UpdateRewardModelAsync(
            string agentId,
            RewardFeedback feedback,
            FeedbackAnalysis analysis,
            CancellationToken cancellationToken)
        {
            var context = GetOrCreateAgentContext(agentId);
            var model = context.RewardModel;

            // Update model parameters based on feedback;
            model.Version++;
            model.LastUpdated = DateTime.UtcNow;
            model.TrainingEpisodes++;

            // Calculate improvement;
            if (feedback.EpisodeRecords != null && feedback.EpisodeRecords.Any())
            {
                var recentRewards = feedback.EpisodeRecords.Select(r => r.Reward).ToList();
                var oldMean = context.RunningStats.Mean;
                var newMean = recentRewards.Average();

                analysis.Improvement = newMean - oldMean;
                model.PerformanceHistory.Add(newMean);

                // Update learning progress;
                context.LearningProgress = CalculateLearningProgress(model.PerformanceHistory);
            }

            // Save updated model;
            await _modelStore.SaveAsync(model, cancellationToken);
        }

        /// <summary>
        /// Update feedback statistics;
        /// </summary>
        private void UpdateFeedbackStatistics(string agentId, RewardFeedback feedback)
        {
            // Update feedback tracking;
            if (!_agentStatistics.TryGetValue(agentId, out var stats))
                return;

            stats.LearningProgress = CalculateLearningProgressFromFeedback(feedback);
        }

        /// <summary>
        /// Calculate correlations between components;
        /// </summary>
        private async Task<Dictionary<string, double>> CalculateCorrelationsAsync(
            Dictionary<string, List<double>> trajectories,
            CancellationToken cancellationToken)
        {
            var correlations = new Dictionary<string, double>();
            var keys = trajectories.Keys.ToList();

            for (int i = 0; i < keys.Count; i++)
            {
                for (int j = i + 1; j < keys.Count; j++)
                {
                    var corr = CalculatePearsonCorrelation(
                        trajectories[keys[i]],
                        trajectories[keys[j]]);

                    correlations[$"{keys[i]}_{keys[j]}"] = corr;
                }
            }

            return await Task.FromResult(correlations);
        }

        /// <summary>
        /// Calculate Pearson correlation;
        /// </summary>
        private double CalculatePearsonCorrelation(List<double> x, List<double> y)
        {
            if (x.Count != y.Count || x.Count < 2)
                return 0.0;

            var n = x.Count;
            var sumX = x.Sum();
            var sumY = y.Sum();
            var sumXY = x.Zip(y, (a, b) => a * b).Sum();
            var sumX2 = x.Sum(v => v * v);
            var sumY2 = y.Sum(v => v * v);

            var numerator = n * sumXY - sumX * sumY;
            var denominator = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));

            if (denominator == 0)
                return 0.0;

            return numerator / denominator;
        }

        /// <summary>
        /// Calculate learning progress;
        /// </summary>
        private double CalculateLearningProgress(List<double> performanceHistory)
        {
            if (performanceHistory.Count < 10)
                return 0.0;

            var recent = performanceHistory.TakeLast(20).ToList();
            var early = performanceHistory.Take(20).ToList();

            if (recent.Count < 10 || early.Count < 10)
                return 0.0;

            var recentMean = recent.Average();
            var earlyMean = early.Average();

            if (earlyMean == 0)
                return 1.0;

            return (recentMean - earlyMean) / Math.Abs(earlyMean);
        }

        /// <summary>
        /// Calculate learning progress from feedback;
        /// </summary>
        private double CalculateLearningProgressFromFeedback(RewardFeedback feedback)
        {
            if (feedback.EpisodeReturn == 0 || feedback.ExpectedReturn == 0)
                return 0.0;

            return (feedback.EpisodeReturn - feedback.ExpectedReturn) / Math.Abs(feedback.ExpectedReturn);
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _calculationLock?.Dispose();

            // Save models before disposal;
            try
            {
                Task.Run(async () =>
                {
                    foreach (var context in _agentContexts.Values)
                    {
                        await _modelStore.SaveAsync(context.RewardModel, CancellationToken.None);
                    }
                }).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving models during disposal");
            }

            GC.SuppressFinalize(this);
        }

        // Supporting classes;
        private class AgentContext;
        {
            public string AgentId { get; set; }
            public DateTime CreatedAt { get; set; }
            public RewardModel RewardModel { get; set; }
            public RewardWeights RewardWeights { get; set; }
            public RunningStatistics RunningStats { get; set; } = new();
            public List<double> RewardHistory { get; set; } = new();
            public Dictionary<string, int> StateVisits { get; set; } = new();
            public Dictionary<string, double> ActionRewards { get; set; } = new();
            public HashSet<string> NovelStates { get; set; } = new();
            public Dictionary<string, double> SurpriseHistory { get; set; } = new();
            public Dictionary<string, RunningStatistics> ComponentStats { get; set; } = new();
            public double LearningProgress { get; set; }
        }

        private class RunningStatistics;
        {
            public int Count { get; private set; }
            public double Mean { get; private set; }
            public double StdDev { get; private set; }
            private double _m2; // For Welford's algorithm;

            public void Update(double value)
            {
                Count++;
                var delta = value - Mean;
                Mean += delta / Count;
                var delta2 = value - Mean;
                _m2 += delta * delta2;

                if (Count > 1)
                    StdDev = Math.Sqrt(_m2 / (Count - 1));
            }
        }

        private class FeedbackAnalysis;
        {
            public string AgentId { get; set; }
            public DateTime Timestamp { get; set; }
            public double EpisodeReturn { get; set; }
            public double ExpectedReturn { get; set; }
            public double PerformanceGap { get; set; }
            public double Improvement { get; set; }
            public Dictionary<string, object> Metrics { get; set; }
        }
    }

    /// <summary>
    /// Interface for reward components;
    /// </summary>
    public interface IRewardComponent;
    {
        string ComponentId { get; }
        Task<double> CalculateRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken);

        Dictionary<string, object> GetParameters();
        void UpdateParameters(Dictionary<string, object> parameters);
    }

    /// <summary>
    /// Environment state representation;
    /// </summary>
    public class EnvironmentState;
    {
        public string StateId { get; set; }
        public string EpisodeId { get; set; }
        public int Step { get; set; }
        public string StateHash { get; set; }
        public Dictionary<string, object> Features { get; set; } = new();
        public List<string> AvailableActions { get; set; } = new();
        public bool IsTerminal { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Agent action representation;
    /// </summary>
    public class AgentAction;
    {
        public string ActionId { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public double Confidence { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Reward model for storage;
    /// </summary>
    public class RewardModel;
    {
        public string ModelId { get; set; }
        public string AgentId { get; set; }
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public int TrainingEpisodes { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public List<double> PerformanceHistory { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Reward model store interface;
    /// </summary>
    public interface IRewardModelStore;
    {
        Task<RewardModel> LoadAsync(string agentId, CancellationToken cancellationToken);
        Task<List<RewardModel>> LoadAllAsync(CancellationToken cancellationToken);
        Task SaveAsync(RewardModel model, CancellationToken cancellationToken);
        Task DeleteAsync(string agentId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// In-memory reward model store for testing;
    /// </summary>
    public class InMemoryRewardModelStore : IRewardModelStore;
    {
        private readonly ConcurrentDictionary<string, RewardModel> _models = new();

        public Task<RewardModel> LoadAsync(string agentId, CancellationToken cancellationToken)
        {
            _models.TryGetValue(agentId, out var model);
            return Task.FromResult(model);
        }

        public Task<List<RewardModel>> LoadAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_models.Values.ToList());
        }

        public Task SaveAsync(RewardModel model, CancellationToken cancellationToken)
        {
            _models[model.AgentId] = model;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string agentId, CancellationToken cancellationToken)
        {
            _models.TryRemove(agentId, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Default reward components;
    /// </summary>
    public class GoalRewardComponent : IRewardComponent;
    {
        public string ComponentId => "goal";

        public async Task<double> CalculateRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken)
        {
            // Reward for reaching goals;
            if (isTerminal && nextState.Features.TryGetValue("goal_reached", out var reached) &&
                reached is bool reachedBool && reachedBool)
            {
                return 100.0;
            }

            // Progress toward goal;
            if (state.Features.TryGetValue("distance_to_goal", out var distObj) &&
                nextState.Features.TryGetValue("distance_to_goal", out var nextDistObj) &&
                distObj is double distance && nextDistObj is double nextDistance)
            {
                var improvement = distance - nextDistance;
                return improvement * 10.0; // Reward for getting closer;
            }

            return await Task.FromResult(0.0);
        }

        public Dictionary<string, object> GetParameters()
        {
            return new Dictionary<string, object>
            {
                ["goal_reward"] = 100.0,
                ["progress_multiplier"] = 10.0;
            };
        }

        public void UpdateParameters(Dictionary<string, object> parameters)
        {
            // Update component parameters;
        }
    }

    public class TimeRewardComponent : IRewardComponent;
    {
        public string ComponentId => "time";

        public async Task<double> CalculateRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken)
        {
            // Small negative reward for each step (encourage efficiency)
            return await Task.FromResult(-0.01);
        }

        public Dictionary<string, object> GetParameters() => new() { ["step_penalty"] = -0.01 };
        public void UpdateParameters(Dictionary<string, object> parameters) { }
    }

    public class EnergyRewardComponent : IRewardComponent;
    {
        public string ComponentId => "energy";

        public async Task<double> CalculateRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken)
        {
            // Penalize energy consumption;
            if (action.Parameters.TryGetValue("energy_cost", out var costObj) &&
                costObj is double cost)
            {
                return -cost * 0.001;
            }

            return await Task.FromResult(0.0);
        }

        public Dictionary<string, object> GetParameters() => new() { ["energy_multiplier"] = -0.001 };
        public void UpdateParameters(Dictionary<string, object> parameters) { }
    }

    public class DamageRewardComponent : IRewardComponent;
    {
        public string ComponentId => "damage";

        public async Task<double> CalculateRewardAsync(
            string agentId,
            EnvironmentState state,
            AgentAction action,
            EnvironmentState nextState,
            bool isTerminal,
            CancellationToken cancellationToken)
        {
            // Penalize damage taken;
            if (state.Features.TryGetValue("health", out var healthObj) &&
                nextState.Features.TryGetValue("health", out var nextHealthObj) &&
                healthObj is double health && nextHealthObj is double nextHealth)
            {
                var damage = health - nextHealth;
                if (damage > 0)
                    return -damage * 0.1;
            }

            return await Task.FromResult(0.0);
        }

        public Dictionary<string, object> GetParameters() => new() { ["damage_multiplier"] = -0.1 };
        public void UpdateParameters(Dictionary<string, object> parameters) { }
    }

    /// <summary>
    /// Extension methods for RewardSystem;
    /// </summary>
    public static class RewardSystemExtensions;
    {
        /// <summary>
        /// Batch reward calculation;
        /// </summary>
        public static async Task<List<RewardCalculation>> CalculateBatchRewardsAsync(
            this IRewardSystem rewardSystem,
            string agentId,
            List<(EnvironmentState state, AgentAction action, EnvironmentState nextState, bool isTerminal)> transitions,
            CancellationToken cancellationToken = default)
        {
            var results = new List<RewardCalculation>();

            foreach (var transition in transitions)
            {
                try
                {
                    var reward = await rewardSystem.CalculateRewardAsync(
                        agentId,
                        transition.state,
                        transition.action,
                        transition.nextState,
                        transition.isTerminal,
                        cancellationToken);

                    results.Add(reward);
                }
                catch (Exception ex)
                {
                    // Log and continue;
                    Console.WriteLine($"Batch reward calculation failed: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Get reward distribution statistics;
        /// </summary>
        public static Dictionary<string, object> GetDistributionStats(
            this IRewardSystem rewardSystem,
            string agentId = null,
            DateTime? startDate = null,
            DateTime? endDate = null)
        {
            var stats = new Dictionary<string, object>();

            // Implementation would analyze reward distribution;
            // from rewardSystem.RewardHistory;

            return stats;
        }

        /// <summary>
        /// Export reward data for analysis;
        /// </summary>
        public static string ExportRewardData(
            this IRewardSystem rewardSystem,
            string format = "json",
            DateTime? startDate = null,
            DateTime? endDate = null)
        {
            // Export reward history in specified format;
            return JsonSerializer.Serialize(rewardSystem.RewardHistory);
        }

        /// <summary>
        /// Reset reward system for a specific agent;
        /// </summary>
        public static void ResetAgent(this IRewardSystem rewardSystem, string agentId)
        {
            // Clear agent-specific data;
            if (rewardSystem is RewardSystem rs)
            {
                // Implementation would clear agent context;
            }
        }
    }
}
