using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NEDA.CharacterSystems.AI_Behaviors.AnimationBlueprints;
{
    /// <summary>
    /// Represents an animation state with entry, update, and exit logic;
    /// </summary>
    public class AnimState : INotifyPropertyChanged;
    {
        private string _stateId;
        private string _stateName;
        private StateType _stateType;
        private string _animationAsset;
        private float _speed = 1.0f;
        private bool _isLooping = true;
        private BlendMode _blendMode;
        private float _blendTime = 0.2f;
        private bool _isDefaultState;
        private StateBlendSettings _blendSettings;
        private Dictionary<string, object> _stateData;

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

        public float Speed;
        {
            get => _speed;
            set { _speed = Math.Max(0.01f, value); OnPropertyChanged(); }
        }

        public bool IsLooping;
        {
            get => _isLooping;
            set { _isLooping = value; OnPropertyChanged(); }
        }

        public BlendMode BlendMode;
        {
            get => _blendMode;
            set { _blendMode = value; OnPropertyChanged(); }
        }

        public float BlendTime;
        {
            get => _blendTime;
            set { _blendTime = Math.Max(0, value); OnPropertyChanged(); }
        }

        public bool IsDefaultState;
        {
            get => _isDefaultState;
            set { _isDefaultState = value; OnPropertyChanged(); }
        }

        public StateBlendSettings BlendSettings;
        {
            get => _blendSettings;
            set { _blendSettings = value; OnPropertyChanged(); }
        }

        public Dictionary<string, object> StateData;
        {
            get => _stateData;
            set { _stateData = value; OnPropertyChanged(); }
        }

        public ObservableCollection<StateTransition> Transitions { get; } = new ObservableCollection<StateTransition>();
        public ObservableCollection<StateAction> EntryActions { get; } = new ObservableCollection<StateAction>();
        public ObservableCollection<StateAction> UpdateActions { get; } = new ObservableCollection<StateAction>();
        public ObservableCollection<StateAction> ExitActions { get; } = new ObservableCollection<StateAction>();
        public ObservableCollection<StateEvent> StateEvents { get; } = new ObservableCollection<StateEvent>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public AnimState()
        {
            _stateId = Guid.NewGuid().ToString();
            _blendSettings = new StateBlendSettings();
            _stateData = new Dictionary<string, object>();
        }

        public AnimState(string name, StateType type = StateType.Simple) : this()
        {
            _stateName = name;
            _stateType = type;
        }

        public void AddTransition(AnimState targetState, TransitionCondition condition, float transitionTime = 0.2f)
        {
            var transition = new StateTransition;
            {
                SourceState = this,
                TargetState = targetState,
                Condition = condition,
                TransitionTime = transitionTime;
            };

            Transitions.Add(transition);
        }

        public void AddEntryAction(StateAction action)
        {
            EntryActions.Add(action);
        }

        public void AddUpdateAction(StateAction action)
        {
            UpdateActions.Add(action);
        }

        public void AddExitAction(StateAction action)
        {
            ExitActions.Add(action);
        }

        public void AddStateEvent(StateEvent stateEvent)
        {
            StateEvents.Add(stateEvent);
        }

        public T GetStateData<T>(string key, T defaultValue = default)
        {
            return _stateData.TryGetValue(key, out object value) && value is T typedValue ? typedValue : defaultValue;
        }

        public void SetStateData<T>(string key, T value)
        {
            _stateData[key] = value;
            OnPropertyChanged(nameof(StateData));
        }

        public override string ToString() => $"{StateName} ({StateType})";
    }

    /// <summary>
    /// Represents a transition between animation states;
    /// </summary>
    public class StateTransition : INotifyPropertyChanged;
    {
        private string _transitionId;
        private AnimState _sourceState;
        private AnimState _targetState;
        private TransitionCondition _condition;
        private float _transitionTime = 0.2f;
        private TransitionType _transitionType;
        private bool _hasExitTime;
        private float _exitTime;
        private float _transitionOffset;
        private bool _canTransitionToSelf;

        public string TransitionId;
        {
            get => _transitionId;
            set { _transitionId = value; OnPropertyChanged(); }
        }

        public AnimState SourceState;
        {
            get => _sourceState;
            set { _sourceState = value; OnPropertyChanged(); }
        }

        public AnimState TargetState;
        {
            get => _targetState;
            set { _targetState = value; OnPropertyChanged(); }
        }

        public TransitionCondition Condition;
        {
            get => _condition;
            set { _condition = value; OnPropertyChanged(); }
        }

        public float TransitionTime;
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

        public float ExitTime;
        {
            get => _exitTime;
            set { _exitTime = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public float TransitionOffset;
        {
            get => _transitionOffset;
            set { _transitionOffset = value; OnPropertyChanged(); }
        }

        public bool CanTransitionToSelf;
        {
            get => _canTransitionToSelf;
            set { _canTransitionToSelf = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public StateTransition()
        {
            _transitionId = Guid.NewGuid().ToString();
            _transitionType = TransitionType.Smooth;
        }

        public bool CanTransition(AnimStateMachineContext context)
        {
            if (Condition == null) return false;

            // Check exit time condition;
            if (HasExitTime && context.CurrentStateTime < ExitTime)
                return false;

            // Check transition condition;
            return Condition.Evaluate(context);
        }

        public float GetActualTransitionTime()
        {
            return TransitionTime + TransitionOffset;
        }
    }

    /// <summary>
    /// Main animation state machine controller;
    /// </summary>
    public class AnimStateMachine : INotifyPropertyChanged, IAnimStateController;
    {
        private readonly IAnimationController _animationController;
        private readonly IStateMachineSolver _solver;
        private readonly IStateMachineValidator _validator;

        private AnimState _currentState;
        private AnimState _previousState;
        private AnimState _nextState;
        private StateMachineStatus _status;
        private float _currentStateTime;
        private bool _isTransitioning;
        private StateTransition _activeTransition;
        private float _transitionProgress;
        private StateMachineContext _context;

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

        public AnimState NextState;
        {
            get => _nextState;
            private set { _nextState = value; OnPropertyChanged(); }
        }

        public StateMachineStatus Status;
        {
            get => _status;
            private set { _status = value; OnPropertyChanged(); }
        }

        public float CurrentStateTime;
        {
            get => _currentStateTime;
            private set { _currentStateTime = value; OnPropertyChanged(); }
        }

        public bool IsTransitioning;
        {
            get => _isTransitioning;
            private set { _isTransitioning = value; OnPropertyChanged(); }
        }

        public float TransitionProgress;
        {
            get => _transitionProgress;
            private set { _transitionProgress = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public string CurrentStateName => CurrentState?.StateName ?? "None";
        public string PreviousStateName => PreviousState?.StateName ?? "None";

        public ObservableCollection<AnimState> States { get; } = new ObservableCollection<AnimState>();
        public ObservableCollection<StateMachineParameter> Parameters { get; } = new ObservableCollection<StateMachineParameter>();
        public ObservableCollection<StateMachineLayer> Layers { get; } = new ObservableCollection<StateMachineLayer>();
        public ObservableCollection<StateTransition> ActiveTransitions { get; } = new ObservableCollection<StateTransition>();

        public string MachineName { get; set; }
        public StateMachineType MachineType { get; set; }
        public UpdateMode UpdateMode { get; set; } = UpdateMode.Auto;

        public event EventHandler<StateChangedEventArgs> StateChanged;
        public event EventHandler<TransitionStartedEventArgs> TransitionStarted;
        public event EventHandler<TransitionCompletedEventArgs> TransitionCompleted;
        public event EventHandler<StateMachineEventEventArgs> StateMachineEvent;

        public AnimStateMachine(IAnimationController animationController, IStateMachineSolver solver = null,
                              IStateMachineValidator validator = null)
        {
            _animationController = animationController ?? throw new ArgumentNullException(nameof(animationController));
            _solver = solver ?? new DefaultStateMachineSolver();
            _validator = validator;
            _context = new StateMachineContext(this);

            InitializeDefaultParameters();
            Status = StateMachineStatus.Stopped;

            States.CollectionChanged += OnStatesCollectionChanged;
        }

        /// <summary>
        /// Initializes the state machine and enters the default state;
        /// </summary>
        public void Initialize()
        {
            if (Status != StateMachineStatus.Stopped)
                throw new InvalidOperationException("State machine is already initialized");

            var validationResult = Validate();
            if (!validationResult.IsValid)
            {
                throw new StateMachineValidationException($"State machine validation failed: {string.Join(", ", validationResult.Errors)}");
            }

            var defaultState = States.FirstOrDefault(s => s.IsDefaultState) ?? States.FirstOrDefault();
            if (defaultState == null)
                throw new InvalidOperationException("No states defined in state machine");

            CurrentState = defaultState;
            CurrentStateTime = 0;
            Status = StateMachineStatus.Ready;

            ExecuteStateEntryActions(CurrentState);

            OnStateMachineEvent("StateMachine initialized", StateMachineEventType.Initialized);

            // Start animation for initial state;
            if (!string.IsNullOrEmpty(CurrentState.AnimationAsset))
            {
                _animationController.PlayAnimation(CurrentState.AnimationAsset, CurrentState.Speed, CurrentState.IsLooping);
            }
        }

        /// <summary>
        /// Updates the state machine logic;
        /// </summary>
        public void Update(float deltaTime)
        {
            if (Status != StateMachineStatus.Ready && Status != StateMachineStatus.Running) return;

            try
            {
                Status = StateMachineStatus.Running;

                if (IsTransitioning)
                {
                    UpdateTransition(deltaTime);
                }
                else;
                {
                    UpdateCurrentState(deltaTime);
                    CheckTransitions();
                }

                UpdateLayers(deltaTime);
                UpdateParameters(deltaTime);

                OnStateMachineEvent("State machine updated", StateMachineEventType.Updated);
            }
            catch (Exception ex)
            {
                Status = StateMachineStatus.Error;
                OnStateMachineEvent($"Update error: {ex.Message}", StateMachineEventType.Error);
                throw new StateMachineException("State machine update failed", ex);
            }
        }

        /// <summary>
        /// Forces a transition to a specific state;
        /// </summary>
        public void ForceTransition(string targetStateName, float transitionTime = 0.2f)
        {
            var targetState = States.FirstOrDefault(s => s.StateName == targetStateName);
            if (targetState == null)
                throw new ArgumentException($"State with name '{targetStateName}' not found");

            if (IsTransitioning)
            {
                OnStateMachineEvent("Cannot force transition while already transitioning", StateMachineEventType.Warning);
                return;
            }

            var transition = new StateTransition;
            {
                SourceState = CurrentState,
                TargetState = targetState,
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
                OnStateMachineEvent($"Parameter '{parameterName}' not found", StateMachineEventType.Warning);
                return;
            }

            if (!parameter.ParameterType.IsInstanceOfType(value) && !TryConvertValue(value, parameter.ParameterType, out value))
            {
                throw new ArgumentException($"Invalid value type for parameter '{parameterName}'. Expected {parameter.ParameterType}, got {value.GetType()}");
            }

            var oldValue = parameter.Value;
            parameter.Value = value;

            OnParameterChanged(parameter, oldValue, value);
        }

        /// <summary>
        /// Gets a parameter value;
        /// </summary>
        public T GetParameter<T>(string parameterName)
        {
            var parameter = Parameters.FirstOrDefault(p => p.Name == parameterName);
            if (parameter == null)
                throw new ArgumentException($"Parameter '{parameterName}' not found");

            if (parameter.Value is T typedValue)
                return typedValue;

            throw new InvalidCastException($"Parameter '{parameterName}' is not of type {typeof(T)}");
        }

        /// <summary>
        /// Adds a new state to the state machine;
        /// </summary>
        public AnimState AddState(string stateName, StateType stateType = StateType.Simple, string animationAsset = null)
        {
            if (States.Any(s => s.StateName == stateName))
                throw new ArgumentException($"State with name '{stateName}' already exists");

            var state = new AnimState(stateName, stateType)
            {
                AnimationAsset = animationAsset;
            };

            States.Add(state);

            // Set as default if it's the first state;
            if (States.Count == 1)
            {
                state.IsDefaultState = true;
            }

            OnStateMachineEvent($"State '{stateName}' added", StateMachineEventType.StateAdded);
            return state;
        }

        /// <summary>
        /// Removes a state from the state machine;
        /// </summary>
        public bool RemoveState(string stateName)
        {
            var state = States.FirstOrDefault(s => s.StateName == stateName);
            if (state == null) return false;

            // Cannot remove current state;
            if (state == CurrentState)
            {
                OnStateMachineEvent("Cannot remove current state", StateMachineEventType.Warning);
                return false;
            }

            // Remove all transitions involving this state;
            var transitionsToRemove = States;
                .SelectMany(s => s.Transitions)
                .Where(t => t.SourceState == state || t.TargetState == state)
                .ToList();

            foreach (var transition in transitionsToRemove)
            {
                transition.SourceState.Transitions.Remove(transition);
            }

            States.Remove(state);

            OnStateMachineEvent($"State '{stateName}' removed", StateMachineEventType.StateRemoved);
            return true;
        }

        /// <summary>
        /// Creates a transition between two states;
        /// </summary>
        public StateTransition CreateTransition(string sourceStateName, string targetStateName,
            TransitionCondition condition = null, string transitionName = null)
        {
            var sourceState = States.FirstOrDefault(s => s.StateName == sourceStateName);
            var targetState = States.FirstOrDefault(s => s.StateName == targetStateName);

            if (sourceState == null || targetState == null)
                throw new ArgumentException("Source or target state not found");

            var transition = new StateTransition;
            {
                SourceState = sourceState,
                TargetState = targetState,
                Condition = condition,
                TransitionName = transitionName ?? $"{sourceStateName}To{targetStateName}"
            };

            sourceState.Transitions.Add(transition);
            OnStateMachineEvent($"Transition '{transition.TransitionName}' created", StateMachineEventType.TransitionAdded);

            return transition;
        }

        /// <summary>
        /// Adds a state machine layer for complex animation blending;
        /// </summary>
        public StateMachineLayer AddLayer(string layerName, float weight = 1.0f, LayerBlendMode blendMode = LayerBlendMode.Override)
        {
            var layer = new StateMachineLayer;
            {
                LayerName = layerName,
                Weight = weight,
                BlendMode = blendMode,
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
            IsTransitioning = false;
            _activeTransition = null;

            _animationController.StopAllAnimations();

            OnStateMachineEvent("State machine stopped", StateMachineEventType.Stopped);
        }

        /// <summary>
        /// Resets the state machine to initial state;
        /// </summary>
        public void Reset()
        {
            Stop();
            CurrentStateTime = 0;
            TransitionProgress = 0;
            Initialize();
        }

        /// <summary>
        /// Validates the state machine structure;
        /// </summary>
        public StateMachineValidationResult Validate()
        {
            if (_validator == null)
                return new StateMachineValidationResult { IsValid = true };

            var result = _validator.Validate(this);

            OnStateMachineEvent($"State machine validation completed: {(result.IsValid ? "Valid" : "Invalid")}",
                StateMachineEventType.Validated);

            return result;
        }

        /// <summary>
        /// Gets the current state machine statistics;
        /// </summary>
        public StateMachineStatistics GetStatistics()
        {
            return new StateMachineStatistics;
            {
                TotalStates = States.Count,
                TotalTransitions = States.Sum(s => s.Transitions.Count),
                TotalParameters = Parameters.Count,
                TotalLayers = Layers.Count,
                CurrentStateTime = CurrentStateTime,
                IsTransitioning = IsTransitioning,
                TransitionProgress = TransitionProgress;
            };
        }

        private void UpdateCurrentState(float deltaTime)
        {
            if (CurrentState == null) return;

            CurrentStateTime += deltaTime;

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
                case StateType.Random:
                    UpdateRandomState(CurrentState, deltaTime);
                    break;
            }

            // Trigger state events based on time;
            CheckStateEvents(CurrentState);
        }

        private void UpdateBlendSpaceState(AnimState state, float deltaTime)
        {
            var blendX = GetParameter<float>("BlendX");
            var blendY = GetParameter<float>("BlendY");

            _animationController.SetBlendParameters(blendX, blendY);
        }

        private void UpdateMultiDirectionalState(AnimState state, float deltaTime)
        {
            var direction = GetParameter<float>("Direction");
            var speed = GetParameter<float>("Speed");

            _animationController.SetMovementParameters(direction, speed);
        }

        private void UpdateRandomState(AnimState state, float deltaTime)
        {
            // Random state logic for variety;
        }

        private void CheckStateEvents(AnimState state)
        {
            foreach (var stateEvent in state.StateEvents)
            {
                if (stateEvent.ShouldTrigger(CurrentStateTime))
                {
                    stateEvent.Trigger(_context);
                }
            }
        }

        private void CheckTransitions()
        {
            if (CurrentState == null || IsTransitioning) return;

            foreach (var transition in CurrentState.Transitions)
            {
                if (transition.CanTransition(_context))
                {
                    StartTransition(transition);
                    break;
                }
            }
        }

        private void StartTransition(StateTransition transition)
        {
            IsTransitioning = true;
            _activeTransition = transition;
            NextState = transition.TargetState;
            TransitionProgress = 0;

            var sourceState = CurrentState;
            var targetState = transition.TargetState;

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
            if (!string.IsNullOrEmpty(sourceState.AnimationAsset) && !string.IsNullOrEmpty(targetState.AnimationAsset))
            {
                _animationController.StartTransition(
                    sourceState.AnimationAsset,
                    targetState.AnimationAsset,
                    transition.GetActualTransitionTime(),
                    transition.TransitionType);
            }
            else if (!string.IsNullOrEmpty(targetState.AnimationAsset))
            {
                _animationController.PlayAnimation(targetState.AnimationAsset, targetState.Speed, targetState.IsLooping);
            }

            // If immediate transition, complete immediately;
            if (transition.TransitionType == TransitionType.Immediate || transition.GetActualTransitionTime() <= 0)
            {
                CompleteTransition(transition, targetState);
            }
        }

        private void UpdateTransition(float deltaTime)
        {
            if (_activeTransition == null) return;

            TransitionProgress += deltaTime / _activeTransition.GetActualTransitionTime();

            if (TransitionProgress >= 1.0f)
            {
                CompleteTransition(_activeTransition, NextState);
            }
        }

        private void CompleteTransition(StateTransition transition, AnimState targetState)
        {
            PreviousState = CurrentState;
            CurrentState = targetState;
            CurrentStateTime = 0;
            IsTransitioning = false;
            _activeTransition = null;
            NextState = null;
            TransitionProgress = 0;

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

        private void UpdateLayers(float deltaTime)
        {
            foreach (var layer in Layers.Where(l => l.IsEnabled))
            {
                layer.Update(deltaTime);
            }
        }

        private void UpdateParameters(float deltaTime)
        {
            foreach (var parameter in Parameters.Where(p => p.UpdateMode == ParameterUpdateMode.Auto))
            {
                parameter.Update(deltaTime);
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
            AddParameter("Speed", 0.0f);
            AddParameter("Direction", 0.0f);
            AddParameter("IsGrounded", true);
            AddParameter("IsMoving", false);
            AddParameter("BlendX", 0.0f);
            AddParameter("BlendY", 0.0f);
            AddParameter("Trigger", false);
        }

        private StateMachineParameter AddParameter<T>(string name, T defaultValue)
        {
            var parameter = new StateMachineParameter;
            {
                Name = name,
                ParameterType = typeof(T),
                Value = defaultValue,
                DefaultValue = defaultValue;
            };

            Parameters.Add(parameter);
            return parameter;
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

        private void OnParameterChanged(StateMachineParameter parameter, object oldValue, object newValue)
        {
            StateMachineEvent?.Invoke(this, new StateMachineEventEventArgs;
            {
                EventType = StateMachineEventType.ParameterChanged,
                Message = $"Parameter '{parameter.Name}' changed from {oldValue} to {newValue}",
                Parameter = parameter,
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
            StateMachineEvent?.Invoke(this, new StateMachineEventEventArgs;
            {
                EventType = eventType,
                Message = message,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnStatesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Handle state collection changes;
            if (e.NewItems != null)
            {
                foreach (AnimState state in e.NewItems)
                {
                    // Wire up state events if needed;
                }
            }
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
        Random,
        Composite,
        SubStateMachine;
    }

    public enum StateMachineStatus;
    {
        Stopped,
        Ready,
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
        StateRemoved,
        TransitionAdded,
        Updated,
        Validated,
        Error,
        Warning;
    }

    public enum StateMachineType;
    {
        Locomotion,
        Combat,
        Interaction,
        Custom;
    }

    public enum LayerBlendMode;
    {
        Override,
        Additive,
        Multiply;
    }

    public interface IAnimStateController;
    {
        void Update(float deltaTime);
        void SetParameter(string parameterName, object value);
        T GetParameter<T>(string parameterName);
        void ForceTransition(string targetStateName, float transitionTime = 0.2f);
    }

    public interface IStateMachineSolver;
    {
        void Solve(AnimStateMachine stateMachine, float deltaTime);
    }

    public interface IStateMachineValidator;
    {
        StateMachineValidationResult Validate(AnimStateMachine stateMachine);
    }

    public class StateMachineContext;
    {
        private readonly AnimStateMachine _stateMachine;

        public StateMachineContext(AnimStateMachine stateMachine)
        {
            _stateMachine = stateMachine;
        }

        public T GetParameter<T>(string name) => _stateMachine.GetParameter<T>(name);
        public void SetParameter<T>(string name, T value) => _stateMachine.SetParameter(name, value);
        public AnimState CurrentState => _stateMachine.CurrentState;
        public float CurrentStateTime => _stateMachine.CurrentStateTime;
        public bool IsTransitioning => _stateMachine.IsTransitioning;
    }

    public class StateBlendSettings;
    {
        public float PositionThreshold { get; set; } = 0.1f;
        public float RotationThreshold { get; set; } = 5.0f;
        public BlendCurve BlendCurve { get; set; } = BlendCurve.Linear;
        public bool SyncTiming { get; set; } = true;
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

    public class StateEvent;
    {
        public string EventId { get; set; }
        public string EventName { get; set; }
        public EventTriggerType TriggerType { get; set; }
        public float TriggerTime { get; set; }
        public List<StateAction> Actions { get; set; } = new List<StateAction>();

        public bool ShouldTrigger(float currentTime)
        {
            return TriggerType == EventTriggerType.TimeBased && Math.Abs(currentTime - TriggerTime) < 0.001f;
        }

        public void Trigger(StateMachineContext context)
        {
            foreach (var action in Actions)
            {
                action.Execute(context);
            }
        }
    }

    public enum EventTriggerType;
    {
        TimeBased,
        ParameterBased,
        Custom;
    }

    public class StateMachineParameter : INotifyPropertyChanged;
    {
        private string _name;
        private Type _parameterType;
        private object _value;
        private object _defaultValue;
        private ParameterUpdateMode _updateMode;
        private float _smoothing;

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

        public ParameterUpdateMode UpdateMode;
        {
            get => _updateMode;
            set { _updateMode = value; OnPropertyChanged(); }
        }

        public float Smoothing;
        {
            get => _smoothing;
            set { _smoothing = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Update(float deltaTime)
        {
            // Parameter smoothing and auto-update logic;
        }
    }

    public enum ParameterUpdateMode;
    {
        Manual,
        Auto,
        Scripted;
    }

    public class StateMachineLayer;
    {
        public string LayerName { get; set; }
        public float Weight { get; set; } = 1.0f;
        public LayerBlendMode BlendMode { get; set; }
        public bool IsEnabled { get; set; } = true;
        public AnimStateMachine ParentMachine { get; set; }
        public AnimStateMachine SubStateMachine { get; set; }

        public void Update(float deltaTime)
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

    public class StateMachineStatistics;
    {
        public int TotalStates { get; set; }
        public int TotalTransitions { get; set; }
        public int TotalParameters { get; set; }
        public int TotalLayers { get; set; }
        public float CurrentStateTime { get; set; }
        public bool IsTransitioning { get; set; }
        public float TransitionProgress { get; set; }
    }

    public enum BlendCurve;
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
        Custom;
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

    public class StateMachineEventEventArgs : EventArgs;
    {
        public StateMachineEventType EventType { get; set; }
        public string Message { get; set; }
        public StateMachineParameter Parameter { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Default Implementations;

    public class DefaultStateMachineSolver : IStateMachineSolver;
    {
        public void Solve(AnimStateMachine stateMachine, float deltaTime)
        {
            // Default state machine solving logic;
            // This could include complex transition logic, 
            // priority-based transition selection, etc.
        }
    }

    #endregion;

    #region Exceptions;

    public class StateMachineException : Exception
    {
        public StateMachineException(string message) : base(message) { }
        public StateMachineException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class StateMachineValidationException : StateMachineException;
    {
        public StateMachineValidationException(string message) : base(message) { }
    }

    #endregion;
}
