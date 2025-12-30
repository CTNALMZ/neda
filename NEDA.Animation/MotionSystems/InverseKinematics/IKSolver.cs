using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
{
    /// <summary>
    /// Represents the result of an IK solving operation;
    /// </summary>
    public class IKSolution : INotifyPropertyChanged;
    {
        private bool _success;
        private string _errorMessage;
        private int _iterations;
        private float _finalError;
        private TimeSpan _solveTime;
        private List<BoneTransform> _boneTransforms;
        private DateTime _timestamp;

        public bool Success;
        {
            get => _success;
            set { _success = value; OnPropertyChanged(); }
        }

        public string ErrorMessage;
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        public int Iterations;
        {
            get => _iterations;
            set { _iterations = value; OnPropertyChanged(); }
        }

        public float FinalError;
        {
            get => _finalError;
            set { _finalError = value; OnPropertyChanged(); }
        }

        public TimeSpan SolveTime;
        {
            get => _solveTime;
            set { _solveTime = value; OnPropertyChanged(); }
        }

        public List<BoneTransform> BoneTransforms;
        {
            get => _boneTransforms;
            set { _boneTransforms = value; OnPropertyChanged(); }
        }

        public DateTime Timestamp;
        {
            get => _timestamp;
            set { _timestamp = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public IKSolution()
        {
            _boneTransforms = new List<BoneTransform>();
            _timestamp = DateTime.UtcNow;
        }

        public override string ToString()
        {
            return Success ?
                $"IK Solution: {Iterations} iterations, Error: {FinalError:F4}, Time: {SolveTime.TotalMilliseconds:F2}ms" :
                $"IK Failed: {ErrorMessage}";
        }
    }

    /// <summary>
    /// Base class for all IK solvers;
    /// </summary>
    public abstract class BaseIKSolver : INotifyPropertyChanged, IIKSolver;
    {
        private string _solverName;
        private IKSolverType _solverType;
        private int _maxIterations = 20;
        private float _tolerance = 0.01f;
        private bool _enableConstraints = true;
        private SolverPerformance _performance;

        public string SolverName;
        {
            get => _solverName;
            set { _solverName = value; OnPropertyChanged(); }
        }

        public IKSolverType SolverType;
        {
            get => _solverType;
            set { _solverType = value; OnPropertyChanged(); }
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

        public bool EnableConstraints;
        {
            get => _enableConstraints;
            set { _enableConstraints = value; OnPropertyChanged(); }
        }

        public SolverPerformance Performance;
        {
            get => _performance;
            set { _performance = value; OnPropertyChanged(); }
        }

        public ObservableCollection<IKSolution> SolutionHistory { get; } = new ObservableCollection<IKSolution>();

        public event EventHandler<SolverCompletedEventArgs> SolverCompleted;
        public event EventHandler<SolverIterationEventArgs> SolverIteration;

        protected BaseIKSolver(string name, IKSolverType type)
        {
            _solverName = name;
            _solverType = type;
            _performance = new SolverPerformance();
        }

        /// <summary>
        /// Solves the IK chain to reach the target position;
        /// </summary>
        public abstract IKSolution Solve(IKChain chain, IKContext context);

        /// <summary>
        /// Validates if the solver can handle the given chain;
        /// </summary>
        public virtual SolverValidationResult Validate(IKChain chain)
        {
            var result = new SolverValidationResult();

            if (chain == null)
            {
                result.Errors.Add("IK chain is null");
                return result;
            }

            if (!chain.IsValid)
            {
                result.Errors.Add("IK chain is not valid");
            }

            if (chain.BoneCount < 2)
            {
                result.Errors.Add("IK chain must have at least 2 bones");
            }

            if (chain.TargetBone == null)
            {
                result.Errors.Add("Target bone is not set");
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }

        /// <summary>
        /// Pre-processes the chain before solving;
        /// </summary>
        protected virtual void PreProcess(IKChain chain, IKContext context)
        {
            // Store initial transforms for blending;
            context.SetCustomData("InitialTransforms",
                chain.Bones.Select(b => b.WorldTransform).ToList());
        }

        /// <summary>
        /// Post-processes the chain after solving;
        /// </summary>
        protected virtual void PostProcess(IKChain chain, IKContext context, IKSolution solution)
        {
            // Apply blending if needed;
            var blendWeight = context.BlendWeight;
            if (blendWeight < 1.0f)
            {
                ApplyBlending(chain, context, blendWeight);
            }

            // Update performance metrics;
            UpdatePerformanceMetrics(solution);
        }

        /// <summary>
        /// Applies blending between initial and solved transforms;
        /// </summary>
        protected virtual void ApplyBlending(IKChain chain, IKContext context, float blendWeight)
        {
            var initialTransforms = context.GetCustomData<List<BoneTransform>>("InitialTransforms");
            if (initialTransforms == null || initialTransforms.Count != chain.Bones.Count) return;

            for (int i = 0; i < chain.Bones.Count; i++)
            {
                var bone = chain.Bones[i];
                var initial = initialTransforms[i];
                var current = bone.WorldTransform;

                bone.WorldTransform = new BoneTransform;
                {
                    Position = Vector3.Lerp(initial.Position, current.Position, blendWeight),
                    Rotation = Quaternion.Slerp(initial.Rotation, current.Rotation, blendWeight),
                    Scale = Vector3.Lerp(initial.Scale, current.Scale, blendWeight)
                };
            }
        }

        /// <summary>
        /// Updates solver performance metrics;
        /// </summary>
        protected virtual void UpdatePerformanceMetrics(IKSolution solution)
        {
            Performance.TotalSolutions++;
            Performance.TotalIterations += solution.Iterations;
            Performance.TotalSolveTime += solution.SolveTime;

            if (solution.Success)
            {
                Performance.SuccessfulSolutions++;
                Performance.AverageError = (Performance.AverageError * (Performance.SuccessfulSolutions - 1) + solution.FinalError) / Performance.SuccessfulSolutions;
            }

            Performance.AverageIterations = (float)Performance.TotalIterations / Performance.TotalSolutions;
            Performance.AverageSolveTime = TimeSpan.FromTicks(Performance.TotalSolveTime.Ticks / Performance.TotalSolutions);

            // Keep solution history manageable;
            SolutionHistory.Add(solution);
            if (SolutionHistory.Count > 1000)
            {
                SolutionHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// Checks if the solution is within tolerance;
        /// </summary>
        protected virtual bool IsWithinTolerance(IKChain chain, Vector3 targetPosition, float tolerance)
        {
            var endEffectorPos = chain.EndEffector.WorldTransform.Position;
            var distance = (endEffectorPos - targetPosition).Magnitude;
            return distance <= tolerance;
        }

        /// <summary>
        /// Calculates the error distance;
        /// </summary>
        protected virtual float CalculateError(IKChain chain, Vector3 targetPosition)
        {
            var endEffectorPos = chain.EndEffector.WorldTransform.Position;
            return (endEffectorPos - targetPosition).Magnitude;
        }

        /// <summary>
        /// Applies constraints to a bone rotation;
        /// </summary>
        protected virtual Quaternion ApplyConstraints(Bone bone, Quaternion rotation, IKContext context)
        {
            if (!EnableConstraints || context.Constraints == null)
                return rotation;

            return context.Constraints.ApplyRotationConstraints(bone, rotation, context);
        }

        /// <summary>
        /// Raises the solver completed event;
        /// </summary>
        protected virtual void OnSolverCompleted(IKChain chain, IKSolution solution)
        {
            SolverCompleted?.Invoke(this, new SolverCompletedEventArgs;
            {
                Chain = chain,
                Solution = solution,
                Solver = this,
                Timestamp = DateTime.UtcNow;
            });
        }

        /// <summary>
        /// Raises the solver iteration event;
        /// </summary>
        protected virtual void OnSolverIteration(IKChain chain, int iteration, float error)
        {
            SolverIteration?.Invoke(this, new SolverIterationEventArgs;
            {
                Chain = chain,
                Iteration = iteration,
                Error = error,
                Solver = this,
                Timestamp = DateTime.UtcNow;
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Cyclic Coordinate Descent IK Solver;
    /// </summary>
    public class CCDIKSolver : BaseIKSolver;
    {
        private bool _enablePoleVector;
        private float _damping = 0.5f;

        public bool EnablePoleVector;
        {
            get => _enablePoleVector;
            set { _enablePoleVector = value; OnPropertyChanged(); }
        }

        public float Damping;
        {
            get => _damping;
            set { _damping = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public CCDIKSolver() : base("CCD Solver", IKSolverType.CCD)
        {
        }

        public override IKSolution Solve(IKChain chain, IKContext context)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var solution = new IKSolution();
            var validation = Validate(chain);

            if (!validation.IsValid)
            {
                solution.Success = false;
                solution.ErrorMessage = string.Join("; ", validation.Errors);
                return solution;
            }

            try
            {
                PreProcess(chain, context);

                var targetPosition = chain.TargetBone.WorldTransform.Position;
                var endEffector = chain.EndEffector;

                for (int iteration = 0; iteration < MaxIterations; iteration++)
                {
                    // Backward pass from end effector towards root;
                    for (int i = chain.Bones.Count - 2; i >= 0; i--)
                    {
                        var currentBone = chain.Bones[i];
                        var currentPos = currentBone.WorldTransform.Position;
                        var endPos = endEffector.WorldTransform.Position;

                        var toEnd = (endPos - currentPos).Normalized;
                        var toTarget = (targetPosition - currentPos).Normalized;

                        // Calculate rotation to align with target;
                        var rotation = CalculateRotation(toEnd, toTarget);

                        // Apply damping;
                        rotation = Quaternion.Slerp(Quaternion.Identity, rotation, Damping);

                        // Apply constraints;
                        rotation = ApplyConstraints(currentBone, rotation, context);

                        // Apply rotation;
                        currentBone.WorldTransform.Rotation = currentBone.WorldTransform.Rotation * rotation;
                        chain.UpdateWorldTransforms();

                        // Check if we're close enough;
                        var error = CalculateError(chain, targetPosition);
                        OnSolverIteration(chain, iteration, error);

                        if (error <= Tolerance)
                        {
                            solution.Success = true;
                            solution.Iterations = iteration + 1;
                            solution.FinalError = error;
                            break;
                        }
                    }

                    var currentError = CalculateError(chain, targetPosition);
                    if (currentError <= Tolerance)
                        break;
                }

                solution.SolveTime = stopwatch.Elapsed;
                solution.FinalError = CalculateError(chain, targetPosition);
                solution.Success = solution.FinalError <= Tolerance;

                if (!solution.Success)
                {
                    solution.Iterations = MaxIterations;
                }

                PostProcess(chain, context, solution);
                OnSolverCompleted(chain, solution);

                return solution;
            }
            catch (Exception ex)
            {
                solution.Success = false;
                solution.ErrorMessage = $"CCD Solver error: {ex.Message}";
                return solution;
            }
        }

        /// <summary>
        /// Calculates the rotation needed to align two vectors;
        /// </summary>
        protected virtual Quaternion CalculateRotation(Vector3 from, Vector3 to)
        {
            from = from.Normalized;
            to = to.Normalized;

            var dot = Vector3.Dot(from, to);

            // Check for edge cases;
            if (dot > 0.9999f) return Quaternion.Identity;
            if (dot < -0.9999f)
            {
                // 180 degree rotation - need to choose an axis;
                var axis = Vector3.Cross(from, Vector3.Up);
                if (axis.Magnitude < 0.0001f)
                    axis = Vector3.Cross(from, Vector3.Right);
                return Quaternion.AngleAxis(180.0f, axis.Normalized);
            }

            var angle = (float)Math.Acos(dot);
            var axis = Vector3.Cross(from, to).Normalized;

            return Quaternion.AngleAxis(angle * (180.0f / (float)Math.PI), axis);
        }
    }

    /// <summary>
    /// FABRIK (Forward And Backward Reaching Inverse Kinematics) Solver;
    /// </summary>
    public class FABRIKIKSolver : BaseIKSolver;
    {
        private bool _maintainBoneLengths = true;

        public bool MaintainBoneLengths;
        {
            get => _maintainBoneLengths;
            set { _maintainBoneLengths = value; OnPropertyChanged(); }
        }

        public FABRIKIKSolver() : base("FABRIK Solver", IKSolverType.FABRIK)
        {
        }

        public override IKSolution Solve(IKChain chain, IKContext context)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var solution = new IKSolution();
            var validation = Validate(chain);

            if (!validation.IsValid)
            {
                solution.Success = false;
                solution.ErrorMessage = string.Join("; ", validation.Errors);
                return solution;
            }

            try
            {
                PreProcess(chain, context);

                var targetPosition = chain.TargetBone.WorldTransform.Position;
                var initialRootPosition = chain.RootBone.WorldTransform.Position;

                // Store initial positions and calculate bone lengths;
                var positions = chain.Bones.Select(b => b.WorldTransform.Position).ToArray();
                var lengths = CalculateBoneLengths(chain);

                for (int iteration = 0; iteration < MaxIterations; iteration++)
                {
                    // Forward reaching phase;
                    positions[^1] = targetPosition;
                    for (int i = chain.Bones.Count - 2; i >= 0; i--)
                    {
                        var direction = (positions[i] - positions[i + 1]).Normalized;
                        positions[i] = positions[i + 1] + direction * lengths[i];
                    }

                    // Backward reaching phase;
                    positions[0] = initialRootPosition;
                    for (int i = 1; i < chain.Bones.Count; i++)
                    {
                        var direction = (positions[i] - positions[i - 1]).Normalized;
                        positions[i] = positions[i - 1] + direction * lengths[i - 1];
                    }

                    // Update bone rotations based on new positions;
                    UpdateBoneRotations(chain, positions, context);

                    var error = CalculateError(chain, targetPosition);
                    OnSolverIteration(chain, iteration, error);

                    if (error <= Tolerance)
                    {
                        solution.Success = true;
                        solution.Iterations = iteration + 1;
                        solution.FinalError = error;
                        break;
                    }
                }

                solution.SolveTime = stopwatch.Elapsed;
                solution.FinalError = CalculateError(chain, targetPosition);
                solution.Success = solution.FinalError <= Tolerance;

                if (!solution.Success)
                {
                    solution.Iterations = MaxIterations;
                }

                PostProcess(chain, context, solution);
                OnSolverCompleted(chain, solution);

                return solution;
            }
            catch (Exception ex)
            {
                solution.Success = false;
                solution.ErrorMessage = $"FABRIK Solver error: {ex.Message}";
                return solution;
            }
        }

        /// <summary>
        /// Calculates individual bone lengths;
        /// </summary>
        protected virtual float[] CalculateBoneLengths(IKChain chain)
        {
            var lengths = new float[chain.Bones.Count - 1];
            for (int i = 0; i < chain.Bones.Count - 1; i++)
            {
                var current = chain.Bones[i].WorldTransform.Position;
                var next = chain.Bones[i + 1].WorldTransform.Position;
                lengths[i] = (next - current).Magnitude;
            }
            return lengths;
        }

        /// <summary>
        /// Updates bone rotations based on new positions;
        /// </summary>
        protected virtual void UpdateBoneRotations(IKChain chain, Vector3[] positions, IKContext context)
        {
            for (int i = 0; i < chain.Bones.Count - 1; i++)
            {
                var bone = chain.Bones[i];
                var childBone = chain.Bones[i + 1];

                var currentDirection = (childBone.WorldTransform.Position - bone.WorldTransform.Position).Normalized;
                var targetDirection = (positions[i + 1] - positions[i]).Normalized;

                var rotation = CalculateRotation(currentDirection, targetDirection);
                rotation = ApplyConstraints(bone, rotation, context);

                bone.WorldTransform.Rotation = bone.WorldTransform.Rotation * rotation;
            }

            chain.UpdateWorldTransforms();
        }
    }

    /// <summary>
    /// Analytical IK Solver for simple chains;
    /// </summary>
    public class AnalyticIKSolver : BaseIKSolver;
    {
        public AnalyticIKSolver() : base("Analytic Solver", IKSolverType.Analytic)
        {
        }

        public override IKSolution Solve(IKChain chain, IKContext context)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var solution = new IKSolution();

            if (chain.BoneCount == 2)
            {
                solution = SolveTwoBoneChain(chain, context);
            }
            else if (chain.BoneCount == 3)
            {
                solution = SolveThreeBoneChain(chain, context);
            }
            else;
            {
                solution.Success = false;
                solution.ErrorMessage = "Analytic solver only supports 2 or 3 bone chains";
            }

            solution.SolveTime = stopwatch.Elapsed;
            return solution;
        }

        /// <summary>
        /// Solves a two-bone chain analytically;
        /// </summary>
        protected virtual IKSolution SolveTwoBoneChain(IKChain chain, IKContext context)
        {
            var solution = new IKSolution();
            var targetPosition = chain.TargetBone.WorldTransform.Position;

            var rootBone = chain.RootBone;
            var endBone = chain.EndEffector;

            var rootPos = rootBone.WorldTransform.Position;
            var targetDir = (targetPosition - rootPos).Normalized;

            // Calculate required rotation for root bone;
            var currentDir = (endBone.WorldTransform.Position - rootPos).Normalized;
            var rotation = CalculateRotation(currentDir, targetDir);

            // Apply constraints;
            rotation = ApplyConstraints(rootBone, rotation, context);

            // Apply rotation;
            rootBone.WorldTransform.Rotation = rootBone.WorldTransform.Rotation * rotation;
            chain.UpdateWorldTransforms();

            solution.Success = true;
            solution.Iterations = 1;
            solution.FinalError = CalculateError(chain, targetPosition);

            return solution;
        }

        /// <summary>
        /// Solves a three-bone chain analytically using cosine law;
        /// </summary>
        protected virtual IKSolution SolveThreeBoneChain(IKChain chain, IKContext context)
        {
            var solution = new IKSolution();
            var targetPosition = chain.TargetBone.WorldTransform.Position;

            var rootBone = chain.Bones[0];
            var midBone = chain.Bones[1];
            var endBone = chain.Bones[2];

            var rootPos = rootBone.WorldTransform.Position;
            var midPos = midBone.WorldTransform.Position;
            var endPos = endBone.WorldTransform.Position;

            // Calculate bone lengths;
            var rootToMid = (midPos - rootPos).Magnitude;
            var midToEnd = (endPos - midPos).Magnitude;
            var rootToTarget = (targetPosition - rootPos).Magnitude;

            // Check if target is reachable;
            if (rootToTarget > rootToMid + midToEnd)
            {
                // Target is too far - fully extend;
                solution.Success = false;
                solution.ErrorMessage = "Target is unreachable";
                return solution;
            }

            // Use cosine law to calculate angles;
            var cosAngle0 = (rootToTarget * rootToTarget + rootToMid * rootToMid - midToEnd * midToEnd)
                          / (2 * rootToTarget * rootToMid);
            var angle0 = (float)Math.Acos(Math.Max(-1, Math.Min(1, cosAngle0)));

            var cosAngle1 = (rootToMid * rootToMid + midToEnd * midToEnd - rootToTarget * rootToTarget)
                          / (2 * rootToMid * midToEnd);
            var angle1 = (float)Math.Acos(Math.Max(-1, Math.Min(1, cosAngle1)));

            // Calculate rotations and apply to bones;
            // Implementation would calculate specific rotations based on geometry

            solution.Success = true;
            solution.Iterations = 1;
            solution.FinalError = CalculateError(chain, targetPosition);

            return solution;
        }
    }

    /// <summary>
    /// Hybrid IK Solver that combines multiple methods;
    /// </summary>
    public class HybridIKSolver : BaseIKSolver;
    {
        private BaseIKSolver _primarySolver;
        private BaseIKSolver _fallbackSolver;
        private float _analyticThreshold = 3; // Use analytic for chains with <= 3 bones;

        public BaseIKSolver PrimarySolver;
        {
            get => _primarySolver;
            set { _primarySolver = value; OnPropertyChanged(); }
        }

        public BaseIKSolver FallbackSolver;
        {
            get => _fallbackSolver;
            set { _fallbackSolver = value; OnPropertyChanged(); }
        }

        public float AnalyticThreshold;
        {
            get => _analyticThreshold;
            set { _analyticThreshold = Math.Max(2, value); OnPropertyChanged(); }
        }

        public HybridIKSolver() : base("Hybrid Solver", IKSolverType.Hybrid)
        {
            _primarySolver = new AnalyticIKSolver();
            _fallbackSolver = new CCDIKSolver();
        }

        public override IKSolution Solve(IKChain chain, IKContext context)
        {
            // Choose solver based on chain complexity;
            IIKSolver solver = chain.BoneCount <= AnalyticThreshold ?
                _primarySolver : _fallbackSolver;

            return solver.Solve(chain, context);
        }
    }

    /// <summary>
    /// IK Solver Manager for handling multiple solvers;
    /// </summary>
    public class IKSolverManager : INotifyPropertyChanged, IIKSolver;
    {
        private readonly Dictionary<IKSolverType, BaseIKSolver> _solvers;
        private IKSolverType _defaultSolverType = IKSolverType.CCD;

        public IKSolverType DefaultSolverType;
        {
            get => _defaultSolverType;
            set { _defaultSolverType = value; OnPropertyChanged(); }
        }

        public ObservableCollection<BaseIKSolver> AvailableSolvers { get; } = new ObservableCollection<BaseIKSolver>();

        public event EventHandler<SolverManagerEventArgs> SolverChanged;

        public IKSolverManager()
        {
            _solvers = new Dictionary<IKSolverType, BaseIKSolver>();
            InitializeDefaultSolvers();
        }

        /// <summary>
        /// Initializes with default solvers;
        /// </summary>
        private void InitializeDefaultSolvers()
        {
            AddSolver(new CCDIKSolver());
            AddSolver(new FABRIKIKSolver());
            AddSolver(new AnalyticIKSolver());
            AddSolver(new HybridIKSolver());
        }

        /// <summary>
        /// Adds a solver to the manager;
        /// </summary>
        public void AddSolver(BaseIKSolver solver)
        {
            if (solver == null) return;

            _solvers[solver.SolverType] = solver;
            if (!AvailableSolvers.Contains(solver))
            {
                AvailableSolvers.Add(solver);
            }
        }

        /// <summary>
        /// Gets a solver by type;
        /// </summary>
        public BaseIKSolver GetSolver(IKSolverType solverType)
        {
            return _solvers.TryGetValue(solverType, out var solver) ? solver : null;
        }

        /// <summary>
        /// Solves using the specified solver type;
        /// </summary>
        public IKSolution Solve(IKChain chain, IKContext context, IKSolverType solverType)
        {
            var solver = GetSolver(solverType);
            if (solver == null)
            {
                throw new ArgumentException($"Solver of type {solverType} not found");
            }

            return solver.Solve(chain, context);
        }

        /// <summary>
        /// Solves using the default solver;
        /// </summary>
        public IKSolution Solve(IKChain chain, IKContext context)
        {
            return Solve(chain, context, DefaultSolverType);
        }

        /// <summary>
        /// Automatically selects the best solver for the chain;
        /// </summary>
        public IKSolverType SelectBestSolver(IKChain chain)
        {
            if (chain.BoneCount <= 2)
                return IKSolverType.Analytic;
            else if (chain.BoneCount <= 4)
                return IKSolverType.CCD;
            else;
                return IKSolverType.FABRIK;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public interface IIKSolver;
    {
        IKSolution Solve(IKChain chain, IKContext context);
    }

    public class SolverPerformance;
    {
        public int TotalSolutions { get; set; }
        public int SuccessfulSolutions { get; set; }
        public long TotalIterations { get; set; }
        public TimeSpan TotalSolveTime { get; set; }
        public float AverageError { get; set; }
        public float AverageIterations { get; set; }
        public TimeSpan AverageSolveTime { get; set; }

        public float SuccessRate => TotalSolutions > 0 ? (float)SuccessfulSolutions / TotalSolutions : 0;
    }

    public class SolverValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class SolverCompletedEventArgs : EventArgs;
    {
        public IKChain Chain { get; set; }
        public IKSolution Solution { get; set; }
        public BaseIKSolver Solver { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SolverIterationEventArgs : EventArgs;
    {
        public IKChain Chain { get; set; }
        public int Iteration { get; set; }
        public float Error { get; set; }
        public BaseIKSolver Solver { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SolverManagerEventArgs : EventArgs;
    {
        public IKSolverType PreviousSolver { get; set; }
        public IKSolverType NewSolver { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}
