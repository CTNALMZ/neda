using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
{
    /// <summary>
    /// Represents a single IK chain with bones, solver, and constraints;
    /// </summary>
    public class IKChain : INotifyPropertyChanged;
    {
        private string _chainId;
        private string _chainName;
        private IKSolverType _solverType;
        private IKChainState _chainState;
        private List<Bone> _bones;
        private Bone _targetBone;
        private Bone _poleVectorBone;
        private float _chainLength;
        private int _maxIterations = 20;
        private float _tolerance = 0.01f;
        private bool _isEnabled = true;
        private float _influence = 1.0f;
        private IKConstraints _constraints;

        public string ChainId;
        {
            get => _chainId;
            set { _chainId = value; OnPropertyChanged(); }
        }

        public string ChainName;
        {
            get => _chainName;
            set { _chainName = value; OnPropertyChanged(); }
        }

        public IKSolverType SolverType;
        {
            get => _solverType;
            set { _solverType = value; OnPropertyChanged(); }
        }

        public IKChainState ChainState;
        {
            get => _chainState;
            set { _chainState = value; OnPropertyChanged(); }
        }

        public List<Bone> Bones;
        {
            get => _bones;
            set { _bones = value; OnPropertyChanged(); RecalculateChainProperties(); }
        }

        public Bone TargetBone;
        {
            get => _targetBone;
            set { _targetBone = value; OnPropertyChanged(); }
        }

        public Bone PoleVectorBone;
        {
            get => _poleVectorBone;
            set { _poleVectorBone = value; OnPropertyChanged(); }
        }

        public float ChainLength;
        {
            get => _chainLength;
            private set { _chainLength = value; OnPropertyChanged(); }
        }

        public int MaxIterations;
        {
            get => _maxIterations;
            set { _maxIterations = Math.Max(1, value); OnPropertyChanged(); }
        }

        public float Tolerance;
        {
            get => _tolerance;
            set { _tolerance = Math.Max(0.001f, value); OnPropertyChanged(); }
        }

        public bool IsEnabled;
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public float Influence;
        {
            get => _influence;
            set { _influence = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public IKConstraints Constraints;
        {
            get => _constraints;
            set { _constraints = value; OnPropertyChanged(); }
        }

        public Bone RootBone => _bones?.FirstOrDefault();
        public Bone EndEffector => _bones?.LastOrDefault();
        public int BoneCount => _bones?.Count ?? 0;
        public bool IsValid => _bones != null && _bones.Count >= 2 && _targetBone != null;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public IKChain()
        {
            _chainId = Guid.NewGuid().ToString();
            _bones = new List<Bone>();
            _constraints = new IKConstraints();
            _chainState = IKChainState.Stopped;
        }

        public IKChain(string name, List<Bone> bones, Bone targetBone, IKSolverType solverType = IKSolverType.CCD) : this()
        {
            _chainName = name;
            _bones = bones;
            _targetBone = targetBone;
            _solverType = solverType;
            RecalculateChainProperties();
        }

        /// <summary>
        /// Solves the IK chain to reach the target position;
        /// </summary>
        public IKSolution Solve(IKContext context = null)
        {
            if (!IsValid || !IsEnabled || Influence <= 0)
                return new IKSolution { Success = false, Error = "IK chain is not valid or enabled" };

            try
            {
                ChainState = IKChainState.Solving;

                context ??= CreateDefaultContext();
                var solution = SolverType switch;
                {
                    IKSolverType.CCD => SolveCCD(context),
                    IKSolverType.FABRIK => SolveFABRIK(context),
                    IKSolverType.Analytic => SolveAnalytic(context),
                    IKSolverType.Hybrid => SolveHybrid(context),
                    _ => SolveCCD(context)
                };

                ChainState = solution.Success ? IKChainState.Solved : IKChainState.Error;
                return solution;
            }
            catch (Exception ex)
            {
                ChainState = IKChainState.Error;
                return new IKSolution { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Cyclic Coordinate Descent solver;
        /// </summary>
        private IKSolution SolveCCD(IKContext context)
        {
            var solution = new IKSolution();
            var endEffector = EndEffector;
            var targetPosition = TargetBone.WorldTransform.Position;

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                // Work backwards from end effector towards root;
                for (int i = Bones.Count - 2; i >= 0; i--)
                {
                    var currentBone = Bones[i];
                    var currentPos = currentBone.WorldTransform.Position;
                    var endPos = endEffector.WorldTransform.Position;

                    var toEnd = (endPos - currentPos).Normalized;
                    var toTarget = (targetPosition - currentPos).Normalized;

                    // Calculate rotation needed to align with target;
                    var rotation = Quaternion.FromToRotation(toEnd, toTarget);

                    // Apply constraints;
                    rotation = Constraints.ApplyRotationConstraints(currentBone, rotation, context);

                    // Update bone rotation;
                    var newRotation = currentBone.WorldTransform.Rotation * rotation;
                    currentBone.WorldTransform.Rotation = newRotation;

                    // Update world transforms;
                    UpdateWorldTransforms();

                    // Check if we're close enough;
                    var distance = (endEffector.WorldTransform.Position - targetPosition).Magnitude;
                    if (distance <= Tolerance)
                    {
                        solution.Success = true;
                        solution.Iterations = iteration + 1;
                        solution.FinalError = distance;
                        return solution;
                    }
                }
            }

            // Calculate final error;
            var finalDistance = (endEffector.WorldTransform.Position - targetPosition).Magnitude;
            solution.Success = finalDistance <= Tolerance;
            solution.Iterations = MaxIterations;
            solution.FinalError = finalDistance;

            return solution;
        }

        /// <summary>
        /// FABRIK (Forward And Backward Reaching Inverse Kinematics) solver;
        /// </summary>
        private IKSolution SolveFABRIK(IKContext context)
        {
            var solution = new IKSolution();
            var targetPosition = TargetBone.WorldTransform.Position;
            var initialRootPosition = RootBone.WorldTransform.Position;

            // Store initial positions;
            var positions = Bones.Select(b => b.WorldTransform.Position).ToArray();
            var lengths = CalculateBoneLengths();

            // Forward reaching;
            positions[^1] = targetPosition;
            for (int i = Bones.Count - 2; i >= 0; i--)
            {
                var direction = (positions[i] - positions[i + 1]).Normalized;
                positions[i] = positions[i + 1] + direction * lengths[i];
            }

            // Backward reaching;
            positions[0] = initialRootPosition;
            for (int i = 1; i < Bones.Count; i++)
            {
                var direction = (positions[i] - positions[i - 1]).Normalized;
                positions[i] = positions[i - 1] + direction * lengths[i - 1];
            }

            // Apply new positions with constraints;
            for (int i = 0; i < Bones.Count; i++)
            {
                if (i > 0) // Skip root bone for rotation calculation;
                {
                    var parentPos = positions[i - 1];
                    var currentPos = positions[i];
                    var direction = (currentPos - parentPos).Normalized;

                    // Calculate rotation to align with new direction;
                    var parentBone = Bones[i - 1];
                    var currentDirection = parentBone.WorldTransform.Rotation * Vector3.Forward;
                    var rotation = Quaternion.FromToRotation(currentDirection, direction);

                    // Apply constraints;
                    rotation = Constraints.ApplyRotationConstraints(parentBone, rotation, context);

                    parentBone.WorldTransform.Rotation = parentBone.WorldTransform.Rotation * rotation;
                }
            }

            UpdateWorldTransforms();

            var finalDistance = (EndEffector.WorldTransform.Position - targetPosition).Magnitude;
            solution.Success = finalDistance <= Tolerance;
            solution.Iterations = 1; // FABRIK typically converges in one full iteration;
            solution.FinalError = finalDistance;

            return solution;
        }

        /// <summary>
        /// Analytic solver for simple chains (2 or 3 bones)
        /// </summary>
        private IKSolution SolveAnalytic(IKContext context)
        {
            var solution = new IKSolution();

            if (Bones.Count == 2)
            {
                solution = SolveTwoBoneAnalytic(context);
            }
            else if (Bones.Count == 3)
            {
                solution = SolveThreeBoneAnalytic(context);
            }
            else;
            {
                solution.Success = false;
                solution.Error = "Analytic solver only supports 2 or 3 bone chains";
            }

            return solution;
        }

        /// <summary>
        /// Two-bone analytic IK solution;
        /// </summary>
        private IKSolution SolveTwoBoneAnalytic(IKContext context)
        {
            var solution = new IKSolution();
            var targetPosition = TargetBone.WorldTransform.Position;
            var rootBone = RootBone;
            var middleBone = Bones[1];
            var endBone = EndEffector;

            var rootPos = rootBone.WorldTransform.Position;
            var targetDir = (targetPosition - rootPos).Normalized;

            // Calculate required rotations;
            var rootToEnd = (endBone.WorldTransform.Position - rootPos).Normalized;
            var rootRotation = Quaternion.FromToRotation(rootToEnd, targetDir);

            // Apply constraints;
            rootRotation = Constraints.ApplyRotationConstraints(rootBone, rootRotation, context);
            rootBone.WorldTransform.Rotation = rootBone.WorldTransform.Rotation * rootRotation;

            UpdateWorldTransforms();

            var finalDistance = (endBone.WorldTransform.Position - targetPosition).Magnitude;
            solution.Success = finalDistance <= Tolerance;
            solution.Iterations = 1;
            solution.FinalError = finalDistance;

            return solution;
        }

        /// <summary>
        /// Three-bone analytic IK solution;
        /// </summary>
        private IKSolution SolveThreeBoneAnalytic(IKContext context)
        {
            // Implementation for three-bone analytic solution;
            // This would involve solving the cosine law for the triangle formed by the bones;
            return new IKSolution { Success = false, Error = "Three-bone analytic solver not implemented" };
        }

        /// <summary>
        /// Hybrid solver combining multiple methods;
        /// </summary>
        private IKSolution SolveHybrid(IKContext context)
        {
            // Try analytic first for efficiency;
            if (Bones.Count <= 3)
            {
                var analyticSolution = SolveAnalytic(context);
                if (analyticSolution.Success) return analyticSolution;
            }

            // Fall back to CCD for complex chains;
            return SolveCCD(context);
        }

        /// <summary>
        /// Updates world transforms for all bones in the chain;
        /// </summary>
        public void UpdateWorldTransforms()
        {
            if (RootBone == null) return;

            RootBone.CalculateWorldTransform();
            foreach (var bone in Bones.Skip(1))
            {
                bone.CalculateWorldTransform();
            }
        }

        /// <summary>
        /// Recalculates chain properties like length and bone relationships;
        /// </summary>
        private void RecalculateChainProperties()
        {
            if (_bones == null || _bones.Count < 2)
            {
                ChainLength = 0;
                return;
            }

            // Calculate total chain length;
            float length = 0;
            for (int i = 0; i < _bones.Count - 1; i++)
            {
                var current = _bones[i].WorldTransform.Position;
                var next = _bones[i + 1].WorldTransform.Position;
                length += (next - current).Magnitude;
            }
            ChainLength = length;
        }

        /// <summary>
        /// Calculates individual bone lengths;
        /// </summary>
        private float[] CalculateBoneLengths()
        {
            var lengths = new float[Bones.Count - 1];
            for (int i = 0; i < Bones.Count - 1; i++)
            {
                var current = Bones[i].WorldTransform.Position;
                var next = Bones[i + 1].WorldTransform.Position;
                lengths[i] = (next - current).Magnitude;
            }
            return lengths;
        }

        /// <summary>
        /// Creates a default IK context;
        /// </summary>
        private IKContext CreateDefaultContext()
        {
            return new IKContext;
            {
                DeltaTime = 0.016f, // ~60 FPS;
                UsePoleVector = PoleVectorBone != null,
                PoleVectorPosition = PoleVectorBone?.WorldTransform.Position ?? Vector3.Zero,
                ApplyConstraints = true,
                BlendWeight = Influence;
            };
        }

        /// <summary>
        /// Gets the current end effector position;
        /// </summary>
        public Vector3 GetEndEffectorPosition()
        {
            return EndEffector?.WorldTransform.Position ?? Vector3.Zero;
        }

        /// <summary>
        /// Gets the current target position;
        /// </summary>
        public Vector3 GetTargetPosition()
        {
            return TargetBone?.WorldTransform.Position ?? Vector3.Zero;
        }

        /// <summary>
        /// Checks if the target is reachable;
        /// </summary>
        public bool IsTargetReachable()
        {
            if (!IsValid) return false;

            var rootPos = RootBone.WorldTransform.Position;
            var targetPos = GetTargetPosition();
            var distance = (targetPos - rootPos).Magnitude;

            return distance <= ChainLength;
        }

        /// <summary>
        /// Resets the chain to its initial state;
        /// </summary>
        public void Reset()
        {
            // Implementation would store initial transforms and restore them;
            ChainState = IKChainState.Stopped;
        }

        public override string ToString() => $"{ChainName} ({SolverType}, {BoneCount} bones)";
    }

    /// <summary>
    /// Represents IK constraints for limiting bone movement;
    /// </summary>
    public class IKConstraints : INotifyPropertyChanged;
    {
        private bool _enableRotationLimits = true;
        private bool _enableTwistLimits = true;
        private bool _enableSwingLimits = true;
        private float _maxTwistAngle = 45.0f;
        private float _maxSwingAngle = 90.0f;
        private bool _maintainBoneLength = true;
        private bool _respectHierarchy = true;

        public bool EnableRotationLimits;
        {
            get => _enableRotationLimits;
            set { _enableRotationLimits = value; OnPropertyChanged(); }
        }

        public bool EnableTwistLimits;
        {
            get => _enableTwistLimits;
            set { _enableTwistLimits = value; OnPropertyChanged(); }
        }

        public bool EnableSwingLimits;
        {
            get => _enableSwingLimits;
            set { _enableSwingLimits = value; OnPropertyChanged(); }
        }

        public float MaxTwistAngle;
        {
            get => _maxTwistAngle;
            set { _maxTwistAngle = Math.Max(0, Math.Min(180, value)); OnPropertyChanged(); }
        }

        public float MaxSwingAngle;
        {
            get => _maxSwingAngle;
            set { _maxSwingAngle = Math.Max(0, Math.Min(180, value)); OnPropertyChanged(); }
        }

        public bool MaintainBoneLength;
        {
            get => _maintainBoneLength;
            set { _maintainBoneLength = value; OnPropertyChanged(); }
        }

        public bool RespectHierarchy;
        {
            get => _respectHierarchy;
            set { _respectHierarchy = value; OnPropertyChanged(); }
        }

        public ObservableCollection<BoneConstraint> PerBoneConstraints { get; } = new ObservableCollection<BoneConstraint>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Applies rotation constraints to a bone;
        /// </summary>
        public Quaternion ApplyRotationConstraints(Bone bone, Quaternion rotation, IKContext context)
        {
            if (!EnableRotationLimits) return rotation;

            var constrainedRotation = rotation;

            // Apply twist limits;
            if (EnableTwistLimits)
            {
                constrainedRotation = ApplyTwistLimits(bone, constrainedRotation, context);
            }

            // Apply swing limits;
            if (EnableSwingLimits)
            {
                constrainedRotation = ApplySwingLimits(bone, constrainedRotation, context);
            }

            // Apply per-bone constraints;
            foreach (var constraint in PerBoneConstraints.Where(c => c.IsEnabled && c.TargetBoneName == bone.Name))
            {
                // Apply individual bone constraints;
                constrainedRotation = ApplyBoneConstraint(bone, constrainedRotation, constraint, context);
            }

            return constrainedRotation;
        }

        private Quaternion ApplyTwistLimits(Bone bone, Quaternion rotation, IKContext context)
        {
            // Extract twist component and clamp;
            var twistAngle = rotation.GetTwistAngle(Vector3.Forward);
            var clampedTwist = Math.Max(-MaxTwistAngle, Math.Min(MaxTwistAngle, twistAngle));

            if (Math.Abs(twistAngle - clampedTwist) > 0.001f)
            {
                var twistDelta = clampedTwist - twistAngle;
                var twistCorrection = Quaternion.AngleAxis(twistDelta, Vector3.Forward);
                rotation = twistCorrection * rotation;
            }

            return rotation;
        }

        private Quaternion ApplySwingLimits(Bone bone, Quaternion rotation, IKContext context)
        {
            // Extract swing component and clamp;
            var swing = rotation.GetSwing(Vector3.Forward);
            var swingAngle = Quaternion.Angle(Quaternion.Identity, swing);

            if (swingAngle > MaxSwingAngle)
            {
                var t = MaxSwingAngle / swingAngle;
                rotation = Quaternion.Slerp(Quaternion.Identity, rotation, t);
            }

            return rotation;
        }

        private Quaternion ApplyBoneConstraint(Bone bone, Quaternion rotation, BoneConstraint constraint, IKContext context)
        {
            // Apply individual bone constraints;
            // This would use the constraint system to limit the rotation;
            return rotation;
        }
    }

    /// <summary>
    /// Main IK chain system for managing multiple IK chains;
    /// </summary>
    public class IKChainSystem : INotifyPropertyChanged, IIKSystem;
    {
        private readonly IIKSolver _solver;
        private readonly IIKValidator _validator;

        private Skeleton _targetSkeleton;
        private bool _isAutoUpdateEnabled = true;
        private bool _isMultiChainEnabled = true;
        private float _globalInfluence = 1.0f;

        public Skeleton TargetSkeleton;
        {
            get => _targetSkeleton;
            set { _targetSkeleton = value; OnPropertyChanged(); OnSkeletonChanged(); }
        }

        public bool IsAutoUpdateEnabled;
        {
            get => _isAutoUpdateEnabled;
            set { _isAutoUpdateEnabled = value; OnPropertyChanged(); }
        }

        public bool IsMultiChainEnabled;
        {
            get => _isMultiChainEnabled;
            set { _isMultiChainEnabled = value; OnPropertyChanged(); }
        }

        public float GlobalInfluence;
        {
            get => _globalInfluence;
            set { _globalInfluence = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public ObservableCollection<IKChain> Chains { get; } = new ObservableCollection<IKChain>();
        public ObservableCollection<IKSolution> SolutionHistory { get; } = new ObservableCollection<IKSolution>();

        public event EventHandler<IKChainSolvedEventArgs> ChainSolved;
        public event EventHandler<IKSystemUpdatedEventArgs> SystemUpdated;

        public IKChainSystem(IIKSolver solver = null, IIKValidator validator = null)
        {
            _solver = solver ?? new DefaultIKSolver();
            _validator = validator;
        }

        /// <summary>
        /// Creates a new IK chain;
        /// </summary>
        public IKChain CreateChain(string chainName, List<string> boneNames, string targetBoneName,
                                 IKSolverType solverType = IKSolverType.CCD)
        {
            if (TargetSkeleton == null)
                throw new InvalidOperationException("Target skeleton must be set before creating IK chains");

            var bones = boneNames.Select(name => TargetSkeleton.GetBone(name))
                                .Where(bone => bone != null).ToList();

            var targetBone = TargetSkeleton.GetBone(targetBoneName);
            if (targetBone == null)
                throw new ArgumentException($"Target bone '{targetBoneName}' not found");

            if (bones.Count < 2)
                throw new ArgumentException("IK chain requires at least 2 bones");

            var chain = new IKChain(chainName, bones, targetBone, solverType);
            Chains.Add(chain);

            OnSystemUpdated(new IKSystemUpdatedEventArgs;
            {
                Operation = IKSystemOperation.ChainAdded,
                Chain = chain,
                Timestamp = DateTime.UtcNow;
            });

            return chain;
        }

        /// <summary>
        /// Creates a limb chain (shoulder/hip to hand/foot)
        /// </summary>
        public IKChain CreateLimbChain(string chainName, string rootBoneName, string midBoneName, string endBoneName,
                                     string targetBoneName, IKSolverType solverType = IKSolverType.CCD)
        {
            var boneNames = new List<string> { rootBoneName, midBoneName, endBoneName };
            return CreateChain(chainName, boneNames, targetBoneName, solverType);
        }

        /// <summary>
        /// Updates all IK chains in the system;
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!IsAutoUpdateEnabled || TargetSkeleton == null) return;

            var context = new IKContext;
            {
                DeltaTime = deltaTime,
                GlobalInfluence = GlobalInfluence,
                Skeleton = TargetSkeleton;
            };

            foreach (var chain in Chains.Where(c => c.IsEnabled && c.Influence > 0))
            {
                var solution = chain.Solve(context);
                SolutionHistory.Add(solution);

                OnChainSolved(new IKChainSolvedEventArgs;
                {
                    Chain = chain,
                    Solution = solution,
                    Timestamp = DateTime.UtcNow;
                });

                // Keep history manageable;
                if (SolutionHistory.Count > 100)
                {
                    SolutionHistory.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Solves a specific IK chain;
        /// </summary>
        public IKSolution SolveChain(string chainName, IKContext context = null)
        {
            var chain = Chains.FirstOrDefault(c => c.ChainName == chainName);
            if (chain == null)
                throw new ArgumentException($"IK chain '{chainName}' not found");

            var solution = chain.Solve(context);
            SolutionHistory.Add(solution);

            OnChainSolved(new IKChainSolvedEventArgs;
            {
                Chain = chain,
                Solution = solution,
                Timestamp = DateTime.UtcNow;
            });

            return solution;
        }

        /// <summary>
        /// Removes an IK chain from the system;
        /// </summary>
        public bool RemoveChain(string chainName)
        {
            var chain = Chains.FirstOrDefault(c => c.ChainName == chainName);
            if (chain == null) return false;

            Chains.Remove(chain);

            OnSystemUpdated(new IKSystemUpdatedEventArgs;
            {
                Operation = IKSystemOperation.ChainRemoved,
                Chain = chain,
                Timestamp = DateTime.UtcNow;
            });

            return true;
        }

        /// <summary>
        /// Validates all IK chains in the system;
        /// </summary>
        public IKValidationResult ValidateSystem()
        {
            if (_validator == null)
                return new IKValidationResult { IsValid = true };

            var result = _validator.Validate(Chains.ToList(), TargetSkeleton);

            OnSystemUpdated(new IKSystemUpdatedEventArgs;
            {
                Operation = IKSystemOperation.SystemValidated,
                ValidationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Gets IK system statistics;
        /// </summary>
        public IKStatistics GetStatistics()
        {
            return new IKStatistics;
            {
                TotalChains = Chains.Count,
                ActiveChains = Chains.Count(c => c.IsEnabled && c.Influence > 0),
                AverageChainLength = Chains.Any() ? Chains.Average(c => c.ChainLength) : 0,
                AverageBonesPerChain = Chains.Any() ? Chains.Average(c => c.BoneCount) : 0,
                SuccessRate = SolutionHistory.Any() ?
                    SolutionHistory.Count(s => s.Success) / (float)SolutionHistory.Count : 0,
                SolverDistribution = Chains.GroupBy(c => c.SolverType)
                                         .ToDictionary(g => g.Key, g => g.Count())
            };
        }

        /// <summary>
        /// Creates mirror chains for symmetric setups;
        /// </summary>
        public void CreateMirrorChains(string leftSuffix = "Left", string rightSuffix = "Right")
        {
            var leftChains = Chains.Where(c => c.ChainName.EndsWith(leftSuffix)).ToList();

            foreach (var leftChain in leftChains)
            {
                var rightChainName = leftChain.ChainName.Replace(leftSuffix, rightSuffix);

                // Create mirror bone names;
                var rightBoneNames = leftChain.Bones.Select(b =>
                    b.Name.Replace(leftSuffix, rightSuffix)).ToList();

                var rightTargetName = leftChain.TargetBone.Name.Replace(leftSuffix, rightSuffix);

                CreateChain(rightChainName, rightBoneNames, rightTargetName, leftChain.SolverType);
            }
        }

        private void OnSkeletonChanged()
        {
            // Update all chains when skeleton changes;
            foreach (var chain in Chains)
            {
                chain.RecalculateChainProperties();
            }
        }

        private void OnChainSolved(IKChainSolvedEventArgs e)
        {
            ChainSolved?.Invoke(this, e);
        }

        private void OnSystemUpdated(IKSystemUpdatedEventArgs e)
        {
            SystemUpdated?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum IKSolverType;
    {
        CCD,        // Cyclic Coordinate Descent;
        FABRIK,     // Forward And Backward Reaching IK;
        Analytic,   // Analytical solution;
        Hybrid      // Combination of methods;
    }

    public enum IKChainState;
    {
        Stopped,
        Solving,
        Solved,
        Error;
    }

    public enum IKSystemOperation;
    {
        ChainAdded,
        ChainRemoved,
        SystemValidated;
    }

    public interface IIKSystem;
    {
        void Update(float deltaTime);
        IKChain CreateChain(string chainName, List<string> boneNames, string targetBoneName,
                          IKSolverType solverType = IKSolverType.CCD);
        IKSolution SolveChain(string chainName, IKContext context = null);
    }

    public interface IIKSolver;
    {
        IKSolution Solve(IKChain chain, IKContext context);
    }

    public interface IIKValidator;
    {
        IKValidationResult Validate(List<IKChain> chains, Skeleton skeleton);
    }

    public class IKSolution;
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Iterations { get; set; }
        public float FinalError { get; set; }
        public TimeSpan SolveTime { get; set; }
        public List<BoneTransform> BoneTransforms { get; set; } = new List<BoneTransform>();
    }

    public class IKContext;
    {
        public float DeltaTime { get; set; }
        public float GlobalInfluence { get; set; } = 1.0f;
        public bool UsePoleVector { get; set; }
        public Vector3 PoleVectorPosition { get; set; }
        public bool ApplyConstraints { get; set; } = true;
        public float BlendWeight { get; set; } = 1.0f;
        public Skeleton Skeleton { get; set; }
        public Dictionary<string, object> CustomData { get; } = new Dictionary<string, object>();

        public T GetCustomData<T>(string key, T defaultValue = default)
        {
            return CustomData.TryGetValue(key, out object value) && value is T typedValue ? typedValue : defaultValue;
        }

        public void SetCustomData<T>(string key, T value)
        {
            CustomData[key] = value;
        }
    }

    public class IKValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<IKChain> ProblematicChains { get; set; } = new List<IKChain>();
    }

    public class IKStatistics;
    {
        public int TotalChains { get; set; }
        public int ActiveChains { get; set; }
        public double AverageChainLength { get; set; }
        public double AverageBonesPerChain { get; set; }
        public float SuccessRate { get; set; }
        public Dictionary<IKSolverType, int> SolverDistribution { get; set; }
    }

    public class IKChainSolvedEventArgs : EventArgs;
    {
        public IKChain Chain { get; set; }
        public IKSolution Solution { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class IKSystemUpdatedEventArgs : EventArgs;
    {
        public IKSystemOperation Operation { get; set; }
        public IKChain Chain { get; set; }
        public IKValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Default Implementations;

    public class DefaultIKSolver : IIKSolver;
    {
        public IKSolution Solve(IKChain chain, IKContext context)
        {
            return chain.Solve(context);
        }
    }

    #endregion;

    #region Math Extensions;

    public static class QuaternionExtensions;
    {
        public static float GetTwistAngle(this Quaternion quaternion, Vector3 axis)
        {
            // Extract twist component around the specified axis;
            var projected = new Vector3(
                Vector3.Dot(quaternion.XYZ, axis) * axis.X,
                Vector3.Dot(quaternion.XYZ, axis) * axis.Y,
                Vector3.Dot(quaternion.XYZ, axis) * axis.Z;
            );

            var twist = new Quaternion(projected.X, projected.Y, projected.Z, quaternion.W).Normalized;
            return (float)Math.Acos(Math.Max(-1, Math.Min(1, twist.W))) * 2.0f * (180.0f / (float)Math.PI);
        }

        public static Quaternion GetSwing(this Quaternion quaternion, Vector3 axis)
        {
            // Remove twist component to get swing;
            var twistAngle = quaternion.GetTwistAngle(axis);
            var twist = Quaternion.AngleAxis(twistAngle, axis);
            return quaternion * Quaternion.Inverse(twist);
        }

        public static Quaternion FromToRotation(Vector3 from, Vector3 to)
        {
            from = from.Normalized;
            to = to.Normalized;

            var dot = Vector3.Dot(from, to);
            if (Math.Abs(dot - 1.0f) < 0.0001f) return Quaternion.Identity;
            if (Math.Abs(dot + 1.0f) < 0.0001f) return Quaternion.AngleAxis(180.0f, Vector3.Up);

            var angle = (float)Math.Acos(dot);
            var axis = Vector3.Cross(from, to).Normalized;

            return Quaternion.AngleAxis(angle * (180.0f / (float)Math.PI), axis);
        }
    }

    #endregion;
}
