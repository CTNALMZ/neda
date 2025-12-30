using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace NEDA.CharacterSystems.AI_Behaviors.AnimationBlueprints;
{
    /// <summary>
    /// Represents a single blend operation between animation states;
    /// </summary>
    public class BlendOperation : INotifyPropertyChanged;
    {
        private string _operationId;
        private AnimState _sourceState;
        private AnimState _targetState;
        private float _blendWeight;
        private float _blendTime;
        private BlendCurve _blendCurve;
        private BlendState _blendState;
        private float _currentTime;

        public string OperationId;
        {
            get => _operationId;
            set { _operationId = value; OnPropertyChanged(); }
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

        public float BlendWeight;
        {
            get => _blendWeight;
            set { _blendWeight = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public float BlendTime;
        {
            get => _blendTime;
            set { _blendTime = Math.Max(0, value); OnPropertyChanged(); }
        }

        public BlendCurve BlendCurve;
        {
            get => _blendCurve;
            set { _blendCurve = value; OnPropertyChanged(); }
        }

        public BlendState BlendState;
        {
            get => _blendState;
            set { _blendState = value; OnPropertyChanged(); }
        }

        public float CurrentTime;
        {
            get => _currentTime;
            set { _currentTime = Math.Max(0, value); OnPropertyChanged(); }
        }

        public float NormalizedTime => BlendTime > 0 ? CurrentTime / BlendTime : 1.0f;
        public bool IsComplete => BlendState == BlendState.Completed || NormalizedTime >= 1.0f;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public BlendOperation()
        {
            _operationId = Guid.NewGuid().ToString();
            _blendCurve = BlendCurve.Linear;
            _blendState = BlendState.Pending;
        }

        public BlendOperation(AnimState source, AnimState target, float blendTime = 0.2f) : this()
        {
            _sourceState = source;
            _targetState = target;
            _blendTime = blendTime;
        }

        public float CalculateCurrentWeight()
        {
            if (BlendState == BlendState.Completed) return 1.0f;
            if (BlendState == BlendState.Pending) return 0.0f;

            var normalizedTime = NormalizedTime;
            var curveValue = CalculateCurveValue(normalizedTime, BlendCurve);
            return curveValue * BlendWeight;
        }

        public void Update(float deltaTime)
        {
            if (BlendState != BlendState.Blending) return;

            CurrentTime += deltaTime;

            if (NormalizedTime >= 1.0f)
            {
                BlendState = BlendState.Completed;
                BlendWeight = 1.0f;
            }
        }

        public void StartBlend()
        {
            if (BlendState == BlendState.Pending)
            {
                BlendState = BlendState.Blending;
                CurrentTime = 0;
            }
        }

        public void CompleteImmediately()
        {
            BlendState = BlendState.Completed;
            CurrentTime = BlendTime;
            BlendWeight = 1.0f;
        }

        public void Cancel()
        {
            BlendState = BlendState.Cancelled;
            BlendWeight = 0.0f;
        }

        private float CalculateCurveValue(float time, BlendCurve curve)
        {
            return curve switch;
            {
                BlendCurve.Linear => time,
                BlendCurve.EaseIn => EaseIn(time),
                BlendCurve.EaseOut => EaseOut(time),
                BlendCurve.EaseInOut => EaseInOut(time),
                BlendCurve.Exponential => Exponential(time),
                BlendCurve.SmoothStep => SmoothStep(time),
                _ => time;
            };
        }

        private float EaseIn(float t) => t * t;
        private float EaseOut(float t) => 1 - (1 - t) * (1 - t);
        private float EaseInOut(float t) => t < 0.5f ? 2 * t * t : 1 - (2 * t - 2) * (2 * t - 2) / 2;
        private float Exponential(float t) => (float)(Math.Exp(6 * (t - 1)));
        private float SmoothStep(float t) => t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Represents a blended pose from multiple animation states;
    /// </summary>
    public class BlendedPose;
    {
        public Dictionary<string, BonePose> BonePoses { get; } = new Dictionary<string, BonePose>();
        public float TotalWeight { get; set; }
        public int SourceCount => BonePoses.Count;

        public void AddPose(BonePose pose, float weight, string boneName)
        {
            if (BonePoses.TryGetValue(boneName, out var existingPose))
            {
                // Blend with existing pose;
                BonePoses[boneName] = BlendPoses(existingPose, pose, weight / (TotalWeight + weight));
            }
            else;
            {
                BonePoses[boneName] = pose;
            }

            TotalWeight += weight;
        }

        public void Normalize()
        {
            if (TotalWeight <= 0) return;

            foreach (var boneName in BonePoses.Keys.ToList())
            {
                var pose = BonePoses[boneName];
                pose.Weight /= TotalWeight;
                BonePoses[boneName] = pose;
            }

            TotalWeight = 1.0f;
        }

        private BonePose BlendPoses(BonePose poseA, BonePose poseB, float weight)
        {
            return new BonePose;
            {
                Transform = BlendTransforms(poseA.Transform, poseB.Transform, weight),
                Weight = poseA.Weight + poseB.Weight;
            };
        }

        private BoneTransform BlendTransforms(BoneTransform transformA, BoneTransform transformB, float weight)
        {
            return new BoneTransform;
            {
                Position = Vector3.Lerp(transformA.Position, transformB.Position, weight),
                Rotation = Quaternion.Slerp(transformA.Rotation, transformB.Rotation, weight),
                Scale = Vector3.Lerp(transformA.Scale, transformB.Scale, weight)
            };
        }
    }

    /// <summary>
    /// Main state blender for smooth transitions between animation states;
    /// </summary>
    public class StateBlender : INotifyPropertyChanged, IStateBlender;
    {
        private readonly IAnimationController _animationController;
        private readonly IPoseSolver _poseSolver;
        private readonly IBlendOptimizer _blendOptimizer;

        private BlendMode _currentBlendMode;
        private float _globalBlendWeight = 1.0f;
        private bool _isAutoUpdateEnabled = true;
        private int _maxConcurrentBlends = 4;
        private float _blendThreshold = 0.01f;

        public BlendMode CurrentBlendMode;
        {
            get => _currentBlendMode;
            set { _currentBlendMode = value; OnPropertyChanged(); }
        }

        public float GlobalBlendWeight;
        {
            get => _globalBlendWeight;
            set { _globalBlendWeight = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public bool IsAutoUpdateEnabled;
        {
            get => _isAutoUpdateEnabled;
            set { _isAutoUpdateEnabled = value; OnPropertyChanged(); }
        }

        public int MaxConcurrentBlends;
        {
            get => _maxConcurrentBlends;
            set { _maxConcurrentBlends = Math.Max(1, value); OnPropertyChanged(); }
        }

        public float BlendThreshold;
        {
            get => _blendThreshold;
            set { _blendThreshold = Math.Max(0.001f, value); OnPropertyChanged(); }
        }

        public ObservableCollection<BlendOperation> ActiveBlends { get; } = new ObservableCollection<BlendOperation>();
        public ObservableCollection<BlendOperation> BlendHistory { get; } = new ObservableCollection<BlendOperation>();
        public ObservableCollection<BlendLayer> BlendLayers { get; } = new ObservableCollection<BlendLayer>();

        public event EventHandler<BlendStartedEventArgs> BlendStarted;
        public event EventHandler<BlendCompletedEventArgs> BlendCompleted;
        public event EventHandler<BlendProgressEventArgs> BlendProgress;
        public event EventHandler<PoseBlendedEventArgs> PoseBlended;

        public StateBlender(IAnimationController animationController, IPoseSolver poseSolver = null,
                          IBlendOptimizer blendOptimizer = null)
        {
            _animationController = animationController ?? throw new ArgumentNullException(nameof(animationController));
            _poseSolver = poseSolver ?? new DefaultPoseSolver();
            _blendOptimizer = blendOptimizer;
            _currentBlendMode = BlendMode.Smooth;
        }

        /// <summary>
        /// Starts a blend between two animation states;
        /// </summary>
        public BlendOperation StartBlend(AnimState sourceState, AnimState targetState, float blendTime = 0.2f,
                                       BlendCurve blendCurve = BlendCurve.Linear)
        {
            if (sourceState == null && targetState == null)
                throw new ArgumentException("Both source and target states cannot be null");

            // Check if we're at the blend limit;
            if (ActiveBlends.Count >= MaxConcurrentBlends)
            {
                // Cancel the oldest blend to make room;
                var oldestBlend = ActiveBlends.OrderBy(b => b.CurrentTime).FirstOrDefault();
                if (oldestBlend != null)
                {
                    CancelBlend(oldestBlend.OperationId);
                }
            }

            var blendOperation = new BlendOperation(sourceState, targetState, blendTime)
            {
                BlendCurve = blendCurve,
                BlendWeight = 1.0f;
            };

            ActiveBlends.Add(blendOperation);
            blendOperation.StartBlend();

            OnBlendStarted(new BlendStartedEventArgs;
            {
                BlendOperation = blendOperation,
                Timestamp = DateTime.UtcNow;
            });

            return blendOperation;
        }

        /// <summary>
        /// Starts a crossfade blend between multiple states;
        /// </summary>
        public List<BlendOperation> StartCrossfade(List<AnimState> states, List<float> blendTimes, BlendCurve blendCurve = BlendCurve.Linear)
        {
            if (states == null || states.Count < 2)
                throw new ArgumentException("Crossfade requires at least 2 states");

            var operations = new List<BlendOperation>();

            for (int i = 0; i < states.Count - 1; i++)
            {
                var blendTime = i < blendTimes.Count ? blendTimes[i] : 0.2f;
                var operation = StartBlend(states[i], states[i + 1], blendTime, blendCurve);
                operations.Add(operation);
            }

            return operations;
        }

        /// <summary>
        /// Starts an additive blend on top of the current state;
        /// </summary>
        public BlendOperation StartAdditiveBlend(AnimState baseState, AnimState additiveState, float blendTime = 0.2f)
        {
            var blendOperation = new BlendOperation(baseState, additiveState, blendTime)
            {
                BlendCurve = BlendCurve.Linear,
                BlendWeight = 1.0f;
            };

            // Mark as additive blend;
            blendOperation.SetProperty("IsAdditive", true);

            ActiveBlends.Add(blendOperation);
            blendOperation.StartBlend();

            return blendOperation;
        }

        /// <summary>
        /// Updates all active blends;
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!IsAutoUpdateEnabled) return;

            var completedBlends = new List<BlendOperation>();

            foreach (var blend in ActiveBlends)
            {
                blend.Update(deltaTime);

                // Notify progress;
                OnBlendProgress(new BlendProgressEventArgs;
                {
                    BlendOperation = blend,
                    Progress = blend.NormalizedTime,
                    Timestamp = DateTime.UtcNow;
                });

                if (blend.IsComplete)
                {
                    completedBlends.Add(blend);
                }
            }

            // Remove completed blends;
            foreach (var completedBlend in completedBlends)
            {
                CompleteBlend(completedBlend);
            }

            // Update blend layers;
            UpdateBlendLayers(deltaTime);

            // Calculate and apply final blended pose;
            if (ActiveBlends.Any(b => b.BlendState == BlendState.Blending || b.BlendState == BlendState.Completed))
            {
                var blendedPose = CalculateBlendedPose();
                ApplyBlendedPose(blendedPose);
            }
        }

        /// <summary>
        /// Calculates the final blended pose from all active blends;
        /// </summary>
        public BlendedPose CalculateBlendedPose()
        {
            var blendedPose = new BlendedPose();

            // Group blends by priority and type;
            var prioritizedBlends = ActiveBlends;
                .Where(b => b.BlendState == BlendState.Blending || b.BlendState == BlendState.Completed)
                .OrderBy(b => b.GetProperty<int>("Priority", 0))
                .ThenBy(b => b.CurrentTime);

            foreach (var blend in prioritizedBlends)
            {
                if (blend.BlendWeight < BlendThreshold) continue;

                var isAdditive = blend.GetProperty<bool>("IsAdditive", false);
                var currentWeight = blend.CalculateCurrentWeight() * GlobalBlendWeight;

                if (isAdditive)
                {
                    ApplyAdditiveBlend(blendedPose, blend, currentWeight);
                }
                else;
                {
                    ApplyStandardBlend(blendedPose, blend, currentWeight);
                }
            }

            // Apply layer blends;
            foreach (var layer in BlendLayers.Where(l => l.IsEnabled))
            {
                ApplyLayerBlend(blendedPose, layer);
            }

            // Normalize the final pose;
            if (blendedPose.TotalWeight > 0)
            {
                blendedPose.Normalize();
            }

            return blendedPose;
        }

        /// <summary>
        /// Gets the current blend weight between two specific states;
        /// </summary>
        public float GetBlendWeight(AnimState sourceState, AnimState targetState)
        {
            var blend = ActiveBlends.FirstOrDefault(b =>
                b.SourceState == sourceState && b.TargetState == targetState);

            return blend?.CalculateCurrentWeight() ?? 0f;
        }

        /// <summary>
        /// Gets the total blend weight for a specific state;
        /// </summary>
        public float GetStateBlendWeight(AnimState state)
        {
            return ActiveBlends;
                .Where(b => (b.SourceState == state || b.TargetState == state) &&
                           (b.BlendState == BlendState.Blending || b.BlendState == BlendState.Completed))
                .Sum(b => b.CalculateCurrentWeight());
        }

        /// <summary>
        /// Cancels an active blend operation;
        /// </summary>
        public bool CancelBlend(string operationId)
        {
            var blend = ActiveBlends.FirstOrDefault(b => b.OperationId == operationId);
            if (blend == null) return false;

            blend.Cancel();
            ActiveBlends.Remove(blend);
            BlendHistory.Add(blend);

            OnBlendCompleted(new BlendCompletedEventArgs;
            {
                BlendOperation = blend,
                WasCompleted = false,
                Timestamp = DateTime.UtcNow;
            });

            return true;
        }

        /// <summary>
        /// Completes a blend operation immediately;
        /// </summary>
        public bool CompleteBlendImmediately(string operationId)
        {
            var blend = ActiveBlends.FirstOrDefault(b => b.OperationId == operationId);
            if (blend == null) return false;

            blend.CompleteImmediately();
            CompleteBlend(blend);

            return true;
        }

        /// <summary>
        /// Adds a blend layer for complex blending scenarios;
        /// </summary>
        public BlendLayer AddBlendLayer(string layerName, float weight = 1.0f, LayerBlendMode blendMode = LayerBlendMode.Override)
        {
            var layer = new BlendLayer;
            {
                LayerName = layerName,
                Weight = weight,
                BlendMode = blendMode;
            };

            BlendLayers.Add(layer);
            return layer;
        }

        /// <summary>
        /// Sets the blend weight for a specific operation;
        /// </summary>
        public void SetBlendWeight(string operationId, float weight)
        {
            var blend = ActiveBlends.FirstOrDefault(b => b.OperationId == operationId);
            if (blend != null)
            {
                blend.BlendWeight = weight;
            }
        }

        /// <summary>
        /// Clears all active blends;
        /// </summary>
        public void ClearAllBlends()
        {
            foreach (var blend in ActiveBlends.ToList())
            {
                CancelBlend(blend.OperationId);
            }
        }

        /// <summary>
        /// Optimizes the blending operations for performance;
        /// </summary>
        public void OptimizeBlends()
        {
            _blendOptimizer?.Optimize(this);
        }

        /// <summary>
        /// Gets blending statistics and metrics;
        /// </summary>
        public BlendStatistics GetStatistics()
        {
            return new BlendStatistics;
            {
                ActiveBlendCount = ActiveBlends.Count,
                CompletedBlendCount = BlendHistory.Count,
                TotalLayerCount = BlendLayers.Count,
                ActiveLayerCount = BlendLayers.Count(l => l.IsEnabled),
                AverageBlendTime = ActiveBlends.Any() ? ActiveBlends.Average(b => b.BlendTime) : 0,
                TotalBlendWeight = ActiveBlends.Sum(b => b.BlendWeight)
            };
        }

        private void ApplyStandardBlend(BlendedPose blendedPose, BlendOperation blend, float weight)
        {
            if (blend.SourceState != null && weight < 1.0f)
            {
                var sourcePose = _poseSolver.SolvePose(blend.SourceState);
                AddPoseToBlend(blendedPose, sourcePose, 1.0f - weight);
            }

            if (blend.TargetState != null && weight > 0)
            {
                var targetPose = _poseSolver.SolvePose(blend.TargetState);
                AddPoseToBlend(blendedPose, targetPose, weight);
            }
        }

        private void ApplyAdditiveBlend(BlendedPose blendedPose, BlendOperation blend, float weight)
        {
            if (blend.SourceState != null && blend.TargetState != null)
            {
                var basePose = _poseSolver.SolvePose(blend.SourceState);
                var additivePose = _poseSolver.SolvePose(blend.TargetState);

                var additiveBlendPose = CalculateAdditivePose(basePose, additivePose, weight);
                AddPoseToBlend(blendedPose, additiveBlendPose, 1.0f);
            }
        }

        private void ApplyLayerBlend(BlendedPose blendedPose, BlendLayer layer)
        {
            if (!layer.IsEnabled || layer.Weight <= 0) return;

            var layerPose = layer.CalculateLayerPose();
            if (layerPose == null) return;

            switch (layer.BlendMode)
            {
                case LayerBlendMode.Override:
                    ApplyOverrideBlend(blendedPose, layerPose, layer.Weight);
                    break;
                case LayerBlendMode.Additive:
                    ApplyAdditiveLayerBlend(blendedPose, layerPose, layer.Weight);
                    break;
                case LayerBlendMode.Multiply:
                    ApplyMultiplyBlend(blendedPose, layerPose, layer.Weight);
                    break;
            }
        }

        private void ApplyOverrideBlend(BlendedPose blendedPose, BlendedPose layerPose, float weight)
        {
            foreach (var bonePose in layerPose.BonePoses)
            {
                blendedPose.BonePoses[bonePose.Key] = bonePose.Value;
            }
        }

        private void ApplyAdditiveLayerBlend(BlendedPose blendedPose, BlendedPose layerPose, float weight)
        {
            foreach (var bonePose in layerPose.BonePoses)
            {
                if (blendedPose.BonePoses.TryGetValue(bonePose.Key, out var existingPose))
                {
                    var additiveTransform = CalculateAdditiveTransform(existingPose.Transform, bonePose.Value.Transform, weight);
                    blendedPose.BonePoses[bonePose.Key] = new BonePose { Transform = additiveTransform };
                }
            }
        }

        private void ApplyMultiplyBlend(BlendedPose blendedPose, BlendedPose layerPose, float weight)
        {
            // Multiply blend implementation;
        }

        private void AddPoseToBlend(BlendedPose blendedPose, BonePose pose, float weight)
        {
            // Implementation for adding a pose to the blend with proper weight;
        }

        private BonePose CalculateAdditivePose(BonePose basePose, BonePose additivePose, float weight)
        {
            return new BonePose;
            {
                Transform = CalculateAdditiveTransform(basePose.Transform, additivePose.Transform, weight),
                Weight = basePose.Weight;
            };
        }

        private BoneTransform CalculateAdditiveTransform(BoneTransform baseTransform, BoneTransform additiveTransform, float weight)
        {
            return new BoneTransform;
            {
                Position = baseTransform.Position + additiveTransform.Position * weight,
                Rotation = baseTransform.Rotation * Quaternion.Slerp(Quaternion.Identity, additiveTransform.Rotation, weight),
                Scale = baseTransform.Scale * (Vector3.One + (additiveTransform.Scale - Vector3.One) * weight)
            };
        }

        private void CompleteBlend(BlendOperation blend)
        {
            ActiveBlends.Remove(blend);
            BlendHistory.Add(blend);

            OnBlendCompleted(new BlendCompletedEventArgs;
            {
                BlendOperation = blend,
                WasCompleted = true,
                Timestamp = DateTime.UtcNow;
            });

            // Keep history manageable;
            if (BlendHistory.Count > 100)
            {
                BlendHistory.RemoveAt(0);
            }
        }

        private void ApplyBlendedPose(BlendedPose blendedPose)
        {
            _animationController.ApplyPose(blendedPose);

            OnPoseBlended(new PoseBlendedEventArgs;
            {
                BlendedPose = blendedPose,
                SourceCount = blendedPose.SourceCount,
                TotalWeight = blendedPose.TotalWeight,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void UpdateBlendLayers(float deltaTime)
        {
            foreach (var layer in BlendLayers.Where(l => l.IsEnabled))
            {
                layer.Update(deltaTime);
            }
        }

        private void OnBlendStarted(BlendStartedEventArgs e)
        {
            BlendStarted?.Invoke(this, e);
        }

        private void OnBlendCompleted(BlendCompletedEventArgs e)
        {
            BlendCompleted?.Invoke(this, e);
        }

        private void OnBlendProgress(BlendProgressEventArgs e)
        {
            BlendProgress?.Invoke(this, e);
        }

        private void OnPoseBlended(PoseBlendedEventArgs e)
        {
            PoseBlended?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum BlendState;
    {
        Pending,
        Blending,
        Completed,
        Cancelled;
    }

    public enum BlendCurve;
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
        Exponential,
        SmoothStep;
    }

    public enum BlendMode;
    {
        Smooth,
        Crossfade,
        Additive,
        Multiply;
    }

    public enum LayerBlendMode;
    {
        Override,
        Additive,
        Multiply;
    }

    public interface IStateBlender;
    {
        BlendOperation StartBlend(AnimState sourceState, AnimState targetState, float blendTime = 0.2f,
                                BlendCurve blendCurve = BlendCurve.Linear);
        void Update(float deltaTime);
        BlendedPose CalculateBlendedPose();
        float GetBlendWeight(AnimState sourceState, AnimState targetState);
    }

    public interface IPoseSolver;
    {
        BonePose SolvePose(AnimState state);
        BlendedPose SolveMultiPose(List<AnimState> states, List<float> weights);
    }

    public interface IBlendOptimizer;
    {
        void Optimize(StateBlender blender);
        bool ShouldOptimize(StateBlender blender);
    }

    public class BlendLayer;
    {
        public string LayerName { get; set; }
        public float Weight { get; set; } = 1.0f;
        public LayerBlendMode BlendMode { get; set; }
        public bool IsEnabled { get; set; } = true;
        public List<BlendOperation> LayerBlends { get; } = new List<BlendOperation>();

        public void Update(float deltaTime)
        {
            foreach (var blend in LayerBlends)
            {
                blend.Update(deltaTime);
            }
        }

        public BlendedPose CalculateLayerPose()
        {
            var layerPose = new BlendedPose();

            foreach (var blend in LayerBlends.Where(b => b.BlendState == BlendState.Blending || b.BlendState == BlendState.Completed))
            {
                // Layer-specific pose calculation;
            }

            return layerPose;
        }
    }

    public class BonePose;
    {
        public BoneTransform Transform { get; set; }
        public float Weight { get; set; } = 1.0f;
        public string BoneName { get; set; }
    }

    public class BlendStatistics;
    {
        public int ActiveBlendCount { get; set; }
        public int CompletedBlendCount { get; set; }
        public int TotalLayerCount { get; set; }
        public int ActiveLayerCount { get; set; }
        public float AverageBlendTime { get; set; }
        public float TotalBlendWeight { get; set; }
    }

    public class BlendStartedEventArgs : EventArgs;
    {
        public BlendOperation BlendOperation { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendCompletedEventArgs : EventArgs;
    {
        public BlendOperation BlendOperation { get; set; }
        public bool WasCompleted { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendProgressEventArgs : EventArgs;
    {
        public BlendOperation BlendOperation { get; set; }
        public float Progress { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PoseBlendedEventArgs : EventArgs;
    {
        public BlendedPose BlendedPose { get; set; }
        public int SourceCount { get; set; }
        public float TotalWeight { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Default Implementations;

    public class DefaultPoseSolver : IPoseSolver;
    {
        public BonePose SolvePose(AnimState state)
        {
            // Default pose solving logic;
            return new BonePose;
            {
                Transform = BoneTransform.Identity,
                Weight = 1.0f;
            };
        }

        public BlendedPose SolveMultiPose(List<AnimState> states, List<float> weights)
        {
            var blendedPose = new BlendedPose();

            for (int i = 0; i < states.Count; i++)
            {
                var pose = SolvePose(states[i]);
                var weight = i < weights.Count ? weights[i] : 1.0f;
                // Add pose to blend with weight;
            }

            return blendedPose;
        }
    }

    #endregion;

    #region Extension Methods;

    public static class BlendExtensions;
    {
        public static T GetProperty<T>(this BlendOperation blend, string key, T defaultValue = default)
        {
            // Implementation for getting blend properties;
            return defaultValue;
        }

        public static void SetProperty<T>(this BlendOperation blend, string key, T value)
        {
            // Implementation for setting blend properties;
        }
    }

    #endregion;
}
