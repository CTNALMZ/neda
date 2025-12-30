using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.CharacterSystems.Common;

namespace NEDA.CharacterSystems.CharacterCreator.AnimationTools.RiggingSystems;
{
    /// <summary>
    /// Bone management system for character rigging and animation;
    /// Handles bone hierarchy, transformations, constraints, and skinning;
    /// </summary>
    public class BoneManager : IDisposable
    {
        #region Nested Types;

        /// <summary>
        /// Bone data structure with transformation and hierarchy information;
        /// </summary>
        public class Bone;
        {
            /// <summary>
            /// Unique identifier for the bone;
            /// </summary>
            public Guid Id { get; }

            /// <summary>
            /// Bone name for identification;
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Bone's local position relative to parent;
            /// </summary>
            public Vector3 LocalPosition { get; set; }

            /// <summary>
            /// Bone's local rotation relative to parent;
            /// </summary>
            public Quaternion LocalRotation { get; set; }

            /// <summary>
            /// Bone's local scale relative to parent;
            /// </summary>
            public Vector3 LocalScale { get; set; }

            /// <summary>
            /// Bone's world position (calculated)
            /// </summary>
            public Vector3 WorldPosition => CalculateWorldPosition();

            /// <summary>
            /// Bone's world rotation (calculated)
            /// </summary>
            public Quaternion WorldRotation => CalculateWorldRotation();

            /// <summary>
            /// Bone's world transformation matrix;
            /// </summary>
            public Matrix4x4 WorldTransform => CalculateWorldTransform();

            /// <summary>
            /// Bone's offset matrix for skinning;
            /// </summary>
            public Matrix4x4 OffsetMatrix { get; set; }

            /// <summary>
            /// Parent bone reference;
            /// </summary>
            public Bone Parent { get; private set; }

            /// <summary>
            /// Child bones collection;
            /// </summary>
            public List<Bone> Children { get; }

            /// <summary>
            /// Bone length for visualization;
            /// </summary>
            public float Length { get; set; }

            /// <summary>
            /// Bone radius for visualization;
            /// </summary>
            public float Radius { get; set; }

            /// <summary>
            /// Bone type classification;
            /// </summary>
            public BoneType Type { get; set; }

            /// <summary>
            /// Bone constraints and limitations;
            /// </summary>
            public BoneConstraints Constraints { get; set; }

            /// <summary>
            /// Vertex weights associated with this bone;
            /// </summary>
            public Dictionary<int, float> VertexWeights { get; }

            /// <summary>
            /// Custom bone properties;
            /// </summary>
            public Dictionary<string, object> Properties { get; }

            /// <summary>
            /// Is bone visible in editor;
            /// </summary>
            public bool IsVisible { get; set; }

            /// <summary>
            /// Is bone selectable;
            /// </summary>
            public bool IsSelectable { get; set; }

            /// <summary>
            /// Bone color for visualization;
            /// </summary>
            public Color Color { get; set; }

            public Bone(string name)
            {
                Id = Guid.NewGuid();
                Name = name ?? throw new ArgumentNullException(nameof(name));
                LocalPosition = Vector3.Zero;
                LocalRotation = Quaternion.Identity;
                LocalScale = Vector3.One;
                OffsetMatrix = Matrix4x4.Identity;
                Children = new List<Bone>();
                VertexWeights = new Dictionary<int, float>();
                Properties = new Dictionary<string, object>();
                Constraints = new BoneConstraints();
                Length = 1.0f;
                Radius = 0.1f;
                Type = BoneType.Regular;
                IsVisible = true;
                IsSelectable = true;
                Color = Color.White;
            }

            /// <summary>
            /// Calculate world position based on hierarchy;
            /// </summary>
            private Vector3 CalculateWorldPosition()
            {
                if (Parent == null)
                    return LocalPosition;

                var parentWorld = Parent.WorldPosition;
                var parentRotation = Parent.WorldRotation;
                var rotatedPosition = Vector3.Transform(LocalPosition, parentRotation);
                return parentWorld + rotatedPosition * Parent.LocalScale;
            }

            /// <summary>
            /// Calculate world rotation based on hierarchy;
            /// </summary>
            private Quaternion CalculateWorldRotation()
            {
                if (Parent == null)
                    return LocalRotation;

                return Parent.WorldRotation * LocalRotation;
            }

            /// <summary>
            /// Calculate world transformation matrix;
            /// </summary>
            private Matrix4x4 CalculateWorldTransform()
            {
                var scale = Matrix4x4.CreateScale(LocalScale);
                var rotation = Matrix4x4.CreateFromQuaternion(LocalRotation);
                var translation = Matrix4x4.CreateTranslation(LocalPosition);
                var localTransform = scale * rotation * translation;

                if (Parent == null)
                    return localTransform;

                return localTransform * Parent.WorldTransform;
            }

            /// <summary>
            /// Add child bone;
            /// </summary>
            public void AddChild(Bone child)
            {
                if (child == null)
                    throw new ArgumentNullException(nameof(child));

                if (child.Parent != null)
                    child.Parent.RemoveChild(child);

                Children.Add(child);
                child.Parent = this;
            }

            /// <summary>
            /// Remove child bone;
            /// </summary>
            public bool RemoveChild(Bone child)
            {
                if (child == null)
                    return false;

                var removed = Children.Remove(child);
                if (removed)
                    child.Parent = null;

                return removed;
            }

            /// <summary>
            /// Get bone depth in hierarchy;
            /// </summary>
            public int GetDepth()
            {
                int depth = 0;
                var current = Parent;
                while (current != null)
                {
                    depth++;
                    current = current.Parent;
                }
                return depth;
            }

            /// <summary>
            /// Check if bone is ancestor of another bone;
            /// </summary>
            public bool IsAncestorOf(Bone bone)
            {
                var current = bone?.Parent;
                while (current != null)
                {
                    if (current == this)
                        return true;
                    current = current.Parent;
                }
                return false;
            }

            /// <summary>
            /// Get all descendant bones;
            /// </summary>
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

            /// <summary>
            /// Set local transformation;
            /// </summary>
            public void SetLocalTransform(Vector3 position, Quaternion rotation, Vector3 scale)
            {
                LocalPosition = Constraints.ClampPosition(position);
                LocalRotation = Constraints.ClampRotation(rotation);
                LocalScale = Constraints.ClampScale(scale);
            }

