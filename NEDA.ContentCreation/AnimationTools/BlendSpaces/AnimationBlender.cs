using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring;

namespace NEDA.Animation.AnimationTools.BlendSpaces;
{
    /// <summary>
    /// Animation blending sistemi - Çoklu animasyonları blend'ler;
    /// </summary>
    public interface IAnimationBlender;
    {
        /// <summary>
        /// İki animasyonu blend et;
        /// </summary>
        Task<BlendResult> BlendTwoAnimationsAsync(BlendRequest request);

        /// <summary>
        /// Çoklu animasyon blend et;
        /// </summary>
        Task<BlendResult> BlendMultipleAnimationsAsync(MultiBlendRequest request);

        /// <summary>
        /// Blend tree oluştur;
        /// </summary>
        Task<BlendTree> CreateBlendTreeAsync(BlendTreeDefinition definition);

        /// <summary>
        /// Blend tree'yi güncelle;
        /// </summary>
        Task<BlendTree> UpdateBlendTreeAsync(string blendTreeId, BlendTreeUpdateRequest updateRequest);

        /// <summary>
        /// Blend tree'yi çalıştır;
        /// </summary>
        Task<AnimationOutput> EvaluateBlendTreeAsync(string blendTreeId, BlendParameters parameters);

        /// <summary>
        /// Blend space oluştur;
        /// </summary>
        Task<BlendSpace> CreateBlendSpaceAsync(BlendSpaceDefinition definition);

        /// <summary>
        /// Blend space'de nokta değerlendir;
        /// </summary>
        Task<AnimationOutput> EvaluateBlendSpacePointAsync(string blendSpaceId, float x, float y);

        /// <summary>
        /// Real-time blend parametreleri güncelle;
        /// </summary>
        Task UpdateBlendParametersAsync(string blendInstanceId, BlendParameters parameters);

        /// <summary>
        /// Smooth transition uygula;
        /// </summary>
        Task<TransitionResult> ApplySmoothTransitionAsync(TransitionRequest request);

        /// <summary>
        /// Layer blend uygula;
        /// </summary>
        Task<LayerBlendResult> ApplyLayerBlendAsync(LayerBlendRequest request);

        /// <summary>
        /// Additive blend uygula;
        /// </summary>
        Task<AdditiveBlendResult> ApplyAdditiveBlendAsync(AdditiveBlendRequest request);

        /// <summary>
        /// Inverse kinematics blend uygula;
        /// </summary>
        Task<IKBlendResult> ApplyIKBlendAsync(IKBlendRequest request);

        /// <summary>
        /// Motion matching blend uygula;
        /// </summary>
        Task<MotionMatchingResult> ApplyMotionMatchingAsync(MotionMatchingRequest request);

        /// <summary>
        /// Blend kalitesini optimize et;
        /// </summary>
        Task<OptimizationResult> OptimizeBlendQualityAsync(string blendInstanceId, OptimizationOptions options);

        /// <summary>
        /// Blend cache'i temizle;
        /// </summary>
        Task ClearBlendCacheAsync(string blendInstanceId = null);

        /// <summary>
        /// Blend performans metriği al;
        /// </summary>
        Task<BlendPerformanceMetrics> GetPerformanceMetricsAsync(string blendInstanceId);

        /// <summary>
        /// Blend debug bilgisi al;
        /// </summary>
        Task<BlendDebugInfo> GetDebugInfoAsync(string blendInstanceId);
    }

    /// <summary>
    /// Animation blender implementasyonu;
    /// </summary>
    public class AnimationBlender : IAnimationBlender, IDisposable;
    {
        private readonly IAnimationRepository _animationRepository;
        private readonly IBlendTreeRepository _blendTreeRepository;
        private readonly IBlendSpaceRepository _blendSpaceRepository;
        private readonly ISkeletonManager _skeletonManager;
        private readonly IWeightCalculator _weightCalculator;
        private readonly IInterpolationEngine _interpolationEngine;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ILogger<AnimationBlender> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly ICacheManager _cacheManager;
        private readonly object _syncLock = new object();
        private readonly Dictionary<string, BlendInstance> _activeBlends = new Dictionary<string, BlendInstance>();
        private bool _disposed = false;

        public AnimationBlender(
            IAnimationRepository animationRepository,
            IBlendTreeRepository blendTreeRepository,
            IBlendSpaceRepository blendSpaceRepository,
            ISkeletonManager skeletonManager,
            IWeightCalculator weightCalculator,
            IInterpolationEngine interpolationEngine,
            IPerformanceMonitor performanceMonitor,
            ILogger<AnimationBlender> logger,
            IErrorReporter errorReporter,
            ICacheManager cacheManager)
        {
            _animationRepository = animationRepository ?? throw new ArgumentNullException(nameof(animationRepository));
            _blendTreeRepository = blendTreeRepository ?? throw new ArgumentNullException(nameof(blendTreeRepository));
            _blendSpaceRepository = blendSpaceRepository ?? throw new ArgumentNullException(nameof(blendSpaceRepository));
            _skeletonManager = skeletonManager ?? throw new ArgumentNullException(nameof(skeletonManager));
            _weightCalculator = weightCalculator ?? throw new ArgumentNullException(nameof(weightCalculator));
            _interpolationEngine = interpolationEngine ?? throw new ArgumentNullException(nameof(interpolationEngine));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        }

        /// <summary>
        /// İki animasyonu blend et;
        /// </summary>
        public async Task<BlendResult> BlendTwoAnimationsAsync(BlendRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("BlendTwoAnimations");

            try
            {
                _logger.LogInformation($"Blending animations: {request.AnimationAId} and {request.AnimationBId}");

                ValidateBlendRequest(request);

                // Animasyonları yükle;
                var animationA = await LoadAnimationAsync(request.AnimationAId);
                var animationB = await LoadAnimationAsync(request.AnimationBId);

                // Skeleton uyumluluğunu kontrol et;
                await ValidateSkeletonCompatibility(animationA, animationB);

                // Weight hesapla;
                var weight = await _weightCalculator.CalculateBlendWeightAsync(
                    request.BlendFactor,
                    request.CurveType,
                    request.EasingFunction);

                // Blend pozisyonları;
                var blendedPoses = await BlendPosesAsync(
                    animationA.Poses,
                    animationB.Poses,
                    weight,
                    request.BlendMode);

                // Blend event'leri;
                var blendedEvents = await BlendAnimationEventsAsync(
                    animationA.Events,
                    animationB.Events,
                    weight);

                // Blend metadata;
                var metadata = CreateBlendMetadata(animationA, animationB, weight);

                var result = new BlendResult;
                {
                    Success = true,
                    BlendedAnimation = new BlendedAnimation;
                    {
                        Id = Guid.NewGuid().ToString(),
                        SourceAnimationIds = new List<string> { animationA.Id, animationB.Id },
                        Poses = blendedPoses,
                        Events = blendedEvents,
                        Duration = CalculateBlendedDuration(animationA.Duration, animationB.Duration, weight),
                        BlendWeight = weight,
                        BlendMode = request.BlendMode,
                        Metadata = metadata,
                        CreatedAt = DateTime.UtcNow;
                    },
                    PerformanceMetrics = stopwatch.StopAndGetMetrics()
                };

                // Cache'e kaydet;
                await CacheBlendResultAsync(result);

                _logger.LogInformation($"Blend completed successfully. Result ID: {result.BlendedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error blending animations {request.AnimationAId} and {request.AnimationBId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationBlendFailed, "Failed to blend animations", ex);
            }
        }

        /// <summary>
        /// Çoklu animasyon blend et;
        /// </summary>
        public async Task<BlendResult> BlendMultipleAnimationsAsync(MultiBlendRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("BlendMultipleAnimations");

            try
            {
                _logger.LogInformation($"Blending {request.Animations.Count} animations");

                if (request.Animations.Count < 2)
                {
                    throw new NEDAException(ErrorCode.InvalidBlendRequest, "At least 2 animations required for blending");
                }

                // Tüm animasyonları yükle;
                var animations = new List<AnimationData>();
                foreach (var animInfo in request.Animations)
                {
                    var animation = await LoadAnimationAsync(animInfo.AnimationId);
                    animations.Add(animation);
                }

                // Skeleton uyumluluğunu kontrol et;
                await ValidateMultipleSkeletonCompatibility(animations);

                // Weights hesapla;
                var weights = await _weightCalculator.CalculateMultiBlendWeightsAsync(
                    request.Animations.Select(a => a.Weight).ToList(),
                    request.CurveType);

                // Çoklu blend uygula;
                var blendedPoses = await MultiBlendPosesAsync(animations, weights, request.BlendMode);

                // Event'leri blend et;
                var blendedEvents = await MultiBlendEventsAsync(animations, weights);

                // Sonuç oluştur;
                var result = new BlendResult;
                {
                    Success = true,
                    BlendedAnimation = new BlendedAnimation;
                    {
                        Id = Guid.NewGuid().ToString(),
                        SourceAnimationIds = animations.Select(a => a.Id).ToList(),
                        Poses = blendedPoses,
                        Events = blendedEvents,
                        Duration = CalculateMultiBlendedDuration(animations, weights),
                        BlendWeight = weights.Average(),
                        BlendMode = request.BlendMode,
                        Metadata = new Dictionary<string, object>
                        {
                            { "BlendType", "MultiBlend" },
                            { "AnimationCount", animations.Count },
                            { "Weights", weights }
                        },
                        CreatedAt = DateTime.UtcNow;
                    },
                    PerformanceMetrics = stopwatch.StopAndGetMetrics()
                };

                _logger.LogInformation($"Multi-blend completed. Result ID: {result.BlendedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error blending multiple animations");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.MultiBlendFailed, "Failed to blend multiple animations", ex);
            }
        }

