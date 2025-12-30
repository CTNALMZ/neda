using Microsoft.Extensions.Logging;
using NEDA.Animation.SequenceEditor.TimelineManagement;
using NEDA.API.Middleware;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.Animation.CharacterAnimation.AnimationBlueprints.StateMachines;
{
    /// <summary>
    /// State Machine sistemini yöneten ana sınıf;
    /// Karakter animasyonları, gameplay durumları ve AI davranışları için kullanılır;
    /// </summary>
    public interface IStateMachine : IDisposable
    {
        /// <summary>
        /// State Machine ID;
        /// </summary>
        Guid MachineId { get; }

        /// <summary>
        /// State Machine adı;
        /// </summary>
        string MachineName { get; set; }

        /// <summary>
        /// Mevcut aktif state;
        /// </summary>
        IState CurrentState { get; }

        /// <summary>
        /// Mevcut aktif state'in ID'si;
        /// </summary>
        string CurrentStateId { get; }

        /// <summary>
        /// Önceki state;
        /// </summary>
        IState PreviousState { get; }

        /// <summary>
        /// State Machine'ın tüm state'leri;
        /// </summary>
        IReadOnlyDictionary<string, IState> States { get; }

        /// <summary>
        /// State Machine'ın tüm transition'ları;
        /// </summary>
        IReadOnlyDictionary<string, ITransition> Transitions { get; }

        /// <summary>
        /// State Machine çalışıyor mu?
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// State Machine debug modunda mı?
        /// </summary>
        bool IsDebugMode { get; set; }

        /// <summary>
        /// State Machine'ı başlat;
        /// </summary>
        Task<bool> InitializeAsync(StateMachineConfiguration configuration);

        /// <summary>
        /// State Machine'ı başlat;
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// State Machine'ı durdur;
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// State Machine'ı güncelle;
        /// </summary>
        Task UpdateAsync(float deltaTime);

        /// <summary>
        /// State Machine'ı sabit framerate ile güncelle;
        /// </summary>
        Task FixedUpdateAsync(float fixedDeltaTime);

        /// <summary>
        /// State ekle;
        /// </summary>
        Task<IState> AddStateAsync(string stateId, StateConfiguration configuration);

        /// <summary>
        /// State kaldır;
        /// </summary>
        Task<bool> RemoveStateAsync(string stateId);

        /// <summary>
        /// Transition ekle;
        /// </summary>
        Task<ITransition> AddTransitionAsync(string transitionId, TransitionConfiguration configuration);

        /// <summary>
        /// Transition kaldır;
        /// </summary>
        Task<bool> RemoveTransitionAsync(string transitionId);

        /// <summary>
        /// State'e geçiş yap;
        /// </summary>
        Task<bool> TransitionToStateAsync(string targetStateId, object transitionData = null);

        /// <summary>
        /// Trigger event gönder;
        /// </summary>
        Task<bool> SendTriggerAsync(string triggerName, object triggerData = null);

        /// <summary>
        /// Parametre değerini ayarla;
        /// </summary>
        Task SetParameterAsync(string parameterName, object value);

        /// <summary>
        /// Parametre değerini al;
        /// </summary>
        Task<object> GetParameterAsync(string parameterName);

        /// <summary>
        /// State Machine'ı kaydet;
        /// </summary>
        Task<bool> SaveMachineAsync(string filePath);

        /// <summary>
        /// State Machine'ı yükle;
        /// </summary>
        Task<bool> LoadMachineAsync(string filePath);

        /// <summary>
        /// State Machine'ı klonla;
        /// </summary>
        Task<IStateMachine> CloneAsync(string newMachineName = null);

        /// <summary>
        /// State Machine event'ine abone ol;
        /// </summary>
        void SubscribeToMachineEvent(StateMachineEventType eventType, Action<StateMachineEventArgs> handler);

        /// <summary>
        /// State Machine event'inden abonelikten çık;
        /// </summary>
        void UnsubscribeFromMachineEvent(StateMachineEventType eventType, Action<StateMachineEventArgs> handler);

        /// <summary>
        /// Debug bilgilerini al;
        /// </summary>
        StateMachineDebugInfo GetDebugInfo();

        /// <summary>
        /// Performans metriklerini al;
        /// </summary>
        StateMachinePerformanceMetrics GetPerformanceMetrics();
    }

    /// <summary>
    /// State interface;
    /// </summary>
    public interface IState;
    {
        string StateId { get; }
        string StateName { get; }
        StateType Type { get; }
        bool IsActive { get; }
        float TimeInState { get; }
        int UpdateCount { get; }
        IReadOnlyDictionary<string, object> Parameters { get; }
        IReadOnlyList<string> OutgoingTransitions { get; }

        Task<bool> OnEnterAsync(object enterData);
        Task<bool> OnUpdateAsync(float deltaTime);
        Task<bool> OnFixedUpdateAsync(float fixedDeltaTime);
        Task<bool> OnExitAsync(object exitData);
        Task<bool> AddTransitionAsync(string transitionId);
        Task<bool> RemoveTransitionAsync(string transitionId);
        Task<bool> SetParameterAsync(string parameterName, object value);
        Task<object> GetParameterAsync(string parameterName);
    }

    /// <summary>
    /// Transition interface;
    /// </summary>
    public interface ITransition;
    {
        string TransitionId { get; }
        string TransitionName { get; }
        string SourceStateId { get; }
        string TargetStateId { get; }
        TransitionType Type { get; }
        TransitionEvaluationMode EvaluationMode { get; }
        bool IsEnabled { get; set; }
        float CoolDownTime { get; set; }
        float LastTransitionTime { get; }

        Task<bool> EvaluateAsync(TransitionEvaluationContext context);
        Task<bool> CanTransitionAsync(TransitionEvaluationContext context);
        Task<bool> ExecuteTransitionAsync(object transitionData);
        void AddCondition(ITransitionCondition condition);
        void RemoveCondition(string conditionId);
        IReadOnlyList<ITransitionCondition> GetConditions();
    }

    /// <summary>
    /// Transition condition interface;
    /// </summary>
    public interface ITransitionCondition;
    {
        string ConditionId { get; }
        string ConditionName { get; }
        ConditionType Type { get; }
        ConditionOperator Operator { get; }
        string ParameterName { get; }
        object ExpectedValue { get; }
        float Tolerance { get; }

        Task<bool> EvaluateAsync(ConditionEvaluationContext context);
    }

    /// <summary>
    /// State Machine ana implementasyonu;
    /// </summary>
    public class StateMachine : IStateMachine;
    {
        private readonly ILogger<StateMachine> _logger;
        private readonly IEventBus _eventBus;
        private readonly IErrorReporter _errorReporter;
        private readonly object _syncLock = new object();

        private Guid _machineId;
        private string _machineName;
        private IState _currentState;
        private IState _previousState;
        private bool _isRunning;
        private bool _isDebugMode;
        private bool _isInitialized;
        private StateMachineConfiguration _configuration;

        private readonly Dictionary<string, IState> _states = new Dictionary<string, IState>();
        private readonly Dictionary<string, ITransition> _transitions = new Dictionary<string, ITransition>();
        private readonly Dictionary<string, object> _globalParameters = new Dictionary<string, object>();
        private readonly Dictionary<StateMachineEventType, List<Action<StateMachineEventArgs>>> _eventHandlers =
            new Dictionary<StateMachineEventType, List<Action<StateMachineEventArgs>>>();

        // Performance tracking;
        private readonly PerformanceTracker _performanceTracker = new PerformanceTracker();
        private DateTime _startTime;
        private int _totalUpdates;
        private int _totalTransitions;
        private float _totalRunTime;

        // Update loop;
        private Task _updateTask;
        private CancellationTokenSource _updateCancellationTokenSource;
        private float _updateInterval = 0.016f; // ~60 FPS;
        private bool _isDisposed;

        /// <summary>
        /// StateMachine constructor;
        /// </summary>
        public StateMachine(
            ILogger<StateMachine> logger,
            IEventBus eventBus,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            _machineId = Guid.NewGuid();
            _machineName = $"StateMachine_{_machineId.ToString("N").Substring(0, 8)}";
            _isRunning = false;
            _isDebugMode = false;
            _isInitialized = false;

            // Default global parameters;
            _globalParameters["Speed"] = 1.0f;
            _globalParameters["IsAlive"] = true;
            _globalParameters["Health"] = 100.0f;
            _globalParameters["Stamina"] = 100.0f;

            _logger.LogInformation("StateMachine initialized with ID: {MachineId}", _machineId);
        }

        public Guid MachineId => _machineId;

        public string MachineName;
        {
            get => _machineName;
            set;
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Machine name cannot be null or empty", nameof(value));

                lock (_syncLock)
                {
                    var oldName = _machineName;
                    _machineName = value;
                    _logger.LogDebug("StateMachine name changed from '{OldName}' to '{NewName}'", oldName, value);

                    RaiseMachineEvent(StateMachineEventType.MachineRenamed,
                        new StateMachineEventArgs;
                        {
                            MachineId = _machineId,
                            OldName = oldName,
                            NewName = value;
                        });
                }
            }
        }

        public IState CurrentState => _currentState;

        public string CurrentStateId => _currentState?.StateId;

        public IState PreviousState => _previousState;

        public IReadOnlyDictionary<string, IState> States;
        {
            get;
            {
                lock (_syncLock)
                {
                    return new Dictionary<string, IState>(_states);
                }
            }
        }

        public IReadOnlyDictionary<string, ITransition> Transitions;
        {
            get;
            {
                lock (_syncLock)
                {
                    return new Dictionary<string, ITransition>(_transitions);
                }
            }
        }

        public bool IsRunning => _isRunning;

        public bool IsDebugMode;
        {
            get => _isDebugMode;
            set;
            {
                lock (_syncLock)
                {
                    _isDebugMode = value;
                    _logger.LogDebug("Debug mode {Status}", value ? "enabled" : "disabled");
                }
            }
        }

        public async Task<bool> InitializeAsync(StateMachineConfiguration configuration)
        {
            try
            {
                using (_performanceTracker.TrackOperation("Initialize"))
                {
                    _logger.LogInformation("Initializing StateMachine with configuration");

                    if (configuration == null)
                        throw new ArgumentNullException(nameof(configuration));

                    lock (_syncLock)
                    {
                        if (_isInitialized)
                        {
                            _logger.LogWarning("StateMachine is already initialized");
                            return true;
                        }

                        _configuration = configuration;
                        _machineName = configuration.MachineName;
                        _isDebugMode = configuration.EnableDebugMode;

                        // Default state'leri oluştur;
                        if (configuration.CreateDefaultStates)
                        {
                            CreateDefaultStates();
                        }

                        // Initial state'i ayarla;
                        if (!string.IsNullOrEmpty(configuration.InitialStateId))
                        {
                            if (_states.ContainsKey(configuration.InitialStateId))
                            {
                                var initialState = _states[configuration.InitialStateId];
                                _currentState = initialState;

                                _logger.LogInformation("Initial state set to: {StateId}", configuration.InitialStateId);
                            }
                            else;
                            {
                                _logger.LogWarning("Initial state '{StateId}' not found, using first available state",
                                    configuration.InitialStateId);
                                _currentState = _states.Values.FirstOrDefault();
                            }
                        }

                        _isInitialized = true;
                    }

                    // Event bus'a kayıt ol;
                    await _eventBus.SubscribeAsync<StateMachineEvent>(HandleStateMachineEvent);

                    _logger.LogInformation("StateMachine initialized successfully. States: {StateCount}, Transitions: {TransitionCount}",
                        _states.Count, _transitions.Count);

                    RaiseMachineEvent(StateMachineEventType.MachineInitialized,
                        new StateMachineEventArgs { MachineId = _machineId });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize StateMachine");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineInitializationFailed);
                throw new StateMachineException("Failed to initialize StateMachine", ex, ErrorCodes.StateMachineInitializationFailed);
            }
        }

        public async Task StartAsync()
        {
            try
            {
                using (_performanceTracker.TrackOperation("Start"))
                {
                    lock (_syncLock)
                    {
                        if (_isRunning)
                        {
                            _logger.LogWarning("StateMachine is already running");
                            return;
                        }

                        if (!_isInitialized)
                        {
                            throw new StateMachineException("StateMachine must be initialized before starting",
                                ErrorCodes.StateMachineNotInitialized);
                        }

                        if (_currentState == null && _states.Count > 0)
                        {
                            _currentState = _states.Values.First();
                            _logger.LogWarning("No current state set, using first state: {StateId}", _currentState.StateId);
                        }

                        _isRunning = true;
                        _startTime = DateTime.UtcNow;
                        _updateCancellationTokenSource = new CancellationTokenSource();
                    }

                    _logger.LogInformation("Starting StateMachine: {MachineName}", _machineName);

                    // Initial state'e giriş yap;
                    if (_currentState != null)
                    {
                        await _currentState.OnEnterAsync(null);

                        _logger.LogDebug("Entered initial state: {StateId}", _currentState.StateId);
                    }

                    // Update loop'u başlat;
                    _updateTask = Task.Run(() => UpdateLoopAsync(_updateCancellationTokenSource.Token));

                    RaiseMachineEvent(StateMachineEventType.MachineStarted,
                        new StateMachineEventArgs;
                        {
                            MachineId = _machineId,
                            CurrentStateId = _currentState?.StateId;
                        });

                    await _eventBus.PublishAsync(new StateMachineEvent;
                    {
                        EventType = StateMachineEventType.MachineStarted.ToString(),
                        MachineId = _machineId,
                        Timestamp = DateTime.UtcNow,
                        Data = new { CurrentStateId = _currentState?.StateId }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start StateMachine");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineStartFailed);
                throw new StateMachineException("Failed to start StateMachine", ex, ErrorCodes.StateMachineStartFailed);
            }
        }

        public async Task StopAsync()
        {
            try
            {
                using (_performanceTracker.TrackOperation("Stop"))
                {
                    lock (_syncLock)
                    {
                        if (!_isRunning)
                        {
                            _logger.LogWarning("StateMachine is not running");
                            return;
                        }

                        _isRunning = false;

                        // Update loop'u durdur;
                        _updateCancellationTokenSource?.Cancel();
                    }

                    _logger.LogInformation("Stopping StateMachine: {MachineName}", _machineName);

                    // Current state'ten çıkış yap;
                    if (_currentState != null)
                    {
                        await _currentState.OnExitAsync(null);
                        _logger.LogDebug("Exited current state: {StateId}", _currentState.StateId);
                    }

                    // Update task'ının tamamlanmasını bekle;
                    if (_updateTask != null && !_updateTask.IsCompleted)
                    {
                        await _updateTask;
                    }

                    RaiseMachineEvent(StateMachineEventType.MachineStopped,
                        new StateMachineEventArgs;
                        {
                            MachineId = _machineId,
                            PreviousStateId = _currentState?.StateId;
                        });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop StateMachine");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineStopFailed);
                throw new StateMachineException("Failed to stop StateMachine", ex, ErrorCodes.StateMachineStopFailed);
            }
        }

        public async Task UpdateAsync(float deltaTime)
        {
            try
            {
                if (!_isRunning || _currentState == null)
                    return;

                using (_performanceTracker.TrackOperation("Update"))
                {
                    _totalUpdates++;
                    _totalRunTime += deltaTime;

                    // Current state'i güncelle;
                    await _currentState.OnUpdateAsync(deltaTime);

                    // Transitions'ları değerlendir;
                    await EvaluateTransitionsAsync(deltaTime);

                    // Performance tracking;
                    _performanceTracker.RecordUpdate(deltaTime);

                    if (_isDebugMode && _totalUpdates % 100 == 0)
                    {
                        _logger.LogDebug("StateMachine update #{UpdateCount}, DeltaTime: {DeltaTime}, State: {StateId}",
                            _totalUpdates, deltaTime, _currentState.StateId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during StateMachine update");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineUpdateError);

                RaiseMachineEvent(StateMachineEventType.MachineError,
                    new StateMachineEventArgs;
                    {
                        MachineId = _machineId,
                        ErrorMessage = ex.Message,
                        CurrentStateId = _currentState?.StateId;
                    });
            }
        }

        public async Task FixedUpdateAsync(float fixedDeltaTime)
        {
            try
            {
                if (!_isRunning || _currentState == null)
                    return;

                using (_performanceTracker.TrackOperation("FixedUpdate"))
                {
                    // Current state'i fixed update;
                    await _currentState.OnFixedUpdateAsync(fixedDeltaTime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during StateMachine fixed update");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineUpdateError);
            }
        }

        public async Task<IState> AddStateAsync(string stateId, StateConfiguration configuration)
        {
            try
            {
                using (_performanceTracker.TrackOperation("AddState"))
                {
                    if (string.IsNullOrWhiteSpace(stateId))
                        throw new ArgumentException("State ID cannot be null or empty", nameof(stateId));

                    if (configuration == null)
                        throw new ArgumentNullException(nameof(configuration));

                    lock (_syncLock)
                    {
                        // State ID benzersiz olmalı;
                        if (_states.ContainsKey(stateId))
                        {
                            throw new StateMachineException($"State with ID '{stateId}' already exists",
                                ErrorCodes.StateAlreadyExists);
                        }

                        var state = new State(stateId, configuration, _logger);
                        _states[stateId] = state;

                        _logger.LogInformation("Added state: {StateName} ({StateId})", configuration.StateName, stateId);

                        // Eğer ilk state ise ve current state yoksa, bunu current state yap;
                        if (_currentState == null)
                        {
                            _currentState = state;
                            _logger.LogDebug("Set as current state: {StateId}", stateId);
                        }

                        RaiseMachineEvent(StateMachineEventType.StateAdded,
                            new StateMachineEventArgs;
                            {
                                MachineId = _machineId,
                                StateId = stateId,
                                StateName = configuration.StateName,
                                StateType = configuration.StateType;
                            });

                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add state: {StateId}", stateId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateOperationFailed);
                throw new StateMachineException("Failed to add state", ex, ErrorCodes.StateOperationFailed);
            }
        }

        public async Task<bool> RemoveStateAsync(string stateId)
        {
            try
            {
                using (_performanceTracker.TrackOperation("RemoveState"))
                {
                    lock (_syncLock)
                    {
                        if (!_states.ContainsKey(stateId))
                        {
                            _logger.LogWarning("State not found: {StateId}", stateId);
                            return false;
                        }

                        var state = _states[stateId];

                        // Current state kaldırılamaz;
                        if (_currentState?.StateId == stateId)
                        {
                            throw new StateMachineException("Cannot remove current state", ErrorCodes.CannotRemoveCurrentState);
                        }

                        // State'e bağlı transition'ları bul ve kaldır;
                        var transitionsToRemove = _transitions.Values;
                            .Where(t => t.SourceStateId == stateId || t.TargetStateId == stateId)
                            .Select(t => t.TransitionId)
                            .ToList();

                        foreach (var transitionId in transitionsToRemove)
                        {
                            _transitions.Remove(transitionId);
                        }

                        // State'i kaldır;
                        _states.Remove(stateId);

                        _logger.LogInformation("Removed state: {StateId} and {TransitionCount} related transitions",
                            stateId, transitionsToRemove.Count);

                        RaiseMachineEvent(StateMachineEventType.StateRemoved,
                            new StateMachineEventArgs;
                            {
                                MachineId = _machineId,
                                StateId = stateId,
                                StateName = state.StateName;
                            });

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove state: {StateId}", stateId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateOperationFailed);
                throw new StateMachineException("Failed to remove state", ex, ErrorCodes.StateOperationFailed);
            }
        }

        public async Task<ITransition> AddTransitionAsync(string transitionId, TransitionConfiguration configuration)
        {
            try
            {
                using (_performanceTracker.TrackOperation("AddTransition"))
                {
                    if (string.IsNullOrWhiteSpace(transitionId))
                        throw new ArgumentException("Transition ID cannot be null or empty", nameof(transitionId));

                    if (configuration == null)
                        throw new ArgumentNullException(nameof(configuration));

                    lock (_syncLock)
                    {
                        // Transition ID benzersiz olmalı;
                        if (_transitions.ContainsKey(transitionId))
                        {
                            throw new StateMachineException($"Transition with ID '{transitionId}' already exists",
                                ErrorCodes.TransitionAlreadyExists);
                        }

                        // Source ve target state'ler mevcut olmalı;
                        if (!_states.ContainsKey(configuration.SourceStateId))
                        {
                            throw new StateMachineException($"Source state '{configuration.SourceStateId}' not found",
                                ErrorCodes.StateNotFound);
                        }

                        if (!_states.ContainsKey(configuration.TargetStateId))
                        {
                            throw new StateMachineException($"Target state '{configuration.TargetStateId}' not found",
                                ErrorCodes.StateNotFound);
                        }

                        var transition = new Transition(transitionId, configuration, _logger);
                        _transitions[transitionId] = transition;

                        // Source state'e transition'ı ekle;
                        var sourceState = _states[configuration.SourceStateId] as State;
                        sourceState?.AddTransitionAsync(transitionId);

                        _logger.LogInformation("Added transition: {TransitionName} ({TransitionId}) from {Source} to {Target}",
                            configuration.TransitionName, transitionId, configuration.SourceStateId, configuration.TargetStateId);

                        RaiseMachineEvent(StateMachineEventType.TransitionAdded,
                            new StateMachineEventArgs;
                            {
                                MachineId = _machineId,
                                TransitionId = transitionId,
                                SourceStateId = configuration.SourceStateId,
                                TargetStateId = configuration.TargetStateId,
                                TransitionType = configuration.TransitionType;
                            });

                        return transition;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add transition: {TransitionId}", transitionId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TransitionOperationFailed);
                throw new StateMachineException("Failed to add transition", ex, ErrorCodes.TransitionOperationFailed);
            }
        }

        public async Task<bool> RemoveTransitionAsync(string transitionId)
        {
            try
            {
                using (_performanceTracker.TrackOperation("RemoveTransition"))
                {
                    lock (_syncLock)
                    {
                        if (!_transitions.ContainsKey(transitionId))
                        {
                            _logger.LogWarning("Transition not found: {TransitionId}", transitionId);
                            return false;
                        }

                        var transition = _transitions[transitionId];

                        // Source state'ten transition'ı kaldır;
                        if (_states.TryGetValue(transition.SourceStateId, out var sourceState))
                        {
                            (sourceState as State)?.RemoveTransitionAsync(transitionId);
                        }

                        _transitions.Remove(transitionId);

                        _logger.LogInformation("Removed transition: {TransitionId}", transitionId);

                        RaiseMachineEvent(StateMachineEventType.TransitionRemoved,
                            new StateMachineEventArgs;
                            {
                                MachineId = _machineId,
                                TransitionId = transitionId,
                                SourceStateId = transition.SourceStateId,
                                TargetStateId = transition.TargetStateId;
                            });

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove transition: {TransitionId}", transitionId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TransitionOperationFailed);
                throw new StateMachineException("Failed to remove transition", ex, ErrorCodes.TransitionOperationFailed);
            }
        }

        public async Task<bool> TransitionToStateAsync(string targetStateId, object transitionData = null)
        {
            try
            {
                using (_performanceTracker.TrackOperation("TransitionToState"))
                {
                    if (string.IsNullOrWhiteSpace(targetStateId))
                        throw new ArgumentException("Target state ID cannot be null or empty", nameof(targetStateId));

                    if (_currentState == null)
                    {
                        throw new StateMachineException("No current state to transition from",
                            ErrorCodes.NoCurrentState);
                    }

                    lock (_syncLock)
                    {
                        // Target state mevcut olmalı;
                        if (!_states.ContainsKey(targetStateId))
                        {
                            throw new StateMachineException($"Target state '{targetStateId}' not found",
                                ErrorCodes.StateNotFound);
                        }

                        // Aynı state'e geçiş;
                        if (_currentState.StateId == targetStateId)
                        {
                            _logger.LogDebug("Already in target state: {StateId}", targetStateId);
                            return false;
                        }

                        // Geçerli bir transition var mı?
                        var transition = _transitions.Values.FirstOrDefault(t =>
                            t.SourceStateId == _currentState.StateId &&
                            t.TargetStateId == targetStateId);

                        if (transition == null)
                        {
                            _logger.LogWarning("No transition found from {Source} to {Target}",
                                _currentState.StateId, targetStateId);

                            RaiseMachineEvent(StateMachineEventType.TransitionRejected,
                                new StateMachineEventArgs;
                                {
                                    MachineId = _machineId,
                                    SourceStateId = _currentState.StateId,
                                    TargetStateId = targetStateId,
                                    Reason = "No transition found"
                                });

                            return false;
                        }

                        if (!transition.IsEnabled)
                        {
                            _logger.LogDebug("Transition {TransitionId} is disabled", transition.TransitionId);

                            RaiseMachineEvent(StateMachineEventType.TransitionRejected,
                                new StateMachineEventArgs;
                                {
                                    MachineId = _machineId,
                                    TransitionId = transition.TransitionId,
                                    SourceStateId = _currentState.StateId,
                                    TargetStateId = targetStateId,
                                    Reason = "Transition disabled"
                                });

                            return false;
                        }

                        // Cool-down kontrolü;
                        if (transition.CoolDownTime > 0)
                        {
                            var timeSinceLastTransition = (float)(DateTime.UtcNow -
                                DateTime.FromBinary((long)transition.LastTransitionTime)).TotalSeconds;

                            if (timeSinceLastTransition < transition.CoolDownTime)
                            {
                                _logger.LogDebug("Transition {TransitionId} is in cool-down", transition.TransitionId);

                                RaiseMachineEvent(StateMachineEventType.TransitionRejected,
                                    new StateMachineEventArgs;
                                    {
                                        MachineId = _machineId,
                                        TransitionId = transition.TransitionId,
                                        SourceStateId = _currentState.StateId,
                                        TargetStateId = targetStateId,
                                        Reason = "In cool-down period"
                                    });

                                return false;
                            }
                        }
                    }

                    // Transition'ı gerçekleştir;
                    return await ExecuteTransitionAsync(_currentState.StateId, targetStateId, transition, transitionData);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to transition to state: {TargetStateId}", targetStateId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TransitionFailed);
                throw new StateMachineException("Failed to transition to state", ex, ErrorCodes.TransitionFailed);
            }
        }

        public async Task<bool> SendTriggerAsync(string triggerName, object triggerData = null)
        {
            try
            {
                using (_performanceTracker.TrackOperation("SendTrigger"))
                {
                    if (string.IsNullOrWhiteSpace(triggerName))
                        throw new ArgumentException("Trigger name cannot be null or empty", nameof(triggerName));

                    if (_currentState == null)
                        return false;

                    _logger.LogDebug("Processing trigger: {TriggerName} in state: {StateId}",
                        triggerName, _currentState.StateId);

                    // Current state'ten çıkan transition'ları kontrol et;
                    var outgoingTransitions = _transitions.Values;
                        .Where(t => t.SourceStateId == _currentState.StateId)
                        .ToList();

                    foreach (var transition in outgoingTransitions)
                    {
                        // Trigger-based transition'ları kontrol et;
                        var context = new TransitionEvaluationContext;
                        {
                            SourceStateId = _currentState.StateId,
                            TargetStateId = transition.TargetStateId,
                            TriggerName = triggerName,
                            TriggerData = triggerData,
                            MachineParameters = _globalParameters,
                            CurrentTime = _totalRunTime;
                        };

                        if (await transition.EvaluateAsync(context))
                        {
                            _logger.LogDebug("Trigger '{TriggerName}' activated transition {TransitionId} to {TargetState}",
                                triggerName, transition.TransitionId, transition.TargetStateId);

                            await ExecuteTransitionAsync(_currentState.StateId, transition.TargetStateId, transition, triggerData);
                            return true;
                        }
                    }

                    _logger.LogDebug("No transition activated by trigger: {TriggerName}", triggerName);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing trigger: {TriggerName}", triggerName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TriggerProcessingFailed);
                return false;
            }
        }

        public async Task SetParameterAsync(string parameterName, object value)
        {
            try
            {
                lock (_syncLock)
                {
                    if (string.IsNullOrWhiteSpace(parameterName))
                        throw new ArgumentException("Parameter name cannot be null or empty", nameof(parameterName));

                    _globalParameters[parameterName] = value;

                    if (_isDebugMode)
                    {
                        _logger.LogDebug("Set global parameter: {ParameterName} = {Value}", parameterName, value);
                    }
                }

                // Current state'e parametre değişikliğini bildir;
                if (_currentState != null)
                {
                    await _currentState.SetParameterAsync(parameterName, value);
                }

                RaiseMachineEvent(StateMachineEventType.ParameterChanged,
                    new StateMachineEventArgs;
                    {
                        MachineId = _machineId,
                        ParameterName = parameterName,
                        ParameterValue = value;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set parameter: {ParameterName}", parameterName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.ParameterOperationFailed);
                throw new StateMachineException("Failed to set parameter", ex, ErrorCodes.ParameterOperationFailed);
            }
        }

        public async Task<object> GetParameterAsync(string parameterName)
        {
            try
            {
                lock (_syncLock)
                {
                    if (string.IsNullOrWhiteSpace(parameterName))
                        throw new ArgumentException("Parameter name cannot be null or empty", nameof(parameterName));

                    if (_globalParameters.TryGetValue(parameterName, out var value))
                    {
                        return value;
                    }

                    // Current state'ten dene;
                    if (_currentState != null)
                    {
                        return await _currentState.GetParameterAsync(parameterName);
                    }

                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get parameter: {ParameterName}", parameterName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.ParameterOperationFailed);
                throw new StateMachineException("Failed to get parameter", ex, ErrorCodes.ParameterOperationFailed);
            }
        }

        public async Task<bool> SaveMachineAsync(string filePath)
        {
            try
            {
                using (_performanceTracker.TrackOperation("SaveMachine"))
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                        throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

                    _logger.LogInformation("Saving StateMachine to: {FilePath}", filePath);

                    var machineData = new StateMachineData;
                    {
                        MachineId = _machineId,
                        MachineName = _machineName,
                        Configuration = _configuration,
                        CurrentStateId = _currentState?.StateId,
                        PreviousStateId = _previousState?.StateId,
                        GlobalParameters = new Dictionary<string, object>(_globalParameters),
                        States = _states.Values.Cast<State>().Select(s => s.GetStateData()).ToList(),
                        Transitions = _transitions.Values.Cast<Transition>().Select(t => t.GetTransitionData()).ToList(),
                        CreatedDate = DateTime.UtcNow,
                        ModifiedDate = DateTime.UtcNow,
                        Version = "1.0.0"
                    };

                    // JSON serialization;
                    var options = new System.Text.Json.JsonSerializerOptions;
                    {
                        WriteIndented = true,
                        Converters = { new StateMachineJsonConverter() }
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(machineData, options);
                    await System.IO.File.WriteAllTextAsync(filePath, json);

                    _logger.LogInformation("StateMachine saved successfully");

                    RaiseMachineEvent(StateMachineEventType.MachineSaved,
                        new StateMachineEventArgs;
                        {
                            MachineId = _machineId,
                            FilePath = filePath;
                        });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save StateMachine to: {FilePath}", filePath);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineSaveFailed);
                throw new StateMachineException("Failed to save StateMachine", ex, ErrorCodes.StateMachineSaveFailed);
            }
        }

        public async Task<bool> LoadMachineAsync(string filePath)
        {
            try
            {
                using (_performanceTracker.TrackOperation("LoadMachine"))
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                        throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

                    if (!System.IO.File.Exists(filePath))
                        throw new FileNotFoundException($"StateMachine file not found: {filePath}");

                    _logger.LogInformation("Loading StateMachine from: {FilePath}", filePath);

                    // Machine'ı durdur;
                    if (_isRunning)
                    {
                        await StopAsync();
                    }

                    lock (_syncLock)
                    {
                        // Mevcut verileri temizle;
                        _states.Clear();
                        _transitions.Clear();
                        _globalParameters.Clear();

                        // Dosyayı oku;
                        var json = System.IO.File.ReadAllText(filePath);
                        var options = new System.Text.Json.JsonSerializerOptions;
                        {
                            Converters = { new StateMachineJsonConverter() }
                        };

                        var machineData = System.Text.Json.JsonSerializer.Deserialize<StateMachineData>(json, options);

                        if (machineData == null)
                            throw new StateMachineException("Failed to deserialize StateMachine data",
                                ErrorCodes.StateMachineDeserializationFailed);

                        // Machine özelliklerini yükle;
                        _machineId = machineData.MachineId;
                        _machineName = machineData.MachineName;
                        _configuration = machineData.Configuration;

                        // Global parameters;
                        foreach (var param in machineData.GlobalParameters)
                        {
                            _globalParameters[param.Key] = param.Value;
                        }

                        // State'leri yeniden oluştur;
                        foreach (var stateData in machineData.States)
                        {
                            var state = State.FromStateData(stateData, _logger);
                            _states[state.StateId] = state;
                        }

                        // Transition'ları yeniden oluştur;
                        foreach (var transitionData in machineData.Transitions)
                        {
                            var transition = Transition.FromTransitionData(transitionData, _logger);
                            _transitions[transition.TransitionId] = transition;
                        }

                        // Current ve previous state'leri ayarla;
                        if (!string.IsNullOrEmpty(machineData.CurrentStateId) &&
                            _states.ContainsKey(machineData.CurrentStateId))
                        {
                            _currentState = _states[machineData.CurrentStateId];
                        }

                        if (!string.IsNullOrEmpty(machineData.PreviousStateId) &&
                            _states.ContainsKey(machineData.PreviousStateId))
                        {
                            _previousState = _states[machineData.PreviousStateId];
                        }

                        _isInitialized = true;
                    }

                    _logger.LogInformation("StateMachine loaded successfully. States: {StateCount}, Transitions: {TransitionCount}",
                        _states.Count, _transitions.Count);

                    RaiseMachineEvent(StateMachineEventType.MachineLoaded,
                        new StateMachineEventArgs;
                        {
                            MachineId = _machineId,
                            FilePath = filePath;
                        });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load StateMachine from: {FilePath}", filePath);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineLoadFailed);
                throw new StateMachineException("Failed to load StateMachine", ex, ErrorCodes.StateMachineLoadFailed);
            }
        }

        public async Task<IStateMachine> CloneAsync(string newMachineName = null)
        {
            try
            {
                using (_performanceTracker.TrackOperation("Clone"))
                {
                    var cloneName = newMachineName ?? $"{_machineName}_Clone";

                    _logger.LogInformation("Cloning StateMachine: {OriginalName} to {CloneName}",
                        _machineName, cloneName);

                    // Yeni bir StateMachine oluştur;
                    var clonedMachine = new StateMachine(_logger, _eventBus, _errorReporter);

                    // Configuration'ı klonla;
                    var clonedConfig = new StateMachineConfiguration;
                    {
                        MachineName = cloneName,
                        InitialStateId = _configuration?.InitialStateId,
                        EnableDebugMode = _isDebugMode,
                        CreateDefaultStates = false;
                    };

                    await clonedMachine.InitializeAsync(clonedConfig);

                    lock (_syncLock)
                    {
                        // State'leri klonla;
                        foreach (var state in _states.Values)
                        {
                            var stateData = (state as State)?.GetStateData();
                            if (stateData != null)
                            {
                                var clonedState = State.FromStateData(stateData, _logger);
                                clonedMachine._states[clonedState.StateId] = clonedState;
                            }
                        }

                        // Transition'ları klonla;
                        foreach (var transition in _transitions.Values)
                        {
                            var transitionData = (transition as Transition)?.GetTransitionData();
                            if (transitionData != null)
                            {
                                var clonedTransition = Transition.FromTransitionData(transitionData, _logger);
                                clonedMachine._transitions[clonedTransition.TransitionId] = clonedTransition;
                            }
                        }

                        // Global parameters'ı klonla;
                        foreach (var param in _globalParameters)
                        {
                            clonedMachine._globalParameters[param.Key] = param.Value;
                        }

                        // Current state'i ayarla;
                        if (_currentState != null && clonedMachine._states.ContainsKey(_currentState.StateId))
                        {
                            clonedMachine._currentState = clonedMachine._states[_currentState.StateId];
                        }

                        // Previous state'i ayarla;
                        if (_previousState != null && clonedMachine._states.ContainsKey(_previousState.StateId))
                        {
                            clonedMachine._previousState = clonedMachine._states[_previousState.StateId];
                        }
                    }

                    _logger.LogInformation("StateMachine cloned successfully");

                    RaiseMachineEvent(StateMachineEventType.MachineCloned,
                        new StateMachineEventArgs;
                        {
                            MachineId = _machineId,
                            CloneMachineId = clonedMachine.MachineId,
                            CloneMachineName = cloneName;
                        });

                    return clonedMachine;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clone StateMachine");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineCloneFailed);
                throw new StateMachineException("Failed to clone StateMachine", ex, ErrorCodes.StateMachineCloneFailed);
            }
        }

        public void SubscribeToMachineEvent(StateMachineEventType eventType, Action<StateMachineEventArgs> handler)
        {
            lock (_syncLock)
            {
                if (!_eventHandlers.ContainsKey(eventType))
                {
                    _eventHandlers[eventType] = new List<Action<StateMachineEventArgs>>();
                }

                _eventHandlers[eventType].Add(handler);
                _logger.LogDebug("Subscribed to machine event: {EventType}", eventType);
            }
        }

        public void UnsubscribeFromMachineEvent(StateMachineEventType eventType, Action<StateMachineEventArgs> handler)
        {
            lock (_syncLock)
            {
                if (_eventHandlers.ContainsKey(eventType))
                {
                    _eventHandlers[eventType].Remove(handler);
                    _logger.LogDebug("Unsubscribed from machine event: {EventType}", eventType);
                }
            }
        }

        public StateMachineDebugInfo GetDebugInfo()
        {
            lock (_syncLock)
            {
                return new StateMachineDebugInfo;
                {
                    MachineId = _machineId,
                    MachineName = _machineName,
                    CurrentStateId = _currentState?.StateId,
                    PreviousStateId = _previousState?.StateId,
                    IsRunning = _isRunning,
                    IsInitialized = _isInitialized,
                    StateCount = _states.Count,
                    TransitionCount = _transitions.Count,
                    GlobalParameterCount = _globalParameters.Count,
                    TotalUpdates = _totalUpdates,
                    TotalTransitions = _totalTransitions,
                    TotalRunTime = _totalRunTime,
                    PerformanceMetrics = GetPerformanceMetrics()
                };
            }
        }

        public StateMachinePerformanceMetrics GetPerformanceMetrics()
        {
            return _performanceTracker.GetMetrics();
        }

        private async Task UpdateLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("Starting StateMachine update loop");

                var lastUpdateTime = DateTime.UtcNow;

                while (!cancellationToken.IsCancellationRequested)
                {
                    lock (_syncLock)
                    {
                        if (!_isRunning)
                        {
                            break;
                        }
                    }

                    var currentTime = DateTime.UtcNow;
                    var deltaTime = (float)(currentTime - lastUpdateTime).TotalSeconds;
                    lastUpdateTime = currentTime;

                    // DeltaTime'ı clamp'le;
                    deltaTime = Math.Min(deltaTime, 0.1f); // Max 100ms;

                    // Update'i çağır;
                    await UpdateAsync(deltaTime);

                    // Fixed update'i çağır (50 FPS)
                    await FixedUpdateAsync(0.02f);

                    // Frame rate kontrolü;
                    var elapsed = (DateTime.UtcNow - currentTime).TotalSeconds;
                    var sleepTime = Math.Max(0, _updateInterval - elapsed);

                    if (sleepTime > 0)
                    {
                        await Task.Delay((int)(sleepTime * 1000), cancellationToken);
                    }
                }

                _logger.LogDebug("StateMachine update loop ended");
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("StateMachine update loop cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in StateMachine update loop");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.UpdateLoopError);
            }
        }

        private async Task<bool> ExecuteTransitionAsync(string sourceStateId, string targetStateId, ITransition transition, object transitionData)
        {
            try
            {
                _logger.LogInformation("Executing transition from {Source} to {Target}", sourceStateId, targetStateId);

                // Source state'ten çıkış;
                if (_states.TryGetValue(sourceStateId, out var sourceState))
                {
                    await sourceState.OnExitAsync(transitionData);
                }

                // State değişikliği;
                lock (_syncLock)
                {
                    _previousState = _currentState;
                    _currentState = _states[targetStateId];
                    _totalTransitions++;
                }

                // Target state'e giriş;
                await _currentState.OnEnterAsync(transitionData);

                // Transition executed event;
                if (transition is Transition concreteTransition)
                {
                    concreteTransition.RecordTransitionTime();
                }

                _logger.LogDebug("Transition completed: {Source} -> {Target}", sourceStateId, targetStateId);

                RaiseMachineEvent(StateMachineEventType.TransitionExecuted,
                    new StateMachineEventArgs;
                    {
                        MachineId = _machineId,
                        TransitionId = transition.TransitionId,
                        SourceStateId = sourceStateId,
                        TargetStateId = targetStateId,
                        PreviousStateId = _previousState?.StateId,
                        NewStateId = _currentState.StateId,
                        TransitionData = transitionData;
                    });

                await _eventBus.PublishAsync(new StateMachineEvent;
                {
                    EventType = StateMachineEventType.TransitionExecuted.ToString(),
                    MachineId = _machineId,
                    Timestamp = DateTime.UtcNow,
                    Data = new;
                    {
                        SourceStateId = sourceStateId,
                        TargetStateId = targetStateId,
                        TransitionId = transition.TransitionId;
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing transition from {Source} to {Target}", sourceStateId, targetStateId);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TransitionExecutionFailed);

                RaiseMachineEvent(StateMachineEventType.TransitionFailed,
                    new StateMachineEventArgs;
                    {
                        MachineId = _machineId,
                        TransitionId = transition.TransitionId,
                        SourceStateId = sourceStateId,
                        TargetStateId = targetStateId,
                        ErrorMessage = ex.Message;
                    });

                return false;
            }
        }

        private async Task EvaluateTransitionsAsync(float deltaTime)
        {
            try
            {
                if (_currentState == null)
                    return;

                // Current state'ten çıkan transition'ları al;
                var outgoingTransitions = _transitions.Values;
                    .Where(t => t.SourceStateId == _currentState.StateId)
                    .ToList();

                foreach (var transition in outgoingTransitions)
                {
                    if (!transition.IsEnabled)
                        continue;

                    // Cool-down kontrolü;
                    if (transition.CoolDownTime > 0)
                    {
                        var timeSinceLastTransition = (float)(DateTime.UtcNow -
                            DateTime.FromBinary((long)transition.LastTransitionTime)).TotalSeconds;

                        if (timeSinceLastTransition < transition.CoolDownTime)
                            continue;
                    }

                    var context = new TransitionEvaluationContext;
                    {
                        SourceStateId = _currentState.StateId,
                        TargetStateId = transition.TargetStateId,
                        MachineParameters = _globalParameters,
                        CurrentTime = _totalRunTime,
                        DeltaTime = deltaTime;
                    };

                    if (await transition.EvaluateAsync(context))
                    {
                        await ExecuteTransitionAsync(_currentState.StateId, transition.TargetStateId, transition, null);
                        break; // Sadece bir transition execute et;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating transitions");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.TransitionEvaluationError);
            }
        }

        private void CreateDefaultStates()
        {
            try
            {
                // Idle state;
                var idleConfig = new StateConfiguration;
                {
                    StateName = "Idle",
                    StateType = StateType.Default,
                    CanBeInterrupted = true,
                    Priority = 0;
                };

                AddStateAsync("Idle", idleConfig).GetAwaiter().GetResult();

                // Walk state;
                var walkConfig = new StateConfiguration;
                {
                    StateName = "Walk",
                    StateType = StateType.Movement,
                    CanBeInterrupted = true,
                    Priority = 1;
                };

                AddStateAsync("Walk", walkConfig).GetAwaiter().GetResult();

                // Run state;
                var runConfig = new StateConfiguration;
                {
                    StateName = "Run",
                    StateType = StateType.Movement,
                    CanBeInterrupted = true,
                    Priority = 2;
                };

                AddStateAsync("Run", runConfig).GetAwaiter().GetResult();

                // Jump state;
                var jumpConfig = new StateConfiguration;
                {
                    StateName = "Jump",
                    StateType = StateType.Action,
                    CanBeInterrupted = false,
                    Priority = 3;
                };

                AddStateAsync("Jump", jumpConfig).GetAwaiter().GetResult();

                // Attack state;
                var attackConfig = new StateConfiguration;
                {
                    StateName = "Attack",
                    StateType = StateType.Combat,
                    CanBeInterrupted = false,
                    Priority = 4;
                };

                AddStateAsync("Attack", attackConfig).GetAwaiter().GetResult();

                // Default transitions;
                var idleToWalk = new TransitionConfiguration;
                {
                    TransitionName = "Idle to Walk",
                    SourceStateId = "Idle",
                    TargetStateId = "Walk",
                    TransitionType = TransitionType.Immediate,
                    EvaluationMode = TransitionEvaluationMode.ConditionBased;
                };

                AddTransitionAsync("Idle_Walk", idleToWalk).GetAwaiter().GetResult();

                var walkToRun = new TransitionConfiguration;
                {
                    TransitionName = "Walk to Run",
                    SourceStateId = "Walk",
                    TargetStateId = "Run",
                    TransitionType = TransitionType.Immediate,
                    EvaluationMode = TransitionEvaluationMode.ConditionBased;
                };

                AddTransitionAsync("Walk_Run", walkToRun).GetAwaiter().GetResult();

                var anyToJump = new TransitionConfiguration;
                {
                    TransitionName = "Any to Jump",
                    SourceStateId = "*", // Wildcard for any state;
                    TargetStateId = "Jump",
                    TransitionType = TransitionType.Immediate,
                    EvaluationMode = TransitionEvaluationMode.TriggerBased;
                };

                AddTransitionAsync("Any_Jump", anyToJump).GetAwaiter().GetResult();

                _logger.LogDebug("Created default states and transitions");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating default states");
            }
        }

        private void RaiseMachineEvent(StateMachineEventType eventType, StateMachineEventArgs args)
        {
            lock (_syncLock)
            {
                if (_eventHandlers.ContainsKey(eventType))
                {
                    foreach (var handler in _eventHandlers[eventType].ToList())
                    {
                        try
                        {
                            handler?.Invoke(args);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in machine event handler for {EventType}", eventType);
                        }
                    }
                }
            }
        }

        private async Task HandleStateMachineEvent(StateMachineEvent @event)
        {
            // External event'leri işle;
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                _logger.LogInformation("Disposing StateMachine: {MachineName}", _machineName);

                // Machine'ı durdur;
                StopAsync().GetAwaiter().GetResult();

                // CancellationTokenSource'u dispose et;
                _updateCancellationTokenSource?.Dispose();

                // Event handler'ları temizle;
                _eventHandlers.Clear();

                // State'leri temizle;
                _states.Clear();

                // Transition'ları temizle;
                _transitions.Clear();

                // Global parameters'ı temizle;
                _globalParameters.Clear();

                _isDisposed = true;

                _logger.LogInformation("StateMachine disposed: {MachineName}", _machineName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing StateMachine");
            }
        }
    }

    /// <summary>
    /// State implementasyonu;
    /// </summary>
    internal class State : IState;
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, object> _parameters = new Dictionary<string, object>();
        private readonly List<string> _outgoingTransitions = new List<string>();

        public string StateId { get; }
        public string StateName { get; }
        public StateType Type { get; }
        public bool IsActive { get; private set; }
        public float TimeInState { get; private set; }
        public int UpdateCount { get; private set; }
        public IReadOnlyDictionary<string, object> Parameters => _parameters;
        public IReadOnlyList<string> OutgoingTransitions => _outgoingTransitions.AsReadOnly();

        // State events;
        public event Action<StateEventArgs> OnStateEntered;
        public event Action<StateEventArgs> OnStateExited;
        public event Action<StateEventArgs> OnStateUpdated;

        public State(string stateId, StateConfiguration configuration, ILogger logger)
        {
            StateId = stateId;
            StateName = configuration.StateName;
            Type = configuration.StateType;
            _logger = logger;

            // Default parameters;
            _parameters["IsActive"] = false;
            _parameters["Speed"] = 0.0f;
            _parameters["AnimationWeight"] = 1.0f;
            _parameters["BlendTime"] = 0.2f;

            _logger.LogDebug("Created state: {StateName} ({StateId})", StateName, StateId);
        }

        public async Task<bool> OnEnterAsync(object enterData)
        {
            try
            {
                IsActive = true;
                TimeInState = 0.0f;
                UpdateCount = 0;
                _parameters["IsActive"] = true;

                _logger.LogDebug("Entered state: {StateId}", StateId);

                OnStateEntered?.Invoke(new StateEventArgs;
                {
                    StateId = StateId,
                    StateName = StateName,
                    EnterData = enterData;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error entering state: {StateId}", StateId);
                return false;
            }
        }

        public async Task<bool> OnUpdateAsync(float deltaTime)
        {
            try
            {
                if (!IsActive)
                    return false;

                TimeInState += deltaTime;
                UpdateCount++;

                // Update parameters;
                _parameters["TimeInState"] = TimeInState;
                _parameters["UpdateCount"] = UpdateCount;

                OnStateUpdated?.Invoke(new StateEventArgs;
                {
                    StateId = StateId,
                    StateName = StateName,
                    DeltaTime = deltaTime,
                    TimeInState = TimeInState;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating state: {StateId}", StateId);
                return false;
            }
        }

        public async Task<bool> OnFixedUpdateAsync(float fixedDeltaTime)
        {
            try
            {
                if (!IsActive)
                    return false;

                // Fixed update logic here;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fixed update for state: {StateId}", StateId);
                return false;
            }
        }

        public async Task<bool> OnExitAsync(object exitData)
        {
            try
            {
                IsActive = false;
                _parameters["IsActive"] = false;

                _logger.LogDebug("Exited state: {StateId}", StateId);

                OnStateExited?.Invoke(new StateEventArgs;
                {
                    StateId = StateId,
                    StateName = StateName,
                    ExitData = exitData,
                    TimeInState = TimeInState;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exiting state: {StateId}", StateId);
                return false;
            }
        }

        public async Task<bool> AddTransitionAsync(string transitionId)
        {
            try
            {
                if (!_outgoingTransitions.Contains(transitionId))
                {
                    _outgoingTransitions.Add(transitionId);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding transition to state: {StateId}", StateId);
                return false;
            }
        }

        public async Task<bool> RemoveTransitionAsync(string transitionId)
        {
            try
            {
                return _outgoingTransitions.Remove(transitionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing transition from state: {StateId}", StateId);
                return false;
            }
        }

        public async Task<bool> SetParameterAsync(string parameterName, object value)
        {
            try
            {
                _parameters[parameterName] = value;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting parameter in state: {StateId}", StateId);
                return false;
            }
        }

        public async Task<object> GetParameterAsync(string parameterName)
        {
            try
            {
                if (_parameters.TryGetValue(parameterName, out var value))
                {
                    return value;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting parameter from state: {StateId}", StateId);
                return null;
            }
        }

        internal StateData GetStateData()
        {
            return new StateData;
            {
                StateId = StateId,
                StateName = StateName,
                StateType = Type,
                Parameters = new Dictionary<string, object>(_parameters),
                OutgoingTransitions = new List<string>(_outgoingTransitions)
            };
        }

        internal static State FromStateData(StateData data, ILogger logger)
        {
            var config = new StateConfiguration;
            {
                StateName = data.StateName,
                StateType = data.StateType;
            };

            var state = new State(data.StateId, config, logger);

            // Parameters'ı yükle;
            foreach (var param in data.Parameters)
            {
                state._parameters[param.Key] = param.Value;
            }

            // Transitions'ı yükle;
            state._outgoingTransitions.AddRange(data.OutgoingTransitions);

            return state;
        }
    }

    /// <summary>
    /// Transition implementasyonu;
    /// </summary>
    internal class Transition : ITransition;
    {
        private readonly ILogger _logger;
        private readonly List<ITransitionCondition> _conditions = new List<ITransitionCondition>();

        public string TransitionId { get; }
        public string TransitionName { get; }
        public string SourceStateId { get; }
        public string TargetStateId { get; }
        public TransitionType Type { get; }
        public TransitionEvaluationMode EvaluationMode { get; }
        public bool IsEnabled { get; set; } = true;
        public float CoolDownTime { get; set; }
        public float LastTransitionTime { get; private set; }

        public Transition(string transitionId, TransitionConfiguration configuration, ILogger logger)
        {
            TransitionId = transitionId;
            TransitionName = configuration.TransitionName;
            SourceStateId = configuration.SourceStateId;
            TargetStateId = configuration.TargetStateId;
            Type = configuration.TransitionType;
            EvaluationMode = configuration.EvaluationMode;
            CoolDownTime = configuration.CoolDownTime;
            _logger = logger;
            LastTransitionTime = DateTime.UtcNow.ToBinary();

            _logger.LogDebug("Created transition: {TransitionName} ({TransitionId})", TransitionName, TransitionId);
        }

        public async Task<bool> EvaluateAsync(TransitionEvaluationContext context)
        {
            try
            {
                if (!IsEnabled)
                    return false;

                // Cool-down kontrolü;
                if (CoolDownTime > 0)
                {
                    var timeSinceLastTransition = (float)(DateTime.UtcNow -
                        DateTime.FromBinary((long)LastTransitionTime)).TotalSeconds;

                    if (timeSinceLastTransition < CoolDownTime)
                        return false;
                }

                // Condition'ları değerlendir;
                if (_conditions.Count > 0)
                {
                    foreach (var condition in _conditions)
                    {
                        if (!await condition.EvaluateAsync(new ConditionEvaluationContext;
                        {
                            MachineParameters = context.MachineParameters,
                            CurrentTime = context.CurrentTime,
                            DeltaTime = context.DeltaTime,
                            TriggerName = context.TriggerName,
                            TriggerData = context.TriggerData;
                        }))
                        {
                            return false;
                        }
                    }
                }

                // Trigger-based evaluation;
                if (EvaluationMode == TransitionEvaluationMode.TriggerBased)
                {
                    if (string.IsNullOrEmpty(context.TriggerName))
                        return false;

                    // Trigger isimlerini karşılaştır;
                    // Burada daha kompleks trigger matching yapılabilir;
                    return true;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating transition: {TransitionId}", TransitionId);
                return false;
            }
        }

        public async Task<bool> CanTransitionAsync(TransitionEvaluationContext context)
        {
            try
            {
                return await EvaluateAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking if transition can occur: {TransitionId}", TransitionId);
                return false;
            }
        }

        public async Task<bool> ExecuteTransitionAsync(object transitionData)
        {
            try
            {
                RecordTransitionTime();
                _logger.LogDebug("Executed transition: {TransitionId}", TransitionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing transition: {TransitionId}", TransitionId);
                return false;
            }
        }

        public void AddCondition(ITransitionCondition condition)
        {
            _conditions.Add(condition);
            _logger.LogDebug("Added condition to transition: {TransitionId}", TransitionId);
        }

        public void RemoveCondition(string conditionId)
        {
            _conditions.RemoveAll(c => c.ConditionId == conditionId);
            _logger.LogDebug("Removed condition from transition: {TransitionId}", TransitionId);
        }

        public IReadOnlyList<ITransitionCondition> GetConditions()
        {
            return _conditions.AsReadOnly();
        }

        internal void RecordTransitionTime()
        {
            LastTransitionTime = DateTime.UtcNow.ToBinary();
        }

        internal TransitionData GetTransitionData()
        {
            return new TransitionData;
            {
                TransitionId = TransitionId,
                TransitionName = TransitionName,
                SourceStateId = SourceStateId,
                TargetStateId = TargetStateId,
                TransitionType = Type,
                EvaluationMode = EvaluationMode,
                IsEnabled = IsEnabled,
                CoolDownTime = CoolDownTime,
                LastTransitionTime = LastTransitionTime,
                Conditions = _conditions.Cast<TransitionCondition>().Select(c => c.GetConditionData()).ToList()
            };
        }

        internal static Transition FromTransitionData(TransitionData data, ILogger logger)
        {
            var config = new TransitionConfiguration;
            {
                TransitionName = data.TransitionName,
                SourceStateId = data.SourceStateId,
                TargetStateId = data.TargetStateId,
                TransitionType = data.TransitionType,
                EvaluationMode = data.EvaluationMode,
                CoolDownTime = data.CoolDownTime;
            };

            var transition = new Transition(data.TransitionId, config, logger)
            {
                IsEnabled = data.IsEnabled,
                LastTransitionTime = data.LastTransitionTime;
            };

            // Conditions'ı yükle;
            foreach (var conditionData in data.Conditions)
            {
                var condition = TransitionCondition.FromConditionData(conditionData);
                transition._conditions.Add(condition);
            }

            return transition;
        }
    }

    /// <summary>
    /// Transition condition implementasyonu;
    /// </summary>
    internal class TransitionCondition : ITransitionCondition;
    {
        public string ConditionId { get; }
        public string ConditionName { get; }
        public ConditionType Type { get; }
        public ConditionOperator Operator { get; }
        public string ParameterName { get; }
        public object ExpectedValue { get; }
        public float Tolerance { get; }

        public TransitionCondition(string conditionId, ConditionConfiguration configuration)
        {
            ConditionId = conditionId;
            ConditionName = configuration.ConditionName;
            Type = configuration.ConditionType;
            Operator = configuration.Operator;
            ParameterName = configuration.ParameterName;
            ExpectedValue = configuration.ExpectedValue;
            Tolerance = configuration.Tolerance;
        }

        public async Task<bool> EvaluateAsync(ConditionEvaluationContext context)
        {
            try
            {
                if (context.MachineParameters == null)
                    return false;

                if (!context.MachineParameters.TryGetValue(ParameterName, out var actualValue))
                    return false;

                switch (Type)
                {
                    case ConditionType.Boolean:
                        return EvaluateBoolean(actualValue);

                    case ConditionType.Numeric:
                        return EvaluateNumeric(actualValue);

                    case ConditionType.String:
                        return EvaluateString(actualValue);

                    case ConditionType.Trigger:
                        return EvaluateTrigger(context);

                    case ConditionType.Timer:
                        return EvaluateTimer(context);

                    default:
                        return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool EvaluateBoolean(object actualValue)
        {
            if (actualValue is bool boolValue && ExpectedValue is bool expectedBool)
            {
                return Operator switch;
                {
                    ConditionOperator.Equals => boolValue == expectedBool,
                    ConditionOperator.NotEquals => boolValue != expectedBool,
                    _ => false;
                };
            }
            return false;
        }

        private bool EvaluateNumeric(object actualValue)
        {
            try
            {
                var actual = Convert.ToSingle(actualValue);
                var expected = Convert.ToSingle(ExpectedValue);

                return Operator switch;
                {
                    ConditionOperator.Equals => Math.Abs(actual - expected) <= Tolerance,
                    ConditionOperator.NotEquals => Math.Abs(actual - expected) > Tolerance,
                    ConditionOperator.GreaterThan => actual > expected,
                    ConditionOperator.LessThan => actual < expected,
                    ConditionOperator.GreaterThanOrEqual => actual >= expected,
                    ConditionOperator.LessThanOrEqual => actual <= expected,
                    _ => false;
                };
            }
            catch
            {
                return false;
            }
        }

        private bool EvaluateString(object actualValue)
        {
            var actual = actualValue?.ToString();
            var expected = ExpectedValue?.ToString();

            if (actual == null || expected == null)
                return false;

            return Operator switch;
            {
                ConditionOperator.Equals => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
                ConditionOperator.NotEquals => !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
                ConditionOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
                ConditionOperator.StartsWith => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
                ConditionOperator.EndsWith => actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
                _ => false;
            };
        }

        private bool EvaluateTrigger(ConditionEvaluationContext context)
        {
            if (string.IsNullOrEmpty(context.TriggerName))
                return false;

            var expected = ExpectedValue?.ToString();

            return Operator switch;
            {
                ConditionOperator.Equals => context.TriggerName.Equals(expected, StringComparison.OrdinalIgnoreCase),
                ConditionOperator.NotEquals => !context.TriggerName.Equals(expected, StringComparison.OrdinalIgnoreCase),
                ConditionOperator.Contains => context.TriggerName.Contains(expected, StringComparison.OrdinalIgnoreCase),
                _ => false;
            };
        }

        private bool EvaluateTimer(ConditionEvaluationContext context)
        {
            try
            {
                var expected = Convert.ToSingle(ExpectedValue);
                var currentTime = context.CurrentTime;

                return Operator switch;
                {
                    ConditionOperator.GreaterThan => currentTime > expected,
                    ConditionOperator.LessThan => currentTime < expected,
                    ConditionOperator.GreaterThanOrEqual => currentTime >= expected,
                    ConditionOperator.LessThanOrEqual => currentTime <= expected,
                    _ => false;
                };
            }
            catch
            {
                return false;
            }
        }

        internal ConditionData GetConditionData()
        {
            return new ConditionData;
            {
                ConditionId = ConditionId,
                ConditionName = ConditionName,
                ConditionType = Type,
                Operator = Operator,
                ParameterName = ParameterName,
                ExpectedValue = ExpectedValue,
                Tolerance = Tolerance;
            };
        }

        internal static TransitionCondition FromConditionData(ConditionData data)
        {
            var config = new ConditionConfiguration;
            {
                ConditionName = data.ConditionName,
                ConditionType = data.ConditionType,
                Operator = data.Operator,
                ParameterName = data.ParameterName,
                ExpectedValue = data.ExpectedValue,
                Tolerance = data.Tolerance;
            };

            return new TransitionCondition(data.ConditionId, config);
        }
    }

    // Performance tracker;
    internal class PerformanceTracker;
    {
        private readonly object _syncLock = new object();
        private readonly List<float> _updateTimes = new List<float>();
        private readonly Dictionary<string, float> _operationTimes = new Dictionary<string, float>();
        private DateTime _lastResetTime;

        public PerformanceTracker()
        {
            _lastResetTime = DateTime.UtcNow;
        }

        public IDisposable TrackOperation(string operationName)
        {
            return new OperationTracker(this, operationName);
        }

        public void RecordUpdate(float deltaTime)
        {
            lock (_syncLock)
            {
                _updateTimes.Add(deltaTime);

                // Keep only last 1000 samples;
                if (_updateTimes.Count > 1000)
                {
                    _updateTimes.RemoveAt(0);
                }
            }
        }

        public void RecordOperationTime(string operationName, float time)
        {
            lock (_syncLock)
            {
                if (!_operationTimes.ContainsKey(operationName))
                {
                    _operationTimes[operationName] = 0;
                }

                _operationTimes[operationName] += time;
            }
        }

        public StateMachinePerformanceMetrics GetMetrics()
        {
            lock (_syncLock)
            {
                var metrics = new StateMachinePerformanceMetrics();

                if (_updateTimes.Count > 0)
                {
                    metrics.AverageUpdateTime = _updateTimes.Average();
                    metrics.MinUpdateTime = _updateTimes.Min();
                    metrics.MaxUpdateTime = _updateTimes.Max();
                    metrics.UpdateCount = _updateTimes.Count;
                }

                metrics.OperationTimes = new Dictionary<string, float>(_operationTimes);
                metrics.TotalTrackedTime = (float)(DateTime.UtcNow - _lastResetTime).TotalSeconds;

                return metrics;
            }
        }

        public void Reset()
        {
            lock (_syncLock)
            {
                _updateTimes.Clear();
                _operationTimes.Clear();
                _lastResetTime = DateTime.UtcNow;
            }
        }

        private class OperationTracker : IDisposable
        {
            private readonly PerformanceTracker _tracker;
            private readonly string _operationName;
            private readonly DateTime _startTime;
            private bool _isDisposed;

            public OperationTracker(PerformanceTracker tracker, string operationName)
            {
                _tracker = tracker;
                _operationName = operationName;
                _startTime = DateTime.UtcNow;
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                var elapsed = (float)(DateTime.UtcNow - _startTime).TotalSeconds;
                _tracker.RecordOperationTime(_operationName, elapsed);
                _isDisposed = true;
            }
        }
    }

    // JSON Converter for StateMachine serialization;
    internal class StateMachineJsonConverter : System.Text.Json.Serialization.JsonConverter<StateMachineData>
    {
        public override StateMachineData Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            // Custom deserialization logic;
            return System.Text.Json.JsonSerializer.Deserialize<StateMachineData>(ref reader, options);
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, StateMachineData value, System.Text.Json.JsonSerializerOptions options)
        {
            // Custom serialization logic;
            System.Text.Json.JsonSerializer.Serialize(writer, value, options);
        }
    }

    // Enum ve Data Class tanımlamaları;

    public enum StateType;
    {
        Default,
        Movement,
        Combat,
        Action,
        Reaction,
        Special,
        Custom;
    }

    public enum TransitionType;
    {
        Immediate,
        Blended,
        Additive,
        Sequential;
    }

    public enum TransitionEvaluationMode;
    {
        ConditionBased,
        TriggerBased,
        TimeBased,
        ProbabilityBased;
    }

    public enum ConditionType;
    {
        Boolean,
        Numeric,
        String,
        Trigger,
        Timer,
        Custom;
    }

    public enum ConditionOperator;
    {
        Equals,
        NotEquals,
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual,
        Contains,
        StartsWith,
        EndsWith;
    }

    public enum StateMachineEventType;
    {
        MachineInitialized,
        MachineStarted,
        MachineStopped,
        MachinePaused,
        MachineResumed,
        MachineSaved,
        MachineLoaded,
        MachineCloned,
        MachineRenamed,
        MachineError,
        StateAdded,
        StateRemoved,
        StateEntered,
        StateExited,
        StateUpdated,
        TransitionAdded,
        TransitionRemoved,
        TransitionExecuted,
        TransitionFailed,
        TransitionRejected,
        ParameterChanged,
        TriggerProcessed;
    }

    // Configuration classes;

    public class StateMachineConfiguration;
    {
        public string MachineName { get; set; } = "New State Machine";
        public string InitialStateId { get; set; }
        public bool EnableDebugMode { get; set; } = false;
        public bool CreateDefaultStates { get; set; } = true;
        public float UpdateInterval { get; set; } = 0.016f; // ~60 FPS;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class StateConfiguration;
    {
        public string StateName { get; set; }
        public StateType StateType { get; set; } = StateType.Default;
        public bool CanBeInterrupted { get; set; } = true;
        public int Priority { get; set; } = 0;
        public Dictionary<string, object> InitialParameters { get; set; } = new Dictionary<string, object>();
    }

    public class TransitionConfiguration;
    {
        public string TransitionName { get; set; }
        public string SourceStateId { get; set; }
        public string TargetStateId { get; set; }
        public TransitionType TransitionType { get; set; } = TransitionType.Immediate;
        public TransitionEvaluationMode EvaluationMode { get; set; } = TransitionEvaluationMode.ConditionBased;
        public float CoolDownTime { get; set; } = 0.0f;
        public List<ConditionConfiguration> Conditions { get; set; } = new List<ConditionConfiguration>();
    }

    public class ConditionConfiguration;
    {
        public string ConditionName { get; set; }
        public ConditionType ConditionType { get; set; }
        public ConditionOperator Operator { get; set; }
        public string ParameterName { get; set; }
        public object ExpectedValue { get; set; }
        public float Tolerance { get; set; } = 0.01f;
    }

    // Event args classes;

    public class StateMachineEventArgs : EventArgs;
    {
        public Guid MachineId { get; set; }
        public string MachineName { get; set; }
        public Guid CloneMachineId { get; set; }
        public string CloneMachineName { get; set; }
        public string CurrentStateId { get; set; }
        public string PreviousStateId { get; set; }
        public string NewStateId { get; set; }
        public string StateId { get; set; }
        public string StateName { get; set; }
        public StateType StateType { get; set; }
        public string TransitionId { get; set; }
        public string TransitionName { get; set; }
        public string SourceStateId { get; set; }
        public string TargetStateId { get; set; }
        public TransitionType TransitionType { get; set; }
        public string ParameterName { get; set; }
        public object ParameterValue { get; set; }
        public object TransitionData { get; set; }
        public string TriggerName { get; set; }
        public object TriggerData { get; set; }
        public string FilePath { get; set; }
        public string OldName { get; set; }
        public string NewName { get; set; }
        public string ErrorMessage { get; set; }
        public string Reason { get; set; }
    }

    public class StateEventArgs : EventArgs;
    {
        public string StateId { get; set; }
        public string StateName { get; set; }
        public float DeltaTime { get; set; }
        public float TimeInState { get; set; }
        public object EnterData { get; set; }
        public object ExitData { get; set; }
    }

    // Context classes;

    public class TransitionEvaluationContext;
    {
        public string SourceStateId { get; set; }
        public string TargetStateId { get; set; }
        public string TriggerName { get; set; }
        public object TriggerData { get; set; }
        public Dictionary<string, object> MachineParameters { get; set; }
        public float CurrentTime { get; set; }
        public float DeltaTime { get; set; }
    }

    public class ConditionEvaluationContext;
    {
        public Dictionary<string, object> MachineParameters { get; set; }
        public float CurrentTime { get; set; }
        public float DeltaTime { get; set; }
        public string TriggerName { get; set; }
        public object TriggerData { get; set; }
    }

    // Data classes for serialization;

    internal class StateMachineData;
    {
        public Guid MachineId { get; set; }
        public string MachineName { get; set; }
        public StateMachineConfiguration Configuration { get; set; }
        public string CurrentStateId { get; set; }
        public string PreviousStateId { get; set; }
        public Dictionary<string, object> GlobalParameters { get; set; }
        public List<StateData> States { get; set; }
        public List<TransitionData> Transitions { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public string Version { get; set; }
    }

    internal class StateData;
    {
        public string StateId { get; set; }
        public string StateName { get; set; }
        public StateType StateType { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public List<string> OutgoingTransitions { get; set; }
    }

    internal class TransitionData;
    {
        public string TransitionId { get; set; }
        public string TransitionName { get; set; }
        public string SourceStateId { get; set; }
        public string TargetStateId { get; set; }
        public TransitionType TransitionType { get; set; }
        public TransitionEvaluationMode EvaluationMode { get; set; }
        public bool IsEnabled { get; set; }
        public float CoolDownTime { get; set; }
        public float LastTransitionTime { get; set; }
        public List<ConditionData> Conditions { get; set; }
    }

    internal class ConditionData;
    {
        public string ConditionId { get; set; }
        public string ConditionName { get; set; }
        public ConditionType ConditionType { get; set; }
        public ConditionOperator Operator { get; set; }
        public string ParameterName { get; set; }
        public object ExpectedValue { get; set; }
        public float Tolerance { get; set; }
    }

    // Debug and performance info classes;

    public class StateMachineDebugInfo;
    {
        public Guid MachineId { get; set; }
        public string MachineName { get; set; }
        public string CurrentStateId { get; set; }
        public string PreviousStateId { get; set; }
        public bool IsRunning { get; set; }
        public bool IsInitialized { get; set; }
        public int StateCount { get; set; }
        public int TransitionCount { get; set; }
        public int GlobalParameterCount { get; set; }
        public int TotalUpdates { get; set; }
        public int TotalTransitions { get; set; }
        public float TotalRunTime { get; set; }
        public StateMachinePerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class StateMachinePerformanceMetrics;
    {
        public float AverageUpdateTime { get; set; }
        public float MinUpdateTime { get; set; }
        public float MaxUpdateTime { get; set; }
        public int UpdateCount { get; set; }
        public Dictionary<string, float> OperationTimes { get; set; }
        public float TotalTrackedTime { get; set; }
    }

    // Event classes;

    public class StateMachineEvent : IEvent;
    {
        public string EventType { get; set; }
        public Guid MachineId { get; set; }
        public DateTime Timestamp { get; set; }
        public object Data { get; set; }
    }

    // Exception class;

    public class StateMachineException : Exception
    {
        public string ErrorCode { get; }

        public StateMachineException(string message) : base(message)
        {
            ErrorCode = ErrorCodes.StateMachineGenericError;
        }

        public StateMachineException(string message, string errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public StateMachineException(string message, Exception innerException, string errorCode)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Error codes;

    public static class ErrorCodes;
    {
        public const string StateMachineGenericError = "STATEMACHINE_001";
        public const string StateMachineInitializationFailed = "STATEMACHINE_002";
        public const string StateMachineNotInitialized = "STATEMACHINE_003";
        public const string StateMachineStartFailed = "STATEMACHINE_004";
        public const string StateMachineStopFailed = "STATEMACHINE_005";
        public const string StateMachineUpdateError = "STATEMACHINE_006";
        public const string StateAlreadyExists = "STATEMACHINE_007";
        public const string StateNotFound = "STATEMACHINE_008";
        public const string StateOperationFailed = "STATEMACHINE_009";
        public const string CannotRemoveCurrentState = "STATEMACHINE_010";
        public const string TransitionAlreadyExists = "STATEMACHINE_011";
        public const string TransitionNotFound = "STATEMACHINE_012";
        public const string TransitionOperationFailed = "STATEMACHINE_013";
        public const string TransitionFailed = "STATEMACHINE_014";
        public const string TransitionExecutionFailed = "STATEMACHINE_015";
        public const string TransitionEvaluationError = "STATEMACHINE_016";
        public const string TriggerProcessingFailed = "STATEMACHINE_017";
        public const string ParameterOperationFailed = "STATEMACHINE_018";
        public const string StateMachineSaveFailed = "STATEMACHINE_019";
        public const string StateMachineLoadFailed = "STATEMACHINE_020";
        public const string StateMachineDeserializationFailed = "STATEMACHINE_021";
        public const string StateMachineCloneFailed = "STATEMACHINE_022";
        public const string UpdateLoopError = "STATEMACHINE_023";
        public const string NoCurrentState = "STATEMACHINE_024";
    }
}
