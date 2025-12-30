using Microsoft.Extensions.Logging;
using NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees;
using NEDA.CharacterSystems.AI_Behaviors.BlackboardSystems;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Monitoring.Diagnostics;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees.BehaviorTree;
using static NEDA.CharacterSystems.AI_Behaviors.CombatAI.CombatSystem;

namespace NEDA.CharacterSystems.AI_Behaviors.CombatAI;
{
    /// <summary>
    /// Main Combat System interface for AI-driven combat mechanics;
    /// </summary>
    public interface ICombatSystem : IDisposable
    {
        Task<bool> InitializeAsync(CombatSystemConfig config);
        Task<CombatResult> ExecuteCombatAsync(CombatContext context, CancellationToken cancellationToken = default);
        Task<bool> RegisterCombatantAsync(Combatant combatant);
        Task<bool> UnregisterCombatantAsync(string combatantId);
        Task<Combatant> GetCombatantAsync(string combatantId);
        Task<IEnumerable<Combatant>> GetActiveCombatantsAsync();

        Task<bool> StartCombatAsync(string combatId, CombatScenario scenario);
        Task<bool> EndCombatAsync(string combatId, CombatConclusion conclusion);
        Task<CombatState> GetCombatStateAsync(string combatId);

        Task<DecisionResult> MakeCombatDecisionAsync(Combatant combatant, CombatContext context);
        Task<ActionResult> ExecuteCombatActionAsync(CombatAction action, CombatContext context);

        Task<bool> ApplyDamageAsync(DamageInstance damage);
        Task<bool> ApplyHealingAsync(HealingInstance healing);
        Task<bool> ApplyStatusEffectAsync(StatusEffect effect);

        Task<CombatMetrics> GetMetricsAsync();
        Task<CombatReport> GenerateCombatReportAsync(string combatId);
        Task<bool> ValidateCombatStateAsync();
    }

    /// <summary>
    /// Main Combat System implementation for tactical AI combat;
    /// </summary>
    public class CombatSystem : ICombatSystem;
    {
        private readonly ILogger<CombatSystem> _logger;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IEventBus _eventBus;
        private readonly IBlackboard _blackboard;
        private readonly IBehaviorTree _behaviorTree;
        private readonly IPathfindingService _pathfinding;
        private readonly ILOSService _losService;

        // Combat state management;
        private readonly ConcurrentDictionary<string, CombatSession> _activeCombats;
        private readonly ConcurrentDictionary<string, Combatant> _registeredCombatants;
        private readonly ConcurrentDictionary<string, CombatAIProfile> _aiProfiles;

        // Combat mechanics;
        private readonly DamageCalculator _damageCalculator;
        private readonly ThreatManager _threatManager;
        private readonly PositionTracker _positionTracker;
        private readonly AbilityManager _abilityManager;
        private readonly CooldownManager _cooldownManager;

        // Configuration and state;
        private CombatSystemConfig _config;
        private bool _isInitialized;
        private readonly CombatMetrics _metrics;
        private readonly CombatHistory _history;
        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);

        // Timers and cleanup;
        private Timer _combatUpdateTimer;
        private Timer _cleanupTimer;
        private readonly CancellationTokenSource _shutdownCts;

        /// <summary>
        /// Combat System constructor with dependency injection;
        /// </summary>
        public CombatSystem(
            ILogger<CombatSystem> logger,
            IDiagnosticTool diagnosticTool,
            IEventBus eventBus,
            IBlackboard blackboard,
            IBehaviorTree behaviorTree,
            IPathfindingService pathfinding,
            ILOSService losService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _behaviorTree = behaviorTree ?? throw new ArgumentNullException(nameof(behaviorTree));
            _pathfinding = pathfinding ?? throw new ArgumentNullException(nameof(pathfinding));
            _losService = losService ?? throw new ArgumentNullException(nameof(losService));

            _activeCombats = new ConcurrentDictionary<string, CombatSession>();
            _registeredCombatants = new ConcurrentDictionary<string, Combatant>();
            _aiProfiles = new ConcurrentDictionary<string, CombatAIProfile>();

            _damageCalculator = new DamageCalculator();
            _threatManager = new ThreatManager();
            _positionTracker = new PositionTracker();
            _abilityManager = new AbilityManager();
            _cooldownManager = new CooldownManager();

            _metrics = new CombatMetrics();
            _history = new CombatHistory();

            _shutdownCts = new CancellationTokenSource();

            _logger.LogInformation("Combat System initialized");
        }

        /// <summary>
        /// Initialize combat system with configuration;
        /// </summary>
        public async Task<bool> InitializeAsync(CombatSystemConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            await _initializationLock.WaitAsync();

            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("Combat System already initialized");
                    return true;
                }

                _logger.LogInformation("Initializing Combat System with configuration: {ConfigName}", config.Name);

                _config = config;

                // Initialize blackboard with combat data;
                await InitializeBlackboardAsync();

                // Initialize behavior tree for combat AI;
                await InitializeBehaviorTreeAsync();

                // Load AI profiles;
                await LoadAIProfilesAsync(config.AIProfilePaths);

                // Initialize subsystems;
                await InitializeSubsystemsAsync();

                // Set up timers;
                if (config.EnableAutoUpdates && config.UpdateInterval > TimeSpan.Zero)
                {
                    _combatUpdateTimer = new Timer(
                        async _ => await ProcessCombatUpdatesAsync(),
                        null,
                        config.UpdateInterval,
                        config.UpdateInterval);

                    _logger.LogDebug("Combat update timer started with interval: {Interval}", config.UpdateInterval);
                }

                if (config.EnableAutoCleanup && config.CleanupInterval > TimeSpan.Zero)
                {
                    _cleanupTimer = new Timer(
                        async _ => await PerformCleanupAsync(),
                        null,
                        config.CleanupInterval,
                        config.CleanupInterval);

                    _logger.LogDebug("Combat cleanup timer started with interval: {Interval}", config.CleanupInterval);
                }

                _isInitialized = true;

