using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.AI_Behaviors.AnimationBlueprints;
{
    /// <summary>
    /// Represents a state in the animation state machine;
    /// </summary>
    public class AnimState : INotifyPropertyChanged;
    {
        private string _stateId;
        private string _stateName;
        private StateType _stateType;
        private string _animationAsset;
        private double _blendTime;
        private bool _isDefaultState;
        private StateBlendMode _blendMode;
        private Dictionary<string, object> _stateParameters;

        public string StateId;
        {
            get => _stateId;
            set { _stateId = value; OnPropertyChanged(); }
        }

        public string StateName;
        {
            get => _stateName;
            set { _stateName = value; OnPropertyChanged(); }
        }

        public StateType StateType;
        {
            get => _stateType;
            set { _stateType = value; OnPropertyChanged(); }
        }

        public string AnimationAsset;
        {
            get => _animationAsset;
            set { _animationAsset = value; OnPropertyChanged(); }
        }

        public double BlendTime;
        {
            get => _blendTime;
            set { _blendTime = Math.Max(0, value); OnPropertyChanged(); }
        }

        public bool IsDefaultState;
        {
            get => _isDefaultState;
            set { _isDefaultState = value; OnPropertyChanged(); }
        }

        public StateBlendMode BlendMode;
        {
            get => _blendMode;
            set { _blendMode = value; OnPropertyChanged(); }
        }

        public Dictionary<string, object> StateParameters;
        {
            get => _stateParameters;
            set { _stateParameters = value; OnPropertyChanged(); }
        }

        public ObservableCollection<StateTransition> Transitions { get; } = new ObservableCollection<StateTransition>();
        public ObservableCollection<StateAction> EntryActions { get; } = new ObservableCollection<StateAction>();
        public ObservableCollection<StateAction> UpdateActions { get; } = new ObservableCollection<StateAction>();
        public ObservableCollection<StateAction> ExitActions { get; } = new ObservableCollection<StateAction>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public AnimState()
        {
            _stateParameters = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Represents a transition between states;
    /// </summary>
    public class StateTransition : INotifyPropertyChanged;
    {
        private string _transitionId;
        private string _sourceStateId;
        private string _targetStateId;
        private string _transitionName;
        private TransitionCondition _condition;
        private double _transitionTime;
        private TransitionType _transitionType;
        private bool _hasExitTime;
        private double _exitTime;

        public string TransitionId;
        {
            get => _transitionId;
            set { _transitionId = value; OnPropertyChanged(); }
        }

        public string SourceStateId;
        {
            get => _sourceStateId;
            set { _sourceStateId = value; OnPropertyChanged(); }
        }

        public string TargetStateId;
        {
            get => _targetStateId;
            set { _targetStateId = value; OnPropertyChanged(); }
        }

        public string TransitionName;
        {
            get => _transitionName;
            set { _transitionName = value; OnPropertyChanged(); }
        }

        public TransitionCondition Condition;
        {
            get => _condition;
            set { _condition = value; OnPropertyChanged(); }
        }

        public double TransitionTime;
        {
            get => _transitionTime;
            set { _transitionTime = Math.Max(0, value); OnPropertyChanged(); }
        }

        public TransitionType TransitionType;
        {
            get => _transitionType;
            set { _transitionType = value; OnPropertyChanged(); }
        }

        public bool HasExitTime;
        {
            get => _hasExitTime;
            set { _hasExitTime = value; OnPropertyChanged(); }
        }

        public double ExitTime;
        {
            get => _exitTime;
            set { _exitTime = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Main state machine controller for animation systems;
    /// </summary>
    public class StateMachine : INotifyPropertyChanged, IStateMachineController;
    {
        private readonly IAnimationController _animationController;
        private readonly IStateMachineExecutor _executor;
        private readonly StateMachineContext _context;

        private AnimState _currentState;
        private AnimState _previousState;
        private StateMachineStatus _status;
        private double _currentStateTime;
        private bool _isTransitioning;
        private StateTransition _activeTransition;

        public ObservableCollection<AnimState> States { get; } = new ObservableCollection<AnimState>();
        public ObservableCollection<StateMachineParameter> Parameters { get; } = new ObservableCollection<StateMachineParameter>();
        public ObservableCollection<StateMachineLayer> Layers { get; } = new ObservableCollection<StateMachineLayer>();

        public AnimState CurrentState;
        {
            get => _currentState;
            private set { _currentState = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentStateName)); }
        }

        public AnimState PreviousState;
        {
            get => _previousState;
            private set { _previousState = value; OnPropertyChanged(); }
        }

        public StateMachineStatus Status;
        {
            get => _status;
            private set { _status = value; OnPropertyChanged(); }
        }

        public string CurrentStateName => CurrentState?.StateName ?? "None";
        public double CurrentStateTime => _currentStateTime;
        public bool IsTransitioning => _isTransitioning;

        public string MachineName { get; set; }
        public StateMachineType MachineType { get; set; }
        public UpdateMode UpdateMode { get; set; } = UpdateMode.Auto;

        public event EventHandler<StateChangedEventArgs> StateChanged;
        public event EventHandler<TransitionStartedEventArgs> TransitionStarted;
        public event EventHandler<TransitionCompletedEventArgs> TransitionCompleted;
        public event EventHandler<StateMachineEventArgs> StateMachineEvent;

        public StateMachine(IAnimationController animationController, IStateMachineExecutor executor)
        {
            _animationController = animationController ?? throw new ArgumentNullException(nameof(animationController));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _context = new StateMachineContext(this);

            InitializeDefaultParameters();
            Status = StateMachineStatus.Stopped;
        }

        /// <summary>
        /// Initializes the state machine and enters the default state;
        /// </summary>
        public void Initialize()
        {
            if (Status != StateMachineStatus.Stopped)
                throw new InvalidOperationException("State machine is already initialized");

            var defaultState = States.FirstOrDefault(s => s.IsDefaultState) ?? States.FirstOrDefault();
            if (defaultState == null)
                throw new InvalidOperationException("No states defined in state machine");

            CurrentState = defaultState;
            _currentStateTime = 0;
            Status = StateMachineStatus.Running;

            ExecuteStateEntryActions(CurrentState);

            OnStateMachineEvent("StateMachine initialized", StateMachineEventType.Initialized);
        }

        /// <summary>
        /// Updates the state machine logic;
        /// </summary>
        public void Update(double deltaTime)
        {
            if (Status != StateMachineStatus.Running) return;

            try
            {
                _currentStateTime += deltaTime;

                if (_isTransitioning)
                {
                    UpdateActiveTransition(deltaTime);
                }
                else;
                {
                    UpdateCurrentState(deltaTime);
                    CheckTransitions();
                }

                UpdateLayers(deltaTime);
            }
            catch (Exception ex)
            {
                OnStateMachineEvent($"Update error: {ex.Message}", StateMachineEventType.Error);
                throw new StateMachineException("State machine update failed", ex);
            }
        }

        /// <summary>
        /// Forces a transition to a specific state;
        /// </summary>
        public void ForceTransition(string targetStateId, double transitionTime = 0.3)
        {
            var targetState = States.FirstOrDefault(s => s.StateId == targetStateId);
            if (targetState == null)
                throw new ArgumentException($"State with ID {targetStateId} not found");

            if (_isTransitioning)
            {
                OnStateMachineEvent("Cannot force transition while already transitioning", StateMachineEventType.Warning);
                return;
            }

            var transition = new StateTransition;
            {
                TransitionId = Guid.NewGuid().ToString(),
                SourceStateId = CurrentState.StateId,
                TargetStateId = targetStateId,
                TransitionTime = transitionTime,
                TransitionType = TransitionType.Immediate;
            };

            StartTransition(transition);
        }

        /// <summary>
        /// Sets a parameter value;
        /// </summary>
        public void SetParameter(string parameterName, object value)
        {
            var parameter = Parameters.FirstOrDefault(p => p.Name == parameterName);
            if (parameter == null)
            {
                OnStateMachineEvent($"Parameter {parameterName} not found", StateMachineEventType.Warning);
                return;
            }

            if (!parameter.ParameterType.IsInstanceOfType(value) && !TryConvertValue(value, parameter.ParameterType, out value))
            {
                throw new ArgumentException($"Invalid value type for parameter {parameterName}. Expected {parameter.ParameterType}, got {value.GetType()}");
            }

            parameter.Value = value;
            OnParameterChanged(parameter);
        }

        /// <summary>
        /// Gets a parameter value;
        /// </summary>
        public T GetParameter<T>(string parameterName)
        {
            var parameter = Parameters.FirstOrDefault(p => p.Name == parameterName);
            if (parameter == null)
                throw new ArgumentException($"Parameter {parameterName} not found");

            if (parameter.Value is T typedValue)
                return typedValue;

            throw new InvalidCastException($"Parameter {parameterName} is not of type {typeof(T)}");
        }

        /// <summary>
        /// Adds a new state to the state machine;
        /// </summary>
        public AnimState AddState(string stateName, StateType stateType = StateType.Simple, string animationAsset = null)
        {
            if (States.Any(s => s.StateName == stateName))
                throw new ArgumentException($"State with name {stateName} already exists");

            var state = new AnimState;
            {
                StateId = Guid.NewGuid().ToString(),
                StateName = stateName,
                StateType = stateType,
                AnimationAsset = animationAsset,
                BlendTime = 0.2;
            };

            States.Add(state);

            if (States.Count == 1)
            {
                state.IsDefaultState = true;
            }

            OnStateMachineEvent($"State {stateName} added", StateMachineEventType.StateAdded);
            return state;
        }

        /// <summary>
        /// Creates a transition between two states;
        /// </summary>
        public StateTransition CreateTransition(string sourceStateId, string targetStateId,
            TransitionCondition condition = null, string transitionName = null)
        {
            var sourceState = States.FirstOrDefault(s => s.StateId == sourceStateId);
            var targetState = States.FirstOrDefault(s => s.StateId == targetStateId);

            if (sourceState == null || targetState == null)
                throw new ArgumentException("Source or target state not found");

            var transition = new StateTransition;
            {
                TransitionId = Guid.NewGuid().ToString(),
                SourceStateId = sourceStateId,
                TargetStateId = targetStateId,
                TransitionName = transitionName ?? $"{sourceState.StateName}To{targetState.StateName}",
                Condition = condition,
                TransitionTime = 0.2,
                TransitionType = TransitionType.Smooth;
            };

            sourceState.Transitions.Add(transition);
            OnStateMachineEvent($"Transition {transition.TransitionName} created", StateMachineEventType.TransitionAdded);

            return transition;
        }

        /// <summary>
        /// Adds a state machine layer for complex animation blending;
        /// </summary>
        public StateMachineLayer AddLayer(string layerName, float weight = 1.0f, BlendMode layerBlendMode = BlendMode.Override)
        {
            var layer = new StateMachineLayer;
            {
                LayerId = Guid.NewGuid().ToString(),
                LayerName = layerName,
                Weight = weight,
                BlendMode = layerBlendMode,
                ParentMachine = this;
            };

            Layers.Add(layer);
            return layer;
        }

        /// <summary>
        /// Stops the state machine;
        /// </summary>
        public void Stop()
        {
            if (Status == StateMachineStatus.Stopped) return;

            if (CurrentState != null)
            {
                ExecuteStateExitActions(CurrentState);
            }

            Status = StateMachineStatus.Stopped;
            _isTransitioning = false;
            _activeTransition = null;

            OnStateMachineEvent("State machine stopped", StateMachineEventType.Stopped);
        }

        /// <summary>
        /// Resets the state machine to initial state;
        /// </summary>
        public void Reset()
        {
            Stop();
            _currentStateTime = 0;
            Initialize();
        }

        /// <summary>
        /// Validates the state machine structure;
        /// </summary>
        public StateMachineValidationResult Validate()
        {
            var result = new StateMachineValidationResult();

            // Check for states;
            if (!States.Any())
            {
                result.Errors.Add("No states defined in state machine");
            }

            // Check for default state;
            var defaultStates = States.Where(s => s.IsDefaultState).ToList();
            if (defaultStates.Count == 0)
            {
                result.Errors.Add("No default state defined");
            }
            else if (defaultStates.Count > 1)
            {
                result.Errors.Add("Multiple default states defined");
            }

            // Check state transitions;
            foreach (var state in States)
            {
                foreach (var transition in state.Transitions)
                {
                    var targetState = States.FirstOrDefault(s => s.StateId == transition.TargetStateId);
                    if (targetState == null)
                    {
                        result.Errors.Add($"Transition {transition.TransitionName} references non-existent target state {transition.TargetStateId}");
                    }
                }
            }

            // Check parameter usage;
            foreach (var parameter in Parameters)
            {
                // Validate parameter constraints;
                if (parameter.HasMinMax)
                {
                    if (Convert.ToDouble(parameter.Value) < parameter.MinValue ||
                        Convert.ToDouble(parameter.Value) > parameter.MaxValue)
                    {
                        result.Warnings.Add($"Parameter {parameter.Name} value is outside min/max range");
                    }
                }
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }

        /// <summary>
        /// Compiles the state machine to optimized runtime format;
        /// </summary>
        public async Task<StateMachineCompilationResult> CompileAsync()
        {
            var validationResult = Validate();
            if (!validationResult.IsValid)
            {
                return new StateMachineCompilationResult;
                {
                    Success = false,
                    Errors = validationResult.Errors;
                };
            }

            try
            {
                var compilationResult = await _executor.CompileAsync(this);
                OnStateMachineEvent("State machine compiled successfully", StateMachineEventType.Compiled);
                return compilationResult;
            }
            catch (Exception ex)
            {
                OnStateMachineEvent($"Compilation failed: {ex.Message}", StateMachineEventType.Error);
                throw new StateMachineException("State machine compilation failed", ex);
            }
        }

        private void UpdateCurrentState(double deltaTime)
        {
            if (CurrentState == null) return;

            // Execute state update actions;
            ExecuteStateUpdateActions(CurrentState);

            // Update state-specific logic based on state type;
            switch (CurrentState.StateType)
            {
                case StateType.BlendSpace:
                    UpdateBlendSpaceState(CurrentState, deltaTime);
                    break;
                case StateType.MultiDirectional:
                    UpdateMultiDirectionalState(CurrentState, deltaTime);
                    break;
            }
        }

        private void UpdateBlendSpaceState(AnimState state, double deltaTime)
        {
            // Implementation for blend space state updates;
            var blendX = GetParameter<float>("BlendX");
            var blendY = GetParameter<float>("BlendY");

            // Update blend weights based on parameters;
            _animationController.SetBlendParameters(blendX, blendY);
        }

        private void UpdateMultiDirectionalState(AnimState state, double deltaTime)
        {
            // Implementation for multi-directional state updates;
            var direction = GetParameter<float>("Direction");
            var speed = GetParameter<float>("Speed");

            // Update animation based on direction and speed;
            _animationController.SetMovementParameters(direction, speed);
        }

        private void CheckTransitions()
        {
            if (CurrentState == null || _isTransitioning) return;

            foreach (var transition in CurrentState.Transitions)
            {
                if (EvaluateTransitionCondition(transition))
                {
                    StartTransition(transition);
                    break;
                }
            }
        }

        private bool EvaluateTransitionCondition(StateTransition transition)
        {
            if (transition.Condition == null) return false;

            // Check exit time condition;
            if (transition.HasExitTime && _currentStateTime < transition.ExitTime)
                return false;

            // Evaluate condition using the state machine context;
            return transition.Condition.Evaluate(_context);
        }

        private void StartTransition(StateTransition transition)
        {
            _isTransitioning = true;
            _activeTransition = transition;

            var sourceState = CurrentState;
            var targetState = States.First(s => s.StateId == transition.TargetStateId);

            // Execute exit actions for current state;
            ExecuteStateExitActions(sourceState);

            OnTransitionStarted(new TransitionStartedEventArgs;
            {
                Transition = transition,
                SourceState = sourceState,
                TargetState = targetState,
                Timestamp = DateTime.UtcNow;
            });

            // Start animation transition;
            _animationController.StartTransition(sourceState.AnimationAsset, targetState.AnimationAsset,
                transition.TransitionTime, transition.TransitionType);

            // If immediate transition, complete immediately;
            if (transition.TransitionType == TransitionType.Immediate || transition.TransitionTime <= 0)
            {
                CompleteTransition(transition, targetState);
            }
        }

        private void UpdateActiveTransition(double deltaTime)
        {
            if (_activeTransition == null) return;

            // Update transition progress;
            // In a real implementation, this would track the actual animation blend progress;

            // For now, simulate completion after transition time;
            if (_currentStateTime >= _activeTransition.TransitionTime)
            {
                var targetState = States.First(s => s.StateId == _activeTransition.TargetStateId);
                CompleteTransition(_activeTransition, targetState);
            }
        }

        private void CompleteTransition(StateTransition transition, AnimState targetState)
        {
            PreviousState = CurrentState;
            CurrentState = targetState;
            _currentStateTime = 0;
            _isTransitioning = false;
            _activeTransition = null;

            // Execute entry actions for new state;
            ExecuteStateEntryActions(CurrentState);

            OnTransitionCompleted(new TransitionCompletedEventArgs;
            {
                Transition = transition,
                PreviousState = PreviousState,
                NewState = CurrentState,
                Timestamp = DateTime.UtcNow;
            });

            OnStateChanged(new StateChangedEventArgs;
            {
                PreviousState = PreviousState,
                NewState = CurrentState,
                Transition = transition,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void UpdateLayers(double deltaTime)
        {
            foreach (var layer in Layers.Where(l => l.IsEnabled))
            {
                layer.Update(deltaTime);
            }
        }

        private void ExecuteStateEntryActions(AnimState state)
        {
            foreach (var action in state.EntryActions)
            {
                action.Execute(_context);
            }
        }

        private void ExecuteStateUpdateActions(AnimState state)
        {
            foreach (var action in state.UpdateActions)
            {
                action.Execute(_context);
            }
        }

        private void ExecuteStateExitActions(AnimState state)
        {
            foreach (var action in state.ExitActions)
            {
                action.Execute(_context);
            }
        }

        private void InitializeDefaultParameters()
        {
            // Add common animation parameters;
            AddParameter("Speed", 0.0f, -1.0f, 1.0f);
            AddParameter("Direction", 0.0f, -180.0f, 180.0f);
            AddParameter("IsGrounded", true);
            AddParameter("IsMoving", false);
            AddParameter("BlendX", 0.0f, -1.0f, 1.0f);
            AddParameter("BlendY", 0.0f, -1.0f, 1.0f);
        }

        private void AddParameter<T>(string name, T defaultValue, T minValue = default, T maxValue = default) where T : struct;
        {
            var parameter = new StateMachineParameter;
            {
                Name = name,
                ParameterType = typeof(T),
                Value = defaultValue,
                DefaultValue = defaultValue;
            };

            if (!minValue.Equals(default(T)) && !maxValue.Equals(default(T)))
            {
                parameter.MinValue = Convert.ToDouble(minValue);
                parameter.MaxValue = Convert.ToDouble(maxValue);
                parameter.HasMinMax = true;
            }

            Parameters.Add(parameter);
        }

        private bool TryConvertValue(object value, Type targetType, out object convertedValue)
        {
            convertedValue = null;
            try
            {
                convertedValue = Convert.ChangeType(value, targetType);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnParameterChanged(StateMachineParameter parameter)
        {
            StateMachineEvent?.Invoke(this, new StateMachineEventArgs;
            {
                EventType = StateMachineEventType.ParameterChanged,
                Message = $"Parameter {parameter.Name} changed to {parameter.Value}",
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnStateChanged(StateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        private void OnTransitionStarted(TransitionStartedEventArgs e)
        {
            TransitionStarted?.Invoke(this, e);
        }

        private void OnTransitionCompleted(TransitionCompletedEventArgs e)
        {
            TransitionCompleted?.Invoke(this, e);
        }

        private void OnStateMachineEvent(string message, StateMachineEventType eventType)
        {
            StateMachineEvent?.Invoke(this, new StateMachineEventArgs;
            {
                EventType = eventType,
                Message = message,
                Timestamp = DateTime.UtcNow;
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum StateType;
    {
        Simple,
        BlendSpace,
        MultiDirectional,
        Composite,
        SubStateMachine;
    }

    public enum StateMachineStatus;
    {
        Stopped,
        Running,
        Paused,
        Error;
    }

    public enum TransitionType;
    {
        Immediate,
        Smooth,
        CrossFade,
        Frozen;
    }

    public enum StateBlendMode;
    {
        Override,
        Additive,
        Multiply;
    }

    public enum BlendMode;
    {
        Override,
        Additive,
        Multiply;
    }

    public enum UpdateMode;
    {
        Auto,
        Manual,
        Fixed;
    }

    public enum StateMachineEventType;
    {
        Initialized,
        Stopped,
        StateChanged,
        TransitionStarted,
        TransitionCompleted,
        ParameterChanged,
        StateAdded,
        TransitionAdded,
        Compiled,
        Error,
        Warning;
    }

    public interface IStateMachineController;
    {
        void Update(double deltaTime);
        void SetParameter(string parameterName, object value);
        T GetParameter<T>(string parameterName);
        void ForceTransition(string targetStateId, double transitionTime = 0.3);
    }

    public interface IAnimationController;
    {
        void StartTransition(string fromAnimation, string toAnimation, double transitionTime, TransitionType transitionType);
        void SetBlendParameters(float x, float y);
        void SetMovementParameters(float direction, float speed);
    }

    public interface IStateMachineExecutor;
    {
        Task<StateMachineCompilationResult> CompileAsync(StateMachine stateMachine);
        void Execute(StateMachine stateMachine);
    }

    public class StateMachineContext;
    {
        private readonly StateMachine _stateMachine;

        public StateMachineContext(StateMachine stateMachine)
        {
            _stateMachine = stateMachine;
        }

        public T GetParameter<T>(string name) => _stateMachine.GetParameter<T>(name);
        public AnimState CurrentState => _stateMachine.CurrentState;
        public double CurrentStateTime => _stateMachine.CurrentStateTime;
        public bool IsTransitioning => _stateMachine.IsTransitioning;
    }

    public class TransitionCondition;
    {
        public string Expression { get; set; }
        public List<ConditionParameter> Parameters { get; set; } = new List<ConditionParameter>();

        public bool Evaluate(StateMachineContext context)
        {
            // Implementation for evaluating condition expression;
            // This would typically use an expression evaluator;
            return true; // Simplified for example;
        }
    }

    public class ConditionParameter;
    {
        public string Name { get; set; }
        public object Value { get; set; }
        public ConditionOperator Operator { get; set; }
    }

    public enum ConditionOperator;
    {
        Equals,
        NotEquals,
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual;
    }

    public class StateAction;
    {
        public string ActionId { get; set; }
        public string ActionName { get; set; }
        public ActionType ActionType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public virtual void Execute(StateMachineContext context)
        {
            // Base implementation - override in derived classes;
        }
    }

    public enum ActionType;
    {
        SetParameter,
        TriggerEvent,
        PlaySound,
        SpawnEffect,
        Custom;
    }

    public class StateMachineParameter : INotifyPropertyChanged;
    {
        private string _name;
        private Type _parameterType;
        private object _value;
        private object _defaultValue;
        private double _minValue;
        private double _maxValue;
        private bool _hasMinMax;

        public string Name;
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public Type ParameterType;
        {
            get => _parameterType;
            set { _parameterType = value; OnPropertyChanged(); }
        }

        public object Value;
        {
            get => _value;
            set { _value = value; OnPropertyChanged(); }
        }

        public object DefaultValue;
        {
            get => _defaultValue;
            set { _defaultValue = value; OnPropertyChanged(); }
        }

        public double MinValue;
        {
            get => _minValue;
            set { _minValue = value; OnPropertyChanged(); }
        }

        public double MaxValue;
        {
            get => _maxValue;
            set { _maxValue = value; OnPropertyChanged(); }
        }

        public bool HasMinMax;
        {
            get => _hasMinMax;
            set { _hasMinMax = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class StateMachineLayer;
    {
        public string LayerId { get; set; }
        public string LayerName { get; set; }
        public float Weight { get; set; } = 1.0f;
        public BlendMode BlendMode { get; set; }
        public bool IsEnabled { get; set; } = true;
        public StateMachine ParentMachine { get; set; }
        public StateMachine SubStateMachine { get; set; }

        public void Update(double deltaTime)
        {
            if (IsEnabled && SubStateMachine != null)
            {
                SubStateMachine.Update(deltaTime);
            }
        }
    }

    public class StateMachineValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class StateMachineCompilationResult;
    {
        public bool Success { get; set; }
        public byte[] CompiledBytecode { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public TimeSpan CompilationTime { get; set; }
    }

    #endregion;

    #region Event Args;

    public class StateChangedEventArgs : EventArgs;
    {
        public AnimState PreviousState { get; set; }
        public AnimState NewState { get; set; }
        public StateTransition Transition { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TransitionStartedEventArgs : EventArgs;
    {
        public StateTransition Transition { get; set; }
        public AnimState SourceState { get; set; }
        public AnimState TargetState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TransitionCompletedEventArgs : EventArgs;
    {
        public StateTransition Transition { get; set; }
        public AnimState PreviousState { get; set; }
        public AnimState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StateMachineEventArgs : EventArgs;
    {
        public StateMachineEventType EventType { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class StateMachineException : Exception
    {
        public StateMachineException(string message) : base(message) { }
        public StateMachineException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class StateValidationException : StateMachineException;
    {
        public StateValidationException(string message) : base(message) { }
    }

    public class TransitionException : StateMachineException;
    {
        public TransitionException(string message) : base(message) { }
    }

    #endregion;
}
