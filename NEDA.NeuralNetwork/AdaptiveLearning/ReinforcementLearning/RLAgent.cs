using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NEDA.Core.Common;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.NeuralNetwork.AdaptiveLearning.PerformanceAdaptation;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.NeuralNetwork.PatternRecognition;

namespace NEDA.NeuralNetwork.AdaptiveLearning.ReinforcementLearning;
{
    /// <summary>
    /// Reinforcement Learning Agent configuration options;
    /// </summary>
    public class RLAgentOptions;
    {
        public const string SectionName = "RLAgent";

        /// <summary>
        /// Gets or sets the learning rate (alpha)
        /// </summary>
        public double LearningRate { get; set; } = 0.01;

        /// <summary>
        /// Gets or sets the discount factor (gamma)
        /// </summary>
        public double DiscountFactor { get; set; } = 0.95;

        /// <summary>
        /// Gets or sets the exploration rate (epsilon)
        /// </summary>
        public double ExplorationRate { get; set; } = 0.1;

        /// <summary>
        /// Gets or sets the exploration decay rate;
        /// </summary>
        public double ExplorationDecay { get; set; } = 0.995;

        /// <summary>
        /// Gets or sets the minimum exploration rate;
        /// </summary>
        public double MinExplorationRate { get; set; } = 0.01;

        /// <summary>
        /// Gets or sets the batch size for experience replay;
        /// </summary>
        public int BatchSize { get; set; } = 32;

        /// <summary>
        /// Gets or sets the capacity of experience replay buffer;
        /// </summary>
        public int ReplayBufferSize { get; set; } = 10000;

        /// <summary>
        /// Gets or sets the target network update frequency;
        /// </summary>
        public int TargetUpdateFrequency { get; set; } = 100;