                await _eventBus.PublishAsync(new CombatSystemInitializedEvent;
                {
                    SystemId = _config.SystemId,
                    Name = config.Name,
                    Version = config.Version,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Combat System initialized successfully with ID: {SystemId}", _config.SystemId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Combat System");
                throw new CombatSystemException("Combat System initialization failed", ex);
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        /// <summary>
        /// Execute combat logic for a given context;
        /// </summary>
        public async Task<CombatResult> ExecuteCombatAsync(CombatContext context, CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("CombatExecution");

            try
            {
                _logger.LogInformation("Executing combat for context: {ContextId}", context.ContextId);

                var startTime = DateTime.UtcNow;
                var result = new CombatResult;
                {
                    ContextId = context.ContextId,
                    StartTime = startTime,
                    CombatActions = new List<CombatAction>(),
                    DamageInstances = new List<DamageInstance>(),
                    HealingInstances = new List<HealingInstance>()
                };

                // Validate combat context;
                var validationResult = await ValidateCombatContextAsync(context);
                if (!validationResult.IsValid)
                {
                    result.Result = CombatResultType.InvalidContext;
                    result.ErrorMessage = validationResult.ErrorMessage;
                    return result;
                }

                // Get or create combat session;
                var combatSession = await GetOrCreateCombatSessionAsync(context);

                // Update combat state;
                combatSession.LastUpdateTime = DateTime.UtcNow;
                combatSession.UpdateCount++;

                // Process each combatant's turn;
                var combatantResults = await ProcessCombatantTurnsAsync(combatSession, context, cancellationToken);

                // Process damage and effects;
                var combatEffects = await ProcessCombatEffectsAsync(combatSession, combatantResults);

                // Update threat levels;
                await UpdateThreatLevelsAsync(combatSession, combatEffects);

                // Check combat conclusion;
                var conclusion = await CheckCombatConclusionAsync(combatSession);
                if (conclusion.IsConcluded)
                {
                    await ConcludeCombatAsync(combatSession, conclusion);
                    result.Result = conclusion.Result;
                }
                else;
                {
                    result.Result = CombatResultType.Ongoing;
                }

                // Collect results;
                result.CombatActions.AddRange(combatantResults.SelectMany(cr => cr.Actions));
                result.DamageInstances.AddRange(combatEffects.DamageInstances);
                result.HealingInstances.AddRange(combatEffects.HealingInstances);
                result.StatusEffects = combatEffects.StatusEffects;

                var endTime = DateTime.UtcNow;
                result.EndTime = endTime;
                result.Duration = endTime - startTime;

                // Update metrics;
                _metrics.RecordCombatExecution(result);

                // Add to history;
                await _history.RecordCombatAsync(result);

                // Publish combat event;
                await _eventBus.PublishAsync(new CombatExecutedEvent;
                {
                    CombatId = combatSession.CombatId,
                    ContextId = context.ContextId,
                    Result = result.Result,
                    Duration = result.Duration,
                    ParticipantCount = combatSession.Participants.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Combat execution completed for context: {ContextId}. Result: {Result}, Duration: {Duration}",
                    context.ContextId, result.Result, result.Duration);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Combat execution cancelled for context: {ContextId}", context.ContextId);
                throw;
            }
            catch (CombatSystemException ex)
            {
                _logger.LogError(ex, "Combat execution error for context: {ContextId}", context.ContextId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unexpected error during combat execution for context: {ContextId}", context.ContextId);
                throw new CombatSystemException($"Combat execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Register a combatant in the system;
        /// </summary>
        public async Task<bool> RegisterCombatantAsync(Combatant combatant)
        {
            if (combatant == null)
                throw new ArgumentNullException(nameof(combatant));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                // Validate combatant;
                var validationResult = await ValidateCombatantAsync(combatant);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Combatant validation failed: {ErrorMessage}", validationResult.ErrorMessage);
                    return false;
                }

                // Check if already registered;
                if (_registeredCombatants.ContainsKey(combatant.Id))
                {
                    _logger.LogDebug("Combatant already registered: {CombatantId}", combatant.Id);
                    return false;
                }

                // Register combatant;
                var added = _registeredCombatants.TryAdd(combatant.Id, combatant);

                if (added)
                {
                    // Initialize combatant state;
                    combatant.State = CombatantState.Idle;
                    combatant.RegisteredAt = DateTime.UtcNow;

                    // Update blackboard;
                    await _blackboard.SetValueAsync($"combatant.{combatant.Id}", combatant);

                    // Publish event;
                    await _eventBus.PublishAsync(new CombatantRegisteredEvent;
                    {
                        CombatantId = combatant.Id,
                        Name = combatant.Name,
                        Faction = combatant.Faction,
                        Level = combatant.Level,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Combatant registered: {CombatantId} ({Name})", combatant.Id, combatant.Name);
                }

                return added;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering combatant: {CombatantId}", combatant.Id);
                return false;
            }
        }

        /// <summary>
        /// Unregister a combatant from the system;
        /// </summary>
        public async Task<bool> UnregisterCombatantAsync(string combatantId)
        {
            if (string.IsNullOrWhiteSpace(combatantId))
                throw new ArgumentException("Combatant ID cannot be empty", nameof(combatantId));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                // Remove from registered combatants;
                var removed = _registeredCombatants.TryRemove(combatantId, out var combatant);

                if (removed && combatant != null)
                {
                    // Remove from all active combats;
                    foreach (var combatSession in _activeCombats.Values)
                    {
                        combatSession.RemoveParticipant(combatantId);
                    }

                    // Remove from blackboard;
                    await _blackboard.RemoveValueAsync($"combatant.{combatantId}");

                    // Publish event;
                    await _eventBus.PublishAsync(new CombatantUnregisteredEvent;
                    {
                        CombatantId = combatantId,
                        Name = combatant.Name,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Combatant unregistered: {CombatantId} ({Name})", combatantId, combatant.Name);
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unregistering combatant: {CombatantId}", combatantId);
                return false;
            }
        }

        /// <summary>
        /// Get combatant by ID;
        /// </summary>
        public async Task<Combatant> GetCombatantAsync(string combatantId)
        {
            if (string.IsNullOrWhiteSpace(combatantId))
                throw new ArgumentException("Combatant ID cannot be empty", nameof(combatantId));

            if (!_isInitialized)
                return null;

            try
            {
                // Try to get from registered combatants;
                if (_registeredCombatants.TryGetValue(combatantId, out var combatant))
                {
                    return combatant;
                }

                // Try to get from blackboard;
                var result = await _blackboard.GetValueAsync<Combatant>($"combatant.{combatantId}");
                if (result.Success)
                {
                    return result.Value;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting combatant: {CombatantId}", combatantId);
                return null;
            }
        }

        /// <summary>
        /// Get all active combatants;
        /// </summary>
        public async Task<IEnumerable<Combatant>> GetActiveCombatantsAsync()
        {
            if (!_isInitialized)
                return Enumerable.Empty<Combatant>();

            try
            {
                return _registeredCombatants.Values;
                    .Where(c => c.State != CombatantState.Dead && c.State != CombatantState.Disabled)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active combatants");
                return Enumerable.Empty<Combatant>();
            }
        }

        /// <summary>
        /// Start a new combat session;
        /// </summary>
        public async Task<bool> StartCombatAsync(string combatId, CombatScenario scenario)
        {
            if (string.IsNullOrWhiteSpace(combatId))
                throw new ArgumentException("Combat ID cannot be empty", nameof(combatId));

            if (scenario == null)
                throw new ArgumentNullException(nameof(scenario));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                // Check if combat already exists;
                if (_activeCombats.ContainsKey(combatId))
                {
                    _logger.LogWarning("Combat already exists: {CombatId}", combatId);
                    return false;
                }

                // Validate scenario;
                var validationResult = await ValidateCombatScenarioAsync(scenario);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Combat scenario validation failed: {ErrorMessage}", validationResult.ErrorMessage);
                    return false;
                }

                // Create combat session;
                var combatSession = new CombatSession;
                {
                    CombatId = combatId,
                    Scenario = scenario,
                    StartTime = DateTime.UtcNow,
                    State = CombatSessionState.Active,
                    Participants = new List<Combatant>(),
                    Environment = scenario.Environment;
                };

                // Register participants;
                foreach (var participantId in scenario.ParticipantIds)
                {
                    var combatant = await GetCombatantAsync(participantId);
                    if (combatant != null)
                    {
                        combatSession.Participants.Add(combatant);
                        combatant.State = CombatantState.InCombat;
                    }
                }

                // Check if we have enough participants;
                if (combatSession.Participants.Count < 2)
                {
                    _logger.LogWarning("Not enough participants for combat: {CombatId}", combatId);
                    return false;
                }

                // Initialize combat state;
                await InitializeCombatSessionAsync(combatSession);

                // Add to active combats;
                var added = _activeCombats.TryAdd(combatId, combatSession);

                if (added)
                {
                    // Update blackboard;
                    await _blackboard.SetValueAsync($"combat.{combatId}", combatSession);

                    // Publish event;
                    await _eventBus.PublishAsync(new CombatStartedEvent;
                    {
                        CombatId = combatId,
                        ScenarioName = scenario.Name,
                        ParticipantCount = combatSession.Participants.Count,
                        Environment = scenario.Environment?.Name,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Combat started: {CombatId} with {ParticipantCount} participants",
                        combatId, combatSession.Participants.Count);
                }

                return added;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting combat: {CombatId}", combatId);
                return false;
            }
        }

        /// <summary>
        /// End an active combat session;
        /// </summary>
        public async Task<bool> EndCombatAsync(string combatId, CombatConclusion conclusion)
        {
            if (string.IsNullOrWhiteSpace(combatId))
                throw new ArgumentException("Combat ID cannot be empty", nameof(combatId));

            if (conclusion == null)
                throw new ArgumentNullException(nameof(conclusion));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                // Get combat session;
                if (!_activeCombats.TryGetValue(combatId, out var combatSession))
                {
                    _logger.LogWarning("Combat not found: {CombatId}", combatId);
                    return false;
                }

                // Update combat session;
                combatSession.EndTime = DateTime.UtcNow;
                combatSession.State = CombatSessionState.Concluded;
                combatSession.Conclusion = conclusion;

                // Update participant states;
                foreach (var participant in combatSession.Participants)
                {
                    if (participant.State != CombatantState.Dead)
                    {
                        participant.State = CombatantState.Idle;
                    }
                }

                // Generate combat report;
                var report = await GenerateCombatReportAsync(combatId);

                // Remove from active combats;
                var removed = _activeCombats.TryRemove(combatId, out _);

                if (removed)
                {
                    // Update blackboard;
                    await _blackboard.SetValueAsync($"combat.{combatId}.concluded", combatSession);
                    await _blackboard.RemoveValueAsync($"combat.{combatId}");

                    // Update history;
                    await _history.RecordCombatConclusionAsync(combatId, conclusion, report);

                    // Publish event;
                    await _eventBus.PublishAsync(new CombatEndedEvent;
                    {
                        CombatId = combatId,
                        Conclusion = conclusion.Result,
                        Duration = combatSession.EndTime.Value - combatSession.StartTime,
                        ParticipantCount = combatSession.Participants.Count,
                        VictoriousFaction = conclusion.VictoriousFaction,
                        Report = report,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Combat ended: {CombatId}. Result: {Result}, Duration: {Duration}",
                        combatId, conclusion.Result, combatSession.EndTime.Value - combatSession.StartTime);
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending combat: {CombatId}", combatId);
                return false;
            }
        }

        /// <summary>
        /// Get current state of a combat session;
        /// </summary>
        public async Task<CombatState> GetCombatStateAsync(string combatId)
        {
            if (string.IsNullOrWhiteSpace(combatId))
                throw new ArgumentException("Combat ID cannot be empty", nameof(combatId));

            if (!_isInitialized)
                return null;

            try
            {
                if (_activeCombats.TryGetValue(combatId, out var combatSession))
                {
                    return new CombatState;
                    {
                        CombatId = combatSession.CombatId,
                        State = combatSession.State,
                        ParticipantCount = combatSession.Participants.Count,
                        AliveCount = combatSession.Participants.Count(p => p.State != CombatantState.Dead),
                        StartTime = combatSession.StartTime,
                        Duration = combatSession.EndTime.HasValue ?
                            combatSession.EndTime.Value - combatSession.StartTime :
                            DateTime.UtcNow - combatSession.StartTime,
                        Environment = combatSession.Environment,
                        ThreatLevels = await _threatManager.GetThreatLevelsAsync(combatSession)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting combat state: {CombatId}", combatId);
                return null;
            }
        }

        /// <summary>
        /// Make a combat decision for a combatant;
        /// </summary>
        public async Task<DecisionResult> MakeCombatDecisionAsync(Combatant combatant, CombatContext context)
        {
            if (combatant == null)
                throw new ArgumentNullException(nameof(combatant));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("CombatDecisionMaking");

            try
            {
                _logger.LogDebug("Making combat decision for: {CombatantId} in context: {ContextId}",
                    combatant.Id, context.ContextId);

                var startTime = DateTime.UtcNow;

                // Get AI profile for combatant;
                var aiProfile = await GetAIProfileForCombatantAsync(combatant);

                // Create decision context;
                var decisionContext = new DecisionContext;
                {
                    Combatant = combatant,
                    Context = context,
                    AIProfile = aiProfile,
                    AvailableActions = await GetAvailableActionsAsync(combatant),
                    ThreatLevels = await _threatManager.GetThreatLevelsForCombatantAsync(combatant),
                    PositionData = await _positionTracker.GetPositionDataAsync(combatant),
                    Cooldowns = await _cooldownManager.GetCooldownsAsync(combatant.Id)
                };

                // Make decision using AI;
                DecisionResult decisionResult;

                switch (aiProfile?.DecisionStrategy ?? DecisionStrategy.Balanced)
                {
                    case DecisionStrategy.BehaviorTree:
                        decisionResult = await MakeDecisionWithBehaviorTreeAsync(decisionContext);
                        break;

                    case DecisionStrategy.UtilityAI:
                        decisionResult = await MakeDecisionWithUtilityAIAsync(decisionContext);
                        break;

                    case DecisionStrategy.StateMachine:
                        decisionResult = await MakeDecisionWithStateMachineAsync(decisionContext);
                        break;

                    case DecisionStrategy.RuleBased:
                        decisionResult = await MakeDecisionWithRuleBasedAIAsync(decisionContext);
                        break;

                    default:
                        decisionResult = await MakeDecisionWithBalancedAIAsync(decisionContext);
                        break;
                }

                var endTime = DateTime.UtcNow;
                decisionResult.DecisionTime = endTime - startTime;
                decisionResult.Timestamp = endTime;

                // Update metrics;
                _metrics.RecordDecision(decisionResult);

                // Publish event;
                await _eventBus.PublishAsync(new CombatDecisionMadeEvent;
                {
                    CombatantId = combatant.Id,
                    DecisionType = decisionResult.DecisionType,
                    SelectedAction = decisionResult.SelectedAction?.ActionType.ToString(),
                    Confidence = decisionResult.Confidence,
                    DecisionTime = decisionResult.DecisionTime,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Combat decision made for {CombatantId}: {DecisionType} in {DecisionTime}ms",
                    combatant.Id, decisionResult.DecisionType, decisionResult.DecisionTime.TotalMilliseconds);

                return decisionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error making combat decision for: {CombatantId}", combatant.Id);

                return new DecisionResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    DecisionType = DecisionType.Error;
                };
            }
        }

        /// <summary>
        /// Execute a combat action;
        /// </summary>
        public async Task<ActionResult> ExecuteCombatActionAsync(CombatAction action, CombatContext context)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("CombatActionExecution");

            try
            {
                _logger.LogInformation("Executing combat action: {ActionType} by {SourceId} on {TargetId}",
                    action.ActionType, action.SourceId, action.TargetId);

                var startTime = DateTime.UtcNow;

                // Validate action;
                var validationResult = await ValidateCombatActionAsync(action, context);
                if (!validationResult.IsValid)
                {
                    return new ActionResult;
                    {
                        Success = false,
                        ErrorMessage = validationResult.ErrorMessage,
                        ActionType = action.ActionType;
                    };
                }

                // Get source and target combatants;
                var source = await GetCombatantAsync(action.SourceId);
                var target = action.TargetId != null ? await GetCombatantAsync(action.TargetId) : null;

                if (source == null || (action.RequiresTarget && target == null))
                {
                    return new ActionResult;
                    {
                        Success = false,
                        ErrorMessage = "Invalid source or target combatant",
                        ActionType = action.ActionType;
                    };
                }

                // Check cooldowns;
                if (!await _cooldownManager.CanUseAbilityAsync(source.Id, action.AbilityId))
                {
                    return new ActionResult;
                    {
                        Success = false,
                        ErrorMessage = "Ability is on cooldown",
                        ActionType = action.ActionType;
                    };
                }

                // Execute action based on type;
                ActionResult result;

                switch (action.ActionType)
                {
                    case ActionType.Attack:
                        result = await ExecuteAttackActionAsync(action, source, target, context);
                        break;

                    case ActionType.Ability:
                        result = await ExecuteAbilityActionAsync(action, source, target, context);
                        break;

                    case ActionType.Move:
                        result = await ExecuteMoveActionAsync(action, source, context);
                        break;

                    case ActionType.Defend:
                        result = await ExecuteDefendActionAsync(action, source, context);
                        break;

                    case ActionType.UseItem:
                        result = await ExecuteUseItemActionAsync(action, source, target, context);
                        break;

                    default:
                        result = new ActionResult;
                        {
                            Success = false,
                            ErrorMessage = $"Unsupported action type: {action.ActionType}",
                            ActionType = action.ActionType;
                        };
                        break;
                }

                var endTime = DateTime.UtcNow;
                result.ExecutionTime = endTime - startTime;
                result.Timestamp = endTime;

                // Update cooldowns;
                if (result.Success && action.AbilityId != null)
                {
                    await _cooldownManager.StartCooldownAsync(source.Id, action.AbilityId, action.CooldownDuration);
                }

                // Update metrics;
                _metrics.RecordAction(result);

                // Publish event;
                await _eventBus.PublishAsync(new CombatActionExecutedEvent;
                {
                    ActionId = action.ActionId,
                    ActionType = action.ActionType,
                    SourceId = action.SourceId,
                    TargetId = action.TargetId,
                    Success = result.Success,
                    DamageDealt = result.DamageDealt,
                    HealingDone = result.HealingDone,
                    ExecutionTime = result.ExecutionTime,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Combat action executed: {ActionType} by {SourceId}. Success: {Success}, Time: {ExecutionTime}ms",
                    action.ActionType, action.SourceId, result.Success, result.ExecutionTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing combat action: {ActionId}", action.ActionId);

                return new ActionResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ActionType = action.ActionType,
                    ExecutionTime = TimeSpan.Zero;
                };
            }
        }

        /// <summary>
        /// Apply damage to a combatant;
        /// </summary>
        public async Task<bool> ApplyDamageAsync(DamageInstance damage)
        {
            if (damage == null)
                throw new ArgumentNullException(nameof(damage));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                _logger.LogDebug("Applying damage: {DamageAmount} {DamageType} to {TargetId} from {SourceId}",
                    damage.Amount, damage.DamageType, damage.TargetId, damage.SourceId);

                // Get target combatant;
                var target = await GetCombatantAsync(damage.TargetId);
                if (target == null)
                {
                    _logger.LogWarning("Target not found for damage: {TargetId}", damage.TargetId);
                    return false;
                }

                // Calculate final damage with resistances and armor;
                var finalDamage = await _damageCalculator.CalculateFinalDamageAsync(damage, target);

                // Apply damage to combatant;
                var previousHealth = target.Health;
                target.Health -= finalDamage.Amount;
                target.Health = Math.Max(0, target.Health);

                // Check for death;
                if (target.Health <= 0)
                {
                    target.State = CombatantState.Dead;
                    target.DeathTime = DateTime.UtcNow;

                    _logger.LogInformation("Combatant killed: {CombatantId} ({Name})", target.Id, target.Name);
                }

                // Update threat levels;
                if (damage.SourceId != null)
                {
                    await _threatManager.AddThreatAsync(damage.SourceId, target.Id, finalDamage.Amount * _config.ThreatDamageMultiplier);
                }

                // Update blackboard;
                await _blackboard.SetValueAsync($"combatant.{target.Id}.health", target.Health);
                await _blackboard.SetValueAsync($"combatant.{target.Id}.state", target.State);

                // Publish event;
                await _eventBus.PublishAsync(new DamageAppliedEvent;
                {
                    DamageInstance = damage,
                    FinalDamage = finalDamage,
                    TargetHealthBefore = previousHealth,
                    TargetHealthAfter = target.Health,
                    TargetState = target.State,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Damage applied: {FinalDamage} to {TargetId}. Health: {PreviousHealth} -> {CurrentHealth}",
                    finalDamage.Amount, damage.TargetId, previousHealth, target.Health);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying damage to: {TargetId}", damage.TargetId);
                return false;
            }
        }

        /// <summary>
        /// Apply healing to a combatant;
        /// </summary>
        public async Task<bool> ApplyHealingAsync(HealingInstance healing)
        {
            if (healing == null)
                throw new ArgumentNullException(nameof(healing));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                _logger.LogDebug("Applying healing: {HealingAmount} to {TargetId} from {SourceId}",
                    healing.Amount, healing.TargetId, healing.SourceId);

                // Get target combatant;
                var target = await GetCombatantAsync(healing.TargetId);
                if (target == null)
                {
                    _logger.LogWarning("Target not found for healing: {TargetId}", healing.TargetId);
                    return false;
                }

                // Calculate final healing with bonuses;
                var finalHealing = await _damageCalculator.CalculateFinalHealingAsync(healing, target);

                // Apply healing to combatant;
                var previousHealth = target.Health;
                target.Health += finalHealing.Amount;
                target.Health = Math.Min(target.Health, target.MaxHealth);

                // Update blackboard;
                await _blackboard.SetValueAsync($"combatant.{target.Id}.health", target.Health);

                // Update threat levels (healing generates threat)
                if (healing.SourceId != null)
                {
                    await _threatManager.AddThreatAsync(healing.SourceId, target.Id, finalHealing.Amount * _config.ThreatHealingMultiplier);
                }

                // Publish event;
                await _eventBus.PublishAsync(new HealingAppliedEvent;
                {
                    HealingInstance = healing,
                    FinalHealing = finalHealing,
                    TargetHealthBefore = previousHealth,
                    TargetHealthAfter = target.Health,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Healing applied: {FinalHealing} to {TargetId}. Health: {PreviousHealth} -> {CurrentHealth}",
                    finalHealing.Amount, healing.TargetId, previousHealth, target.Health);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying healing to: {TargetId}", healing.TargetId);
                return false;
            }
        }

        /// <summary>
        /// Apply status effect to a combatant;
        /// </summary>
        public async Task<bool> ApplyStatusEffectAsync(StatusEffect effect)
        {
            if (effect == null)
                throw new ArgumentNullException(nameof(effect));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                _logger.LogDebug("Applying status effect: {EffectType} to {TargetId} from {SourceId}",
                    effect.EffectType, effect.TargetId, effect.SourceId);

                // Get target combatant;
                var target = await GetCombatantAsync(effect.TargetId);
                if (target == null)
                {
                    _logger.LogWarning("Target not found for status effect: {TargetId}", effect.TargetId);
                    return false;
                }

                // Apply status effect;
                effect.ApplicationTime = DateTime.UtcNow;
                target.StatusEffects.Add(effect);

                // Update blackboard;
                await _blackboard.SetValueAsync($"combatant.{target.Id}.statuseffects", target.StatusEffects);

                // Publish event;
                await _eventBus.PublishAsync(new StatusEffectAppliedEvent;
                {
                    StatusEffect = effect,
                    TargetId = target.Id,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Status effect applied: {EffectType} to {TargetId} for {Duration} seconds",
                    effect.EffectType, effect.TargetId, effect.Duration.TotalSeconds);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying status effect to: {TargetId}", effect.TargetId);
                return false;
            }
        }

        /// <summary>
        /// Get combat system metrics;
        /// </summary>
        public async Task<CombatMetrics> GetMetricsAsync()
        {
            if (!_isInitialized)
                return new CombatMetrics();

            try
            {
                return _metrics.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting combat metrics");
                return new CombatMetrics();
            }
        }

        /// <summary>
        /// Generate combat report;
        /// </summary>
        public async Task<CombatReport> GenerateCombatReportAsync(string combatId)
        {
            if (string.IsNullOrWhiteSpace(combatId))
                throw new ArgumentException("Combat ID cannot be empty", nameof(combatId));

            if (!_isInitialized)
                throw new InvalidOperationException("Combat System not initialized. Call InitializeAsync first.");

            try
            {
                // Get combat data from history;
                var combatData = await _history.GetCombatDataAsync(combatId);
                if (combatData == null)
                {
                    _logger.LogWarning("Combat data not found: {CombatId}", combatId);
                    return null;
                }

                // Generate report;
                var report = new CombatReport;
                {
                    CombatId = combatId,
                    GeneratedAt = DateTime.UtcNow,
                    Summary = await GenerateCombatSummaryAsync(combatData),
                    Participants = await GetCombatParticipantsDataAsync(combatData),
                    Timeline = await GenerateCombatTimelineAsync(combatData),
                    Statistics = await GenerateCombatStatisticsAsync(combatData),
                    Recommendations = await GenerateCombatRecommendationsAsync(combatData)
                };

                _logger.LogDebug("Combat report generated: {CombatId}", combatId);

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating combat report: {CombatId}", combatId);
                throw new CombatSystemException($"Failed to generate combat report for {combatId}", ex);
            }
        }

        /// <summary>
        /// Validate combat system state;
        /// </summary>
        public async Task<bool> ValidateCombatStateAsync()
        {
            if (!_isInitialized)
                return false;

            try
            {
                // Validate active combats;
                foreach (var combat in _activeCombats.Values)
                {
                    var combatValid = await ValidateCombatSessionAsync(combat);
                    if (!combatValid)
                    {
                        _logger.LogWarning("Combat validation failed: {CombatId}", combat.CombatId);
                        return false;
                    }
                }

                // Validate registered combatants;
                foreach (var combatant in _registeredCombatants.Values)
                {
                    var combatantValid = await ValidateCombatantStateAsync(combatant);
                    if (!combatantValid)
                    {
                        _logger.LogWarning("Combatant validation failed: {CombatantId}", combatant.Id);
                        return false;
                    }
                }

                // Validate subsystems;
                var subsystemsValid = await ValidateSubsystemsAsync();
                if (!subsystemsValid)
                {
                    _logger.LogWarning("Subsystem validation failed");
                    return false;
                }

                _logger.LogDebug("Combat system validation passed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during combat system validation");
                return false;
            }
        }

        /// <summary>
        /// Dispose combat system resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shutdownCts.Cancel();

                _combatUpdateTimer?.Dispose();
                _cleanupTimer?.Dispose();
                _initializationLock?.Dispose();
                _shutdownCts?.Dispose();

                // Clear all data;
                _activeCombats.Clear();
                _registeredCombatants.Clear();
                _aiProfiles.Clear();

                _logger.LogInformation("Combat System disposed: {SystemId}", _config?.SystemId);
            }
        }

        #region Private Methods;

        private async Task InitializeBlackboardAsync()
        {
            try
            {
                // Initialize combat-related blackboard entries;
                await _blackboard.SetValueAsync("combat.system.initialized", true);
                await _blackboard.SetValueAsync("combat.system.config", _config);
                await _blackboard.SetValueAsync("combat.system.metrics", _metrics);

                _logger.LogDebug("Blackboard initialized for combat system");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing blackboard");
                throw;
            }
        }

        private async Task InitializeBehaviorTreeAsync()
        {
            try
            {
                // Load behavior tree configuration for combat AI;
                var btConfig = new BehaviorTreeConfig;
                {
                    Id = "combat_ai_behavior_tree",
                    Name = "Combat AI Behavior Tree",
                    Version = "1.0.0",
                    Description = "Behavior tree for combat decision making",
                    RootNodeId = "combat_root"
                };

                await _behaviorTree.InitializeAsync(btConfig);

                _logger.LogDebug("Behavior tree initialized for combat AI");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing behavior tree");
                throw;
            }
        }

        private async Task LoadAIProfilesAsync(IEnumerable<string> profilePaths)
        {
            if (profilePaths == null)
                return;

            try
            {
                foreach (var profilePath in profilePaths)
                {
                    // In real implementation, load from file/database;
                    var profile = new CombatAIProfile;
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = Path.GetFileNameWithoutExtension(profilePath),
                        LoadedAt = DateTime.UtcNow;
                    };

                    _aiProfiles[profile.Id] = profile;

                    _logger.LogDebug("Loaded AI profile: {ProfileName}", profile.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading AI profiles");
                throw;
            }
        }

        private async Task InitializeSubsystemsAsync()
        {
            try
            {
                await _damageCalculator.InitializeAsync(_config.DamageConfig);
                await _threatManager.InitializeAsync(_config.ThreatConfig);
                await _positionTracker.InitializeAsync(_config.PositionConfig);
                await _abilityManager.InitializeAsync(_config.AbilityConfig);
                await _cooldownManager.InitializeAsync(_config.CooldownConfig);

                _logger.LogDebug("Combat subsystems initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing subsystems");
                throw;
            }
        }

        private async Task<CombatSession> GetOrCreateCombatSessionAsync(CombatContext context)
        {
            // Check for existing combat session;
            foreach (var combat in _activeCombats.Values)
            {
                if (combat.Participants.Any(p => p.Id == context.InitiatorId) ||
                    combat.Participants.Any(p => context.TargetIds.Contains(p.Id)))
                {
                    return combat;
                }
            }

            // Create new combat session;
            var combatId = $"combat_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            var scenario = new CombatScenario;
            {
                Name = $"Auto-generated combat for context {context.ContextId}",
                ParticipantIds = new List<string> { context.InitiatorId }.Concat(context.TargetIds).ToList(),
                Environment = context.Environment;
            };

            await StartCombatAsync(combatId, scenario);

            return _activeCombats[combatId];
        }

        private async Task<IEnumerable<CombatantResult>> ProcessCombatantTurnsAsync(
            CombatSession combatSession,
            CombatContext context,
            CancellationToken cancellationToken)
        {
            var results = new List<CombatantResult>();
            var activeCombatants = combatSession.Participants;
                .Where(p => p.State == CombatantState.InCombat)
                .OrderByDescending(p => p.Initiative) // Process by initiative;
                .ToList();

            foreach (var combatant in activeCombatants)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    // Make decision for combatant;
                    var decision = await MakeCombatDecisionAsync(combatant, context);

                    // Execute selected actions;
                    var actions = new List<CombatAction>();
                    foreach (var action in decision.SelectedActions)
                    {
                        var actionResult = await ExecuteCombatActionAsync(action, context);
                        actions.Add(action);

                        // Apply results;
                        if (actionResult.DamageInstances != null)
                        {
                            foreach (var damage in actionResult.DamageInstances)
                            {
                                await ApplyDamageAsync(damage);
                            }
                        }

                        if (actionResult.HealingInstances != null)
                        {
                            foreach (var healing in actionResult.HealingInstances)
                            {
                                await ApplyHealingAsync(healing);
                            }
                        }

                        if (actionResult.StatusEffects != null)
                        {
                            foreach (var effect in actionResult.StatusEffects)
                            {
                                await ApplyStatusEffectAsync(effect);
                            }
                        }
                    }

                    results.Add(new CombatantResult;
                    {
                        CombatantId = combatant.Id,
                        Decision = decision,
                        Actions = actions,
                        Success = decision.Success;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing turn for combatant: {CombatantId}", combatant.Id);
                }
            }

            return results;
        }

        private async Task<CombatEffects> ProcessCombatEffectsAsync(CombatSession combatSession, IEnumerable<CombatantResult> combatantResults)
        {
            var effects = new CombatEffects();

            // Process status effect updates;
            foreach (var combatant in combatSession.Participants)
            {
                await ProcessCombatantStatusEffectsAsync(combatant, effects);
            }

            // Process environmental effects;
            if (combatSession.Environment?.Hazards != null)
            {
                await ProcessEnvironmentalHazardsAsync(combatSession, effects);
            }

            return effects;
        }

        private async Task<CombatConclusion> CheckCombatConclusionAsync(CombatSession combatSession)
        {
            var conclusion = new CombatConclusion;
            {
                CombatId = combatSession.CombatId,
                CheckTime = DateTime.UtcNow;
            };

            // Check if all participants of one faction are dead/disabled;
            var factions = combatSession.Participants;
                .GroupBy(p => p.Faction)
                .ToDictionary(g => g.Key, g => g.ToList());

            var aliveFactions = factions.Where(f => f.Value.Any(p => p.State == CombatantState.InCombat)).ToList();

            if (aliveFactions.Count == 0)
            {
                // All participants dead - draw;
                conclusion.Result = CombatResultType.Draw;
                conclusion.IsConcluded = true;
            }
            else if (aliveFactions.Count == 1)
            {
                // One faction remains - victory;
                conclusion.Result = CombatResultType.Victory;
                conclusion.VictoriousFaction = aliveFactions[0].Key;
                conclusion.IsConcluded = true;
            }
            else;
            {
                // Multiple factions still alive - ongoing;
                conclusion.Result = CombatResultType.Ongoing;
                conclusion.IsConcluded = false;
            }

            // Check for timeout;
            if (!conclusion.IsConcluded &&
                combatSession.StartTime.Add(_config.MaxCombatDuration) < DateTime.UtcNow)
            {
                conclusion.Result = CombatResultType.Timeout;
                conclusion.IsConcluded = true;
                conclusion.TimeoutReason = "Maximum combat duration exceeded";
            }

            return conclusion;
        }

        private async Task<DecisionResult> MakeDecisionWithBehaviorTreeAsync(DecisionContext context)
        {
            // Create behavior tree context;
            var btContext = new BehaviorContext;
            {
                AgentId = context.Combatant.Id,
                Parameters = new Dictionary<string, object>
                {
                    ["combat_context"] = context.Context,
                    ["ai_profile"] = context.AIProfile,
                    ["available_actions"] = context.AvailableActions,
                    ["threat_levels"] = context.ThreatLevels,
                    ["position_data"] = context.PositionData,
                    ["cooldowns"] = context.Cooldowns;
                }
            };

            // Execute behavior tree;
            var result = await _behaviorTree.ExecuteAsync(btContext);

            // Convert behavior tree result to decision result;
            return new DecisionResult;
            {
                Success = result == BehaviorStatus.Success,
                DecisionType = DecisionType.BehaviorTree,
                SelectedActions = context.AvailableActions.Take(1).ToList(), // Simplified;
                Confidence = 0.8f,
                Reasoning = "Behavior tree decision"
            };
        }

        private async Task<DecisionResult> MakeDecisionWithUtilityAIAsync(DecisionContext context)
        {
            // Utility AI implementation;
            var scoredActions = new List<(CombatAction Action, float Score)>();

            foreach (var action in context.AvailableActions)
            {
                var score = await CalculateActionUtilityAsync(action, context);
                scoredActions.Add((action, score));
            }

            // Select best action;
            var bestAction = scoredActions.OrderByDescending(sa => sa.Score).FirstOrDefault();

            return new DecisionResult;
            {
                Success = bestAction.Action != null,
                DecisionType = DecisionType.UtilityAI,
                SelectedActions = bestAction.Action != null ? new List<CombatAction> { bestAction.Action } : new List<CombatAction>(),
                Confidence = bestAction.Score,
                Reasoning = $"Utility score: {bestAction.Score}"
            };
        }

        private async Task<ActionResult> ExecuteAttackActionAsync(CombatAction action, Combatant source, Combatant target, CombatContext context)
        {
            // Calculate damage;
            var damage = await _damageCalculator.CalculateAttackDamageAsync(source, target, action);

            // Check hit/miss/critical;
            var attackResult = await _damageCalculator.ResolveAttackAsync(source, target, damage);

            var result = new ActionResult;
            {
                Success = attackResult.Hit,
                ActionType = ActionType.Attack,
                DamageInstances = attackResult.Hit ? new List<DamageInstance> { damage } : null,
                CriticalHit = attackResult.Critical,
                HitResult = attackResult.HitResult;
            };

            return result;
        }

        private async Task<float> CalculateActionUtilityAsync(CombatAction action, DecisionContext context)
        {
            // Calculate utility score for an action;
            float score = 0.0f;

            // Damage potential;
            if (action.ExpectedDamage > 0)
            {
                score += action.ExpectedDamage * _config.UtilityWeights.DamageWeight;
            }

            // Healing potential;
            if (action.ExpectedHealing > 0)
            {
                score += action.ExpectedHealing * _config.UtilityWeights.HealingWeight;
            }

            // Threat management;
            var threatScore = await CalculateThreatUtilityAsync(action, context);
            score += threatScore * _config.UtilityWeights.ThreatWeight;

            // Position advantage;
            var positionScore = await CalculatePositionUtilityAsync(action, context);
            score += positionScore * _config.UtilityWeights.PositionWeight;

            // Resource efficiency;
            if (action.ResourceCost > 0)
            {
                score -= action.ResourceCost * _config.UtilityWeights.ResourceWeight;
            }

            // Cooldown consideration;
            if (action.CooldownDuration > TimeSpan.Zero)
            {
                score -= (float)action.CooldownDuration.TotalSeconds * _config.UtilityWeights.CooldownWeight;
            }

            return Math.Max(0, score);
        }

        // Additional private methods for validation, cleanup, etc.
        private async Task<bool> ValidateCombatContextAsync(CombatContext context) => await Task.FromResult(true);
        private async Task<bool> ValidateCombatantAsync(Combatant combatant) => await Task.FromResult(true);
        private async Task<bool> ValidateCombatScenarioAsync(CombatScenario scenario) => await Task.FromResult(true);
        private async Task<bool> ValidateCombatActionAsync(CombatAction action, CombatContext context) => await Task.FromResult(true);
        private async Task<bool> ValidateCombatSessionAsync(CombatSession session) => await Task.FromResult(true);
        private async Task<bool> ValidateCombatantStateAsync(Combatant combatant) => await Task.FromResult(true);
        private async Task<bool> ValidateSubsystemsAsync() => await Task.FromResult(true);

        private async Task<CombatAIProfile> GetAIProfileForCombatantAsync(Combatant combatant) => await Task.FromResult(new CombatAIProfile());
        private async Task<IEnumerable<CombatAction>> GetAvailableActionsAsync(Combatant combatant) => await Task.FromResult(Enumerable.Empty<CombatAction>());
        private async Task InitializeCombatSessionAsync(CombatSession session) => await Task.CompletedTask;
        private async Task ConcludeCombatAsync(CombatSession session, CombatConclusion conclusion) => await Task.CompletedTask;
        private async Task UpdateThreatLevelsAsync(CombatSession session, CombatEffects effects) => await Task.CompletedTask;
        private async Task ProcessCombatUpdatesAsync() => await Task.CompletedTask;
        private async Task PerformCleanupAsync() => await Task.CompletedTask;
        private async Task<float> CalculateThreatUtilityAsync(CombatAction action, DecisionContext context) => await Task.FromResult(0.5f);
        private async Task<float> CalculatePositionUtilityAsync(CombatAction action, DecisionContext context) => await Task.FromResult(0.5f);
        private async Task ProcessCombatantStatusEffectsAsync(Combatant combatant, CombatEffects effects) => await Task.CompletedTask;
        private async Task ProcessEnvironmentalHazardsAsync(CombatSession session, CombatEffects effects) => await Task.CompletedTask;
        private async Task<CombatSummary> GenerateCombatSummaryAsync(object combatData) => await Task.FromResult(new CombatSummary());
        private async Task<List<CombatParticipantData>> GetCombatParticipantsDataAsync(object combatData) => await Task.FromResult(new List<CombatParticipantData>());
        private async Task<CombatTimeline> GenerateCombatTimelineAsync(object combatData) => await Task.FromResult(new CombatTimeline());
        private async Task<CombatStatistics> GenerateCombatStatisticsAsync(object combatData) => await Task.FromResult(new CombatStatistics());
        private async Task<List<string>> GenerateCombatRecommendationsAsync(object combatData) => await Task.FromResult(new List<string>());

        private async Task<DecisionResult> MakeDecisionWithStateMachineAsync(DecisionContext context) => await Task.FromResult(new DecisionResult());
        private async Task<DecisionResult> MakeDecisionWithRuleBasedAIAsync(DecisionContext context) => await Task.FromResult(new DecisionResult());
        private async Task<DecisionResult> MakeDecisionWithBalancedAIAsync(DecisionContext context) => await Task.FromResult(new DecisionResult());
        private async Task<ActionResult> ExecuteAbilityActionAsync(CombatAction action, Combatant source, Combatant target, CombatContext context) => await Task.FromResult(new ActionResult());
        private async Task<ActionResult> ExecuteMoveActionAsync(CombatAction action, Combatant source, CombatContext context) => await Task.FromResult(new ActionResult());
        private async Task<ActionResult> ExecuteDefendActionAsync(CombatAction action, Combatant source, CombatContext context) => await Task.FromResult(new ActionResult());
        private async Task<ActionResult> ExecuteUseItemActionAsync(CombatAction action, Combatant source, Combatant target, CombatContext context) => await Task.FromResult(new ActionResult());

        #endregion;

        #region Supporting Classes and Enums;

        public enum CombatResultType;
        {
            Ongoing,
            Victory,
            Defeat,
            Draw,
            Timeout,
            InvalidContext,
            Error;
        }

        public enum CombatantState;
        {
            Idle,
            InCombat,
            Dead,
            Disabled,
            Fleeing,
            Stunned;
        }

        public enum CombatSessionState;
        {
            Pending,
            Active,
            Paused,
            Concluded,
            Cancelled;
        }

        public enum ActionType;
        {
            Attack,
            Ability,
            Move,
            Defend,
            UseItem,
            Flee,
            Wait;
        }

        public enum DecisionType;
        {
            BehaviorTree,
            UtilityAI,
            StateMachine,
            RuleBased,
            Random,
            Error;
        }

        public enum DecisionStrategy;
        {
            BehaviorTree,
            UtilityAI,
            StateMachine,
            RuleBased,
            Balanced,
            Aggressive,
            Defensive;
        }

        public enum DamageType;
        {
            Physical,
            Fire,
            Cold,
            Lightning,
            Poison,
            Arcane,
            Holy,
            Shadow;
        }

        public enum HitResult;
        {
            Miss,
            Hit,
            Critical,
            Dodged,
            Parried,
            Blocked,
            Resisted,
            Immune;
        }

        public enum StatusEffectType;
        {
            Stun,
            Slow,
            Root,
            Silence,
            Disarm,
            Bleed,
            Poison,
            Burn,
            Freeze,
            Buff,
            Debuff;
        }

        public class CombatSystemConfig;
        {
            public string SystemId { get; set; } = Guid.NewGuid().ToString();
            public string Name { get; set; } = "Default Combat System";
            public string Version { get; set; } = "1.0.0";
            public bool EnableAutoUpdates { get; set; } = true;
            public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(0.1);
            public bool EnableAutoCleanup { get; set; } = true;
            public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
            public TimeSpan MaxCombatDuration { get; set; } = TimeSpan.FromMinutes(10);
            public float ThreatDamageMultiplier { get; set; } = 1.0f;
            public float ThreatHealingMultiplier { get; set; } = 0.5f;
            public List<string> AIProfilePaths { get; set; } = new List<string>();
            public DamageConfig DamageConfig { get; set; } = new DamageConfig();
            public ThreatConfig ThreatConfig { get; set; } = new ThreatConfig();
            public PositionConfig PositionConfig { get; set; } = new PositionConfig();
            public AbilityConfig AbilityConfig { get; set; } = new AbilityConfig();
            public CooldownConfig CooldownConfig { get; set; } = new CooldownConfig();
            public UtilityWeights UtilityWeights { get; set; } = new UtilityWeights();
        }

        public class CombatContext;
        {
            public string ContextId { get; set; } = Guid.NewGuid().ToString();
            public string InitiatorId { get; set; }
            public List<string> TargetIds { get; set; } = new List<string>();
            public CombatEnvironment Environment { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }

        public class Combatant;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Faction { get; set; }
            public int Level { get; set; } = 1;
            public float Health { get; set; } = 100;
            public float MaxHealth { get; set; } = 100;
            public float Initiative { get; set; } = 10;
            public CombatantState State { get; set; }
            public Vector3 Position { get; set; }
            public Quaternion Rotation { get; set; }
            public List<StatusEffect> StatusEffects { get; set; } = new List<StatusEffect>();
            public Dictionary<string, float> Resistances { get; set; } = new Dictionary<string, float>();
            public DateTime RegisteredAt { get; set; }
            public DateTime? DeathTime { get; set; }
            public CombatAIProfile AIProfile { get; set; }
        }

        public class CombatSession;
        {
            public string CombatId { get; set; }
            public CombatScenario Scenario { get; set; }
            public CombatSessionState State { get; set; }
            public List<Combatant> Participants { get; set; }
            public CombatEnvironment Environment { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public CombatConclusion Conclusion { get; set; }
            public DateTime LastUpdateTime { get; set; }
            public int UpdateCount { get; set; }

            public void RemoveParticipant(string combatantId)
            {
                var participant = Participants.FirstOrDefault(p => p.Id == combatantId);
                if (participant != null)
                {
                    participant.State = CombatantState.Idle;
                    Participants.Remove(participant);
                }
            }
        }

        public class CombatAction;
        {
            public string ActionId { get; set; } = Guid.NewGuid().ToString();
            public ActionType ActionType { get; set; }
            public string SourceId { get; set; }
            public string TargetId { get; set; }
            public string AbilityId { get; set; }
            public bool RequiresTarget { get; set; }
            public TimeSpan CooldownDuration { get; set; }
            public float ResourceCost { get; set; }
            public float ExpectedDamage { get; set; }
            public float ExpectedHealing { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        }

        public class DamageInstance;
        {
            public string DamageId { get; set; } = Guid.NewGuid().ToString();
            public string SourceId { get; set; }
            public string TargetId { get; set; }
            public float Amount { get; set; }
            public DamageType DamageType { get; set; }
            public bool IsCritical { get; set; }
            public bool CanBeResisted { get; set; } = true;
            public Dictionary<string, object> Modifiers { get; set; } = new Dictionary<string, object>();
        }

        public class HealingInstance;
        {
            public string HealingId { get; set; } = Guid.NewGuid().ToString();
            public string SourceId { get; set; }
            public string TargetId { get; set; }
            public float Amount { get; set; }
            public bool IsCritical { get; set; }
            public Dictionary<string, object> Modifiers { get; set; } = new Dictionary<string, object>();
        }

        public class StatusEffect;
        {
            public string EffectId { get; set; } = Guid.NewGuid().ToString();
            public StatusEffectType EffectType { get; set; }
            public string SourceId { get; set; }
            public string TargetId { get; set; }
            public TimeSpan Duration { get; set; }
            public DateTime ApplicationTime { get; set; }
            public Dictionary<string, float> Modifiers { get; set; } = new Dictionary<string, float>();
            public List<EffectTrigger> Triggers { get; set; } = new List<EffectTrigger>();
        }

        public class CombatResult;
        {
            public string ContextId { get; set; }
            public CombatResultType Result { get; set; }
            public List<CombatAction> CombatActions { get; set; }
            public List<DamageInstance> DamageInstances { get; set; }
            public List<HealingInstance> HealingInstances { get; set; }
            public List<StatusEffect> StatusEffects { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class DecisionResult;
        {
            public bool Success { get; set; }
            public DecisionType DecisionType { get; set; }
            public List<CombatAction> SelectedActions { get; set; }
            public float Confidence { get; set; }
            public string Reasoning { get; set; }
            public TimeSpan DecisionTime { get; set; }
            public DateTime Timestamp { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class ActionResult;
        {
            public bool Success { get; set; }
            public ActionType ActionType { get; set; }
            public List<DamageInstance> DamageInstances { get; set; }
            public List<HealingInstance> HealingInstances { get; set; }
            public List<StatusEffect> StatusEffects { get; set; }
            public float DamageDealt { get; set; }
            public float HealingDone { get; set; }
            public bool CriticalHit { get; set; }
            public HitResult HitResult { get; set; }
            public TimeSpan ExecutionTime { get; set; }
            public DateTime Timestamp { get; set; }
            public string ErrorMessage { get; set; }
        }

        public class CombatReport;
        {
            public string CombatId { get; set; }
            public DateTime GeneratedAt { get; set; }
            public CombatSummary Summary { get; set; }
            public List<CombatParticipantData> Participants { get; set; }
            public CombatTimeline Timeline { get; set; }
            public CombatStatistics Statistics { get; set; }
            public List<string> Recommendations { get; set; }
        }

        // Additional supporting classes...
        public class CombatAIProfile { }
        public class CombatScenario { }
        public class CombatEnvironment { }
        public class CombatConclusion { }
        public class CombatState { }
        public class DecisionContext { }
        public class CombatEffects { }
        public class CombatantResult { }
        public class CombatMetrics { }
        public class CombatHistory { }
        public class DamageCalculator { }
        public class ThreatManager { }
        public class PositionTracker { }
        public class AbilityManager { }
        public class CooldownManager { }
        public class IPathfindingService { }
        public class ILOSService { }
        public class DamageConfig { }
        public class ThreatConfig { }
        public class PositionConfig { }
        public class AbilityConfig { }
        public class CooldownConfig { }
        public class UtilityWeights { }
        public class CombatSummary { }
        public class CombatParticipantData { }
        public class CombatTimeline { }
        public class CombatStatistics { }
        public class EffectTrigger { }
        public class FinalDamage { }
        public class FinalHealing { }
        public class AttackResolution { }

        #endregion;

        #region Events;

        public class CombatSystemInitializedEvent : IEvent;
        {
            public string SystemId { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class CombatExecutedEvent : IEvent;
        {
            public string CombatId { get; set; }
            public string ContextId { get; set; }
            public CombatResultType Result { get; set; }
            public TimeSpan Duration { get; set; }
            public int ParticipantCount { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class CombatantRegisteredEvent : IEvent;
        {
            public string CombatantId { get; set; }
            public string Name { get; set; }
            public string Faction { get; set; }
            public int Level { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class CombatantUnregisteredEvent : IEvent;
        {
            public string CombatantId { get; set; }
            public string Name { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class CombatStartedEvent : IEvent;
        {
            public string CombatId { get; set; }
            public string ScenarioName { get; set; }
            public int ParticipantCount { get; set; }
            public string Environment { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class CombatEndedEvent : IEvent;
        {
            public string CombatId { get; set; }
            public CombatResultType Conclusion { get; set; }
            public TimeSpan Duration { get; set; }
            public int ParticipantCount { get; set; }
            public string VictoriousFaction { get; set; }
            public CombatReport Report { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class CombatDecisionMadeEvent : IEvent;
        {
            public string CombatantId { get; set; }
            public DecisionType DecisionType { get; set; }
            public string SelectedAction { get; set; }
            public float Confidence { get; set; }
            public TimeSpan DecisionTime { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class CombatActionExecutedEvent : IEvent;
        {
            public string ActionId { get; set; }
            public ActionType ActionType { get; set; }
            public string SourceId { get; set; }
            public string TargetId { get; set; }
            public bool Success { get; set; }
            public float DamageDealt { get; set; }
            public float HealingDone { get; set; }
            public TimeSpan ExecutionTime { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class DamageAppliedEvent : IEvent;
        {
            public DamageInstance DamageInstance { get; set; }
            public FinalDamage FinalDamage { get; set; }
            public float TargetHealthBefore { get; set; }
            public float TargetHealthAfter { get; set; }
            public CombatantState TargetState { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class HealingAppliedEvent : IEvent;
        {
            public HealingInstance HealingInstance { get; set; }
            public FinalHealing FinalHealing { get; set; }
            public float TargetHealthBefore { get; set; }
            public float TargetHealthAfter { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class StatusEffectAppliedEvent : IEvent;
        {
            public StatusEffect StatusEffect { get; set; }
            public string TargetId { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        #endregion;
    }

    #region Exceptions;

    public class CombatSystemException : Exception
    {
        public CombatSystemException() { }
        public CombatSystemException(string message) : base(message) { }
        public CombatSystemException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
