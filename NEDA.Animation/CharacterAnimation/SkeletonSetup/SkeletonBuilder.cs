using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
{
    /// <summary>
    /// Represents a skeleton building operation with parameters and results;
    /// </summary>
    public class SkeletonBuildOperation : INotifyPropertyChanged;
    {
        private string _operationId;
        private BuildOperationType _operationType;
        private SkeletonBuildState _state;
        private float _progress;
        private string _statusMessage;
        private DateTime _startTime;
        private DateTime _endTime;
        private List<SkeletonBuildError> _errors;

        public string OperationId;
        {
            get => _operationId;
            set { _operationId = value; OnPropertyChanged(); }
        }

        public BuildOperationType OperationType;
        {
            get => _operationType;
            set { _operationType = value; OnPropertyChanged(); }
        }

        public SkeletonBuildState State;
        {
            get => _state;
            set { _state = value; OnPropertyChanged(); }
        }

        public float Progress;
        {
            get => _progress;
            set { _progress = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public string StatusMessage;
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public DateTime StartTime;
        {
            get => _startTime;
            set { _startTime = value; OnPropertyChanged(); }
        }

        public DateTime EndTime;
        {
            get => _endTime;
            set { _endTime = value; OnPropertyChanged(); }
        }

        public List<SkeletonBuildError> Errors;
        {
            get => _errors;
            set { _errors = value; OnPropertyChanged(); }
        }

        public TimeSpan Duration => EndTime - StartTime;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public SkeletonBuildOperation()
        {
            _operationId = Guid.NewGuid().ToString();
            _errors = new List<SkeletonBuildError>();
            _state = SkeletonBuildState.Pending;
        }
    }

    /// <summary>
    /// Main skeleton builder for creating and configuring character skeletons;
    /// </summary>
    public class SkeletonBuilder : INotifyPropertyChanged, ISkeletonBuilder;
    {
        private readonly ISkeletonValidator _validator;
        private readonly ISkeletonOptimizer _optimizer;
        private readonly ISkeletonTemplateProvider _templateProvider;

        private Skeleton _currentSkeleton;
        private SkeletonBuildProfile _currentProfile;
        private SkeletonBuildMode _buildMode;
        private bool _isAutoValidateEnabled = true;
        private bool _isAutoOptimizeEnabled = true;

        public Skeleton CurrentSkeleton;
        {
            get => _currentSkeleton;
            private set { _currentSkeleton = value; OnPropertyChanged(); }
        }

        public SkeletonBuildProfile CurrentProfile;
        {
            get => _currentProfile;
            set { _currentProfile = value; OnPropertyChanged(); OnProfileChanged(); }
        }

        public SkeletonBuildMode BuildMode;
        {
            get => _buildMode;
            set { _buildMode = value; OnPropertyChanged(); }
        }

        public bool IsAutoValidateEnabled;
        {
            get => _isAutoValidateEnabled;
            set { _isAutoValidateEnabled = value; OnPropertyChanged(); }
        }

        public bool IsAutoOptimizeEnabled;
        {
            get => _isAutoOptimizeEnabled;
            set { _isAutoOptimizeEnabled = value; OnPropertyChanged(); }
        }

        public ObservableCollection<SkeletonBuildOperation> BuildHistory { get; } = new ObservableCollection<SkeletonBuildOperation>();
        public ObservableCollection<SkeletonTemplate> AvailableTemplates { get; } = new ObservableCollection<SkeletonTemplate>();
        public ObservableCollection<SkeletonBuildError> RecentErrors { get; } = new ObservableCollection<SkeletonBuildError>();

        public event EventHandler<SkeletonBuildEventArgs> BuildStarted;
        public event EventHandler<SkeletonBuildEventArgs> BuildCompleted;
        public event EventHandler<SkeletonBuildEventArgs> BuildProgressChanged;
        public event EventHandler<SkeletonValidationEventArgs> SkeletonValidated;
        public event EventHandler<SkeletonOptimizationEventArgs> SkeletonOptimized;

        public SkeletonBuilder(ISkeletonValidator validator = null, ISkeletonOptimizer optimizer = null,
                            ISkeletonTemplateProvider templateProvider = null)
        {
            _validator = validator ?? new DefaultSkeletonValidator();
            _optimizer = optimizer;
            _templateProvider = templateProvider;
            _buildMode = SkeletonBuildMode.Standard;

            InitializeTemplates();
        }

        /// <summary>
        /// Creates a new skeleton from scratch;
        /// </summary>
        public SkeletonBuildResult CreateNewSkeleton(string skeletonName, SkeletonType skeletonType)
        {
            var operation = StartBuildOperation(BuildOperationType.Create, $"Creating new {skeletonType} skeleton: {skeletonName}");

            try
            {
                CurrentSkeleton = new Skeleton;
                {
                    Name = skeletonName,
                    SkeletonType = skeletonType;
                };

                // Create root bone;
                var rootBone = CreateBone("Root", BoneType.Root, null, Vector3.Zero);
                CurrentSkeleton.RootBone = rootBone;
                CurrentSkeleton.Bones.Add(rootBone);

                var result = CompleteBuildOperation(operation, true, "Skeleton created successfully");

                if (_isAutoValidateEnabled)
                {
                    ValidateSkeleton();
                }

                return result;
            }
            catch (Exception ex)
            {
                return FailBuildOperation(operation, "Failed to create skeleton", ex);
            }
        }

        /// <summary>
        /// Creates a skeleton from a template;
        /// </summary>
        public SkeletonBuildResult CreateSkeletonFromTemplate(string templateName, string skeletonName = null)
        {
            var template = AvailableTemplates.FirstOrDefault(t => t.Name == templateName);
            if (template == null)
                throw new ArgumentException($"Template '{templateName}' not found");

            var operation = StartBuildOperation(BuildOperationType.Template, $"Creating skeleton from template: {templateName}");

            try
            {
                var skeleton = template.CreateSkeleton();
                skeleton.Name = skeletonName ?? $"{templateName}_Skeleton";
                CurrentSkeleton = skeleton;

                var result = CompleteBuildOperation(operation, true, $"Skeleton created from template: {templateName}");

                if (_isAutoValidateEnabled)
                {
                    ValidateSkeleton();
                }

                return result;
            }
            catch (Exception ex)
            {
                return FailBuildOperation(operation, "Failed to create skeleton from template", ex);
            }
        }

        /// <summary>
        /// Creates a humanoid skeleton with standard bone structure;
        /// </summary>
        public SkeletonBuildResult CreateHumanoidSkeleton(string skeletonName, HumanoidSkeletonPreset preset = HumanoidSkeletonPreset.Standard)
        {
            var operation = StartBuildOperation(BuildOperationType.Humanoid, $"Creating humanoid skeleton: {skeletonName}");

            try
            {
                CurrentSkeleton = new Skeleton;
                {
                    Name = skeletonName,
                    SkeletonType = SkeletonType.Humanoid;
                };

                // Create core hierarchy;
                var root = CreateBone("Root", BoneType.Root, null, Vector3.Zero);
                var hips = CreateBone("Hips", BoneType.Hip, root, new Vector3(0, 1.0f, 0));
                var spine = CreateBone("Spine", BoneType.Spine, hips, new Vector3(0, 0.2f, 0));
                var chest = CreateBone("Chest", BoneType.Chest, spine, new Vector3(0, 0.2f, 0));
                var neck = CreateBone("Neck", BoneType.Neck, chest, new Vector3(0, 0.15f, 0));
                var head = CreateBone("Head", BoneType.Head, neck, new Vector3(0, 0.1f, 0));

                // Create arms;
                var leftShoulder = CreateBone("LeftShoulder", BoneType.Arm, chest, new Vector3(-0.2f, 0.1f, 0));
                var leftArm = CreateBone("LeftArm", BoneType.Arm, leftShoulder, new Vector3(-0.3f, 0, 0));
                var leftForearm = CreateBone("LeftForearm", BoneType.Arm, leftArm, new Vector3(-0.3f, 0, 0));
                var leftHand = CreateBone("LeftHand", BoneType.Hand, leftForearm, new Vector3(-0.2f, 0, 0));

                var rightShoulder = CreateBone("RightShoulder", BoneType.Arm, chest, new Vector3(0.2f, 0.1f, 0));
                var rightArm = CreateBone("RightArm", BoneType.Arm, rightShoulder, new Vector3(0.3f, 0, 0));
                var rightForearm = CreateBone("RightForearm", BoneType.Arm, rightArm, new Vector3(0.3f, 0, 0));
                var rightHand = CreateBone("RightHand", BoneType.Hand, rightForearm, new Vector3(0.2f, 0, 0));

                // Create legs;
                var leftUpLeg = CreateBone("LeftUpLeg", BoneType.Thigh, hips, new Vector3(-0.1f, -0.2f, 0));
                var leftLeg = CreateBone("LeftLeg", BoneType.Calf, leftUpLeg, new Vector3(0, -0.4f, 0));
                var leftFoot = CreateBone("LeftFoot", BoneType.Foot, leftLeg, new Vector3(0, -0.4f, 0));
                var leftToes = CreateBone("LeftToes", BoneType.Toe, leftFoot, new Vector3(0, 0, 0.1f));

                var rightUpLeg = CreateBone("RightUpLeg", BoneType.Thigh, hips, new Vector3(0.1f, -0.2f, 0));
                var rightLeg = CreateBone("RightLeg", BoneType.Calf, rightUpLeg, new Vector3(0, -0.4f, 0));
                var rightFoot = CreateBone("RightFoot", BoneType.Foot, rightLeg, new Vector3(0, -0.4f, 0));
                var rightToes = CreateBone("RightToes", BoneType.Toe, rightFoot, new Vector3(0, 0, 0.1f));

                // Add fingers for detailed preset;
                if (preset == HumanoidSkeletonPreset.Detailed)
                {
                    CreateFingerChain(leftHand, "LeftThumb", 3, new Vector3(-0.05f, 0, 0), new Vector3(-0.03f, 0, 0.02f));
                    CreateFingerChain(leftHand, "LeftIndex", 4, new Vector3(-0.02f, 0, 0), new Vector3(-0.02f, 0, 0));
                    CreateFingerChain(leftHand, "LeftMiddle", 4, new Vector3(0, 0, 0), new Vector3(-0.02f, 0, 0));
                    CreateFingerChain(leftHand, "LeftRing", 4, new Vector3(0.02f, 0, 0), new Vector3(-0.02f, 0, 0));
                    CreateFingerChain(leftHand, "LeftPinky", 4, new Vector3(0.04f, 0, 0), new Vector3(-0.02f, 0, 0));

                    CreateFingerChain(rightHand, "RightThumb", 3, new Vector3(0.05f, 0, 0), new Vector3(0.03f, 0, 0.02f));
                    CreateFingerChain(rightHand, "RightIndex", 4, new Vector3(0.02f, 0, 0), new Vector3(0.02f, 0, 0));
                    CreateFingerChain(rightHand, "RightMiddle", 4, new Vector3(0, 0, 0), new Vector3(0.02f, 0, 0));
                    CreateFingerChain(rightHand, "RightRing", 4, new Vector3(-0.02f, 0, 0), new Vector3(0.02f, 0, 0));
                    CreateFingerChain(rightHand, "RightPinky", 4, new Vector3(-0.04f, 0, 0), new Vector3(0.02f, 0, 0));
                }

                CurrentSkeleton.RootBone = root;
                RebuildBoneList();

                var result = CompleteBuildOperation(operation, true, "Humanoid skeleton created successfully");

                if (_isAutoOptimizeEnabled)
                {
                    OptimizeSkeleton();
                }

                return result;
            }
            catch (Exception ex)
            {
                return FailBuildOperation(operation, "Failed to create humanoid skeleton", ex);
            }
        }

        /// <summary>
        /// Creates a quadruped skeleton;
        /// </summary>
        public SkeletonBuildResult CreateQuadrupedSkeleton(string skeletonName, QuadrupedType quadrupedType = QuadrupedType.Generic)
        {
            var operation = StartBuildOperation(BuildOperationType.Quadruped, $"Creating quadruped skeleton: {skeletonName}");

            try
            {
                CurrentSkeleton = new Skeleton;
                {
                    Name = skeletonName,
                    SkeletonType = SkeletonType.Quadruped;
                };

                var root = CreateBone("Root", BoneType.Root, null, Vector3.Zero);
                var spine = CreateBone("Spine", BoneType.Spine, root, new Vector3(0, 0.5f, 0));
                var spine1 = CreateBone("Spine1", BoneType.Spine, spine, new Vector3(0, 0.3f, 0));
                var spine2 = CreateBone("Spine2", BoneType.Spine, spine1, new Vector3(0, 0.3f, 0));
                var neck = CreateBone("Neck", BoneType.Neck, spine2, new Vector3(0, 0.2f, 0));
                var head = CreateBone("Head", BoneType.Head, neck, new Vector3(0, 0.15f, 0.1f));

                // Front legs;
                var leftFrontLeg = CreateBone("LeftFrontLeg", BoneType.Thigh, spine, new Vector3(-0.2f, -0.1f, 0));
                var leftFrontKnee = CreateBone("LeftFrontKnee", BoneType.Calf, leftFrontLeg, new Vector3(0, -0.4f, 0));
                var leftFrontAnkle = CreateBone("LeftFrontAnkle", BoneType.Foot, leftFrontKnee, new Vector3(0, -0.3f, 0));
                var leftFrontFoot = CreateBone("LeftFrontFoot", BoneType.Foot, leftFrontAnkle, new Vector3(0, 0, 0.1f));

                var rightFrontLeg = CreateBone("RightFrontLeg", BoneType.Thigh, spine, new Vector3(0.2f, -0.1f, 0));
                var rightFrontKnee = CreateBone("RightFrontKnee", BoneType.Calf, rightFrontLeg, new Vector3(0, -0.4f, 0));
                var rightFrontAnkle = CreateBone("RightFrontAnkle", BoneType.Foot, rightFrontKnee, new Vector3(0, -0.3f, 0));
                var rightFrontFoot = CreateBone("RightFrontFoot", BoneType.Foot, rightFrontAnkle, new Vector3(0, 0, 0.1f));

                // Hind legs;
                var leftHindLeg = CreateBone("LeftHindLeg", BoneType.Thigh, spine1, new Vector3(-0.15f, -0.1f, 0));
                var leftHindKnee = CreateBone("LeftHindKnee", BoneType.Calf, leftHindLeg, new Vector3(0, -0.5f, 0));
                var leftHindAnkle = CreateBone("LeftHindAnkle", BoneType.Foot, leftHindKnee, new Vector3(0, -0.4f, 0));
                var leftHindFoot = CreateBone("LeftHindFoot", BoneType.Foot, leftHindAnkle, new Vector3(0, 0, 0.1f));

                var rightHindLeg = CreateBone("RightHindLeg", BoneType.Thigh, spine1, new Vector3(0.15f, -0.1f, 0));
                var rightHindKnee = CreateBone("RightHindKnee", BoneType.Calf, rightHindLeg, new Vector3(0, -0.5f, 0));
                var rightHindAnkle = CreateBone("RightHindAnkle", BoneType.Foot, rightHindKnee, new Vector3(0, -0.4f, 0));
                var rightHindFoot = CreateBone("RightHindFoot", BoneType.Foot, rightHindAnkle, new Vector3(0, 0, 0.1f));

                // Tail for certain quadrupeds;
                if (quadrupedType == QuadrupedType.Canine || quadrupedType == QuadrupedType.Feline)
                {
                    var tail = CreateBone("Tail", BoneType.Spine, spine2, new Vector3(0, -0.1f, 0));
                    var tail1 = CreateBone("Tail1", BoneType.Spine, tail, new Vector3(0, -0.1f, 0));
                    var tail2 = CreateBone("Tail2", BoneType.Spine, tail1, new Vector3(0, -0.1f, 0));
                }

                CurrentSkeleton.RootBone = root;
                RebuildBoneList();

                var result = CompleteBuildOperation(operation, true, "Quadruped skeleton created successfully");

                if (_isAutoOptimizeEnabled)
                {
                    OptimizeSkeleton();
                }

                return result;
            }
            catch (Exception ex)
            {
                return FailBuildOperation(operation, "Failed to create quadruped skeleton", ex);
            }
        }

        /// <summary>
        /// Adds a bone to the current skeleton;
        /// </summary>
        public Bone AddBone(string boneName, BoneType boneType, string parentBoneName, Vector3 localPosition)
        {
            if (CurrentSkeleton == null)
                throw new InvalidOperationException("No active skeleton. Create a skeleton first.");

            var parentBone = CurrentSkeleton.GetBone(parentBoneName);
            if (parentBone == null)
                throw new ArgumentException($"Parent bone '{parentBoneName}' not found");

            return CreateBone(boneName, boneType, parentBone, localPosition);
        }

        /// <summary>
        /// Removes a bone from the skeleton;
        /// </summary>
        public bool RemoveBone(string boneName)
        {
            if (CurrentSkeleton == null) return false;

            var bone = CurrentSkeleton.GetBone(boneName);
            if (bone == null) return false;

            // Remove from parent;
            bone.ParentName = null;

            // Remove from skeleton bones list;
            CurrentSkeleton.Bones.Remove(bone);

            // Remove all children recursively;
            RemoveBoneChildren(bone);

            return true;
        }

        /// <summary>
        /// Creates a bone chain between two points;
        /// </summary>
        public List<Bone> CreateBoneChain(string baseName, Vector3 startPosition, Vector3 endPosition, int segmentCount, BoneType boneType = BoneType.Custom)
        {
            if (segmentCount < 1)
                throw new ArgumentException("Segment count must be at least 1", nameof(segmentCount));

            var bones = new List<Bone>();
            var direction = (endPosition - startPosition).Normalized;
            var segmentLength = (endPosition - startPosition).Magnitude / segmentCount;

            Bone parentBone = null;
            var currentPosition = startPosition;

            for (int i = 0; i < segmentCount; i++)
            {
                var boneName = $"{baseName}_{i:00}";
                var bone = CreateBone(boneName, boneType, parentBone, currentPosition);

                bones.Add(bone);
                parentBone = bone;
                currentPosition += direction * segmentLength;
            }

            return bones;
        }

        /// <summary>
        /// Creates a finger bone chain;
        /// </summary>
        public List<Bone> CreateFingerChain(Bone handBone, string fingerName, int segmentCount, Vector3 startOffset, Vector3 segmentOffset)
        {
            var bones = new List<Bone>();
            Bone parentBone = handBone;
            var currentPosition = startOffset;

            for (int i = 0; i < segmentCount; i++)
            {
                var boneName = $"{handBone.Name}{fingerName}{i + 1}";
                var bone = CreateBone(boneName, BoneType.Finger, parentBone, currentPosition);

                bones.Add(bone);
                parentBone = bone;
                currentPosition += segmentOffset;
            }

            return bones;
        }

        /// <summary>
        /// Applies a symmetry operation to the skeleton;
        /// </summary>
        public void MirrorSkeleton(string leftSuffix = "Left", string rightSuffix = "Right")
        {
            if (CurrentSkeleton == null) return;

            var leftBones = CurrentSkeleton.Bones.Where(b => b.Name.EndsWith(leftSuffix)).ToList();

            foreach (var leftBone in leftBones)
            {
                var rightBoneName = leftBone.Name.Replace(leftSuffix, rightSuffix);
                var rightBone = CurrentSkeleton.GetBone(rightBoneName);

                if (rightBone == null)
                {
                    // Create mirrored bone;
                    var parentName = leftBone.ParentName?.Replace(leftSuffix, rightSuffix);
                    var parentBone = !string.IsNullOrEmpty(parentName) ? CurrentSkeleton.GetBone(parentName) : null;

                    rightBone = CreateBone(rightBoneName, leftBone.BoneType, parentBone, MirrorVector(leftBone.LocalTransform.Position));
                }
                else;
                {
                    // Update existing bone;
                    rightBone.LocalTransform.Position = MirrorVector(leftBone.LocalTransform.Position);
                    rightBone.LocalTransform.Rotation = MirrorQuaternion(leftBone.LocalTransform.Rotation);
                }
            }
        }

        /// <summary>
        /// Validates the current skeleton;
        /// </summary>
        public SkeletonValidationResult ValidateSkeleton()
        {
            if (CurrentSkeleton == null)
                throw new InvalidOperationException("No active skeleton to validate");

            var result = _validator.Validate(CurrentSkeleton);

            // Update recent errors;
            RecentErrors.Clear();
            foreach (var error in result.Errors)
            {
                RecentErrors.Add(new SkeletonBuildError;
                {
                    ErrorType = BuildErrorType.Validation,
                    Message = error,
                    Timestamp = DateTime.UtcNow;
                });
            }

            OnSkeletonValidated(new SkeletonValidationEventArgs;
            {
                ValidationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Optimizes the current skeleton;
        /// </summary>
        public SkeletonOptimizationResult OptimizeSkeleton()
        {
            if (CurrentSkeleton == null)
                throw new InvalidOperationException("No active skeleton to optimize");

            if (_optimizer == null)
                throw new InvalidOperationException("No skeleton optimizer configured");

            var result = _optimizer.Optimize(CurrentSkeleton);

            OnSkeletonOptimized(new SkeletonOptimizationEventArgs;
            {
                OptimizationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Calculates bone lengths and proportions;
        /// </summary>
        public SkeletonProportions CalculateProportions()
        {
            if (CurrentSkeleton == null)
                throw new InvalidOperationException("No active skeleton");

            var proportions = new SkeletonProportions();

            // Calculate limb lengths;
            proportions.LeftArmLength = CalculateLimbLength("LeftShoulder", "LeftHand");
            proportions.RightArmLength = CalculateLimbLength("RightShoulder", "RightHand");
            proportions.LeftLegLength = CalculateLimbLength("LeftUpLeg", "LeftFoot");
            proportions.RightLegLength = CalculateLimbLength("RightUpLeg", "RightFoot");
            proportions.TorsoLength = CalculateLimbLength("Hips", "Neck");
            proportions.Height = CalculateHeight();

            return proportions;
        }

        /// <summary>
        /// Exports the current skeleton to a file;
        /// </summary>
        public void ExportSkeleton(string filePath, SkeletonExportFormat format = SkeletonExportFormat.JSON)
        {
            if (CurrentSkeleton == null)
                throw new InvalidOperationException("No active skeleton to export");

            var exporter = SkeletonExporterFactory.CreateExporter(format);
            exporter.Export(CurrentSkeleton, filePath);
        }

        /// <summary>
        /// Imports a skeleton from a file;
        /// </summary>
        public SkeletonBuildResult ImportSkeleton(string filePath, SkeletonExportFormat format = SkeletonExportFormat.JSON)
        {
            var operation = StartBuildOperation(BuildOperationType.Import, $"Importing skeleton from: {filePath}");

            try
            {
                var importer = SkeletonExporterFactory.CreateImporter(format);
                CurrentSkeleton = importer.Import(filePath);

                var result = CompleteBuildOperation(operation, true, "Skeleton imported successfully");

                if (_isAutoValidateEnabled)
                {
                    ValidateSkeleton();
                }

                return result;
            }
            catch (Exception ex)
            {
                return FailBuildOperation(operation, "Failed to import skeleton", ex);
            }
        }

        private Bone CreateBone(string name, BoneType type, Bone parent, Vector3 localPosition)
        {
            var bone = new Bone(name, parent?.Name)
            {
                BoneType = type,
                LocalTransform = new BoneTransform;
                {
                    Position = localPosition,
                    Rotation = Quaternion.Identity,
                    Scale = Vector3.One;
                }
            };

            CurrentSkeleton.Bones.Add(bone);
            parent?.AddChild(bone);

            return bone;
        }

        private void RemoveBoneChildren(Bone bone)
        {
            foreach (var child in bone.Children.ToList())
            {
                CurrentSkeleton.Bones.Remove(child);
                RemoveBoneChildren(child);
            }
        }

        private void RebuildBoneList()
        {
            if (CurrentSkeleton.RootBone == null) return;

            CurrentSkeleton.Bones.Clear();
            CurrentSkeleton.Bones.Add(CurrentSkeleton.RootBone);

            foreach (var descendant in CurrentSkeleton.RootBone.GetDescendants())
            {
                CurrentSkeleton.Bones.Add(descendant);
            }
        }

        private Vector3 MirrorVector(Vector3 vector)
        {
            return new Vector3(-vector.X, vector.Y, vector.Z);
        }

        private Quaternion MirrorQuaternion(Quaternion quaternion)
        {
            return new Quaternion(-quaternion.X, quaternion.Y, quaternion.Z, -quaternion.W);
        }

        private float CalculateLimbLength(string startBoneName, string endBoneName)
        {
            var startBone = CurrentSkeleton.GetBone(startBoneName);
            var endBone = CurrentSkeleton.GetBone(endBoneName);

            if (startBone == null || endBone == null) return 0;

            var startPos = startBone.CalculateWorldTransform().Position;
            var endPos = endBone.CalculateWorldTransform().Position;

            return (endPos - startPos).Magnitude;
        }

        private float CalculateHeight()
        {
            var root = CurrentSkeleton.RootBone;
            var head = CurrentSkeleton.GetBone("Head");

            if (root == null || head == null) return 0;

            var rootPos = root.CalculateWorldTransform().Position;
            var headPos = head.CalculateWorldTransform().Position;

            return (headPos - rootPos).Magnitude;
        }

        private void InitializeTemplates()
        {
            if (_templateProvider == null) return;

            var templates = _templateProvider.GetTemplates();
            foreach (var template in templates)
            {
                AvailableTemplates.Add(template);
            }
        }

        private SkeletonBuildOperation StartBuildOperation(BuildOperationType type, string message)
        {
            var operation = new SkeletonBuildOperation;
            {
                OperationType = type,
                StatusMessage = message,
                StartTime = DateTime.UtcNow,
                State = SkeletonBuildState.Running;
            };

            BuildHistory.Add(operation);

            OnBuildStarted(new SkeletonBuildEventArgs;
            {
                Operation = operation,
                Timestamp = DateTime.UtcNow;
            });

            return operation;
        }

        private SkeletonBuildResult CompleteBuildOperation(SkeletonBuildOperation operation, bool success, string message)
        {
            operation.State = success ? SkeletonBuildState.Completed : SkeletonBuildState.Failed;
            operation.StatusMessage = message;
            operation.EndTime = DateTime.UtcNow;
            operation.Progress = 1.0f;

            var result = new SkeletonBuildResult;
            {
                Success = success,
                Message = message,
                Operation = operation,
                Skeleton = CurrentSkeleton;
            };

            OnBuildCompleted(new SkeletonBuildEventArgs;
            {
                Operation = operation,
                Result = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        private SkeletonBuildResult FailBuildOperation(SkeletonBuildOperation operation, string message, Exception exception)
        {
            operation.Errors.Add(new SkeletonBuildError;
            {
                ErrorType = BuildErrorType.Exception,
                Message = $"{message}: {exception.Message}",
                Exception = exception,
                Timestamp = DateTime.UtcNow;
            });

            return CompleteBuildOperation(operation, false, message);
        }

        private void OnProfileChanged()
        {
            // Handle profile changes;
        }

        private void OnBuildStarted(SkeletonBuildEventArgs e)
        {
            BuildStarted?.Invoke(this, e);
        }

        private void OnBuildCompleted(SkeletonBuildEventArgs e)
        {
            BuildCompleted?.Invoke(this, e);
        }

        private void OnSkeletonValidated(SkeletonValidationEventArgs e)
        {
            SkeletonValidated?.Invoke(this, e);
        }

        private void OnSkeletonOptimized(SkeletonOptimizationEventArgs e)
        {
            SkeletonOptimized?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum SkeletonBuildMode;
    {
        Standard,
        Advanced,
        Expert;
    }

    public enum BuildOperationType;
    {
        Create,
        Template,
        Humanoid,
        Quadruped,
        Import,
        Export,
        Modify;
    }

    public enum SkeletonBuildState;
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled;
    }

    public enum HumanoidSkeletonPreset;
    {
        Standard,
        Detailed,
        GameReady,
        FilmQuality;
    }

    public enum QuadrupedType;
    {
        Generic,
        Canine,
        Feline,
        Equine,
        Bovine;
    }

    public enum BuildErrorType;
    {
        Validation,
        Optimization,
        Import,
        Export,
        Exception;
    }

    public enum SkeletonExportFormat;
    {
        JSON,
        XML,
        Binary,
        FBX;
    }

    public interface ISkeletonBuilder;
    {
        SkeletonBuildResult CreateNewSkeleton(string skeletonName, SkeletonType skeletonType);
        SkeletonBuildResult CreateHumanoidSkeleton(string skeletonName, HumanoidSkeletonPreset preset = HumanoidSkeletonPreset.Standard);
        Bone AddBone(string boneName, BoneType boneType, string parentBoneName, Vector3 localPosition);
        bool RemoveBone(string boneName);
        SkeletonValidationResult ValidateSkeleton();
    }

    public interface ISkeletonValidator;
    {
        SkeletonValidationResult Validate(Skeleton skeleton);
    }

    public interface ISkeletonOptimizer;
    {
        SkeletonOptimizationResult Optimize(Skeleton skeleton);
    }

    public interface ISkeletonTemplateProvider;
    {
        IEnumerable<SkeletonTemplate> GetTemplates();
    }

    public class SkeletonBuildProfile;
    {
        public string Name { get; set; }
        public SkeletonType TargetSkeletonType { get; set; }
        public Dictionary<string, object> Settings { get; } = new Dictionary<string, object>();
        public List<BoneDefinition> BoneDefinitions { get; } = new List<BoneDefinition>();
    }

    public class BoneDefinition;
    {
        public string Name { get; set; }
        public BoneType Type { get; set; }
        public string Parent { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public float Length { get; set; }
    }

    public class SkeletonTemplate;
    {
        public string Name { get; set; }
        public SkeletonType SkeletonType { get; set; }
        public string Description { get; set; }
        public List<BoneDefinition> BoneDefinitions { get; } = new List<BoneDefinition>();

        public Skeleton CreateSkeleton()
        {
            var skeleton = new Skeleton;
            {
                Name = $"{Name}_Instance",
                SkeletonType = SkeletonType;
            };

            var boneMap = new Dictionary<string, Bone>();

            // Create bones;
            foreach (var boneDef in BoneDefinitions)
            {
                var bone = new Bone(boneDef.Name, boneDef.Parent)
                {
                    BoneType = boneDef.Type,
                    LocalTransform = new BoneTransform;
                    {
                        Position = boneDef.Position,
                        Rotation = boneDef.Rotation,
                        Scale = Vector3.One;
                    },
                    Length = boneDef.Length;
                };

                boneMap[boneDef.Name] = bone;
                skeleton.Bones.Add(bone);
            }

            // Build hierarchy;
            foreach (var bone in skeleton.Bones)
            {
                if (!string.IsNullOrEmpty(bone.ParentName) && boneMap.TryGetValue(bone.ParentName, out var parent))
                {
                    parent.AddChild(bone);
                }
            }

            // Set root bone;
            skeleton.RootBone = skeleton.Bones.FirstOrDefault(b => string.IsNullOrEmpty(b.ParentName));

            return skeleton;
        }
    }

    public class SkeletonBuildResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public SkeletonBuildOperation Operation { get; set; }
        public Skeleton Skeleton { get; set; }
        public List<SkeletonBuildError> Errors { get; set; } = new List<SkeletonBuildError>();
        public TimeSpan Duration => Operation?.Duration ?? TimeSpan.Zero;
    }

    public class SkeletonBuildError;
    {
        public BuildErrorType ErrorType { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SkeletonValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Information { get; set; } = new List<string>();
    }

    public class SkeletonOptimizationResult;
    {
        public bool Success { get; set; }
        public Skeleton OptimizedSkeleton { get; set; }
        public List<string> OptimizationsApplied { get; set; } = new List<string>();
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class PerformanceMetrics;
    {
        public int OriginalBoneCount { get; set; }
        public int OptimizedBoneCount { get; set; }
        public float OptimizationRatio { get; set; }
        public TimeSpan OptimizationTime { get; set; }
    }

    public class SkeletonProportions;
    {
        public float Height { get; set; }
        public float TorsoLength { get; set; }
        public float LeftArmLength { get; set; }
        public float RightArmLength { get; set; }
        public float LeftLegLength { get; set; }
        public float RightLegLength { get; set; }
        public float ArmSpan => LeftArmLength + RightArmLength;
    }

    public class SkeletonBuildEventArgs : EventArgs;
    {
        public SkeletonBuildOperation Operation { get; set; }
        public SkeletonBuildResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SkeletonValidationEventArgs : EventArgs;
    {
        public SkeletonValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SkeletonOptimizationEventArgs : EventArgs;
    {
        public SkeletonOptimizationResult OptimizationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Default Implementations;

    public class DefaultSkeletonValidator : ISkeletonValidator;
    {
        public SkeletonValidationResult Validate(Skeleton skeleton)
        {
            var result = new SkeletonValidationResult();

            if (skeleton == null)
            {
                result.Errors.Add("Skeleton is null");
                return result;
            }

            // Check for root bone;
            if (skeleton.RootBone == null)
            {
                result.Errors.Add("No root bone found");
            }

            // Check for bone name uniqueness;
            var duplicateNames = skeleton.Bones;
                .GroupBy(b => b.Name)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var name in duplicateNames)
            {
                result.Errors.Add($"Duplicate bone name: {name}");
            }

            // Check for circular references;
            CheckCircularReferences(skeleton.RootBone, new HashSet<Bone>(), result);

            // Check bone hierarchy;
            foreach (var bone in skeleton.Bones)
            {
                if (!string.IsNullOrEmpty(bone.ParentName))
                {
                    var parent = skeleton.GetBone(bone.ParentName);
                    if (parent == null)
                    {
                        result.Errors.Add($"Bone '{bone.Name}' has invalid parent '{bone.ParentName}'");
                    }
                }
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }

        private void CheckCircularReferences(Bone bone, HashSet<Bone> visited, SkeletonValidationResult result)
        {
            if (bone == null) return;

            if (visited.Contains(bone))
            {
                result.Errors.Add($"Circular reference detected at bone: {bone.Name}");
                return;
            }

            visited.Add(bone);

            foreach (var child in bone.Children)
            {
                CheckCircularReferences(child, visited, result);
            }

            visited.Remove(bone);
        }
    }

    public static class SkeletonExporterFactory;
    {
        public static ISkeletonExporter CreateExporter(SkeletonExportFormat format)
        {
            return format switch;
            {
                SkeletonExportFormat.JSON => new JsonSkeletonExporter(),
                SkeletonExportFormat.XML => new XmlSkeletonExporter(),
                SkeletonExportFormat.Binary => new BinarySkeletonExporter(),
                SkeletonExportFormat.FBX => new FbxSkeletonExporter(),
                _ => throw new NotSupportedException($"Export format {format} is not supported")
            };
        }

        public static ISkeletonImporter CreateImporter(SkeletonExportFormat format)
        {
            return format switch;
            {
                SkeletonExportFormat.JSON => new JsonSkeletonImporter(),
                SkeletonExportFormat.XML => new XmlSkeletonImporter(),
                SkeletonExportFormat.Binary => new BinarySkeletonImporter(),
                SkeletonExportFormat.FBX => new FbxSkeletonImporter(),
                _ => throw new NotSupportedException($"Import format {format} is not supported")
            };
        }
    }

    public interface ISkeletonExporter;
    {
        void Export(Skeleton skeleton, string filePath);
    }

    public interface ISkeletonImporter;
    {
        Skeleton Import(string filePath);
    }

    public class JsonSkeletonExporter : ISkeletonExporter;
    {
        public void Export(Skeleton skeleton, string filePath)
        {
            // JSON export implementation;
        }
    }

    public class JsonSkeletonImporter : ISkeletonImporter;
    {
        public Skeleton Import(string filePath)
        {
            // JSON import implementation;
            return new Skeleton();
        }
    }

    public class XmlSkeletonExporter : ISkeletonExporter;
    {
        public void Export(Skeleton skeleton, string filePath)
        {
            // XML export implementation;
        }
    }

    public class XmlSkeletonImporter : ISkeletonImporter;
    {
        public Skeleton Import(string filePath)
        {
            // XML import implementation;
            return new Skeleton();
        }
    }

    public class BinarySkeletonExporter : ISkeletonExporter;
    {
        public void Export(Skeleton skeleton, string filePath)
        {
            // Binary export implementation;
        }
    }

    public class BinarySkeletonImporter : ISkeletonImporter;
    {
        public Skeleton Import(string filePath)
        {
            // Binary import implementation;
            return new Skeleton();
        }
    }

    public class FbxSkeletonExporter : ISkeletonExporter;
    {
        public void Export(Skeleton skeleton, string filePath)
        {
            // FBX export implementation;
        }
    }

    public class FbxSkeletonImporter : ISkeletonImporter;
    {
        public Skeleton Import(string filePath)
        {
            // FBX import implementation;
            return new Skeleton();
        }
    }

    #endregion;

    #region Exceptions;

    public class SkeletonBuildException : Exception
    {
        public SkeletonBuildException(string message) : base(message) { }
        public SkeletonBuildException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SkeletonValidationException : SkeletonBuildException;
    {
        public SkeletonValidationException(string message) : base(message) { }
    }

    #endregion;
}