        /// <summary>
        /// Gets or sets the maximum steps per episode;
        /// </summary>
        public int MaxStepsPerEpisode { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the number of training episodes;
        /// </summary>
        public int TrainingEpisodes { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the reward scaling factor;
        /// </summary>
        public double RewardScale { get; set; } = 1.0;

        /// <summary>
        /// Gets or sets whether to use double DQN;
        /// </summary>
        public bool UseDoubleDQN { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to use prioritized experience replay;
        /// </summary>
        public bool UsePrioritizedReplay { get; set; } = true;

        /// <summary>
        /// Gets or sets the policy type to use;
        /// </summary>
        public PolicyType Policy { get; set; } = PolicyType.EpsilonGreedy;

        /// <summary>
        /// Gets or sets the neural network architecture configuration;
        /// </summary>
        public NeuralNetworkConfig NeuralNetworkConfig { get; set; } = new();

        /// <summary>
        /// Gets or sets the agent mode (Training, Evaluation, Inference)
        /// </summary>
        public AgentMode Mode { get; set; } = AgentMode.Training;

        /// <summary>
        /// Gets or sets the save interval in episodes;
        /// </summary>
        public int SaveInterval { get; set; } = 100;
    }

    /// <summary>
    /// Neural network configuration for RL agent;
    /// </summary>
    public class NeuralNetworkConfig;
    {
        public int[] HiddenLayers { get; set; } = { 128, 128 };
        public string ActivationFunction { get; set; } = "ReLU";
        public string OutputActivation { get; set; } = "Linear";
        public double WeightDecay { get; set; } = 0.0001;
        public double DropoutRate { get; set; } = 0.2;
        public bool UseBatchNormalization { get; set; } = true;
    }

    /// <summary>
    /// Policy types for RL agent;
    /// </summary>
    public enum PolicyType;
    {
        EpsilonGreedy,
        Boltzmann,
        Gaussian,
        ThompsonSampling,
        UpperConfidenceBound;
    }

    /// <summary>
    /// Agent operation modes;
    /// </summary>
    public enum AgentMode;
    {
        Training,
        Evaluation,
        Inference;
    }

    /// <summary>
    /// Represents a state in the environment;
    /// </summary>
    public class State;
    {
        public double[] Observations { get; set; }
        public bool IsTerminal { get; set; }
        public int Step { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public DateTime Timestamp { get; set; }

        public State Clone()
        {
            return new State;
            {
                Observations = (double[])Observations?.Clone(),
                IsTerminal = IsTerminal,
                Step = Step,
                Metadata = new Dictionary<string, object>(Metadata),
                Timestamp = Timestamp;
            };
        }
    }

    /// <summary>
    /// Represents an action taken by the agent;
    /// </summary>
    public class Action;
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public double[] Parameters { get; set; }
        public double Probability { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Represents an experience tuple (s, a, r, s', done)
    /// </summary>
    public class Experience;
    {
        public State State { get; set; }
        public Action Action { get; set; }
        public double Reward { get; set; }
        public State NextState { get; set; }
        public bool IsTerminal { get; set; }
        public double Priority { get; set; } = 1.0;
        public DateTime Timestamp { get; set; }
        public double TDError { get; set; }
    }

    /// <summary>
    /// Represents training statistics;
    /// </summary>
    public class TrainingStats;
    {
        public int Episode { get; set; }
        public double TotalReward { get; set; }
        public double AverageReward { get; set; }
        public int Steps { get; set; }
        public double ExplorationRate { get; set; }
        public double Loss { get; set; }
        public double QValueMean { get; set; }
        public double QValueStd { get; set; }
        public Dictionary<string, double> AdditionalStats { get; set; } = new();
        public TimeSpan EpisodeDuration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Interface for RL Agent;
    /// </summary>
    public interface IRLAgent : IDisposable
    {
        /// <summary>
        /// Gets the agent's unique identifier;
        /// </summary>
        string AgentId { get; }

        /// <summary>
        /// Initializes the agent with environment;
        /// </summary>
        Task InitializeAsync(IEnvironment environment, CancellationToken cancellationToken = default);

        /// <summary>
        /// Trains the agent for specified number of episodes;
        /// </summary>
        Task<TrainingStats> TrainAsync(int episodes, CancellationToken cancellationToken = default);

        /// <summary>
        /// Performs a single step in the environment;
        /// </summary>
        Task<Experience> StepAsync(State state, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the best action for a given state;
        /// </summary>
        Task<Action> GetBestActionAsync(State state, CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves the agent's model and configuration;
        /// </summary>
        Task SaveAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads the agent's model and configuration;
        /// </summary>
        Task LoadAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Resets the agent's learning progress;
        /// </summary>
        Task ResetAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the training statistics history;
        /// </summary>
        Task<List<TrainingStats>> GetTrainingHistoryAsync(int limit = 100);

        /// <summary>
        /// Evaluates the agent's performance;
        /// </summary>
        Task<EvaluationResult> EvaluateAsync(int episodes, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current Q-values for a state;
        /// </summary>
        Task<double[]> GetQValuesAsync(State state, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates the agent's policy;
        /// </summary>
        Task UpdatePolicyAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Interface for RL Environment;
    /// </summary>
    public interface IEnvironment;
    {
        /// <summary>
        /// Gets the environment name;
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the observation space dimension;
        /// </summary>
        int ObservationSpace { get; }

        /// <summary>
        /// Gets the action space dimension;
        /// </summary>
        int ActionSpace { get; }

        /// <summary>
        /// Resets the environment to initial state;
        /// </summary>
        Task<State> ResetAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes an action and returns the result;
        /// </summary>
        Task<StepResult> StepAsync(Action action, CancellationToken cancellationToken = default);

        /// <summary>
        /// Renders the environment state;
        /// </summary>
        Task RenderAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the available actions for current state;
        /// </summary>
        Task<List<Action>> GetAvailableActionsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of an environment step;
    /// </summary>
    public class StepResult;
    {
        public State NextState { get; set; }
        public double Reward { get; set; }
        public bool IsTerminal { get; set; }
        public Dictionary<string, object> Info { get; set; } = new();
    }

    /// <summary>
    /// Result of agent evaluation;
    /// </summary>
    public class EvaluationResult;
    {
        public double AverageReward { get; set; }
        public double StdReward { get; set; }
        public double MinReward { get; set; }
        public double MaxReward { get; set; }
        public double SuccessRate { get; set; }
        public int TotalSteps { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public List<TrainingStats> EpisodeStats { get; set; } = new();
        public Dictionary<string, double> AdditionalMetrics { get; set; } = new();
    }

    /// <summary>
    /// Priority experience replay buffer;
    /// </summary>
    public class PrioritizedReplayBuffer;
    {
        private readonly ConcurrentQueue<Experience> _buffer;
        private readonly int _capacity;
        private readonly Random _random;
        private readonly ConcurrentDictionary<int, double> _priorities;
        private double _maxPriority = 1.0;

        public PrioritizedReplayBuffer(int capacity)
        {
            _capacity = capacity;
            _buffer = new ConcurrentQueue<Experience>();
            _priorities = new ConcurrentDictionary<int, double>();
            _random = new Random();
        }

        public void Add(Experience experience)
        {
            if (_buffer.Count >= _capacity)
            {
                _buffer.TryDequeue(out _);
            }

            _buffer.Enqueue(experience);
            _priorities[experience.GetHashCode()] = _maxPriority;
        }

        public List<Experience> Sample(int batchSize)
        {
            var experiences = _buffer.ToList();
            if (experiences.Count == 0)
            {
                return new List<Experience>();
            }

            var probabilities = experiences.Select(e => _priorities.GetValueOrDefault(e.GetHashCode(), 1.0)).ToList();
            var total = probabilities.Sum();
            var normalized = probabilities.Select(p => p / total).ToList();

            var sampledIndices = new List<int>();
            for (int i = 0; i < batchSize; i++)
            {
                var rand = _random.NextDouble();
                var cumulative = 0.0;
                for (int j = 0; j < normalized.Count; j++)
                {
                    cumulative += normalized[j];
                    if (rand <= cumulative)
                    {
                        sampledIndices.Add(j);
                        break;
                    }
                }
            }

            return sampledIndices.Select(i => experiences[i]).ToList();
        }

        public void UpdatePriority(Experience experience, double priority)
        {
            _priorities[experience.GetHashCode()] = priority;
            _maxPriority = Math.Max(_maxPriority, priority);
        }

        public int Count => _buffer.Count;
        public bool IsFull => _buffer.Count >= _capacity;
    }

    /// <summary>
    /// Deep Q-Network implementation for RL Agent;
    /// </summary>
    public class RLAgent : IRLAgent;
    {
        private readonly ILogger<RLAgent> _logger;
        private readonly IEnvironmentSimulator _environmentSimulator;
        private readonly IRewardSystem _rewardSystem;
        private readonly IAdaptiveEngine _adaptiveEngine;
        private readonly RLAgentOptions _options;
        private readonly PrioritizedReplayBuffer _replayBuffer;
        private readonly List<TrainingStats> _trainingHistory;
        private readonly SemaphoreSlim _trainingLock;
        private readonly Random _random;

        private IEnvironment _environment;
        private NeuralNetwork _onlineNetwork;
        private NeuralNetwork _targetNetwork;
        private int _trainingSteps;
        private int _episodeCount;
        private double _currentExplorationRate;
        private bool _isTraining;
        private volatile bool _disposed;
        private string _agentId;

        /// <summary>
        /// Gets the agent's unique identifier;
        /// </summary>
        public string AgentId => _agentId;

        /// <summary>
        /// Initializes a new instance of the RLAgent class;
        /// </summary>
        public RLAgent(
            ILogger<RLAgent> logger,
            IOptions<RLAgentOptions> options,
            IEnvironmentSimulator environmentSimulator,
            IRewardSystem rewardSystem,
            IAdaptiveEngine adaptiveEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _environmentSimulator = environmentSimulator ?? throw new ArgumentNullException(nameof(environmentSimulator));
            _rewardSystem = rewardSystem ?? throw new ArgumentNullException(nameof(rewardSystem));
            _adaptiveEngine = adaptiveEngine ?? throw new ArgumentNullException(nameof(adaptiveEngine));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _agentId = $"RLAgent_{Guid.NewGuid():N}";
            _replayBuffer = new PrioritizedReplayBuffer(_options.ReplayBufferSize);
            _trainingHistory = new List<TrainingStats>();
            _trainingLock = new SemaphoreSlim(1, 1);
            _random = new Random();
            _currentExplorationRate = _options.ExplorationRate;

            InitializeNetworks();

            _logger.LogInformation("RLAgent {AgentId} initialized with {ActionSpace} actions, exploration: {ExplorationRate}",
                _agentId, _options.ActionSpace, _currentExplorationRate);
        }

        /// <inheritdoc/>
        public async Task InitializeAsync(IEnvironment environment, CancellationToken cancellationToken = default)
        {
            if (environment == null)
                throw new ArgumentNullException(nameof(environment));

            _environment = environment;

            _logger.LogInformation("Agent {AgentId} initialized with environment: {EnvironmentName}",
                _agentId, environment.Name);

            // Validate environment compatibility;
            await ValidateEnvironmentAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<TrainingStats> TrainAsync(int episodes, CancellationToken cancellationToken = default)
        {
            await _trainingLock.WaitAsync(cancellationToken);

            try
            {
                if (_environment == null)
                    throw new InvalidOperationException("Environment not initialized. Call InitializeAsync first.");

                _isTraining = true;
                var startTime = DateTime.UtcNow;
                var episodeStats = new List<TrainingStats>();

                _logger.LogInformation("Starting training for {Episodes} episodes", episodes);

                for (int episode = 1; episode <= episodes; episode++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var episodeStartTime = DateTime.UtcNow;
                    var stats = await TrainEpisodeAsync(episode, cancellationToken);

                    episodeStats.Add(stats);
                    _trainingHistory.Add(stats);

                    // Update exploration rate;
                    _currentExplorationRate = Math.Max(
                        _options.MinExplorationRate,
                        _currentExplorationRate * _options.ExplorationDecay;
                    );

                    // Log progress;
                    if (episode % 10 == 0 || episode == episodes)
                    {
                        _logger.LogInformation(
                            "Episode {Episode}/{Total}: Reward={Reward:F2}, Steps={Steps}, Exploration={Exploration:P2}, Loss={Loss:F4}",
                            episode, episodes, stats.TotalReward, stats.Steps, _currentExplorationRate, stats.Loss);
                    }

                    // Save checkpoint;
                    if (episode % _options.SaveInterval == 0)
                    {
                        await SaveCheckpointAsync(episode, cancellationToken);
                    }

                    TrimHistory();
                }

                var totalDuration = DateTime.UtcNow - startTime;
                _logger.LogInformation("Training completed in {Duration}", totalDuration);

                return episodeStats.LastOrDefault() ?? new TrainingStats();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Training failed");
                throw new NEDAException($"Training failed: {ex.Message}", ErrorCodes.TrainingFailed, ex);
            }
            finally
            {
                _isTraining = false;
                _trainingLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<Experience> StepAsync(State state, CancellationToken cancellationToken = default)
        {
            try
            {
                // Select action based on current policy;
                Action action;
                if (_random.NextDouble() < _currentExplorationRate && _options.Mode == AgentMode.Training)
                {
                    action = await GetRandomActionAsync(cancellationToken);
                }
                else;
                {
                    action = await GetBestActionAsync(state, cancellationToken);
                }

                // Execute action in environment;
                var stepResult = await _environment.StepAsync(action, cancellationToken);

                // Create experience;
                var experience = new Experience;
                {
                    State = state.Clone(),
                    Action = action,
                    Reward = stepResult.Reward * _options.RewardScale,
                    NextState = stepResult.NextState?.Clone(),
                    IsTerminal = stepResult.IsTerminal,
                    Timestamp = DateTime.UtcNow;
                };

                // Store experience if in training mode;
                if (_options.Mode == AgentMode.Training)
                {
                    _replayBuffer.Add(experience);

                    // Train network if enough experiences;
                    if (_replayBuffer.Count >= _options.BatchSize)
                    {
                        await TrainFromExperienceAsync(cancellationToken);
                    }
                }

                return experience;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step execution failed");
                throw new NEDAException($"Step failed: {ex.Message}", ErrorCodes.StepFailed, ex);
            }
        }

        /// <inheritdoc/>
        public async Task<Action> GetBestActionAsync(State state, CancellationToken cancellationToken = default)
        {
            try
            {
                // Get Q-values for current state;
                var qValues = await GetQValuesAsync(state, cancellationToken);

                // Select action with highest Q-value;
                var bestActionIndex = Array.IndexOf(qValues, qValues.Max());

                var availableActions = await _environment.GetAvailableActionsAsync(cancellationToken);
                if (availableActions == null || availableActions.Count == 0)
                {
                    throw new InvalidOperationException("No available actions in environment");
                }

                var bestAction = availableActions[bestActionIndex % availableActions.Count];
                bestAction.Probability = 1.0;

                return bestAction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get best action");
                throw new NEDAException($"Failed to get best action: {ex.Message}", ErrorCodes.ActionSelectionFailed, ex);
            }
        }

        /// <inheritdoc/>
        public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        {
            try
            {
                await _trainingLock.WaitAsync(cancellationToken);

                var agentData = new AgentData;
                {
                    AgentId = _agentId,
                    Options = _options,
                    TrainingSteps = _trainingSteps,
                    EpisodeCount = _episodeCount,
                    ExplorationRate = _currentExplorationRate,
                    NetworkWeights = await _onlineNetwork.GetWeightsAsync(cancellationToken),
                    TrainingHistory = _trainingHistory.TakeLast(100).ToList(),
                    Timestamp = DateTime.UtcNow;
                };

                var json = JsonConvert.SerializeObject(agentData, Formatting.Indented);
                await System.IO.File.WriteAllTextAsync(path, json, cancellationToken);

                _logger.LogInformation("Agent saved to {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save agent");
                throw new NEDAException($"Failed to save agent: {ex.Message}", ErrorCodes.SaveFailed, ex);
            }
            finally
            {
                _trainingLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            try
            {
                await _trainingLock.WaitAsync(cancellationToken);

                if (!System.IO.File.Exists(path))
                    throw new FileNotFoundException($"Agent file not found: {path}");

                var json = await System.IO.File.ReadAllTextAsync(path, cancellationToken);
                var agentData = JsonConvert.DeserializeObject<AgentData>(json);

                if (agentData == null)
                    throw new InvalidOperationException("Failed to deserialize agent data");

                // Restore agent state;
                _agentId = agentData.AgentId;
                _trainingSteps = agentData.TrainingSteps;
                _episodeCount = agentData.EpisodeCount;
                _currentExplorationRate = agentData.ExplorationRate;

                // Load network weights;
                await _onlineNetwork.SetWeightsAsync(agentData.NetworkWeights, cancellationToken);
                await UpdateTargetNetworkAsync();

                // Restore training history;
                _trainingHistory.Clear();
                _trainingHistory.AddRange(agentData.TrainingHistory);

                _logger.LogInformation("Agent loaded from {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load agent");
                throw new NEDAException($"Failed to load agent: {ex.Message}", ErrorCodes.LoadFailed, ex);
            }
            finally
            {
                _trainingLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            await _trainingLock.WaitAsync(cancellationToken);

            try
            {
                // Reset networks;
                await _onlineNetwork.ResetAsync(cancellationToken);
                await UpdateTargetNetworkAsync();

                // Reset buffers and history;
                _replayBuffer.Clear();
                _trainingHistory.Clear();
                _trainingSteps = 0;
                _episodeCount = 0;
                _currentExplorationRate = _options.ExplorationRate;

                _logger.LogInformation("Agent {AgentId} reset", _agentId);
            }
            finally
            {
                _trainingLock.Release();
            }
        }

        /// <inheritdoc/>
        public Task<List<TrainingStats>> GetTrainingHistoryAsync(int limit = 100)
        {
            return Task.FromResult(_trainingHistory;
                .OrderByDescending(s => s.Episode)
                .Take(limit)
                .ToList());
        }

        /// <inheritdoc/>
        public async Task<EvaluationResult> EvaluateAsync(int episodes, CancellationToken cancellationToken = default)
        {
            try
            {
                if (_environment == null)
                    throw new InvalidOperationException("Environment not initialized");

                var originalMode = _options.Mode;
                _options.Mode = AgentMode.Evaluation;

                var episodeStats = new List<TrainingStats>();
                var rewards = new List<double>();

                _logger.LogInformation("Starting evaluation for {Episodes} episodes", episodes);

                for (int i = 0; i < episodes; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var stats = await RunEvaluationEpisodeAsync(i + 1, cancellationToken);
                    episodeStats.Add(stats);
                    rewards.Add(stats.TotalReward);

                    _logger.LogDebug("Evaluation episode {Episode}: Reward={Reward:F2}, Steps={Steps}",
                        i + 1, stats.TotalReward, stats.Steps);
                }

                var result = new EvaluationResult;
                {
                    AverageReward = rewards.Average(),
                    StdReward = CalculateStandardDeviation(rewards),
                    MinReward = rewards.Min(),
                    MaxReward = rewards.Max(),
                    SuccessRate = rewards.Count(r => r > 0) / (double)rewards.Count,
                    TotalSteps = episodeStats.Sum(s => s.Steps),
                    TotalDuration = TimeSpan.FromSeconds(episodeStats.Sum(s => s.EpisodeDuration.TotalSeconds)),
                    EpisodeStats = episodeStats,
                    AdditionalMetrics = CalculateAdditionalMetrics(episodeStats)
                };

                _options.Mode = originalMode;

                _logger.LogInformation("Evaluation completed: Avg Reward={AvgReward:F2}, Success Rate={SuccessRate:P2}",
                    result.AverageReward, result.SuccessRate);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Evaluation failed");
                throw new NEDAException($"Evaluation failed: {ex.Message}", ErrorCodes.EvaluationFailed, ex);
            }
        }

        /// <inheritdoc/>
        public async Task<double[]> GetQValuesAsync(State state, CancellationToken cancellationToken = default)
        {
            try
            {
                // Forward pass through network;
                var qValues = await _onlineNetwork.PredictAsync(state.Observations, cancellationToken);
                return qValues;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Q-values");
                throw new NEDAException($"Failed to get Q-values: {ex.Message}", ErrorCodes.QValueCalculationFailed, ex);
            }
        }

        /// <inheritdoc/>
        public async Task UpdatePolicyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Update target network if needed;
                if (_trainingSteps % _options.TargetUpdateFrequency == 0)
                {
                    await UpdateTargetNetworkAsync();
                }

                // Adaptive learning rate adjustment;
                await AdjustLearningRateAsync(cancellationToken);

                _logger.LogDebug("Policy updated at step {Step}", _trainingSteps);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Policy update failed");
                throw new NEDAException($"Policy update failed: {ex.Message}", ErrorCodes.PolicyUpdateFailed, ex);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            _trainingLock?.Dispose();
            _onlineNetwork?.Dispose();
            _targetNetwork?.Dispose();

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private void InitializeNetworks()
        {
            var config = _options.NeuralNetworkConfig;

            _onlineNetwork = new NeuralNetwork(new NeuralNetworkConfig;
            {
                InputSize = _options.ObservationSpace,
                OutputSize = _options.ActionSpace,
                HiddenLayers = config.HiddenLayers,
                ActivationFunction = config.ActivationFunction,
                OutputActivation = config.OutputActivation,
                LearningRate = _options.LearningRate,
                WeightDecay = config.WeightDecay,
                DropoutRate = config.DropoutRate,
                UseBatchNormalization = config.UseBatchNormalization;
            });

            _targetNetwork = new NeuralNetwork(new NeuralNetworkConfig;
            {
                InputSize = _options.ObservationSpace,
                OutputSize = _options.ActionSpace,
                HiddenLayers = config.HiddenLayers,
                ActivationFunction = config.ActivationFunction,
                OutputActivation = config.OutputActivation,
                LearningRate = _options.LearningRate,
                WeightDecay = config.WeightDecay,
                DropoutRate = config.DropoutRate,
                UseBatchNormalization = config.UseBatchNormalization;
            });

            // Copy weights from online to target network;
            _targetNetwork.SetWeights(_onlineNetwork.GetWeights());
        }

        private async Task<TrainingStats> TrainEpisodeAsync(int episodeNumber, CancellationToken cancellationToken)
        {
            var episodeStartTime = DateTime.UtcNow;
            var state = await _environment.ResetAsync(cancellationToken);
            var totalReward = 0.0;
            var steps = 0;
            var episodeLoss = 0.0;
            var qValues = new List<double>();

            while (!state.IsTerminal && steps < _options.MaxStepsPerEpisode)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Take step;
                var experience = await StepAsync(state, cancellationToken);

                totalReward += experience.Reward;
                steps++;

                // Store Q-values for statistics;
                var currentQValues = await GetQValuesAsync(state, cancellationToken);
                qValues.AddRange(currentQValues);

                // Update state;
                state = experience.NextState ?? await _environment.ResetAsync(cancellationToken);

                // Update policy periodically;
                if (steps % 10 == 0)
                {
                    await UpdatePolicyAsync(cancellationToken);
                }
            }

            _episodeCount++;
            _trainingSteps += steps;

            var stats = new TrainingStats;
            {
                Episode = episodeNumber,
                TotalReward = totalReward,
                AverageReward = totalReward / Math.Max(1, steps),
                Steps = steps,
                ExplorationRate = _currentExplorationRate,
                Loss = episodeLoss / Math.Max(1, steps),
                QValueMean = qValues.Count > 0 ? qValues.Average() : 0,
                QValueStd = qValues.Count > 0 ? CalculateStandardDeviation(qValues) : 0,
                EpisodeDuration = DateTime.UtcNow - episodeStartTime,
                Timestamp = DateTime.UtcNow,
                AdditionalStats = CalculateEpisodeStats(steps, totalReward)
            };

            return stats;
        }

        private async Task<TrainingStats> RunEvaluationEpisodeAsync(int episodeNumber, CancellationToken cancellationToken)
        {
            var episodeStartTime = DateTime.UtcNow;
            var state = await _environment.ResetAsync(cancellationToken);
            var totalReward = 0.0;
            var steps = 0;

            while (!state.IsTerminal && steps < _options.MaxStepsPerEpisode)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Always choose best action in evaluation mode;
                var action = await GetBestActionAsync(state, cancellationToken);
                var stepResult = await _environment.StepAsync(action, cancellationToken);

                totalReward += stepResult.Reward;
                steps++;
                state = stepResult.NextState ?? await _environment.ResetAsync(cancellationToken);
            }

            return new TrainingStats;
            {
                Episode = episodeNumber,
                TotalReward = totalReward,
                AverageReward = totalReward / Math.Max(1, steps),
                Steps = steps,
                ExplorationRate = 0, // No exploration in evaluation;
                Loss = 0,
                EpisodeDuration = DateTime.UtcNow - episodeStartTime,
                Timestamp = DateTime.UtcNow;
            };
        }

        private async Task TrainFromExperienceAsync(CancellationToken cancellationToken)
        {
            var batch = _replayBuffer.Sample(_options.BatchSize);
            if (batch.Count == 0)
                return;

            var losses = new List<double>();
            var tdErrors = new List<double>();

            foreach (var experience in batch)
            {
                try
                {
                    // Calculate target Q-value;
                    var targetQ = experience.Reward;

                    if (!experience.IsTerminal && experience.NextState != null)
                    {
                        var nextQValues = await GetTargetQValuesAsync(experience.NextState, cancellationToken);
                        targetQ += _options.DiscountFactor * nextQValues.Max();
                    }

                    // Get current Q-values;
                    var currentQValues = await GetQValuesAsync(experience.State, cancellationToken);
                    var actionIndex = experience.Action.Id;
                    var currentQ = currentQValues[actionIndex];

                    // Calculate TD error;
                    var tdError = targetQ - currentQ;
                    tdErrors.Add(Math.Abs(tdError));

                    // Update experience priority;
                    if (_options.UsePrioritizedReplay)
                    {
                        _replayBuffer.UpdatePriority(experience, Math.Abs(tdError) + 0.01);
                    }

                    // Train network;
                    var loss = await _onlineNetwork.TrainAsync(
                        experience.State.Observations,
                        actionIndex,
                        targetQ,
                        cancellationToken;
                    );

                    losses.Add(loss);
                    experience.TDError = tdError;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to train from experience");
                }
            }

            if (losses.Count > 0)
            {
                _logger.LogDebug("Training batch completed: Avg Loss={Loss:F4}, Avg TD Error={TDError:F4}",
                    losses.Average(), tdErrors.Average());
            }
        }

        private async Task<double[]> GetTargetQValuesAsync(State state, CancellationToken cancellationToken)
        {
            if (_options.UseDoubleDQN)
            {
                // Double DQN: use online network to select action, target network to evaluate;
                var onlineQValues = await GetQValuesAsync(state, cancellationToken);
                var bestAction = onlineQValues.ToList().IndexOf(onlineQValues.Max());

                var targetQValues = await _targetNetwork.PredictAsync(state.Observations, cancellationToken);
                return new[] { targetQValues[bestAction] };
            }
            else;
            {
                // Standard DQN: use target network for both selection and evaluation;
                return await _targetNetwork.PredictAsync(state.Observations, cancellationToken);
            }
        }

        private async Task<Action> GetRandomActionAsync(CancellationToken cancellationToken)
        {
            var availableActions = await _environment.GetAvailableActionsAsync(cancellationToken);
            if (availableActions == null || availableActions.Count == 0)
            {
                throw new InvalidOperationException("No available actions");
            }

            var randomIndex = _random.Next(availableActions.Count);
            var action = availableActions[randomIndex];
            action.Probability = 1.0 / availableActions.Count;

            return action;
        }

        private async Task UpdateTargetNetworkAsync()
        {
            var onlineWeights = await _onlineNetwork.GetWeightsAsync(CancellationToken.None);
            await _targetNetwork.SetWeightsAsync(onlineWeights, CancellationToken.None);

            _logger.LogDebug("Target network updated");
        }

        private async Task AdjustLearningRateAsync(CancellationToken cancellationToken)
        {
            if (_adaptiveEngine != null)
            {
                var performanceMetrics = await CalculatePerformanceMetricsAsync(cancellationToken);
                var newLearningRate = await _adaptiveEngine.AdjustLearningRateAsync(
                    _options.LearningRate,
                    performanceMetrics,
                    cancellationToken;
                );

                if (Math.Abs(newLearningRate - _options.LearningRate) > 0.0001)
                {
                    _options.LearningRate = newLearningRate;
                    await _onlineNetwork.SetLearningRateAsync(newLearningRate, cancellationToken);

                    _logger.LogDebug("Learning rate adjusted to {LearningRate}", newLearningRate);
                }
            }
        }

        private async Task<Dictionary<string, double>> CalculatePerformanceMetricsAsync(CancellationToken cancellationToken)
        {
            return new Dictionary<string, double>
            {
                ["training_steps"] = _trainingSteps,
                ["episode_count"] = _episodeCount,
                ["buffer_size"] = _replayBuffer.Count,
                ["exploration_rate"] = _currentExplorationRate;
            };
        }

        private Dictionary<string, double> CalculateEpisodeStats(int steps, double totalReward)
        {
            return new Dictionary<string, double>
            {
                ["steps_per_second"] = steps / Math.Max(1, _options.MaxStepsPerEpisode),
                ["reward_per_step"] = totalReward / Math.Max(1, steps),
                ["exploration_steps"] = steps * _currentExplorationRate,
                ["exploitation_steps"] = steps * (1 - _currentExplorationRate)
            };
        }

        private Dictionary<string, double> CalculateAdditionalMetrics(List<TrainingStats> episodeStats)
        {
            if (episodeStats.Count == 0)
                return new Dictionary<string, double>();

            return new Dictionary<string, double>
            {
                ["avg_steps"] = episodeStats.Average(s => s.Steps),
                ["std_steps"] = CalculateStandardDeviation(episodeStats.Select(s => (double)s.Steps)),
                ["avg_duration"] = episodeStats.Average(s => s.EpisodeDuration.TotalSeconds),
                ["success_episodes"] = episodeStats.Count(s => s.TotalReward > 0)
            };
        }

        private async Task ValidateEnvironmentAsync(CancellationToken cancellationToken)
        {
            // Validate observation space;
            if (_environment.ObservationSpace <= 0)
                throw new InvalidOperationException("Invalid observation space");

            // Validate action space;
            if (_environment.ActionSpace <= 0)
                throw new InvalidOperationException("Invalid action space");

            // Test environment reset;
            var initialState = await _environment.ResetAsync(cancellationToken);
            if (initialState == null)
                throw new InvalidOperationException("Environment reset failed");

            _logger.LogInformation("Environment validated successfully");
        }

        private async Task SaveCheckpointAsync(int episode, CancellationToken cancellationToken)
        {
            var checkpointDir = Path.Combine("checkpoints", _agentId);
            Directory.CreateDirectory(checkpointDir);

            var checkpointPath = Path.Combine(checkpointDir, $"checkpoint_episode_{episode}.json");
            await SaveAsync(checkpointPath, cancellationToken);

            _logger.LogDebug("Checkpoint saved: {CheckpointPath}", checkpointPath);
        }

        private void TrimHistory()
        {
            const int maxHistory = 10000;

            if (_trainingHistory.Count > maxHistory)
            {
                _trainingHistory.RemoveRange(0, _trainingHistory.Count - maxHistory);
            }
        }

        private double CalculateStandardDeviation(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count < 2)
                return 0;

            var mean = list.Average();
            var variance = list.Select(x => Math.Pow(x - mean, 2)).Sum() / (list.Count - 1);
            return Math.Sqrt(variance);
        }

        #endregion;

        #region Internal Classes;

        private class AgentData;
        {
            public string AgentId { get; set; }
            public RLAgentOptions Options { get; set; }
            public int TrainingSteps { get; set; }
            public int EpisodeCount { get; set; }
            public double ExplorationRate { get; set; }
            public double[] NetworkWeights { get; set; }
            public List<TrainingStats> TrainingHistory { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class ReplayBuffer;
        {
            public void Clear()
            {
                // Implementation for clearing buffer;
            }
        }

        #endregion;

        #region Error Codes;

        private static class ErrorCodes;
        {
            public const string TrainingFailed = "RL_AGENT_001";
            public const string StepFailed = "RL_AGENT_002";
            public const string ActionSelectionFailed = "RL_AGENT_003";
            public const string SaveFailed = "RL_AGENT_004";
            public const string LoadFailed = "RL_AGENT_005";
            public const string EvaluationFailed = "RL_AGENT_006";
            public const string QValueCalculationFailed = "RL_AGENT_007";
            public const string PolicyUpdateFailed = "RL_AGENT_008";
        }

        #endregion;
    }

    /// <summary>
    /// Factory for creating RLAgent instances;
    /// </summary>
    public interface IRLAgentFactory;
    {
        /// <summary>
        /// Creates a new RLAgent instance;
        /// </summary>
        IRLAgent CreateAgent(string agentType = "DQN");

        /// <summary>
        /// Creates an RLAgent with custom configuration;
        /// </summary>
        IRLAgent CreateAgent(RLAgentOptions options);
    }

    /// <summary>
    /// Implementation of RLAgent factory;
    /// </summary>
    public class RLAgentFactory : IRLAgentFactory;
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<RLAgentFactory> _logger;

        public RLAgentFactory(IServiceProvider serviceProvider, ILogger<RLAgentFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public IRLAgent CreateAgent(string agentType = "DQN")
        {
            _logger.LogInformation("Creating RL agent of type: {AgentType}", agentType);

            return agentType.ToUpper() switch;
            {
                "DQN" => CreateDQNAgent(),
                "DOUBLE_DQN" => CreateDoubleDQNAgent(),
                "DUELING_DQN" => CreateDuelingDQNAgent(),
                "A2C" => CreateA2CAgent(),
                "PPO" => CreatePPOAgent(),
                "SAC" => CreateSACAgent(),
                _ => CreateDQNAgent()
            };
        }

        public IRLAgent CreateAgent(RLAgentOptions options)
        {
            var agent = CreateDQNAgent();
            // Apply custom options if needed;
            return agent;
        }

        private IRLAgent CreateDQNAgent()
        {
            return _serviceProvider.GetRequiredService<IRLAgent>();
        }

        private IRLAgent CreateDoubleDQNAgent()
        {
            var options = new RLAgentOptions;
            {
                UseDoubleDQN = true,
                Policy = PolicyType.EpsilonGreedy;
            };

            return CreateAgent(options);
        }

        private IRLAgent CreateDuelingDQNAgent()
        {
            var options = new RLAgentOptions;
            {
                UseDoubleDQN = true,
                Policy = PolicyType.EpsilonGreedy,
                NeuralNetworkConfig = new NeuralNetworkConfig;
                {
                    HiddenLayers = new[] { 256, 256 }
                }
            };

            return CreateAgent(options);
        }

        private IRLAgent CreateA2CAgent()
        {
            // Actor-Critic agent implementation;
            throw new NotImplementedException("A2C agent not implemented yet");
        }

        private IRLAgent CreatePPOAgent()
        {
            // PPO agent implementation;
            throw new NotImplementedException("PPO agent not implemented yet");
        }

        private IRLAgent CreateSACAgent()
        {
            // SAC agent implementation;
            throw new NotImplementedException("SAC agent not implemented yet");
        }
    }
}