            /// <summary>
            /// Set world transformation;
            /// </summary>
            public void SetWorldTransform(Vector3 position, Quaternion rotation, Vector3 scale)
            {
                if (Parent == null)
                {
                    SetLocalTransform(position, rotation, scale);
                    return;
                }

                var parentInverse = Matrix4x4.Invert(Parent.WorldTransform, out var inverse) ? inverse : Matrix4x4.Identity;
                var worldMatrix = Matrix4x4.CreateScale(scale) *
                                 Matrix4x4.CreateFromQuaternion(rotation) *
                                 Matrix4x4.CreateTranslation(position);
                var localMatrix = worldMatrix * parentInverse;

                Matrix4x4.Decompose(localMatrix, out var localScale, out var localRotation, out var localPosition);
                SetLocalTransform(localPosition, localRotation, localScale);
            }

            /// <summary>
            /// Get bone transformation for skinning;
            /// </summary>
            public Matrix4x4 GetSkinningTransform()
            {
                return OffsetMatrix * WorldTransform;
            }

            /// <summary>
            /// Calculate bone direction vector;
            /// </summary>
            public Vector3 GetDirection()
            {
                if (Children.Count == 0)
                    return Vector3.UnitY; // Default direction;

                // Calculate average direction to children;
                var direction = Vector3.Zero;
                foreach (var child in Children)
                {
                    direction += Vector3.Normalize(child.WorldPosition - WorldPosition);
                }
                return Vector3.Normalize(direction / Children.Count);
            }
        }

        /// <summary>
        /// Bone type classification;
        /// </summary>
        public enum BoneType;
        {
            Regular,
            Root,
            Spine,
            Neck,
            Head,
            Shoulder,
            Arm,
            Forearm,
            Hand,
            Finger,
            Thigh,
            Shin,
            Foot,
            Toe,
            Tail,
            Wing,
            Prop,
            Control,
            IkTarget,
            Facial,
            Eye,
            Jaw,
            Tongue,
            Special;
        }

        /// <summary>
        /// Bone constraints for animation limits;
        /// </summary>
        public class BoneConstraints;
        {
            /// <summary>
            /// Minimum position limits;
            /// </summary>
            public Vector3 MinPosition { get; set; }

            /// <summary>
            /// Maximum position limits;
            /// </summary>
            public Vector3 MaxPosition { get; set; }

            /// <summary>
            /// Minimum rotation limits in degrees;
            /// </summary>
            public Vector3 MinRotation { get; set; }

            /// <summary>
            /// Maximum rotation limits in degrees;
            /// </summary>
            public Vector3 MaxRotation { get; set; }

            /// <summary>
            /// Minimum scale limits;
            /// </summary>
            public Vector3 MinScale { get; set; }

            /// <summary>
            /// Maximum scale limits;
            /// </summary>
            public Vector3 MaxScale { get; set; }

            /// <summary>
            /// Enable position constraints;
            /// </summary>
            public bool EnablePositionConstraints { get; set; }

            /// <summary>
            /// Enable rotation constraints;
            /// </summary>
            public bool EnableRotationConstraints { get; set; }

            /// <summary>
            /// Enable scale constraints;
            /// </summary>
            public bool EnableScaleConstraints { get; set; }

            /// <summary>
            /// Spring stiffness for physics;
            /// </summary>
            public float SpringStiffness { get; set; }

            /// <summary>
            /// Damping factor for physics;
            /// </summary>
            public float DampingFactor { get; set; }

            public BoneConstraints()
            {
                MinPosition = new Vector3(-10, -10, -10);
                MaxPosition = new Vector3(10, 10, 10);
                MinRotation = new Vector3(-180, -180, -180);
                MaxRotation = new Vector3(180, 180, 180);
                MinScale = new Vector3(0.1f, 0.1f, 0.1f);
                MaxScale = new Vector3(10, 10, 10);
                EnablePositionConstraints = false;
                EnableRotationConstraints = false;
                EnableScaleConstraints = false;
                SpringStiffness = 100.0f;
                DampingFactor = 10.0f;
            }

            /// <summary>
            /// Clamp position within constraints;
            /// </summary>
            public Vector3 ClampPosition(Vector3 position)
            {
                if (!EnablePositionConstraints)
                    return position;

                return new Vector3(
                    Math.Clamp(position.X, MinPosition.X, MaxPosition.X),
                    Math.Clamp(position.Y, MinPosition.Y, MaxPosition.Y),
                    Math.Clamp(position.Z, MinPosition.Z, MaxPosition.Z)
                );
            }

            /// <summary>
            /// Clamp rotation within constraints;
            /// </summary>
            public Quaternion ClampRotation(Quaternion rotation)
            {
                if (!EnableRotationConstraints)
                    return rotation;

                var euler = rotation.ToEulerAngles();
                var clampedEuler = new Vector3(
                    Math.Clamp(euler.X, MinRotation.X, MaxRotation.X),
                    Math.Clamp(euler.Y, MinRotation.Y, MaxRotation.Y),
                    Math.Clamp(euler.Z, MinRotation.Z, MaxRotation.Z)
                );

                return Quaternion.CreateFromYawPitchRoll(
                    MathHelper.DegreesToRadians(clampedEuler.Y),
                    MathHelper.DegreesToRadians(clampedEuler.X),
                    MathHelper.DegreesToRadians(clampedEuler.Z)
                );
            }

            /// <summary>
            /// Clamp scale within constraints;
            /// </summary>
            public Vector3 ClampScale(Vector3 scale)
            {
                if (!EnableScaleConstraints)
                    return scale;

                return new Vector3(
                    Math.Clamp(scale.X, MinScale.X, MaxScale.X),
                    Math.Clamp(scale.Y, MinScale.Y, MaxScale.Y),
                    Math.Clamp(scale.Z, MinScale.Z, MaxScale.Z)
                );
            }
        }

        /// <summary>
        /// Bone selection event arguments;
        /// </summary>
        public class BoneSelectionChangedEventArgs : EventArgs;
        {
            public Bone SelectedBone { get; }
            public Bone PreviousBone { get; }

            public BoneSelectionChangedEventArgs(Bone selected, Bone previous)
            {
                SelectedBone = selected;
                PreviousBone = previous;
            }
        }

        /// <summary>
        /// Bone transformation event arguments;
        /// </summary>
        public class BoneTransformedEventArgs : EventArgs;
        {
            public Bone TransformedBone { get; }
            public TransformationType TransformationType { get; }

