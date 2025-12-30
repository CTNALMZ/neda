using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;

namespace NEDA.Animation.SequenceEditor.CameraAnimation;
{
    /// <summary>
    /// Advanced path animation system for creating smooth, customizable motion along predefined paths.
    /// Supports various path types, easing functions, and real-time path manipulation.
    /// </summary>
    public class PathAnimator : IPathAnimator, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly ISettingsManager _settingsManager;
        private readonly IRecoveryEngine _recoveryEngine;

        private readonly Dictionary<string, PathAnimation> _activeAnimations;
        private readonly Dictionary<string, PathDefinition> _pathDefinitions;
        private readonly Dictionary<string, PathPreset> _pathPresets;
        private readonly Queue<PathCommand> _commandQueue;
        private readonly object _animationLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        private PathAnimatorSettings _settings;
        private PathAnimatorMetrics _metrics;
        private PathInterpolator _interpolator;
        private PathSmoother _smoother;
        private PathOptimizer _optimizer;
        #endregion;

        #region Public Properties;
        /// <summary>
        /// Gets the number of active path animations;
        /// </summary>
        public int ActiveAnimationCount;
        {
            get;
            {
                lock (_animationLock)
                {
                    return _activeAnimations.Count;
                }
            }
        }

        /// <summary>
        /// Gets the number of registered path definitions;
        /// </summary>
        public int PathDefinitionCount;
        {
            get;
            {
                lock (_animationLock)
                {
                    return _pathDefinitions.Count;
                }
            }
        }

        /// <summary>
        /// Gets the system performance metrics;
        /// </summary>
        public PathAnimatorMetrics Metrics => _metrics;

        /// <summary>
        /// Gets the current system settings;
        /// </summary>
        public PathAnimatorSettings Settings => _settings;

        /// <summary>
        /// Gets whether the system is initialized and ready;
        /// </summary>
        public bool IsInitialized => _isInitialized;
        #endregion;

        #region Events;
        /// <summary>
        /// Raised when a path animation starts;
        /// </summary>
        public event EventHandler<PathAnimationStartedEventArgs> PathAnimationStarted;

        /// <summary>
        /// Raised when a path animation completes;
        /// </summary>
        public event EventHandler<PathAnimationCompletedEventArgs> PathAnimationCompleted;

        /// <summary>
        /// Raised when a path animation is paused;
        /// </summary>
        public event EventHandler<PathAnimationPausedEventArgs> PathAnimationPaused;

        /// <summary>
        /// Raised when a path animation is resumed;
        /// </summary>
        public event EventHandler<PathAnimationResumedEventArgs> PathAnimationResumed;

        /// <summary>
        /// Raised when a path animation is stopped;
        /// </summary>
        public event EventHandler<PathAnimationStoppedEventArgs> PathAnimationStopped;

        /// <summary>
        /// Raised when a path animation progresses;
        /// </summary>
        public event EventHandler<PathAnimationProgressEventArgs> PathAnimationProgress;

        /// <summary>
        /// Raised when path metrics are updated;
        /// </summary>
        public event EventHandler<PathAnimatorMetricsUpdatedEventArgs> MetricsUpdated;
        #endregion;

        #region Constructor;
        /// <summary>
        /// Initializes a new instance of the PathAnimator;
        /// </summary>
        public PathAnimator(
            ILogger logger,
            ISettingsManager settingsManager,
            IRecoveryEngine recoveryEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _recoveryEngine = recoveryEngine ?? throw new ArgumentNullException(nameof(recoveryEngine));

            _activeAnimations = new Dictionary<string, PathAnimation>();
            _pathDefinitions = new Dictionary<string, PathDefinition>();
            _pathPresets = new Dictionary<string, PathPreset>();
            _commandQueue = new Queue<PathCommand>();
            _metrics = new PathAnimatorMetrics();
            _interpolator = new PathInterpolator(logger);
            _smoother = new PathSmoother(logger);
            _optimizer = new PathOptimizer(logger);

            _logger.LogInformation("PathAnimator instance created");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Initializes the path animator system;
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("PathAnimator is already initialized");
                    return;
                }

                await LoadConfigurationAsync();
                await PreloadPathPresetsAsync();
                await InitializeSubsystemsAsync();

                _isInitialized = true;
                _logger.LogInformation("PathAnimator initialized successfully");
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Failed to initialize PathAnimator", ex);
                throw new PathAnimatorException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Updates all active path animations;
        /// </summary>
        public async Task UpdateAsync(float deltaTime)
        {
            try
            {
                if (!_isInitialized) return;

                var updateTimer = System.Diagnostics.Stopwatch.StartNew();
                _metrics.LastUpdateTime = deltaTime;

                await ProcessCommandQueueAsync();
                await UpdateActiveAnimationsAsync(deltaTime);
                await UpdateMetricsAsync();

                updateTimer.Stop();
                _metrics.LastUpdateDuration = (float)updateTimer.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Error during path animation update", ex);
                await _recoveryEngine.ExecuteRecoveryStrategyAsync("PathAnimationUpdate");
            }
        }

        /// <summary>
        /// Registers a path definition for later use;
        /// </summary>
        public async Task<string> RegisterPathAsync(PathDefinition pathDefinition)
        {
            if (pathDefinition == null)
                throw new ArgumentNullException(nameof(pathDefinition));

            try
            {
                await ValidateSystemState();
                await ValidatePathDefinition(pathDefinition);

                var pathId = GeneratePathId();
                pathDefinition.Id = pathId;

                // Optimize path if enabled;
                if (_settings.AutoOptimizePaths)
                {
                    pathDefinition = await _optimizer.OptimizePathAsync(pathDefinition);
                }

                lock (_animationLock)
                {
                    _pathDefinitions[pathId] = pathDefinition;
                }

                _metrics.PathsRegistered++;
                _logger.LogDebug($"Path registered: {pathId} ({pathDefinition.PathType})");

                return pathId;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Failed to register path", ex);
                throw new PathAnimatorException("Path registration failed", ex);
            }
        }

        /// <summary>
        /// Unregisters a path definition;
        /// </summary>
        public async Task<bool> UnregisterPathAsync(string pathId)
        {
            if (string.IsNullOrEmpty(pathId))
                throw new ArgumentException("Path ID cannot be null or empty", nameof(pathId));

            try
            {
                PathDefinition pathDefinition;
                lock (_animationLock)
                {
                    if (!_pathDefinitions.TryGetValue(pathId, out pathDefinition))
                    {
                        _logger.LogWarning($"Path not found: {pathId}");
                        return false;
                    }

                    _pathDefinitions.Remove(pathId);
                }

                _metrics.PathsUnregistered++;
                _logger.LogDebug($"Path unregistered: {pathId}");

                return true;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to unregister path: {pathId}", ex);
                throw new PathAnimatorException("Path unregistration failed", ex);
            }
        }

        /// <summary>
        /// Starts a path animation;
        /// </summary>
        public async Task<string> StartPathAnimationAsync(PathAnimationParams animationParams)
        {
            if (animationParams == null)
                throw new ArgumentNullException(nameof(animationParams));

            try
            {
                await ValidateSystemState();
                await ValidateAnimationParams(animationParams);

                var pathDefinition = await GetPathDefinitionAsync(animationParams.PathId);
                var animationId = GenerateAnimationId();

                var animation = new PathAnimation(animationId, pathDefinition, animationParams);

                lock (_animationLock)
                {
                    _activeAnimations[animationId] = animation;
                }

                // Initialize animation state;
                await InitializeAnimationAsync(animation);
                _metrics.AnimationsStarted++;

                RaisePathAnimationStarted(animationId, animationParams);
                _logger.LogDebug($"Path animation started: {animationId} on path {animationParams.PathId}");

                return animationId;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Failed to start path animation", ex);
                throw new PathAnimatorException("Path animation start failed", ex);
            }
        }

