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
    /// Represents a transition condition with evaluation logic;
    /// </summary>
    public class TransitionCondition : INotifyPropertyChanged;
    {
        private string _conditionId;
        private string _conditionName;
        private ConditionType _conditionType;
        private string _parameterName;
        private object _comparisonValue;
        private ComparisonOperator _comparisonOperator;
        private float _threshold;
        private bool _isEnabled = true;
        private ConditionEvaluationMode _evaluationMode;

        public string ConditionId;
        {
            get => _conditionId;
            set { _conditionId = value; OnPropertyChanged(); }
        }

        public string ConditionName;
        {
            get => _conditionName;
            set { _conditionName = value; OnPropertyChanged(); }
        }

        public ConditionType ConditionType;
        {
            get => _conditionType;
            set { _conditionType = value; OnPropertyChanged(); }
        }

        public string ParameterName;
        {
            get => _parameterName;
            set { _parameterName = value; OnPropertyChanged(); }
        }

        public object ComparisonValue;
        {
            get => _comparisonValue;
            set { _comparisonValue = value; OnPropertyChanged(); }
        }

        public ComparisonOperator ComparisonOperator;
        {
            get => _comparisonOperator;
            set { _comparisonOperator = value; OnPropertyChanged(); }
        }

        public float Threshold;
        {
            get => _threshold;
            set { _threshold = Math.Max(0, value); OnPropertyChanged(); }
        }

        public bool IsEnabled;
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public ConditionEvaluationMode EvaluationMode;
        {
            get => _evaluationMode;
            set { _evaluationMode = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public TransitionCondition()
        {
            _conditionId = Guid.NewGuid().ToString();
            _evaluationMode = ConditionEvaluationMode.Continuous;
        }

        public TransitionCondition(string name, ConditionType type) : this()
        {
            _conditionName = name;
            _conditionType = type;
        }

        public bool Evaluate(StateMachineContext context)
        {
            if (!IsEnabled) return false;

            try
            {
                return ConditionType switch;
                {
                    ConditionType.Parameter => EvaluateParameterCondition(context),
                    ConditionType.AnimationTime => EvaluateAnimationTimeCondition(context),
                    ConditionType.StateTime => EvaluateStateTimeCondition(context),
                    ConditionType.Compound => EvaluateCompoundCondition(context),
                    ConditionType.Custom => EvaluateCustomCondition(context),
                    _ => false;
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Condition evaluation failed: {ex.Message}");
                return false;
            }
        }

        private bool EvaluateParameterCondition(StateMachineContext context)
        {
            if (string.IsNullOrEmpty(ParameterName)) return false;

            var parameterValue = context.GetParameter<object>(ParameterName);
            if (parameterValue == null || ComparisonValue == null) return false;

            return CompareValues(parameterValue, ComparisonValue, ComparisonOperator);
        }

        private bool EvaluateAnimationTimeCondition(StateMachineContext context)
        {
            var currentTime = context.CurrentStateTime;
            return CompareValues(currentTime, Threshold, ComparisonOperator);
        }

        private bool EvaluateStateTimeCondition(StateMachineContext context)
        {
            var stateTime = context.CurrentStateTime;
            return CompareValues(stateTime, Threshold, ComparisonOperator);
        }

        private bool EvaluateCompoundCondition(StateMachineContext context)
        {
            // Compound conditions would evaluate multiple sub-conditions;
            return false;
        }

        private bool EvaluateCustomCondition(StateMachineContext context)
        {
            // Custom condition evaluation logic;
            return false;
        }

        private bool CompareValues(object valueA, object valueB, ComparisonOperator op)
        {
            if (valueA is IComparable comparableA && valueB is IComparable comparableB)
            {
                var comparison = comparableA.CompareTo(comparableB);
                return op switch;
                {
                    ComparisonOperator.Equals => comparison == 0,
                    ComparisonOperator.NotEquals => comparison != 0,
                    ComparisonOperator.GreaterThan => comparison > 0,
                    ComparisonOperator.LessThan => comparison < 0,
                    ComparisonOperator.GreaterThanOrEqual => comparison >= 0,
                    ComparisonOperator.LessThanOrEqual => comparison <= 0,
                    _ => false;
                };
            }
            return false;
        }

        public override string ToString() => $"{ConditionName} ({ConditionType})";
    }

    /// <summary>
    /// Represents a transition between animation states with conditions and settings;
    /// </summary>
    public class AnimationTransition : INotifyPropertyChanged;
    {
        private string _transitionId;
        private string _transitionName;
        private AnimState _sourceState;
        private AnimState _targetState;
        private float _transitionTime = 0.2f;
        private TransitionType _transitionType;
        private TransitionPriority _priority;
        private bool _hasExitTime;
        private float _exitTime;
        private float _transitionOffset;
        private bool _canInterrupt;
        private bool _isEnabled = true;

        public string TransitionId;
        {
            get => _transitionId;
            set { _transitionId = value; OnPropertyChanged(); }
        }

        public string TransitionName;
        {
            get => _transitionName;
            set { _transitionName = value; OnPropertyChanged(); }
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

        public TransitionPriority Priority;
        {
            get => _priority;
            set { _priority = value; OnPropertyChanged(); }
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

        public bool CanInterrupt;
        {
            get => _canInterrupt;
            set { _canInterrupt = value; OnPropertyChanged(); }
        }

        public bool IsEnabled;
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public ObservableCollection<TransitionCondition> Conditions { get; } = new ObservableCollection<TransitionCondition>();
        public ObservableCollection<TransitionAction> Actions { get; } = new ObservableCollection<TransitionAction>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public AnimationTransition()
        {
            _transitionId = Guid.NewGuid().ToString();
            _transitionType = TransitionType.Smooth;
            _priority = TransitionPriority.Normal;
        }

        public AnimationTransition(string name, AnimState source, AnimState target) : this()
        {
            _transitionName = name;
            _sourceState = source;
            _targetState = target;
        }

        public bool CanTransition(StateMachineContext context)
        {
            if (!IsEnabled) return false;

            // Check exit time condition;
            if (HasExitTime && context.CurrentStateTime < ExitTime)
                return false;

            // Check if any conditions are met;
            if (!Conditions.Any()) return true;

            return Conditions.Any(condition => condition.Evaluate(context));
        }

        public bool CanInterruptCurrentTransition(AnimationTransition currentTransition)
        {
            if (!CanInterrupt) return false;
            if (currentTransition == null) return true;

            // Higher priority transitions can interrupt lower ones;
            return Priority > currentTransition.Priority;
        }

        public float GetActualTransitionTime()
        {
            return TransitionTime + TransitionOffset;
        }

        public void AddCondition(TransitionCondition condition)
        {
            Conditions.Add(condition);
        }

        public void AddAction(TransitionAction action)
        {
            Actions.Add(action);
        }

        public void ExecuteActions(StateMachineContext context)
        {
            foreach (var action in Actions.Where(a => a.IsEnabled))
            {
                action.Execute(context);
            }
        }

        public override string ToString() => $"{TransitionName} ({SourceState?.StateName} -> {TargetState?.StateName})";
    }

    /// <summary>
    /// Main transition builder for creating and managing animation state transitions;
    /// </summary>
    public class TransitionBuilder : INotifyPropertyChanged, ITransitionBuilder;
    {
        private readonly ITransitionValidator _validator;
        private readonly ITransitionOptimizer _optimizer;

        private AnimStateMachine _targetStateMachine;
        private TransitionBuildMode _buildMode;
        private bool _isAutoValidateEnabled = true;

        public AnimStateMachine TargetStateMachine;
        {
            get => _targetStateMachine;
            set { _targetStateMachine = value; OnPropertyChanged(); OnStateMachineChanged(); }
        }

        public TransitionBuildMode BuildMode;
        {
            get => _buildMode;
            set { _buildMode = value; OnPropertyChanged(); }
        }

        public bool IsAutoValidateEnabled;
        {
            get => _isAutoValidateEnabled;
            set { _isAutoValidateEnabled = value; OnPropertyChanged(); }
        }

        public ObservableCollection<AnimationTransition> BuiltTransitions { get; } = new ObservableCollection<AnimationTransition>();
        public ObservableCollection<TransitionTemplate> TransitionTemplates { get; } = new ObservableCollection<TransitionTemplate>();
        public ObservableCollection<TransitionValidationResult> ValidationResults { get; } = new ObservableCollection<TransitionValidationResult>();

        public event EventHandler<TransitionBuiltEventArgs> TransitionBuilt;
        public event EventHandler<TransitionValidatedEventArgs> TransitionValidated;
        public event EventHandler<TransitionOptimizedEventArgs> TransitionOptimized;

        public TransitionBuilder(ITransitionValidator validator = null, ITransitionOptimizer optimizer = null)
        {
            _validator = validator ?? new DefaultTransitionValidator();
            _optimizer = optimizer;
            _buildMode = TransitionBuildMode.Standard;

            InitializeTemplates();
        }

        /// <summary>
        /// Creates a basic transition between two states;
        /// </summary>
        public AnimationTransition CreateTransition(string transitionName, string sourceStateName, string targetStateName,
                                                  float transitionTime = 0.2f, TransitionType transitionType = TransitionType.Smooth)
        {
            ValidateStateMachine();

            var sourceState = GetState(sourceStateName);
            var targetState = GetState(targetStateName);

            if (sourceState == null || targetState == null)
                throw new ArgumentException("Source or target state not found");

            var transition = new AnimationTransition(transitionName, sourceState, targetState)
            {
                TransitionTime = transitionTime,
                TransitionType = transitionType;
            };

            BuiltTransitions.Add(transition);
            sourceState.Transitions.Add(transition);

            OnTransitionBuilt(new TransitionBuiltEventArgs;
            {
                Transition = transition,
                Operation = TransitionOperation.Created,
                Timestamp = DateTime.UtcNow;
            });

            if (_isAutoValidateEnabled)
            {
                ValidateTransition(transition);
            }

            return transition;
        }

        /// <summary>
        /// Creates a transition with a single condition;
        /// </summary>
        public AnimationTransition CreateConditionalTransition(string transitionName, string sourceStateName, string targetStateName,
                                                             string parameterName, object comparisonValue, ComparisonOperator comparisonOperator,
                                                             float transitionTime = 0.2f)
        {
            var transition = CreateTransition(transitionName, sourceStateName, targetStateName, transitionTime);

            var condition = new TransitionCondition($"{parameterName}_{comparisonOperator}", ConditionType.Parameter)
            {
                ParameterName = parameterName,
                ComparisonValue = comparisonValue,
                ComparisonOperator = comparisonOperator;
            };

            transition.AddCondition(condition);

            return transition;
        }

        /// <summary>
        /// Creates a transition with exit time;
        /// </summary>
        public AnimationTransition CreateExitTimeTransition(string transitionName, string sourceStateName, string targetStateName,
                                                          float exitTime, float transitionTime = 0.2f)
        {
            var transition = CreateTransition(transitionName, sourceStateName, targetStateName, transitionTime);
            transition.HasExitTime = true;
            transition.ExitTime = exitTime;

            return transition;
        }

        /// <summary>
        /// Creates a bidirectional transition between two states;
        /// </summary>
        public List<AnimationTransition> CreateBidirectionalTransition(string stateAName, string stateBName,
                                                                     string parameterName, object valueA, object valueB,
                                                                     float transitionTime = 0.2f)
        {
            var transitions = new List<AnimationTransition>();

            // A -> B transition;
            var transitionAB = CreateConditionalTransition(
                $"{stateAName}To{stateBName}", stateAName, stateBName,
                parameterName, valueB, ComparisonOperator.Equals, transitionTime);

            // B -> A transition;
            var transitionBA = CreateConditionalTransition(
                $"{stateBName}To{stateAName}", stateBName, stateAName,
                parameterName, valueA, ComparisonOperator.Equals, transitionTime);

            transitions.Add(transitionAB);
            transitions.Add(transitionBA);

            return transitions;
        }

        /// <summary>
        /// Creates a transition from a template;
        /// </summary>
        public AnimationTransition CreateTransitionFromTemplate(string templateName, string sourceStateName, string targetStateName)
        {
            var template = TransitionTemplates.FirstOrDefault(t => t.TemplateName == templateName);
            if (template == null)
                throw new ArgumentException($"Transition template '{templateName}' not found");

            var transition = CreateTransition(templateName, sourceStateName, targetStateName, template.DefaultTransitionTime, template.TransitionType);

            // Apply template settings;
            transition.Priority = template.Priority;
            transition.CanInterrupt = template.CanInterrupt;
            transition.HasExitTime = template.HasExitTime;
            transition.ExitTime = template.ExitTime;

            // Copy conditions;
            foreach (var conditionTemplate in template.ConditionTemplates)
            {
                var condition = new TransitionCondition(conditionTemplate.ConditionName, conditionTemplate.ConditionType)
                {
                    ParameterName = conditionTemplate.ParameterName,
                    ComparisonValue = conditionTemplate.ComparisonValue,
                    ComparisonOperator = conditionTemplate.ComparisonOperator,
                    Threshold = conditionTemplate.Threshold,
                    EvaluationMode = conditionTemplate.EvaluationMode;
                };
                transition.AddCondition(condition);
            }

            // Copy actions;
            foreach (var actionTemplate in template.ActionTemplates)
            {
                var action = new TransitionAction(actionTemplate.ActionName, actionTemplate.ActionType)
                {
                    Parameters = new Dictionary<string, object>(actionTemplate.Parameters)
                };
                transition.AddAction(action);
            }

            return transition;
        }

        /// <summary>
        /// Creates a transition group for complex state relationships;
        /// </summary>
        public TransitionGroup CreateTransitionGroup(string groupName, List<string> stateNames,
                                                   TransitionGroupType groupType = TransitionGroupType.Cyclic)
        {
            ValidateStateMachine();

            var states = stateNames.Select(GetState).Where(s => s != null).ToList();
            if (states.Count < 2)
                throw new ArgumentException("Transition group requires at least 2 states");

            var group = new TransitionGroup(groupName, states, groupType);
            BuildTransitionGroup(group);

            return group;
        }

        /// <summary>
        /// Adds a condition to an existing transition;
        /// </summary>
        public TransitionCondition AddConditionToTransition(string transitionName, ConditionType conditionType,
                                                          string parameterName = null, object comparisonValue = null,
                                                          ComparisonOperator comparisonOperator = ComparisonOperator.Equals)
        {
            var transition = GetTransition(transitionName);
            if (transition == null)
                throw new ArgumentException($"Transition '{transitionName}' not found");

            var condition = new TransitionCondition($"{parameterName}_Condition", conditionType)
            {
                ParameterName = parameterName,
                ComparisonValue = comparisonValue,
                ComparisonOperator = comparisonOperator;
            };

            transition.AddCondition(condition);

            if (_isAutoValidateEnabled)
            {
                ValidateTransition(transition);
            }

            return condition;
        }

        /// <summary>
        /// Adds an action to an existing transition;
        /// </summary>
        public TransitionAction AddActionToTransition(string transitionName, ActionType actionType,
                                                    Dictionary<string, object> parameters = null)
        {
            var transition = GetTransition(transitionName);
            if (transition == null)
                throw new ArgumentException($"Transition '{transitionName}' not found");

            var action = new TransitionAction($"{actionType}_Action", actionType);
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    action.Parameters[param.Key] = param.Value;
                }
            }

            transition.AddAction(action);
            return action;
        }

        /// <summary>
        /// Removes a transition from the state machine;
        /// </summary>
        public bool RemoveTransition(string transitionName)
        {
            var transition = GetTransition(transitionName);
            if (transition == null) return false;

            // Remove from source state;
            transition.SourceState?.Transitions.Remove(transition);

            // Remove from built transitions;
            BuiltTransitions.Remove(transition);

            OnTransitionBuilt(new TransitionBuiltEventArgs;
            {
                Transition = transition,
                Operation = TransitionOperation.Removed,
                Timestamp = DateTime.UtcNow;
            });

            return true;
        }

        /// <summary>
        /// Validates a specific transition;
        /// </summary>
        public TransitionValidationResult ValidateTransition(AnimationTransition transition)
        {
            if (_validator == null)
                return new TransitionValidationResult { IsValid = true };

            var result = _validator.Validate(transition);
            UpdateValidationResults(result);

            OnTransitionValidated(new TransitionValidatedEventArgs;
            {
                Transition = transition,
                ValidationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Validates all built transitions;
        /// </summary>
        public TransitionValidationResult ValidateAllTransitions()
        {
            if (_validator == null)
                return new TransitionValidationResult { IsValid = true };

            var result = _validator.ValidateAll(BuiltTransitions.ToList());
            UpdateValidationResults(result);

            OnTransitionValidated(new TransitionValidatedEventArgs;
            {
                ValidationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Optimizes transitions for performance;
        /// </summary>
        public TransitionOptimizationResult OptimizeTransitions()
        {
            if (_optimizer == null)
                return new TransitionOptimizationResult { Success = true };

            var result = _optimizer.Optimize(BuiltTransitions.ToList());

            OnTransitionOptimized(new TransitionOptimizedEventArgs;
            {
                OptimizationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Creates a transition template from an existing transition;
        /// </summary>
        public TransitionTemplate CreateTemplateFromTransition(string templateName, AnimationTransition sourceTransition)
        {
            var template = new TransitionTemplate(templateName)
            {
                DefaultTransitionTime = sourceTransition.TransitionTime,
                TransitionType = sourceTransition.TransitionType,
                Priority = sourceTransition.Priority,
                CanInterrupt = sourceTransition.CanInterrupt,
                HasExitTime = sourceTransition.HasExitTime,
                ExitTime = sourceTransition.ExitTime;
            };

            // Copy conditions;
            foreach (var condition in sourceTransition.Conditions)
            {
                template.ConditionTemplates.Add(new ConditionTemplate;
                {
                    ConditionName = condition.ConditionName,
                    ConditionType = condition.ConditionType,
                    ParameterName = condition.ParameterName,
                    ComparisonValue = condition.ComparisonValue,
                    ComparisonOperator = condition.ComparisonOperator,
                    Threshold = condition.Threshold,
                    EvaluationMode = condition.EvaluationMode;
                });
            }

            // Copy actions;
            foreach (var action in sourceTransition.Actions)
            {
                template.ActionTemplates.Add(new ActionTemplate;
                {
                    ActionName = action.ActionName,
                    ActionType = action.ActionType,
                    Parameters = new Dictionary<string, object>(action.Parameters)
                });
            }

            TransitionTemplates.Add(template);
            return template;
        }

        /// <summary>
        /// Gets transition statistics;
        /// </summary>
        public TransitionStatistics GetStatistics()
        {
            return new TransitionStatistics;
            {
                TotalTransitions = BuiltTransitions.Count,
                AverageConditionsPerTransition = BuiltTransitions.Any() ? BuiltTransitions.Average(t => t.Conditions.Count) : 0,
                AverageActionsPerTransition = BuiltTransitions.Any() ? BuiltTransitions.Average(t => t.Actions.Count) : 0,
                EnabledTransitions = BuiltTransitions.Count(t => t.IsEnabled),
                ConditionalTransitions = BuiltTransitions.Count(t => t.Conditions.Any()),
                ExitTimeTransitions = BuiltTransitions.Count(t => t.HasExitTime),
                HighPriorityTransitions = BuiltTransitions.Count(t => t.Priority == TransitionPriority.High)
            };
        }

        private void BuildTransitionGroup(TransitionGroup group)
        {
            switch (group.GroupType)
            {
                case TransitionGroupType.Cyclic:
                    BuildCyclicTransitions(group);
                    break;
                case TransitionGroupType.Bidirectional:
                    BuildBidirectionalTransitions(group);
                    break;
                case TransitionGroupType.Star:
                    BuildStarTransitions(group);
                    break;
                case TransitionGroupType.Custom:
                    BuildCustomTransitions(group);
                    break;
            }
        }

        private void BuildCyclicTransitions(TransitionGroup group)
        {
            for (int i = 0; i < group.States.Count; i++)
            {
                var sourceState = group.States[i];
                var targetState = group.States[(i + 1) % group.States.Count];

                CreateTransition($"{sourceState.StateName}To{targetState.StateName}",
                               sourceState.StateName, targetState.StateName, group.DefaultTransitionTime);
            }
        }

        private void BuildBidirectionalTransitions(TransitionGroup group)
        {
            for (int i = 0; i < group.States.Count; i++)
            {
                for (int j = i + 1; j < group.States.Count; j++)
                {
                    var stateA = group.States[i];
                    var stateB = group.States[j];

                    CreateTransition($"{stateA.StateName}To{stateB.StateName}", stateA.StateName, stateB.StateName, group.DefaultTransitionTime);
                    CreateTransition($"{stateB.StateName}To{stateA.StateName}", stateB.StateName, stateA.StateName, group.DefaultTransitionTime);
                }
            }
        }

        private void BuildStarTransitions(TransitionGroup group)
        {
            if (group.States.Count < 2) return;

            var centerState = group.States[0];
            for (int i = 1; i < group.States.Count; i++)
            {
                var peripheralState = group.States[i];

                CreateTransition($"{centerState.StateName}To{peripheralState.StateName}",
                               centerState.StateName, peripheralState.StateName, group.DefaultTransitionTime);
                CreateTransition($"{peripheralState.StateName}To{centerState.StateName}",
                               peripheralState.StateName, centerState.StateName, group.DefaultTransitionTime);
            }
        }

        private void BuildCustomTransitions(TransitionGroup group)
        {
            // Custom transition building logic based on group configuration;
        }

        private void ValidateStateMachine()
        {
            if (_targetStateMachine == null)
                throw new InvalidOperationException("Target state machine is not set");
        }

        private AnimState GetState(string stateName)
        {
            return _targetStateMachine?.States.FirstOrDefault(s => s.StateName == stateName);
        }

        private AnimationTransition GetTransition(string transitionName)
        {
            return BuiltTransitions.FirstOrDefault(t => t.TransitionName == transitionName);
        }

        private void InitializeTemplates()
        {
            // Initialize with common transition templates;
            var quickTransition = new TransitionTemplate("QuickTransition")
            {
                DefaultTransitionTime = 0.1f,
                TransitionType = TransitionType.Immediate,
                Priority = TransitionPriority.High,
                CanInterrupt = true;
            };

            var smoothTransition = new TransitionTemplate("SmoothTransition")
            {
                DefaultTransitionTime = 0.3f,
                TransitionType = TransitionType.Smooth,
                Priority = TransitionPriority.Normal,
                CanInterrupt = false;
            };

            TransitionTemplates.Add(quickTransition);
            TransitionTemplates.Add(smoothTransition);
        }

        private void UpdateValidationResults(TransitionValidationResult result)
        {
            ValidationResults.Clear();
            ValidationResults.Add(result);
        }

        private void OnStateMachineChanged()
        {
            // Clear transitions when state machine changes;
            BuiltTransitions.Clear();
            ValidationResults.Clear();
        }

        private void OnTransitionBuilt(TransitionBuiltEventArgs e)
        {
            TransitionBuilt?.Invoke(this, e);
        }

        private void OnTransitionValidated(TransitionValidatedEventArgs e)
        {
            TransitionValidated?.Invoke(this, e);
        }

        private void OnTransitionOptimized(TransitionOptimizedEventArgs e)
        {
            TransitionOptimized?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum ConditionType;
    {
        Parameter,
        AnimationTime,
        StateTime,
        Compound,
        Custom;
    }

    public enum ComparisonOperator;
    {
        Equals,
        NotEquals,
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual;
    }

    public enum ConditionEvaluationMode;
    {
        Continuous,
        OnChange,
        Once;
    }

    public enum TransitionType;
    {
        Immediate,
        Smooth,
        Crossfade,
        Frozen;
    }

    public enum TransitionPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    public enum TransitionBuildMode;
    {
        Standard,
        Advanced,
        Expert;
    }

    public enum TransitionOperation;
    {
        Created,
        Modified,
        Removed;
    }

    public enum TransitionGroupType;
    {
        Cyclic,
        Bidirectional,
        Star,
        Custom;
    }

    public interface ITransitionBuilder;
    {
        AnimationTransition CreateTransition(string transitionName, string sourceStateName, string targetStateName,
                                           float transitionTime = 0.2f, TransitionType transitionType = TransitionType.Smooth);
        AnimationTransition CreateConditionalTransition(string transitionName, string sourceStateName, string targetStateName,
                                                      string parameterName, object comparisonValue, ComparisonOperator comparisonOperator,
                                                      float transitionTime = 0.2f);
        bool RemoveTransition(string transitionName);
        TransitionValidationResult ValidateAllTransitions();
    }

    public interface ITransitionValidator;
    {
        TransitionValidationResult Validate(AnimationTransition transition);
        TransitionValidationResult ValidateAll(List<AnimationTransition> transitions);
    }

    public interface ITransitionOptimizer;
    {
        TransitionOptimizationResult Optimize(List<AnimationTransition> transitions);
    }

    public class TransitionAction;
    {
        public string ActionId { get; set; }
        public string ActionName { get; set; }
        public ActionType ActionType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public bool IsEnabled { get; set; } = true;

        public TransitionAction()
        {
            ActionId = Guid.NewGuid().ToString();
        }

        public TransitionAction(string name, ActionType type) : this()
        {
            ActionName = name;
            ActionType = type;
        }

        public virtual void Execute(StateMachineContext context)
        {
            // Base implementation - override in derived classes;
        }
    }

    public class TransitionGroup;
    {
        public string GroupName { get; set; }
        public List<AnimState> States { get; }
        public TransitionGroupType GroupType { get; set; }
        public float DefaultTransitionTime { get; set; } = 0.2f;
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        public TransitionGroup(string name, List<AnimState> states, TransitionGroupType groupType)
        {
            GroupName = name;
            States = states;
            GroupType = groupType;
        }
    }

    public class TransitionTemplate;
    {
        public string TemplateName { get; set; }
        public float DefaultTransitionTime { get; set; } = 0.2f;
        public TransitionType TransitionType { get; set; } = TransitionType.Smooth;
        public TransitionPriority Priority { get; set; } = TransitionPriority.Normal;
        public bool CanInterrupt { get; set; }
        public bool HasExitTime { get; set; }
        public float ExitTime { get; set; }
        public List<ConditionTemplate> ConditionTemplates { get; } = new List<ConditionTemplate>();
        public List<ActionTemplate> ActionTemplates { get; } = new List<ActionTemplate>();

        public TransitionTemplate(string name)
        {
            TemplateName = name;
        }
    }

    public class ConditionTemplate;
    {
        public string ConditionName { get; set; }
        public ConditionType ConditionType { get; set; }
        public string ParameterName { get; set; }
        public object ComparisonValue { get; set; }
        public ComparisonOperator ComparisonOperator { get; set; }
        public float Threshold { get; set; }
        public ConditionEvaluationMode EvaluationMode { get; set; }
    }

    public class ActionTemplate;
    {
        public string ActionName { get; set; }
        public ActionType ActionType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class TransitionValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<AnimationTransition> ProblematicTransitions { get; set; } = new List<AnimationTransition>();
    }

    public class TransitionOptimizationResult;
    {
        public bool Success { get; set; }
        public List<string> OptimizationsApplied { get; set; } = new List<string>();
        public int OriginalTransitionCount { get; set; }
        public int OptimizedTransitionCount { get; set; }
        public float OptimizationRatio { get; set; }
    }

    public class TransitionStatistics;
    {
        public int TotalTransitions { get; set; }
        public double AverageConditionsPerTransition { get; set; }
        public double AverageActionsPerTransition { get; set; }
        public int EnabledTransitions { get; set; }
        public int ConditionalTransitions { get; set; }
        public int ExitTimeTransitions { get; set; }
        public int HighPriorityTransitions { get; set; }
    }

    public class TransitionBuiltEventArgs : EventArgs;
    {
        public AnimationTransition Transition { get; set; }
        public TransitionOperation Operation { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TransitionValidatedEventArgs : EventArgs;
    {
        public AnimationTransition Transition { get; set; }
        public TransitionValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TransitionOptimizedEventArgs : EventArgs;
    {
        public TransitionOptimizationResult OptimizationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Default Implementations;

    public class DefaultTransitionValidator : ITransitionValidator;
    {
        public TransitionValidationResult Validate(AnimationTransition transition)
        {
            var result = new TransitionValidationResult();

            if (transition == null)
            {
                result.Errors.Add("Transition is null");
                return result;
            }

            // Check for valid states;
            if (transition.SourceState == null)
            {
                result.Errors.Add("Source state is null");
            }

            if (transition.TargetState == null)
            {
                result.Errors.Add("Target state is null");
            }

            // Check for self-transition conditions;
            if (transition.SourceState == transition.TargetState && !transition.CanInterrupt)
            {
                result.Warnings.Add("Self-transition without interrupt capability may cause issues");
            }

            // Check transition time;
            if (transition.TransitionTime < 0)
            {
                result.Errors.Add("Transition time cannot be negative");
            }

            // Validate conditions;
            foreach (var condition in transition.Conditions)
            {
                if (string.IsNullOrEmpty(condition.ParameterName) && condition.ConditionType == ConditionType.Parameter)
                {
                    result.Errors.Add($"Condition '{condition.ConditionName}' has no parameter name");
                }
            }

            result.IsValid = !result.Errors.Any();
            if (!result.IsValid)
            {
                result.ProblematicTransitions.Add(transition);
            }

            return result;
        }

        public TransitionValidationResult ValidateAll(List<AnimationTransition> transitions)
        {
            var result = new TransitionValidationResult();

            foreach (var transition in transitions)
            {
                var transitionResult = Validate(transition);
                result.Errors.AddRange(transitionResult.Errors);
                result.Warnings.AddRange(transitionResult.Warnings);
                result.ProblematicTransitions.AddRange(transitionResult.ProblematicTransitions);
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }
    }

    #endregion;
}