            public BoneTransformedEventArgs(Bone bone, TransformationType type)
            {
                TransformedBone = bone;
                TransformationType = type;
            }
        }

        public enum TransformationType;
        {
            Position,
            Rotation,
            Scale,
            All;
        }

        #endregion;

        #region Fields;

        private readonly Dictionary<Guid, Bone> _bones;
        private readonly Dictionary<string, Guid> _boneNameToId;
        private Bone _rootBone;
        private Bone _selectedBone;
        private readonly object _syncLock = new object();
        private bool _isDisposed;

        // Bone layers for organization;
        private readonly Dictionary<string, List<Guid>> _boneLayers;

        // Bone selection sets;
        private readonly Dictionary<string, HashSet<Guid>> _selectionSets;

        #endregion;

        #region Properties;

        /// <summary>
        /// Root bone of the skeleton;
        /// </summary>
        public Bone RootBone;
        {
            get => _rootBone;
            set;
            {
                if (_rootBone != value)
                {
                    _rootBone = value;
                    OnRootBoneChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Currently selected bone;
        /// </summary>
        public Bone SelectedBone;
        {
            get => _selectedBone;
            set;
            {
                if (_selectedBone != value)
                {
                    var previous = _selectedBone;
                    _selectedBone = value;
                    OnBoneSelectionChanged?.Invoke(this, new BoneSelectionChangedEventArgs(value, previous));
                }
            }
        }

        /// <summary>
        /// Total bone count;
        /// </summary>
        public int BoneCount => _bones.Count;

        /// <summary>
        /// All bones in the manager;
        /// </summary>
        public IEnumerable<Bone> AllBones => _bones.Values;

        /// <summary>
        /// Bone names for quick lookup;
        /// </summary>
        public IEnumerable<string> BoneNames => _boneNameToId.Keys;

        /// <summary>
        /// Is manager initialized;
        /// </summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Enable auto normalization of bone rotations;
        /// </summary>
        public bool AutoNormalizeRotations { get; set; } = true;

        /// <summary>
        /// Enable bone transformation notifications;
        /// </summary>
        public bool EnableTransformationEvents { get; set; } = true;

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when bone is selected;
        /// </summary>
        public event EventHandler<BoneSelectionChangedEventArgs> OnBoneSelectionChanged;

        /// <summary>
        /// Event raised when bone is transformed;
        /// </summary>
        public event EventHandler<BoneTransformedEventArgs> OnBoneTransformed;

        /// <summary>
        /// Event raised when bone is added;
        /// </summary>
        public event EventHandler<Bone> OnBoneAdded;

        /// <summary>
        /// Event raised when bone is removed;
        /// </summary>
        public event EventHandler<Bone> OnBoneRemoved;

        /// <summary>
        /// Event raised when root bone changes;
        /// </summary>
        public event EventHandler OnRootBoneChanged;

        /// <summary>
        /// Event raised when skeleton hierarchy changes;
        /// </summary>
        public event EventHandler OnSkeletonChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initialize BoneManager;
        /// </summary>
        public BoneManager()
        {
            _bones = new Dictionary<Guid, Bone>();
            _boneNameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            _boneLayers = new Dictionary<string, List<Guid>>();
            _selectionSets = new Dictionary<string, HashSet<Guid>>();
            IsInitialized = true;
        }

        #endregion;

        #region Bone Management;

        /// <summary>
        /// Create and add a new bone;
        /// </summary>
        public Bone CreateBone(string name, Bone parent = null)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Bone name cannot be null or empty", nameof(name));

            lock (_syncLock)
            {
                if (_boneNameToId.ContainsKey(name))
                    throw new InvalidOperationException($"Bone with name '{name}' already exists");

                var bone = new Bone(name);
                _bones[bone.Id] = bone;
                _boneNameToId[name] = bone.Id;

                if (parent != null)
                {
                    parent.AddChild(bone);
                }
                else if (RootBone == null)
                {
                    RootBone = bone;
                }

                OnBoneAdded?.Invoke(this, bone);
                OnSkeletonChanged?.Invoke(this, EventArgs.Empty);

                return bone;
            }
        }

        /// <summary>
        /// Add existing bone to manager;
        /// </summary>
        public void AddBone(Bone bone, Bone parent = null)
        {
            ValidateNotDisposed();

            if (bone == null)
                throw new ArgumentNullException(nameof(bone));

            lock (_syncLock)
            {
                if (_bones.ContainsKey(bone.Id))
                    throw new InvalidOperationException("Bone already exists in manager");

                if (_boneNameToId.ContainsKey(bone.Name))
                    throw new InvalidOperationException($"Bone with name '{bone.Name}' already exists");

                _bones[bone.Id] = bone;
                _boneNameToId[bone.Name] = bone.Id;

                if (parent != null)
                {
                    parent.AddChild(bone);
                }
                else if (RootBone == null)
                {
                    RootBone = bone;
                }

                OnBoneAdded?.Invoke(this, bone);
                OnSkeletonChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Remove bone from manager;
        /// </summary>
        public bool RemoveBone(Guid boneId, bool removeChildren = false)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return false;

                if (removeChildren)
                {
                    // Remove all descendants;
                    var descendants = bone.GetDescendants().ToList();
                    foreach (var descendant in descendants)
                    {
                        RemoveBoneInternal(descendant);
                    }
                }
                else;
                {
                    // Re-parent children to bone's parent;
                    var parent = bone.Parent;
                    var children = bone.Children.ToList();
                    foreach (var child in children)
                    {
                        if (parent != null)
                            parent.AddChild(child);
                        else;
                            child.Parent = null;
                    }
                }

                RemoveBoneInternal(bone);

                if (SelectedBone?.Id == boneId)
                    SelectedBone = null;

                if (RootBone?.Id == boneId)
                    RootBone = _bones.Values.FirstOrDefault(b => b.Parent == null);

                OnSkeletonChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }

        /// <summary>
        /// Internal bone removal;
        /// </summary>
        private void RemoveBoneInternal(Bone bone)
        {
            bone.Parent?.RemoveChild(bone);
            _bones.Remove(bone.Id);
            _boneNameToId.Remove(bone.Name);

            // Remove from layers;
            foreach (var layer in _boneLayers.Values)
            {
                layer.Remove(bone.Id);
            }

            // Remove from selection sets;
            foreach (var set in _selectionSets.Values)
            {
                set.Remove(bone.Id);
            }

            OnBoneRemoved?.Invoke(this, bone);
        }

        /// <summary>
        /// Get bone by ID;
        /// </summary>
        public Bone GetBone(Guid boneId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                _bones.TryGetValue(boneId, out var bone);
                return bone;
            }
        }

        /// <summary>
        /// Get bone by name;
        /// </summary>
        public Bone GetBone(string name)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(name))
                return null;

            lock (_syncLock)
            {
                if (_boneNameToId.TryGetValue(name, out var boneId))
                    return GetBone(boneId);

                return null;
            }
        }

