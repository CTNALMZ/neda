using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace NEDA.CharacterSystems.AI_Behaviors.AnimationBlueprints;
{
    /// <summary>
    /// Represents a weight calculation context with all necessary parameters;
    /// </summary>
    public class WeightCalculationContext;
    {
        public Dictionary<string, float> InputParameters { get; set; } = new Dictionary<string, float>();
        public NodePosition CurrentPosition { get; set; }
        public List<BlendTreeNode> NeighborNodes { get; set; } = new List<BlendTreeNode>();
        public float TotalDistance { get; set; }
        public WeightCalculationMode CalculationMode { get; set; }
        public Dictionary<string, object> CustomData { get; set; } = new Dictionary<string, object>();

        public T GetCustomData<T>(string key, T defaultValue = default)
        {
            if (CustomData.TryGetValue(key, out object value) && value is T typedValue)
                return typedValue;
            return defaultValue;
        }

        public void SetCustomData<T>(string key, T value)
        {
            CustomData[key] = value;
        }
    }

    /// <summary>
    /// Configuration for weight calculation algorithms;
    /// </summary>
    public class WeightCalculationConfig;
    {
        public float DistanceThreshold { get; set; } = 1.0f;
        public float MinimumWeight { get; set; } = 0.001f;
        public float MaximumWeight { get; set; } = 1.0f;
        public bool NormalizeWeights { get; set; } = true;
        public float NormalizationTolerance { get; set; } = 0.001f;
        public int MaxInfluences { get; set; } = 4;
        public float FalloffExponent { get; set; } = 2.0f;
        public DistanceMetric DistanceMetric { get; set; } = DistanceMetric.Euclidean;
        public BlendAlgorithm PrimaryAlgorithm { get; set; } = BlendAlgorithm.Linear;
        public BlendAlgorithm FallbackAlgorithm { get; set; } = BlendAlgorithm.InverseDistance;
    }

    /// <summary>
    /// Main weight calculator for blend trees and animation systems;
    /// </summary>
    public class WeightCalculator : INotifyPropertyChanged, IWeightCalculator;
    {
        private readonly WeightCalculationConfig _config;
        private readonly IWeightCache _weightCache;
        private readonly IWeightOptimizer _optimizer;

        private WeightCalculationMode _currentMode;
        private bool _isCacheEnabled = true;
        private int _calculationCount;
        private float _averageCalculationTime;

        public WeightCalculationConfig Config;
        {
            get => _config;
        }

        public WeightCalculationMode CurrentMode;
        {
            get => _currentMode;
            set { _currentMode = value; OnPropertyChanged(); }
        }

        public bool IsCacheEnabled;
        {
            get => _isCacheEnabled;
            set { _isCacheEnabled = value; OnPropertyChanged(); }
        }

        public int CalculationCount;
        {
            get => _calculationCount;
            private set { _calculationCount = value; OnPropertyChanged(); }
        }

        public float AverageCalculationTime;
        {
            get => _averageCalculationTime;
            private set { _averageCalculationTime = value; OnPropertyChanged(); }
        }

        public ObservableCollection<WeightCalculationResult> RecentCalculations { get; } = new ObservableCollection<WeightCalculationResult>();

        public event EventHandler<WeightCalculationEventArgs> CalculationStarted;
        public event EventHandler<WeightCalculationEventArgs> CalculationCompleted;
        public event EventHandler<WeightCacheEventArgs> CacheUpdated;

        public WeightCalculator(WeightCalculationConfig config = null, IWeightCache weightCache = null, IWeightOptimizer optimizer = null)
        {
            _config = config ?? new WeightCalculationConfig();
            _weightCache = weightCache;
            _optimizer = optimizer;
            _currentMode = WeightCalculationMode.Standard;
        }

        /// <summary>
        /// Calculates weights for a set of nodes based on input parameters;
        /// </summary>
        public WeightCalculationResult CalculateWeights(List<BlendTreeNode> nodes, Dictionary<string, float> inputParameters)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                OnCalculationStarted(new WeightCalculationEventArgs;
                {
                    Nodes = nodes,
                    InputParameters = inputParameters,
                    Timestamp = DateTime.UtcNow;
                });

                // Check cache first;
                var cacheKey = GenerateCacheKey(nodes, inputParameters);
                if (_isCacheEnabled && _weightCache != null && _weightCache.TryGetValue(cacheKey, out var cachedResult))
                {
                    OnCalculationCompleted(new WeightCalculationEventArgs;
                    {
                        Nodes = nodes,
                        InputParameters = inputParameters,
                        Result = cachedResult,
                        WasCached = true,
                        Timestamp = DateTime.UtcNow;
                    });

                    return cachedResult;
                }

                var context = CreateCalculationContext(nodes, inputParameters);
                var result = CalculateWeightsInternal(nodes, context);

                // Cache the result;
                if (_isCacheEnabled && _weightCache != null)
                {
                    _weightCache.Add(cacheKey, result);
                    OnCacheUpdated(new WeightCacheEventArgs;
                    {
                        CacheKey = cacheKey,
                        Result = result,
                        Operation = CacheOperation.Add,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                UpdateStatistics(stopwatch.ElapsedMilliseconds);

                OnCalculationCompleted(new WeightCalculationEventArgs;
                {
                    Nodes = nodes,
                    InputParameters = inputParameters,
                    Result = result,
                    WasCached = false,
                    CalculationTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow;
                });

                return result;
            }
            catch (Exception ex)
            {
                throw new WeightCalculationException("Weight calculation failed", ex);
            }
        }

        /// <summary>
        /// Calculates weight for a single node;
        /// </summary>
        public float CalculateNodeWeight(BlendTreeNode node, Dictionary<string, float> inputParameters)
        {
            var context = CreateCalculationContext(new List<BlendTreeNode> { node }, inputParameters);
            return CalculateSingleNodeWeight(node, context);
        }

        /// <summary>
        /// Calculates weights using a specific algorithm;
        /// </summary>
        public WeightCalculationResult CalculateWeightsWithAlgorithm(List<BlendTreeNode> nodes,
            Dictionary<string, float> inputParameters, BlendAlgorithm algorithm)
        {
            var originalAlgorithm = _config.PrimaryAlgorithm;
            try
            {
                _config.PrimaryAlgorithm = algorithm;
                return CalculateWeights(nodes, inputParameters);
            }
            finally
            {
                _config.PrimaryAlgorithm = originalAlgorithm;
            }
        }

        /// <summary>
        /// Performs progressive weight calculation for smooth transitions;
        /// </summary>
        public WeightCalculationResult CalculateWeightsProgressive(List<BlendTreeNode> nodes,
            Dictionary<string, float> inputParameters, WeightCalculationResult previousResult, float blendFactor)
        {
            var newResult = CalculateWeights(nodes, inputParameters);

            if (previousResult == null || blendFactor >= 1.0f)
                return newResult;

            // Blend between previous and new weights;
            var blendedWeights = new Dictionary<string, float>();
            foreach (var kvp in newResult.NodeWeights)
            {
                var nodeId = kvp.Key;
                var newWeight = kvp.Value;
                var previousWeight = previousResult.NodeWeights.TryGetValue(nodeId, out float weight) ? weight : 0f;

                var blendedWeight = MathUtils.Lerp(previousWeight, newWeight, blendFactor);
                blendedWeights[nodeId] = blendedWeight;
            }

            return new WeightCalculationResult;
            {
                NodeWeights = blendedWeights,
                TotalWeight = blendedWeights.Values.Sum(),
                CalculationMode = WeightCalculationMode.Progressive,
                WasOptimized = newResult.WasOptimized;
            };
        }

        /// <summary>
        /// Optimizes the weight calculation for performance;
        /// </summary>
        public WeightCalculationResult CalculateWeightsOptimized(List<BlendTreeNode> nodes,
            Dictionary<string, float> inputParameters)
        {
            if (_optimizer == null)
                return CalculateWeights(nodes, inputParameters);

            var optimizedNodes = _optimizer.OptimizeNodes(nodes, inputParameters);
            var result = CalculateWeights(optimizedNodes, inputParameters);
            result.WasOptimized = true;

            return result;
        }

        /// <summary>
        /// Calculates weights for 1D blend space;
        /// </summary>
        public WeightCalculationResult Calculate1DWeights(List<BlendTreeNode> nodes, float inputValue)
        {
            if (nodes == null || !nodes.Any())
                return new WeightCalculationResult();

            var inputParameters = new Dictionary<string, float> { { "Blend", inputValue } };
            return CalculateWeights(nodes, inputParameters);
        }

        /// <summary>
        /// Calculates weights for 2D blend space;
        /// </summary>
        public WeightCalculationResult Calculate2DWeights(List<BlendTreeNode> nodes, float x, float y)
        {
            if (nodes == null || !nodes.Any())
                return new WeightCalculationResult();

            var inputParameters = new Dictionary<string, float>
            {
                { "BlendX", x },
                { "BlendY", y }
            };

            return CalculateWeights(nodes, inputParameters);
        }

        /// <summary>
        /// Calculates weights for directional blend space;
        /// </summary>
        public WeightCalculationResult CalculateDirectionalWeights(List<BlendTreeNode> nodes, float direction, float speed)
        {
            if (nodes == null || !nodes.Any())
                return new WeightCalculationResult();

            var inputParameters = new Dictionary<string, float>
            {
                { "Direction", direction },
                { "Speed", speed }
            };

            return CalculateWeights(nodes, inputParameters);
        }

        /// <summary>
        /// Normalizes a set of weights to ensure they sum to 1.0;
        /// </summary>
        public Dictionary<string, float> NormalizeWeights(Dictionary<string, float> weights, float tolerance = 0.001f)
        {
            if (weights == null || !weights.Any())
                return weights;

            var totalWeight = weights.Values.Sum();

            // If total weight is very small or zero, distribute equally;
            if (totalWeight < tolerance)
            {
                var equalWeight = 1.0f / weights.Count;
                return weights.ToDictionary(kvp => kvp.Key, kvp => equalWeight);
            }

            // Normalize weights;
            var normalized = new Dictionary<string, float>();
            foreach (var kvp in weights)
            {
                normalized[kvp.Key] = kvp.Value / totalWeight;
            }

            return normalized;
        }

        /// <summary>
        /// Applies constraints to weights (min/max values, max influences)
        /// </summary>
        public Dictionary<string, float> ApplyConstraints(Dictionary<string, float> weights,
            float minWeight = 0.0f, float maxWeight = 1.0f, int maxInfluences = 0)
        {
            if (weights == null) return weights;

            var constrained = new Dictionary<string, float>();

            // Apply min/max constraints;
            foreach (var kvp in weights)
            {
                var weight = Math.Max(minWeight, Math.Min(maxWeight, kvp.Value));
                if (weight > _config.MinimumWeight)
                {
                    constrained[kvp.Key] = weight;
                }
            }

            // Apply max influences constraint;
            if (maxInfluences > 0 && constrained.Count > maxInfluences)
            {
                var topWeights = constrained;
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(maxInfluences)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                // Re-normalize the top weights;
                return NormalizeWeights(topWeights);
            }

            return constrained;
        }

        /// <summary>
        /// Clears the weight cache;
        /// </summary>
        public void ClearCache()
        {
            _weightCache?.Clear();
            OnCacheUpdated(new WeightCacheEventArgs;
            {
                Operation = CacheOperation.Clear,
                Timestamp = DateTime.UtcNow;
            });
        }

        /// <summary>
        /// Pre-calculates weights for common parameter combinations;
        /// </summary>
        public void PrecalculateWeights(List<BlendTreeNode> nodes, IEnumerable<Dictionary<string, float>> parameterSets)
        {
            if (_weightCache == null) return;

            foreach (var parameters in parameterSets)
            {
                var cacheKey = GenerateCacheKey(nodes, parameters);
                if (!_weightCache.ContainsKey(cacheKey))
                {
                    var result = CalculateWeightsInternal(nodes, CreateCalculationContext(nodes, parameters));
                    _weightCache.Add(cacheKey, result);
                }
            }
        }

        /// <summary>
        /// Validates that weights meet required criteria;
        /// </summary>
        public WeightValidationResult ValidateWeights(WeightCalculationResult result)
        {
            var validation = new WeightValidationResult();

            if (result.NodeWeights == null)
            {
                validation.Errors.Add("Node weights dictionary is null");
                return validation;
            }

            var totalWeight = result.NodeWeights.Values.Sum();

            // Check normalization;
            if (_config.NormalizeWeights && Math.Abs(totalWeight - 1.0f) > _config.NormalizationTolerance)
            {
                validation.Warnings.Add($"Weights are not properly normalized. Total: {totalWeight}");
            }

            // Check for negative weights;
            var negativeWeights = result.NodeWeights.Where(kvp => kvp.Value < 0).ToList();
            if (negativeWeights.Any())
            {
                validation.Errors.Add($"Found {negativeWeights.Count} nodes with negative weights");
            }

            // Check for NaN or infinite values;
            var invalidWeights = result.NodeWeights.Where(kvp => float.IsNaN(kvp.Value) || float.IsInfinity(kvp.Value)).ToList();
            if (invalidWeights.Any())
            {
                validation.Errors.Add($"Found {invalidWeights.Count} nodes with invalid weight values");
            }

            // Check minimum weight threshold;
            var belowThreshold = result.NodeWeights.Where(kvp => kvp.Value > 0 && kvp.Value < _config.MinimumWeight).ToList();
            if (belowThreshold.Any())
            {
                validation.Warnings.Add($"Found {belowThreshold.Count} nodes with weights below minimum threshold");
            }

            validation.IsValid = !validation.Errors.Any();
            validation.TotalWeight = totalWeight;
            validation.NodeCount = result.NodeWeights.Count;

            return validation;
        }

        private WeightCalculationResult CalculateWeightsInternal(List<BlendTreeNode> nodes, WeightCalculationContext context)
        {
            if (nodes == null || !nodes.Any())
                return new WeightCalculationResult();

            var weights = new Dictionary<string, float>();

            switch (context.CalculationMode)
            {
                case WeightCalculationMode.Standard:
                    weights = CalculateStandardWeights(nodes, context);
                    break;
                case WeightCalculationMode.Optimized:
                    weights = CalculateOptimizedWeights(nodes, context);
                    break;
                case WeightCalculationMode.Progressive:
                    weights = CalculateProgressiveWeights(nodes, context);
                    break;
            }

            // Apply constraints;
            weights = ApplyConstraints(weights, _config.MinimumWeight, _config.MaximumWeight, _config.MaxInfluences);

            // Normalize if required;
            if (_config.NormalizeWeights)
            {
                weights = NormalizeWeights(weights, _config.NormalizationTolerance);
            }

            return new WeightCalculationResult;
            {
                NodeWeights = weights,
                TotalWeight = weights.Values.Sum(),
                CalculationMode = context.CalculationMode,
                Context = context;
            };
        }

        private Dictionary<string, float> CalculateStandardWeights(List<BlendTreeNode> nodes, WeightCalculationContext context)
        {
            var weights = new Dictionary<string, float>();

            foreach (var node in nodes)
            {
                var weight = CalculateSingleNodeWeight(node, context);
                if (weight > _config.MinimumWeight)
                {
                    weights[node.NodeId] = weight;
                }
            }

            return weights;
        }

        private Dictionary<string, float> CalculateOptimizedWeights(List<BlendTreeNode> nodes, WeightCalculationContext context)
        {
            // Use optimizer if available, otherwise fall back to standard calculation;
            if (_optimizer != null)
            {
                var optimizedNodes = _optimizer.OptimizeNodes(nodes, context.InputParameters);
                return CalculateStandardWeights(optimizedNodes, context);
            }

            return CalculateStandardWeights(nodes, context);
        }

        private Dictionary<string, float> CalculateProgressiveWeights(List<BlendTreeNode> nodes, WeightCalculationContext context)
        {
            // Get previous weights from context;
            var previousWeights = context.GetCustomData<Dictionary<string, float>>("PreviousWeights");
            var blendFactor = context.GetCustomData<float>("BlendFactor", 1.0f);

            var newWeights = CalculateStandardWeights(nodes, context);

            if (previousWeights != null && blendFactor < 1.0f)
            {
                // Blend between previous and new weights;
                var blendedWeights = new Dictionary<string, float>();
                foreach (var node in nodes)
                {
                    var nodeId = node.NodeId;
                    var newWeight = newWeights.GetValueOrDefault(nodeId, 0f);
                    var previousWeight = previousWeights.GetValueOrDefault(nodeId, 0f);

                    var blendedWeight = MathUtils.Lerp(previousWeight, newWeight, blendFactor);
                    blendedWeights[nodeId] = blendedWeight;
                }
                return blendedWeights;
            }

            return newWeights;
        }

        private float CalculateSingleNodeWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            return node.BlendType switch;
            {
                BlendType.Direct => CalculateDirectWeight(node, context),
                BlendType.Simple1D => Calculate1DWeight(node, context),
                BlendType.Simple2D => Calculate2DWeight(node, context),
                BlendType.Directional => CalculateDirectionalWeight(node, context),
                BlendType.FreeformDirectional => CalculateFreeformDirectionalWeight(node, context),
                BlendType.FreeformCartesian => CalculateFreeformCartesianWeight(node, context),
                _ => CalculateDefaultWeight(node, context)
            };
        }

        private float CalculateDirectWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            return 1.0f; // Direct nodes always have full weight;
        }

        private float Calculate1DWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            if (!context.InputParameters.Any()) return 0f;

            var inputValue = context.InputParameters.Values.First();
            var distance = Math.Abs(inputValue - node.Position.X);

            return CalculateWeightFromDistance(distance, node.Threshold);
        }

        private float Calculate2DWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            if (context.InputParameters.Count < 2) return 0f;

            var x = context.InputParameters.Values.ElementAt(0);
            var y = context.InputParameters.Values.ElementAt(1);

            var inputPos = new NodePosition(x, y);
            var distance = CalculateDistance(node.Position, inputPos, _config.DistanceMetric);

            return CalculateWeightFromDistance(distance, node.Threshold);
        }

        private float CalculateDirectionalWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            if (context.InputParameters.Count < 2) return 0f;

            var direction = context.InputParameters["Direction"];
            var speed = context.InputParameters["Speed"];

            // Calculate angular distance for direction;
            var angularDistance = MathUtils.AngularDistance(direction, node.Position.X);
            var speedDistance = Math.Abs(speed - node.Position.Y);

            // Combine distances with appropriate weighting;
            var totalDistance = (angularDistance * 0.7f) + (speedDistance * 0.3f);

            return CalculateWeightFromDistance(totalDistance, node.Threshold);
        }

        private float CalculateFreeformDirectionalWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            // Similar to directional but with more complex distance calculation;
            return CalculateDirectionalWeight(node, context);
        }

        private float CalculateFreeformCartesianWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            // Similar to 2D but with different falloff characteristics;
            return Calculate2DWeight(node, context);
        }

        private float CalculateDefaultWeight(BlendTreeNode node, WeightCalculationContext context)
        {
            // Fallback calculation;
            return 0.0f;
        }

        private float CalculateWeightFromDistance(float distance, float threshold)
        {
            if (distance >= threshold) return 0.0f;

            var normalizedDistance = distance / threshold;

            return _config.PrimaryAlgorithm switch;
            {
                BlendAlgorithm.Linear => 1.0f - normalizedDistance,
                BlendAlgorithm.Quadratic => 1.0f - (normalizedDistance * normalizedDistance),
                BlendAlgorithm.Cubic => 1.0f - (normalizedDistance * normalizedDistance * normalizedDistance),
                BlendAlgorithm.InverseDistance => 1.0f / (1.0f + normalizedDistance * _config.FalloffExponent),
                BlendAlgorithm.Barycentric => CalculateBarycentricWeight(normalizedDistance),
                _ => 1.0f - normalizedDistance;
            };
        }

        private float CalculateBarycentricWeight(float distance)
        {
            // Barycentric coordinate calculation for triangle interpolation;
            return Math.Max(0, 1 - distance * distance);
        }

        private float CalculateDistance(NodePosition a, NodePosition b, DistanceMetric metric)
        {
            return metric switch;
            {
                DistanceMetric.Euclidean => (float)Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)),
                DistanceMetric.Manhattan => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y),
                DistanceMetric.Chebyshev => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)),
                _ => (float)Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2))
            };
        }

        private WeightCalculationContext CreateCalculationContext(List<BlendTreeNode> nodes, Dictionary<string, float> inputParameters)
        {
            var currentPosition = CalculateAveragePosition(nodes);
            var neighbors = FindNearestNeighbors(nodes, currentPosition, 8); // Find 8 nearest neighbors;

            return new WeightCalculationContext;
            {
                InputParameters = inputParameters,
                CurrentPosition = currentPosition,
                NeighborNodes = neighbors,
                CalculationMode = _currentMode,
                TotalDistance = CalculateTotalDistance(nodes, currentPosition)
            };
        }

        private NodePosition CalculateAveragePosition(List<BlendTreeNode> nodes)
        {
            if (!nodes.Any()) return new NodePosition(0, 0);

            var avgX = nodes.Average(n => n.Position.X);
            var avgY = nodes.Average(n => n.Position.Y);

            return new NodePosition(avgX, avgY);
        }

        private List<BlendTreeNode> FindNearestNeighbors(List<BlendTreeNode> nodes, NodePosition position, int count)
        {
            return nodes;
                .OrderBy(n => CalculateDistance(n.Position, position, _config.DistanceMetric))
                .Take(count)
                .ToList();
        }

        private float CalculateTotalDistance(List<BlendTreeNode> nodes, NodePosition referencePoint)
        {
            return nodes.Sum(n => CalculateDistance(n.Position, referencePoint, _config.DistanceMetric));
        }

        private string GenerateCacheKey(List<BlendTreeNode> nodes, Dictionary<string, float> parameters)
        {
            var nodeIds = string.Join("|", nodes.OrderBy(n => n.NodeId).Select(n => n.NodeId));
            var paramString = string.Join("|", parameters.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

            return $"{nodeIds}|{paramString}|{_currentMode}";
        }

        private void UpdateStatistics(long calculationTimeMs)
        {
            CalculationCount++;
            AverageCalculationTime = (AverageCalculationTime * (CalculationCount - 1) + calculationTimeMs) / CalculationCount;

            // Keep recent calculations list manageable;
            if (RecentCalculations.Count > 100)
            {
                RecentCalculations.RemoveAt(0);
            }
        }

        private void OnCalculationStarted(WeightCalculationEventArgs e)
        {
            CalculationStarted?.Invoke(this, e);
        }

        private void OnCalculationCompleted(WeightCalculationEventArgs e)
        {
            CalculationCompleted?.Invoke(this, e);
            RecentCalculations.Add(e.Result);
        }

        private void OnCacheUpdated(WeightCacheEventArgs e)
        {
            CacheUpdated?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum WeightCalculationMode;
    {
        Standard,
        Optimized,
        Progressive,
        Predictive;
    }

    public enum BlendAlgorithm;
    {
        Linear,
        Quadratic,
        Cubic,
        InverseDistance,
        Barycentric,
        Gaussian;
    }

    public enum DistanceMetric;
    {
        Euclidean,
        Manhattan,
        Chebyshev;
    }

    public enum CacheOperation;
    {
        Add,
        Remove,
        Clear;
    }

    public class WeightCalculationResult;
    {
        public Dictionary<string, float> NodeWeights { get; set; } = new Dictionary<string, float>();
        public float TotalWeight { get; set; }
        public WeightCalculationMode CalculationMode { get; set; }
        public bool WasOptimized { get; set; }
        public WeightCalculationContext Context { get; set; }
        public TimeSpan CalculationTime { get; set; }
    }

    public class WeightValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public float TotalWeight { get; set; }
        public int NodeCount { get; set; }
    }

    public class WeightCalculationEventArgs : EventArgs;
    {
        public List<BlendTreeNode> Nodes { get; set; }
        public Dictionary<string, float> InputParameters { get; set; }
        public WeightCalculationResult Result { get; set; }
        public bool WasCached { get; set; }
        public TimeSpan CalculationTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class WeightCacheEventArgs : EventArgs;
    {
        public string CacheKey { get; set; }
        public WeightCalculationResult Result { get; set; }
        public CacheOperation Operation { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public interface IWeightCalculator;
    {
        WeightCalculationResult CalculateWeights(List<BlendTreeNode> nodes, Dictionary<string, float> inputParameters);
        float CalculateNodeWeight(BlendTreeNode node, Dictionary<string, float> inputParameters);
    }

    public interface IWeightCache;
    {
        bool TryGetValue(string key, out WeightCalculationResult result);
        void Add(string key, WeightCalculationResult result);
        bool Remove(string key);
        void Clear();
        bool ContainsKey(string key);
    }

    public interface IWeightOptimizer;
    {
        List<BlendTreeNode> OptimizeNodes(List<BlendTreeNode> nodes, Dictionary<string, float> parameters);
        bool ShouldOptimize(List<BlendTreeNode> nodes);
    }

    public static class MathUtils;
    {
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Math.Max(0, Math.Min(1, t));
        }

        public static float AngularDistance(float a, float b)
        {
            var diff = Math.Abs(a - b) % 360;
            return diff > 180 ? 360 - diff : diff;
        }

        public static float SmoothStep(float edge0, float edge1, float x)
        {
            x = Math.Max(0, Math.Min(1, (x - edge0) / (edge1 - edge0)));
            return x * x * (3 - 2 * x);
        }
    }

    #endregion;

    #region Exceptions;

    public class WeightCalculationException : Exception
    {
        public WeightCalculationException(string message) : base(message) { }
        public WeightCalculationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class WeightValidationException : WeightCalculationException;
    {
        public WeightValidationException(string message) : base(message) { }
    }

    #endregion;
}
