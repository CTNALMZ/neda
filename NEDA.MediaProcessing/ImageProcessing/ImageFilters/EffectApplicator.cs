using Microsoft.Extensions.DependencyInjection;
using NEDA.Common.Utilities;
using NEDA.Logging;
using NEDA.MediaProcessing.ImageProcessing.ImageFilters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.CharacterCreator.AppearanceCustomization.Customizer;

namespace NEDA.MediaProcessing.ImageProcessing.ImageFilters;
{
    /// <summary>
    /// Advanced effect application engine with support for complex filter chains,
    /// real-time preview, layer-based composition, and GPU acceleration;
    /// </summary>
    public class EffectApplicator : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly EffectRegistry _effectRegistry
        private readonly LayerCompositor _layerCompositor;
        private readonly EffectOptimizer _effectOptimizer;
        private readonly PreviewGenerator _previewGenerator;
        private readonly EffectCache _effectCache;

        private readonly ConcurrentDictionary<string, EffectSession> _activeSessions;
        private readonly object _processingLock = new object();

        private bool _useGpuAcceleration;
        private bool _enableRealTimePreview;
        private int _maxLayerDepth;
        private bool _isDisposed;

        /// <summary>
        /// Effect application modes;
        /// </summary>
        public enum ApplicationMode;
        {
            Standard,
            RealTime,
            Batch,
            Interactive,
            Preview;
        }

        /// <summary>
        /// Blending modes for effect composition;
        /// </summary>
        public enum BlendingMode;
        {
            Normal,
            Multiply,
            Screen,
            Overlay,
            SoftLight,
            HardLight,
            ColorDodge,
            ColorBurn,
            Darken,
            Lighten,
            Difference,
            Exclusion,
            Hue,
            Saturation,
            Color,
            Luminosity;
        }

        /// <summary>
        /// Effect application settings;
        /// </summary>
        public class EffectSettings;
        {
            public ApplicationMode Mode { get; set; } = ApplicationMode.Standard;
            public BlendingMode BlendMode { get; set; } = BlendingMode.Normal;
            public float Opacity { get; set; } = 1.0f;
            public bool PreserveOriginal { get; set; } = true;
            public bool EnableOptimization { get; set; } = true;
            public bool EnableCaching { get; set; } = true;
            public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(30);
            public int MaxPreviewSize { get; set; } = 1024;
            public QualitySettings Quality { get; set; } = new QualitySettings();
        }

        /// <summary>
        /// Initialize EffectApplicator;
        /// </summary>
        public EffectApplicator(
            IServiceProvider serviceProvider,
            ILogger logger,
            EffectRegistry effectRegistry)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _effectRegistry = effectRegistry ?? throw new ArgumentNullException(nameof(effectRegistry));

            // Initialize components;
            _layerCompositor = new LayerCompositor(logger);
            _effectOptimizer = new EffectOptimizer(logger);
            _previewGenerator = new PreviewGenerator(logger);
            _effectCache = new EffectCache(TimeSpan.FromMinutes(30));

            _activeSessions = new ConcurrentDictionary<string, EffectSession>();
            _maxLayerDepth = 10;