        /// <summary>
        /// Blend tree oluştur;
        /// </summary>
        public async Task<BlendTree> CreateBlendTreeAsync(BlendTreeDefinition definition)
        {
            var stopwatch = _performanceMonitor.StartOperation("CreateBlendTree");

            try
            {
                _logger.LogInformation($"Creating blend tree: {definition.Name}");

                ValidateBlendTreeDefinition(definition);

                // Blend tree oluştur;
                var blendTree = new BlendTree;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = definition.Name,
                    Description = definition.Description,
                    Type = definition.Type,
                    RootNode = await CreateBlendTreeNodeAsync(definition.RootNode),
                    Parameters = definition.Parameters,
                    OptimizationLevel = definition.OptimizationLevel,
                    CacheEnabled = definition.CacheEnabled,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Version = 1;
                };

                // Optimizasyon uygula;
                if (definition.OptimizationLevel > OptimizationLevel.None)
                {
                    await OptimizeBlendTreeAsync(blendTree);
                }

                // Kaydet;
                var result = await _blendTreeRepository.AddAsync(blendTree);

                // Cache'e al;
                if (definition.CacheEnabled)
                {
                    await CacheBlendTreeAsync(result);
                }

                _logger.LogInformation($"Blend tree created: {result.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating blend tree: {definition.Name}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, definition);
                throw new NEDAException(ErrorCode.BlendTreeCreationFailed, "Failed to create blend tree", ex);
            }
        }

        /// <summary>
        /// Blend tree'yi güncelle;
        /// </summary>
        public async Task<BlendTree> UpdateBlendTreeAsync(string blendTreeId, BlendTreeUpdateRequest updateRequest)
        {
            try
            {
                _logger.LogInformation($"Updating blend tree: {blendTreeId}");

                var blendTree = await _blendTreeRepository.GetByIdAsync(blendTreeId);
                if (blendTree == null)
                {
                    throw new NEDAException(ErrorCode.BlendTreeNotFound, $"Blend tree {blendTreeId} not found");
                }

                // Aktif blend'leri kontrol et;
                if (IsBlendTreeActive(blendTreeId))
                {
                    throw new NEDAException(ErrorCode.BlendTreeActive,
                        $"Cannot update active blend tree {blendTreeId}");
                }

                // Güncelleme uygula;
                if (!string.IsNullOrEmpty(updateRequest.Name))
                {
                    blendTree.Name = updateRequest.Name;
                }

                if (!string.IsNullOrEmpty(updateRequest.Description))
                {
                    blendTree.Description = updateRequest.Description;
                }

                if (updateRequest.RootNode != null)
                {
                    blendTree.RootNode = await CreateBlendTreeNodeAsync(updateRequest.RootNode);
                }

                if (updateRequest.Parameters != null)
                {
                    blendTree.Parameters = updateRequest.Parameters;
                }

                if (updateRequest.OptimizationLevel.HasValue)
                {
                    blendTree.OptimizationLevel = updateRequest.OptimizationLevel.Value;
                }

                if (updateRequest.CacheEnabled.HasValue)
                {
                    blendTree.CacheEnabled = updateRequest.CacheEnabled.Value;
                }

                blendTree.UpdatedAt = DateTime.UtcNow;
                blendTree.Version++;

                // Optimizasyon uygula;
                if (blendTree.OptimizationLevel > OptimizationLevel.None)
                {
                    await OptimizeBlendTreeAsync(blendTree);
                }

                // Güncelle;
                var result = await _blendTreeRepository.UpdateAsync(blendTree);

                // Cache'i güncelle;
                await UpdateBlendTreeCache(blendTreeId);

                _logger.LogInformation($"Blend tree updated: {blendTreeId}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating blend tree: {blendTreeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { blendTreeId, updateRequest });
                throw new NEDAException(ErrorCode.BlendTreeUpdateFailed, "Failed to update blend tree", ex);
            }
        }

        /// <summary>
        /// Blend tree'yi çalıştır;
        /// </summary>
        public async Task<AnimationOutput> EvaluateBlendTreeAsync(string blendTreeId, BlendParameters parameters)
        {
            var stopwatch = _performanceMonitor.StartOperation("EvaluateBlendTree");

            try
            {
                // Cache'den kontrol et;
                var cacheKey = $"blendtree_eval_{blendTreeId}_{parameters.GetHashCode()}";
                var cached = await _cacheManager.GetAsync<AnimationOutput>(cacheKey);
                if (cached != null)
                {
                    _logger.LogDebug($"Cache hit for blend tree evaluation: {blendTreeId}");
                    return cached;
                }

                // Blend tree'yi yükle;
                var blendTree = await LoadBlendTreeAsync(blendTreeId);

                // Parametreleri validate et;
                ValidateBlendParameters(blendTree, parameters);

                // Blend tree'yi değerlendir;
                var result = await EvaluateBlendTreeNodeAsync(blendTree.RootNode, parameters);

                // Sonuçları optimize et;
                if (blendTree.OptimizationLevel > OptimizationLevel.None)
                {
                    result = await OptimizeAnimationOutputAsync(result, blendTree.OptimizationLevel);
                }

                // Aktif blend olarak kaydet;
                RegisterActiveBlend(blendTreeId, result, parameters);

                // Cache'e kaydet;
                if (blendTree.CacheEnabled)
                {
                    await _cacheManager.SetAsync(cacheKey, result, TimeSpan.FromMinutes(10));
                }

                // Performans metriği ekle;
                result.PerformanceMetrics = stopwatch.StopAndGetMetrics();

                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error evaluating blend tree: {blendTreeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { blendTreeId, parameters });
                throw new NEDAException(ErrorCode.BlendTreeEvaluationFailed, "Failed to evaluate blend tree", ex);
            }
        }

        /// <summary>
        /// Blend space oluştur;
        /// </summary>
        public async Task<BlendSpace> CreateBlendSpaceAsync(BlendSpaceDefinition definition)
        {
            try
            {
                _logger.LogInformation($"Creating blend space: {definition.Name}");

                ValidateBlendSpaceDefinition(definition);

                // Animasyon noktalarını yükle;
                var animationPoints = new List<BlendSpacePoint>();
                foreach (var pointDef in definition.Points)
                {
                    var animation = await LoadAnimationAsync(pointDef.AnimationId);
                    var point = new BlendSpacePoint;
                    {
                        Id = Guid.NewGuid().ToString(),
                        AnimationId = pointDef.AnimationId,
                        X = pointDef.X,
                        Y = pointDef.Y,
                        AnimationData = animation,
                        Weight = 1.0f;
                    };
                    animationPoints.Add(point);
                }

                // Blend space oluştur;
                var blendSpace = new BlendSpace;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = definition.Name,
                    Description = definition.Description,
                    Type = definition.Type,
                    Dimensions = definition.Dimensions,
                    Points = animationPoints,
                    InterpolationMethod = definition.InterpolationMethod,
                    Boundaries = definition.Boundaries,
                    GridResolution = definition.GridResolution,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                };

                // Grid oluştur;
                await GenerateBlendSpaceGridAsync(blendSpace);

                // Kaydet;
                var result = await _blendSpaceRepository.AddAsync(blendSpace);

                _logger.LogInformation($"Blend space created: {result.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating blend space: {definition.Name}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, definition);
                throw new NEDAException(ErrorCode.BlendSpaceCreationFailed, "Failed to create blend space", ex);
            }
        }

        /// <summary>
        /// Blend space'de nokta değerlendir;
        /// </summary>
        public async Task<AnimationOutput> EvaluateBlendSpacePointAsync(string blendSpaceId, float x, float y)
        {
            var stopwatch = _performanceMonitor.StartOperation("EvaluateBlendSpacePoint");

            try
            {
                // Blend space'i yükle;
                var blendSpace = await _blendSpaceRepository.GetByIdAsync(blendSpaceId);
                if (blendSpace == null)
                {
                    throw new NEDAException(ErrorCode.BlendSpaceNotFound, $"Blend space {blendSpaceId} not found");
                }

                // Sınırları kontrol et;
                if (!IsPointInBounds(x, y, blendSpace.Boundaries))
                {
                    throw new NEDAException(ErrorCode.PointOutOfBounds,
                        $"Point ({x}, {y}) is out of blend space bounds");
                }

                // En yakın noktaları bul;
                var nearestPoints = FindNearestBlendSpacePoints(blendSpace, x, y, 3);

                // Weights hesapla;
                var weights = CalculateBlendSpaceWeights(nearestPoints, x, y);

                // Animasyonları blend et;
                var animations = nearestPoints.Select(p => p.AnimationData).ToList();
                var blendedPoses = await MultiBlendPosesAsync(animations, weights, BlendMode.Linear);

                var result = new AnimationOutput;
                {
                    Success = true,
                    Poses = blendedPoses,
                    BlendWeights = weights,
                    EvaluationPoint = new Vector2(x, y),
                    PerformanceMetrics = stopwatch.StopAndGetMetrics()
                };

                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error evaluating blend space point: ({x}, {y})");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { blendSpaceId, x, y });
                throw new NEDAException(ErrorCode.BlendSpaceEvaluationFailed,
                    "Failed to evaluate blend space point", ex);
            }
        }

        /// <summary>
        /// Real-time blend parametreleri güncelle;
        /// </summary>
        public async Task UpdateBlendParametersAsync(string blendInstanceId, BlendParameters parameters)
        {
            try
            {
                lock (_syncLock)
                {
                    if (!_activeBlends.TryGetValue(blendInstanceId, out var blendInstance))
                    {
                        throw new NEDAException(ErrorCode.BlendInstanceNotFound,
                            $"Active blend instance {blendInstanceId} not found");
                    }

                    // Parametreleri güncelle;
                    blendInstance.Parameters = parameters;
                    blendInstance.LastUpdate = DateTime.UtcNow;

                    // Real-time blend hesapla;
                    blendInstance.CurrentOutput = CalculateRealTimeBlend(blendInstance);
                }

                _logger.LogDebug($"Updated blend parameters for instance: {blendInstanceId}");
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating blend parameters for instance: {blendInstanceId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low,
                    new { blendInstanceId, parameters });
                throw new NEDAException(ErrorCode.BlendParameterUpdateFailed,
                    "Failed to update blend parameters", ex);
            }
        }

        /// <summary>
        /// Smooth transition uygula;
        /// </summary>
        public async Task<TransitionResult> ApplySmoothTransitionAsync(TransitionRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("ApplySmoothTransition");

            try
            {
                _logger.LogInformation($"Applying smooth transition from {request.SourceAnimationId} to {request.TargetAnimationId}");

                // Animasyonları yükle;
                var sourceAnim = await LoadAnimationAsync(request.SourceAnimationId);
                var targetAnim = await LoadAnimationAsync(request.TargetAnimationId);

                // Transition curve oluştur;
                var transitionCurve = CreateTransitionCurve(request.TransitionType, request.Duration);

                // Transition uygula;
                var transitionFrames = new List<AnimationFrame>();
                var transitionEvents = new List<AnimationEvent>();

                for (float t = 0; t <= 1.0f; t += 1.0f / request.FrameCount)
                {
                    var blendFactor = transitionCurve.Evaluate(t);

                    // Frame blend et;
                    var frame = await BlendAnimationFramesAsync(
                        sourceAnim.GetFrameAtTime(t * sourceAnim.Duration),
                        targetAnim.GetFrameAtTime(t * targetAnim.Duration),
                        blendFactor,
                        request.BlendMode);

                    transitionFrames.Add(frame);

                    // Event blend et;
                    var events = await BlendFrameEventsAsync(
                        sourceAnim.GetEventsAtTime(t * sourceAnim.Duration),
                        targetAnim.GetEventsAtTime(t * targetAnim.Duration),
                        blendFactor);

                    transitionEvents.AddRange(events);
                }

                var result = new TransitionResult;
                {
                    Success = true,
                    TransitionAnimation = new TransitionAnimation;
                    {
                        Id = Guid.NewGuid().ToString(),
                        SourceAnimationId = request.SourceAnimationId,
                        TargetAnimationId = request.TargetAnimationId,
                        Frames = transitionFrames,
                        Events = transitionEvents,
                        Duration = request.Duration,
                        TransitionType = request.TransitionType,
                        CreatedAt = DateTime.UtcNow;
                    },
                    PerformanceMetrics = stopwatch.StopAndGetMetrics()
                };

                _logger.LogInformation($"Smooth transition completed. Result ID: {result.TransitionAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying smooth transition");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.TransitionFailed, "Failed to apply smooth transition", ex);
            }
        }

