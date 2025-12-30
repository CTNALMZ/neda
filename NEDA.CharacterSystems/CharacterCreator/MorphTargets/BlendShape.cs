using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NEDA.API.ClientSDK;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.ContentCreation.ModelOptimization;
using NEDA.Core.Common;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.CharacterCreator.MorphTargets.BlendShapeEngine;

namespace NEDA.CharacterSystems.CharacterCreator.MorphTargets;
{
    /// <summary>
    /// Represents a single blend shape (morph target) with vertex deformations;
    /// </summary>
    public class BlendShape;
    {
        [JsonPropertyName("id")]
        public string BlendShapeId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("category")]
        public BlendShapeCategory Category { get; set; }

        [JsonPropertyName("subcategory")]
        public string Subcategory { get; set; }

        [JsonPropertyName("type")]
        public BlendShapeType Type { get; set; }

        [JsonPropertyName("vertexCount")]
        public int VertexCount { get; set; }

        [JsonPropertyName("baseVertices")]
        public List<Vector3> BaseVertices { get; set; }

        [JsonPropertyName("targetVertices")]
        public List<Vector3> TargetVertices { get; set; }

        [JsonPropertyName("vertexIndices")]
        public List<int> VertexIndices { get; set; }

        [JsonPropertyName("normals")]
        public List<Vector3> Normals { get; set; }

        [JsonPropertyName("tangents")]
        public List<Vector3> Tangents { get; set; }

        [JsonPropertyName("uvs")]
        public List<Vector2> UVs { get; set; }

        [JsonPropertyName("weight")]
        public float Weight { get; set; }

        [JsonPropertyName("minWeight")]
        public float MinWeight { get; set; }

        [JsonPropertyName("maxWeight")]
        public float MaxWeight { get; set; }

        [JsonPropertyName("defaultWeight")]
        public float DefaultWeight { get; set; }

        [JsonPropertyName("influenceRadius")]
        public float InfluenceRadius { get; set; }

        [JsonPropertyName("symmetryGroup")]
        public string SymmetryGroup { get; set; }

        [JsonPropertyName("mirrorBlendShapeId")]
        public string MirrorBlendShapeId { get; set; }

        [JsonPropertyName("parentBlendShapeId")]
        public string ParentBlendShapeId { get; set; }

        [JsonPropertyName("childBlendShapes")]
        public List<string> ChildBlendShapes { get; set; }

        [JsonPropertyName("influences")]
        public List<BlendShapeInfluence> Influences { get; set; }

        [JsonPropertyName("constraints")]
        public List<BlendShapeConstraint> Constraints { get; set; }

        [JsonPropertyName("performanceImpact")]
        public PerformanceImpact PerformanceImpact { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonIgnore]
        public bool IsDirty { get; set; }

        [JsonIgnore]
        public bool IsLoaded { get; set; }

        [JsonIgnore]
        private float[] _cachedDeltaVertices;

        [JsonIgnore]
        private bool _isCacheValid;

        public BlendShape()
        {
            BaseVertices = new List<Vector3>();
            TargetVertices = new List<Vector3>();
            VertexIndices = new List<int>();
            Normals = new List<Vector3>();
            Tangents = new List<Vector3>();
            UVs = new List<Vector2>();
            ChildBlendShapes = new List<string>();
            Influences = new List<BlendShapeInfluence>();
            Constraints = new List<BlendShapeConstraint>();
            Metadata = new Dictionary<string, object>();

            MinWeight = 0f;
            MaxWeight = 1f;
            DefaultWeight = 0f;
            InfluenceRadius = 1f;

            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
            IsDirty = false;
            IsLoaded = false;
            _isCacheValid = false;
        }

        /// <summary>
        /// Calculate delta vertices (target - base)
        /// </summary>
        public float[] GetDeltaVertices()
        {
            if (_isCacheValid && _cachedDeltaVertices != null)
                return _cachedDeltaVertices;

            if (BaseVertices.Count != TargetVertices.Count)
                throw new InvalidOperationException("Base and target vertex counts must match");

            var deltaCount = BaseVertices.Count * 3;
            _cachedDeltaVertices = new float[deltaCount];

            for (int i = 0; i < BaseVertices.Count; i++)
            {
                var delta = TargetVertices[i] - BaseVertices[i];
                _cachedDeltaVertices[i * 3] = delta.X;
                _cachedDeltaVertices[i * 3 + 1] = delta.Y;
                _cachedDeltaVertices[i * 3 + 2] = delta.Z;
            }

            _isCacheValid = true;
            return _cachedDeltaVertices;
        }