            _logger.LogInformation("EffectApplicator initialized");
        }

        /// <summary>
        /// Configure effect applicator;
        /// </summary>
        public void Configure(bool useGpuAcceleration = false, bool enableRealTimePreview = true)
        {
            lock (_processingLock)
            {
                _useGpuAcceleration = useGpuAcceleration && IsGpuAvailable();
                _enableRealTimePreview = enableRealTimePreview;

                if (_useGpuAcceleration)
                {
                    InitializeGpuAcceleration();
                }

                _logger.LogInformation($"EffectApplicator configured: GPU={_useGpuAcceleration}, Preview={enableRealTimePreview}");
            }
        }

        /// <summary>
        /// Apply a single effect to an image;
        /// </summary>
        public async Task<EffectResult> ApplyEffectAsync(
            Image input,
            string effectId,
            EffectParameters parameters = null,
            EffectSettings settings = null)
        {
            ValidateImage(input);

            if (string.IsNullOrWhiteSpace(effectId))
                throw new ArgumentException("Effect ID cannot be empty", nameof(effectId));

            try
            {
                _logger.LogDebug($"Applying effect: {effectId}");

                var effect = _effectRegistry.GetEffect(effectId);
                if (effect == null)
                {
                    throw new EffectNotFoundException($"Effect not found: {effectId}");
                }

                var sessionId = Guid.NewGuid().ToString();
                var session = CreateSession(sessionId, input, effect, parameters, settings);

                _activeSessions[sessionId] = session;

                try
                {
                    var result = await ApplyEffectInternalAsync(session);

                    _logger.LogInformation($"Effect applied: {effectId} in {result.ProcessingTime.TotalMilliseconds}ms");

                    return result;
                }
                finally
                {
                    _activeSessions.TryRemove(sessionId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply effect: {effectId}");
                throw new EffectApplicationException($"Effect application failed: {effectId}", ex);
            }
        }

        /// <summary>
        /// Apply multiple effects in sequence;
        /// </summary>
        public async Task<MultiEffectResult> ApplyEffectChainAsync(
            Image input,
            List<EffectChainItem> effects,
            ChainSettings chainSettings = null)
        {
            ValidateImage(input);

            if (effects == null || !effects.Any())
            {
                throw new ArgumentException("Effects list cannot be empty", nameof(effects));
            }

            try
            {
                _logger.LogDebug($"Applying effect chain with {effects.Count} effects");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var sessionId = Guid.NewGuid().ToString();

                var chainResult = new MultiEffectResult();
                Image currentImage = input;

                // Create session for chain;
                var chainSession = new EffectSession;
                {
                    SessionId = sessionId,
                    InputImage = input,
                    IsChain = true,
                    ChainItems = effects;
                };

                _activeSessions[sessionId] = chainSession;

                try
                {
                    // Apply each effect in sequence;
                    for (int i = 0; i < effects.Count; i++)
                    {
                        var effectItem = effects[i];
                        var effect = _effectRegistry.GetEffect(effectItem.EffectId);

                        if (effect == null)
                        {
                            _logger.LogWarning($"Effect not found in chain: {effectItem.EffectId}, skipping");
                            chainResult.SkippedEffects.Add(effectItem.EffectId);
                            continue;
                        }

                        // Check cache for this effect combination;
                        var cacheKey = GenerateEffectCacheKey(currentImage, effect, effectItem.Parameters);
                        if (chainSettings?.EnableCaching == true && _effectCache.TryGet(cacheKey, out Image cachedResult))
                        {
                            _logger.LogDebug($"Cache hit for effect: {effectItem.EffectId}");
                            currentImage = cachedResult;
                            chainResult.AddEffectResult(effectItem.EffectId, TimeSpan.Zero, true);
                            continue;
                        }

                        // Apply effect;
                        var effectSession = new EffectSession;
                        {
                            SessionId = $"{sessionId}_{i}",
                            InputImage = currentImage,
                            Effect = effect,
                            Parameters = effectItem.Parameters,
                            Settings = effectItem.Settings;
                        };

                        var effectResult = await ApplyEffectInternalAsync(effectSession);

                        // Update current image;
                        var previousImage = currentImage;
                        currentImage = effectResult.ProcessedImage;

                        // Clean up previous image if not original;
                        if (previousImage != input)
                        {
                            previousImage?.Dispose();
                        }

                        // Cache result if enabled;
                        if (chainSettings?.EnableCaching == true && effectResult.Success)
                        {
                            _effectCache.Set(cacheKey, currentImage);
                        }

                        chainResult.AddEffectResult(
                            effectItem.EffectId,
                            effectResult.ProcessingTime,
                            effectResult.Success);
                    }

                    stopwatch.Stop();
                    chainResult.TotalProcessingTime = stopwatch.Elapsed;
                    chainResult.FinalImage = currentImage;

                    _logger.LogInformation($"Effect chain completed: {effects.Count} effects in {stopwatch.ElapsedMilliseconds}ms");

                    return chainResult;
                }
                finally
                {
                    _activeSessions.TryRemove(sessionId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Effect chain application failed");
                throw new EffectApplicationException("Effect chain application failed", ex);
            }
        }

        /// <summary>
        /// Apply effects with layer composition;
        /// </summary>
        public async Task<LayerCompositionResult> ApplyLayeredEffectsAsync(
            Image baseImage,
            List<EffectLayer> layers,
            CompositionSettings compositionSettings = null)
        {
            ValidateImage(baseImage);

            if (layers == null || !layers.Any())
            {
                throw new ArgumentException("Layers list cannot be empty", nameof(layers));
            }

            if (layers.Count > _maxLayerDepth)
            {
                throw new InvalidOperationException($"Maximum layer depth exceeded: {layers.Count} > {_maxLayerDepth}");
            }

            try
            {
                _logger.LogDebug($"Applying {layers.Count} effect layers");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = new LayerCompositionResult();

                // Apply effects to each layer;
                var processedLayers = new List<ProcessedLayer>();

                foreach (var layer in layers)
                {
                    Image layerImage = layer.Image ?? baseImage;

                    if (layer.Effects != null && layer.Effects.Any())
                    {
                        var chainResult = await ApplyEffectChainAsync(layerImage, layer.Effects);
                        layerImage = chainResult.FinalImage;
                        result.LayerProcessingTimes.Add(layer.Id, chainResult.TotalProcessingTime);
                    }

                    processedLayers.Add(new ProcessedLayer;
                    {
                        OriginalLayer = layer,
                        ProcessedImage = layerImage,
                        BlendMode = layer.BlendMode,
                        Opacity = layer.Opacity,
                        Mask = layer.Mask;
                    });
                }

                // Composite layers;
                var compositedImage = await _layerCompositor.CompositeLayersAsync(
                    baseImage,
                    processedLayers,
                    compositionSettings);

                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;
                result.CompositedImage = compositedImage;
                result.ProcessedLayerCount = processedLayers.Count;

                _logger.LogInformation($"Layer composition completed: {processedLayers.Count} layers in {stopwatch.ElapsedMilliseconds}ms");

                // Clean up intermediate layer images;
                foreach (var layer in processedLayers)
                {
                    if (layer.ProcessedImage != baseImage && layer.ProcessedImage != compositedImage)
                    {
                        layer.ProcessedImage?.Dispose();
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Layered effect application failed");
                throw new EffectApplicationException("Layered effect application failed", ex);
            }
        }

        /// <summary>
        /// Generate real-time preview of effect;
        /// </summary>
        public async Task<PreviewResult> GeneratePreviewAsync(
            Image input,
            string effectId,
            EffectParameters parameters = null,
            PreviewSettings previewSettings = null)
        {
            if (!_enableRealTimePreview)
            {
                throw new InvalidOperationException("Real-time preview is disabled");
            }

            ValidateImage(input);

            try
            {
                var effect = _effectRegistry.GetEffect(effectId);
                if (effect == null)
                {
                    throw new EffectNotFoundException($"Effect not found: {effectId}");
                }

                _logger.LogDebug($"Generating preview for effect: {effectId}");

                // Create preview image (downscaled for performance)
                var previewImage = await _previewGenerator.GeneratePreviewImageAsync(input, previewSettings);

                // Apply effect to preview image;
                var previewSession = new EffectSession;
                {
                    SessionId = $"preview_{Guid.NewGuid()}",
                    InputImage = previewImage,
                    Effect = effect,
                    Parameters = parameters,
                    Settings = new EffectSettings { Mode = ApplicationMode.Preview }
                };

                var result = await ApplyEffectInternalAsync(previewSession);

                var previewResult = new PreviewResult;
                {
                    OriginalPreview = previewImage,
                    EffectPreview = result.ProcessedImage,
                    EffectId = effectId,
                    ProcessingTime = result.ProcessingTime,
                    PreviewSettings = previewSettings;
                };

                _logger.LogDebug($"Preview generated for effect: {effectId}");

                return previewResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to generate preview for effect: {effectId}");
                throw new EffectApplicationException($"Preview generation failed for effect: {effectId}", ex);
            }
        }

        /// <summary>
        /// Apply effect to specific region of image;
        /// </summary>
        public async Task<EffectResult> ApplyEffectToRegionAsync(
            Image input,
            string effectId,
            Rectangle region,
            EffectParameters parameters = null,
            EffectSettings settings = null)
        {
            ValidateImage(input);

            if (region.IsEmpty || !input.Size.Contains(region))
            {
                throw new ArgumentException("Invalid region specified", nameof(region));
            }

            try
            {
                _logger.LogDebug($"Applying effect {effectId} to region: {region}");

                // Extract region;
                var regionImage = ExtractRegion(input, region);

                // Apply effect to region;
                var regionResult = await ApplyEffectAsync(regionImage, effectId, parameters, settings);

                // Composite region back into original image;
                var finalImage = CompositeRegion(input, regionResult.ProcessedImage, region);

                // Clean up;
                regionImage?.Dispose();
                regionResult.ProcessedImage?.Dispose();

                var result = new EffectResult;
                {
                    OriginalImage = input,
                    ProcessedImage = finalImage,
                    EffectId = effectId,
                    ProcessingTime = regionResult.ProcessingTime,
                    Success = true;
                };

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply effect to region: {effectId}");
                throw new EffectApplicationException($"Region effect application failed: {effectId}", ex);
            }
        }

        /// <summary>
        /// Apply effect with animation (progressive application)
        /// </summary>
        public async Task<AnimatedEffectResult> ApplyAnimatedEffectAsync(
            Image input,
            string effectId,
            AnimationParameters animationParams,
            EffectParameters baseParameters = null)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug($"Applying animated effect: {effectId} with {animationParams.FrameCount} frames");

                var result = new AnimatedEffectResult;
                {
                    EffectId = effectId,
                    FrameCount = animationParams.FrameCount,
                    FrameRate = animationParams.FrameRate;
                };

                var frames = new List<Image>();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Generate animation frames;
                for (int frame = 0; frame < animationParams.FrameCount; frame++)
                {
                    // Calculate interpolation value for this frame;
                    float progress = (float)frame / (animationParams.FrameCount - 1);

                    // Apply interpolation to parameters;
                    var frameParameters = InterpolateParameters(baseParameters, animationParams, progress);

                    // Apply effect for this frame;
                    var frameResult = await ApplyEffectAsync(input, effectId, frameParameters);

                    frames.Add(frameResult.ProcessedImage);
                    result.FrameProcessingTimes.Add(frameResult.ProcessingTime);

                    // Clean up;
                    if (frame > 0)
                    {
                        frames[frame - 1]?.Dispose();
                    }
                }

                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;
                result.Frames = frames;

                _logger.LogInformation($"Animated effect completed: {animationParams.FrameCount} frames in {stopwatch.ElapsedMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply animated effect: {effectId}");
                throw new EffectApplicationException($"Animated effect application failed: {effectId}", ex);
            }
        }

        /// <summary>
        /// Cancel active effect session;
        /// </summary>
        public bool CancelSession(string sessionId)
        {
            if (_activeSessions.TryGetValue(sessionId, out var session))
            {
                session.CancellationRequested = true;
                _logger.LogInformation($"Cancelled effect session: {sessionId}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get active sessions;
        /// </summary>
        public IEnumerable<SessionInfo> GetActiveSessions()
        {
            return _activeSessions.Values.Select(s => new SessionInfo;
            {
                SessionId = s.SessionId,
                EffectId = s.Effect?.Id,
                StartTime = s.StartTime,
                IsChain = s.IsChain,
                Progress = s.Progress;
            });
        }

        /// <summary>
        /// Get effect applicator statistics;
        /// </summary>
        public EffectApplicatorStatistics GetStatistics()
        {
            return new EffectApplicatorStatistics;
            {
                ActiveSessions = _activeSessions.Count,
                TotalEffectsAvailable = _effectRegistry.GetEffectCount(),
                CacheHitRate = _effectCache.HitRate,
                CacheSize = _effectCache.Count,
                UseGpuAcceleration = _useGpuAcceleration,
                MaxLayerDepth = _maxLayerDepth;
            };
        }

        /// <summary>
        /// Clean up resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            lock (_processingLock)
            {
                if (_isDisposed) return;

                try
                {
                    // Cancel all active sessions;
                    foreach (var sessionId in _activeSessions.Keys.ToList())
                    {
                        CancelSession(sessionId);
                    }

                    // Clear sessions;
                    _activeSessions.Clear();

                    // Dispose components;
                    _layerCompositor?.Dispose();
                    _effectOptimizer?.Dispose();
                    _previewGenerator?.Dispose();
                    _effectCache?.Dispose();

                    _isDisposed = true;
                    _logger.LogInformation("EffectApplicator disposed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing EffectApplicator");
                }
            }
        }

        #region Private Methods;

        private EffectSession CreateSession(
            string sessionId,
            Image input,
            IImageEffect effect,
            EffectParameters parameters,
            EffectSettings settings)
        {
            return new EffectSession;
            {
                SessionId = sessionId,
                InputImage = input,
                Effect = effect,
                Parameters = parameters ?? effect.DefaultParameters,
                Settings = settings ?? new EffectSettings(),
                StartTime = DateTime.UtcNow,
                Progress = 0;
            };
        }

        private async Task<EffectResult> ApplyEffectInternalAsync(EffectSession session)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                session.Progress = 0.1f;

                // Check cache first;
                if (session.Settings.EnableCaching)
                {
                    var cacheKey = GenerateEffectCacheKey(session.InputImage, session.Effect, session.Parameters);
                    if (_effectCache.TryGet(cacheKey, out Image cachedResult))
                    {
                        _logger.LogDebug($"Cache hit for effect: {session.Effect.Id}");
                        stopwatch.Stop();

                        return new EffectResult;
                        {
                            OriginalImage = session.InputImage,
                            ProcessedImage = cachedResult,
                            EffectId = session.Effect.Id,
                            ProcessingTime = stopwatch.Elapsed,
                            Success = true,
                            FromCache = true;
                        };
                    }
                }

                session.Progress = 0.3f;

                // Apply effect optimization if enabled;
                if (session.Settings.EnableOptimization)
                {
                    var optimization = _effectOptimizer.OptimizeEffect(session.Effect, session.Parameters, session.InputImage.Size);
                    session.Parameters = optimization.Parameters;
                }

                session.Progress = 0.5f;

                Image processedImage;

                // Apply effect using appropriate method;
                if (_useGpuAcceleration && session.Effect.SupportsGpu)
                {
                    processedImage = await ApplyEffectGpuAsync(session);
                }
                else;
                {
                    processedImage = await ApplyEffectCpuAsync(session);
                }

                session.Progress = 0.9f;

                // Apply blending if needed;
                if (session.Settings.Opacity < 1.0f || session.Settings.BlendMode != BlendingMode.Normal)
                {
                    processedImage = await ApplyBlendingAsync(session.InputImage, processedImage, session.Settings);
                }

                // Cache result;
                if (session.Settings.EnableCaching && processedImage != null)
                {
                    var cacheKey = GenerateEffectCacheKey(session.InputImage, session.Effect, session.Parameters);
                    _effectCache.Set(cacheKey, processedImage);
                }

                session.Progress = 1.0f;
                stopwatch.Stop();

                return new EffectResult;
                {
                    OriginalImage = session.InputImage,
                    ProcessedImage = processedImage,
                    EffectId = session.Effect.Id,
                    ProcessingTime = stopwatch.Elapsed,
                    Success = true;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                _logger.LogError(ex, $"Effect application failed in session: {session.SessionId}");

                return new EffectResult;
                {
                    OriginalImage = session.InputImage,
                    EffectId = session.Effect?.Id,
                    ProcessingTime = stopwatch.Elapsed,
                    Success = false,
                    Error = ex.Message;
                };
            }
        }

        private async Task<Image> ApplyEffectCpuAsync(EffectSession session)
        {
            return await Task.Run(() =>
            {
                using var bitmap = new Bitmap(session.InputImage);
                return session.Effect.Apply(bitmap, session.Parameters);
            });
        }

        private async Task<Image> ApplyEffectGpuAsync(EffectSession session)
        {
            // GPU implementation would go here;
            // For now, fall back to CPU;
            _logger.LogDebug($"GPU effect not available for {session.Effect.Id}, falling back to CPU");
            return await ApplyEffectCpuAsync(session);
        }

        private async Task<Image> ApplyBlendingAsync(Image baseImage, Image effectImage, EffectSettings settings)
        {
            return await Task.Run(() =>
            {
                return _layerCompositor.BlendImages(baseImage, effectImage, settings.BlendMode, settings.Opacity);
            });
        }

        private Image ExtractRegion(Image image, Rectangle region)
        {
            var regionBitmap = new Bitmap(region.Width, region.Height);

            using (var graphics = Graphics.FromImage(regionBitmap))
            {
                graphics.DrawImage(image,
                    new Rectangle(0, 0, region.Width, region.Height),
                    region,
                    GraphicsUnit.Pixel);
            }

            return regionBitmap;
        }

        private Image CompositeRegion(Image baseImage, Image regionImage, Rectangle region)
        {
            var resultBitmap = new Bitmap(baseImage);

            using (var graphics = Graphics.FromImage(resultBitmap))
            {
                graphics.DrawImage(regionImage, region);
            }

            return resultBitmap;
        }

        private EffectParameters InterpolateParameters(
            EffectParameters baseParams,
            AnimationParameters animationParams,
            float progress)
        {
            if (baseParams == null) return null;

            var interpolated = new EffectParameters();

            foreach (var param in baseParams.GetAllParameters())
            {
                if (param.Value is float floatValue)
                {
                    var animatedParam = animationParams.ParameterAnimations;
                        .FirstOrDefault(p => p.ParameterName == param.Key);

                    if (animatedParam != null)
                    {
                        var interpolatedValue = floatValue +
                            (animatedParam.EndValue - floatValue) * progress;
                        interpolated.SetParameter(param.Key, interpolatedValue);
                    }
                    else;
                    {
                        interpolated.SetParameter(param.Key, floatValue);
                    }
                }
                else;
                {
                    interpolated.SetParameter(param.Key, param.Value);
                }
            }

            return interpolated;
        }

        private string GenerateEffectCacheKey(Image image, IImageEffect effect, EffectParameters parameters)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();

            var keyData = $"{effect.Id}_{image.Width}_{image.Height}_{image.GetHashCode()}";
            if (parameters != null)
            {
                keyData += $"_{parameters.GetHashCode()}";
            }

            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(keyData));
            return Convert.ToBase64String(hash);
        }

        private bool IsGpuAvailable()
        {
            try
            {
                // Check for GPU availability;
                return false; // Simplified - implement actual GPU detection;
            }
            catch
            {
                return false;
            }
        }

        private void InitializeGpuAcceleration()
        {
            try
            {
                // Initialize GPU processing;
                _logger.LogInformation("GPU acceleration initialized for EffectApplicator");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPU acceleration initialization failed");
                _useGpuAcceleration = false;
            }
        }

        private void ValidateImage(Image image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            if (image.Width <= 0 || image.Height <= 0)
                throw new ArgumentException("Invalid image dimensions");
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Effect application session;
        /// </summary>
        private class EffectSession;
        {
            public string SessionId { get; set; }
            public Image InputImage { get; set; }
            public IImageEffect Effect { get; set; }
            public EffectParameters Parameters { get; set; }
            public EffectSettings Settings { get; set; }
            public DateTime StartTime { get; set; }
            public float Progress { get; set; }
            public bool CancellationRequested { get; set; }
            public bool IsChain { get; set; }
            public List<EffectChainItem> ChainItems { get; set; }
        }

        /// <summary>
        /// Effect application result;
        /// </summary>
        public class EffectResult;
        {
            public Image OriginalImage { get; set; }
            public Image ProcessedImage { get; set; }
            public string EffectId { get; set; }
            public TimeSpan ProcessingTime { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
            public bool FromCache { get; set; }
        }

        /// <summary>
        /// Multi-effect chain result;
        /// </summary>
        public class MultiEffectResult;
        {
            public Image FinalImage { get; set; }
            public TimeSpan TotalProcessingTime { get; set; }
            public Dictionary<string, TimeSpan> EffectTimes { get; } = new Dictionary<string, TimeSpan>();
            public Dictionary<string, bool> EffectSuccess { get; } = new Dictionary<string, bool>();
            public List<string> SkippedEffects { get; } = new List<string>();

            public void AddEffectResult(string effectId, TimeSpan time, bool success)
            {
                EffectTimes[effectId] = time;
                EffectSuccess[effectId] = success;
            }
        }

        /// <summary>
        /// Layer composition result;
        /// </summary>
        public class LayerCompositionResult;
        {
            public Image CompositedImage { get; set; }
            public TimeSpan TotalProcessingTime { get; set; }
            public Dictionary<string, TimeSpan> LayerProcessingTimes { get; } = new Dictionary<string, TimeSpan>();
            public int ProcessedLayerCount { get; set; }
        }

        /// <summary>
        /// Preview generation result;
        /// </summary>
        public class PreviewResult;
        {
            public Image OriginalPreview { get; set; }
            public Image EffectPreview { get; set; }
            public string EffectId { get; set; }
            public TimeSpan ProcessingTime { get; set; }
            public PreviewSettings PreviewSettings { get; set; }
        }

        /// <summary>
        /// Animated effect result;
        /// </summary>
        public class AnimatedEffectResult;
        {
            public string EffectId { get; set; }
            public List<Image> Frames { get; set; }
            public TimeSpan TotalProcessingTime { get; set; }
            public List<TimeSpan> FrameProcessingTimes { get; } = new List<TimeSpan>();
            public int FrameCount { get; set; }
            public float FrameRate { get; set; }
        }

        /// <summary>
        /// Session information;
        /// </summary>
        public class SessionInfo;
        {
            public string SessionId { get; set; }
            public string EffectId { get; set; }
            public DateTime StartTime { get; set; }
            public bool IsChain { get; set; }
            public float Progress { get; set; }
        }

        /// <summary>
        /// Effect applicator statistics;
        /// </summary>
        public class EffectApplicatorStatistics;
        {
            public int ActiveSessions { get; set; }
            public int TotalEffectsAvailable { get; set; }
            public double CacheHitRate { get; set; }
            public int CacheSize { get; set; }
            public bool UseGpuAcceleration { get; set; }
            public int MaxLayerDepth { get; set; }
        }

        /// <summary>
        /// Effect chain item;
        /// </summary>
        public class EffectChainItem;
        {
            public string EffectId { get; set; }
            public EffectParameters Parameters { get; set; }
            public EffectSettings Settings { get; set; }
        }

        /// <summary>
        /// Effect layer definition;
        /// </summary>
        public class EffectLayer;
        {
            public string Id { get; set; }
            public Image Image { get; set; }
            public List<EffectChainItem> Effects { get; set; }
            public BlendingMode BlendMode { get; set; } = BlendingMode.Normal;
            public float Opacity { get; set; } = 1.0f;
            public Image Mask { get; set; }
        }

        /// <summary>
        /// Processed layer for composition;
        /// </summary>
        public class ProcessedLayer;
        {
            public EffectLayer OriginalLayer { get; set; }
            public Image ProcessedImage { get; set; }
            public BlendingMode BlendMode { get; set; }
            public float Opacity { get; set; }
            public Image Mask { get; set; }
        }

        /// <summary>
        /// Animation parameters;
        /// </summary>
        public class AnimationParameters;
        {
            public int FrameCount { get; set; } = 24;
            public float FrameRate { get; set; } = 24f;
            public List<ParameterAnimation> ParameterAnimations { get; set; } = new List<ParameterAnimation>();
        }

        /// <summary>
        /// Parameter animation definition;
        /// </summary>
        public class ParameterAnimation;
        {
            public string ParameterName { get; set; }
            public float StartValue { get; set; }
            public float EndValue { get; set; }
            public AnimationCurve Curve { get; set; } = AnimationCurve.Linear;
        }

        /// <summary>
        /// Animation curves;
        /// </summary>
        public enum AnimationCurve;
        {
            Linear,
            EaseIn,
            EaseOut,
            EaseInOut,
            Bounce,
            Elastic;
        }

        /// <summary>
        /// Effect application exceptions;
        /// </summary>
        public class EffectApplicationException : Exception
        {
            public EffectApplicationException(string message) : base(message) { }
            public EffectApplicationException(string message, Exception inner) : base(message, inner) { }
        }

        public class EffectNotFoundException : Exception
        {
            public EffectNotFoundException(string message) : base(message) { }
        }

        #endregion;
    }

    /// <summary>
    /// Image effect interface;
    /// </summary>
    public interface IImageEffect;
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        bool SupportsGpu { get; }
        EffectParameters DefaultParameters { get; }

        Image Apply(Image input, EffectParameters parameters);
    }

    /// <summary>
    /// Effect parameters container;
    /// </summary>
    public class EffectParameters;
    {
        private readonly Dictionary<string, object> _parameters = new Dictionary<string, object>();

        public T GetParameter<T>(string name, T defaultValue = default)
        {
            return _parameters.TryGetValue(name, out var value) ? (T)value : defaultValue;
        }

        public void SetParameter(string name, object value)
        {
            _parameters[name] = value;
        }

        public Dictionary<string, object> GetAllParameters()
        {
            return new Dictionary<string, object>(_parameters);
        }

        public override int GetHashCode()
        {
            return _parameters.GetHashCode();
        }
    }
}
