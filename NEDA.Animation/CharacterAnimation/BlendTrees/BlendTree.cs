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
    /// Represents a single blend tree node in the animation system;
    /// </summary>
    public class BlendTreeNode : INotifyPropertyChanged;
    {
        private string _nodeId;
        private string _animationAsset;
        private float _weight;
        private float _speed = 1.0f;
        private bool _isLooping = true;
        private BlendType _blendType;
        private Dictionary<string, float> _blendParameters;
        private NodePosition _position;
        private float _threshold;
        private float _normalizedTime;

        public string NodeId;
        {
            get => _nodeId;
            set { _nodeId = value; OnPropertyChanged(); }
        }

        public string AnimationAsset;
        {
            get => _animationAsset;
            set { _animationAsset = value; OnPropertyChanged(); }
        }

        public float Weight;
        {
            get => _weight;
            set { _weight = Math.Max(0, value); OnPropertyChanged(); }
        }

        public float Speed;
        {
            get => _speed;
            set { _speed = Math.Max(0, value); OnPropertyChanged(); }
        }

        public bool IsLooping;
        {
            get => _isLooping;
            set { _isLooping = value; OnPropertyChanged(); }
        }

        public BlendType BlendType;
        {
            get => _blendType;
            set { _blendType = value; OnPropertyChanged(); }
        }

        public Dictionary<string, float> BlendParameters;
        {
            get => _blendParameters;
            set { _blendParameters = value; OnPropertyChanged(); }
        }

        public NodePosition Position;
        {
            get => _position;
            set { _position = value; OnPropertyChanged(); }
        }

        public float Threshold;
        {
            get => _threshold;
            set { _threshold = value; OnPropertyChanged(); }
        }

        public float NormalizedTime;
        {
            get => _normalizedTime;
            set { _normalizedTime = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public ObservableCollection<BlendTreeNode> Children { get; } = new ObservableCollection<BlendTreeNode>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public BlendTreeNode()
        {
            _nodeId = Guid.NewGuid().ToString();
            _blendParameters = new Dictionary<string, float>();
            _position = new NodePosition(0, 0);
        }

        public float CalculateWeight(Dictionary<string, float> inputParameters)
        {
            return BlendCalculator.CalculateWeight(this, inputParameters);
        }
    }

    /// <summary>
    /// Main blend tree controller for complex animation blending;
    /// </summary>
    public class BlendTree : INotifyPropertyChanged, IBlendTreeController;
    {
        private readonly IAnimationController _animationController;
        private readonly IBlendTreeOptimizer _optimizer;

        private BlendTreeNode _rootNode;
        private BlendAlgorithm _blendAlgorithm;
        private float _totalWeight;
        private bool _isAutoUpdateEnabled = true;
        private Dictionary<string, float> _currentParameters;
        private BlendTreeState _currentState;

        public BlendTreeNode RootNode;
        {
            get => _rootNode;
            set { _rootNode = value; OnPropertyChanged(); RecalculateWeights(); }
        }

        public BlendAlgorithm BlendAlgorithm;
        {
            get => _blendAlgorithm;
            set { _blendAlgorithm = value; OnPropertyChanged(); RecalculateWeights(); }
        }

        public float TotalWeight;
        {
            get => _totalWeight;
            private set { _totalWeight = value; OnPropertyChanged(); }
        }

        public bool IsAutoUpdateEnabled;
        {
            get => _isAutoUpdateEnabled;
            set { _isAutoUpdateEnabled = value; OnPropertyChanged(); }
        }

        public Dictionary<string, float> CurrentParameters;
        {
            get => _currentParameters;
            private set { _currentParameters = value; OnPropertyChanged(); }
        }

        public BlendTreeState CurrentState;
        {
            get => _currentState;
            private set { _currentState = value; OnPropertyChanged(); }
        }

        public ObservableCollection<BlendTreeNode> ActiveNodes { get; } = new ObservableCollection<BlendTreeNode>();
        public ObservableCollection<BlendParameter> Parameters { get; } = new ObservableCollection<BlendParameter>();

        public string TreeName { get; set; }
        public BlendTreeType TreeType { get; set; }
        public float GlobalSpeed { get; set; } = 1.0f;

        public event EventHandler<BlendTreeUpdatedEventArgs> BlendTreeUpdated;
        public event EventHandler<BlendWeightsChangedEventArgs> BlendWeightsChanged;
        public event EventHandler<BlendParameterChangedEventArgs> BlendParameterChanged;

        public BlendTree(IAnimationController animationController, IBlendTreeOptimizer optimizer = null)
        {
            _animationController = animationController ?? throw new ArgumentNullException(nameof(animationController));
            _optimizer = optimizer;
            _currentParameters = new Dictionary<string, float>();
            _currentState = BlendTreeState.Stopped;

            InitializeDefaultParameters();
            SetupRootNode();

            ActiveNodes.CollectionChanged += OnActiveNodesChanged;
        }

        /// <summary>
        /// Updates the blend tree with new parameter values;
        /// </summary>
        public void Update(Dictionary<string, float> parameters)
        {
            if (_currentState != BlendTreeState.Running) return;

            try
            {
                UpdateParameters(parameters);
                RecalculateWeights();
                UpdateActiveAnimations();
                OptimizeIfNeeded();

                OnBlendTreeUpdated(new BlendTreeUpdatedEventArgs;
                {
                    Parameters = new Dictionary<string, float>(_currentParameters),
                    ActiveNodeCount = ActiveNodes.Count,
                    TotalWeight = TotalWeight,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                throw new BlendTreeException("Blend tree update failed", ex);
            }
        }

        /// <summary>
        /// Updates a single parameter value;
        /// </summary>
        public void SetParameter(string parameterName, float value)
        {
            var parameter = Parameters.FirstOrDefault(p => p.Name == parameterName);
            if (parameter == null)
            {
                throw new ArgumentException($"Parameter {parameterName} not found in blend tree");
            }

            // Apply parameter constraints;
            var constrainedValue = Math.Max(parameter.MinValue, Math.Min(parameter.MaxValue, value));

            if (_currentParameters.ContainsKey(parameterName))
            {
                _currentParameters[parameterName] = constrainedValue;
            }
            else;
            {
                _currentParameters.Add(parameterName, constrainedValue);
            }

            OnBlendParameterChanged(new BlendParameterChangedEventArgs;
            {
                ParameterName = parameterName,
                OldValue = parameter.Value,
                NewValue = constrainedValue,
                Timestamp = DateTime.UtcNow;
            });

            parameter.Value = constrainedValue;

            if (_isAutoUpdateEnabled)
            {
                RecalculateWeights();
            }
        }

        /// <summary>
        /// Gets the current value of a parameter;
        /// </summary>
        public float GetParameter(string parameterName)
        {
            return _currentParameters.TryGetValue(parameterName, out float value) ? value : 0f;
        }

        /// <summary>
        /// Adds a new node to the blend tree;
        /// </summary>
        public BlendTreeNode AddNode(string animationAsset, NodePosition position, BlendType blendType = BlendType.Simple1D, float threshold = 0.5f)
        {
            var node = new BlendTreeNode;
            {
                AnimationAsset = animationAsset,
                Position = position,
                BlendType = blendType,
                Threshold = threshold;
            };

            if (RootNode == null)
            {
                RootNode = node;
            }
            else;
            {
                RootNode.Children.Add(node);
            }

            RecalculateWeights();
            return node;
        }

        /// <summary>
        /// Adds a node as a child of another node;
        /// </summary>
        public BlendTreeNode AddChildNode(string parentNodeId, string animationAsset, NodePosition position, BlendType blendType = BlendType.Simple1D)
        {
            var parentNode = FindNodeById(parentNodeId);
            if (parentNode == null)
                throw new ArgumentException($"Parent node with ID {parentNodeId} not found");

            var node = new BlendTreeNode;
            {
                AnimationAsset = animationAsset,
                Position = position,
                BlendType = blendType;
            };

            parentNode.Children.Add(node);
            RecalculateWeights();
            return node;
        }

        /// <summary>
        /// Creates a 1D blend space with multiple nodes;
        /// </summary>
        public void Create1DBlendSpace(string parameterName, params (float position, string animationAsset)[] nodes)
        {
            RootNode = new BlendTreeNode;
            {
                BlendType = BlendType.Simple1D,
                Position = new NodePosition(0, 0)
            };

            foreach (var (position, animationAsset) in nodes)
            {
                var node = new BlendTreeNode;
                {
                    AnimationAsset = animationAsset,
                    Position = new NodePosition(position, 0),
                    BlendType = BlendType.Simple1D;
                };

                RootNode.Children.Add(node);
            }

            // Ensure parameter exists;
            if (!Parameters.Any(p => p.Name == parameterName))
            {
                AddParameter(parameterName, 0f, -1f, 1f);
            }

            RecalculateWeights();
        }

        /// <summary>
        /// Creates a 2D blend space with multiple nodes;
        /// </summary>
        public void Create2DBlendSpace(string xParameter, string yParameter, params (float x, float y, string animationAsset)[] nodes)
        {
            RootNode = new BlendTreeNode;
            {
                BlendType = BlendType.Simple2D,
                Position = new NodePosition(0, 0)
            };

            foreach (var (x, y, animationAsset) in nodes)
            {
                var node = new BlendTreeNode;
                {
                    AnimationAsset = animationAsset,
                    Position = new NodePosition(x, y),
                    BlendType = BlendType.Simple2D;
                };

                RootNode.Children.Add(node);
            }

            // Ensure parameters exist;
            if (!Parameters.Any(p => p.Name == xParameter))
            {
                AddParameter(xParameter, 0f, -1f, 1f);
            }

            if (!Parameters.Any(p => p.Name == yParameter))
            {
                AddParameter(yParameter, 0f, -1f, 1f);
            }

            RecalculateWeights();
        }

        /// <summary>
        /// Creates a directional blend space for movement animations;
        /// </summary>
        public void CreateDirectionalBlendSpace(params (float angle, float speed, string animationAsset)[] nodes)
        {
            RootNode = new BlendTreeNode;
            {
                BlendType = BlendType.Directional,
                Position = new NodePosition(0, 0)
            };

            foreach (var (angle, speed, animationAsset) in nodes)
            {
                var node = new BlendTreeNode;
                {
                    AnimationAsset = animationAsset,
                    Position = new NodePosition(angle, speed),
                    BlendType = BlendType.Directional;
                };

                RootNode.Children.Add(node);
            }

            AddParameter("Direction", 0f, 0f, 360f);
            AddParameter("Speed", 0f, 0f, 1f);

            RecalculateWeights();
        }

        /// <summary>
        /// Adds a new parameter to the blend tree;
        /// </summary>
        public BlendParameter AddParameter(string name, float defaultValue, float minValue, float maxValue)
        {
            if (Parameters.Any(p => p.Name == name))
                throw new ArgumentException($"Parameter {name} already exists");

            var parameter = new BlendParameter;
            {
                Name = name,
                Value = defaultValue,
                DefaultValue = defaultValue,
                MinValue = minValue,
                MaxValue = maxValue;
            };

            Parameters.Add(parameter);
            _currentParameters[name] = defaultValue;

            return parameter;
        }

        /// <summary>
        /// Starts the blend tree execution;
        /// </summary>
        public void Start()
        {
            if (_currentState == BlendTreeState.Running) return;

            _currentState = BlendTreeState.Running;
            RecalculateWeights();
            UpdateActiveAnimations();

            OnBlendTreeUpdated(new BlendTreeUpdatedEventArgs;
            {
                Parameters = new Dictionary<string, float>(_currentParameters),
                ActiveNodeCount = ActiveNodes.Count,
                TotalWeight = TotalWeight,
                Timestamp = DateTime.UtcNow,
                EventType = BlendTreeEventType.Started;
            });
        }

        /// <summary>
        /// Stops the blend tree execution;
        /// </summary>
        public void Stop()
        {
            if (_currentState == BlendTreeState.Stopped) return;

            _currentState = BlendTreeState.Stopped;
            ActiveNodes.Clear();
            TotalWeight = 0;

            OnBlendTreeUpdated(new BlendTreeUpdatedEventArgs;
            {
                EventType = BlendTreeEventType.Stopped,
                Timestamp = DateTime.UtcNow;
            });
        }

        /// <summary>
        /// Resets the blend tree to initial state;
        /// </summary>
        public void Reset()
        {
            foreach (var parameter in Parameters)
            {
                parameter.Value = parameter.DefaultValue;
                _currentParameters[parameter.Name] = parameter.DefaultValue;
            }

            RecalculateWeights();
        }

        /// <summary>
        /// Gets the current blend weight for a specific node;
        /// </summary>
        public float GetNodeWeight(string nodeId)
        {
            var node = FindNodeById(nodeId);
            return node?.Weight ?? 0f;
        }

        /// <summary>
        /// Gets all nodes with non-zero weights;
        /// </summary>
        public IEnumerable<BlendTreeNode> GetActiveNodes()
        {
            return ActiveNodes.Where(n => n.Weight > 0.001f);
        }

        /// <summary>
        /// Optimizes the blend tree for performance;
        /// </summary>
        public void Optimize()
        {
            _optimizer?.Optimize(this);
        }

        /// <summary>
        /// Validates the blend tree structure;
        /// </summary>
        public BlendTreeValidationResult Validate()
        {
            var result = new BlendTreeValidationResult();

            if (RootNode == null)
            {
                result.Errors.Add("Blend tree has no root node");
            }

            ValidateNode(RootNode, result, new HashSet<string>());

            // Check parameter usage;
            foreach (var parameter in Parameters)
            {
                if (parameter.MinValue >= parameter.MaxValue)
                {
                    result.Errors.Add($"Parameter {parameter.Name} has invalid min/max range");
                }
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }

        /// <summary>
        /// Calculates the final blended animation pose;
        /// </summary>
        public AnimationPose CalculateBlendedPose()
        {
            var activeNodes = GetActiveNodes().ToList();
            if (!activeNodes.Any())
                return new AnimationPose();

            if (activeNodes.Count == 1)
            {
                return _animationController.GetPose(activeNodes[0].AnimationAsset);
            }

            // Perform multi-node blending;
            var blendedPose = new AnimationPose();
            float totalWeight = activeNodes.Sum(n => n.Weight);

            foreach (var node in activeNodes)
            {
                var normalizedWeight = node.Weight / totalWeight;
                var pose = _animationController.GetPose(node.AnimationAsset);
                blendedPose.BlendWith(pose, normalizedWeight);
            }

            return blendedPose;
        }

        private void SetupRootNode()
        {
            if (RootNode == null)
            {
                RootNode = new BlendTreeNode;
                {
                    BlendType = BlendType.Direct,
                    Position = new NodePosition(0, 0)
                };
            }
        }

        private void InitializeDefaultParameters()
        {
            AddParameter("Blend", 0f, 0f, 1f);
            AddParameter("Speed", 0f, 0f, 1f);
            AddParameter("Direction", 0f, -180f, 180f);
        }

        private void UpdateParameters(Dictionary<string, float> parameters)
        {
            foreach (var param in parameters)
            {
                if (_currentParameters.ContainsKey(param.Key))
                {
                    _currentParameters[param.Key] = param.Value;
                }
                else;
                {
                    _currentParameters.Add(param.Key, param.Value);
                }

                // Update parameter object if it exists;
                var parameterObj = Parameters.FirstOrDefault(p => p.Name == param.Key);
                if (parameterObj != null)
                {
                    parameterObj.Value = param.Value;
                }
            }
        }

        private void RecalculateWeights()
        {
            if (RootNode == null || _currentState != BlendTreeState.Running) return;

            TotalWeight = 0f;
            var oldActiveNodes = new List<BlendTreeNode>(ActiveNodes);

            ClearWeights(RootNode);
            CalculateNodeWeights(RootNode, _currentParameters);

            UpdateActiveNodesList();
            NotifyWeightsChanged(oldActiveNodes);
        }

        private void CalculateNodeWeights(BlendTreeNode node, Dictionary<string, float> parameters)
        {
            if (node == null) return;

            node.Weight = node.CalculateWeight(parameters);
            TotalWeight += node.Weight;

            foreach (var child in node.Children)
            {
                CalculateNodeWeights(child, parameters);
            }
        }

        private void ClearWeights(BlendTreeNode node)
        {
            if (node == null) return;

            node.Weight = 0f;
            foreach (var child in node.Children)
            {
                ClearWeights(child);
            }
        }

        private void UpdateActiveNodesList()
        {
            var nodesToRemove = ActiveNodes.Where(n => n.Weight <= 0.001f).ToList();
            var nodesToAdd = GetAllNodes().Where(n => n.Weight > 0.001f && !ActiveNodes.Contains(n)).ToList();

            foreach (var node in nodesToRemove)
            {
                ActiveNodes.Remove(node);
            }

            foreach (var node in nodesToAdd)
            {
                ActiveNodes.Add(node);
            }
        }

        private void UpdateActiveAnimations()
        {
            var activeNodes = GetActiveNodes().ToList();
            foreach (var node in activeNodes)
            {
                _animationController.SetAnimationWeight(node.AnimationAsset, node.Weight);
                _animationController.SetAnimationSpeed(node.AnimationAsset, node.Speed * GlobalSpeed);
            }
        }

        private void OptimizeIfNeeded()
        {
            if (_optimizer != null && _optimizer.ShouldOptimize(this))
            {
                _optimizer.Optimize(this);
            }
        }

        private BlendTreeNode FindNodeById(string nodeId)
        {
            return FindNodeByIdRecursive(RootNode, nodeId);
        }

        private BlendTreeNode FindNodeByIdRecursive(BlendTreeNode node, string nodeId)
        {
            if (node == null) return null;
            if (node.NodeId == nodeId) return node;

            foreach (var child in node.Children)
            {
                var found = FindNodeByIdRecursive(child, nodeId);
                if (found != null) return found;
            }

            return null;
        }

        private IEnumerable<BlendTreeNode> GetAllNodes()
        {
            return GetAllNodesRecursive(RootNode);
        }

        private IEnumerable<BlendTreeNode> GetAllNodesRecursive(BlendTreeNode node)
        {
            if (node == null) yield break;

            yield return node;

            foreach (var child in node.Children)
            {
                foreach (var childNode in GetAllNodesRecursive(child))
                {
                    yield return childNode;
                }
            }
        }

        private void ValidateNode(BlendTreeNode node, BlendTreeValidationResult result, HashSet<string> visitedNodes)
        {
            if (node == null) return;

            if (visitedNodes.Contains(node.NodeId))
            {
                result.Errors.Add($"Circular reference detected at node {node.NodeId}");
                return;
            }

            visitedNodes.Add(node.NodeId);

            // Validate node properties;
            if (string.IsNullOrEmpty(node.AnimationAsset) && node.Children.Count == 0)
            {
                result.Warnings.Add($"Node {node.NodeId} has no animation asset and no children");
            }

            foreach (var child in node.Children)
            {
                ValidateNode(child, result, visitedNodes);
            }

            visitedNodes.Remove(node.NodeId);
        }

        private void OnActiveNodesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (BlendTreeNode node in e.NewItems)
                {
                    _animationController.PlayAnimation(node.AnimationAsset);
                }
            }

            if (e.OldItems != null)
            {
                foreach (BlendTreeNode node in e.OldItems)
                {
                    _animationController.StopAnimation(node.AnimationAsset);
                }
            }
        }

        private void NotifyWeightsChanged(List<BlendTreeNode> oldActiveNodes)
        {
            var changedNodes = ActiveNodes;
                .Where(n => oldActiveNodes.Contains(n) && Math.Abs(n.Weight - oldActiveNodes.First(o => o.NodeId == n.NodeId).Weight) > 0.001f)
                .Union(ActiveNodes.Except(oldActiveNodes))
                .Union(oldActiveNodes.Except(ActiveNodes))
                .ToList();

            if (changedNodes.Any())
            {
                OnBlendWeightsChanged(new BlendWeightsChangedEventArgs;
                {
                    ChangedNodes = changedNodes,
                    TotalWeight = TotalWeight,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private void OnBlendTreeUpdated(BlendTreeUpdatedEventArgs e)
        {
            BlendTreeUpdated?.Invoke(this, e);
        }

        private void OnBlendWeightsChanged(BlendWeightsChangedEventArgs e)
        {
            BlendWeightsChanged?.Invoke(this, e);
        }

        private void OnBlendParameterChanged(BlendParameterChangedEventArgs e)
        {
            BlendParameterChanged?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum BlendType;
    {
        Direct,
        Simple1D,
        Simple2D,
        Directional,
        FreeformDirectional,
        FreeformCartesian;
    }

    public enum BlendAlgorithm;
    {
        Linear,
        Quadratic,
        Cubic,
        InverseDistance,
        Barycentric;
    }

    public enum BlendTreeState;
    {
        Stopped,
        Running,
        Paused;
    }

    public enum BlendTreeType;
    {
        Simple,
        Complex,
        Directional,
        Freeform;
    }

    public enum BlendTreeEventType;
    {
        Started,
        Stopped,
        Updated,
        WeightsChanged,
        ParameterChanged;
    }

    public struct NodePosition;
    {
        public float X { get; }
        public float Y { get; }

        public NodePosition(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float DistanceTo(NodePosition other)
        {
            float dx = X - other.X;
            float dy = Y - other.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public override string ToString() => $"({X}, {Y})";
    }

    public interface IBlendTreeController;
    {
        void Update(Dictionary<string, float> parameters);
        void SetParameter(string parameterName, float value);
        float GetParameter(string parameterName);
        void Start();
        void Stop();
    }

    public interface IAnimationController;
    {
        void PlayAnimation(string animationAsset);
        void StopAnimation(string animationAsset);
        void SetAnimationWeight(string animationAsset, float weight);
        void SetAnimationSpeed(string animationAsset, float speed);
        AnimationPose GetPose(string animationAsset);
    }

    public interface IBlendTreeOptimizer;
    {
        void Optimize(BlendTree blendTree);
        bool ShouldOptimize(BlendTree blendTree);
    }

    public class BlendParameter : INotifyPropertyChanged;
    {
        private string _name;
        private float _value;
        private float _defaultValue;
        private float _minValue;
        private float _maxValue;

        public string Name;
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public float Value;
        {
            get => _value;
            set { _value = Math.Max(_minValue, Math.Min(_maxValue, value)); OnPropertyChanged(); }
        }

        public float DefaultValue;
        {
            get => _defaultValue;
            set { _defaultValue = value; OnPropertyChanged(); }
        }

        public float MinValue;
        {
            get => _minValue;
            set { _minValue = value; OnPropertyChanged(); }
        }

        public float MaxValue;
        {
            get => _maxValue;
            set { _maxValue = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public static class BlendCalculator;
    {
        public static float CalculateWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            if (node.BlendParameters == null || !node.BlendParameters.Any())
                return CalculateDefaultWeight(node, inputParameters);

            // Custom blend parameter calculation;
            float totalInfluence = 0f;
            int matchedParameters = 0;

            foreach (var param in node.BlendParameters)
            {
                if (inputParameters.TryGetValue(param.Key, out float inputValue))
                {
                    float influence = CalculateParameterInfluence(inputValue, param.Value, node.Threshold);
                    totalInfluence += influence;
                    matchedParameters++;
                }
            }

            return matchedParameters > 0 ? totalInfluence / matchedParameters : 0f;
        }

        private static float CalculateDefaultWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            return node.BlendType switch;
            {
                BlendType.Simple1D => Calculate1DWeight(node, inputParameters),
                BlendType.Simple2D => Calculate2DWeight(node, inputParameters),
                BlendType.Directional => CalculateDirectionalWeight(node, inputParameters),
                BlendType.FreeformDirectional => CalculateFreeformDirectionalWeight(node, inputParameters),
                BlendType.FreeformCartesian => CalculateFreeformCartesianWeight(node, inputParameters),
                _ => 1.0f // Direct blending;
            };
        }

        private static float Calculate1DWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            if (!inputParameters.Any()) return 0f;

            float inputValue = inputParameters.Values.First();
            float distance = Math.Abs(inputValue - node.Position.X);
            return Math.Max(0, 1 - distance / node.Threshold);
        }

        private static float Calculate2DWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            if (inputParameters.Count < 2) return 0f;

            float x = inputParameters.Values.ElementAt(0);
            float y = inputParameters.Values.ElementAt(1);

            var inputPos = new NodePosition(x, y);
            float distance = node.Position.DistanceTo(inputPos);

            return Math.Max(0, 1 - distance / node.Threshold);
        }

        private static float CalculateDirectionalWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            // Implementation for directional blending based on angle and speed;
            return 0f; // Simplified;
        }

        private static float CalculateFreeformDirectionalWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            // Implementation for freeform directional blending;
            return 0f; // Simplified;
        }

        private static float CalculateFreeformCartesianWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            // Implementation for freeform Cartesian blending;
            return 0f; // Simplified;
        }

        private static float CalculateParameterInfluence(float inputValue, float targetValue, float threshold)
        {
            float distance = Math.Abs(inputValue - targetValue);
            return Math.Max(0, 1 - distance / threshold);
        }
    }

    public class AnimationPose;
    {
        public Dictionary<string, BoneTransform> BoneTransforms { get; } = new Dictionary<string, BoneTransform>();

        public void BlendWith(AnimationPose other, float weight)
        {
            // Implementation for pose blending;
        }
    }

    public struct BoneTransform;
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    public struct Vector3;
    {
        public float X, Y, Z;
    }

    public struct Quaternion;
    {
        public float X, Y, Z, W;
    }

    public class BlendTreeValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    #endregion;

    #region Event Args;

    public class BlendTreeUpdatedEventArgs : EventArgs;
    {
        public Dictionary<string, float> Parameters { get; set; }
        public int ActiveNodeCount { get; set; }
        public float TotalWeight { get; set; }
        public BlendTreeEventType EventType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendWeightsChangedEventArgs : EventArgs;
    {
        public List<BlendTreeNode> ChangedNodes { get; set; }
        public float TotalWeight { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendParameterChangedEventArgs : EventArgs;
    {
        public string ParameterName { get; set; }
        public float OldValue { get; set; }
        public float NewValue { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class BlendTreeException : Exception
    {
        public BlendTreeException(string message) : base(message) { }
        public BlendTreeException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class BlendTreeValidationException : BlendTreeException;
    {
        public BlendTreeValidationException(string message) : base(message) { }
    }

    #endregion;
}