        /// <summary>
        /// Layer blend uygula;
        /// </summary>
        public async Task<LayerBlendResult> ApplyLayerBlendAsync(LayerBlendRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("ApplyLayerBlend");

            try
            {
                _logger.LogInformation($"Applying layer blend with {request.Layers.Count} layers");

                // Base layer'ı yükle;
                var baseAnimation = await LoadAnimationAsync(request.BaseAnimationId);
                var blendedPoses = baseAnimation.Poses.ToList();

                // Her layer için blend uygula;
                foreach (var layer in request.Layers)
                {
                    var layerAnimation = await LoadAnimationAsync(layer.AnimationId);

                    // Mask kontrolü;
                    var mask = layer.Mask ?? CreateFullBodyMask();

                    // Layer blend uygula;
                    blendedPoses = await ApplyLayerBlendToPosesAsync(
                        blendedPoses,
                        layerAnimation.Poses,
                        layer.BlendWeight,
                        mask,
                        layer.BlendMode);
                }

                var result = new LayerBlendResult;
                {
                    Success = true,
                    BlendedAnimation = new BlendedAnimation;
                    {
                        Id = Guid.NewGuid().ToString(),
                        SourceAnimationIds = new List<string> { request.BaseAnimationId }
                            .Concat(request.Layers.Select(l => l.AnimationId)).ToList(),
                        Poses = blendedPoses,
                        Duration = baseAnimation.Duration,
                        BlendMode = BlendMode.Layered,
                        CreatedAt = DateTime.UtcNow;
                    },
                    LayerCount = request.Layers.Count,
                    PerformanceMetrics = stopwatch.StopAndGetMetrics()
                };

                _logger.LogInformation($"Layer blend completed. Result ID: {result.BlendedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying layer blend");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.LayerBlendFailed, "Failed to apply layer blend", ex);
            }
        }

