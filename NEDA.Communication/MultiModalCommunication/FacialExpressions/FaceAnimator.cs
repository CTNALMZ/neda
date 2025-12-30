using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.CharacterSystems.CharacterCreator.MorphTargets;
using NEDA.Common.Constants;
using NEDA.Common.Utilities;
using NEDA.Communication.MultiModalCommunication.FacialExpressions.Models;
using NEDA.Communication.MultiModalCommunication.FacialExpressions.Options;
using NEDA.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Logging;
using NEDA.MotionTracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Communication.MultiModalCommunication.FacialExpressions;
{
    /// <summary>
    /// Advanced facial animation engine supporting real-time emotion rendering,
    /// blend shapes, and facial expression synchronization;
    /// </summary>
    public class FaceAnimator : IFaceAnimator;
    {
        private readonly ILogger<FaceAnimator> _logger;
        private readonly FaceAnimatorOptions _options;
        private readonly IExpressionEngine _expressionEngine;
        private readonly IEmotionDisplay _emotionDisplay;
        private readonly IFacialDataProvider _facialDataProvider;
        private readonly CancellationTokenSource _animationCts;
        private readonly Dictionary<string, FacialAnimation> _activeAnimations;
        private readonly object _syncLock = new object();
        private readonly Queue<FacialExpression> _expressionQueue;
        private readonly Dictionary<string, BlendShape> _blendShapes;
        private FacialState _currentState;
        private bool _isAnimating;

        /// <summary>
        /// Event triggered when facial expression changes;
        /// </summary>
        public event EventHandler<FacialExpressionChangedEventArgs> OnExpressionChanged;

        /// <summary>
        /// Event triggered when animation completes;
        /// </summary>
        public event EventHandler<AnimationCompletedEventArgs> OnAnimationCompleted;

        /// <summary>
        /// Current facial expression;
        /// </summary>
        public FacialExpression CurrentExpression => _currentState.CurrentExpression;

        /// <summary>
        /// Whether the animator is currently active;
        /// </summary>
        public bool IsActive => _isAnimating;

        /// <summary>
        /// Animation frame rate;
        /// </summary>
        public int FrameRate { get; private set; }

        /// <summary>
        /// Initialize new instance of FaceAnimator;
        /// </summary>
        public FaceAnimator(
            ILogger<FaceAnimator> logger,
            IOptions<FaceAnimatorOptions> options,
            IExpressionEngine expressionEngine,
            IEmotionDisplay emotionDisplay,
            IFacialDataProvider facialDataProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _expressionEngine = expressionEngine ?? throw new ArgumentNullException(nameof(expressionEngine));
            _emotionDisplay = emotionDisplay ?? throw new ArgumentNullException(nameof(emotionDisplay));
            _facialDataProvider = facialDataProvider;

            _animationCts = new CancellationTokenSource();
            _activeAnimations = new Dictionary<string, FacialAnimation>();
            _expressionQueue = new Queue<FacialExpression>();
            _blendShapes = new Dictionary<string, BlendShape>();
            _currentState = new FacialState();
            FrameRate = _options.DefaultFrameRate;

            InitializeBlendShapes();
            _logger.LogInformation("FaceAnimator initialized with frame rate: {FrameRate}", FrameRate);
        }

        /// <summary>
        /// Start facial animation system;
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                lock (_syncLock)
                {
                    if (_isAnimating)
                    {
                        _logger.LogWarning("FaceAnimator is already running");
                        return;
                    }

                    _isAnimating = true;
                    _logger.LogInformation("Starting FaceAnimator...");
                }

                // Initialize facial tracking if provider exists;
                if (_facialDataProvider != null)
                {
                    await _facialDataProvider.InitializeAsync(cancellationToken);
                    _logger.LogDebug("Facial data provider initialized");
                }

                // Start animation loop;
                _ = Task.Run(() => AnimationLoopAsync(_animationCts.Token), cancellationToken);

                _logger.LogInformation("FaceAnimator started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start FaceAnimator");
                throw new FaceAnimationException("Failed to start animation system", ex);
            }
        }

        /// <summary>
        /// Stop facial animation system;
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                lock (_syncLock)
                {
                    if (!_isAnimating)
                    {
                        return;
                    }

                    _isAnimating = false;
                    _animationCts.Cancel();
                    _logger.LogInformation("Stopping FaceAnimator...");
                }

                // Clear all active animations;
                _activeAnimations.Clear();
                _expressionQueue.Clear();

                // Stop facial data provider;
                if (_facialDataProvider != null)
                {
                    await _facialDataProvider.StopAsync(cancellationToken);
                }

                // Reset to neutral expression;
                await SetExpressionAsync(FacialExpression.Neutral, 0.5f, cancellationToken);

                _logger.LogInformation("FaceAnimator stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping FaceAnimator");
                throw new FaceAnimationException("Failed to stop animation system", ex);
            }
        }

        /// <summary>
        /// Set facial expression with smooth transition;
        /// </summary>
        public async Task SetExpressionAsync(
            FacialExpression expression,
            float intensity = 1.0f,
            float transitionDuration = 0.3f,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isAnimating)
                {
                    throw new InvalidOperationException("FaceAnimator is not running");
                }

                ValidateExpressionParameters(expression, intensity, transitionDuration);

                _logger.LogDebug("Setting expression: {Expression} with intensity: {Intensity}",
                    expression.Type, intensity);

                // Calculate blend shape values for the expression;
                var blendShapeValues = _expressionEngine.CalculateBlendShapes(expression, intensity);

                // Create animation for smooth transition;
                var animation = new FacialAnimation(
                    expression,
                    blendShapeValues,
                    transitionDuration,
                    DateTime.UtcNow);

                var animationId = Guid.NewGuid().ToString();

                lock (_syncLock)
                {
                    _activeAnimations[animationId] = animation;
                }

                // Update current state;
                lock (_syncLock)
                {
                    _currentState = new FacialState;
                    {
                        CurrentExpression = expression,
                        CurrentIntensity = intensity,
                        BlendShapeValues = blendShapeValues,
                        LastUpdate = DateTime.UtcNow;
                    };
                }

                // Update emotion display;
                await _emotionDisplay.UpdateDisplayAsync(expression, intensity, cancellationToken);

                // Trigger expression changed event;
                OnExpressionChanged?.Invoke(this, new FacialExpressionChangedEventArgs;
                {
                    PreviousExpression = _currentState.PreviousExpression,
                    NewExpression = expression,
                    Intensity = intensity,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Expression changed to: {Expression} (Intensity: {Intensity})",
                    expression.Type, intensity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set expression: {Expression}", expression.Type);
                throw new FaceAnimationException($"Failed to set expression: {expression.Type}", ex);
            }
        }

        /// <summary>
        /// Queue expression for sequential playback;
        /// </summary>
        public void QueueExpression(FacialExpression expression, float intensity = 1.0f, float delay = 0f)
        {
            try
            {
                ValidateExpressionParameters(expression, intensity);

                var queuedExpression = new FacialExpression;
                {
                    Type = expression.Type,
                    Intensity = intensity,
                    Duration = expression.Duration,
                    BlendShapeOverrides = expression.BlendShapeOverrides,
                    DelayBeforeStart = delay;
                };

                lock (_syncLock)
                {
                    _expressionQueue.Enqueue(queuedExpression);
                }

                _logger.LogDebug("Expression queued: {Expression} (Delay: {Delay}s)",
                    expression.Type, delay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to queue expression");
                throw new FaceAnimationException("Failed to queue expression", ex);
            }
        }

        /// <summary>
        /// Play expression sequence;
        /// </summary>
        public async Task PlaySequenceAsync(
            IEnumerable<FacialExpression> sequence,
            bool loop = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!sequence.Any())
                {
                    _logger.LogWarning("Empty expression sequence provided");
                    return;
                }

                _logger.LogInformation("Playing expression sequence with {Count} expressions",
                    sequence.Count());

                var sequenceId = Guid.NewGuid().ToString();
                var sequenceList = sequence.ToList();

                // Play sequence in background;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        do;
                        {
                            foreach (var expression in sequenceList)
                            {
                                if (cancellationToken.IsCancellationRequested)
                                    break;

                                await SetExpressionAsync(
                                    expression,
                                    expression.Intensity,
                                    expression.Duration,
                                    cancellationToken);

                                // Wait for expression duration;
                                if (expression.Duration > 0)
                                {
                                    await Task.Delay(
                                        TimeSpan.FromSeconds(expression.Duration),
                                        cancellationToken);
                                }
                            }
                        } while (loop && !cancellationToken.IsCancellationRequested);

                        // Trigger sequence completion;
                        OnAnimationCompleted?.Invoke(this, new AnimationCompletedEventArgs;
                        {
                            SequenceId = sequenceId,
                            Success = true,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error playing expression sequence");

                        OnAnimationCompleted?.Invoke(this, new AnimationCompletedEventArgs;
                        {
                            SequenceId = sequenceId,
                            Success = false,
                            Error = ex.Message,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to play expression sequence");
                throw new FaceAnimationException("Failed to play expression sequence", ex);
            }
        }

        /// <summary>
        /// Update blend shape value directly;
        /// </summary>
        public void UpdateBlendShape(string shapeName, float value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shapeName))
                    throw new ArgumentException("Blend shape name cannot be empty", nameof(shapeName));

                if (value < 0 || value > 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "Blend shape value must be between 0 and 1");

                if (!_blendShapes.ContainsKey(shapeName))
                {
                    _logger.LogWarning("Unknown blend shape: {ShapeName}", shapeName);
                    return;
                }

                lock (_syncLock)
                {
                    _blendShapes[shapeName].CurrentValue = value;
                    _blendShapes[shapeName].LastUpdate = DateTime.UtcNow;
                }

                _logger.LogTrace("Blend shape updated: {ShapeName} = {Value}", shapeName, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update blend shape: {ShapeName}", shapeName);
                throw new FaceAnimationException($"Failed to update blend shape: {shapeName}", ex);
            }
        }

        /// <summary>
        /// Get current facial state;
        /// </summary>
        public FacialState GetCurrentState()
        {
            lock (_syncLock)
            {
                return _currentState.Clone();
            }
        }

        /// <summary>
        /// Get all active blend shapes;
        /// </summary>
        public IReadOnlyDictionary<string, BlendShape> GetBlendShapes()
        {
            lock (_syncLock)
            {
                return new Dictionary<string, BlendShape>(_blendShapes);
            }
        }

        /// <summary>
        /// Reset to neutral expression;
        /// </summary>
        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                lock (_syncLock)
                {
                    _activeAnimations.Clear();
                    _expressionQueue.Clear();
                }

                await SetExpressionAsync(FacialExpression.Neutral, 0.5f, 0.5f, cancellationToken);

                _logger.LogInformation("FaceAnimator reset to neutral state");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset FaceAnimator");
                throw new FaceAnimationException("Failed to reset animation system", ex);
            }
        }

        /// <summary>
        /// Set animation frame rate;
        /// </summary>
        public void SetFrameRate(int frameRate)
        {
            try
            {
                if (frameRate < 1 || frameRate > 240)
                    throw new ArgumentOutOfRangeException(nameof(frameRate), "Frame rate must be between 1 and 240");

                FrameRate = frameRate;
                _logger.LogInformation("Frame rate set to: {FrameRate}", frameRate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set frame rate");
                throw new FaceAnimationException("Failed to set frame rate", ex);
            }
        }

        /// <summary>
        /// Main animation loop;
        /// </summary>
        private async Task AnimationLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Animation loop started");

            var frameInterval = TimeSpan.FromSeconds(1.0 / FrameRate);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested && _isAnimating)
            {
                try
                {
                    var frameStartTime = stopwatch.Elapsed;

                    // Process active animations;
                    await ProcessAnimationsAsync(cancellationToken);

                    // Process queued expressions;
                    await ProcessExpressionQueueAsync(cancellationToken);

                    // Update facial data if provider exists;
                    if (_facialDataProvider != null)
                    {
                        await UpdateFacialDataAsync(cancellationToken);
                    }

                    // Calculate frame processing time;
                    var processingTime = stopwatch.Elapsed - frameStartTime;

                    // Maintain frame rate;
                    if (processingTime < frameInterval)
                    {
                        await Task.Delay(frameInterval - processingTime, cancellationToken);
                    }
                    else;
                    {
                        _logger.LogWarning("Frame processing took too long: {ProcessingTime}ms",
                            processingTime.TotalMilliseconds);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Normal shutdown;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in animation loop");
                    await Task.Delay(100, cancellationToken); // Prevent tight error loop;
                }
            }

            _logger.LogDebug("Animation loop stopped");
        }

        /// <summary>
        /// Process active animations;
        /// </summary>
        private async Task ProcessAnimationsAsync(CancellationToken cancellationToken)
        {
            List<string> completedAnimations = null;

            lock (_syncLock)
            {
                foreach (var kvp in _activeAnimations)
                {
                    var animation = kvp.Value;
                    var progress = (float)((DateTime.UtcNow - animation.StartTime).TotalSeconds / animation.Duration);

                    if (progress >= 1.0f)
                    {
                        // Animation completed;
                        completedAnimations ??= new List<string>();
                        completedAnimations.Add(kvp.Key);

                        // Apply final values;
                        foreach (var blendShape in animation.TargetBlendShapes)
                        {
                            UpdateBlendShape(blendShape.Key, blendShape.Value);
                        }
                    }
                    else;
                    {
                        // Interpolate blend shape values;
                        foreach (var blendShape in animation.TargetBlendShapes)
                        {
                            var currentValue = _blendShapes[blendShape.Key].CurrentValue;
                            var newValue = currentValue + (blendShape.Value - currentValue) * progress;
                            UpdateBlendShape(blendShape.Key, newValue);
                        }
                    }
                }

                // Remove completed animations;
                if (completedAnimations != null)
                {
                    foreach (var id in completedAnimations)
                    {
                        _activeAnimations.Remove(id);
                    }
                }
            }

            if (completedAnimations != null)
            {
                _logger.LogDebug("Completed {Count} animations", completedAnimations.Count);
            }
        }

        /// <summary>
        /// Process expression queue;
        /// </summary>
        private async Task ProcessExpressionQueueAsync(CancellationToken cancellationToken)
        {
            FacialExpression nextExpression = null;

            lock (_syncLock)
            {
                if (_expressionQueue.Count > 0)
                {
                    nextExpression = _expressionQueue.Peek();

                    if (nextExpression.DelayBeforeStart > 0)
                    {
                        // Still waiting for delay;
                        return;
                    }

                    _expressionQueue.Dequeue();
                }
            }

            if (nextExpression != null)
            {
                await SetExpressionAsync(
                    nextExpression,
                    nextExpression.Intensity,
                    nextExpression.Duration,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Update facial data from provider;
        /// </summary>
        private async Task UpdateFacialDataAsync(CancellationToken cancellationToken)
        {
            try
            {
                var facialData = await _facialDataProvider.GetCurrentDataAsync(cancellationToken);

                if (facialData != null)
                {
                    // Update blend shapes based on facial tracking;
                    foreach (var data in facialData.BlendShapeData)
                    {
                        UpdateBlendShape(data.Key, data.Value);
                    }

                    // Optionally adjust expression based on tracked data;
                    if (facialData.SuggestedExpression != null)
                    {
                        await _expressionEngine.AdaptToTrackedDataAsync(
                            facialData,
                            _currentState,
                            cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update facial data from provider");
            }
        }

        /// <summary>
        /// Initialize default blend shapes;
        /// </summary>
        private void InitializeBlendShapes()
        {
            var defaultShapes = new[]
            {
                new BlendShape("EyeBlinkLeft", "Left eye blink"),
                new BlendShape("EyeBlinkRight", "Right eye blink"),
                new BlendShape("EyeSquintLeft", "Left eye squint"),
                new BlendShape("EyeSquintRight", "Right eye squint"),
                new BlendShape("EyeWideLeft", "Left eye wide"),
                new BlendShape("EyeWideRight", "Right eye wide"),
                new BlendShape("BrowDownLeft", "Left brow down"),
                new BlendShape("BrowDownRight", "Right brow down"),
                new BlendShape("BrowInnerUp", "Brow inner up"),
                new BlendShape("BrowOuterUpLeft", "Left brow outer up"),
                new BlendShape("BrowOuterUpRight", "Right brow outer up"),
                new BlendShape("CheekPuff", "Cheek puff"),
                new BlendShape("CheekSquintLeft", "Left cheek squint"),
                new BlendShape("CheekSquintRight", "Right cheek squint"),
                new BlendShape("NoseSneerLeft", "Left nose sneer"),
                new BlendShape("NoseSneerRight", "Right nose sneer"),
                new BlendShape("JawOpen", "Jaw open"),
                new BlendShape("JawForward", "Jaw forward"),
                new BlendShape("JawLeft", "Jaw left"),
                new BlendShape("JawRight", "Jaw right"),
                new BlendShape("MouthClose", "Mouth close"),
                new BlendShape("MouthFunnel", "Mouth funnel"),
                new BlendShape("MouthPucker", "Mouth pucker"),
                new BlendShape("MouthLeft", "Mouth left"),
                new BlendShape("MouthRight", "Mouth right"),
                new BlendShape("MouthSmileLeft", "Left mouth smile"),
                new BlendShape("MouthSmileRight", "Right mouth smile"),
                new BlendShape("MouthFrownLeft", "Left mouth frown"),
                new BlendShape("MouthFrownRight", "Right mouth frown"),
                new BlendShape("MouthDimpleLeft", "Left mouth dimple"),
                new BlendShape("MouthDimpleRight", "Right mouth dimple"),
                new BlendShape("MouthUpperUpLeft", "Left mouth upper up"),
                new BlendShape("MouthUpperUpRight", "Right mouth upper up"),
                new BlendShape("MouthLowerDownLeft", "Left mouth lower down"),
                new BlendShape("MouthLowerDownRight", "Right mouth lower down"),
                new BlendShape("MouthPressLeft", "Left mouth press"),
                new BlendShape("MouthPressRight", "Right mouth press"),
                new BlendShape("MouthStretchLeft", "Left mouth stretch"),
                new BlendShape("MouthStretchRight", "Right mouth stretch"),
                new BlendShape("TongueOut", "Tongue out")
            };

            lock (_syncLock)
            {
                foreach (var shape in defaultShapes)
                {
                    _blendShapes[shape.Name] = shape;
                }
            }

            _logger.LogDebug("Initialized {Count} blend shapes", _blendShapes.Count);
        }

        /// <summary>
        /// Validate expression parameters;
        /// </summary>
        private void ValidateExpressionParameters(FacialExpression expression, float intensity, float transitionDuration = 0.3f)
        {
            if (expression == null)
                throw new ArgumentNullException(nameof(expression));

            if (intensity < 0 || intensity > 1)
                throw new ArgumentOutOfRangeException(nameof(intensity), "Intensity must be between 0 and 1");

            if (transitionDuration < 0)
                throw new ArgumentOutOfRangeException(nameof(transitionDuration), "Transition duration cannot be negative");
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
                _animationCts.Dispose();

                _logger.LogInformation("FaceAnimator disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing FaceAnimator");
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().Wait();
        }
    }

    /// <summary>
    /// FaceAnimator interface;
    /// </summary>
    public interface IFaceAnimator : IDisposable, IAsyncDisposable;
    {
        event EventHandler<FacialExpressionChangedEventArgs> OnExpressionChanged;
        event EventHandler<AnimationCompletedEventArgs> OnAnimationCompleted;

        FacialExpression CurrentExpression { get; }
        bool IsActive { get; }
        int FrameRate { get; }

        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        Task SetExpressionAsync(FacialExpression expression, float intensity = 1.0f, float transitionDuration = 0.3f, CancellationToken cancellationToken = default);
        void QueueExpression(FacialExpression expression, float intensity = 1.0f, float delay = 0f);
        Task PlaySequenceAsync(IEnumerable<FacialExpression> sequence, bool loop = false, CancellationToken cancellationToken = default);
        void UpdateBlendShape(string shapeName, float value);
        FacialState GetCurrentState();
        IReadOnlyDictionary<string, BlendShape> GetBlendShapes();
        Task ResetAsync(CancellationToken cancellationToken = default);
        void SetFrameRate(int frameRate);
    }

    /// <summary>
    /// Facial expression changed event arguments;
    /// </summary>
    public class FacialExpressionChangedEventArgs : EventArgs;
    {
        public FacialExpression PreviousExpression { get; set; }
        public FacialExpression NewExpression { get; set; }
        public float Intensity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Animation completed event arguments;
    /// </summary>
    public class AnimationCompletedEventArgs : EventArgs;
    {
        public string SequenceId { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Face animation specific exception;
    /// </summary>
    public class FaceAnimationException : Exception
    {
        public FaceAnimationException() { }
        public FaceAnimationException(string message) : base(message) { }
        public FaceAnimationException(string message, Exception innerException) : base(message, innerException) { }
    }
}