        /// <summary>
        /// Stops a path animation;
        /// </summary>
        public async Task<bool> StopPathAnimationAsync(string animationId, bool immediate = true)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                PathAnimation animation;
                lock (_animationLock)
                {
                    if (!_activeAnimations.TryGetValue(animationId, out animation))
                    {
                        _logger.LogWarning($"Animation not found: {animationId}");
                        return false;
                    }

                    _activeAnimations.Remove(animationId);
                }

                animation.State = PathAnimationState.Stopped;
                _metrics.AnimationsStopped++;

                RaisePathAnimationStopped(animationId, immediate);
                _logger.LogDebug($"Path animation stopped: {animationId}");

                return true;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to stop path animation: {animationId}", ex);
                throw new PathAnimatorException("Path animation stop failed", ex);
            }
        }

        /// <summary>
        /// Pauses a path animation;
        /// </summary>
        public async Task<bool> PausePathAnimationAsync(string animationId)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                var animation = await GetAnimationAsync(animationId);

                if (animation.State == PathAnimationState.Playing)
                {
                    animation.State = PathAnimationState.Paused;
                    _metrics.AnimationsPaused++;

                    RaisePathAnimationPaused(animationId);
                    _logger.LogDebug($"Path animation paused: {animationId}");

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to pause path animation: {animationId}", ex);
                throw new PathAnimatorException("Path animation pause failed", ex);
            }
        }

        /// <summary>
        /// Resumes a paused path animation;
        /// </summary>
        public async Task<bool> ResumePathAnimationAsync(string animationId)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                var animation = await GetAnimationAsync(animationId);

                if (animation.State == PathAnimationState.Paused)
                {
                    animation.State = PathAnimationState.Playing;
                    _metrics.AnimationsResumed++;

                    RaisePathAnimationResumed(animationId);
                    _logger.LogDebug($"Path animation resumed: {animationId}");

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to resume path animation: {animationId}", ex);
                throw new PathAnimatorException("Path animation resume failed", ex);
            }
        }

        /// <summary>
        /// Seeks to a specific position in the path animation;
        /// </summary>
        public async Task<bool> SeekAnimationAsync(string animationId, float progress, bool updateImmediately = true)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                var animation = await GetAnimationAsync(animationId);
                progress = Math.Clamp(progress, 0.0f, 1.0f);

                animation.CurrentProgress = progress;
                animation.ElapsedTime = progress * animation.TotalDuration;

                if (updateImmediately)
                {
                    await UpdateAnimationTransformAsync(animation);
                }

                _metrics.AnimationSeeks++;
                _logger.LogDebug($"Animation seek: {animationId} to {progress:P2}");

                return true;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to seek animation: {animationId}", ex);
                throw new PathAnimatorException("Animation seek failed", ex);
            }
        }

        /// <summary>
        /// Gets the current transform for a path animation;
        /// </summary>
        public async Task<PathTransform> GetCurrentTransformAsync(string animationId)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                var animation = await GetAnimationAsync(animationId);
                return new PathTransform;
                {
                    Position = animation.CurrentPosition,
                    Rotation = animation.CurrentRotation,
                    Scale = animation.CurrentScale,
                    Progress = animation.CurrentProgress;
                };
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to get current transform for animation: {animationId}", ex);
                throw new PathAnimatorException("Transform retrieval failed", ex);
            }
        }

        /// <summary>
        /// Sets the playback speed of a path animation;
        /// </summary>
        public async Task SetAnimationSpeedAsync(string animationId, float speedMultiplier)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                var animation = await GetAnimationAsync(animationId);

                if (speedMultiplier <= 0)
                    throw new PathAnimatorException("Speed multiplier must be greater than zero");

                animation.SpeedMultiplier = speedMultiplier;
                _metrics.SpeedChanges++;

                _logger.LogDebug($"Animation speed changed: {animationId} to {speedMultiplier}x");
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to set animation speed: {animationId}", ex);
                throw new PathAnimatorException("Animation speed setting failed", ex);
            }
        }

        /// <summary>
        /// Reverses the direction of a path animation;
        /// </summary>
        public async Task ReverseAnimationAsync(string animationId)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                var animation = await GetAnimationAsync(animationId);
                animation.Direction = animation.Direction == PathDirection.Forward;
                    ? PathDirection.Backward;
                    : PathDirection.Forward;

                _metrics.DirectionChanges++;
                _logger.LogDebug($"Animation direction reversed: {animationId} to {animation.Direction}");
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to reverse animation: {animationId}", ex);
                throw new PathAnimatorException("Animation reversal failed", ex);
            }
        }

        /// <summary>
        /// Creates a path from a set of points;
        /// </summary>
        public async Task<string> CreatePathFromPointsAsync(IList<Vector3> points, PathCreationParams creationParams)
        {
            if (points == null || points.Count < 2)
                throw new ArgumentException("Path must contain at least 2 points", nameof(points));

            if (creationParams == null)
                throw new ArgumentNullException(nameof(creationParams));

            try
            {
                await ValidateSystemState();

                var pathDefinition = new PathDefinition;
                {
                    PathType = creationParams.PathType,
                    Points = new List<Vector3>(points),
                    IsClosed = creationParams.IsClosed,
                    Resolution = creationParams.Resolution;
                };

                // Apply smoothing if requested;
                if (creationParams.SmoothPath && points.Count > 2)
                {
                    pathDefinition = await _smoother.SmoothPathAsync(pathDefinition, creationParams.SmoothingFactor);
                }

                // Calculate path length and properties;
                await CalculatePathPropertiesAsync(pathDefinition);

                return await RegisterPathAsync(pathDefinition);
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Failed to create path from points", ex);
                throw new PathAnimatorException("Path creation failed", ex);
            }
        }

        /// <summary>
        /// Creates a bezier path from control points;
        /// </summary>
        public async Task<string> CreateBezierPathAsync(IList<BezierControlPoint> controlPoints, PathCreationParams creationParams)
        {
            if (controlPoints == null || controlPoints.Count < 2)
                throw new ArgumentException("Bezier path must contain at least 2 control points", nameof(controlPoints));

            try
            {
                await ValidateSystemState();

                var pathDefinition = new PathDefinition;
                {
                    PathType = PathType.Bezier,
                    ControlPoints = new List<BezierControlPoint>(controlPoints),
                    IsClosed = creationParams.IsClosed,
                    Resolution = creationParams.Resolution;
                };

                // Generate bezier curve points;
                await GenerateBezierPointsAsync(pathDefinition);

                // Calculate path length and properties;
                await CalculatePathPropertiesAsync(pathDefinition);

                return await RegisterPathAsync(pathDefinition);
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Failed to create bezier path", ex);
                throw new PathAnimatorException("Bezier path creation failed", ex);
            }
        }

        /// <summary>
        /// Creates a circular path;
        /// </summary>
        public async Task<string> CreateCircularPathAsync(CircularPathParams circularParams)
        {
            if (circularParams == null)
                throw new ArgumentNullException(nameof(circularParams));

            try
            {
                await ValidateSystemState();

                var points = new List<Vector3>();
                var segments = circularParams.Segments;

                for (int i = 0; i <= segments; i++)
                {
                    var angle = (float)i / segments * MathF.PI * 2.0f;
                    var x = circularParams.Center.X + MathF.Cos(angle) * circularParams.Radius;
                    var z = circularParams.Center.Z + MathF.Sin(angle) * circularParams.Radius;
                    points.Add(new Vector3(x, circularParams.Center.Y, z));
                }

                var creationParams = new PathCreationParams;
                {
                    PathType = PathType.Linear,
                    IsClosed = true,
                    Resolution = circularParams.Resolution;
                };

                return await CreatePathFromPointsAsync(points, creationParams);
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Failed to create circular path", ex);
                throw new PathAnimatorException("Circular path creation failed", ex);
            }
        }

        /// <summary>
        /// Samples a path at a specific progress value;
        /// </summary>
        public async Task<PathSampleResult> SamplePathAsync(string pathId, float progress, PathSamplingParams samplingParams = null)
        {
            if (string.IsNullOrEmpty(pathId))
                throw new ArgumentException("Path ID cannot be null or empty", nameof(pathId));

            try
            {
                var pathDefinition = await GetPathDefinitionAsync(pathId);
                samplingParams ??= new PathSamplingParams();

                return await _interpolator.SamplePathAsync(pathDefinition, progress, samplingParams);
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to sample path: {pathId}", ex);
                throw new PathAnimatorException("Path sampling failed", ex);
            }
        }

        /// <summary>
        /// Calculates the length of a path;
        /// </summary>
        public async Task<float> CalculatePathLengthAsync(string pathId)
        {
            if (string.IsNullOrEmpty(pathId))
                throw new ArgumentException("Path ID cannot be null or empty", nameof(pathId));

            try
            {
                var pathDefinition = await GetPathDefinitionAsync(pathId);
                return pathDefinition.TotalLength;
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to calculate path length: {pathId}", ex);
                throw new PathAnimatorException("Path length calculation failed", ex);
            }
        }

        /// <summary>
        /// Gets the progress along the path for a given distance;
        /// </summary>
        public async Task<float> GetProgressForDistanceAsync(string pathId, float distance)
        {
            if (string.IsNullOrEmpty(pathId))
                throw new ArgumentException("Path ID cannot be null or empty", nameof(pathId));

            try
            {
                var pathDefinition = await GetPathDefinitionAsync(pathId);
                return await _interpolator.GetProgressForDistanceAsync(pathDefinition, distance);
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to get progress for distance: {pathId}", ex);
                throw new PathAnimatorException("Progress calculation failed", ex);
            }
        }

        /// <summary>
        /// Registers a path preset for quick path creation;
        /// </summary>
        public async Task RegisterPathPresetAsync(PathPreset preset)
        {
            if (preset == null)
                throw new ArgumentNullException(nameof(preset));

            try
            {
                await ValidatePreset(preset);

                lock (_animationLock)
                {
                    _pathPresets[preset.PresetId] = preset;
                }

                _logger.LogInformation($"Path preset registered: {preset.PresetId}");
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to register path preset: {preset.PresetId}", ex);
                throw new PathAnimatorException("Path preset registration failed", ex);
            }
        }

        /// <summary>
        /// Creates a path from a preset;
        /// </summary>
        public async Task<string> CreatePathFromPresetAsync(string presetId, PathPresetParams presetParams)
        {
            if (string.IsNullOrEmpty(presetId))
                throw new ArgumentException("Preset ID cannot be null or empty", nameof(presetId));

            try
            {
                var preset = await GetPresetAsync(presetId);
                PathDefinition pathDefinition;

                switch (preset.PresetType)
                {
                    case PathPresetType.Circular:
                        pathDefinition = await CreateCircularPathFromPresetAsync(preset, presetParams);
                        break;
                    case PathPresetType.FigureEight:
                        pathDefinition = await CreateFigureEightPathFromPresetAsync(preset, presetParams);
                        break;
                    case PathPresetType.Helix:
                        pathDefinition = await CreateHelixPathFromPresetAsync(preset, presetParams);
                        break;
                    case PathPresetType.Wave:
                        pathDefinition = await CreateWavePathFromPresetAsync(preset, presetParams);
                        break;
                    default:
                        throw new PathAnimatorException($"Unknown preset type: {preset.PresetType}");
                }

                return await RegisterPathAsync(pathDefinition);
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to create path from preset: {presetId}", ex);
                throw new PathAnimatorException("Preset path creation failed", ex);
            }
        }

        /// <summary>
        /// Queues a path command for execution;
        /// </summary>
        public async Task QueuePathCommandAsync(PathCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            try
            {
                lock (_animationLock)
                {
                    _commandQueue.Enqueue(command);
                }

                _metrics.CommandsQueued++;
                _logger.LogDebug($"Path command queued: {command.CommandType}");
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync("Failed to queue path command", ex);
                throw new PathAnimatorException("Path command queuing failed", ex);
            }
        }

        /// <summary>
        /// Gets the progress of a path animation;
        /// </summary>
        public async Task<PathAnimationProgress> GetAnimationProgressAsync(string animationId)
        {
            if (string.IsNullOrEmpty(animationId))
                throw new ArgumentException("Animation ID cannot be null or empty", nameof(animationId));

            try
            {
                var animation = await GetAnimationAsync(animationId);

                return new PathAnimationProgress;
                {
                    AnimationId = animationId,
                    CurrentProgress = animation.CurrentProgress,
                    ElapsedTime = animation.ElapsedTime,
                    TotalDuration = animation.TotalDuration,
                    State = animation.State,
                    SpeedMultiplier = animation.SpeedMultiplier,
                    Direction = animation.Direction;
                };
            }
            catch (Exception ex)
            {
                await HandlePathExceptionAsync($"Failed to get animation progress: {animationId}", ex);
                throw new PathAnimatorException("Animation progress retrieval failed", ex);
            }
        }
        #endregion;

        #region Private Methods;
        private async Task LoadConfigurationAsync()
        {
            try
            {
                _settings = _settingsManager.GetSection<PathAnimatorSettings>("PathAnimator") ?? new PathAnimatorSettings();
                _logger.LogInformation("Path animator configuration loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load path animator configuration: {ex.Message}");
                _settings = new PathAnimatorSettings();
            }

            await Task.CompletedTask;
        }

        private async Task PreloadPathPresetsAsync()
        {
            try
            {
                // Preload common path presets;
                var commonPresets = new[]
                {
                    CreateCircularPreset(),
                    CreateFigureEightPreset(),
                    CreateHelixPreset(),
                    CreateWavePreset()
                };

                foreach (var preset in commonPresets)
                {
                    await RegisterPathPresetAsync(preset);
                }

                _logger.LogInformation("Common path presets preloaded");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to preload path presets: {ex.Message}");
            }
        }

        private async Task InitializeSubsystemsAsync()
        {
            _interpolator.Initialize(_settings);
            _smoother.Initialize(_settings);
            _optimizer.Initialize(_settings);

            _logger.LogDebug("Path animator subsystems initialized");
            await Task.CompletedTask;
        }

        private async Task ProcessCommandQueueAsync()
        {
            List<PathCommand> commandsToProcess;
            lock (_animationLock)
            {
                commandsToProcess = new List<PathCommand>();
                while (_commandQueue.Count > 0 && commandsToProcess.Count < _settings.MaxCommandsPerFrame)
                {
                    commandsToProcess.Add(_commandQueue.Dequeue());
                }
            }

            foreach (var command in commandsToProcess)
            {
                try
                {
                    await ExecutePathCommandAsync(command);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error executing path command: {ex.Message}");
                }
            }

            _metrics.CommandsProcessed += commandsToProcess.Count;
        }

        private async Task ExecutePathCommandAsync(PathCommand command)
        {
            switch (command.CommandType)
            {
                case PathCommandType.StartAnimation:
                    await ExecuteStartAnimationCommandAsync(command);
                    break;
                case PathCommandType.StopAnimation:
                    await ExecuteStopAnimationCommandAsync(command);
                    break;
                case PathCommandType.PauseAnimation:
                    await ExecutePauseAnimationCommandAsync(command);
                    break;
                case PathCommandType.ResumeAnimation:
                    await ExecuteResumeAnimationCommandAsync(command);
                    break;
                case PathCommandType.SeekAnimation:
                    await ExecuteSeekAnimationCommandAsync(command);
                    break;
                case PathCommandType.SetSpeed:
                    await ExecuteSetSpeedCommandAsync(command);
                    break;
                case PathCommandType.ReverseAnimation:
                    await ExecuteReverseAnimationCommandAsync(command);
                    break;
                default:
                    _logger.LogWarning($"Unknown path command type: {command.CommandType}");
                    break;
            }
        }

        private async Task UpdateActiveAnimationsAsync(float deltaTime)
        {
            List<PathAnimation> animationsToUpdate;
            List<string> animationsToRemove = new List<string>();

            lock (_animationLock)
            {
                animationsToUpdate = _activeAnimations.Values.ToList();
            }

            foreach (var animation in animationsToUpdate)
            {
                try
                {
                    var shouldContinue = await UpdateAnimationAsync(animation, deltaTime);
                    if (!shouldContinue)
                    {
                        animationsToRemove.Add(animation.AnimationId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error updating animation {animation.AnimationId}: {ex.Message}");
                    animationsToRemove.Add(animation.AnimationId);
                }
            }

            // Remove completed animations;
            foreach (var animationId in animationsToRemove)
            {
                await StopPathAnimationAsync(animationId, false);
            }
        }

        private async Task<bool> UpdateAnimationAsync(PathAnimation animation, float deltaTime)
        {
            if (animation.State != PathAnimationState.Playing)
                return true;

            // Apply speed multiplier;
            var effectiveDeltaTime = deltaTime * animation.SpeedMultiplier;

            // Update animation progress based on direction;
            if (animation.Direction == PathDirection.Forward)
            {
                animation.ElapsedTime += effectiveDeltaTime;
                animation.CurrentProgress = animation.ElapsedTime / animation.TotalDuration;
            }
            else;
            {
                animation.ElapsedTime -= effectiveDeltaTime;
                animation.CurrentProgress = animation.ElapsedTime / animation.TotalDuration;
            }

            // Handle loop behavior;
            if (animation.CurrentProgress >= 1.0f || animation.CurrentProgress <= 0.0f)
            {
                switch (animation.AnimationParams.LoopBehavior)
                {
                    case PathLoopBehavior.Once:
                        await CompleteAnimationAsync(animation);
                        return false;
                    case PathLoopBehavior.Loop:
                        if (animation.Direction == PathDirection.Forward)
                        {
                            animation.ElapsedTime = 0.0f;
                            animation.CurrentProgress = 0.0f;
                        }
                        else;
                        {
                            animation.ElapsedTime = animation.TotalDuration;
                            animation.CurrentProgress = 1.0f;
                        }
                        break;
                    case PathLoopBehavior.PingPong:
                        animation.Direction = animation.Direction == PathDirection.Forward;
                            ? PathDirection.Backward;
                            : PathDirection.Forward;
                        animation.CurrentProgress = Math.Clamp(animation.CurrentProgress, 0.0f, 1.0f);
                        break;
                }
            }

            // Update animation transform;
            await UpdateAnimationTransformAsync(animation);

            // Raise progress event;
            RaisePathAnimationProgress(animation.AnimationId, animation.CurrentProgress);

            _metrics.AnimationUpdates++;
            return true;
        }

        private async Task UpdateAnimationTransformAsync(PathAnimation animation)
        {
            var samplingParams = new PathSamplingParams;
            {
                CalculateRotation = animation.AnimationParams.OrientToPath,
                CalculateTangent = animation.AnimationParams.OrientToPath,
                RotationMode = animation.AnimationParams.RotationMode,
                UpVector = animation.AnimationParams.UpVector;
            };

            var sampleResult = await _interpolator.SamplePathAsync(
                animation.PathDefinition,
                animation.CurrentProgress,
                samplingParams;
            );

            // Apply easing to progress if specified;
            var easedProgress = ApplyEasing(animation.CurrentProgress, animation.AnimationParams.EasingFunction);

            // Update animation state;
            animation.CurrentPosition = sampleResult.Position;
            animation.CurrentRotation = sampleResult.Rotation;
            animation.CurrentScale = Vector3.Lerp(
                animation.AnimationParams.StartScale,
                animation.AnimationParams.EndScale,
                easedProgress;
            );

            // Store additional sample data;
            animation.CurrentTangent = sampleResult.Tangent;
            animation.CurrentNormal = sampleResult.Normal;
            animation.CurrentBinormal = sampleResult.Binormal;
        }

        private async Task InitializeAnimationAsync(PathAnimation animation)
        {
            animation.State = PathAnimationState.Playing;
            animation.StartTime = DateTime.UtcNow;
            animation.TotalDuration = animation.PathDefinition.TotalLength / animation.AnimationParams.Speed;

            // Set initial transform;
            await UpdateAnimationTransformAsync(animation);
        }

        private async Task CompleteAnimationAsync(PathAnimation animation)
        {
            animation.State = PathAnimationState.Completed;
            animation.EndTime = DateTime.UtcNow;

            // Ensure final transform is set;
            animation.CurrentProgress = animation.Direction == PathDirection.Forward ? 1.0f : 0.0f;
            await UpdateAnimationTransformAsync(animation);

            _metrics.AnimationsCompleted++;
            RaisePathAnimationCompleted(animation.AnimationId, animation.AnimationParams);

            _logger.LogDebug($"Path animation completed: {animation.AnimationId}");
        }

        private async Task CalculatePathPropertiesAsync(PathDefinition pathDefinition)
        {
            pathDefinition.TotalLength = await _interpolator.CalculatePathLengthAsync(pathDefinition);
            pathDefinition.BoundingBox = CalculateBoundingBox(pathDefinition.Points);
        }

        private async Task GenerateBezierPointsAsync(PathDefinition pathDefinition)
        {
            pathDefinition.Points = await _interpolator.GenerateBezierPointsAsync(
                pathDefinition.ControlPoints,
                pathDefinition.Resolution,
                pathDefinition.IsClosed;
            );
        }

        private BoundingBox CalculateBoundingBox(List<Vector3> points)
        {
            if (points == null || points.Count == 0)
                return new BoundingBox();

            var min = points[0];
            var max = points[0];

            foreach (var point in points)
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            return new BoundingBox(min, max);
        }

        private float ApplyEasing(float progress, EasingFunction easingFunction)
        {
            return _interpolator.ApplyEasing(progress, easingFunction);
        }

        private async Task<PathDefinition> CreateCircularPathFromPresetAsync(PathPreset preset, PathPresetParams presetParams)
        {
            var circularParams = new CircularPathParams;
            {
                Center = presetParams.Center,
                Radius = presetParams.Radius,
                Segments = preset.Segments,
                Resolution = preset.Resolution;
            };

            var pathId = await CreateCircularPathAsync(circularParams);
            return await GetPathDefinitionAsync(pathId);
        }

        private async Task<PathDefinition> CreateFigureEightPathFromPresetAsync(PathPreset preset, PathPresetParams presetParams)
        {
            var points = new List<Vector3>();
            var segments = preset.Segments;

            for (int i = 0; i <= segments; i++)
            {
                var t = (float)i / segments * MathF.PI * 2.0f;
                var x = presetParams.Center.X + MathF.Sin(t) * presetParams.Radius;
                var z = presetParams.Center.Z + MathF.Sin(2 * t) * presetParams.Radius * 0.5f;
                points.Add(new Vector3(x, presetParams.Center.Y, z));
            }

            var creationParams = new PathCreationParams;
            {
                PathType = PathType.Linear,
                IsClosed = true,
                Resolution = preset.Resolution;
            };

            var pathId = await CreatePathFromPointsAsync(points, creationParams);
            return await GetPathDefinitionAsync(pathId);
        }

        private async Task<PathDefinition> CreateHelixPathFromPresetAsync(PathPreset preset, PathPresetParams presetParams)
        {
            var points = new List<Vector3>();
            var segments = preset.Segments;
            var height = presetParams.Height;

            for (int i = 0; i <= segments; i++)
            {
                var t = (float)i / segments * MathF.PI * 2.0f * presetParams.Turns;
                var x = presetParams.Center.X + MathF.Cos(t) * presetParams.Radius;
                var z = presetParams.Center.Z + MathF.Sin(t) * presetParams.Radius;
                var y = presetParams.Center.Y + (float)i / segments * height;
                points.Add(new Vector3(x, y, z));
            }

            var creationParams = new PathCreationParams;
            {
                PathType = PathType.Linear,
                IsClosed = false,
                Resolution = preset.Resolution;
            };

            var pathId = await CreatePathFromPointsAsync(points, creationParams);
            return await GetPathDefinitionAsync(pathId);
        }

        private async Task<PathDefinition> CreateWavePathFromPresetAsync(PathPreset preset, PathPresetParams presetParams)
        {
            var points = new List<Vector3>();
            var segments = preset.Segments;
            var length = presetParams.Length;

            for (int i = 0; i <= segments; i++)
            {
                var x = (float)i / segments * length;
                var y = presetParams.Center.Y + MathF.Sin(x * presetParams.Frequency) * presetParams.Amplitude;
                points.Add(new Vector3(x, y, presetParams.Center.Z));
            }

            var creationParams = new PathCreationParams;
            {
                PathType = PathType.Linear,
                IsClosed = false,
                Resolution = preset.Resolution;
            };

            var pathId = await CreatePathFromPointsAsync(points, creationParams);
            return await GetPathDefinitionAsync(pathId);
        }

        private async Task UpdateMetricsAsync()
        {
            _metrics.ActiveAnimations = ActiveAnimationCount;
            _metrics.RegisteredPaths = PathDefinitionCount;

            RaiseMetricsUpdated();
            await Task.CompletedTask;
        }

        private PathPreset CreateCircularPreset()
        {
            return new PathPreset;
            {
                PresetId = "Circular_Standard",
                PresetType = PathPresetType.Circular,
                Segments = 32,
                Resolution = 100;
            };
        }

        private PathPreset CreateFigureEightPreset()
        {
            return new PathPreset;
            {
                PresetId = "FigureEight_Standard",
                PresetType = PathPresetType.FigureEight,
                Segments = 64,
                Resolution = 100;
            };
        }

        private PathPreset CreateHelixPreset()
        {
            return new PathPreset;
            {
                PresetId = "Helix_Standard",
                PresetType = PathPresetType.Helix,
                Segments = 48,
                Resolution = 100;
            };
        }

        private PathPreset CreateWavePreset()
        {
            return new PathPreset;
            {
                PresetId = "Wave_Standard",
                PresetType = PathPresetType.Wave,
                Segments = 50,
                Resolution = 100;
            };
        }

        private async Task<PathAnimation> GetAnimationAsync(string animationId)
        {
            lock (_animationLock)
            {
                if (_activeAnimations.TryGetValue(animationId, out var animation))
                {
                    return animation;
                }
            }

            throw new PathAnimatorException($"Animation not found: {animationId}");
        }

        private async Task<PathDefinition> GetPathDefinitionAsync(string pathId)
        {
            lock (_animationLock)
            {
                if (_pathDefinitions.TryGetValue(pathId, out var pathDefinition))
                {
                    return pathDefinition;
                }
            }

            throw new PathAnimatorException($"Path definition not found: {pathId}");
        }

        private async Task<PathPreset> GetPresetAsync(string presetId)
        {
            lock (_animationLock)
            {
                if (_pathPresets.TryGetValue(presetId, out var preset))
                {
                    return preset;
                }
            }

            throw new PathAnimatorException($"Path preset not found: {presetId}");
        }

        private async Task ValidateSystemState()
        {
            if (!_isInitialized)
                throw new PathAnimatorException("PathAnimator is not initialized");

            await Task.CompletedTask;
        }

        private async Task ValidatePathDefinition(PathDefinition pathDefinition)
        {
            if (pathDefinition.Points == null || pathDefinition.Points.Count < 2)
                throw new PathAnimatorException("Path must contain at least 2 points");

            if (pathDefinition.Resolution <= 0)
                throw new PathAnimatorException("Path resolution must be greater than zero");

            await Task.CompletedTask;
        }

        private async Task ValidateAnimationParams(PathAnimationParams animationParams)
        {
            if (string.IsNullOrEmpty(animationParams.PathId))
                throw new PathAnimatorException("Path ID cannot be null or empty");

            if (animationParams.Speed <= 0)
                throw new PathAnimatorException("Animation speed must be greater than zero");

            await Task.CompletedTask;
        }

        private async Task ValidatePreset(PathPreset preset)
        {
            if (string.IsNullOrEmpty(preset.PresetId))
                throw new PathAnimatorException("Preset ID cannot be null or empty");

            if (preset.Segments <= 0)
                throw new PathAnimatorException("Preset segments must be greater than zero");

            await Task.CompletedTask;
        }

        private string GeneratePathId()
        {
            return $"Path_{Guid.NewGuid():N}";
        }

        private string GenerateAnimationId()
        {
            return $"Animation_{Guid.NewGuid():N}";
        }

        private async Task HandlePathExceptionAsync(string context, Exception exception)
        {
            _logger.LogError($"{context}: {exception.Message}", exception);
            await _recoveryEngine.ExecuteRecoveryStrategyAsync("PathAnimator", exception);
        }

        // Command execution methods;
        private async Task ExecuteStartAnimationCommandAsync(PathCommand command)
        {
            var animationParams = (PathAnimationParams)command.Parameters["AnimationParams"];
            await StartPathAnimationAsync(animationParams);
        }

        private async Task ExecuteStopAnimationCommandAsync(PathCommand command)
        {
            var animationId = command.Parameters["AnimationId"] as string;
            var immediate = (bool)command.Parameters["Immediate"];
            await StopPathAnimationAsync(animationId, immediate);
        }

        private async Task ExecutePauseAnimationCommandAsync(PathCommand command)
        {
            var animationId = command.Parameters["AnimationId"] as string;
            await PausePathAnimationAsync(animationId);
        }

        private async Task ExecuteResumeAnimationCommandAsync(PathCommand command)
        {
            var animationId = command.Parameters["AnimationId"] as string;
            await ResumePathAnimationAsync(animationId);
        }

        private async Task ExecuteSeekAnimationCommandAsync(PathCommand command)
        {
            var animationId = command.Parameters["AnimationId"] as string;
            var progress = (float)command.Parameters["Progress"];
            var updateImmediately = (bool)command.Parameters["UpdateImmediately"];
            await SeekAnimationAsync(animationId, progress, updateImmediately);
        }

        private async Task ExecuteSetSpeedCommandAsync(PathCommand command)
        {
            var animationId = command.Parameters["AnimationId"] as string;
            var speedMultiplier = (float)command.Parameters["SpeedMultiplier"];
            await SetAnimationSpeedAsync(animationId, speedMultiplier);
        }

        private async Task ExecuteReverseAnimationCommandAsync(PathCommand command)
        {
            var animationId = command.Parameters["AnimationId"] as string;
            await ReverseAnimationAsync(animationId);
        }

        private void RaisePathAnimationStarted(string animationId, PathAnimationParams animationParams)
        {
            PathAnimationStarted?.Invoke(this, new PathAnimationStartedEventArgs;
            {
                AnimationId = animationId,
                AnimationParams = animationParams,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaisePathAnimationCompleted(string animationId, PathAnimationParams animationParams)
        {
            PathAnimationCompleted?.Invoke(this, new PathAnimationCompletedEventArgs;
            {
                AnimationId = animationId,
                AnimationParams = animationParams,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaisePathAnimationPaused(string animationId)
        {
            PathAnimationPaused?.Invoke(this, new PathAnimationPausedEventArgs;
            {
                AnimationId = animationId,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaisePathAnimationResumed(string animationId)
        {
            PathAnimationResumed?.Invoke(this, new PathAnimationResumedEventArgs;
            {
                AnimationId = animationId,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaisePathAnimationStopped(string animationId, bool immediate)
        {
            PathAnimationStopped?.Invoke(this, new PathAnimationStoppedEventArgs;
            {
                AnimationId = animationId,
                Immediate = immediate,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaisePathAnimationProgress(string animationId, float progress)
        {
            PathAnimationProgress?.Invoke(this, new PathAnimationProgressEventArgs;
            {
                AnimationId = animationId,
                Progress = progress,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseMetricsUpdated()
        {
            MetricsUpdated?.Invoke(this, new PathAnimatorMetricsUpdatedEventArgs;
            {
                Metrics = _metrics,
                Timestamp = DateTime.UtcNow;
            });
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
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Stop all active animations;
                    var animationIds = _activeAnimations.Keys.ToList();
                    foreach (var animationId in animationIds)
                    {
                        try
                        {
                            StopPathAnimationAsync(animationId).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error stopping animation during disposal: {ex.Message}");
                        }
                    }

                    _activeAnimations.Clear();
                    _pathDefinitions.Clear();
                    _pathPresets.Clear();
                    _commandQueue.Clear();

                    _interpolator?.Dispose();
                    _smoother?.Dispose();
                    _optimizer?.Dispose();
                }

                _isDisposed = true;
            }
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    public enum PathType;
    {
        Linear,
        Bezier,
        CatmullRom,
        Hermite,
        BSpline;
    }

    public enum PathAnimationState;
    {
        Playing,
        Paused,
        Stopped,
        Completed;
    }

    public enum PathDirection;
    {
        Forward,
        Backward;
    }

    public enum PathLoopBehavior;
    {
        Once,
        Loop,
        PingPong;
    }

    public enum EasingFunction;
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
        Bounce,
        Elastic,
        Back;
    }

    public enum RotationMode;
    {
        None,
        Tangent,
        LookAt,
        Custom;
    }

    public enum PathPresetType;
    {
        Circular,
        FigureEight,
        Helix,
        Wave,
        Spiral,
        Custom;
    }

    public enum PathCommandType;
    {
        StartAnimation,
        StopAnimation,
        PauseAnimation,
        ResumeAnimation,
        SeekAnimation,
        SetSpeed,
        ReverseAnimation;
    }

    public class PathAnimatorSettings;
    {
        public float DefaultSpeed { get; set; } = 1.0f;
        public int DefaultResolution { get; set; } = 100;
        public bool AutoOptimizePaths { get; set; } = true;
        public float OptimizationTolerance { get; set; } = 0.01f;
        public int MaxCommandsPerFrame { get; set; } = 10;
        public int MaxActiveAnimations { get; set; } = 50;
        public bool EnablePathSmoothing { get; set; } = true;
        public float SmoothingFactor { get; set; } = 0.5f;
    }

    public class PathAnimatorMetrics;
    {
        public int AnimationsStarted { get; set; }
        public int AnimationsCompleted { get; set; }
        public int AnimationsStopped { get; set; }
        public int AnimationsPaused { get; set; }
        public int AnimationsResumed { get; set; }
        public int ActiveAnimations { get; set; }
        public int PathsRegistered { get; set; }
        public int PathsUnregistered { get; set; }
        public int RegisteredPaths { get; set; }
        public int AnimationUpdates { get; set; }
        public int AnimationSeeks { get; set; }
        public int SpeedChanges { get; set; }
        public int DirectionChanges { get; set; }
        public int CommandsQueued { get; set; }
        public int CommandsProcessed { get; set; }
        public float LastUpdateTime { get; set; }
        public float LastUpdateDuration { get; set; }
    }

    public class PathDefinition;
    {
        public string Id { get; set; }
        public PathType PathType { get; set; }
        public List<Vector3> Points { get; set; } = new List<Vector3>();
        public List<BezierControlPoint> ControlPoints { get; set; } = new List<BezierControlPoint>();
        public bool IsClosed { get; set; }
        public int Resolution { get; set; } = 100;
        public float TotalLength { get; set; }
        public BoundingBox BoundingBox { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public class PathAnimation;
    {
        public string AnimationId { get; }
        public PathDefinition PathDefinition { get; }
        public PathAnimationParams AnimationParams { get; }
        public PathAnimationState State { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public float ElapsedTime { get; set; }
        public float TotalDuration { get; set; }
        public float CurrentProgress { get; set; }
        public float SpeedMultiplier { get; set; } = 1.0f;
        public PathDirection Direction { get; set; } = PathDirection.Forward;

        // Current transform;
        public Vector3 CurrentPosition { get; set; }
        public Quaternion CurrentRotation { get; set; }
        public Vector3 CurrentScale { get; set; }

        // Path frame;
        public Vector3 CurrentTangent { get; set; }
        public Vector3 CurrentNormal { get; set; }
        public Vector3 CurrentBinormal { get; set; }

        public PathAnimation(string animationId, PathDefinition pathDefinition, PathAnimationParams animationParams)
        {
            AnimationId = animationId;
            PathDefinition = pathDefinition;
            AnimationParams = animationParams;
            State = PathAnimationState.Playing;
            CurrentScale = Vector3.One;
        }
    }

    public class PathAnimationParams;
    {
        public string PathId { get; set; }
        public float Speed { get; set; } = 1.0f;
        public PathLoopBehavior LoopBehavior { get; set; } = PathLoopBehavior.Once;
        public EasingFunction EasingFunction { get; set; } = EasingFunction.Linear;
        public bool OrientToPath { get; set; } = true;
        public RotationMode RotationMode { get; set; } = RotationMode.Tangent;
        public Vector3 UpVector { get; set; } = Vector3.UnitY;
        public Vector3 StartScale { get; set; } = Vector3.One;
        public Vector3 EndScale { get; set; } = Vector3.One;
        public Dictionary<string, object> AdditionalParams { get; set; } = new Dictionary<string, object>();
    }

    public class PathCreationParams;
    {
        public PathType PathType { get; set; } = PathType.Linear;
        public bool IsClosed { get; set; }
        public int Resolution { get; set; } = 100;
        public bool SmoothPath { get; set; } = true;
        public float SmoothingFactor { get; set; } = 0.5f;
    }

    public class CircularPathParams;
    {
        public Vector3 Center { get; set; }
        public float Radius { get; set; } = 1.0f;
        public int Segments { get; set; } = 32;
        public int Resolution { get; set; } = 100;
    }

    public class PathSamplingParams;
    {
        public bool CalculateRotation { get; set; } = true;
        public bool CalculateTangent { get; set; } = true;
        public RotationMode RotationMode { get; set; } = RotationMode.Tangent;
        public Vector3 UpVector { get; set; } = Vector3.UnitY;
        public float SamplePrecision { get; set; } = 0.001f;
    }

    public class PathSampleResult;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Tangent { get; set; }
        public Vector3 Normal { get; set; }
        public Vector3 Binormal { get; set; }
        public float Progress { get; set; }
        public float Distance { get; set; }
    }

    public class PathTransform;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; } = Vector3.One;
        public float Progress { get; set; }
    }

    public class PathAnimationProgress;
    {
        public string AnimationId { get; set; }
        public float CurrentProgress { get; set; }
        public float ElapsedTime { get; set; }
        public float TotalDuration { get; set; }
        public PathAnimationState State { get; set; }
        public float SpeedMultiplier { get; set; }
        public PathDirection Direction { get; set; }
    }

    public class BezierControlPoint;
    {
        public Vector3 Position { get; set; }
        public Vector3 ControlPointIn { get; set; }
        public Vector3 ControlPointOut { get; set; }

        public BezierControlPoint(Vector3 position)
        {
            Position = position;
            ControlPointIn = position;
            ControlPointOut = position;
        }

        public BezierControlPoint(Vector3 position, Vector3 controlPointIn, Vector3 controlPointOut)
        {
            Position = position;
            ControlPointIn = controlPointIn;
            ControlPointOut = controlPointOut;
        }
    }

    public class PathPreset;
    {
        public string PresetId { get; set; }
        public PathPresetType PresetType { get; set; }
        public int Segments { get; set; } = 32;
        public int Resolution { get; set; } = 100;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class PathPresetParams;
    {
        public Vector3 Center { get; set; }
        public float Radius { get; set; } = 1.0f;
        public float Height { get; set; } = 2.0f;
        public float Length { get; set; } = 5.0f;
        public float Frequency { get; set; } = 1.0f;
        public float Amplitude { get; set; } = 1.0f;
        public float Turns { get; set; } = 3.0f;
    }

    public class PathCommand;
    {
        public PathCommandType CommandType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public DateTime QueueTime { get; set; } = DateTime.UtcNow;
    }

    public class BoundingBox;
    {
        public Vector3 Min { get; set; }
        public Vector3 Max { get; set; }
        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;

        public BoundingBox() { }

        public BoundingBox(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }
    }

    // Event args classes;
    public class PathAnimationStartedEventArgs : EventArgs;
    {
        public string AnimationId { get; set; }
        public PathAnimationParams AnimationParams { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PathAnimationCompletedEventArgs : EventArgs;
    {
        public string AnimationId { get; set; }
        public PathAnimationParams AnimationParams { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PathAnimationPausedEventArgs : EventArgs;
    {
        public string AnimationId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PathAnimationResumedEventArgs : EventArgs;
    {
        public string AnimationId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PathAnimationStoppedEventArgs : EventArgs;
    {
        public string AnimationId { get; set; }
        public bool Immediate { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PathAnimationProgressEventArgs : EventArgs;
    {
        public string AnimationId { get; set; }
        public float Progress { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PathAnimatorMetricsUpdatedEventArgs : EventArgs;
    {
        public PathAnimatorMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PathAnimatorException : Exception
    {
        public PathAnimatorException(string message) : base(message) { }
        public PathAnimatorException(string message, Exception innerException) : base(message, innerException) { }
    }

    // Internal subsystem implementations;
    internal class PathInterpolator : IDisposable
    {
        private readonly ILogger _logger;
        private PathAnimatorSettings _settings;

        public PathInterpolator(ILogger logger)
        {
            _logger = logger;
        }

        public void Initialize(PathAnimatorSettings settings)
        {
            _settings = settings;
        }

        public async Task<PathSampleResult> SamplePathAsync(PathDefinition pathDefinition, float progress, PathSamplingParams samplingParams)
        {
            return await Task.Run(() =>
            {
                progress = Math.Clamp(progress, 0.0f, 1.0f);
                var result = new PathSampleResult { Progress = progress };

                switch (pathDefinition.PathType)
                {
                    case PathType.Linear:
                        result = SampleLinearPath(pathDefinition, progress, samplingParams);
                        break;
                    case PathType.Bezier:
                        result = SampleBezierPath(pathDefinition, progress, samplingParams);
                        break;
                    case PathType.CatmullRom:
                        result = SampleCatmullRomPath(pathDefinition, progress, samplingParams);
                        break;
                    default:
                        result = SampleLinearPath(pathDefinition, progress, samplingParams);
                        break;
                }

                return result;
            });
        }

        public async Task<float> CalculatePathLengthAsync(PathDefinition pathDefinition)
        {
            return await Task.Run(() =>
            {
                var length = 0.0f;
                for (int i = 1; i < pathDefinition.Points.Count; i++)
                {
                    length += Vector3.Distance(pathDefinition.Points[i - 1], pathDefinition.Points[i]);
                }
                return length;
            });
        }

        public async Task<float> GetProgressForDistanceAsync(PathDefinition pathDefinition, float distance)
        {
            return await Task.Run(() =>
            {
                var totalLength = pathDefinition.TotalLength;
                if (totalLength <= 0) return 0.0f;

                return Math.Clamp(distance / totalLength, 0.0f, 1.0f);
            });
        }

        public async Task<List<Vector3>> GenerateBezierPointsAsync(List<BezierControlPoint> controlPoints, int resolution, bool isClosed)
        {
            return await Task.Run(() =>
            {
                var points = new List<Vector3>();
                var segmentCount = isClosed ? controlPoints.Count : controlPoints.Count - 1;

                for (int i = 0; i < segmentCount; i++)
                {
                    var p0 = controlPoints[i].Position;
                    var p1 = controlPoints[i].ControlPointOut;
                    var p2 = controlPoints[(i + 1) % controlPoints.Count].ControlPointIn;
                    var p3 = controlPoints[(i + 1) % controlPoints.Count].Position;

                    for (int j = 0; j <= resolution; j++)
                    {
                        var t = (float)j / resolution;
                        var point = CalculateBezierPoint(p0, p1, p2, p3, t);
                        points.Add(point);
                    }
                }

                return points;
            });
        }

        public float ApplyEasing(float progress, EasingFunction easingFunction)
        {
            switch (easingFunction)
            {
                case EasingFunction.Linear:
                    return progress;
                case EasingFunction.EaseIn:
                    return progress * progress;
                case EasingFunction.EaseOut:
                    return 1.0f - (1.0f - progress) * (1.0f - progress);
                case EasingFunction.EaseInOut:
                    return progress < 0.5f;
                        ? 2.0f * progress * progress;
                        : 1.0f - (2.0f * (1.0f - progress) * (1.0f - progress));
                case EasingFunction.Bounce:
                    return BounceEase(progress);
                case EasingFunction.Elastic:
                    return ElasticEase(progress);
                case EasingFunction.Back:
                    return BackEase(progress);
                default:
                    return progress;
            }
        }

        private PathSampleResult SampleLinearPath(PathDefinition pathDefinition, float progress, PathSamplingParams samplingParams)
        {
            var points = pathDefinition.Points;
            if (points.Count < 2) return new PathSampleResult();

            var totalSegments = points.Count - 1;
            var segmentProgress = progress * totalSegments;
            var segmentIndex = (int)Math.Floor(segmentProgress);
            var t = segmentProgress - segmentIndex;

            segmentIndex = Math.Clamp(segmentIndex, 0, totalSegments - 1);

            var p0 = points[segmentIndex];
            var p1 = points[segmentIndex + 1];

            var result = new PathSampleResult;
            {
                Position = Vector3.Lerp(p0, p1, t),
                Tangent = Vector3.Normalize(p1 - p0)
            };

            if (samplingParams.CalculateRotation)
            {
                result.Rotation = CalculateRotation(result.Tangent, samplingParams.UpVector, samplingParams.RotationMode);
            }

            CalculateFrenetFrame(result, samplingParams.UpVector);

            return result;
        }

        private PathSampleResult SampleBezierPath(PathDefinition pathDefinition, float progress, PathSamplingParams samplingParams)
        {
            var controlPoints = pathDefinition.ControlPoints;
            if (controlPoints.Count < 2) return new PathSampleResult();

            var totalSegments = pathDefinition.IsClosed ? controlPoints.Count : controlPoints.Count - 1;
            var segmentProgress = progress * totalSegments;
            var segmentIndex = (int)Math.Floor(segmentProgress);
            var t = segmentProgress - segmentIndex;

            segmentIndex = segmentIndex % controlPoints.Count;

            var p0 = controlPoints[segmentIndex].Position;
            var p1 = controlPoints[segmentIndex].ControlPointOut;
            var p2 = controlPoints[(segmentIndex + 1) % controlPoints.Count].ControlPointIn;
            var p3 = controlPoints[(segmentIndex + 1) % controlPoints.Count].Position;

            var result = new PathSampleResult;
            {
                Position = CalculateBezierPoint(p0, p1, p2, p3, t),
                Tangent = CalculateBezierTangent(p0, p1, p2, p3, t)
            };

            if (samplingParams.CalculateRotation)
            {
                result.Rotation = CalculateRotation(result.Tangent, samplingParams.UpVector, samplingParams.RotationMode);
            }

            CalculateFrenetFrame(result, samplingParams.UpVector);

            return result;
        }

        private PathSampleResult SampleCatmullRomPath(PathDefinition pathDefinition, float progress, PathSamplingParams samplingParams)
        {
            var points = pathDefinition.Points;
            if (points.Count < 4) return new PathSampleResult();

            var totalSegments = points.Count - (pathDefinition.IsClosed ? 0 : 3);
            var segmentProgress = progress * totalSegments;
            var segmentIndex = (int)Math.Floor(segmentProgress);
            var t = segmentProgress - segmentIndex;

            segmentIndex = pathDefinition.IsClosed;
                ? segmentIndex % points.Count;
                : Math.Clamp(segmentIndex, 0, points.Count - 4);

            var p0 = points[segmentIndex];
            var p1 = points[(segmentIndex + 1) % points.Count];
            var p2 = points[(segmentIndex + 2) % points.Count];
            var p3 = points[(segmentIndex + 3) % points.Count];

            var result = new PathSampleResult;
            {
                Position = CalculateCatmullRomPoint(p0, p1, p2, p3, t),
                Tangent = CalculateCatmullRomTangent(p0, p1, p2, p3, t)
            };

            if (samplingParams.CalculateRotation)
            {
                result.Rotation = CalculateRotation(result.Tangent, samplingParams.UpVector, samplingParams.RotationMode);
            }

            CalculateFrenetFrame(result, samplingParams.UpVector);

            return result;
        }

        private Vector3 CalculateBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            var u = 1.0f - t;
            var u2 = u * u;
            var u3 = u2 * u;
            var t2 = t * t;
            var t3 = t2 * t;

            return u3 * p0 +
                   3.0f * u2 * t * p1 +
                   3.0f * u * t2 * p2 +
                   t3 * p3;
        }

        private Vector3 CalculateBezierTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            var u = 1.0f - t;
            var u2 = u * u;
            var t2 = t * t;

            var tangent = 3.0f * u2 * (p1 - p0) +
                          6.0f * u * t * (p2 - p1) +
                          3.0f * t2 * (p3 - p2);

            return Vector3.Normalize(tangent);
        }

        private Vector3 CalculateCatmullRomPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            var t2 = t * t;
            var t3 = t2 * t;

            return 0.5f * ((2.0f * p1) +
                           (-p0 + p2) * t +
                           (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2 +
                           (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3);
        }

        private Vector3 CalculateCatmullRomTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            var t2 = t * t;

            var tangent = 0.5f * ((-p0 + p2) +
                                  2.0f * (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t +
                                  3.0f * (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t2);

            return Vector3.Normalize(tangent);
        }

        private Quaternion CalculateRotation(Vector3 tangent, Vector3 upVector, RotationMode rotationMode)
        {
            if (rotationMode == RotationMode.None)
                return Quaternion.Identity;

            if (rotationMode == RotationMode.Tangent || rotationMode == RotationMode.LookAt)
            {
                if (tangent.LengthSquared() < 0.0001f)
                    return Quaternion.Identity;

                var forward = Vector3.Normalize(tangent);
                var right = Vector3.Normalize(Vector3.Cross(upVector, forward));
                var up = Vector3.Cross(forward, right);

                return Quaternion.CreateFromRotationMatrix(
                    new Matrix4x4(
                        right.X, right.Y, right.Z, 0,
                        up.X, up.Y, up.Z, 0,
                        forward.X, forward.Y, forward.Z, 0,
                        0, 0, 0, 1;
                    )
                );
            }

            return Quaternion.Identity;
        }

        private void CalculateFrenetFrame(PathSampleResult result, Vector3 upVector)
        {
            if (result.Tangent.LengthSquared() < 0.0001f)
            {
                result.Normal = upVector;
                result.Binormal = Vector3.UnitX;
                return;
            }

            var tangent = Vector3.Normalize(result.Tangent);
            var binormal = Vector3.Normalize(Vector3.Cross(upVector, tangent));
            var normal = Vector3.Cross(tangent, binormal);

            result.Normal = normal;
            result.Binormal = binormal;
        }

        private float BounceEase(float t)
        {
            if (t < 1.0f / 2.75f)
            {
                return 7.5625f * t * t;
            }
            else if (t < 2.0f / 2.75f)
            {
                t -= 1.5f / 2.75f;
                return 7.5625f * t * t + 0.75f;
            }
            else if (t < 2.5f / 2.75f)
            {
                t -= 2.25f / 2.75f;
                return 7.5625f * t * t + 0.9375f;
            }
            else;
            {
                t -= 2.625f / 2.75f;
                return 7.5625f * t * t + 0.984375f;
            }
        }

        private float ElasticEase(float t)
        {
            var t2 = t * t;
            var t3 = t2 * t;
            return t3 * t2 * MathF.Sin(t * MathF.PI * 4.5f);
        }

        private float BackEase(float t)
        {
            var s = 1.70158f;
            return t * t * ((s + 1.0f) * t - s);
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }

    internal class PathSmoother : IDisposable
    {
        private readonly ILogger _logger;
        private PathAnimatorSettings _settings;

        public PathSmoother(ILogger logger)
        {
            _logger = logger;
        }

        public void Initialize(PathAnimatorSettings settings)
        {
            _settings = settings;
        }

        public async Task<PathDefinition> SmoothPathAsync(PathDefinition pathDefinition, float smoothingFactor)
        {
            return await Task.Run(() =>
            {
                if (pathDefinition.Points.Count < 3)
                    return pathDefinition;

                var smoothedPoints = new List<Vector3>();
                var points = pathDefinition.Points;

                // Add first point;
                smoothedPoints.Add(points[0]);

                for (int i = 1; i < points.Count - 1; i++)
                {
                    var prev = points[i - 1];
                    var current = points[i];
                    var next = points[i + 1];

                    // Calculate smoothed position;
                    var smoothed = (prev + current + next) / 3.0f;
                    smoothedPoints.Add(Vector3.Lerp(current, smoothed, smoothingFactor));
                }

                // Add last point;
                smoothedPoints.Add(points[points.Count - 1]);

                var smoothedDefinition = new PathDefinition;
                {
                    PathType = pathDefinition.PathType,
                    Points = smoothedPoints,
                    IsClosed = pathDefinition.IsClosed,
                    Resolution = pathDefinition.Resolution;
                };

                return smoothedDefinition;
            });
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }

    internal class PathOptimizer : IDisposable
    {
        private readonly ILogger _logger;
        private PathAnimatorSettings _settings;

        public PathOptimizer(ILogger logger)
        {
            _logger = logger;
        }

        public void Initialize(PathAnimatorSettings settings)
        {
            _settings = settings;
        }

        public async Task<PathDefinition> OptimizePathAsync(PathDefinition pathDefinition)
        {
            return await Task.Run(() =>
            {
                if (pathDefinition.Points.Count <= 2)
                    return pathDefinition;

                var optimizedPoints = new List<Vector3>();
                var points = pathDefinition.Points;

                // Always include first point;
                optimizedPoints.Add(points[0]);

                for (int i = 1; i < points.Count - 1; i++)
                {
                    var prev = optimizedPoints[optimizedPoints.Count - 1];
                    var current = points[i];
                    var next = points[i + 1];

                    // Check if current point can be removed (if it's colinear within tolerance)
                    if (!IsPointRedundant(prev, current, next, _settings.OptimizationTolerance))
                    {
                        optimizedPoints.Add(current);
                    }
                }

                // Always include last point;
                optimizedPoints.Add(points[points.Count - 1]);

                var optimizedDefinition = new PathDefinition;
                {
                    PathType = pathDefinition.PathType,
                    Points = optimizedPoints,
                    IsClosed = pathDefinition.IsClosed,
                    Resolution = pathDefinition.Resolution;
                };

                return optimizedDefinition;
            });
        }

        private bool IsPointRedundant(Vector3 a, Vector3 b, Vector3 c, float tolerance)
        {
            var ab = Vector3.Normalize(b - a);
            var bc = Vector3.Normalize(c - b);

            // Check if points are colinear;
            var dot = Vector3.Dot(ab, bc);
            return Math.Abs(dot - 1.0f) < tolerance;
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }
    #endregion;
}