        /// <summary>
        /// Apply blend shape weight to vertices;
        /// </summary>
        public void ApplyToVertices(List<Vector3> vertices, float weight)
        {
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));

            if (VertexIndices.Count == 0)
                throw new InvalidOperationException("No vertex indices defined");

            weight = Math.Clamp(weight, MinWeight, MaxWeight);

            for (int i = 0; i < VertexIndices.Count; i++)
            {
                var vertexIndex = VertexIndices[i];
                if (vertexIndex >= 0 && vertexIndex < vertices.Count)
                {
                    var delta = TargetVertices[i] - BaseVertices[i];
                    vertices[vertexIndex] += delta * weight;
                }
            }
        }

        /// <summary>
        /// Apply blend shape with normalized weight (0-1)
        /// </summary>
        public void ApplyNormalized(List<Vector3> vertices, float normalizedWeight)
        {
            var weight = MinWeight + (MaxWeight - MinWeight) * normalizedWeight;
            ApplyToVertices(vertices, weight);
        }

        /// <summary>
        /// Calculate bounding sphere of affected vertices;
        /// </summary>
        public BoundingSphere GetBoundingSphere()
        {
            if (BaseVertices.Count == 0)
                return new BoundingSphere(Vector3.Zero, 0);

            var center = Vector3.Zero;
            foreach (var vertex in BaseVertices)
            {
                center += vertex;
            }
            center /= BaseVertices.Count;

            var maxRadius = 0f;
            foreach (var vertex in BaseVertices)
            {
                var distance = Vector3.Distance(vertex, center);
                if (distance > maxRadius)
                    maxRadius = distance;
            }

            return new BoundingSphere(center, maxRadius);
        }

        /// <summary>
        /// Check if point is within influence radius;
        /// </summary>
        public bool IsPointInfluenced(Vector3 point)
        {
            var sphere = GetBoundingSphere();
            var distance = Vector3.Distance(point, sphere.Center);
            return distance <= sphere.Radius * InfluenceRadius;
        }

        /// <summary>
        /// Get influence weight at point (0-1)
        /// </summary>
        public float GetInfluenceAtPoint(Vector3 point)
        {
            if (!IsPointInfluenced(point))
                return 0f;

            var sphere = GetBoundingSphere();
            var distance = Vector3.Distance(point, sphere.Center);
            var normalizedDistance = distance / (sphere.Radius * InfluenceRadius);

            // Inverse square falloff;
            return 1f - (normalizedDistance * normalizedDistance);
        }

        /// <summary>
        /// Validate blend shape data;
        /// </summary>
        public bool Validate()
        {
            if (string.IsNullOrWhiteSpace(BlendShapeId))
                return false;

            if (string.IsNullOrWhiteSpace(Name))
                return false;

            if (BaseVertices.Count != TargetVertices.Count)
                return false;

            if (VertexIndices.Count != BaseVertices.Count)
                return false;

            if (MinWeight > MaxWeight)
                return false;

            if (Weight < MinWeight || Weight > MaxWeight)
                return false;

            return true;
        }

        /// <summary>
        /// Create a mirrored version of this blend shape;
        /// </summary>
        public BlendShape CreateMirrored(string mirroredId, MirrorAxis axis = MirrorAxis.X)
        {
            var mirrored = new BlendShape;
            {
                BlendShapeId = mirroredId,
                Name = $"{Name}_Mirrored",
                DisplayName = $"{DisplayName} (Mirrored)",
                Category = Category,
                Type = Type,
                MinWeight = MinWeight,
                MaxWeight = MaxWeight,
                DefaultWeight = DefaultWeight,
                InfluenceRadius = InfluenceRadius,
                SymmetryGroup = SymmetryGroup,
                MirrorBlendShapeId = BlendShapeId,
                ParentBlendShapeId = ParentBlendShapeId;
            };

            // Mirror vertices;
            foreach (var vertex in BaseVertices)
            {
                mirrored.BaseVertices.Add(MirrorVector(vertex, axis));
            }

            foreach (var vertex in TargetVertices)
            {
                mirrored.TargetVertices.Add(MirrorVector(vertex, axis));
            }

            // Copy other properties;
            mirrored.VertexIndices = new List<int>(VertexIndices);
            mirrored.Normals = Normals.Select(n => MirrorVector(n, axis)).ToList();
            mirrored.Tangents = Tangents.Select(t => MirrorVector(t, axis)).ToList();
            mirrored.UVs = new List<Vector2>(UVs);

            // Copy influences and constraints (adjust for mirroring)
            foreach (var influence in Influences)
            {
                mirrored.Influences.Add(influence.CreateMirrored(axis));
            }

            foreach (var constraint in Constraints)
            {
                mirrored.Constraints.Add(constraint.CreateMirrored(axis));
            }

            mirrored.Metadata = new Dictionary<string, object>(Metadata)
            {
                ["MirroredFrom"] = BlendShapeId,
                ["MirrorAxis"] = axis.ToString()
            };

            return mirrored;
        }

        private Vector3 MirrorVector(Vector3 vector, MirrorAxis axis)
        {
            return axis switch;
            {
                MirrorAxis.X => new Vector3(-vector.X, vector.Y, vector.Z),
                MirrorAxis.Y => new Vector3(vector.X, -vector.Y, vector.Z),
                MirrorAxis.Z => new Vector3(vector.X, vector.Y, -vector.Z),
                _ => vector;
            };
        }

        /// <summary>
        /// Invalidate cache (call when vertices change)
        /// </summary>
        public void InvalidateCache()
        {
            _isCacheValid = false;
            _cachedDeltaVertices = null;
            IsDirty = true;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Deep clone this blend shape;
        /// </summary>
        public BlendShape DeepClone()
        {
            var clone = new BlendShape;
            {
                BlendShapeId = $"{BlendShapeId}_clone_{Guid.NewGuid().ToString()[..8]}",
                Name = $"{Name}_Clone",
                DisplayName = $"{DisplayName} (Clone)",
                Category = Category,
                Subcategory = Subcategory,
                Type = Type,
                VertexCount = VertexCount,
                Weight = Weight,
                MinWeight = MinWeight,
                MaxWeight = MaxWeight,
                DefaultWeight = DefaultWeight,
                InfluenceRadius = InfluenceRadius,
                SymmetryGroup = SymmetryGroup,
                MirrorBlendShapeId = MirrorBlendShapeId,
                ParentBlendShapeId = ParentBlendShapeId,
                PerformanceImpact = PerformanceImpact?.Clone(),
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                IsDirty = IsDirty,
                IsLoaded = IsLoaded;
            };

            // Deep copy collections;
            clone.BaseVertices = new List<Vector3>(BaseVertices);
            clone.TargetVertices = new List<Vector3>(TargetVertices);
            clone.VertexIndices = new List<int>(VertexIndices);
            clone.Normals = new List<Vector3>(Normals);
            clone.Tangents = new List<Vector3>(Tangents);
            clone.UVs = new List<Vector2>(UVs);
            clone.ChildBlendShapes = new List<string>(ChildBlendShapes);

            clone.Influences = Influences.Select(i => i.Clone()).ToList();
            clone.Constraints = Constraints.Select(c => c.Clone()).ToList();
            clone.Metadata = new Dictionary<string, object>(Metadata);

            return clone;
        }
    }

    /// <summary>
    /// Represents a collection of blend shapes for a character/mesh;
    /// </summary>
    public class BlendShapeSet;
    {
        [JsonPropertyName("id")]
        public string SetId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("meshId")]
        public string MeshId { get; set; }

        [JsonPropertyName("characterId")]
        public string CharacterId { get; set; }

        [JsonPropertyName("blendShapes")]
        public Dictionary<string, BlendShape> BlendShapes { get; set; }

        [JsonPropertyName("categories")]
        public Dictionary<BlendShapeCategory, BlendShapeCategoryInfo> Categories { get; set; }

        [JsonPropertyName("symmetryGroups")]
        public Dictionary<string, SymmetryGroup> SymmetryGroups { get; set; }

        [JsonPropertyName("vertexCount")]
        public int VertexCount { get; set; }

        [JsonPropertyName("totalInfluences")]
        public int TotalInfluences { get; set; }

        [JsonPropertyName("performanceRating")]
        public PerformanceRating PerformanceRating { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }

        [JsonIgnore]
        public bool IsCompressed { get; set; }

        [JsonIgnore]
        public CompressionFormat CompressionFormat { get; set; }

        [JsonIgnore]
        private Dictionary<string, float[]> _blendShapeCache;

        [JsonIgnore]
        private Dictionary<string, BlendShapeInfluenceMap> _influenceMaps;

        public BlendShapeSet()
        {
            BlendShapes = new Dictionary<string, BlendShape>();
            Categories = new Dictionary<BlendShapeCategory, BlendShapeCategoryInfo>();
            SymmetryGroups = new Dictionary<string, SymmetryGroup>();
            Metadata = new Dictionary<string, object>();
            _blendShapeCache = new Dictionary<string, float[]>();
            _influenceMaps = new Dictionary<string, BlendShapeInfluenceMap>();

            PerformanceRating = PerformanceRating.Medium;
            IsCompressed = false;
        }

        /// <summary>
        /// Add blend shape to set;
        /// </summary>
        public bool AddBlendShape(BlendShape blendShape)
        {
            if (blendShape == null)
                throw new ArgumentNullException(nameof(blendShape));

            if (BlendShapes.ContainsKey(blendShape.BlendShapeId))
                return false;

            BlendShapes[blendShape.BlendShapeId] = blendShape;

            // Update category info;
            if (!Categories.ContainsKey(blendShape.Category))
            {
                Categories[blendShape.Category] = new BlendShapeCategoryInfo;
                {
                    Category = blendShape.Category,
                    BlendShapeCount = 0,
                    TotalVertices = 0,
                    AverageWeight = 0;
                };
            }

            var categoryInfo = Categories[blendShape.Category];
            categoryInfo.BlendShapeCount++;
            categoryInfo.TotalVertices += blendShape.VertexCount;

            // Update symmetry groups;
            if (!string.IsNullOrEmpty(blendShape.SymmetryGroup))
            {
                if (!SymmetryGroups.ContainsKey(blendShape.SymmetryGroup))
                {
                    SymmetryGroups[blendShape.SymmetryGroup] = new SymmetryGroup;
                    {
                        GroupId = blendShape.SymmetryGroup,
                        BlendShapeIds = new List<string>()
                    };
                }

                SymmetryGroups[blendShape.SymmetryGroup].BlendShapeIds.Add(blendShape.BlendShapeId);
            }

            // Update performance rating;
            UpdatePerformanceRating();

            // Cache delta vertices;
            _blendShapeCache[blendShape.BlendShapeId] = blendShape.GetDeltaVertices();

            // Build influence map;
            BuildInfluenceMap(blendShape);

            return true;
        }

        /// <summary>
        /// Remove blend shape from set;
        /// </summary>
        public bool RemoveBlendShape(string blendShapeId)
        {
            if (!BlendShapes.TryGetValue(blendShapeId, out var blendShape))
                return false;

            // Remove from categories;
            if (Categories.ContainsKey(blendShape.Category))
            {
                var categoryInfo = Categories[blendShape.Category];
                categoryInfo.BlendShapeCount--;
                categoryInfo.TotalVertices -= blendShape.VertexCount;

                if (categoryInfo.BlendShapeCount == 0)
                {
                    Categories.Remove(blendShape.Category);
                }
            }

            // Remove from symmetry groups;
            if (!string.IsNullOrEmpty(blendShape.SymmetryGroup) &&
                SymmetryGroups.ContainsKey(blendShape.SymmetryGroup))
            {
                var group = SymmetryGroups[blendShape.SymmetryGroup];
                group.BlendShapeIds.Remove(blendShapeId);

                if (group.BlendShapeIds.Count == 0)
                {
                    SymmetryGroups.Remove(blendShape.SymmetryGroup);
                }
            }

            // Remove from cache;
            _blendShapeCache.Remove(blendShapeId);
            _influenceMaps.Remove(blendShapeId);

            // Remove from dictionary;
            BlendShapes.Remove(blendShapeId);

            // Update performance;
            UpdatePerformanceRating();

            return true;
        }

        /// <summary>
        /// Get blend shape by ID;
        /// </summary>
        public BlendShape GetBlendShape(string blendShapeId)
        {
            return BlendShapes.TryGetValue(blendShapeId, out var blendShape) ? blendShape : null;
        }

        /// <summary>
        /// Get blend shapes by category;
        /// </summary>
        public List<BlendShape> GetBlendShapesByCategory(BlendShapeCategory category)
        {
            return BlendShapes.Values;
                .Where(bs => bs.Category == category)
                .ToList();
        }

        /// <summary>
        /// Get blend shapes in symmetry group;
        /// </summary>
        public List<BlendShape> GetBlendShapesInGroup(string symmetryGroup)
        {
            if (!SymmetryGroups.TryGetValue(symmetryGroup, out var group))
                return new List<BlendShape>();

            return group.BlendShapeIds;
                .Select(id => GetBlendShape(id))
                .Where(bs => bs != null)
                .ToList();
        }

        /// <summary>
        /// Apply multiple blend shapes to vertices;
        /// </summary>
        public void ApplyBlendShapes(List<Vector3> vertices, Dictionary<string, float> weights)
        {
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));

            if (weights == null)
                return;

            foreach (var kvp in weights)
            {
                if (BlendShapes.TryGetValue(kvp.Key, out var blendShape))
                {
                    blendShape.ApplyToVertices(vertices, kvp.Value);
                }
            }
        }

        /// <summary>
        /// Apply all blend shapes with their current weights;
        /// </summary>
        public void ApplyAll(List<Vector3> vertices)
        {
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));

            foreach (var blendShape in BlendShapes.Values)
            {
                if (blendShape.Weight != 0)
                {
                    blendShape.ApplyToVertices(vertices, blendShape.Weight);
                }
            }
        }

        /// <summary>
        /// Calculate combined delta for optimization;
        /// </summary>
        public float[] GetCombinedDelta(Dictionary<string, float> weights)
        {
            if (VertexCount == 0)
                return Array.Empty<float>();

            var combinedDelta = new float[VertexCount * 3];

            foreach (var kvp in weights)
            {
                if (_blendShapeCache.TryGetValue(kvp.Key, out var delta) && delta != null)
                {
                    var weight = kvp.Value;

                    for (int i = 0; i < delta.Length; i++)
                    {
                        combinedDelta[i] += delta[i] * weight;
                    }
                }
            }

            return combinedDelta;
        }

        /// <summary>
        /// Get blend shapes that influence a specific vertex;
        /// </summary>
        public List<BlendShapeInfluence> GetInfluencesForVertex(int vertexIndex)
        {
            var influences = new List<BlendShapeInfluence>();

            foreach (var blendShape in BlendShapes.Values)
            {
                var influence = blendShape.Influences;
                    .FirstOrDefault(i => i.VertexIndices.Contains(vertexIndex));

                if (influence != null)
                {
                    influences.Add(influence);
                }
            }

            return influences;
        }

        /// <summary>
        /// Optimize blend shapes by merging similar ones;
        /// </summary>
        public OptimizationResult Optimize(float similarityThreshold = 0.9f)
        {
            var result = new OptimizationResult;
            {
                OriginalCount = BlendShapes.Count,
                RemovedCount = 0,
                MergedCount = 0,
                RemovedBlendShapes = new List<string>(),
                MergedPairs = new List<MergePair>()
            };

            var blendShapeList = BlendShapes.Values.ToList();
            var toRemove = new HashSet<string>();

            for (int i = 0; i < blendShapeList.Count; i++)
            {
                if (toRemove.Contains(blendShapeList[i].BlendShapeId))
                    continue;

                for (int j = i + 1; j < blendShapeList.Count; j++)
                {
                    if (toRemove.Contains(blendShapeList[j].BlendShapeId))
                        continue;

                    var similarity = CalculateSimilarity(blendShapeList[i], blendShapeList[j]);

                    if (similarity >= similarityThreshold)
                    {
                        // Merge j into i;
                        MergeBlendShapes(blendShapeList[i], blendShapeList[j]);
                        toRemove.Add(blendShapeList[j].BlendShapeId);

                        result.MergedPairs.Add(new MergePair;
                        {
                            BlendShapeId1 = blendShapeList[i].BlendShapeId,
                            BlendShapeId2 = blendShapeList[j].BlendShapeId,
                            Similarity = similarity;
                        });

                        result.MergedCount++;
                    }
                }
            }

            // Remove merged blend shapes;
            foreach (var blendShapeId in toRemove)
            {
                RemoveBlendShape(blendShapeId);
                result.RemovedBlendShapes.Add(blendShapeId);
                result.RemovedCount++;
            }

            // Rebuild caches;
            RebuildCaches();

            result.FinalCount = BlendShapes.Count;
            result.ReductionPercentage = result.OriginalCount > 0;
                ? (float)result.RemovedCount / result.OriginalCount * 100;
                : 0;

            return result;
        }

        /// <summary>
        /// Compress blend shape data;
        /// </summary>
        public CompressionResult Compress(CompressionFormat format, float quality = 0.8f)
        {
            if (IsCompressed)
                throw new InvalidOperationException("Blend shape set is already compressed");

            var result = new CompressionResult;
            {
                Format = format,
                OriginalSize = CalculateSize(),
                Quality = quality;
            };

            switch (format)
            {
                case CompressionFormat.Quantized16Bit:
                    result = CompressQuantized16Bit(quality);
                    break;

                case CompressionFormat.DeltaEncoding:
                    result = CompressDeltaEncoding(quality);
                    break;

                case CompressionFormat.Wavelet:
                    result = CompressWavelet(quality);
                    break;

                default:
                    throw new NotSupportedException($"Compression format {format} not supported");
            }

            IsCompressed = true;
            CompressionFormat = format;

            result.CompressionRatio = result.OriginalSize > 0;
                ? (float)result.CompressedSize / result.OriginalSize;
                : 1;

            result.Success = true;

            return result;
        }

        /// <summary>
        /// Decompress blend shape data;
        /// </summary>
        public bool Decompress()
        {
            if (!IsCompressed)
                return true;

            // Implementation would depend on compression format;
            // For now, just mark as uncompressed;
            IsCompressed = false;
            CompressionFormat = CompressionFormat.None;

            // Rebuild caches;
            RebuildCaches();

            return true;
        }

        /// <summary>
        /// Export blend shapes to file format;
        /// </summary>
        public ExportResult Export(ExportFormat format, string filePath)
        {
            var result = new ExportResult;
            {
                Format = format,
                FilePath = filePath,
                ExportTime = DateTime.UtcNow;
            };

            try
            {
                switch (format)
                {
                    case ExportFormat.JSON:
                        result = ExportToJson(filePath);
                        break;

                    case ExportFormat.Binary:
                        result = ExportToBinary(filePath);
                        break;

                    case ExportFormat.FBX:
                        result = ExportToFbx(filePath);
                        break;

                    case ExportFormat.GLTF:
                        result = ExportToGltf(filePath);
                        break;

                    default:
                        throw new NotSupportedException($"Export format {format} not supported");
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Import blend shapes from file;
        /// </summary>
        public ImportResult Import(ImportFormat format, string filePath)
        {
            var result = new ImportResult;
            {
                Format = format,
                FilePath = filePath,
                ImportTime = DateTime.UtcNow;
            };

            try
            {
                switch (format)
                {
                    case ImportFormat.JSON:
                        result = ImportFromJson(filePath);
                        break;

                    case ImportFormat.Binary:
                        result = ImportFromBinary(filePath);
                        break;

                    case ImportFormat.FBX:
                        result = ImportFromFbx(filePath);
                        break;

                    case ImportFormat.GLTF:
                        result = ImportFromGltf(filePath);
                        break;

                    default:
                        throw new NotSupportedException($"Import format {format} not supported");
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Validate all blend shapes in set;
        /// </summary>
        public ValidationReport ValidateAll()
        {
            var report = new ValidationReport;
            {
                TotalBlendShapes = BlendShapes.Count,
                ValidBlendShapes = 0,
                InvalidBlendShapes = new List<BlendShapeValidation>(),
                Warnings = new List<string>(),
                Errors = new List<string>()
            };

            foreach (var blendShape in BlendShapes.Values)
            {
                var validation = ValidateBlendShape(blendShape);

                if (validation.IsValid)
                {
                    report.ValidBlendShapes++;
                }
                else;
                {
                    report.InvalidBlendShapes.Add(validation);

                    if (validation.Severity == ValidationSeverity.Error)
                    {
                        report.Errors.Add($"Blend shape {blendShape.BlendShapeId}: {validation.Message}");
                    }
                    else;
                    {
                        report.Warnings.Add($"Blend shape {blendShape.BlendShapeId}: {validation.Message}");
                    }
                }
            }

            // Additional set-wide validations;
            ValidateSetWide(report);

            report.ValidationTime = DateTime.UtcNow;
            report.IsValid = report.Errors.Count == 0;

            return report;
        }

        private void UpdatePerformanceRating()
        {
            var totalVertices = BlendShapes.Values.Sum(bs => bs.VertexCount);
            var totalInfluences = BlendShapes.Values.Sum(bs => bs.Influences.Count);

            TotalInfluences = totalInfluences;

            // Simple performance rating based on vertex count and influences;
            var complexityScore = totalVertices * totalInfluences / 1000000f;

            PerformanceRating = complexityScore switch;
            {
                < 0.1f => PerformanceRating.VeryLow,
                < 0.5f => PerformanceRating.Low,
                < 2f => PerformanceRating.Medium,
                < 5f => PerformanceRating.High,
                _ => PerformanceRating.VeryHigh;
            };
        }

        private void BuildInfluenceMap(BlendShape blendShape)
        {
            var influenceMap = new BlendShapeInfluenceMap;
            {
                BlendShapeId = blendShape.BlendShapeId,
                VertexInfluences = new Dictionary<int, float>()
            };

            foreach (var influence in blendShape.Influences)
            {
                foreach (var vertexIndex in influence.VertexIndices)
                {
                    if (!influenceMap.VertexInfluences.ContainsKey(vertexIndex))
                    {
                        influenceMap.VertexInfluences[vertexIndex] = 0f;
                    }

                    influenceMap.VertexInfluences[vertexIndex] += influence.Weight;
                }
            }

            _influenceMaps[blendShape.BlendShapeId] = influenceMap;
        }

        private void RebuildCaches()
        {
            _blendShapeCache.Clear();
            _influenceMaps.Clear();

            foreach (var blendShape in BlendShapes.Values)
            {
                _blendShapeCache[blendShape.BlendShapeId] = blendShape.GetDeltaVertices();
                BuildInfluenceMap(blendShape);
            }
        }

        private float CalculateSimilarity(BlendShape bs1, BlendShape bs2)
        {
            if (bs1.VertexCount != bs2.VertexCount)
                return 0f;

            var delta1 = bs1.GetDeltaVertices();
            var delta2 = bs2.GetDeltaVertices();

            if (delta1.Length != delta2.Length)
                return 0f;

            // Calculate cosine similarity;
            float dotProduct = 0;
            float magnitude1 = 0;
            float magnitude2 = 0;

            for (int i = 0; i < delta1.Length; i++)
            {
                dotProduct += delta1[i] * delta2[i];
                magnitude1 += delta1[i] * delta1[i];
                magnitude2 += delta2[i] * delta2[i];
            }

            magnitude1 = MathF.Sqrt(magnitude1);
            magnitude2 = MathF.Sqrt(magnitude2);

            if (magnitude1 == 0 || magnitude2 == 0)
                return 0f;

            return dotProduct / (magnitude1 * magnitude2);
        }

        private void MergeBlendShapes(BlendShape target, BlendShape source)
        {
            // Average the deltas;
            var targetDelta = target.GetDeltaVertices();
            var sourceDelta = source.GetDeltaVertices();

            for (int i = 0; i < targetDelta.Length; i++)
            {
                targetDelta[i] = (targetDelta[i] + sourceDelta[i]) / 2f;
            }

            // Update target vertices;
            for (int i = 0; i < target.BaseVertices.Count; i++)
            {
                target.TargetVertices[i] = target.BaseVertices[i] +
                    new Vector3(targetDelta[i * 3], targetDelta[i * 3 + 1], targetDelta[i * 3 + 2]);
            }

            // Merge influences;
            target.Influences.AddRange(source.Influences.Select(i => i.Clone()));

            // Update metadata;
            target.Metadata["MergedFrom"] = source.BlendShapeId;
            target.Metadata["MergeTime"] = DateTime.UtcNow;

            target.InvalidateCache();
        }

        private long CalculateSize()
        {
            long size = 0;

            foreach (var blendShape in BlendShapes.Values)
            {
                // Base vertices: 3 floats * 4 bytes;
                size += blendShape.BaseVertices.Count * 3 * 4;

                // Target vertices: same;
                size += blendShape.TargetVertices.Count * 3 * 4;

                // Vertex indices: int * 4 bytes;
                size += blendShape.VertexIndices.Count * 4;

                // Normals and tangents;
                size += blendShape.Normals.Count * 3 * 4;
                size += blendShape.Tangents.Count * 3 * 4;

                // UVs: 2 floats * 4 bytes;
                size += blendShape.UVs.Count * 2 * 4;
            }

            return size;
        }

        private CompressionResult CompressQuantized16Bit(float quality)
        {
            var result = new CompressionResult;
            {
                OriginalSize = CalculateSize(),
                Quality = quality;
            };

            // Quantize to 16-bit floats;
            foreach (var blendShape in BlendShapes.Values)
            {
                QuantizeVertices(blendShape.BaseVertices, quality);
                QuantizeVertices(blendShape.TargetVertices, quality);
                QuantizeVertices(blendShape.Normals, quality);
                QuantizeVertices(blendShape.Tangents, quality);

                blendShape.InvalidateCache();
            }

            result.CompressedSize = CalculateSize();
            return result;
        }

        private void QuantizeVertices(List<Vector3> vertices, float quality)
        {
            // Simple quantization implementation;
            var quantizationLevel = (int)(65535 * quality); // 16-bit range;

            for (int i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i];
                vertices[i] = new Vector3(
                    QuantizeValue(v.X, quantizationLevel),
                    QuantizeValue(v.Y, quantizationLevel),
                    QuantizeValue(v.Z, quantizationLevel)
                );
            }
        }

        private float QuantizeValue(float value, int levels)
        {
            return MathF.Round(value * levels) / levels;
        }

        private CompressionResult CompressDeltaEncoding(float quality)
        {
            // Delta encoding: store only differences from base mesh;
            var result = new CompressionResult;
            {
                OriginalSize = CalculateSize(),
                Quality = quality;
            };

            // Implementation would store only delta vertices;
            // For now, just return placeholder;
            result.CompressedSize = result.OriginalSize / 2;

            return result;
        }

        private CompressionResult CompressWavelet(float quality)
        {
            // Wavelet compression for smooth shapes;
            var result = new CompressionResult;
            {
                OriginalSize = CalculateSize(),
                Quality = quality;
            };

            // Implementation would apply wavelet transform;
            // For now, just return placeholder;
            result.CompressedSize = (long)(result.OriginalSize * (1 - quality * 0.5f));

            return result;
        }

        private ExportResult ExportToJson(string filePath)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new Vector3Converter(), new Vector2Converter() }
            };

            var json = JsonSerializer.Serialize(this, options);
            System.IO.File.WriteAllText(filePath, json);

            return new ExportResult;
            {
                FileSize = new System.IO.FileInfo(filePath).Length,
                Success = true;
            };
        }

        private ExportResult ExportToBinary(string filePath)
        {
            // Binary serialization implementation;
            // For now, just return placeholder;
            return new ExportResult;
            {
                FileSize = CalculateSize(),
                Success = true;
            };
        }

        private ExportResult ExportToFbx(string filePath)
        {
            // FBX export implementation;
            // Would require FBX SDK integration;
            return new ExportResult;
            {
                Success = false,
                Error = "FBX export not implemented"
            };
        }

        private ExportResult ExportToGltf(string filePath)
        {
            // glTF export implementation;
            // Would require glTF library;
            return new ExportResult;
            {
                Success = false,
                Error = "glTF export not implemented"
            };
        }

        private ImportResult ImportFromJson(string filePath)
        {
            var json = System.IO.File.ReadAllText(filePath);
            var options = new JsonSerializerOptions
            {
                Converters = { new Vector3Converter(), new Vector2Converter() }
            };

            var importedSet = JsonSerializer.Deserialize<BlendShapeSet>(json, options);

            // Merge with current set;
            foreach (var blendShape in importedSet.BlendShapes.Values)
            {
                AddBlendShape(blendShape);
            }

            return new ImportResult;
            {
                ImportedCount = importedSet.BlendShapes.Count,
                Success = true;
            };
        }

        private ImportResult ImportFromBinary(string filePath)
        {
            // Binary import implementation;
            return new ImportResult;
            {
                Success = false,
                Error = "Binary import not implemented"
            };
        }

        private ImportResult ImportFromFbx(string filePath)
        {
            // FBX import implementation;
            return new ImportResult;
            {
                Success = false,
                Error = "FBX import not implemented"
            };
        }

        private ImportResult ImportFromGltf(string filePath)
        {
            // glTF import implementation;
            return new ImportResult;
            {
                Success = false,
                Error = "glTF import not implemented"
            };
        }

        private BlendShapeValidation ValidateBlendShape(BlendShape blendShape)
        {
            var validation = new BlendShapeValidation;
            {
                BlendShapeId = blendShape.BlendShapeId,
                Name = blendShape.Name,
                IsValid = true,
                Severity = ValidationSeverity.Info,
                Message = "OK"
            };

            // Basic validations;
            if (!blendShape.Validate())
            {
                validation.IsValid = false;
                validation.Severity = ValidationSeverity.Error;
                validation.Message = "Basic validation failed";
                return validation;
            }

            // Check for NaN or infinity;
            foreach (var vertex in blendShape.BaseVertices)
            {
                if (float.IsNaN(vertex.X) || float.IsNaN(vertex.Y) || float.IsNaN(vertex.Z) ||
                    float.IsInfinity(vertex.X) || float.IsInfinity(vertex.Y) || float.IsInfinity(vertex.Z))
                {
                    validation.IsValid = false;
                    validation.Severity = ValidationSeverity.Error;
                    validation.Message = "Contains NaN or Infinity values";
                    return validation;
                }
            }

            // Check vertex indices range;
            if (blendShape.VertexIndices.Any(idx => idx < 0 || idx >= VertexCount))
            {
                validation.IsValid = false;
                validation.Severity = ValidationSeverity.Error;
                validation.Message = "Vertex indices out of range";
                return validation;
            }

            // Check for duplicate vertex indices;
            var duplicateIndices = blendShape.VertexIndices;
                .GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIndices.Count > 0)
            {
                validation.IsValid = false;
                validation.Severity = ValidationSeverity.Warning;
                validation.Message = $"Duplicate vertex indices: {string.Join(",", duplicateIndices.Take(5))}";
            }

            // Check influence weights;
            foreach (var influence in blendShape.Influences)
            {
                if (influence.Weight < 0 || influence.Weight > 1)
                {
                    validation.IsValid = false;
                    validation.Severity = ValidationSeverity.Warning;
                    validation.Message = $"Influence weight out of range: {influence.Weight}";
                }
            }

            return validation;
        }

        private void ValidateSetWide(ValidationReport report)
        {
            // Check for overlapping influences;
            var vertexInfluenceCount = new Dictionary<int, int>();

            foreach (var blendShape in BlendShapes.Values)
            {
                foreach (var influence in blendShape.Influences)
                {
                    foreach (var vertexIndex in influence.VertexIndices)
                    {
                        if (!vertexInfluenceCount.ContainsKey(vertexIndex))
                            vertexInfluenceCount[vertexIndex] = 0;

                        vertexInfluenceCount[vertexIndex]++;
                    }
                }
            }

            var highlyInfluencedVertices = vertexInfluenceCount;
                .Where(kvp => kvp.Value > 5)
                .Select(kvp => kvp.Key)
                .ToList();

            if (highlyInfluencedVertices.Count > 0)
            {
                report.Warnings.Add($"{highlyInfluencedVertices.Count} vertices influenced by more than 5 blend shapes");
            }

            // Check symmetry groups;
            foreach (var group in SymmetryGroups.Values)
            {
                if (group.BlendShapeIds.Count % 2 != 0)
                {
                    report.Warnings.Add($"Symmetry group {group.GroupId} has uneven number of blend shapes");
                }
            }
        }
    }

    /// <summary>
    /// Blend Shape Engine for creating, manipulating, and applying blend shapes;
    /// </summary>
    public interface IBlendShapeEngine;
    {
        // Blend Shape Management;
        Task<BlendShape> CreateBlendShapeAsync(BlendShapeDefinition definition);
        Task<bool> UpdateBlendShapeAsync(string blendShapeId, BlendShapeUpdate update);
        Task<bool> DeleteBlendShapeAsync(string blendShapeId);
        Task<BlendShape> GetBlendShapeAsync(string blendShapeId);
        Task<List<BlendShape>> GetBlendShapesByCategoryAsync(BlendShapeCategory category);

        // Blend Shape Set Operations;
        Task<BlendShapeSet> CreateBlendShapeSetAsync(string setId, string meshId);
        Task<bool> AddToSetAsync(string setId, string blendShapeId);
        Task<bool> RemoveFromSetAsync(string setId, string blendShapeId);
        Task<BlendShapeSet> GetBlendShapeSetAsync(string setId);
        Task<bool> SaveBlendShapeSetAsync(string setId);

        // Generation and Processing;
        Task<BlendShape> GenerateFromDifferenceAsync(MeshDifference difference);
        Task<BlendShape> GenerateFromSculptAsync(SculptData sculptData);
        Task<List<BlendShape>> GenerateExpressionSetAsync(ExpressionTemplate template);
        Task<BlendShape> CombineBlendShapesAsync(List<string> blendShapeIds, CombineMode mode);

        // Application and Evaluation;
        Task<MeshData> ApplyBlendShapesAsync(MeshData mesh, Dictionary<string, float> weights);
        Task<MeshData> ApplyBlendShapeSetAsync(MeshData mesh, BlendShapeSet set, Dictionary<string, float> weights);
        Task<BlendShapeEvaluation> EvaluateBlendShapeAsync(string blendShapeId, MeshData mesh);

        // Optimization;
        Task<OptimizationResult> OptimizeSetAsync(string setId, OptimizationOptions options);
        Task<CompressionResult> CompressSetAsync(string setId, CompressionFormat format, float quality);
        Task<bool> DecompressSetAsync(string setId);

        // Import/Export;
        Task<ExportResult> ExportSetAsync(string setId, ExportFormat format, string filePath);
        Task<ImportResult> ImportSetAsync(ImportFormat format, string filePath, string targetSetId = null);

        // Analysis and Validation;
        Task<ValidationReport> ValidateSetAsync(string setId);
        Task<BlendShapeAnalysis> AnalyzeBlendShapeAsync(string blendShapeId);
        Task<PerformanceReport> AnalyzePerformanceAsync(string setId);

        // Advanced Features;
        Task<BlendShape> CreateMirroredAsync(string blendShapeId, MirrorAxis axis);
        Task<bool> CreateSymmetryGroupAsync(string groupId, List<string> blendShapeIds);
        Task<BlendShape> InterpolateBlendShapesAsync(string blendShapeId1, string blendShapeId2, float t);
        Task<BlendShape> ExtrapolateBlendShapeAsync(string blendShapeId, float factor);
    }

    /// <summary>
    /// Implementation of Blend Shape Engine;
    /// </summary>
    public class BlendShapeEngine : IBlendShapeEngine, IDisposable;
    {
        private readonly ILogger<BlendShapeEngine> _logger;
        private readonly IEventBus _eventBus;
        private readonly IMetricsCollector _metrics;
        private readonly IMemoryCache _cache;
        private readonly IBlendShapeRepository _repository;
        private readonly IMeshProcessor _meshProcessor;
        private readonly IOptimizationEngine _optimizationEngine;

        private readonly BlendShapeEngineConfig _config;
        private readonly Dictionary<string, BlendShapeSet> _activeSets;
        private readonly Dictionary<string, Task<BlendShape>> _generationTasks;
        private readonly object _lockObject = new object();

        private const string CACHE_PREFIX = "blendshape_";
        private const int CACHE_DURATION_MINUTES = 60;

        public BlendShapeEngine(
            ILogger<BlendShapeEngine> logger,
            IEventBus eventBus,
            IMetricsCollector metrics,
            IMemoryCache cache,
            IBlendShapeRepository repository,
            IMeshProcessor meshProcessor,
            IOptimizationEngine optimizationEngine,
            BlendShapeEngineConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _meshProcessor = meshProcessor ?? throw new ArgumentNullException(nameof(meshProcessor));
            _optimizationEngine = optimizationEngine ?? throw new ArgumentNullException(nameof(optimizationEngine));
            _config = config ?? new BlendShapeEngineConfig();

            _activeSets = new Dictionary<string, BlendShapeSet>();
            _generationTasks = new Dictionary<string, Task<BlendShape>>();

            InitializeCache();
            SubscribeToEvents();

            _logger.LogInformation("BlendShapeEngine initialized with configuration: {@Config}", _config);
        }

        // Implementation of IBlendShapeEngine interface methods;
        // (Due to length, showing key methods. Full implementation would include all interface methods)

        public async Task<BlendShape> CreateBlendShapeAsync(BlendShapeDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            _logger.LogInformation("Creating blend shape: {Name}", definition.Name);

            try
            {
                var blendShape = new BlendShape;
                {
                    BlendShapeId = Guid.NewGuid().ToString(),
                    Name = definition.Name,
                    DisplayName = definition.DisplayName ?? definition.Name,
                    Category = definition.Category,
                    Subcategory = definition.Subcategory,
                    Type = definition.Type,
                    VertexCount = definition.BaseVertices.Count,
                    BaseVertices = new List<Vector3>(definition.BaseVertices),
                    TargetVertices = new List<Vector3>(definition.TargetVertices),
                    VertexIndices = new List<int>(definition.VertexIndices),
                    Normals = new List<Vector3>(definition.Normals),
                    Tangents = new List<Vector3>(definition.Tangents),
                    UVs = new List<Vector2>(definition.UVs),
                    Weight = definition.Weight,
                    MinWeight = definition.MinWeight,
                    MaxWeight = definition.MaxWeight,
                    DefaultWeight = definition.DefaultWeight,
                    InfluenceRadius = definition.InfluenceRadius,
                    SymmetryGroup = definition.SymmetryGroup,
                    ParentBlendShapeId = definition.ParentBlendShapeId,
                    PerformanceImpact = definition.PerformanceImpact;
                };

                // Add child references if provided;
                if (definition.ChildBlendShapes != null)
                {
                    blendShape.ChildBlendShapes = new List<string>(definition.ChildBlendShapes);
                }

                // Add influences;
                if (definition.Influences != null)
                {
                    blendShape.Influences = definition.Influences;
                        .Select(i => new BlendShapeInfluence;
                        {
                            VertexIndices = new List<int>(i.VertexIndices),
                            Weight = i.Weight,
                            FalloffType = i.FalloffType,
                            InfluenceRadius = i.InfluenceRadius;
                        })
                        .ToList();
                }

                // Add constraints;
                if (definition.Constraints != null)
                {
                    blendShape.Constraints = definition.Constraints;
                        .Select(c => new BlendShapeConstraint;
                        {
                            Type = c.Type,
                            Parameters = new Dictionary<string, object>(c.Parameters)
                        })
                        .ToList();
                }

                // Validate;
                if (!blendShape.Validate())
                {
                    throw new InvalidBlendShapeException("Blend shape validation failed");
                }

                // Cache;
                await CacheBlendShapeAsync(blendShape);

                // Publish event;
                await _eventBus.PublishAsync(new BlendShapeCreatedEvent;
                {
                    BlendShapeId = blendShape.BlendShapeId,
                    Name = blendShape.Name,
                    Category = blendShape.Category,
                    VertexCount = blendShape.VertexCount,
                    Timestamp = DateTime.UtcNow;
                });

                // Update metrics;
                _metrics.IncrementCounter("blendshapes.created", new Dictionary<string, string>
                {
                    ["category"] = blendShape.Category.ToString(),
                    ["type"] = blendShape.Type.ToString()
                });

                _logger.LogInformation("Blend shape created: {BlendShapeId} - {Name}",
                    blendShape.BlendShapeId, blendShape.Name);

                return blendShape;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create blend shape: {Name}", definition.Name);
                throw new BlendShapeEngineException($"Failed to create blend shape: {ex.Message}", ex);
            }
        }

        public async Task<MeshData> ApplyBlendShapesAsync(MeshData mesh, Dictionary<string, float> weights)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            if (weights == null || weights.Count == 0)
                return mesh;

            _logger.LogDebug("Applying {Count} blend shapes to mesh", weights.Count);

            try
            {
                var result = mesh.DeepClone();
                var vertices = new List<Vector3>(result.Vertices);

                foreach (var kvp in weights)
                {
                    var blendShape = await GetBlendShapeAsync(kvp.Key);
                    if (blendShape != null)
                    {
                        blendShape.ApplyToVertices(vertices, kvp.Value);
                    }
                }

                result.Vertices = vertices;

                // Update normals if needed;
                if (_config.RecalculateNormals)
                {
                    result.Normals = await _meshProcessor.CalculateNormalsAsync(vertices, result.Indices);
                }

                // Update metrics;
                _metrics.IncrementCounter("blendshapes.applied", new Dictionary<string, string>
                {
                    ["count"] = weights.Count.ToString()
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply blend shapes to mesh");
                throw new BlendShapeEngineException($"Failed to apply blend shapes: {ex.Message}", ex);
            }
        }

        public async Task<OptimizationResult> OptimizeSetAsync(string setId, OptimizationOptions options)
        {
            if (string.IsNullOrWhiteSpace(setId))
                throw new ArgumentException("Set ID cannot be empty", nameof(setId));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _logger.LogInformation("Optimizing blend shape set: {SetId}", setId);

            try
            {
                var set = await GetBlendShapeSetAsync(setId);
                if (set == null)
                    throw new BlendShapeSetNotFoundException($"Blend shape set not found: {setId}");

                var result = await _optimizationEngine.OptimizeBlendShapeSetAsync(set, options);

                // Update cache;
                await CacheBlendShapeSetAsync(set);

                // Publish event;
                await _eventBus.PublishAsync(new BlendShapeSetOptimizedEvent;
                {
                    SetId = setId,
                    OriginalCount = result.OriginalCount,
                    FinalCount = result.FinalCount,
                    ReductionPercentage = result.ReductionPercentage,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Blend shape set optimized: {SetId} - Reduction: {Reduction}%",
                    setId, result.ReductionPercentage);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to optimize blend shape set: {SetId}", setId);
                throw new BlendShapeEngineException($"Failed to optimize set: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Clean up active sets;
                lock (_lockObject)
                {
                    _activeSets.Clear();
                    _generationTasks.Clear();
                }

                _logger.LogInformation("BlendShapeEngine disposed");
            }
        }

        #region Private Methods;

        private void InitializeCache()
        {
            // Pre-load frequently used blend shapes;
            Task.Run(async () =>
            {
                try
                {
                    var commonBlendShapes = await _repository.GetCommonBlendShapesAsync();
                    foreach (var blendShape in commonBlendShapes)
                    {
                        var cacheKey = $"{CACHE_PREFIX}{blendShape.BlendShapeId}";
                        _cache.Set(cacheKey, blendShape, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
                    }

                    _logger.LogDebug("Pre-loaded {Count} common blend shapes into cache", commonBlendShapes.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to pre-load common blend shapes");
                }
            });
        }

        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<MeshUpdatedEvent>(async e =>
            {
                await OnMeshUpdatedAsync(e);
            });

            _eventBus.Subscribe<PerformanceThresholdExceededEvent>(async e =>
            {
                await OnPerformanceThresholdExceededAsync(e);
            });
        }

        private async Task OnMeshUpdatedAsync(MeshUpdatedEvent e)
        {
            // Update any blend shapes that reference this mesh;
            var affectedSets = new List<string>();

            lock (_lockObject)
            {
                foreach (var set in _activeSets.Values)
                {
                    if (set.MeshId == e.MeshId)
                    {
                        affectedSets.Add(set.SetId);
                    }
                }
            }

            if (affectedSets.Count > 0)
            {
                await _eventBus.PublishAsync(new MeshUpdateAffectedBlendShapesEvent;
                {
                    MeshId = e.MeshId,
                    AffectedSetIds = affectedSets,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private async Task OnPerformanceThresholdExceededAsync(PerformanceThresholdExceededEvent e)
        {
            // Automatically optimize sets that exceed performance thresholds;
            if (e.MetricName == "blendshape_complexity" && e.Value > _config.AutoOptimizationThreshold)
            {
                _logger.LogWarning("Performance threshold exceeded for blend shape set: {SetId}, optimizing...", e.SetId);

                var options = new OptimizationOptions;
                {
                    SimilarityThreshold = 0.85f,
                    RemoveUnused = true,
                    MergeSimilar = true,
                    QualityPreservation = 0.9f;
                };

                await OptimizeSetAsync(e.SetId, options);
            }
        }

        private async Task CacheBlendShapeAsync(BlendShape blendShape)
        {
            var cacheKey = $"{CACHE_PREFIX}{blendShape.BlendShapeId}";
            _cache.Set(cacheKey, blendShape, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
        }

        private async Task CacheBlendShapeSetAsync(BlendShapeSet set)
        {
            var cacheKey = $"blendshape_set_{set.SetId}";
            _cache.Set(cacheKey, set, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
        }

        #endregion;

        #region Supporting Classes and Enums;

        public enum BlendShapeCategory;
        {
            FaceExpression,
            FacialFeature,
            BodyShape,
            MuscleDefinition,
            ClothingFit,
            Emotion,
            Phoneme,
            Deformation,
            Custom,
            Correction,
            Exaggeration,
            Stylized;
        }

        public enum BlendShapeType;
        {
            Linear,
            Corrective,
            Combination,
            Driven,
            Scripted,
            PhysicsBased,
            Procedural;
        }

        public enum MirrorAxis;
        {
            X,
            Y,
            Z;
        }

        public enum PerformanceImpact;
        {
            VeryLow,
            Low,
            Medium,
            High,
            VeryHigh;
        }

        public enum PerformanceRating;
        {
            VeryLow,
            Low,
            Medium,
            High,
            VeryHigh;
        }

        public enum CompressionFormat;
        {
            None,
            Quantized16Bit,
            DeltaEncoding,
            Wavelet,
            LZ4,
            Zstandard;
        }

        public enum ExportFormat;
        {
            JSON,
            Binary,
            FBX,
            GLTF,
            OBJ,
            USD;
        }

        public enum ImportFormat;
        {
            JSON,
            Binary,
            FBX,
            GLTF,
            OBJ,
            USD;
        }

        public enum ValidationSeverity;
        {
            Info,
            Warning,
            Error;
        }

        public enum CombineMode;
        {
            Additive,
            Average,
            Maximum,
            Minimum,
            Multiply;
        }

        public class BlendShapeInfluence;
        {
            public List<int> VertexIndices { get; set; }
            public float Weight { get; set; }
            public string FalloffType { get; set; }
            public float InfluenceRadius { get; set; }

            public BlendShapeInfluence()
            {
                VertexIndices = new List<int>();
                Weight = 1.0f;
                InfluenceRadius = 1.0f;
            }

            public BlendShapeInfluence Clone()
            {
                return new BlendShapeInfluence;
                {
                    VertexIndices = new List<int>(VertexIndices),
                    Weight = Weight,
                    FalloffType = FalloffType,
                    InfluenceRadius = InfluenceRadius;
                };
            }

            public BlendShapeInfluence CreateMirrored(MirrorAxis axis)
            {
                var mirrored = Clone();
                // Mirroring logic would be implemented here;
                return mirrored;
            }
        }

        public class BlendShapeConstraint;
        {
            public string Type { get; set; }
            public Dictionary<string, object> Parameters { get; set; }

            public BlendShapeConstraint()
            {
                Parameters = new Dictionary<string, object>();
            }

            public BlendShapeConstraint Clone()
            {
                return new BlendShapeConstraint;
                {
                    Type = Type,
                    Parameters = new Dictionary<string, object>(Parameters)
                };
            }

            public BlendShapeConstraint CreateMirrored(MirrorAxis axis)
            {
                var mirrored = Clone();
                // Mirroring logic would be implemented here;
                return mirrored;
            }
        }

        public class BlendShapeCategoryInfo;
        {
            public BlendShapeCategory Category { get; set; }
            public int BlendShapeCount { get; set; }
            public int TotalVertices { get; set; }
            public float AverageWeight { get; set; }
        }

        public class SymmetryGroup;
        {
            public string GroupId { get; set; }
            public List<string> BlendShapeIds { get; set; }

            public SymmetryGroup()
            {
                BlendShapeIds = new List<string>();
            }
        }

        public class BoundingSphere;
        {
            public Vector3 Center { get; set; }
            public float Radius { get; set; }

            public BoundingSphere(Vector3 center, float radius)
            {
                Center = center;
                Radius = radius;
            }
        }

        public class BlendShapeInfluenceMap;
        {
            public string BlendShapeId { get; set; }
            public Dictionary<int, float> VertexInfluences { get; set; }

            public BlendShapeInfluenceMap()
            {
                VertexInfluences = new Dictionary<int, float>();
            }
        }

        public class OptimizationResult;
        {
            public int OriginalCount { get; set; }
            public int FinalCount { get; set; }
            public int RemovedCount { get; set; }
            public int MergedCount { get; set; }
            public float ReductionPercentage { get; set; }
            public List<string> RemovedBlendShapes { get; set; }
            public List<MergePair> MergedPairs { get; set; }

            public OptimizationResult()
            {
                RemovedBlendShapes = new List<string>();
                MergedPairs = new List<MergePair>();
            }
        }

        public class MergePair;
        {
            public string BlendShapeId1 { get; set; }
            public string BlendShapeId2 { get; set; }
            public float Similarity { get; set; }
        }

        public class CompressionResult;
        {
            public CompressionFormat Format { get; set; }
            public long OriginalSize { get; set; }
            public long CompressedSize { get; set; }
            public float CompressionRatio { get; set; }
            public float Quality { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
        }

        public class ExportResult;
        {
            public ExportFormat Format { get; set; }
            public string FilePath { get; set; }
            public long FileSize { get; set; }
            public DateTime ExportTime { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
        }

        public class ImportResult;
        {
            public ImportFormat Format { get; set; }
            public string FilePath { get; set; }
            public int ImportedCount { get; set; }
            public DateTime ImportTime { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
        }

        public class ValidationReport;
        {
            public int TotalBlendShapes { get; set; }
            public int ValidBlendShapes { get; set; }
            public List<BlendShapeValidation> InvalidBlendShapes { get; set; }
            public List<string> Warnings { get; set; }
            public List<string> Errors { get; set; }
            public DateTime ValidationTime { get; set; }
            public bool IsValid { get; set; }

            public ValidationReport()
            {
                InvalidBlendShapes = new List<BlendShapeValidation>();
                Warnings = new List<string>();
                Errors = new List<string>();
            }
        }

        public class BlendShapeValidation;
        {
            public string BlendShapeId { get; set; }
            public string Name { get; set; }
            public bool IsValid { get; set; }
            public ValidationSeverity Severity { get; set; }
            public string Message { get; set; }
        }

        public class BlendShapeDefinition;
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public BlendShapeCategory Category { get; set; }
            public string Subcategory { get; set; }
            public BlendShapeType Type { get; set; }
            public List<Vector3> BaseVertices { get; set; }
            public List<Vector3> TargetVertices { get; set; }
            public List<int> VertexIndices { get; set; }
            public List<Vector3> Normals { get; set; }
            public List<Vector3> Tangents { get; set; }
            public List<Vector2> UVs { get; set; }
            public float Weight { get; set; }
            public float MinWeight { get; set; }
            public float MaxWeight { get; set; }
            public float DefaultWeight { get; set; }
            public float InfluenceRadius { get; set; }
            public string SymmetryGroup { get; set; }
            public string ParentBlendShapeId { get; set; }
            public List<string> ChildBlendShapes { get; set; }
            public List<BlendShapeInfluence> Influences { get; set; }
            public List<BlendShapeConstraint> Constraints { get; set; }
            public PerformanceImpact PerformanceImpact { get; set; }

            public BlendShapeDefinition()
            {
                BaseVertices = new List<Vector3>();
                TargetVertices = new List<Vector3>();
                VertexIndices = new List<int>();
                Normals = new List<Vector3>();
                Tangents = new List<Vector3>();
                UVs = new List<Vector2>();
                ChildBlendShapes = new List<string>();
                Influences = new List<BlendShapeInfluence>();
                Constraints = new List<BlendShapeConstraint>();
                MinWeight = 0f;
                MaxWeight = 1f;
                DefaultWeight = 0f;
                InfluenceRadius = 1f;
            }
        }

        public class BlendShapeUpdate;
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public float? Weight { get; set; }
            public float? MinWeight { get; set; }
            public float? MaxWeight { get; set; }
            public Dictionary<string, object> Metadata { get; set; }

            public BlendShapeUpdate()
            {
                Metadata = new Dictionary<string, object>();
            }
        }

        public class MeshDifference;
        {
            public string BaseMeshId { get; set; }
            public string TargetMeshId { get; set; }
            public List<Vector3> BaseVertices { get; set; }
            public List<Vector3> TargetVertices { get; set; }
            public List<int> VertexIndices { get; set; }
            public float Threshold { get; set; }

            public MeshDifference()
            {
                BaseVertices = new List<Vector3>();
                TargetVertices = new List<Vector3>();
                VertexIndices = new List<int>();
                Threshold = 0.001f;
            }
        }

        public class SculptData;
        {
            public List<Vector3> BrushPositions { get; set; }
            public List<float> BrushStrengths { get; set; }
            public float BrushRadius { get; set; }
            public string BrushFalloff { get; set; }
            public List<int> AffectedVertices { get; set; }

            public SculptData()
            {
                BrushPositions = new List<Vector3>();
                BrushStrengths = new List<float>();
                AffectedVertices = new List<int>();
            }
        }

        public class ExpressionTemplate;
        {
            public string TemplateId { get; set; }
            public string Name { get; set; }
            public List<ExpressionWeight> Weights { get; set; }
            public Dictionary<string, object> Parameters { get; set; }

            public ExpressionTemplate()
            {
                Weights = new List<ExpressionWeight>();
                Parameters = new Dictionary<string, object>();
            }
        }

        public class ExpressionWeight;
        {
            public string BlendShapeId { get; set; }
            public float Weight { get; set; }
            public string Side { get; set; } // "left", "right", "both"
        }

        public class MeshData;
        {
            public string MeshId { get; set; }
            public List<Vector3> Vertices { get; set; }
            public List<Vector3> Normals { get; set; }
            public List<Vector3> Tangents { get; set; }
            public List<Vector2> UVs { get; set; }
            public List<int> Indices { get; set; }
            public Dictionary<string, object> Metadata { get; set; }

            public MeshData()
            {
                Vertices = new List<Vector3>();
                Normals = new List<Vector3>();
                Tangents = new List<Vector3>();
                UVs = new List<Vector2>();
                Indices = new List<int>();
                Metadata = new Dictionary<string, object>();
            }

            public MeshData DeepClone()
            {
                return new MeshData;
                {
                    MeshId = MeshId,
                    Vertices = new List<Vector3>(Vertices),
                    Normals = new List<Vector3>(Normals),
                    Tangents = new List<Vector3>(Tangents),
                    UVs = new List<Vector2>(UVs),
                    Indices = new List<int>(Indices),
                    Metadata = new Dictionary<string, object>(Metadata)
                };
            }
        }

        public class BlendShapeEvaluation;
        {
            public string BlendShapeId { get; set; }
            public float MaximumDisplacement { get; set; }
            public float AverageDisplacement { get; set; }
            public float VolumeChange { get; set; }
            public float SurfaceAreaChange { get; set; }
            public List<Vector3> DisplacementVectors { get; set; }
            public DateTime EvaluatedAt { get; set; }

            public BlendShapeEvaluation()
            {
                DisplacementVectors = new List<Vector3>();
            }
        }

        public class BlendShapeAnalysis;
        {
            public string BlendShapeId { get; set; }
            public PerformanceImpact PerformanceImpact { get; set; }
            public float MemoryUsage { get; set; }
            public float ComputationalComplexity { get; set; }
            public List<string> Dependencies { get; set; }
            public Dictionary<string, float> Similarities { get; set; }

            public BlendShapeAnalysis()
            {
                Dependencies = new List<string>();
                Similarities = new Dictionary<string, float>();
            }
        }

        public class PerformanceReport;
        {
            public string SetId { get; set; }
            public PerformanceRating Rating { get; set; }
            public float EstimatedMemoryMB { get; set; }
            public float EstimatedComputeTimeMs { get; set; }
            public int TotalInfluences { get; set; }
            public Dictionary<BlendShapeCategory, int> InfluencesByCategory { get; set; }
            public List<PerformanceRecommendation> Recommendations { get; set; }

            public PerformanceReport()
            {
                InfluencesByCategory = new Dictionary<BlendShapeCategory, int>();
                Recommendations = new List<PerformanceRecommendation>();
            }
        }

        public class PerformanceRecommendation;
        {
            public string Type { get; set; }
            public string Description { get; set; }
            public float ExpectedImprovement { get; set; }
            public string Priority { get; set; } // "low", "medium", "high"
        }

        public class OptimizationOptions;
        {
            public float SimilarityThreshold { get; set; } = 0.9f;
            public bool RemoveUnused { get; set; } = true;
            public bool MergeSimilar { get; set; } = true;
            public bool CompressData { get; set; } = false;
            public float QualityPreservation { get; set; } = 0.95f;
            public int TargetCount { get; set; } = -1; // -1 for auto;
        }

        public class BlendShapeEngineConfig;
        {
            public bool EnableCaching { get; set; } = true;
            public int CacheDurationMinutes { get; set; } = 60;
            public bool RecalculateNormals { get; set; } = true;
            public float AutoOptimizationThreshold { get; set; } = 0.8f;
            public int MaxActiveSets { get; set; } = 100;
            public bool EnablePerformanceMonitoring { get; set; } = true;
        }

        #endregion;

        #region Event Definitions;

        public class BlendShapeCreatedEvent : IEvent;
        {
            public string BlendShapeId { get; set; }
            public string Name { get; set; }
            public BlendShapeCategory Category { get; set; }
            public int VertexCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class BlendShapeSetOptimizedEvent : IEvent;
        {
            public string SetId { get; set; }
            public int OriginalCount { get; set; }
            public int FinalCount { get; set; }
            public float ReductionPercentage { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class MeshUpdatedEvent : IEvent;
        {
            public string MeshId { get; set; }
            public int VertexCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class PerformanceThresholdExceededEvent : IEvent;
        {
            public string SetId { get; set; }
            public string MetricName { get; set; }
            public float Value { get; set; }
            public float Threshold { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class MeshUpdateAffectedBlendShapesEvent : IEvent;
        {
            public string MeshId { get; set; }
            public List<string> AffectedSetIds { get; set; }
            public DateTime Timestamp { get; set; }

            public MeshUpdateAffectedBlendShapesEvent()
            {
                AffectedSetIds = new List<string>();
            }
        }

        #endregion;

        #region Interfaces for Dependency Injection;

        public interface IBlendShapeRepository;
        {
            Task<BlendShape> GetBlendShapeAsync(string blendShapeId);
            Task<List<BlendShape>> GetCommonBlendShapesAsync();
            Task<bool> SaveBlendShapeAsync(BlendShape blendShape);
            Task<bool> DeleteBlendShapeAsync(string blendShapeId);
            Task<BlendShapeSet> GetBlendShapeSetAsync(string setId);
            Task<bool> SaveBlendShapeSetAsync(BlendShapeSet set);
        }

        public interface IMeshProcessor;
        {
            Task<List<Vector3>> CalculateNormalsAsync(List<Vector3> vertices, List<int> indices);
            Task<MeshData> CalculateDifferencesAsync(MeshData baseMesh, MeshData targetMesh);
            Task<MeshData> ApplyDeformationAsync(MeshData mesh, List<Vector3> displacements);
        }

        public interface IOptimizationEngine;
        {
            Task<OptimizationResult> OptimizeBlendShapeSetAsync(BlendShapeSet set, OptimizationOptions options);
            Task<CompressionResult> CompressBlendShapeSetAsync(BlendShapeSet set, CompressionFormat format, float quality);
            Task<bool> DecompressBlendShapeSetAsync(BlendShapeSet set);
        }

        #endregion;

        #region Custom Exceptions;

        public class BlendShapeEngineException : Exception
        {
            public BlendShapeEngineException(string message) : base(message) { }
            public BlendShapeEngineException(string message, Exception innerException) : base(message, innerException) { }
        }

        public class InvalidBlendShapeException : BlendShapeEngineException;
        {
            public InvalidBlendShapeException(string message) : base(message) { }
        }

        public class BlendShapeSetNotFoundException : BlendShapeEngineException;
        {
            public BlendShapeSetNotFoundException(string message) : base(message) { }
        }

        #endregion;

        #region JSON Converters;

        public class Vector3Converter : JsonConverter<Vector3>
        {
            public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new JsonException();

                reader.Read();
                var x = reader.GetSingle();
                reader.Read();
                var y = reader.GetSingle();
                reader.Read();
                var z = reader.GetSingle();
                reader.Read();

                if (reader.TokenType != JsonTokenType.EndArray)
                    throw new JsonException();

                return new Vector3(x, y, z);
            }

            public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(value.X);
                writer.WriteNumberValue(value.Y);
                writer.WriteNumberValue(value.Z);
                writer.WriteEndArray();
            }
        }

        public class Vector2Converter : JsonConverter<Vector2>
        {
            public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new JsonException();

                reader.Read();
                var x = reader.GetSingle();
                reader.Read();
                var y = reader.GetSingle();
                reader.Read();

                if (reader.TokenType != JsonTokenType.EndArray)
                    throw new JsonException();

                return new Vector2(x, y);
            }

            public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(value.X);
                writer.WriteNumberValue(value.Y);
                writer.WriteEndArray();
            }
        }

        #endregion;
    }
}
