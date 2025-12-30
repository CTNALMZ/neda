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
    /// Represents a bone in the character skeleton with transformation data;
    /// </summary>
    public class Bone : INotifyPropertyChanged;
    {
        private string _name;
        private string _parentName;
        private BoneTransform _localTransform;
        private BoneTransform _worldTransform;
        private BoneType _boneType;
        private float _length;
        private bool _isEssential;
        private Dictionary<string, object> _metadata;

        public string Name;
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string ParentName;
        {
            get => _parentName;
            set { _parentName = value; OnPropertyChanged(); }
        }

        public BoneTransform LocalTransform;
        {
            get => _localTransform;
            set { _localTransform = value; OnPropertyChanged(); }
        }

        public BoneTransform WorldTransform;
        {
            get => _worldTransform;
            set { _worldTransform = value; OnPropertyChanged(); }
        }

        public BoneType BoneType;
        {
            get => _boneType;
            set { _boneType = value; OnPropertyChanged(); }
        }

        public float Length;
        {
            get => _length;
            set { _length = Math.Max(0, value); OnPropertyChanged(); }
        }

        public bool IsEssential;
        {
            get => _isEssential;
            set { _isEssential = value; OnPropertyChanged(); }
        }

        public Dictionary<string, object> Metadata;
        {
            get => _metadata;
            set { _metadata = value; OnPropertyChanged(); }
        }

        public ObservableCollection<Bone> Children { get; } = new ObservableCollection<Bone>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public Bone()
        {
            _localTransform = BoneTransform.Identity;
            _worldTransform = BoneTransform.Identity;
            _metadata = new Dictionary<string, object>();
        }

        public Bone(string name, string parentName = null) : this()
        {
            _name = name;
            _parentName = parentName;
        }

        public void AddChild(Bone child)
        {
            if (child == null) return;

            Children.Add(child);
            child.ParentName = Name;
        }

        public void RemoveChild(Bone child)
        {
            if (child == null) return;

            Children.Remove(child);
            child.ParentName = null;
        }

        public Bone FindChild(string boneName)
        {
            return Children.FirstOrDefault(b => b.Name == boneName) ??
                   Children.Select(b => b.FindChild(boneName)).FirstOrDefault(result => result != null);
        }

        public IEnumerable<Bone> GetDescendants()
        {
            foreach (var child in Children)
            {
                yield return child;
                foreach (var descendant in child.GetDescendants())
                {
                    yield return descendant;
                }
            }
        }

        public void CalculateWorldTransform(Bone parentWorldTransform = null)
        {
            if (parentWorldTransform != null)
            {
                WorldTransform = parentWorldTransform.Combine(LocalTransform);
            }
            else;
            {
                WorldTransform = LocalTransform;
            }

            foreach (var child in Children)
            {
                child.CalculateWorldTransform(WorldTransform);
            }
        }

        public override string ToString() => $"{Name} ({BoneType})";
    }

    /// <summary>
    /// Represents bone transformation data (position, rotation, scale)
    /// </summary>
    public struct BoneTransform : IEquatable<BoneTransform>
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public static BoneTransform Identity => new BoneTransform;
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One;
        };

        public BoneTransform Combine(BoneTransform other)
        {
            return new BoneTransform;
            {
                Position = Position + Rotation * (other.Position * Scale),
                Rotation = Rotation * other.Rotation,
                Scale = Scale * other.Scale;
            };
        }

        public BoneTransform Inverse()
        {
            var inverseRotation = Rotation.Inverse();
            var inverseScale = new Vector3(1.0f / Scale.X, 1.0f / Scale.Y, 1.0f / Scale.Z);

            return new BoneTransform;
            {
                Position = inverseRotation * (-Position * inverseScale),
                Rotation = inverseRotation,
                Scale = inverseScale;
            };
        }

        public bool Equals(BoneTransform other)
        {
            return Position.Equals(other.Position) &&
                   Rotation.Equals(other.Rotation) &&
                   Scale.Equals(other.Scale);
        }

        public override bool Equals(object obj)
        {
            return obj is BoneTransform other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Position, Rotation, Scale);
        }

        public static bool operator ==(BoneTransform left, BoneTransform right) => left.Equals(right);
        public static bool operator !=(BoneTransform left, BoneTransform right) => !left.Equals(right);
    }

    /// <summary>
    /// Bone mapping between source and target skeletons;
    /// </summary>
    public class BoneMapping : INotifyPropertyChanged;
    {
        private string _sourceBone;
        private string _targetBone;
        private MappingType _mappingType;
        private float _mappingWeight = 1.0f;
        private Vector3 _positionOffset;
        private Quaternion _rotationOffset;
        private Vector3 _scaleOffset;
        private MappingConstraint _constraint;

        public string SourceBone;
        {
            get => _sourceBone;
            set { _sourceBone = value; OnPropertyChanged(); }
        }

        public string TargetBone;
        {
            get => _targetBone;
            set { _targetBone = value; OnPropertyChanged(); }
        }

        public MappingType MappingType;
        {
            get => _mappingType;
            set { _mappingType = value; OnPropertyChanged(); }
        }

        public float MappingWeight;
        {
            get => _mappingWeight;
            set { _mappingWeight = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public Vector3 PositionOffset;
        {
            get => _positionOffset;
            set { _positionOffset = value; OnPropertyChanged(); }
        }

        public Quaternion RotationOffset;
        {
            get => _rotationOffset;
            set { _rotationOffset = value; OnPropertyChanged(); }
        }

        public Vector3 ScaleOffset;
        {
            get => _scaleOffset;
            set { _scaleOffset = value; OnPropertyChanged(); }
        }

        public MappingConstraint Constraint;
        {
            get => _constraint;
            set { _constraint = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public BoneTransform ApplyMapping(BoneTransform sourceTransform)
        {
            var result = sourceTransform;

            // Apply offsets;
            result.Position += PositionOffset;
            result.Rotation = result.Rotation * RotationOffset;
            result.Scale *= ScaleOffset;

            // Apply constraints;
            if (_constraint != null)
            {
                result = _constraint.ApplyConstraint(result);
            }

            return result;
        }
    }

    /// <summary>
    /// Main bone mapper for character skeleton setup and retargeting;
    /// </summary>
    public class BoneMapper : INotifyPropertyChanged, IBoneMapper;
    {
        private readonly IBoneMappingStrategy _mappingStrategy;
        private readonly IBoneValidator _validator;

        private Skeleton _sourceSkeleton;
        private Skeleton _targetSkeleton;
        private BoneMappingProfile _currentProfile;
        private MappingQuality _mappingQuality;
        private bool _isAutoUpdateEnabled = true;

        public Skeleton SourceSkeleton;
        {
            get => _sourceSkeleton;
            set { _sourceSkeleton = value; OnPropertyChanged(); OnSkeletonChanged(); }
        }

        public Skeleton TargetSkeleton;
        {
            get => _targetSkeleton;
            set { _targetSkeleton = value; OnPropertyChanged(); OnSkeletonChanged(); }
        }

        public BoneMappingProfile CurrentProfile;
        {
            get => _currentProfile;
            set { _currentProfile = value; OnPropertyChanged(); OnProfileChanged(); }
        }

        public MappingQuality MappingQuality;
        {
            get => _mappingQuality;
            set { _mappingQuality = value; OnPropertyChanged(); }
        }

        public bool IsAutoUpdateEnabled;
        {
            get => _isAutoUpdateEnabled;
            set { _isAutoUpdateEnabled = value; OnPropertyChanged(); }
        }

        public ObservableCollection<BoneMapping> ActiveMappings { get; } = new ObservableCollection<BoneMapping>();
        public ObservableCollection<BoneMappingRule> MappingRules { get; } = new ObservableCollection<BoneMappingRule>();
        public ObservableCollection<MappingValidationResult> ValidationResults { get; } = new ObservableCollection<MappingValidationResult>();

        public event EventHandler<BoneMappingEventArgs> BoneMapped;
        public event EventHandler<SkeletonMappingEventArgs> SkeletonMapped;
        public event EventHandler<MappingValidationEventArgs> MappingValidated;

        public BoneMapper(IBoneMappingStrategy mappingStrategy = null, IBoneValidator validator = null)
        {
            _mappingStrategy = mappingStrategy ?? new DefaultBoneMappingStrategy();
            _validator = validator;
            _mappingQuality = MappingQuality.High;

            ActiveMappings.CollectionChanged += OnActiveMappingsChanged;
        }

        /// <summary>
        /// Automatically maps bones between source and target skeletons;
        /// </summary>
        public BoneMappingResult AutoMapBones(MappingPreset preset = MappingPreset.Humanoid)
        {
            if (_sourceSkeleton == null || _targetSkeleton == null)
                throw new InvalidOperationException("Source and target skeletons must be set before mapping");

            var result = new BoneMappingResult();

            try
            {
                var mappings = _mappingStrategy.GenerateMappings(_sourceSkeleton, _targetSkeleton, preset);

                ActiveMappings.Clear();
                foreach (var mapping in mappings)
                {
                    ActiveMappings.Add(mapping);
                }

                result.Mappings = new List<BoneMapping>(mappings);
                result.Success = true;
                result.MappedBoneCount = mappings.Count;

                OnSkeletonMapped(new SkeletonMappingEventArgs;
                {
                    SourceSkeleton = _sourceSkeleton,
                    TargetSkeleton = _targetSkeleton,
                    Mappings = result.Mappings,
                    Preset = preset,
                    Timestamp = DateTime.UtcNow;
                });

                // Auto-validate if validator is available;
                if (_validator != null)
                {
                    var validationResult = _validator.ValidateMappings(_sourceSkeleton, _targetSkeleton, ActiveMappings.ToList());
                    UpdateValidationResults(validationResult);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Auto-mapping failed: {ex.Message}");
                throw new BoneMappingException("Automatic bone mapping failed", ex);
            }

            return result;
        }

        /// <summary>
        /// Manually adds a bone mapping;
        /// </summary>
        public BoneMapping AddManualMapping(string sourceBone, string targetBone, MappingType mappingType = MappingType.Full)
        {
            if (string.IsNullOrWhiteSpace(sourceBone) || string.IsNullOrWhiteSpace(targetBone))
                throw new ArgumentException("Source and target bone names cannot be null or empty");

            if (_sourceSkeleton?.GetBone(sourceBone) == null)
                throw new ArgumentException($"Source bone '{sourceBone}' not found in source skeleton");

            if (_targetSkeleton?.GetBone(targetBone) == null)
                throw new ArgumentException($"Target bone '{targetBone}' not found in target skeleton");

            var mapping = new BoneMapping;
            {
                SourceBone = sourceBone,
                TargetBone = targetBone,
                MappingType = mappingType;
            };

            ActiveMappings.Add(mapping);

            OnBoneMapped(new BoneMappingEventArgs;
            {
                Mapping = mapping,
                Operation = MappingOperation.Added,
                Timestamp = DateTime.UtcNow;
            });

            return mapping;
        }

        /// <summary>
        /// Removes a bone mapping;
        /// </summary>
        public bool RemoveMapping(string sourceBone, string targetBone)
        {
            var mapping = ActiveMappings.FirstOrDefault(m =>
                m.SourceBone == sourceBone && m.TargetBone == targetBone);

            if (mapping != null)
            {
                ActiveMappings.Remove(mapping);

                OnBoneMapped(new BoneMappingEventArgs;
                {
                    Mapping = mapping,
                    Operation = MappingOperation.Removed,
                    Timestamp = DateTime.UtcNow;
                });

                return true;
            }

            return false;
        }

        /// <summary>
        /// Applies bone mappings to transfer animation from source to target;
        /// </summary>
        public PoseMappingResult ApplyMappings(SkeletonPose sourcePose)
        {
            if (_targetSkeleton == null)
                throw new InvalidOperationException("Target skeleton must be set before applying mappings");

            var result = new PoseMappingResult();
            var targetPose = new SkeletonPose(_targetSkeleton);

            try
            {
                foreach (var mapping in ActiveMappings.Where(m => m.MappingWeight > 0))
                {
                    var sourceBonePose = sourcePose.GetBonePose(mapping.SourceBone);
                    if (sourceBonePose == null) continue;

                    var targetBone = _targetSkeleton.GetBone(mapping.TargetBone);
                    if (targetBone == null) continue;

                    var mappedTransform = mapping.ApplyMapping(sourceBonePose.Transform);
                    targetPose.SetBonePose(mapping.TargetBone, new BonePose(mappedTransform));

                    result.AppliedMappings.Add(mapping);
                }

                result.MappedPose = targetPose;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Pose mapping failed: {ex.Message}");
                throw new BoneMappingException("Pose mapping failed", ex);
            }

            return result;
        }

        /// <summary>
        /// Finds the best mapping for a source bone;
        /// </summary>
        public BoneMapping FindBestMapping(string sourceBone, MappingSearchMode searchMode = MappingSearchMode.Exact)
        {
            if (string.IsNullOrWhiteSpace(sourceBone)) return null;

            var sourceBoneObj = _sourceSkeleton?.GetBone(sourceBone);
            if (sourceBoneObj == null) return null;

            // First check existing mappings;
            var existingMapping = ActiveMappings.FirstOrDefault(m => m.SourceBone == sourceBone);
            if (existingMapping != null) return existingMapping;

            // Find potential target bones based on search mode;
            var potentialTargets = FindPotentialTargetBones(sourceBoneObj, searchMode);
            if (!potentialTargets.Any()) return null;

            // Create temporary mapping for the best match;
            var bestTarget = potentialTargets.First();
            return new BoneMapping;
            {
                SourceBone = sourceBone,
                TargetBone = bestTarget.Name,
                MappingType = DetermineMappingType(sourceBoneObj, bestTarget)
            };
        }

        /// <summary>
        /// Validates all current bone mappings;
        /// </summary>
        public MappingValidationResult ValidateMappings()
        {
            if (_validator == null)
                throw new InvalidOperationException("No bone validator configured");

            var result = _validator.ValidateMappings(_sourceSkeleton, _targetSkeleton, ActiveMappings.ToList());
            UpdateValidationResults(result);

            OnMappingValidated(new MappingValidationEventArgs;
            {
                ValidationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Optimizes bone mappings for performance;
        /// </summary>
        public void OptimizeMappings()
        {
            // Remove mappings with very low weight;
            var lowWeightMappings = ActiveMappings.Where(m => m.MappingWeight < 0.01f).ToList();
            foreach (var mapping in lowWeightMappings)
            {
                ActiveMappings.Remove(mapping);
            }

            // Merge similar mappings;
            MergeSimilarMappings();

            // Reorder mappings for better cache performance;
            ReorderMappingsForPerformance();
        }

        /// <summary>
        /// Saves current mappings to a profile;
        /// </summary>
        public BoneMappingProfile SaveToProfile(string profileName)
        {
            var profile = new BoneMappingProfile;
            {
                Name = profileName,
                SourceSkeletonType = _sourceSkeleton?.SkeletonType ?? SkeletonType.Unknown,
                TargetSkeletonType = _targetSkeleton?.SkeletonType ?? SkeletonType.Unknown;
            };

            foreach (var mapping in ActiveMappings)
            {
                profile.Mappings.Add(new BoneMapping;
                {
                    SourceBone = mapping.SourceBone,
                    TargetBone = mapping.TargetBone,
                    MappingType = mapping.MappingType,
                    MappingWeight = mapping.MappingWeight,
                    PositionOffset = mapping.PositionOffset,
                    RotationOffset = mapping.RotationOffset,
                    ScaleOffset = mapping.ScaleOffset;
                });
            }

            return profile;
        }

        /// <summary>
        /// Loads mappings from a profile;
        /// </summary>
        public void LoadFromProfile(BoneMappingProfile profile)
        {
            if (profile == null) return;

            ActiveMappings.Clear();
            foreach (var mapping in profile.Mappings)
            {
                ActiveMappings.Add(mapping);
            }

            _currentProfile = profile;

            OnProfileChanged();
        }

        /// <summary>
        /// Creates a mirror mapping for symmetric skeletons;
        /// </summary>
        public void CreateMirrorMappings()
        {
            var mirrorMappings = new List<BoneMapping>();

            foreach (var mapping in ActiveMappings.ToList())
            {
                var mirroredSource = GetMirroredBoneName(mapping.SourceBone);
                var mirroredTarget = GetMirroredBoneName(mapping.TargetBone);

                if (_sourceSkeleton?.GetBone(mirroredSource) != null &&
                    _targetSkeleton?.GetBone(mirroredTarget) != null)
                {
                    var mirrorMapping = new BoneMapping;
                    {
                        SourceBone = mirroredSource,
                        TargetBone = mirroredTarget,
                        MappingType = mapping.MappingType,
                        MappingWeight = mapping.MappingWeight,
                        PositionOffset = MirrorVector(mapping.PositionOffset),
                        RotationOffset = MirrorQuaternion(mapping.RotationOffset),
                        ScaleOffset = mapping.ScaleOffset;
                    };

                    mirrorMappings.Add(mirrorMapping);
                }
            }

            foreach (var mirrorMapping in mirrorMappings)
            {
                if (!ActiveMappings.Any(m => m.SourceBone == mirrorMapping.SourceBone && m.TargetBone == mirrorMapping.TargetBone))
                {
                    ActiveMappings.Add(mirrorMapping);
                }
            }
        }

        /// <summary>
        /// Calculates mapping statistics;
        /// </summary>
        public MappingStatistics GetStatistics()
        {
            var stats = new MappingStatistics;
            {
                TotalMappings = ActiveMappings.Count,
                FullMappings = ActiveMappings.Count(m => m.MappingType == MappingType.Full),
                PartialMappings = ActiveMappings.Count(m => m.MappingType == MappingType.PositionOnly || m.MappingType == MappingType.RotationOnly),
                AverageWeight = ActiveMappings.Average(m => m.MappingWeight)
            };

            if (_sourceSkeleton != null)
            {
                stats.SourceBoneCount = _sourceSkeleton.Bones.Count;
                stats.MappedSourceBones = ActiveMappings.Select(m => m.SourceBone).Distinct().Count();
            }

            if (_targetSkeleton != null)
            {
                stats.TargetBoneCount = _targetSkeleton.Bones.Count;
                stats.MappedTargetBones = ActiveMappings.Select(m => m.TargetBone).Distinct().Count();
            }

            return stats;
        }

        private List<Bone> FindPotentialTargetBones(Bone sourceBone, MappingSearchMode searchMode)
        {
            var potentialTargets = new List<Bone>();

            if (_targetSkeleton == null) return potentialTargets;

            foreach (var targetBone in _targetSkeleton.Bones)
            {
                if (IsPotentialMatch(sourceBone, targetBone, searchMode))
                {
                    potentialTargets.Add(targetBone);
                }
            }

            // Sort by relevance;
            return potentialTargets;
                .OrderBy(b => CalculateBoneSimilarity(sourceBone, b))
                .ToList();
        }

        private bool IsPotentialMatch(Bone sourceBone, Bone targetBone, MappingSearchMode searchMode)
        {
            return searchMode switch;
            {
                MappingSearchMode.Exact => sourceBone.Name.Equals(targetBone.Name, StringComparison.OrdinalIgnoreCase),
                MappingSearchMode.Smart => IsSmartMatch(sourceBone, targetBone),
                MappingSearchMode.Loose => IsLooseMatch(sourceBone, targetBone),
                _ => false;
            };
        }

        private bool IsSmartMatch(Bone sourceBone, Bone targetBone)
        {
            // Exact name match;
            if (sourceBone.Name.Equals(targetBone.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            // Common naming variations;
            var normalizedSource = NormalizeBoneName(sourceBone.Name);
            var normalizedTarget = NormalizeBoneName(targetBone.Name);

            if (normalizedSource.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                return true;

            // Hierarchical match;
            if (sourceBone.ParentName != null && targetBone.ParentName != null)
            {
                var sourceParent = _sourceSkeleton.GetBone(sourceBone.ParentName);
                var targetParent = _targetSkeleton.GetBone(targetBone.ParentName);

                if (sourceParent != null && targetParent != null &&
                    IsSmartMatch(sourceParent, targetParent))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsLooseMatch(Bone sourceBone, Bone targetBone)
        {
            // Bone type matching;
            if (sourceBone.BoneType == targetBone.BoneType)
                return true;

            // Name contains common keywords;
            var sourceKeywords = ExtractBoneKeywords(sourceBone.Name);
            var targetKeywords = ExtractBoneKeywords(targetBone.Name);

            return sourceKeywords.Any(k => targetKeywords.Contains(k));
        }

        private float CalculateBoneSimilarity(Bone source, Bone target)
        {
            float similarity = 0f;

            // Name similarity;
            if (source.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                similarity += 0.4f;
            else if (NormalizeBoneName(source.Name).Equals(NormalizeBoneName(target.Name), StringComparison.OrdinalIgnoreCase))
                similarity += 0.3f;

            // Bone type similarity;
            if (source.BoneType == target.BoneType)
                similarity += 0.3f;

            // Hierarchical similarity;
            if (source.ParentName != null && target.ParentName != null)
            {
                var sourceParent = _sourceSkeleton.GetBone(source.ParentName);
                var targetParent = _targetSkeleton.GetBone(target.ParentName);

                if (sourceParent != null && targetParent != null &&
                    sourceParent.Name.Equals(targetParent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    similarity += 0.2f;
                }
            }

            return similarity;
        }

        private string NormalizeBoneName(string boneName)
        {
            // Remove common prefixes/suffixes and special characters;
            return boneName;
                .Replace("_", "")
                .Replace("-", "")
                .Replace(" ", "")
                .ToLowerInvariant();
        }

        private List<string> ExtractBoneKeywords(string boneName)
        {
            var keywords = new List<string>();
            var parts = boneName.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (part.Length > 2) // Ignore very short parts;
                {
                    keywords.Add(part.ToLowerInvariant());
                }
            }

            return keywords;
        }

        private MappingType DetermineMappingType(Bone sourceBone, Bone targetBone)
        {
            // Determine appropriate mapping type based on bone characteristics;
            if (sourceBone.BoneType == BoneType.EndSite || targetBone.BoneType == BoneType.EndSite)
                return MappingType.PositionOnly;

            if (sourceBone.BoneType == BoneType.Root || targetBone.BoneType == BoneType.Root)
                return MappingType.Full;

            return MappingType.Full;
        }

        private string GetMirroredBoneName(string boneName)
        {
            // Common bone mirroring logic;
            if (boneName.Contains("Left", StringComparison.OrdinalIgnoreCase))
                return boneName.Replace("Left", "Right", StringComparison.OrdinalIgnoreCase);

            if (boneName.Contains("Right", StringComparison.OrdinalIgnoreCase))
                return boneName.Replace("Right", "Left", StringComparison.OrdinalIgnoreCase);

            if (boneName.Contains("L_", StringComparison.OrdinalIgnoreCase))
                return boneName.Replace("L_", "R_", StringComparison.OrdinalIgnoreCase);

            if (boneName.Contains("R_", StringComparison.OrdinalIgnoreCase))
                return boneName.Replace("R_", "L_", StringComparison.OrdinalIgnoreCase);

            return boneName;
        }

        private Vector3 MirrorVector(Vector3 vector)
        {
            return new Vector3(-vector.X, vector.Y, vector.Z);
        }

        private Quaternion MirrorQuaternion(Quaternion quaternion)
        {
            return new Quaternion(-quaternion.X, quaternion.Y, quaternion.Z, -quaternion.W);
        }

        private void MergeSimilarMappings()
        {
            // Implementation for merging similar mappings to reduce redundancy;
        }

        private void ReorderMappingsForPerformance()
        {
            // Reorder mappings to improve cache performance during pose application;
            var orderedMappings = ActiveMappings;
                .OrderBy(m => m.SourceBone)
                .ThenBy(m => m.TargetBone)
                .ToList();

            ActiveMappings.Clear();
            foreach (var mapping in orderedMappings)
            {
                ActiveMappings.Add(mapping);
            }
        }

        private void UpdateValidationResults(MappingValidationResult result)
        {
            ValidationResults.Clear();
            ValidationResults.Add(result);
        }

        private void OnSkeletonChanged()
        {
            // Clear mappings when skeletons change;
            if (!IsAutoUpdateEnabled) return;

            ActiveMappings.Clear();
            ValidationResults.Clear();
        }

        private void OnProfileChanged()
        {
            // Handle profile change events;
        }

        private void OnActiveMappingsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Handle mapping collection changes;
        }

        private void OnBoneMapped(BoneMappingEventArgs e)
        {
            BoneMapped?.Invoke(this, e);
        }

        private void OnSkeletonMapped(SkeletonMappingEventArgs e)
        {
            SkeletonMapped?.Invoke(this, e);
        }

        private void OnMappingValidated(MappingValidationEventArgs e)
        {
            MappingValidated?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum BoneType;
    {
        Root,
        Hip,
        Spine,
        Chest,
        Neck,
        Head,
        Arm,
        Forearm,
        Hand,
        Finger,
        Thigh,
        Calf,
        Foot,
        Toe,
        EndSite,
        Custom;
    }

    public enum MappingType;
    {
        Full,
        PositionOnly,
        RotationOnly,
        ScaleOnly,
        Custom;
    }

    public enum MappingPreset;
    {
        Humanoid,
        Biped,
        Quadruped,
        Custom;
    }

    public enum MappingQuality;
    {
        Low,
        Medium,
        High,
        Ultra;
    }

    public enum MappingSearchMode;
    {
        Exact,
        Smart,
        Loose;
    }

    public enum MappingOperation;
    {
        Added,
        Removed,
        Updated;
    }

    public enum SkeletonType;
    {
        Humanoid,
        Biped,
        Quadruped,
        Avian,
        Custom,
        Unknown;
    }

    public interface IBoneMapper;
    {
        BoneMappingResult AutoMapBones(MappingPreset preset = MappingPreset.Humanoid);
        PoseMappingResult ApplyMappings(SkeletonPose sourcePose);
        BoneMapping FindBestMapping(string sourceBone, MappingSearchMode searchMode = MappingSearchMode.Exact);
    }

    public interface IBoneMappingStrategy;
    {
        List<BoneMapping> GenerateMappings(Skeleton source, Skeleton target, MappingPreset preset);
        float CalculateMappingQuality(Bone source, Bone target);
    }

    public interface IBoneValidator;
    {
        MappingValidationResult ValidateMappings(Skeleton source, Skeleton target, List<BoneMapping> mappings);
    }

    public class Skeleton;
    {
        public string Name { get; set; }
        public SkeletonType SkeletonType { get; set; }
        public Bone RootBone { get; set; }
        public List<Bone> Bones { get; } = new List<Bone>();

        public Bone GetBone(string boneName)
        {
            return Bones.FirstOrDefault(b => b.Name == boneName) ??
                   Bones.SelectMany(b => b.GetDescendants()).FirstOrDefault(b => b.Name == boneName);
        }

        public void CalculateWorldTransforms()
        {
            RootBone?.CalculateWorldTransform();
        }
    }

    public class SkeletonPose;
    {
        public Skeleton Skeleton { get; }
        public Dictionary<string, BonePose> BonePoses { get; } = new Dictionary<string, BonePose>();

        public SkeletonPose(Skeleton skeleton)
        {
            Skeleton = skeleton;
        }

        public BonePose GetBonePose(string boneName)
        {
            return BonePoses.TryGetValue(boneName, out var pose) ? pose : null;
        }

        public void SetBonePose(string boneName, BonePose pose)
        {
            BonePoses[boneName] = pose;
        }
    }

    public class BonePose;
    {
        public BoneTransform Transform { get; set; }

        public BonePose(BoneTransform transform)
        {
            Transform = transform;
        }
    }

    public class BoneMappingProfile;
    {
        public string Name { get; set; }
        public SkeletonType SourceSkeletonType { get; set; }
        public SkeletonType TargetSkeletonType { get; set; }
        public List<BoneMapping> Mappings { get; } = new List<BoneMapping>();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime Modified { get; set; } = DateTime.UtcNow;
    }

    public class BoneMappingRule;
    {
        public string Pattern { get; set; }
        public string Replacement { get; set; }
        public MappingType MappingType { get; set; }
        public float Weight { get; set; } = 1.0f;
    }

    public class MappingConstraint;
    {
        public Vector3 MinPosition { get; set; } = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        public Vector3 MaxPosition { get; set; } = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        public Vector3 MinScale { get; set; } = Vector3.Zero;
        public Vector3 MaxScale { get; set; } = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);

        public BoneTransform ApplyConstraint(BoneTransform transform)
        {
            var result = transform;

            // Apply position constraints;
            result.Position = ClampVector(result.Position, MinPosition, MaxPosition);

            // Apply scale constraints;
            result.Scale = ClampVector(result.Scale, MinScale, MaxScale);

            return result;
        }

        private Vector3 ClampVector(Vector3 value, Vector3 min, Vector3 max)
        {
            return new Vector3(
                Math.Max(min.X, Math.Min(max.X, value.X)),
                Math.Max(min.Y, Math.Min(max.Y, value.Y)),
                Math.Max(min.Z, Math.Min(max.Z, value.Z))
            );
        }
    }

    public class BoneMappingResult;
    {
        public bool Success { get; set; }
        public List<BoneMapping> Mappings { get; set; } = new List<BoneMapping>();
        public int MappedBoneCount { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class PoseMappingResult;
    {
        public bool Success { get; set; }
        public SkeletonPose MappedPose { get; set; }
        public List<BoneMapping> AppliedMappings { get; set; } = new List<BoneMapping>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class MappingValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public int TotalMappings { get; set; }
        public int ValidMappings { get; set; }
    }

    public class MappingStatistics;
    {
        public int TotalMappings { get; set; }
        public int FullMappings { get; set; }
        public int PartialMappings { get; set; }
        public int SourceBoneCount { get; set; }
        public int TargetBoneCount { get; set; }
        public int MappedSourceBones { get; set; }
        public int MappedTargetBones { get; set; }
        public float AverageWeight { get; set; }
    }

    public class BoneMappingEventArgs : EventArgs;
    {
        public BoneMapping Mapping { get; set; }
        public MappingOperation Operation { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SkeletonMappingEventArgs : EventArgs;
    {
        public Skeleton SourceSkeleton { get; set; }
        public Skeleton TargetSkeleton { get; set; }
        public List<BoneMapping> Mappings { get; set; }
        public MappingPreset Preset { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MappingValidationEventArgs : EventArgs;
    {
        public MappingValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public struct Vector3;
    {
        public float X, Y, Z;

        public static Vector3 Zero => new Vector3(0, 0, 0);
        public static Vector3 One => new Vector3(1, 1, 1);

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator *(Vector3 a, Vector3 b) => new Vector3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.X * b, a.Y * b, a.Z * b);
    }

    public struct Quaternion;
    {
        public float X, Y, Z, W;

        public static Quaternion Identity => new Quaternion(0, 0, 0, 1);

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z;
            );
        }

        public Quaternion Inverse()
        {
            return new Quaternion(-X, -Y, -Z, W);
        }
    }

    #endregion;

    #region Default Implementations;

    public class DefaultBoneMappingStrategy : IBoneMappingStrategy;
    {
        public List<BoneMapping> GenerateMappings(Skeleton source, Skeleton target, MappingPreset preset)
        {
            var mappings = new List<BoneMapping>();

            // Implement preset-based mapping logic;
            switch (preset)
            {
                case MappingPreset.Humanoid:
                    mappings.AddRange(GenerateHumanoidMappings(source, target));
                    break;
                case MappingPreset.Biped:
                    mappings.AddRange(GenerateBipedMappings(source, target));
                    break;
                case MappingPreset.Quadruped:
                    mappings.AddRange(GenerateQuadrupedMappings(source, target));
                    break;
            }

            return mappings;
        }

        public float CalculateMappingQuality(Bone source, Bone target)
        {
            // Implement quality calculation based on bone properties;
            return 1.0f; // Simplified;
        }

        private IEnumerable<BoneMapping> GenerateHumanoidMappings(Skeleton source, Skeleton target)
        {
            // Standard humanoid bone mappings;
            var humanoidBones = new[]
            {
                "Hips", "Spine", "Chest", "Neck", "Head",
                "LeftShoulder", "LeftArm", "LeftForearm", "LeftHand",
                "RightShoulder", "RightArm", "RightForearm", "RightHand",
                "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToes",
                "RightUpLeg", "RightLeg", "RightFoot", "RightToes"
            };

            foreach (var boneName in humanoidBones)
            {
                var sourceBone = source.GetBone(boneName);
                var targetBone = target.GetBone(boneName);

                if (sourceBone != null && targetBone != null)
                {
                    yield return new BoneMapping;
                    {
                        SourceBone = boneName,
                        TargetBone = boneName,
                        MappingType = MappingType.Full;
                    };
                }
            }
        }

        private IEnumerable<BoneMapping> GenerateBipedMappings(Skeleton source, Skeleton target)
        {
            // Biped-specific mappings;
            return GenerateHumanoidMappings(source, target); // Similar to humanoid for now;
        }

        private IEnumerable<BoneMapping> GenerateQuadrupedMappings(Skeleton source, Skeleton target)
        {
            // Quadruped-specific mappings;
            var quadrupedBones = new[]
            {
                "Spine", "Spine1", "Spine2", "Neck", "Head",
                "LeftFrontLeg", "LeftFrontKnee", "LeftFrontAnkle", "LeftFrontFoot",
                "RightFrontLeg", "RightFrontKnee", "RightFrontAnkle", "RightFrontFoot",
                "LeftHindLeg", "LeftHindKnee", "LeftHindAnkle", "LeftHindFoot",
                "RightHindLeg", "RightHindKnee", "RightHindAnkle", "RightHindFoot"
            };

            foreach (var boneName in quadrupedBones)
            {
                var sourceBone = source.GetBone(boneName);
                var targetBone = target.GetBone(boneName);

                if (sourceBone != null && targetBone != null)
                {
                    yield return new BoneMapping;
                    {
                        SourceBone = boneName,
                        TargetBone = boneName,
                        MappingType = MappingType.Full;
                    };
                }
            }
        }
    }

    #endregion;

    #region Exceptions;

    public class BoneMappingException : Exception
    {
        public BoneMappingException(string message) : base(message) { }
        public BoneMappingException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class BoneValidationException : BoneMappingException;
    {
        public BoneValidationException(string message) : base(message) { }
    }

    #endregion;
}