        /// <summary>
        /// Find bones by predicate;
        /// </summary>
        public IEnumerable<Bone> FindBones(Func<Bone, bool> predicate)
        {
            ValidateNotDisposed();

            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            lock (_syncLock)
            {
                return _bones.Values.Where(predicate).ToList();
            }
        }

        /// <summary>
        /// Find bone by partial name;
        /// </summary>
        public IEnumerable<Bone> FindBonesByName(string partialName)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(partialName))
                return Enumerable.Empty<Bone>();

            lock (_syncLock)
            {
                return _bones.Values;
                    .Where(b => b.Name.IndexOf(partialName, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }
        }

        /// <summary>
        /// Get bone by type;
        /// </summary>
        public IEnumerable<Bone> GetBonesByType(BoneType type)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _bones.Values.Where(b => b.Type == type).ToList();
            }
        }

        /// <summary>
        /// Rename bone;
        /// </summary>
        public bool RenameBone(Guid boneId, string newName)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("New name cannot be null or empty", nameof(newName));

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return false;

                if (_boneNameToId.ContainsKey(newName) && _boneNameToId[newName] != boneId)
                    throw new InvalidOperationException($"Bone with name '{newName}' already exists");

                _boneNameToId.Remove(bone.Name);
                bone.Name = newName;
                _boneNameToId[newName] = boneId;

                return true;
            }
        }

        /// <summary>
        /// Duplicate bone with hierarchy;
        /// </summary>
        public Bone DuplicateBone(Guid boneId, string newName = null)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var original))
                    throw new ArgumentException($"Bone with ID {boneId} not found", nameof(boneId));

                var name = newName ?? $"{original.Name}_Copy";
                var duplicate = CreateBone(name, original.Parent);

                // Copy properties;
                duplicate.LocalPosition = original.LocalPosition;
                duplicate.LocalRotation = original.LocalRotation;
                duplicate.LocalScale = original.LocalScale;
                duplicate.OffsetMatrix = original.OffsetMatrix;
                duplicate.Length = original.Length;
                duplicate.Radius = original.Radius;
                duplicate.Type = original.Type;
                duplicate.Color = original.Color;
                duplicate.Constraints = new BoneConstraints;
                {
                    MinPosition = original.Constraints.MinPosition,
                    MaxPosition = original.Constraints.MaxPosition,
                    MinRotation = original.Constraints.MinRotation,
                    MaxRotation = original.Constraints.MaxRotation,
                    MinScale = original.Constraints.MinScale,
                    MaxScale = original.Constraints.MaxScale,
                    EnablePositionConstraints = original.Constraints.EnablePositionConstraints,
                    EnableRotationConstraints = original.Constraints.EnableRotationConstraints,
                    EnableScaleConstraints = original.Constraints.EnableScaleConstraints,
                    SpringStiffness = original.Constraints.SpringStiffness,
                    DampingFactor = original.Constraints.DampingFactor;
                };

                // Copy vertex weights;
                foreach (var kvp in original.VertexWeights)
                {
                    duplicate.VertexWeights[kvp.Key] = kvp.Value;
                }

                // Copy custom properties;
                foreach (var kvp in original.Properties)
                {
                    duplicate.Properties[kvp.Key] = kvp.Value;
                }

                // Recursively duplicate children;
                foreach (var child in original.Children)
                {
                    DuplicateBoneHierarchy(child, duplicate);
                }

                return duplicate;
            }
        }

        /// <summary>
        /// Recursively duplicate bone hierarchy;
        /// </summary>
        private void DuplicateBoneHierarchy(Bone original, Bone newParent)
        {
            var duplicate = CreateBone(original.Name, newParent);

            // Copy properties (same as above)
            duplicate.LocalPosition = original.LocalPosition;
            duplicate.LocalRotation = original.LocalRotation;
            duplicate.LocalScale = original.LocalScale;
            duplicate.OffsetMatrix = original.OffsetMatrix;
            duplicate.Length = original.Length;
            duplicate.Radius = original.Radius;
            duplicate.Type = original.Type;
            duplicate.Color = original.Color;
            duplicate.Constraints = new BoneConstraints;
            {
                MinPosition = original.Constraints.MinPosition,
                MaxPosition = original.Constraints.MaxPosition,
                MinRotation = original.Constraints.MinRotation,
                MaxRotation = original.Constraints.MaxRotation,
                MinScale = original.Constraints.MinScale,
                MaxScale = original.Constraints.MaxScale,
                EnablePositionConstraints = original.Constraints.EnablePositionConstraints,
                EnableRotationConstraints = original.Constraints.EnableRotationConstraints,
                EnableScaleConstraints = original.Constraints.EnableScaleConstraints,
                SpringStiffness = original.Constraints.SpringStiffness,
                DampingFactor = original.Constraints.DampingFactor;
            };

            foreach (var kvp in original.VertexWeights)
            {
                duplicate.VertexWeights[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in original.Properties)
            {
                duplicate.Properties[kvp.Key] = kvp.Value;
            }

            foreach (var child in original.Children)
            {
                DuplicateBoneHierarchy(child, duplicate);
            }
        }

        #endregion;

        #region Bone Transformation;

        /// <summary>
        /// Set bone local position;
        /// </summary>
        public void SetBoneLocalPosition(Guid boneId, Vector3 position)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                bone.LocalPosition = bone.Constraints.ClampPosition(position);
                NotifyBoneTransformed(bone, TransformationType.Position);
            }
        }

        /// <summary>
        /// Set bone local rotation;
        /// </summary>
        public void SetBoneLocalRotation(Guid boneId, Quaternion rotation)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                if (AutoNormalizeRotations)
                    rotation = Quaternion.Normalize(rotation);

                bone.LocalRotation = bone.Constraints.ClampRotation(rotation);
                NotifyBoneTransformed(bone, TransformationType.Rotation);
            }
        }

        /// <summary>
        /// Set bone local scale;
        /// </summary>
        public void SetBoneLocalScale(Guid boneId, Vector3 scale)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                bone.LocalScale = bone.Constraints.ClampScale(scale);
                NotifyBoneTransformed(bone, TransformationType.Scale);
            }
        }

        /// <summary>
        /// Set bone local transformation;
        /// </summary>
        public void SetBoneLocalTransform(Guid boneId, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                bone.SetLocalTransform(position, rotation, scale);
                NotifyBoneTransformed(bone, TransformationType.All);
            }
        }

        /// <summary>
        /// Set bone world position;
        /// </summary>
        public void SetBoneWorldPosition(Guid boneId, Vector3 position)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                var parent = bone.Parent;
                if (parent == null)
                {
                    SetBoneLocalPosition(boneId, position);
                    return;
                }

                var parentInverse = Matrix4x4.Invert(parent.WorldTransform, out var inverse) ? inverse : Matrix4x4.Identity;
                var localPosition = Vector3.Transform(position, parentInverse);
                SetBoneLocalPosition(boneId, localPosition);
            }
        }

        /// <summary>
        /// Rotate bone around axis;
        /// </summary>
        public void RotateBone(Guid boneId, Vector3 axis, float angle)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                var rotation = Quaternion.CreateFromAxisAngle(axis, angle);
                var newRotation = bone.LocalRotation * rotation;

                if (AutoNormalizeRotations)
                    newRotation = Quaternion.Normalize(newRotation);

                bone.LocalRotation = bone.Constraints.ClampRotation(newRotation);
                NotifyBoneTransformed(bone, TransformationType.Rotation);
            }
        }

        /// <summary>
        /// Translate bone relative to current position;
        /// </summary>
        public void TranslateBone(Guid boneId, Vector3 translation)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                var newPosition = bone.LocalPosition + translation;
                bone.LocalPosition = bone.Constraints.ClampPosition(newPosition);
                NotifyBoneTransformed(bone, TransformationType.Position);
            }
        }

        /// <summary>
        /// Scale bone relative to current scale;
        /// </summary>
        public void ScaleBone(Guid boneId, Vector3 scaleFactor)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                var newScale = new Vector3(
                    bone.LocalScale.X * scaleFactor.X,
                    bone.LocalScale.Y * scaleFactor.Y,
                    bone.LocalScale.Z * scaleFactor.Z;
                );

                bone.LocalScale = bone.Constraints.ClampScale(newScale);
                NotifyBoneTransformed(bone, TransformationType.Scale);
            }
        }

        /// <summary>
        /// Reset bone to default transform;
        /// </summary>
        public void ResetBoneTransform(Guid boneId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                bone.LocalPosition = Vector3.Zero;
                bone.LocalRotation = Quaternion.Identity;
                bone.LocalScale = Vector3.One;
                NotifyBoneTransformed(bone, TransformationType.All);
            }
        }

        /// <summary>
        /// Align bone to direction;
        /// </summary>
        public void AlignBoneToDirection(Guid boneId, Vector3 direction)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                direction = Vector3.Normalize(direction);
                var currentDirection = bone.GetDirection();

                if (Vector3.Dot(currentDirection, direction) > 0.999f)
                    return; // Already aligned;

                var rotation = Quaternion.CreateFromTwoVectors(currentDirection, direction);
                var newRotation = bone.LocalRotation * rotation;

                if (AutoNormalizeRotations)
                    newRotation = Quaternion.Normalize(newRotation);

                bone.LocalRotation = bone.Constraints.ClampRotation(newRotation);
                NotifyBoneTransformed(bone, TransformationType.Rotation);
            }
        }

        /// <summary>
        /// Look at target position;
        /// </summary>
        public void LookAt(Guid boneId, Vector3 targetPosition, Vector3 upVector)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                var direction = Vector3.Normalize(targetPosition - bone.WorldPosition);
                var rotation = Quaternion.CreateFromRotationMatrix(
                    Matrix4x4.CreateLookAt(bone.WorldPosition, targetPosition, upVector)
                );

                bone.LocalRotation = bone.Constraints.ClampRotation(rotation);
                NotifyBoneTransformed(bone, TransformationType.Rotation);
            }
        }

        /// <summary>
        /// Notify about bone transformation;
        /// </summary>
        private void NotifyBoneTransformed(Bone bone, TransformationType type)
        {
            if (EnableTransformationEvents)
            {
                OnBoneTransformed?.Invoke(this, new BoneTransformedEventArgs(bone, type));
            }
        }

        #endregion;

        #region Hierarchy Operations;

        /// <summary>
        /// Reparent bone;
        /// </summary>
        public bool ReparentBone(Guid boneId, Guid newParentId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return false;

                if (!_bones.TryGetValue(newParentId, out var newParent))
                    return false;

                // Check for circular dependency;
                if (newParent.IsAncestorOf(bone))
                    throw new InvalidOperationException("Cannot create circular bone hierarchy");

                bone.Parent?.RemoveChild(bone);
                newParent.AddChild(bone);

                OnSkeletonChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }

        /// <summary>
        /// Get bone hierarchy as tree;
        /// </summary>
        public List<Bone> GetBoneHierarchy()
        {
            ValidateNotDisposed();

            var hierarchy = new List<Bone>();

            if (RootBone != null)
            {
                BuildHierarchyList(RootBone, hierarchy, 0);
            }

            return hierarchy;
        }

        /// <summary>
        /// Recursively build hierarchy list;
        /// </summary>
        private void BuildHierarchyList(Bone bone, List<Bone> list, int depth)
        {
            list.Add(bone);

            foreach (var child in bone.Children)
            {
                BuildHierarchyList(child, list, depth + 1);
            }
        }

        /// <summary>
        /// Get bone path from root;
        /// </summary>
        public List<Bone> GetBonePath(Guid boneId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return new List<Bone>();

                var path = new List<Bone>();
                var current = bone;

                while (current != null)
                {
                    path.Insert(0, current);
                    current = current.Parent;
                }

                return path;
            }
        }

        /// <summary>
        /// Get all leaf bones (bones without children)
        /// </summary>
        public IEnumerable<Bone> GetLeafBones()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _bones.Values.Where(b => b.Children.Count == 0).ToList();
            }
        }

        /// <summary>
        /// Get bone depth in hierarchy;
        /// </summary>
        public int GetBoneDepth(Guid boneId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return -1;

                return bone.GetDepth();
            }
        }

        /// <summary>
        /// Check if bone is ancestor of another;
        /// </summary>
        public bool IsAncestor(Guid ancestorId, Guid descendantId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(ancestorId, out var ancestor) ||
                    !_bones.TryGetValue(descendantId, out var descendant))
                    return false;

                return ancestor.IsAncestorOf(descendant);
            }
        }

        /// <summary>
        /// Sort bones by hierarchy depth;
        /// </summary>
        public List<Bone> GetSortedBones()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _bones.Values;
                    .OrderBy(b => b.GetDepth())
                    .ThenBy(b => b.Name)
                    .ToList();
            }
        }

        #endregion;

        #region Bone Layers;

        /// <summary>
        /// Create bone layer;
        /// </summary>
        public bool CreateLayer(string layerName)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(layerName))
                throw new ArgumentException("Layer name cannot be null or empty", nameof(layerName));

            lock (_syncLock)
            {
                if (_boneLayers.ContainsKey(layerName))
                    return false;

                _boneLayers[layerName] = new List<Guid>();
                return true;
            }
        }

        /// <summary>
        /// Add bone to layer;
        /// </summary>
        public bool AddBoneToLayer(Guid boneId, string layerName)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.ContainsKey(boneId) || !_boneLayers.ContainsKey(layerName))
                    return false;

                var layer = _boneLayers[layerName];
                if (!layer.Contains(boneId))
                {
                    layer.Add(boneId);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Remove bone from layer;
        /// </summary>
        public bool RemoveBoneFromLayer(Guid boneId, string layerName)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_boneLayers.TryGetValue(layerName, out var layer))
                    return false;

                return layer.Remove(boneId);
            }
        }

        /// <summary>
        /// Get bones in layer;
        /// </summary>
        public IEnumerable<Bone> GetBonesInLayer(string layerName)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_boneLayers.TryGetValue(layerName, out var boneIds))
                    return Enumerable.Empty<Bone>();

                return boneIds.Select(id => GetBone(id)).Where(b => b != null);
            }
        }

        /// <summary>
        /// Get layers containing bone;
        /// </summary>
        public IEnumerable<string> GetBoneLayers(Guid boneId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _boneLayers;
                    .Where(kvp => kvp.Value.Contains(boneId))
                    .Select(kvp => kvp.Key);
            }
        }

        /// <summary>
        /// Remove layer;
        /// </summary>
        public bool RemoveLayer(string layerName)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _boneLayers.Remove(layerName);
            }
        }

        #endregion;

        #region Selection Sets;

        /// <summary>
        /// Create selection set;
        /// </summary>
        public bool CreateSelectionSet(string setName)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(setName))
                throw new ArgumentException("Set name cannot be null or empty", nameof(setName));

            lock (_syncLock)
            {
                if (_selectionSets.ContainsKey(setName))
                    return false;

                _selectionSets[setName] = new HashSet<Guid>();
                return true;
            }
        }

        /// <summary>
        /// Add bone to selection set;
        /// </summary>
        public bool AddToSelectionSet(Guid boneId, string setName)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.ContainsKey(boneId) || !_selectionSets.ContainsKey(setName))
                    return false;

                return _selectionSets[setName].Add(boneId);
            }
        }

        /// <summary>
        /// Get selection set bones;
        /// </summary>
        public IEnumerable<Bone> GetSelectionSet(string setName)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_selectionSets.TryGetValue(setName, out var boneIds))
                    return Enumerable.Empty<Bone>();

                return boneIds.Select(id => GetBone(id)).Where(b => b != null);
            }
        }

        /// <summary>
        /// Select all bones in set;
        /// </summary>
        public void SelectSet(string setName)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_selectionSets.TryGetValue(setName, out var boneIds))
                    return;

                // For now, just select the first bone in the set;
                // In a real implementation, you might want multi-selection;
                var firstBoneId = boneIds.FirstOrDefault();
                if (firstBoneId != Guid.Empty)
                {
                    SelectedBone = GetBone(firstBoneId);
                }
            }
        }

        #endregion;

        #region Skinning Operations;

        /// <summary>
        /// Calculate offset matrices for skinning;
        /// </summary>
        public void CalculateOffsetMatrices()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                foreach (var bone in _bones.Values)
                {
                    if (Matrix4x4.Invert(bone.WorldTransform, out var inverse))
                    {
                        bone.OffsetMatrix = inverse;
                    }
                    else;
                    {
                        bone.OffsetMatrix = Matrix4x4.Identity;
                    }
                }
            }
        }

        /// <summary>
        /// Assign vertex weight to bone;
        /// </summary>
        public void AssignVertexWeight(Guid boneId, int vertexIndex, float weight)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (!_bones.TryGetValue(boneId, out var bone))
                    return;

                if (weight <= 0)
                {
                    bone.VertexWeights.Remove(vertexIndex);
                }
                else;
                {
                    bone.VertexWeights[vertexIndex] = Math.Clamp(weight, 0, 1);
                }
            }
        }

        /// <summary>
        /// Normalize vertex weights for a vertex;
        /// </summary>
        public void NormalizeVertexWeights(int vertexIndex)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                var bonesWithWeight = _bones.Values;
                    .Where(b => b.VertexWeights.ContainsKey(vertexIndex))
                    .ToList();

                var totalWeight = bonesWithWeight.Sum(b => b.VertexWeights[vertexIndex]);

                if (totalWeight <= 0)
                    return;

                foreach (var bone in bonesWithWeight)
                {
                    bone.VertexWeights[vertexIndex] /= totalWeight;
                }
            }
        }

        /// <summary>
        /// Get bones influencing a vertex;
        /// </summary>
        public Dictionary<Bone, float> GetVertexInfluences(int vertexIndex)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _bones.Values;
                    .Where(b => b.VertexWeights.ContainsKey(vertexIndex))
                    .ToDictionary(b => b, b => b.VertexWeights[vertexIndex]);
            }
        }

        #endregion;

        #region Serialization;

        /// <summary>
        /// Serialize bone data to JSON;
        /// </summary>
        public string SerializeToJson()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                var data = new BoneManagerData;
                {
                    Bones = _bones.Values.Select(b => new BoneData(b)).ToList(),
                    RootBoneId = RootBone?.Id,
                    SelectedBoneId = SelectedBone?.Id;
                };

                return JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new Vector3Converter(), new QuaternionConverter(), new Matrix4x4Converter() }
                });
            }
        }

        /// <summary>
        /// Deserialize from JSON;
        /// </summary>
        public void DeserializeFromJson(string json)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                Clear();

                var options = new JsonSerializerOptions
                {
                    Converters = { new Vector3Converter(), new QuaternionConverter(), new Matrix4x4Converter() }
                };

                var data = JsonSerializer.Deserialize<BoneManagerData>(json, options);
                if (data == null)
                    throw new InvalidOperationException("Failed to deserialize bone data");

                // First create all bones;
                var boneMap = new Dictionary<Guid, Bone>();
                foreach (var boneData in data.Bones)
                {
                    var bone = new Bone(boneData.Name)
                    {
                        Id = boneData.Id,
                        LocalPosition = boneData.LocalPosition,
                        LocalRotation = boneData.LocalRotation,
                        LocalScale = boneData.LocalScale,
                        OffsetMatrix = boneData.OffsetMatrix,
                        Length = boneData.Length,
                        Radius = boneData.Radius,
                        Type = boneData.Type,
                        Color = boneData.Color,
                        IsVisible = boneData.IsVisible,
                        IsSelectable = boneData.IsSelectable;
                    };

                    // Copy constraints;
                    bone.Constraints = new BoneConstraints;
                    {
                        MinPosition = boneData.Constraints.MinPosition,
                        MaxPosition = boneData.Constraints.MaxPosition,
                        MinRotation = boneData.Constraints.MinRotation,
                        MaxRotation = boneData.Constraints.MaxRotation,
                        MinScale = boneData.Constraints.MinScale,
                        MaxScale = boneData.Constraints.MaxScale,
                        EnablePositionConstraints = boneData.Constraints.EnablePositionConstraints,
                        EnableRotationConstraints = boneData.Constraints.EnableRotationConstraints,
                        EnableScaleConstraints = boneData.Constraints.EnableScaleConstraints,
                        SpringStiffness = boneData.Constraints.SpringStiffness,
                        DampingFactor = boneData.Constraints.DampingFactor;
                    };

                    // Copy vertex weights;
                    foreach (var weight in boneData.VertexWeights)
                    {
                        bone.VertexWeights[weight.VertexIndex] = weight.Weight;
                    }

                    // Copy properties;
                    foreach (var prop in boneData.Properties)
                    {
                        bone.Properties[prop.Key] = prop.Value;
                    }

                    _bones[bone.Id] = bone;
                    _boneNameToId[bone.Name] = bone.Id;
                    boneMap[bone.Id] = bone;
                }

                // Then set up hierarchy;
                foreach (var boneData in data.Bones)
                {
                    var bone = boneMap[boneData.Id];

                    if (boneData.ParentId.HasValue && boneMap.TryGetValue(boneData.ParentId.Value, out var parent))
                    {
                        parent.AddChild(bone);
                    }

                    foreach (var childId in boneData.ChildrenIds)
                    {
                        if (boneMap.TryGetValue(childId, out var child))
                        {
                            bone.AddChild(child);
                        }
                    }
                }

                // Set root and selected bones;
                if (data.RootBoneId.HasValue && boneMap.TryGetValue(data.RootBoneId.Value, out var root))
                {
                    RootBone = root;
                }

                if (data.SelectedBoneId.HasValue && boneMap.TryGetValue(data.SelectedBoneId.Value, out var selected))
                {
                    SelectedBone = selected;
                }

                OnSkeletonChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Bone data for serialization;
        /// </summary>
        private class BoneManagerData;
        {
            public List<BoneData> Bones { get; set; }
            public Guid? RootBoneId { get; set; }
            public Guid? SelectedBoneId { get; set; }
        }

        /// <summary>
        /// Individual bone data for serialization;
        /// </summary>
        private class BoneData;
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
            public Vector3 LocalPosition { get; set; }
            public Quaternion LocalRotation { get; set; }
            public Vector3 LocalScale { get; set; }
            public Matrix4x4 OffsetMatrix { get; set; }
            public float Length { get; set; }
            public float Radius { get; set; }
            public BoneType Type { get; set; }
            public Color Color { get; set; }
            public bool IsVisible { get; set; }
            public bool IsSelectable { get; set; }
            public BoneConstraintsData Constraints { get; set; }
            public List<VertexWeightData> VertexWeights { get; set; }
            public Dictionary<string, object> Properties { get; set; }
            public Guid? ParentId { get; set; }
            public List<Guid> ChildrenIds { get; set; }

            public BoneData() { }

            public BoneData(Bone bone)
            {
                Id = bone.Id;
                Name = bone.Name;
                LocalPosition = bone.LocalPosition;
                LocalRotation = bone.LocalRotation;
                LocalScale = bone.LocalScale;
                OffsetMatrix = bone.OffsetMatrix;
                Length = bone.Length;
                Radius = bone.Radius;
                Type = bone.Type;
                Color = bone.Color;
                IsVisible = bone.IsVisible;
                IsSelectable = bone.IsSelectable;

                Constraints = new BoneConstraintsData;
                {
                    MinPosition = bone.Constraints.MinPosition,
                    MaxPosition = bone.Constraints.MaxPosition,
                    MinRotation = bone.Constraints.MinRotation,
                    MaxRotation = bone.Constraints.MaxRotation,
                    MinScale = bone.Constraints.MinScale,
                    MaxScale = bone.Constraints.MaxScale,
                    EnablePositionConstraints = bone.Constraints.EnablePositionConstraints,
                    EnableRotationConstraints = bone.Constraints.EnableRotationConstraints,
                    EnableScaleConstraints = bone.Constraints.EnableScaleConstraints,
                    SpringStiffness = bone.Constraints.SpringStiffness,
                    DampingFactor = bone.Constraints.DampingFactor;
                };

                VertexWeights = bone.VertexWeights;
                    .Select(kvp => new VertexWeightData { VertexIndex = kvp.Key, Weight = kvp.Value })
                    .ToList();

                Properties = new Dictionary<string, object>(bone.Properties);
                ParentId = bone.Parent?.Id;
                ChildrenIds = bone.Children.Select(c => c.Id).ToList();
            }
        }

        /// <summary>
        /// Bone constraints data for serialization;
        /// </summary>
        private class BoneConstraintsData;
        {
            public Vector3 MinPosition { get; set; }
            public Vector3 MaxPosition { get; set; }
            public Vector3 MinRotation { get; set; }
            public Vector3 MaxRotation { get; set; }
            public Vector3 MinScale { get; set; }
            public Vector3 MaxScale { get; set; }
            public bool EnablePositionConstraints { get; set; }
            public bool EnableRotationConstraints { get; set; }
            public bool EnableScaleConstraints { get; set; }
            public float SpringStiffness { get; set; }
            public float DampingFactor { get; set; }
        }

        /// <summary>
        /// Vertex weight data for serialization;
        /// </summary>
        private class VertexWeightData;
        {
            public int VertexIndex { get; set; }
            public float Weight { get; set; }
        }

        #endregion;

        #region Utility Methods;

        /// <summary>
        /// Clear all bones;
        /// </summary>
        public void Clear()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                _bones.Clear();
                _boneNameToId.Clear();
                _boneLayers.Clear();
                _selectionSets.Clear();
                RootBone = null;
                SelectedBone = null;

                OnSkeletonChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Validate bone name uniqueness;
        /// </summary>
        public bool IsBoneNameUnique(string name)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return !_boneNameToId.ContainsKey(name);
            }
        }

        /// <summary>
        /// Get bone statistics;
        /// </summary>
        public BoneStatistics GetStatistics()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return new BoneStatistics;
                {
                    TotalBones = _bones.Count,
                    RootBones = _bones.Values.Count(b => b.Parent == null),
                    LeafBones = _bones.Values.Count(b => b.Children.Count == 0),
                    MaxDepth = _bones.Values.Max(b => b.GetDepth()),
                    AverageChildren = _bones.Values.Average(b => b.Children.Count),
                    BoneTypes = _bones.Values.GroupBy(b => b.Type)
                        .ToDictionary(g => g.Key, g => g.Count())
                };
            }
        }

        /// <summary>
        /// Bone statistics structure;
        /// </summary>
        public class BoneStatistics;
        {
            public int TotalBones { get; set; }
            public int RootBones { get; set; }
            public int LeafBones { get; set; }
            public int MaxDepth { get; set; }
            public double AverageChildren { get; set; }
            public Dictionary<BoneType, int> BoneTypes { get; set; }
        }

        /// <summary>
        /// Validate manager is not disposed;
        /// </summary>
        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(BoneManager));
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose implementation;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    Clear();

                    // Clear event subscriptions;
                    OnBoneSelectionChanged = null;
                    OnBoneTransformed = null;
                    OnBoneAdded = null;
                    OnBoneRemoved = null;
                    OnRootBoneChanged = null;
                    OnSkeletonChanged = null;
                }

                _isDisposed = true;
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~BoneManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Extension Classes;

    /// <summary>
    /// Math helper extensions for bone operations;
    /// </summary>
    public static class MathHelper;
    {
        public static float DegreesToRadians(float degrees) => degrees * (float)Math.PI / 180f;
        public static float RadiansToDegrees(float radians) => radians * 180f / (float)Math.PI;
    }

    /// <summary>
    /// Quaternion extensions for bone operations;
    /// </summary>
    public static class QuaternionExtensions;
    {
        public static Vector3 ToEulerAngles(this Quaternion q)
        {
            // Convert quaternion to Euler angles (roll, pitch, yaw)
            float roll = MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
            float pitch = MathF.Asin(2 * (q.W * q.Y - q.Z * q.X));
            float yaw = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));

            return new Vector3(
                MathHelper.RadiansToDegrees(roll),
                MathHelper.RadiansToDegrees(pitch),
                MathHelper.RadiansToDegrees(yaw)
            );
        }

        public static Quaternion CreateFromTwoVectors(Vector3 from, Vector3 to)
        {
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);

            float dot = Vector3.Dot(from, to);

            if (Math.Abs(dot + 1.0f) < 0.000001f)
            {
                // Vectors are opposite, use any perpendicular axis;
                return Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI);
            }
            else if (Math.Abs(dot - 1.0f) < 0.000001f)
            {
                // Vectors are the same;
                return Quaternion.Identity;
            }

            float angle = MathF.Acos(dot);
            Vector3 axis = Vector3.Normalize(Vector3.Cross(from, to));

            return Quaternion.CreateFromAxisAngle(axis, angle);
        }
    }

    /// <summary>
    /// JSON converters for Vector3, Quaternion, Matrix4x4;
    /// </summary>
    public class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var obj = JsonSerializer.Deserialize<Vector3Data>(ref reader, options);
            return new Vector3(obj.X, obj.Y, obj.Z);
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
        {
            var obj = new Vector3Data { X = value.X, Y = value.Y, Z = value.Z };
            JsonSerializer.Serialize(writer, obj, options);
        }

        private class Vector3Data;
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
        }
    }

    public class QuaternionConverter : JsonConverter<Quaternion>
    {
        public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var obj = JsonSerializer.Deserialize<QuaternionData>(ref reader, options);
            return new Quaternion(obj.X, obj.Y, obj.Z, obj.W);
        }

        public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
        {
            var obj = new QuaternionData { X = value.X, value.Y, value.Z, value.W };
            JsonSerializer.Serialize(writer, obj, options);
        }

        private class QuaternionData;
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float W { get; set; }
        }
    }

    public class Matrix4x4Converter : JsonConverter<Matrix4x4>
    {
        public override Matrix4x4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var data = JsonSerializer.Deserialize<float[]>(ref reader, options);
            if (data == null || data.Length != 16)
                return Matrix4x4.Identity;

            return new Matrix4x4(
                data[0], data[1], data[2], data[3],
                data[4], data[5], data[6], data[7],
                data[8], data[9], data[10], data[11],
                data[12], data[13], data[14], data[15]
            );
        }

        public override void Write(Utf8JsonWriter writer, Matrix4x4 value, JsonSerializerOptions options)
        {
            var data = new float[16];
            data[0] = value.M11; data[1] = value.M12; data[2] = value.M13; data[3] = value.M14;
            data[4] = value.M21; data[5] = value.M22; data[6] = value.M23; data[7] = value.M24;
            data[8] = value.M31; data[9] = value.M32; data[10] = value.M33; data[11] = value.M34;
            data[12] = value.M41; data[13] = value.M42; data[14] = value.M43; data[15] = value.M44;

            JsonSerializer.Serialize(writer, data, options);
        }
    }

    #endregion;
}