        /// <summary>
        /// Additive blend uygula;
        /// </summary>
        public async Task<AdditiveBlendResult> ApplyAdditiveBlendAsync(AdditiveBlendRequest request)
        {
            try
            {
                _logger.LogInformation($"Applying additive blend: {request.BaseAnimationId} + {request.AdditiveAnimationId}");

                // Animasyonları yükle;
                var baseAnimation = await LoadAnimationAsync(request.BaseAnimationId);
                var additiveAnimation = await LoadAnimationAsync(request.AdditiveAnimationId);

                // Additive blend uygula;
                var blendedPoses = await ApplyAdditiveBlendToPosesAsync(
                    baseAnimation.Poses,
                    additiveAnimation.Poses,
                    request.Intensity);

                var result = new AdditiveBlendResult;
                {
                    Success = true,
                    BlendedAnimation = new BlendedAnimation;
                    {
                        Id = Guid.NewGuid().ToString(),
                        SourceAnimationIds = new List<string> { request.BaseAnimationId, request.AdditiveAnimationId },
                        Poses = blendedPoses,
                        Duration = baseAnimation.Duration,
                        BlendMode = BlendMode.Additive,
                        Metadata = new Dictionary<string, object>
                        {
                            { "AdditiveIntensity", request.Intensity },
                            { "ReferencePose", request.UseReferencePose }
                        },
                        CreatedAt = DateTime.UtcNow;
                    },
                    IntensityApplied = request.Intensity;
                };

                _logger.LogInformation($"Additive blend completed. Result ID: {result.BlendedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying additive blend");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AdditiveBlendFailed, "Failed to apply additive blend", ex);
            }
        }

        /// <summary>
        /// Inverse kinematics blend uygula;
        /// </summary>
        public async Task<IKBlendResult> ApplyIKBlendAsync(IKBlendRequest request)
        {
            try
            {
                _logger.LogInformation($"Applying IK blend for {request.EffectorCount} effectors");

                // Animasyonu yükle;
                var baseAnimation = await LoadAnimationAsync(request.BaseAnimationId);

                // IK hedeflerini uygula;
                var ikPoses = await ApplyIKTargetsToPosesAsync(
                    baseAnimation.Poses,
                    request.IKTargets,
                    request.BlendWeight,
                    request.IterationCount);

                var result = new IKBlendResult;
                {
                    Success = true,
                    BlendedAnimation = new BlendedAnimation;
                    {
                        Id = Guid.NewGuid().ToString(),
                        SourceAnimationIds = new List<string> { request.BaseAnimationId },
                        Poses = ikPoses,
                        Duration = baseAnimation.Duration,
                        BlendMode = BlendMode.IK,
                        Metadata = new Dictionary<string, object>
                        {
                            { "IKTargetCount", request.IKTargets.Count },
                            { "IterationCount", request.IterationCount },
                            { "Tolerance", request.Tolerance }
                        },
                        CreatedAt = DateTime.UtcNow;
                    },
                    EffectorsApplied = request.IKTargets.Count,
                    MaxError = CalculateIKError(ikPoses, request.IKTargets)
                };

                _logger.LogInformation($"IK blend completed. Result ID: {result.BlendedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying IK blend");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.IKBlendFailed, "Failed to apply IK blend", ex);
            }
        }

        /// <summary>
        /// Motion matching blend uygula;
        /// </summary>
        public async Task<MotionMatchingResult> ApplyMotionMatchingAsync(MotionMatchingRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("ApplyMotionMatching");

            try
            {
                _logger.LogInformation($"Applying motion matching with {request.MotionDatabaseId}");

                // Motion database'i yükle;
                var motionDatabase = await LoadMotionDatabaseAsync(request.MotionDatabaseId);

                // En iyi eşleşmeyi bul;
                var bestMatch = await FindBestMotionMatchAsync(
                    motionDatabase,
                    request.CurrentPose,
                    request.DesiredVelocity,
                    request.FutureTrajectory);

                // Blend uygula;
                var blendedPoses = await ApplyMotionMatchingBlendAsync(
                    request.CurrentPose,
                    bestMatch.Poses,
                    request.BlendDuration);

                var result = new MotionMatchingResult;
                {
                    Success = true,
                    MatchedAnimationId = bestMatch.AnimationId,
                    BlendedAnimation = new BlendedAnimation;
                    {
                        Id = Guid.NewGuid().ToString(),
                        SourceAnimationIds = new List<string> { bestMatch.AnimationId },
                        Poses = blendedPoses,
                        Duration = bestMatch.Duration,
                        BlendMode = BlendMode.MotionMatching,
                        CreatedAt = DateTime.UtcNow;
                    },
                    MatchScore = bestMatch.MatchScore,
                    BlendDuration = request.BlendDuration,
                    PerformanceMetrics = stopwatch.StopAndGetMetrics()
                };

                _logger.LogInformation($"Motion matching completed. Match score: {bestMatch.MatchScore}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying motion matching");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.MotionMatchingFailed, "Failed to apply motion matching", ex);
            }
        }

        /// <summary>
        /// Blend kalitesini optimize et;
        /// </summary>
        public async Task<OptimizationResult> OptimizeBlendQualityAsync(string blendInstanceId, OptimizationOptions options)
        {
            try
            {
                _logger.LogInformation($"Optimizing blend quality for instance: {blendInstanceId}");

                BlendInstance blendInstance;
                lock (_syncLock)
                {
                    if (!_activeBlends.TryGetValue(blendInstanceId, out blendInstance))
                    {
                        throw new NEDAException(ErrorCode.BlendInstanceNotFound,
                            $"Active blend instance {blendInstanceId} not found");
                    }
                }

                // Optimizasyon uygula;
                var optimizedOutput = await ApplyBlendOptimizationAsync(
                    blendInstance.CurrentOutput,
                    options);

                // Güncelle;
                lock (_syncLock)
                {
                    blendInstance.CurrentOutput = optimizedOutput;
                    blendInstance.OptimizationApplied = true;
                }

                var result = new OptimizationResult;
                {
                    Success = true,
                    OriginalMetrics = CalculateBlendMetrics(blendInstance.CurrentOutput),
                    OptimizedMetrics = CalculateBlendMetrics(optimizedOutput),
                    OptimizationType = options.Type,
                    TimeSaved = CalculateOptimizationTimeSave(blendInstance.CurrentOutput, optimizedOutput)
                };

                _logger.LogInformation($"Blend optimization completed. Time saved: {result.TimeSaved}ms");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error optimizing blend quality for instance: {blendInstanceId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low,
                    new { blendInstanceId, options });
                throw new NEDAException(ErrorCode.BlendOptimizationFailed, "Failed to optimize blend quality", ex);
            }
        }

        /// <summary>
        /// Blend cache'i temizle;
        /// </summary>
        public async Task ClearBlendCacheAsync(string blendInstanceId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(blendInstanceId))
                {
                    // Tüm cache'i temizle;
                    await _cacheManager.ClearByPatternAsync("blend_*");
                    _logger.LogInformation("All blend cache cleared");
                }
                else;
                {
                    // Belirli instance'ın cache'ini temizle;
                    await _cacheManager.RemoveAsync($"blend_instance_{blendInstanceId}");
                    await _cacheManager.RemoveByPatternAsync($"blendtree_*_{blendInstanceId}_*");
                    _logger.LogInformation($"Blend cache cleared for instance: {blendInstanceId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error clearing blend cache");
                // Cache temizleme hatası kritik değil;
            }
        }

        /// <summary>
        /// Blend performans metriği al;
        /// </summary>
        public async Task<BlendPerformanceMetrics> GetPerformanceMetricsAsync(string blendInstanceId)
        {
            try
            {
                lock (_syncLock)
                {
                    if (!_activeBlends.TryGetValue(blendInstanceId, out var blendInstance))
                    {
                        throw new NEDAException(ErrorCode.BlendInstanceNotFound,
                            $"Active blend instance {blendInstanceId} not found");
                    }

                    return CalculatePerformanceMetrics(blendInstance);
                }
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting performance metrics for instance: {blendInstanceId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low, new { blendInstanceId });
                throw new NEDAException(ErrorCode.MetricsRetrievalFailed,
                    "Failed to retrieve performance metrics", ex);
            }
        }

        /// <summary>
        /// Blend debug bilgisi al;
        /// </summary>
        public async Task<BlendDebugInfo> GetDebugInfoAsync(string blendInstanceId)
        {
            try
            {
                BlendInstance blendInstance;
                lock (_syncLock)
                {
                    if (!_activeBlends.TryGetValue(blendInstanceId, out blendInstance))
                    {
                        throw new NEDAException(ErrorCode.BlendInstanceNotFound,
                            $"Active blend instance {blendInstanceId} not found");
                    }
                }

                var debugInfo = new BlendDebugInfo;
                {
                    InstanceId = blendInstanceId,
                    Type = blendInstance.Type,
                    Parameters = blendInstance.Parameters,
                    LastUpdate = blendInstance.LastUpdate,
                    FrameCount = blendInstance.CurrentOutput?.Poses?.Count ?? 0,
                    CurrentWeights = blendInstance.CurrentOutput?.BlendWeights,
                    PerformanceMetrics = CalculatePerformanceMetrics(blendInstance),
                    MemoryUsage = CalculateMemoryUsage(blendInstance),
                    ActiveBlendsCount = _activeBlends.Count,
                    CacheStatus = await GetCacheStatusAsync(blendInstanceId)
                };

                return debugInfo;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting debug info for instance: {blendInstanceId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low, new { blendInstanceId });
                throw new NEDAException(ErrorCode.DebugInfoRetrievalFailed,
                    "Failed to retrieve debug info", ex);
            }
        }

        #region Private Methods;

        private async Task<AnimationData> LoadAnimationAsync(string animationId)
        {
            var cacheKey = $"animation_{animationId}";
            var cached = await _cacheManager.GetAsync<AnimationData>(cacheKey);
            if (cached != null)
            {
                return cached;
            }

            var animation = await _animationRepository.GetByIdAsync(animationId);
            if (animation == null)
            {
                throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {animationId} not found");
            }

            await _cacheManager.SetAsync(cacheKey, animation, TimeSpan.FromMinutes(30));
            return animation;
        }

        private async Task ValidateSkeletonCompatibility(AnimationData animA, AnimationData animB)
        {
            var skeletonA = await _skeletonManager.GetSkeletonAsync(animA.SkeletonId);
            var skeletonB = await _skeletonManager.GetSkeletonAsync(animB.SkeletonId);

            if (!skeletonA.IsCompatibleWith(skeletonB))
            {
                throw new NEDAException(ErrorCode.SkeletonIncompatible,
                    $"Skeletons {animA.SkeletonId} and {animB.SkeletonId} are not compatible");
            }
        }

        private async Task ValidateMultipleSkeletonCompatibility(List<AnimationData> animations)
        {
            if (animations.Count < 2) return;

            var firstSkeleton = await _skeletonManager.GetSkeletonAsync(animations[0].SkeletonId);

            for (int i = 1; i < animations.Count; i++)
            {
                var skeleton = await _skeletonManager.GetSkeletonAsync(animations[i].SkeletonId);
                if (!firstSkeleton.IsCompatibleWith(skeleton))
                {
                    throw new NEDAException(ErrorCode.SkeletonIncompatible,
                        $"Animation {i} has incompatible skeleton");
                }
            }
        }

        private async Task<List<Pose>> BlendPosesAsync(
            List<Pose> posesA,
            List<Pose> posesB,
            float weight,
            BlendMode blendMode)
        {
            if (posesA.Count != posesB.Count)
            {
                // Frame sayıları farklıysa, interpolasyon uygula;
                posesB = await _interpolationEngine.ResamplePosesAsync(posesB, posesA.Count);
            }

            var blendedPoses = new List<Pose>();
            for (int i = 0; i < posesA.Count; i++)
            {
                var blendedPose = await _interpolationEngine.BlendPoseAsync(
                    posesA[i],
                    posesB[i],
                    weight,
                    blendMode);
                blendedPoses.Add(blendedPose);
            }

            return blendedPoses;
        }

        private async Task<List<Pose>> MultiBlendPosesAsync(
            List<AnimationData> animations,
            List<float> weights,
            BlendMode blendMode)
        {
            if (animations.Count != weights.Count)
            {
                throw new ArgumentException("Animations and weights count must match");
            }

            // En uzun animasyonu bul;
            var maxFrames = animations.Max(a => a.Poses.Count);
            var resampledAnimations = new List<List<Pose>>();

            // Tüm animasyonları aynı frame sayısına getir;
            foreach (var animation in animations)
            {
                var resampledPoses = animation.Poses.Count == maxFrames;
                    ? animation.Poses;
                    : await _interpolationEngine.ResamplePosesAsync(animation.Poses, maxFrames);

                resampledAnimations.Add(resampledPoses);
            }

            // Çoklu blend uygula;
            var blendedPoses = new List<Pose>();
            for (int frameIndex = 0; frameIndex < maxFrames; frameIndex++)
            {
                var framePoses = resampledAnimations.Select(a => a[frameIndex]).ToList();
                var blendedPose = await _interpolationEngine.MultiBlendPoseAsync(framePoses, weights, blendMode);
                blendedPoses.Add(blendedPose);
            }

            return blendedPoses;
        }

        private async Task<List<AnimationEvent>> BlendAnimationEventsAsync(
            List<AnimationEvent> eventsA,
            List<AnimationEvent> eventsB,
            float weight)
        {
            var blendedEvents = new List<AnimationEvent>();

            // Event'leri zaman bazlı merge et;
            var allEvents = eventsA;
                .Select(e => new { Event = e, Source = "A", Weight = weight })
                .Concat(eventsB.Select(e => new { Event = e, Source = "B", Weight = 1 - weight }))
                .OrderBy(e => e.Event.Time)
                .ToList();

            foreach (var eventGroup in allEvents.GroupBy(e => e.Event.Time))
            {
                var blendedEvent = await BlendEventGroupAsync(eventGroup.ToList());
                if (blendedEvent != null)
                {
                    blendedEvents.Add(blendedEvent);
                }
            }

            return blendedEvents;
        }

        private async Task<AnimationEvent> BlendEventGroupAsync(List<dynamic> eventGroup)
        {
            if (eventGroup.Count == 1)
            {
                var singleEvent = eventGroup[0];
                return singleEvent.Event with { Intensity = singleEvent.Event.Intensity * singleEvent.Weight };
            }

            // Çoklu event'leri blend et;
            var baseEvent = eventGroup[0].Event;
            var totalWeight = eventGroup.Sum(e => e.Weight);
            var blendedIntensity = eventGroup.Average(e => e.Event.Intensity * e.Weight / totalWeight);

            return baseEvent with;
            {
                Intensity = blendedIntensity,
                Metadata = new Dictionary<string, object>
                {
                    { "BlendedFrom", eventGroup.Select(e => e.Source).ToList() },
                    { "SourceWeights", eventGroup.Select(e => e.Weight).ToList() }
                }
            };
        }

        private Dictionary<string, object> CreateBlendMetadata(
            AnimationData animA,
            AnimationData animB,
            float weight)
        {
            return new Dictionary<string, object>
            {
                { "SourceAnimations", new[] { animA.Id, animB.Id } },
                { "BlendWeight", weight },
                { "SkeletonId", animA.SkeletonId },
                { "FrameCount", Math.Max(animA.Poses.Count, animB.Poses.Count) },
                { "BlendTimestamp", DateTime.UtcNow }
            };
        }

        private float CalculateBlendedDuration(float durationA, float durationB, float weight)
        {
            return durationA * weight + durationB * (1 - weight);
        }

        private float CalculateMultiBlendedDuration(List<AnimationData> animations, List<float> weights)
        {
            float totalDuration = 0;
            for (int i = 0; i < animations.Count; i++)
            {
                totalDuration += animations[i].Duration * weights[i];
            }
            return totalDuration;
        }

        private void ValidateBlendRequest(BlendRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.AnimationAId) || string.IsNullOrEmpty(request.AnimationBId))
                throw new NEDAException(ErrorCode.InvalidBlendRequest, "Animation IDs cannot be empty");

            if (request.BlendFactor < 0 || request.BlendFactor > 1)
                throw new NEDAException(ErrorCode.InvalidBlendFactor, "Blend factor must be between 0 and 1");
        }

        private void ValidateBlendTreeDefinition(BlendTreeDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (string.IsNullOrEmpty(definition.Name))
                throw new NEDAException(ErrorCode.InvalidBlendTreeDefinition, "Blend tree name cannot be empty");

            if (definition.RootNode == null)
                throw new NEDAException(ErrorCode.InvalidBlendTreeDefinition, "Blend tree must have a root node");
        }

        private async Task<BlendTreeNode> CreateBlendTreeNodeAsync(BlendTreeNodeDefinition definition)
        {
            var node = new BlendTreeNode;
            {
                Id = Guid.NewGuid().ToString(),
                Type = definition.Type,
                AnimationId = definition.AnimationId,
                Children = new List<BlendTreeNode>(),
                Parameters = definition.Parameters,
                Position = definition.Position;
            };

            if (definition.Children != null)
            {
                foreach (var childDef in definition.Children)
                {
                    var childNode = await CreateBlendTreeNodeAsync(childDef);
                    node.Children.Add(childNode);
                }
            }

            return node;
        }

        private async Task OptimizeBlendTreeAsync(BlendTree blendTree)
        {
            switch (blendTree.OptimizationLevel)
            {
                case OptimizationLevel.Low:
                    await ApplyLowLevelOptimizationAsync(blendTree);
                    break;
                case OptimizationLevel.Medium:
                    await ApplyMediumLevelOptimizationAsync(blendTree);
                    break;
                case OptimizationLevel.High:
                    await ApplyHighLevelOptimizationAsync(blendTree);
                    break;
            }
        }

        private async Task ApplyLowLevelOptimizationAsync(BlendTree blendTree)
        {
            // Basit optimizasyonlar;
            blendTree.RootNode = await OptimizeBlendTreeNodeAsync(blendTree.RootNode, OptimizationLevel.Low);
        }

        private async Task ApplyMediumLevelOptimizationAsync(BlendTree blendTree)
        {
            // Orta seviye optimizasyonlar;
            blendTree.RootNode = await OptimizeBlendTreeNodeAsync(blendTree.RootNode, OptimizationLevel.Medium);

            // Cache stratejileri;
            if (blendTree.CacheEnabled)
            {
                await PrecacheBlendTreeNodesAsync(blendTree.RootNode);
            }
        }

        private async Task ApplyHighLevelOptimizationAsync(BlendTree blendTree)
        {
            // Yüksek seviye optimizasyonlar;
            blendTree.RootNode = await OptimizeBlendTreeNodeAsync(blendTree.RootNode, OptimizationLevel.High);

            // Paralel işleme optimizasyonu;
            await ParallelizeBlendTreeAsync(blendTree);

            // Memory optimizasyonu;
            await OptimizeBlendTreeMemoryAsync(blendTree);
        }

        private async Task<BlendTreeNode> OptimizeBlendTreeNodeAsync(BlendTreeNode node, OptimizationLevel level)
        {
            // Node optimizasyonu implement edilecek;
            return node;
        }

        private async Task PrecacheBlendTreeNodesAsync(BlendTreeNode node)
        {
            if (!string.IsNullOrEmpty(node.AnimationId))
            {
                await LoadAnimationAsync(node.AnimationId); // Cache'e al;
            }

            foreach (var child in node.Children)
            {
                await PrecacheBlendTreeNodesAsync(child);
            }
        }

        private async Task ParallelizeBlendTreeAsync(BlendTree blendTree)
        {
            // Paralel işleme için tree'yi analiz et;
            var parallelizableNodes = FindParallelizableNodes(blendTree.RootNode);

            // Paralel execution plan oluştur;
            var executionPlan = CreateParallelExecutionPlan(parallelizableNodes);

            // Planı uygula;
            await ExecuteParallelPlanAsync(executionPlan);
        }

        private async Task OptimizeBlendTreeMemoryAsync(BlendTree blendTree)
        {
            // Memory kullanımını optimize et;
            var memoryOptimizer = new MemoryOptimizer();
            await memoryOptimizer.OptimizeAsync(blendTree);
        }

        private async Task<BlendTree> LoadBlendTreeAsync(string blendTreeId)
        {
            var cacheKey = $"blendtree_{blendTreeId}";
            var cached = await _cacheManager.GetAsync<BlendTree>(cacheKey);
            if (cached != null)
            {
                return cached;
            }

            var blendTree = await _blendTreeRepository.GetByIdAsync(blendTreeId);
            if (blendTree == null)
            {
                throw new NEDAException(ErrorCode.BlendTreeNotFound, $"Blend tree {blendTreeId} not found");
            }

            await _cacheManager.SetAsync(cacheKey, blendTree, TimeSpan.FromMinutes(15));
            return blendTree;
        }

        private void ValidateBlendParameters(BlendTree blendTree, BlendParameters parameters)
        {
            // Gerekli parametreleri kontrol et;
            foreach (var requiredParam in blendTree.Parameters.Where(p => p.IsRequired))
            {
                if (!parameters.ContainsKey(requiredParam.Name))
                {
                    throw new NEDAException(ErrorCode.MissingBlendParameter,
                        $"Required parameter '{requiredParam.Name}' is missing");
                }

                // Tip kontrolü;
                if (!IsParameterTypeValid(requiredParam.Type, parameters[requiredParam.Name]))
                {
                    throw new NEDAException(ErrorCode.InvalidParameterType,
                        $"Parameter '{requiredParam.Name}' has invalid type");
                }
            }
        }

        private bool IsParameterTypeValid(ParameterType expectedType, object value)
        {
            return expectedType switch;
            {
                ParameterType.Float => value is float || value is double,
                ParameterType.Int => value is int,
                ParameterType.Bool => value is bool,
                ParameterType.Vector2 => value is Vector2,
                ParameterType.Vector3 => value is Vector3,
                _ => true;
            };
        }

        private async Task<AnimationOutput> EvaluateBlendTreeNodeAsync(BlendTreeNode node, BlendParameters parameters)
        {
            switch (node.Type)
            {
                case BlendNodeType.Animation:
                    return await EvaluateAnimationNodeAsync(node, parameters);

                case BlendNodeType.Blend:
                    return await EvaluateBlendNodeAsync(node, parameters);

                case BlendNodeType.Layer:
                    return await EvaluateLayerNodeAsync(node, parameters);

                case BlendNodeType.Additive:
                    return await EvaluateAdditiveNodeAsync(node, parameters);

                default:
                    throw new NEDAException(ErrorCode.InvalidBlendNodeType,
                        $"Unknown blend node type: {node.Type}");
            }
        }

        private async Task<AnimationOutput> EvaluateAnimationNodeAsync(BlendTreeNode node, BlendParameters parameters)
        {
            var animation = await LoadAnimationAsync(node.AnimationId);
            return new AnimationOutput;
            {
                Success = true,
                Poses = animation.Poses,
                Duration = animation.Duration,
                NodeId = node.Id;
            };
        }

        private async Task<AnimationOutput> EvaluateBlendNodeAsync(BlendTreeNode node, BlendParameters parameters)
        {
            if (node.Children.Count < 2)
            {
                throw new NEDAException(ErrorCode.InvalidBlendNode,
                    $"Blend node requires at least 2 children");
            }

            // Child node'ları değerlendir;
            var childOutputs = new List<AnimationOutput>();
            foreach (var child in node.Children)
            {
                var output = await EvaluateBlendTreeNodeAsync(child, parameters);
                childOutputs.Add(output);
            }

            // Blend weights hesapla;
            var weights = CalculateBlendNodeWeights(node, parameters);

            // Blend uygula;
            var blendedPoses = await MultiBlendPosesAsync(
                childOutputs.Select(o => new AnimationData { Poses = o.Poses }).ToList(),
                weights,
                BlendMode.Linear);

            return new AnimationOutput;
            {
                Success = true,
                Poses = blendedPoses,
                BlendWeights = weights,
                NodeId = node.Id;
            };
        }

        private List<float> CalculateBlendNodeWeights(BlendTreeNode node, BlendParameters parameters)
        {
            // Parametrelere göre weight'leri hesapla;
            var weightParam = node.Parameters.FirstOrDefault(p => p.Name == "Weights");
            if (weightParam != null && weightParam.Value is List<float> weights)
            {
                return weights;
            }

            // Varsayılan: eşit dağılım;
            return Enumerable.Repeat(1.0f / node.Children.Count, node.Children.Count).ToList();
        }

        private async Task<AnimationOutput> EvaluateLayerNodeAsync(BlendTreeNode node, BlendParameters parameters)
        {
            if (node.Children.Count == 0)
            {
                throw new NEDAException(ErrorCode.InvalidLayerNode,
                    $"Layer node requires at least 1 child");
            }

            // Base layer'ı al;
            var baseOutput = await EvaluateBlendTreeNodeAsync(node.Children[0], parameters);
            var layeredPoses = baseOutput.Poses.ToList();

            // Diğer layer'ları uygula;
            for (int i = 1; i < node.Children.Count; i++)
            {
                var layerOutput = await EvaluateBlendTreeNodeAsync(node.Children[i], parameters);
                var layerWeight = GetLayerWeight(node, i, parameters);
                var mask = GetLayerMask(node, i, parameters);

                layeredPoses = await ApplyLayerBlendToPosesAsync(
                    layeredPoses,
                    layerOutput.Poses,
                    layerWeight,
                    mask,
                    BlendMode.Layer);
            }

            return new AnimationOutput;
            {
                Success = true,
                Poses = layeredPoses,
                NodeId = node.Id;
            };
        }

        private async Task<AnimationOutput> EvaluateAdditiveNodeAsync(BlendTreeNode node, BlendParameters parameters)
        {
            if (node.Children.Count != 2)
            {
                throw new NEDAException(ErrorCode.InvalidAdditiveNode,
                    $"Additive node requires exactly 2 children");
            }

            var baseOutput = await EvaluateBlendTreeNodeAsync(node.Children[0], parameters);
            var additiveOutput = await EvaluateBlendTreeNodeAsync(node.Children[1], parameters);
            var intensity = GetAdditiveIntensity(node, parameters);

            var blendedPoses = await ApplyAdditiveBlendToPosesAsync(
                baseOutput.Poses,
                additiveOutput.Poses,
                intensity);

            return new AnimationOutput;
            {
                Success = true,
                Poses = blendedPoses,
                NodeId = node.Id;
            };
        }

        private async Task<AnimationOutput> OptimizeAnimationOutputAsync(
            AnimationOutput output,
            OptimizationLevel level)
        {
            switch (level)
            {
                case OptimizationLevel.Medium:
                    output.Poses = await ApplyMediumOptimizationAsync(output.Poses);
                    break;
                case OptimizationLevel.High:
                    output.Poses = await ApplyHighOptimizationAsync(output.Poses);
                    break;
            }

            return output;
        }

        private async Task<List<Pose>> ApplyMediumOptimizationAsync(List<Pose> poses)
        {
            // Orta seviye optimizasyonlar;
            var optimized = new List<Pose>();
            for (int i = 0; i < poses.Count; i++)
            {
                var pose = poses[i];
                if (i > 0 && i < poses.Count - 1)
                {
                    // Redundant pose'leri kontrol et;
                    if (!IsPoseRedundant(pose, poses[i - 1], poses[i + 1]))
                    {
                        optimized.Add(pose);
                    }
                }
                else;
                {
                    optimized.Add(pose);
                }
            }
            return optimized;
        }

        private async Task<List<Pose>> ApplyHighOptimizationAsync(List<Pose> poses)
        {
            // Yüksek seviye optimizasyonlar;
            var optimized = await ApplyMediumOptimizationAsync(poses);

            // LOD seviyesine göre optimize et;
            optimized = await ApplyLODOptimizationAsync(optimized);

            // Compression uygula;
            optimized = await ApplyCompressionAsync(optimized);

            return optimized;
        }

        private bool IsPoseRedundant(Pose current, Pose previous, Pose next)
        {
            // Pose redundancy kontrolü implement edilecek;
            return false;
        }

        private async Task<List<Pose>> ApplyLODOptimizationAsync(List<Pose> poses)
        {
            // LOD seviyesine göre optimizasyon;
            var lodEngine = new LODOptimizer();
            return await lodEngine.OptimizeAsync(poses);
        }

        private async Task<List<Pose>> ApplyCompressionAsync(List<Pose> poses)
        {
            // Compression uygula;
            var compressor = new PoseCompressor();
            return await compressor.CompressAsync(poses);
        }

        private void RegisterActiveBlend(string blendId, AnimationOutput output, BlendParameters parameters)
        {
            lock (_syncLock)
            {
                var instance = new BlendInstance;
                {
                    Id = Guid.NewGuid().ToString(),
                    BlendId = blendId,
                    Type = BlendInstanceType.BlendTree,
                    CurrentOutput = output,
                    Parameters = parameters,
                    StartTime = DateTime.UtcNow,
                    LastUpdate = DateTime.UtcNow,
                    OptimizationApplied = false;
                };

                _activeBlends[instance.Id] = instance;
            }
        }

        private void ValidateBlendSpaceDefinition(BlendSpaceDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (string.IsNullOrEmpty(definition.Name))
                throw new NEDAException(ErrorCode.InvalidBlendSpaceDefinition, "Blend space name cannot be empty");

            if (definition.Points == null || definition.Points.Count < 3)
                throw new NEDAException(ErrorCode.InvalidBlendSpaceDefinition, "Blend space requires at least 3 points");

            if (definition.Dimensions < 1 || definition.Dimensions > 3)
                throw new NEDAException(ErrorCode.InvalidBlendSpaceDefinition, "Blend space dimensions must be 1-3");
        }

        private async Task GenerateBlendSpaceGridAsync(BlendSpace blendSpace)
        {
            // Grid oluştur;
            var gridGenerator = new BlendSpaceGridGenerator();
            blendSpace.Grid = await gridGenerator.GenerateGridAsync(
                blendSpace.Points,
                blendSpace.GridResolution,
                blendSpace.InterpolationMethod);
        }

        private bool IsPointInBounds(float x, float y, BlendSpaceBoundaries boundaries)
        {
            return x >= boundaries.MinX && x <= boundaries.MaxX &&
                   y >= boundaries.MinY && y <= boundaries.MaxY;
        }

        private List<BlendSpacePoint> FindNearestBlendSpacePoints(
            BlendSpace blendSpace,
            float x,
            float y,
            int count)
        {
            // Noktaları mesafeye göre sırala;
            var pointsWithDistance = blendSpace.Points;
                .Select(p => new;
                {
                    Point = p,
                    Distance = CalculateDistance(p.X, p.Y, x, y)
                })
                .OrderBy(p => p.Distance)
                .Take(count)
                .ToList();

            return pointsWithDistance.Select(p => p.Point).ToList();
        }

        private float CalculateDistance(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private List<float> CalculateBlendSpaceWeights(
            List<BlendSpacePoint> points,
            float x,
            float y)
        {
            var weights = new List<float>();
            float totalInverseDistance = 0;

            // Mesafe bazlı weight hesapla;
            foreach (var point in points)
            {
                float distance = CalculateDistance(point.X, point.Y, x, y);
                if (distance < 0.001f)
                {
                    // Tam eşleşme;
                    weights = Enumerable.Repeat(0f, points.Count).ToList();
                    weights[points.IndexOf(point)] = 1f;
                    return weights;
                }

                float inverseDistance = 1.0f / distance;
                weights.Add(inverseDistance);
                totalInverseDistance += inverseDistance;
            }

            // Normalize et;
            for (int i = 0; i < weights.Count; i++)
            {
                weights[i] /= totalInverseDistance;
            }

            return weights;
        }

        private AnimationOutput CalculateRealTimeBlend(BlendInstance blendInstance)
        {
            // Real-time blend hesapla;
            // Bu metod her frame'de çağrılır;
            return blendInstance.CurrentOutput;
        }

        private async Task CacheBlendResultAsync(BlendResult result)
        {
            var cacheKey = $"blend_result_{result.BlendedAnimation.Id}";
            await _cacheManager.SetAsync(cacheKey, result, TimeSpan.FromHours(1));
        }

        private async Task CacheBlendTreeAsync(BlendTree blendTree)
        {
            var cacheKey = $"blendtree_full_{blendTree.Id}";
            await _cacheManager.SetAsync(cacheKey, blendTree, TimeSpan.FromMinutes(30));
        }

        private async Task UpdateBlendTreeCache(string blendTreeId)
        {
            await _cacheManager.RemoveAsync($"blendtree_{blendTreeId}");
            await _cacheManager.RemoveByPatternAsync($"blendtree_eval_{blendTreeId}_*");
        }

        private bool IsBlendTreeActive(string blendTreeId)
        {
            lock (_syncLock)
            {
                return _activeBlends.Values.Any(b => b.BlendId == blendTreeId);
            }
        }

        private float GetLayerWeight(BlendTreeNode node, int layerIndex, BlendParameters parameters)
        {
            // Layer weight'ini parametrelerden al;
            var weightParam = $"Layer{layerIndex}Weight";
            if (parameters.TryGetValue(weightParam, out var weightValue) && weightValue is float weight)
            {
                return weight;
            }

            return 1.0f; // Varsayılan;
        }

        private BoneMask GetLayerMask(BlendTreeNode node, int layerIndex, BlendParameters parameters)
        {
            // Layer mask'ını parametrelerden al;
            var maskParam = $"Layer{layerIndex}Mask";
            if (parameters.TryGetValue(maskParam, out var maskValue) && maskValue is BoneMask mask)
            {
                return mask;
            }

            return CreateFullBodyMask(); // Varsayılan: tüm body;
        }

        private BoneMask CreateFullBodyMask()
        {
            return new BoneMask;
            {
                BoneNames = new List<string>(), // Boş liste = tüm kemikler;
                Intensity = 1.0f;
            };
        }

        private float GetAdditiveIntensity(BlendTreeNode node, BlendParameters parameters)
        {
            var intensityParam = "AdditiveIntensity";
            if (parameters.TryGetValue(intensityParam, out var intensityValue) && intensityValue is float intensity)
            {
                return intensity;
            }

            return 1.0f; // Varsayılan;
        }

        private async Task<List<Pose>> ApplyLayerBlendToPosesAsync(
            List<Pose> basePoses,
            List<Pose> layerPoses,
            float weight,
            BoneMask mask,
            BlendMode blendMode)
        {
            var blendedPoses = new List<Pose>();
            int frameCount = Math.Min(basePoses.Count, layerPoses.Count);

            for (int i = 0; i < frameCount; i++)
            {
                var blendedPose = await _interpolationEngine.BlendPoseWithMaskAsync(
                    basePoses[i],
                    layerPoses[i],
                    weight,
                    mask,
                    blendMode);

                blendedPoses.Add(blendedPose);
            }

            return blendedPoses;
        }

        private async Task<List<Pose>> ApplyAdditiveBlendToPosesAsync(
            List<Pose> basePoses,
            List<Pose> additivePoses,
            float intensity)
        {
            var blendedPoses = new List<Pose>();
            int frameCount = Math.Min(basePoses.Count, additivePoses.Count);

            for (int i = 0; i < frameCount; i++)
            {
                var blendedPose = await _interpolationEngine.ApplyAdditiveBlendAsync(
                    basePoses[i],
                    additivePoses[i],
                    intensity);

                blendedPoses.Add(blendedPose);
            }

            return blendedPoses;
        }

        private async Task<List<Pose>> ApplyIKTargetsToPosesAsync(
            List<Pose> basePoses,
            List<IKTarget> ikTargets,
            float blendWeight,
            int iterationCount)
        {
            var ikSolver = new IKSolver();
            var ikPoses = new List<Pose>();

            foreach (var pose in basePoses)
            {
                var ikPose = await ikSolver.SolveAsync(pose, ikTargets, iterationCount, blendWeight);
                ikPoses.Add(ikPose);
            }

            return ikPoses;
        }

        private float CalculateIKError(List<Pose> ikPoses, List<IKTarget> ikTargets)
        {
            float totalError = 0;
            foreach (var target in ikTargets)
            {
                // IK hatasını hesapla;
                var error = CalculateTargetError(ikPoses.Last(), target);
                totalError += error;
            }
            return totalError / ikTargets.Count;
        }

        private float CalculateTargetError(Pose pose, IKTarget target)
        {
            // Target error hesaplama implement edilecek;
            return 0f;
        }

        private async Task<MotionDatabase> LoadMotionDatabaseAsync(string databaseId)
        {
            // Motion database yükleme implement edilecek;
            return new MotionDatabase();
        }

        private async Task<MotionMatch> FindBestMotionMatchAsync(
            MotionDatabase database,
            Pose currentPose,
            Vector3 desiredVelocity,
            List<Vector3> futureTrajectory)
        {
            var matcher = new MotionMatcher();
            return await matcher.FindBestMatchAsync(
                database,
                currentPose,
                desiredVelocity,
                futureTrajectory);
        }

        private async Task<List<Pose>> ApplyMotionMatchingBlendAsync(
            Pose currentPose,
            List<Pose> targetPoses,
            float blendDuration)
        {
            var blender = new MotionMatchingBlender();
            return await blender.BlendAsync(currentPose, targetPoses, blendDuration);
        }

        private async Task<AnimationOutput> ApplyBlendOptimizationAsync(
            AnimationOutput output,
            OptimizationOptions options)
        {
            var optimizer = new BlendOptimizer();
            return await optimizer.OptimizeAsync(output, options);
        }

        private BlendMetrics CalculateBlendMetrics(AnimationOutput output)
        {
            return new BlendMetrics;
            {
                FrameCount = output.Poses.Count,
                BlendQuality = CalculateBlendQuality(output),
                Smoothness = CalculateBlendSmoothness(output),
                MemoryUsage = CalculateOutputMemoryUsage(output)
            };
        }

        private float CalculateBlendQuality(AnimationOutput output)
        {
            // Blend kalitesini hesapla;
            return 1.0f; // Placeholder;
        }

        private float CalculateBlendSmoothness(AnimationOutput output)
        {
            // Blend smoothness'ını hesapla;
            return 1.0f; // Placeholder;
        }

        private long CalculateOutputMemoryUsage(AnimationOutput output)
        {
            // Memory kullanımını hesapla;
            return 0; // Placeholder;
        }

        private float CalculateOptimizationTimeSave(AnimationOutput original, AnimationOutput optimized)
        {
            // Optimizasyon zaman kazancını hesapla;
            return 0f; // Placeholder;
        }

        private BlendPerformanceMetrics CalculatePerformanceMetrics(BlendInstance blendInstance)
        {
            return new BlendPerformanceMetrics;
            {
                InstanceId = blendInstance.Id,
                FramesProcessed = blendInstance.CurrentOutput?.Poses?.Count ?? 0,
                AverageFrameTime = CalculateAverageFrameTime(blendInstance),
                MemoryUsage = CalculateMemoryUsage(blendInstance),
                ActiveDuration = (DateTime.UtcNow - blendInstance.StartTime).TotalSeconds,
                LastUpdateAge = (DateTime.UtcNow - blendInstance.LastUpdate).TotalSeconds,
                OptimizationApplied = blendInstance.OptimizationApplied;
            };
        }

        private float CalculateAverageFrameTime(BlendInstance blendInstance)
        {
            // Ortalama frame time hesapla;
            return 0f; // Placeholder;
        }

        private long CalculateMemoryUsage(BlendInstance blendInstance)
        {
            // Memory kullanımını hesapla;
            return 0; // Placeholder;
        }

        private async Task<CacheStatus> GetCacheStatusAsync(string blendInstanceId)
        {
            var cacheKey = $"blend_instance_{blendInstanceId}";
            var exists = await _cacheManager.ExistsAsync(cacheKey);

            return new CacheStatus;
            {
                IsCached = exists,
                CacheKey = cacheKey,
                LastAccessed = DateTime.UtcNow // Bu bilgi cache manager'dan alınmalı;
            };
        }

        private List<BlendTreeNode> FindParallelizableNodes(BlendTreeNode rootNode)
        {
            var parallelizable = new List<BlendTreeNode>();
            FindParallelizableNodesRecursive(rootNode, parallelizable);
            return parallelizable;
        }

        private void FindParallelizableNodesRecursive(BlendTreeNode node, List<BlendTreeNode> parallelizable)
        {
            if (node.Type == BlendNodeType.Animation && node.Children.Count == 0)
            {
                parallelizable.Add(node);
            }

            foreach (var child in node.Children)
            {
                FindParallelizableNodesRecursive(child, parallelizable);
            }
        }

        private ParallelExecutionPlan CreateParallelExecutionPlan(List<BlendTreeNode> nodes)
        {
            // Paralel execution plan oluştur;
            return new ParallelExecutionPlan;
            {
                Nodes = nodes,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                EstimatedCompletionTime = EstimateParallelCompletionTime(nodes)
            };
        }

        private float EstimateParallelCompletionTime(List<BlendTreeNode> nodes)
        {
            // Tamamlanma zamanını tahmin et;
            return nodes.Count * 0.1f; // Placeholder;
        }

        private async Task ExecuteParallelPlanAsync(ParallelExecutionPlan plan)
        {
            // Paralel planı çalıştır;
            var tasks = new List<Task>();

            foreach (var nodeGroup in plan.Nodes.Chunk(plan.MaxDegreeOfParallelism))
            {
                foreach (var node in nodeGroup)
                {
                    tasks.Add(EvaluateBlendTreeNodeAsync(node, new BlendParameters()));
                }

                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }

        private async Task<List<AnimationEvent>> BlendFrameEventsAsync(
            List<AnimationEvent> eventsA,
            List<AnimationEvent> eventsB,
            float blendFactor)
        {
            // Frame event'lerini blend et;
            return new List<AnimationEvent>(); // Placeholder;
        }

        private async Task<AnimationFrame> BlendAnimationFramesAsync(
            AnimationFrame frameA,
            AnimationFrame frameB,
            float blendFactor,
            BlendMode blendMode)
        {
            // Animation frame'leri blend et;
            return new AnimationFrame(); // Placeholder;
        }

        private TransitionCurve CreateTransitionCurve(TransitionType type, float duration)
        {
            // Transition curve oluştur;
            return new TransitionCurve(); // Placeholder;
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Managed kaynakları serbest bırak;
                    lock (_syncLock)
                    {
                        _activeBlends.Clear();
                    }

                    _logger.LogInformation("AnimationBlender disposed");
                }

                _disposed = true;
            }
        }

        ~AnimationBlender()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public class BlendRequest;
    {
        public string AnimationAId { get; set; }
        public string AnimationBId { get; set; }
        public float BlendFactor { get; set; }
        public BlendMode BlendMode { get; set; }
        public CurveType CurveType { get; set; }
        public EasingFunction EasingFunction { get; set; }
    }

    public class MultiBlendRequest;
    {
        public List<AnimationBlendInfo> Animations { get; set; }
        public BlendMode BlendMode { get; set; }
        public CurveType CurveType { get; set; }
    }

    public class AnimationBlendInfo;
    {
        public string AnimationId { get; set; }
        public float Weight { get; set; }
    }

    public class BlendResult;
    {
        public bool Success { get; set; }
        public BlendedAnimation BlendedAnimation { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class BlendedAnimation;
    {
        public string Id { get; set; }
        public List<string> SourceAnimationIds { get; set; }
        public List<Pose> Poses { get; set; }
        public List<AnimationEvent> Events { get; set; }
        public float Duration { get; set; }
        public float BlendWeight { get; set; }
        public BlendMode BlendMode { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class BlendTreeDefinition;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public BlendTreeType Type { get; set; }
        public BlendTreeNodeDefinition RootNode { get; set; }
        public List<BlendParameter> Parameters { get; set; }
        public OptimizationLevel OptimizationLevel { get; set; }
        public bool CacheEnabled { get; set; }
    }

    public class BlendTreeNodeDefinition;
    {
        public BlendNodeType Type { get; set; }
        public string AnimationId { get; set; }
        public List<BlendTreeNodeDefinition> Children { get; set; }
        public List<BlendParameter> Parameters { get; set; }
        public Vector2 Position { get; set; }
    }

    public class BlendTreeUpdateRequest;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public BlendTreeNodeDefinition RootNode { get; set; }
        public List<BlendParameter> Parameters { get; set; }
        public OptimizationLevel? OptimizationLevel { get; set; }
        public bool? CacheEnabled { get; set; }
    }

    public class BlendTree;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public BlendTreeType Type { get; set; }
        public BlendTreeNode RootNode { get; set; }
        public List<BlendParameter> Parameters { get; set; }
        public OptimizationLevel OptimizationLevel { get; set; }
        public bool CacheEnabled { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int Version { get; set; }
    }

    public class BlendTreeNode;
    {
        public string Id { get; set; }
        public BlendNodeType Type { get; set; }
        public string AnimationId { get; set; }
        public List<BlendTreeNode> Children { get; set; }
        public List<BlendParameter> Parameters { get; set; }
        public Vector2 Position { get; set; }
    }

    public class BlendParameter;
    {
        public string Name { get; set; }
        public ParameterType Type { get; set; }
        public object Value { get; set; }
        public bool IsRequired { get; set; }
        public float MinValue { get; set; }
        public float MaxValue { get; set; }
        public string Description { get; set; }
    }

    public class BlendParameters : Dictionary<string, object>
    {
        public float GetFloat(string key, float defaultValue = 0)
        {
            return TryGetValue(key, out var value) && value is float f ? f : defaultValue;
        }

        public int GetInt(string key, int defaultValue = 0)
        {
            return TryGetValue(key, out var value) && value is int i ? i : defaultValue;
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            return TryGetValue(key, out var value) && value is bool b ? b : defaultValue;
        }
    }

    public class AnimationOutput;
    {
        public bool Success { get; set; }
        public List<Pose> Poses { get; set; }
        public List<float> BlendWeights { get; set; }
        public float Duration { get; set; }
        public string NodeId { get; set; }
        public Vector2 EvaluationPoint { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class BlendSpaceDefinition;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public BlendSpaceType Type { get; set; }
        public int Dimensions { get; set; }
        public List<BlendSpacePointDefinition> Points { get; set; }
        public InterpolationMethod InterpolationMethod { get; set; }
        public BlendSpaceBoundaries Boundaries { get; set; }
        public int GridResolution { get; set; }
    }

    public class BlendSpacePointDefinition;
    {
        public string AnimationId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
    }

    public class BlendSpace;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public BlendSpaceType Type { get; set; }
        public int Dimensions { get; set; }
        public List<BlendSpacePoint> Points { get; set; }
        public BlendSpaceGrid Grid { get; set; }
        public InterpolationMethod InterpolationMethod { get; set; }
        public BlendSpaceBoundaries Boundaries { get; set; }
        public int GridResolution { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class BlendSpacePoint;
    {
        public string Id { get; set; }
        public string AnimationId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public AnimationData AnimationData { get; set; }
        public float Weight { get; set; }
    }

    public class BlendSpaceBoundaries;
    {
        public float MinX { get; set; }
        public float MaxX { get; set; }
        public float MinY { get; set; }
        public float MaxY { get; set; }
    }

    public class TransitionRequest;
    {
        public string SourceAnimationId { get; set; }
        public string TargetAnimationId { get; set; }
        public float Duration { get; set; }
        public TransitionType TransitionType { get; set; }
        public BlendMode BlendMode { get; set; }
        public int FrameCount { get; set; }
    }

    public class TransitionResult;
    {
        public bool Success { get; set; }
        public TransitionAnimation TransitionAnimation { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class TransitionAnimation;
    {
        public string Id { get; set; }
        public string SourceAnimationId { get; set; }
        public string TargetAnimationId { get; set; }
        public List<AnimationFrame> Frames { get; set; }
        public List<AnimationEvent> Events { get; set; }
        public float Duration { get; set; }
        public TransitionType TransitionType { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class LayerBlendRequest;
    {
        public string BaseAnimationId { get; set; }
        public List<AnimationLayer> Layers { get; set; }
    }

    public class AnimationLayer;
    {
        public string AnimationId { get; set; }
        public float BlendWeight { get; set; }
        public BoneMask Mask { get; set; }
        public BlendMode BlendMode { get; set; }
    }

    public class LayerBlendResult;
    {
        public bool Success { get; set; }
        public BlendedAnimation BlendedAnimation { get; set; }
        public int LayerCount { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class AdditiveBlendRequest;
    {
        public string BaseAnimationId { get; set; }
        public string AdditiveAnimationId { get; set; }
        public float Intensity { get; set; }
        public bool UseReferencePose { get; set; }
    }

    public class AdditiveBlendResult;
    {
        public bool Success { get; set; }
        public BlendedAnimation BlendedAnimation { get; set; }
        public float IntensityApplied { get; set; }
    }

    public class IKBlendRequest;
    {
        public string BaseAnimationId { get; set; }
        public List<IKTarget> IKTargets { get; set; }
        public float BlendWeight { get; set; }
        public int IterationCount { get; set; }
        public float Tolerance { get; set; }
        public int EffectorCount => IKTargets?.Count ?? 0;
    }

    public class IKBlendResult;
    {
        public bool Success { get; set; }
        public BlendedAnimation BlendedAnimation { get; set; }
        public int EffectorsApplied { get; set; }
        public float MaxError { get; set; }
    }

    public class MotionMatchingRequest;
    {
        public string MotionDatabaseId { get; set; }
        public Pose CurrentPose { get; set; }
        public Vector3 DesiredVelocity { get; set; }
        public List<Vector3> FutureTrajectory { get; set; }
        public float BlendDuration { get; set; }
    }

    public class MotionMatchingResult;
    {
        public bool Success { get; set; }
        public string MatchedAnimationId { get; set; }
        public BlendedAnimation BlendedAnimation { get; set; }
        public float MatchScore { get; set; }
        public float BlendDuration { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    public class OptimizationOptions;
    {
        public OptimizationType Type { get; set; }
        public float QualityThreshold { get; set; }
        public float PerformanceTarget { get; set; }
        public bool EnableCompression { get; set; }
        public int LODLevel { get; set; }
    }

    public class OptimizationResult;
    {
        public bool Success { get; set; }
        public BlendMetrics OriginalMetrics { get; set; }
        public BlendMetrics OptimizedMetrics { get; set; }
        public OptimizationType OptimizationType { get; set; }
        public float TimeSaved { get; set; }
    }

    public class BlendPerformanceMetrics;
    {
        public string InstanceId { get; set; }
        public int FramesProcessed { get; set; }
        public float AverageFrameTime { get; set; }
        public long MemoryUsage { get; set; }
        public double ActiveDuration { get; set; }
        public double LastUpdateAge { get; set; }
        public bool OptimizationApplied { get; set; }
    }

    public class BlendDebugInfo;
    {
        public string InstanceId { get; set; }
        public BlendInstanceType Type { get; set; }
        public BlendParameters Parameters { get; set; }
        public DateTime LastUpdate { get; set; }
        public int FrameCount { get; set; }
        public List<float> CurrentWeights { get; set; }
        public BlendPerformanceMetrics PerformanceMetrics { get; set; }
        public long MemoryUsage { get; set; }
        public int ActiveBlendsCount { get; set; }
        public CacheStatus CacheStatus { get; set; }
    }

    public class CacheStatus;
    {
        public bool IsCached { get; set; }
        public string CacheKey { get; set; }
        public DateTime LastAccessed { get; set; }
    }

    public class BlendInstance;
    {
        public string Id { get; set; }
        public string BlendId { get; set; }
        public BlendInstanceType Type { get; set; }
        public AnimationOutput CurrentOutput { get; set; }
        public BlendParameters Parameters { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastUpdate { get; set; }
        public bool OptimizationApplied { get; set; }
    }

    public class BlendMetrics;
    {
        public int FrameCount { get; set; }
        public float BlendQuality { get; set; }
        public float Smoothness { get; set; }
        public long MemoryUsage { get; set; }
    }

    public class ParallelExecutionPlan;
    {
        public List<BlendTreeNode> Nodes { get; set; }
        public int MaxDegreeOfParallelism { get; set; }
        public float EstimatedCompletionTime { get; set; }
    }

    // Data Classes;
    public class AnimationData;
    {
        public string Id { get; set; }
        public string SkeletonId { get; set; }
        public List<Pose> Poses { get; set; }
        public List<AnimationEvent> Events { get; set; }
        public float Duration { get; set; }
        public float FrameRate { get; set; }

        public AnimationFrame GetFrameAtTime(float time)
        {
            // Implement edilecek;
            return new AnimationFrame();
        }

        public List<AnimationEvent> GetEventsAtTime(float time)
        {
            // Implement edilecek;
            return new List<AnimationEvent>();
        }
    }

    public class Pose;
    {
        public List<BoneTransform> BoneTransforms { get; set; }
        public float Time { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class BoneTransform;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }
    }

    public class AnimationEvent;
    {
        public string Name { get; set; }
        public float Time { get; set; }
        public float Intensity { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class AnimationFrame;
    {
        public Pose Pose { get; set; }
        public List<AnimationEvent> Events { get; set; }
        public float Time { get; set; }
    }

    public class BoneMask;
    {
        public List<string> BoneNames { get; set; }
        public float Intensity { get; set; }
    }

    public class IKTarget;
    {
        public string BoneName { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public float Weight { get; set; }
    }

    public class MotionDatabase;
    {
        public string Id { get; set; }
        public List<MotionClip> Clips { get; set; }
    }

    public class MotionClip;
    {
        public string AnimationId { get; set; }
        public List<Pose> Poses { get; set; }
        public List<MotionFeature> Features { get; set; }
    }

    public class MotionFeature;
    {
        public Vector3 Velocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public List<Vector3> Trajectory { get; set; }
    }

    public class MotionMatch;
    {
        public string AnimationId { get; set; }
        public List<Pose> Poses { get; set; }
        public float Duration { get; set; }
        public float MatchScore { get; set; }
    }

    public class TransitionCurve;
    {
        public float Evaluate(float t)
        {
            // Implement edilecek;
            return t;
        }
    }

    public class BlendSpaceGrid;
    {
        public List<BlendSpaceCell> Cells { get; set; }
    }

    public class BlendSpaceCell;
    {
        public float X { get; set; }
        public float Y { get; set; }
        public List<BlendSpacePoint> Points { get; set; }
        public List<float> Weights { get; set; }
    }

    // Enums;
    public enum BlendMode;
    {
        Linear,
        Override,
        Additive,
        Layer,
        IK,
        MotionMatching;
    }

    public enum CurveType;
    {
        Linear,
        Bezier,
        EaseIn,
        EaseOut,
        EaseInOut,
        Custom;
    }

    public enum EasingFunction;
    {
        Linear,
        Quad,
        Cubic,
        Quart,
        Quint,
        Sine,
        Expo,
        Circ,
        Back,
        Elastic,
        Bounce;
    }

    public enum BlendTreeType;
    {
        Simple1D,
        Simple2D,
        Directional,
        Freeform,
        Custom;
    }

    public enum BlendNodeType;
    {
        Animation,
        Blend,
        Layer,
        Additive,
        IK,
        StateMachine;
    }

    public enum ParameterType;
    {
        Float,
        Int,
        Bool,
        Vector2,
        Vector3,
        String;
    }

    public enum OptimizationLevel;
    {
        None,
        Low,
        Medium,
        High;
    }

    public enum BlendSpaceType;
    {
        OneDimensional,
        TwoDimensional,
        ThreeDimensional;
    }

    public enum InterpolationMethod;
    {
        Linear,
        Bilinear,
        Barycentric,
        NearestNeighbor;
    }

    public enum TransitionType;
    {
        Linear,
        Smooth,
        Exponential,
        Custom;
    }

    public enum BlendInstanceType;
    {
        SimpleBlend,
        BlendTree,
        BlendSpace,
        Transition;
    }

    public enum OptimizationType;
    {
        Performance,
        Quality,
        Memory,
        Balanced;
    }

    // Math Structs;
    public struct Vector2;
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public struct Vector3;
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public struct Quaternion;
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }

    #endregion;
}
