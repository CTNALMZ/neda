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
    /// Advanced camera controller for managing complex camera behaviors, transitions, and cinematic sequences.
    /// Supports multiple camera modes, smooth transitions, and real-time camera manipulation.
    /// </summary>
    public class CameraController : ICameraController, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly ISettingsManager _settingsManager;
        private readonly IRecoveryEngine _recoveryEngine;

        private readonly Dictionary<string, CameraInstance> _cameras;
        private readonly Dictionary<string, CameraShot> _cameraShots;
        private readonly List<CameraTransition> _activeTransitions;
        private readonly Queue<CameraCommand> _commandQueue;
        private readonly object _cameraLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        private CameraControllerSettings _settings;
        private CameraMetrics _metrics;
        private CameraState _currentState;
        private string _activeCameraId;
        private CameraShakeHandler _shakeHandler;
        private CameraNoiseHandler _noiseHandler;
        private CameraFramingHandler _framingHandler;
        private CameraOrbitalHandler _orbitalHandler;
        #endregion;

        #region Public Properties;
        /// <summary>
        /// Gets the number of registered cameras;
        /// </summary>
        public int CameraCount;
        {
            get;
            {
                lock (_cameraLock)
                {
                    return _cameras.Count;
                }
            }
        }

        /// <summary>
        /// Gets the active camera instance;
        /// </summary>
        public CameraInstance ActiveCamera;
        {
            get;
            {
                lock (_cameraLock)
                {
                    return _activeCameraId != null && _cameras.ContainsKey(_activeCameraId)
                        ? _cameras[_activeCameraId]
                        : null;
                }
            }
        }

        /// <summary>
        /// Gets the current camera state;
        /// </summary>
        public CameraState CurrentState => _currentState;

        /// <summary>
        /// Gets the system performance metrics;
        /// </summary>
        public CameraMetrics Metrics => _metrics;

        /// <summary>
        /// Gets the current system settings;
        /// </summary>
        public CameraControllerSettings Settings => _settings;

        /// <summary>
        /// Gets whether the system is initialized and ready;
        /// </summary>
        public bool IsInitialized => _isInitialized;
        #endregion;

        #region Events;
        /// <summary>
        /// Raised when a camera is registered;
        /// </summary>
        public event EventHandler<CameraRegisteredEventArgs> CameraRegistered;

        /// <summary>
        /// Raised when a camera is unregistered;
        /// </summary>
        public event EventHandler<CameraUnregisteredEventArgs> CameraUnregistered;

        /// <summary>
        /// Raised when the active camera changes;
        /// </summary>
        public event EventHandler<ActiveCameraChangedEventArgs> ActiveCameraChanged;

        /// <summary>
        /// Raised when a camera transition starts;
        /// </summary>
        public event EventHandler<CameraTransitionStartedEventArgs> CameraTransitionStarted;

        /// <summary>
        /// Raised when a camera transition completes;
        /// </summary>
        public event EventHandler<CameraTransitionCompletedEventArgs> CameraTransitionCompleted;

        /// <summary>
        /// Raised when camera shake is applied;
        /// </summary>
        public event EventHandler<CameraShakeAppliedEventArgs> CameraShakeApplied;

        /// <summary>
        /// Raised when camera metrics are updated;
        /// </summary>
        public event EventHandler<CameraMetricsUpdatedEventArgs> MetricsUpdated;
        #endregion;

        #region Constructor;
        /// <summary>
        /// Initializes a new instance of the CameraController;
        /// </summary>
        public CameraController(
            ILogger logger,
            ISettingsManager settingsManager,
            IRecoveryEngine recoveryEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _recoveryEngine = recoveryEngine ?? throw new ArgumentNullException(nameof(recoveryEngine));

            _cameras = new Dictionary<string, CameraInstance>();
            _cameraShots = new Dictionary<string, CameraShot>();
            _activeTransitions = new List<CameraTransition>();
            _commandQueue = new Queue<CameraCommand>();
            _metrics = new CameraMetrics();
            _currentState = CameraState.Idle;
            _shakeHandler = new CameraShakeHandler(logger);
            _noiseHandler = new CameraNoiseHandler(logger);
            _framingHandler = new CameraFramingHandler(logger);
            _orbitalHandler = new CameraOrbitalHandler(logger);

            _logger.LogInformation("CameraController instance created");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Initializes the camera controller system;
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("CameraController is already initialized");
                    return;
                }

                await LoadConfigurationAsync();
                await CreateDefaultCameraAsync();
                await InitializeSubsystemsAsync();

                _isInitialized = true;
                _logger.LogInformation("CameraController initialized successfully");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync("Failed to initialize CameraController", ex);
                throw new CameraControllerException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Updates the camera controller and all active cameras;
        /// </summary>
        public async Task UpdateAsync(float deltaTime)
        {
            try
            {
                if (!_isInitialized) return;

                var updateTimer = System.Diagnostics.Stopwatch.StartNew();
                _metrics.LastUpdateTime = deltaTime;

                await ProcessCommandQueueAsync();
                await UpdateActiveCameraAsync(deltaTime);
                await UpdateCameraTransitionsAsync(deltaTime);
                await UpdateCameraEffectsAsync(deltaTime);
                await UpdateMetricsAsync();

                updateTimer.Stop();
                _metrics.LastUpdateDuration = (float)updateTimer.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync("Error during camera update", ex);
                await _recoveryEngine.ExecuteRecoveryStrategyAsync("CameraUpdate");
            }
        }

        /// <summary>
        /// Registers a camera with the controller;
        /// </summary>
        public async Task<string> RegisterCameraAsync(CameraConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            try
            {
                await ValidateSystemState();
                await ValidateCameraConfig(config);

                var cameraId = GenerateCameraId();
                var camera = new CameraInstance(cameraId, config);

                lock (_cameraLock)
                {
                    _cameras[cameraId] = camera;
                }

                _metrics.CamerasRegistered++;
                RaiseCameraRegistered(cameraId, config);

                _logger.LogDebug($"Camera registered: {cameraId} ({config.CameraType})");

                return cameraId;
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync("Failed to register camera", ex);
                throw new CameraControllerException("Camera registration failed", ex);
            }
        }

        /// <summary>
        /// Unregisters a camera from the controller;
        /// </summary>
        public async Task<bool> UnregisterCameraAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                CameraInstance camera;
                lock (_cameraLock)
                {
                    if (!_cameras.TryGetValue(cameraId, out camera))
                    {
                        _logger.LogWarning($"Camera not found: {cameraId}");
                        return false;
                    }

                    _cameras.Remove(cameraId);
                }

                // If this was the active camera, clear active camera;
                if (_activeCameraId == cameraId)
                {
                    _activeCameraId = null;
                    _currentState = CameraState.Idle;
                }

                _metrics.CamerasUnregistered++;
                RaiseCameraUnregistered(cameraId);

                _logger.LogDebug($"Camera unregistered: {cameraId}");

                return true;
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to unregister camera: {cameraId}", ex);
                throw new CameraControllerException("Camera unregistration failed", ex);
            }
        }

        /// <summary>
        /// Sets the active camera;
        /// </summary>
        public async Task<bool> SetActiveCameraAsync(string cameraId, CameraTransitionParams transitionParams = null)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                var previousCameraId = _activeCameraId;

                if (_activeCameraId == cameraId)
                {
                    _logger.LogDebug($"Camera {cameraId} is already active");
                    return true;
                }

                // Handle transition if specified;
                if (transitionParams != null && previousCameraId != null)
                {
                    await TransitionToCameraAsync(previousCameraId, cameraId, transitionParams);
                }
                else;
                {
                    // Immediate switch;
                    lock (_cameraLock)
                    {
                        _activeCameraId = cameraId;
                    }

                    _currentState = CameraState.Active;
                    _metrics.ActiveCameraChanges++;

                    RaiseActiveCameraChanged(previousCameraId, cameraId);
                    _logger.LogDebug($"Active camera changed: {previousCameraId} -> {cameraId}");
                }

                return true;
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to set active camera: {cameraId}", ex);
                throw new CameraControllerException("Active camera setting failed", ex);
            }
        }

        /// <summary>
        /// Transitions between two cameras with specified parameters;
        /// </summary>
        public async Task<string> TransitionToCameraAsync(string fromCameraId, string toCameraId, CameraTransitionParams transitionParams)
        {
            if (string.IsNullOrEmpty(fromCameraId))
                throw new ArgumentException("From camera ID cannot be null or empty", nameof(fromCameraId));

            if (string.IsNullOrEmpty(toCameraId))
                throw new ArgumentException("To camera ID cannot be null or empty", nameof(toCameraId));

            if (transitionParams == null)
                throw new ArgumentNullException(nameof(transitionParams));

            try
            {
                var fromCamera = await GetCameraAsync(fromCameraId);
                var toCamera = await GetCameraAsync(toCameraId);

                var transitionId = GenerateTransitionId();
                var transition = new CameraTransition(transitionId, fromCamera, toCamera, transitionParams);

                lock (_cameraLock)
                {
                    _activeTransitions.Add(transition);
                }

                _currentState = CameraState.Transitioning;
                _metrics.TransitionsStarted++;

                RaiseCameraTransitionStarted(transitionId, fromCameraId, toCameraId, transitionParams);
                _logger.LogDebug($"Camera transition started: {transitionId} ({fromCameraId} -> {toCameraId})");

                return transitionId;
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to transition from {fromCameraId} to {toCameraId}", ex);
                throw new CameraControllerException("Camera transition failed", ex);
            }
        }

        /// <summary>
        /// Sets the camera position and rotation;
        /// </summary>
        public async Task SetCameraTransformAsync(string cameraId, Vector3 position, Quaternion rotation, bool immediate = true)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);

                if (immediate)
                {
                    camera.Position = position;
                    camera.Rotation = rotation;
                    camera.TargetPosition = position;
                    camera.TargetRotation = rotation;
                }
                else;
                {
                    camera.TargetPosition = position;
                    camera.TargetRotation = rotation;
                }

                _metrics.TransformsSet++;
                _logger.LogDebug($"Camera transform set: {cameraId}");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to set camera transform: {cameraId}", ex);
                throw new CameraControllerException("Camera transform setting failed", ex);
            }
        }

        /// <summary>
        /// Sets the camera field of view;
        /// </summary>
        public async Task SetFieldOfViewAsync(string cameraId, float fov, float transitionTime = 0.0f)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);

                if (transitionTime > 0)
                {
                    camera.TargetFieldOfView = fov;
                    camera.FieldOfViewTransitionTime = transitionTime;
                    camera.FieldOfViewTransitionProgress = 0.0f;
                }
                else;
                {
                    camera.FieldOfView = fov;
                    camera.TargetFieldOfView = fov;
                }

                _metrics.FieldOfViewChanges++;
                _logger.LogDebug($"Camera FOV set: {cameraId} -> {fov} degrees");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to set camera FOV: {cameraId}", ex);
                throw new CameraControllerException("Camera FOV setting failed", ex);
            }
        }

        /// <summary>
        /// Applies shake effect to the camera;
        /// </summary>
        public async Task ApplyShakeAsync(string cameraId, CameraShakeParams shakeParams)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (shakeParams == null)
                throw new ArgumentNullException(nameof(shakeParams));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                await _shakeHandler.ApplyShakeAsync(camera, shakeParams);

                _metrics.ShakesApplied++;
                RaiseCameraShakeApplied(cameraId, shakeParams);

                _logger.LogDebug($"Camera shake applied: {cameraId} (Magnitude: {shakeParams.Magnitude})");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to apply camera shake: {cameraId}", ex);
                throw new CameraControllerException("Camera shake application failed", ex);
            }
        }

        /// <summary>
        /// Applies noise effect to the camera;
        /// </summary>
        public async Task ApplyNoiseAsync(string cameraId, CameraNoiseParams noiseParams)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (noiseParams == null)
                throw new ArgumentNullException(nameof(noiseParams));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                await _noiseHandler.ApplyNoiseAsync(camera, noiseParams);

                _metrics.NoiseApplied++;
                _logger.LogDebug($"Camera noise applied: {cameraId}");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to apply camera noise: {cameraId}", ex);
                throw new CameraControllerException("Camera noise application failed", ex);
            }
        }

        /// <summary>
        /// Starts orbital movement around a target;
        /// </summary>
        public async Task StartOrbitalMovementAsync(string cameraId, OrbitalMovementParams orbitalParams)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (orbitalParams == null)
                throw new ArgumentNullException(nameof(orbitalParams));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                await _orbitalHandler.StartOrbitalMovementAsync(camera, orbitalParams);

                _metrics.OrbitalMovementsStarted++;
                _logger.LogDebug($"Orbital movement started: {cameraId}");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to start orbital movement: {cameraId}", ex);
                throw new CameraControllerException("Orbital movement start failed", ex);
            }
        }

        /// <summary>
        /// Stops orbital movement;
        /// </summary>
        public async Task StopOrbitalMovementAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                await _orbitalHandler.StopOrbitalMovementAsync(camera);

                _metrics.OrbitalMovementsStopped++;
                _logger.LogDebug($"Orbital movement stopped: {cameraId}");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to stop orbital movement: {cameraId}", ex);
                throw new CameraControllerException("Orbital movement stop failed", ex);
            }
        }

        /// <summary>
        /// Sets camera framing for a target;
        /// </summary>
        public async Task SetFramingAsync(string cameraId, FramingParams framingParams)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (framingParams == null)
                throw new ArgumentNullException(nameof(framingParams));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                await _framingHandler.SetFramingAsync(camera, framingParams);

                _metrics.FramingSets++;
                _logger.LogDebug($"Camera framing set: {cameraId}");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to set camera framing: {cameraId}", ex);
                throw new CameraControllerException("Camera framing setting failed", ex);
            }
        }

        /// <summary>
        /// Creates a camera shot for cinematic sequences;
        /// </summary>
        public async Task<string> CreateCameraShotAsync(CameraShotConfig shotConfig)
        {
            if (shotConfig == null)
                throw new ArgumentNullException(nameof(shotConfig));

            try
            {
                await ValidateSystemState();
                await ValidateCameraShotConfig(shotConfig);

                var shotId = GenerateShotId();
                var shot = new CameraShot(shotId, shotConfig);

                lock (_cameraLock)
                {
                    _cameraShots[shotId] = shot;
                }

                _metrics.ShotsCreated++;
                _logger.LogDebug($"Camera shot created: {shotId}");

                return shotId;
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync("Failed to create camera shot", ex);
                throw new CameraControllerException("Camera shot creation failed", ex);
            }
        }

        /// <summary>
        /// Plays a camera shot;
        /// </summary>
        public async Task PlayCameraShotAsync(string shotId, string targetCameraId = null)
        {
            if (string.IsNullOrEmpty(shotId))
                throw new ArgumentException("Shot ID cannot be null or empty", nameof(shotId));

            try
            {
                var shot = await GetCameraShotAsync(shotId);
                var cameraId = targetCameraId ?? _activeCameraId;

                if (string.IsNullOrEmpty(cameraId))
                {
                    throw new CameraControllerException("No active camera available for shot playback");
                }

                var camera = await GetCameraAsync(cameraId);

                // Apply shot configuration to camera;
                await ApplyCameraShotAsync(camera, shot);
                _metrics.ShotsPlayed++;

                _logger.LogDebug($"Camera shot played: {shotId} on camera {cameraId}");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to play camera shot: {shotId}", ex);
                throw new CameraControllerException("Camera shot playback failed", ex);
            }
        }

        /// <summary>
        /// Queues a camera command for execution;
        /// </summary>
        public async Task QueueCameraCommandAsync(CameraCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            try
            {
                lock (_cameraLock)
                {
                    _commandQueue.Enqueue(command);
                }

                _metrics.CommandsQueued++;
                _logger.LogDebug($"Camera command queued: {command.CommandType}");
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync("Failed to queue camera command", ex);
                throw new CameraControllerException("Camera command queuing failed", ex);
            }
        }

        /// <summary>
        /// Gets the current camera view matrix;
        /// </summary>
        public async Task<Matrix4x4> GetViewMatrixAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                return CalculateViewMatrix(camera);
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to get view matrix for camera: {cameraId}", ex);
                throw new CameraControllerException("View matrix retrieval failed", ex);
            }
        }

        /// <summary>
        /// Gets the current camera projection matrix;
        /// </summary>
        public async Task<Matrix4x4> GetProjectionMatrixAsync(string cameraId, float aspectRatio)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                return CalculateProjectionMatrix(camera, aspectRatio);
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to get projection matrix for camera: {cameraId}", ex);
                throw new CameraControllerException("Projection matrix retrieval failed", ex);
            }
        }

        /// <summary>
        /// Gets the camera's forward vector;
        /// </summary>
        public async Task<Vector3> GetForwardVectorAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                return Vector3.Transform(Vector3.UnitZ, camera.Rotation);
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to get forward vector for camera: {cameraId}", ex);
                throw new CameraControllerException("Forward vector retrieval failed", ex);
            }
        }

        /// <summary>
        /// Gets the camera's right vector;
        /// </summary>
        public async Task<Vector3> GetRightVectorAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                return Vector3.Transform(Vector3.UnitX, camera.Rotation);
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to get right vector for camera: {cameraId}", ex);
                throw new CameraControllerException("Right vector retrieval failed", ex);
            }
        }

        /// <summary>
        /// Gets the camera's up vector;
        /// </summary>
        public async Task<Vector3> GetUpVectorAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                return Vector3.Transform(Vector3.UnitY, camera.Rotation);
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to get up vector for camera: {cameraId}", ex);
                throw new CameraControllerException("Up vector retrieval failed", ex);
            }
        }

        /// <summary>
        /// Performs a screen point to ray conversion;
        /// </summary>
        public async Task<Ray> ScreenPointToRayAsync(string cameraId, Vector2 screenPoint, float aspectRatio)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            try
            {
                var camera = await GetCameraAsync(cameraId);
                var viewMatrix = CalculateViewMatrix(camera);
                var projectionMatrix = CalculateProjectionMatrix(camera, aspectRatio);

                return CalculateScreenPointToRay(screenPoint, viewMatrix, projectionMatrix, aspectRatio);
            }
            catch (Exception ex)
            {
                await HandleCameraExceptionAsync($"Failed to convert screen point to ray for camera: {cameraId}", ex);
                throw new CameraControllerException("Screen point to ray conversion failed", ex);
            }
        }
        #endregion;

        #region Private Methods;
        private async Task LoadConfigurationAsync()
        {
            try
            {
                _settings = _settingsManager.GetSection<CameraControllerSettings>("CameraController") ?? new CameraControllerSettings();
                _logger.LogInformation("Camera controller configuration loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load camera controller configuration: {ex.Message}");
                _settings = new CameraControllerSettings();
            }

            await Task.CompletedTask;
        }

        private async Task CreateDefaultCameraAsync()
        {
            try
            {
                var defaultConfig = new CameraConfig;
                {
                    CameraType = CameraType.Free,
                    Position = Vector3.Zero,
                    Rotation = Quaternion.Identity,
                    FieldOfView = 60.0f,
                    NearClipPlane = 0.1f,
                    FarClipPlane = 1000.0f;
                };

                var defaultCameraId = await RegisterCameraAsync(defaultConfig);
                await SetActiveCameraAsync(defaultCameraId);

                _logger.LogDebug("Default camera created and set as active");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to create default camera: {ex.Message}");
            }
        }

        private async Task InitializeSubsystemsAsync()
        {
            _shakeHandler.Initialize(_settings);
            _noiseHandler.Initialize(_settings);
            _framingHandler.Initialize(_settings);
            _orbitalHandler.Initialize(_settings);

            _logger.LogDebug("Camera controller subsystems initialized");
            await Task.CompletedTask;
        }

        private async Task ProcessCommandQueueAsync()
        {
            List<CameraCommand> commandsToProcess;
            lock (_cameraLock)
            {
                commandsToProcess = new List<CameraCommand>();
                while (_commandQueue.Count > 0 && commandsToProcess.Count < _settings.MaxCommandsPerFrame)
                {
                    commandsToProcess.Add(_commandQueue.Dequeue());
                }
            }

            foreach (var command in commandsToProcess)
            {
                try
                {
                    await ExecuteCameraCommandAsync(command);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error executing camera command: {ex.Message}");
                }
            }

            _metrics.CommandsProcessed += commandsToProcess.Count;
        }

        private async Task ExecuteCameraCommandAsync(CameraCommand command)
        {
            switch (command.CommandType)
            {
                case CameraCommandType.SetTransform:
                    await ExecuteSetTransformCommandAsync(command);
                    break;
                case CameraCommandType.SetFieldOfView:
                    await ExecuteSetFieldOfViewCommandAsync(command);
                    break;
                case CameraCommandType.ApplyShake:
                    await ExecuteApplyShakeCommandAsync(command);
                    break;
                case CameraCommandType.StartTransition:
                    await ExecuteStartTransitionCommandAsync(command);
                    break;
                case CameraCommandType.StopTransition:
                    await ExecuteStopTransitionCommandAsync(command);
                    break;
                case CameraCommandType.SetFraming:
                    await ExecuteSetFramingCommandAsync(command);
                    break;
                case CameraCommandType.StartOrbital:
                    await ExecuteStartOrbitalCommandAsync(command);
                    break;
                case CameraCommandType.StopOrbital:
                    await ExecuteStopOrbitalCommandAsync(command);
                    break;
                default:
                    _logger.LogWarning($"Unknown camera command type: {command.CommandType}");
                    break;
            }
        }

        private async Task UpdateActiveCameraAsync(float deltaTime)
        {
            var activeCamera = ActiveCamera;
            if (activeCamera == null) return;

            await UpdateCameraAsync(activeCamera, deltaTime);
        }

        private async Task UpdateCameraAsync(CameraInstance camera, float deltaTime)
        {
            // Update camera movement towards target;
            await UpdateCameraMovementAsync(camera, deltaTime);

            // Update field of view transition;
            await UpdateFieldOfViewTransitionAsync(camera, deltaTime);

            // Update camera effects;
            await UpdateCameraEffectsForCameraAsync(camera, deltaTime);

            // Update camera-specific behaviors;
            await UpdateCameraBehaviorsAsync(camera, deltaTime);
        }

        private async Task UpdateCameraMovementAsync(CameraInstance camera, float deltaTime)
        {
            if (camera.Position != camera.TargetPosition || camera.Rotation != camera.TargetRotation)
            {
                var positionSpeed = camera.MovementSpeed * deltaTime;
                var rotationSpeed = camera.RotationSpeed * deltaTime;

                // Interpolate position;
                camera.Position = Vector3.Lerp(camera.Position, camera.TargetPosition, positionSpeed);

                // Interpolate rotation;
                camera.Rotation = Quaternion.Slerp(camera.Rotation, camera.TargetRotation, rotationSpeed);

                _metrics.MovementUpdates++;
            }
        }

        private async Task UpdateFieldOfViewTransitionAsync(CameraInstance camera, float deltaTime)
        {
            if (camera.FieldOfView != camera.TargetFieldOfView && camera.FieldOfViewTransitionTime > 0)
            {
                camera.FieldOfViewTransitionProgress += deltaTime / camera.FieldOfViewTransitionTime;
                camera.FieldOfViewTransitionProgress = Math.Min(camera.FieldOfViewTransitionProgress, 1.0f);

                camera.FieldOfView = MathHelper.Lerp(
                    camera.FieldOfView,
                    camera.TargetFieldOfView,
                    camera.FieldOfViewTransitionProgress;
                );

                if (camera.FieldOfViewTransitionProgress >= 1.0f)
                {
                    camera.FieldOfView = camera.TargetFieldOfView;
                    camera.FieldOfViewTransitionTime = 0.0f;
                    camera.FieldOfViewTransitionProgress = 0.0f;
                }

                _metrics.FieldOfViewUpdates++;
            }
        }

        private async Task UpdateCameraTransitionsAsync(float deltaTime)
        {
            List<CameraTransition> transitionsToRemove = new List<CameraTransition>();

            lock (_cameraLock)
            {
                foreach (var transition in _activeTransitions)
                {
                    try
                    {
                        var shouldContinue = await UpdateCameraTransitionAsync(transition, deltaTime);
                        if (!shouldContinue)
                        {
                            transitionsToRemove.Add(transition);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error updating camera transition {transition.TransitionId}: {ex.Message}");
                        transitionsToRemove.Add(transition);
                    }
                }

                // Remove completed transitions;
                foreach (var transition in transitionsToRemove)
                {
                    _activeTransitions.Remove(transition);
                }
            }

            // Update state if no transitions are active;
            if (_activeTransitions.Count == 0 && _currentState == CameraState.Transitioning)
            {
                _currentState = CameraState.Active;
            }
        }

        private async Task<bool> UpdateCameraTransitionAsync(CameraTransition transition, float deltaTime)
        {
            transition.ElapsedTime += deltaTime;
            var progress = transition.ElapsedTime / transition.TransitionParams.Duration;

            if (progress >= 1.0f)
            {
                // Transition complete;
                transition.ToCamera.Position = transition.ToCamera.TargetPosition;
                transition.ToCamera.Rotation = transition.ToCamera.TargetRotation;
                transition.ToCamera.FieldOfView = transition.ToCamera.TargetFieldOfView;

                // Set active camera to target;
                lock (_cameraLock)
                {
                    _activeCameraId = transition.ToCamera.Id;
                }

                _metrics.TransitionsCompleted++;
                RaiseCameraTransitionCompleted(transition.TransitionId, transition.FromCamera.Id, transition.ToCamera.Id);

                _logger.LogDebug($"Camera transition completed: {transition.TransitionId}");
                return false;
            }

            // Calculate interpolation factor based on curve;
            var t = CalculateTransitionProgress(progress, transition.TransitionParams.Curve);

            // Interpolate camera properties;
            await InterpolateCameraTransitionAsync(transition, t);

            return true;
        }

        private async Task InterpolateCameraTransitionAsync(CameraTransition transition, float progress)
        {
            // Interpolate position;
            transition.ToCamera.Position = Vector3.Lerp(
                transition.FromCamera.Position,
                transition.ToCamera.TargetPosition,
                progress;
            );

            // Interpolate rotation;
            transition.ToCamera.Rotation = Quaternion.Slerp(
                transition.FromCamera.Rotation,
                transition.ToCamera.TargetRotation,
                progress;
            );

            // Interpolate field of view;
            transition.ToCamera.FieldOfView = MathHelper.Lerp(
                transition.FromCamera.FieldOfView,
                transition.ToCamera.TargetFieldOfView,
                progress;
            );

            await Task.CompletedTask;
        }

        private async Task UpdateCameraEffectsAsync(float deltaTime)
        {
            await _shakeHandler.UpdateAsync(deltaTime);
            await _noiseHandler.UpdateAsync(deltaTime);
            await _framingHandler.UpdateAsync(deltaTime);
            await _orbitalHandler.UpdateAsync(deltaTime);
        }

        private async Task UpdateCameraEffectsForCameraAsync(CameraInstance camera, float deltaTime)
        {
            // Update shake effect;
            if (camera.ShakeEffect != null)
            {
                await _shakeHandler.UpdateCameraShakeAsync(camera, deltaTime);
            }

            // Update noise effect;
            if (camera.NoiseEffect != null)
            {
                await _noiseHandler.UpdateCameraNoiseAsync(camera, deltaTime);
            }

            // Update orbital movement;
            if (camera.OrbitalMovement != null)
            {
                await _orbitalHandler.UpdateOrbitalMovementAsync(camera, deltaTime);
            }

            // Update framing;
            if (camera.Framing != null)
            {
                await _framingHandler.UpdateFramingAsync(camera, deltaTime);
            }
        }

        private async Task UpdateCameraBehaviorsAsync(CameraInstance camera, float deltaTime)
        {
            // Update camera-specific behaviors based on camera type;
            switch (camera.Config.CameraType)
            {
                case CameraType.Free:
                    await UpdateFreeCameraBehaviorAsync(camera, deltaTime);
                    break;
                case CameraType.Orbital:
                    await UpdateOrbitalCameraBehaviorAsync(camera, deltaTime);
                    break;
                case CameraType.Follow:
                    await UpdateFollowCameraBehaviorAsync(camera, deltaTime);
                    break;
                case CameraType.FirstPerson:
                    await UpdateFirstPersonCameraBehaviorAsync(camera, deltaTime);
                    break;
                case CameraType.Cinematic:
                    await UpdateCinematicCameraBehaviorAsync(camera, deltaTime);
                    break;
            }
        }

        private async Task UpdateFreeCameraBehaviorAsync(CameraInstance camera, float deltaTime)
        {
            // Free camera behavior - typically controlled by input;
            await Task.CompletedTask;
        }

        private async Task UpdateOrbitalCameraBehaviorAsync(CameraInstance camera, float deltaTime)
        {
            // Orbital camera behavior - orbits around a target;
            if (camera.OrbitalTarget != null)
            {
                await _orbitalHandler.UpdateOrbitalCameraAsync(camera, deltaTime);
            }
        }

        private async Task UpdateFollowCameraBehaviorAsync(CameraInstance camera, float deltaTime)
        {
            // Follow camera behavior - follows a target with offset;
            if (camera.FollowTarget != null)
            {
                var targetPosition = camera.FollowTarget.Position + camera.FollowOffset;
                camera.TargetPosition = Vector3.Lerp(camera.TargetPosition, targetPosition, deltaTime * camera.FollowSmoothness);

                // Look at target;
                var direction = Vector3.Normalize(camera.FollowTarget.Position - camera.TargetPosition);
                var targetRotation = Quaternion.CreateFromYawPitchRoll(
                    MathF.Atan2(direction.X, direction.Z),
                    MathF.Asin(-direction.Y),
                    0.0f;
                );
                camera.TargetRotation = Quaternion.Slerp(camera.TargetRotation, targetRotation, deltaTime * camera.RotationSpeed);
            }

            await Task.CompletedTask;
        }

        private async Task UpdateFirstPersonCameraBehaviorAsync(CameraInstance camera, float deltaTime)
        {
            // First person camera behavior - attached to a character;
            if (camera.AttachedTo != null)
            {
                camera.Position = camera.AttachedTo.Position + camera.FirstPersonOffset;
                camera.Rotation = camera.AttachedTo.Rotation;
            }

            await Task.CompletedTask;
        }

        private async Task UpdateCinematicCameraBehaviorAsync(CameraInstance camera, float deltaTime)
        {
            // Cinematic camera behavior - follows predefined paths or animations;
            if (camera.CinematicPath != null)
            {
                await UpdateCinematicPathAsync(camera, deltaTime);
            }

            await Task.CompletedTask;
        }

        private async Task UpdateCinematicPathAsync(CameraInstance camera, float deltaTime)
        {
            // Update camera position and rotation along cinematic path;
            camera.CinematicProgress += deltaTime * camera.CinematicSpeed;

            if (camera.CinematicProgress >= 1.0f)
            {
                camera.CinematicProgress = 0.0f;
                // Handle path completion;
            }

            // Sample path at current progress;
            var pathPoint = camera.CinematicPath.Sample(camera.CinematicProgress);
            camera.TargetPosition = pathPoint.Position;
            camera.TargetRotation = pathPoint.Rotation;

            await Task.CompletedTask;
        }

        private async Task UpdateMetricsAsync()
        {
            _metrics.ActiveCameras = _cameras.Count;
            _metrics.ActiveTransitions = _activeTransitions.Count;
            _metrics.ActiveShots = _cameraShots.Count;

            RaiseMetricsUpdated();
            await Task.CompletedTask;
        }

        private async Task ApplyCameraShotAsync(CameraInstance camera, CameraShot shot)
        {
            // Apply shot configuration to camera;
            camera.TargetPosition = shot.Config.Position;
            camera.TargetRotation = shot.Config.Rotation;
            camera.TargetFieldOfView = shot.Config.FieldOfView;

            // Apply shot effects;
            if (shot.Config.ShakeParams != null)
            {
                await ApplyShakeAsync(camera.Id, shot.Config.ShakeParams);
            }

            if (shot.Config.NoiseParams != null)
            {
                await ApplyNoiseAsync(camera.Id, shot.Config.NoiseParams);
            }

            await Task.CompletedTask;
        }

        private Matrix4x4 CalculateViewMatrix(CameraInstance camera)
        {
            var forward = Vector3.Transform(Vector3.UnitZ, camera.Rotation);
            var up = Vector3.Transform(Vector3.UnitY, camera.Rotation);

            return Matrix4x4.CreateLookAt(camera.Position, camera.Position + forward, up);
        }

        private Matrix4x4 CalculateProjectionMatrix(CameraInstance camera, float aspectRatio)
        {
            return Matrix4x4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(camera.FieldOfView),
                aspectRatio,
                camera.Config.NearClipPlane,
                camera.Config.FarClipPlane;
            );
        }

        private Ray CalculateScreenPointToRay(Vector2 screenPoint, Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix, float aspectRatio)
        {
            // Convert screen coordinates to normalized device coordinates;
            var ndcX = (2.0f * screenPoint.X) - 1.0f;
            var ndcY = 1.0f - (2.0f * screenPoint.Y);

            // Create ray in view space;
            var rayOrigin = Vector3.Transform(Vector3.Zero, Matrix4x4.Invert(viewMatrix));
            var rayDirection = CalculateRayDirection(ndcX, ndcY, viewMatrix, projectionMatrix, aspectRatio);

            return new Ray(rayOrigin, rayDirection);
        }

        private Vector3 CalculateRayDirection(float ndcX, float ndcY, Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix, float aspectRatio)
        {
            var clipCoords = new Vector4(ndcX, ndcY, -1.0f, 1.0f);

            // Transform to view space;
            var viewCoords = Vector4.Transform(clipCoords, Matrix4x4.Invert(projectionMatrix));
            viewCoords.Z = -1.0f;
            viewCoords.W = 0.0f;

            // Transform to world space;
            var worldCoords = Vector4.Transform(viewCoords, Matrix4x4.Invert(viewMatrix));

            return Vector3.Normalize(new Vector3(worldCoords.X, worldCoords.Y, worldCoords.Z));
        }

        private float CalculateTransitionProgress(float progress, AnimationCurve curve)
        {
            switch (curve)
            {
                case AnimationCurve.Linear:
                    return progress;
                case AnimationCurve.EaseIn:
                    return progress * progress;
                case AnimationCurve.EaseOut:
                    return 1.0f - (1.0f - progress) * (1.0f - progress);
                case AnimationCurve.EaseInOut:
                    return progress < 0.5f;
                        ? 2.0f * progress * progress;
                        : 1.0f - (2.0f * (1.0f - progress) * (1.0f - progress));
                case AnimationCurve.Exponential:
                    return (float)Math.Pow(progress, 2.0);
                default:
                    return progress;
            }
        }

        private async Task<CameraInstance> GetCameraAsync(string cameraId)
        {
            lock (_cameraLock)
            {
                if (_cameras.TryGetValue(cameraId, out var camera))
                {
                    return camera;
                }
            }

            throw new CameraControllerException($"Camera not found: {cameraId}");
        }

        private async Task<CameraShot> GetCameraShotAsync(string shotId)
        {
            lock (_cameraLock)
            {
                if (_cameraShots.TryGetValue(shotId, out var shot))
                {
                    return shot;
                }
            }

            throw new CameraControllerException($"Camera shot not found: {shotId}");
        }

        private async Task ValidateSystemState()
        {
            if (!_isInitialized)
                throw new CameraControllerException("CameraController is not initialized");

            await Task.CompletedTask;
        }

        private async Task ValidateCameraConfig(CameraConfig config)
        {
            if (config.FieldOfView <= 0 || config.FieldOfView > 180)
                throw new CameraControllerException("Field of view must be between 0 and 180 degrees");

            if (config.NearClipPlane <= 0)
                throw new CameraControllerException("Near clip plane must be greater than zero");

            if (config.FarClipPlane <= config.NearClipPlane)
                throw new CameraControllerException("Far clip plane must be greater than near clip plane");

            await Task.CompletedTask;
        }

        private async Task ValidateCameraShotConfig(CameraShotConfig config)
        {
            if (config.FieldOfView <= 0 || config.FieldOfView > 180)
                throw new CameraControllerException("Shot field of view must be between 0 and 180 degrees");

            await Task.CompletedTask;
        }

        private string GenerateCameraId()
        {
            return $"Camera_{Guid.NewGuid():N}";
        }

        private string GenerateTransitionId()
        {
            return $"Transition_{Guid.NewGuid():N}";
        }

        private string GenerateShotId()
        {
            return $"Shot_{Guid.NewGuid():N}";
        }

        private async Task HandleCameraExceptionAsync(string context, Exception exception)
        {
            _logger.LogError($"{context}: {exception.Message}", exception);
            await _recoveryEngine.ExecuteRecoveryStrategyAsync("CameraController", exception);
        }

        // Command execution methods;
        private async Task ExecuteSetTransformCommandAsync(CameraCommand command)
        {
            var cameraId = command.Parameters["CameraId"] as string;
            var position = (Vector3)command.Parameters["Position"];
            var rotation = (Quaternion)command.Parameters["Rotation"];
            var immediate = (bool)command.Parameters["Immediate"];

            await SetCameraTransformAsync(cameraId, position, rotation, immediate);
        }

        private async Task ExecuteSetFieldOfViewCommandAsync(CameraCommand command)
        {
            var cameraId = command.Parameters["CameraId"] as string;
            var fov = (float)command.Parameters["FieldOfView"];
            var transitionTime = (float)command.Parameters["TransitionTime"];

            await SetFieldOfViewAsync(cameraId, fov, transitionTime);
        }

        private async Task ExecuteApplyShakeCommandAsync(CameraCommand command)
        {
            var cameraId = command.Parameters["CameraId"] as string;
            var shakeParams = (CameraShakeParams)command.Parameters["ShakeParams"];

            await ApplyShakeAsync(cameraId, shakeParams);
        }

        private async Task ExecuteStartTransitionCommandAsync(CameraCommand command)
        {
            var fromCameraId = command.Parameters["FromCameraId"] as string;
            var toCameraId = command.Parameters["ToCameraId"] as string;
            var transitionParams = (CameraTransitionParams)command.Parameters["TransitionParams"];

            await TransitionToCameraAsync(fromCameraId, toCameraId, transitionParams);
        }

        private async Task ExecuteSetFramingCommandAsync(CameraCommand command)
        {
            var cameraId = command.Parameters["CameraId"] as string;
            var framingParams = (FramingParams)command.Parameters["FramingParams"];

            await SetFramingAsync(cameraId, framingParams);
        }

        private async Task ExecuteStartOrbitalCommandAsync(CameraCommand command)
        {
            var cameraId = command.Parameters["CameraId"] as string;
            var orbitalParams = (OrbitalMovementParams)command.Parameters["OrbitalParams"];

            await StartOrbitalMovementAsync(cameraId, orbitalParams);
        }

        private async Task ExecuteStopOrbitalCommandAsync(CameraCommand command)
        {
            var cameraId = command.Parameters["CameraId"] as string;
            await StopOrbitalMovementAsync(cameraId);
        }

        private void RaiseCameraRegistered(string cameraId, CameraConfig config)
        {
            CameraRegistered?.Invoke(this, new CameraRegisteredEventArgs;
            {
                CameraId = cameraId,
                Config = config,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCameraUnregistered(string cameraId)
        {
            CameraUnregistered?.Invoke(this, new CameraUnregisteredEventArgs;
            {
                CameraId = cameraId,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseActiveCameraChanged(string previousCameraId, string newCameraId)
        {
            ActiveCameraChanged?.Invoke(this, new ActiveCameraChangedEventArgs;
            {
                PreviousCameraId = previousCameraId,
                NewCameraId = newCameraId,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCameraTransitionStarted(string transitionId, string fromCameraId, string toCameraId, CameraTransitionParams transitionParams)
        {
            CameraTransitionStarted?.Invoke(this, new CameraTransitionStartedEventArgs;
            {
                TransitionId = transitionId,
                FromCameraId = fromCameraId,
                ToCameraId = toCameraId,
                TransitionParams = transitionParams,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCameraTransitionCompleted(string transitionId, string fromCameraId, string toCameraId)
        {
            CameraTransitionCompleted?.Invoke(this, new CameraTransitionCompletedEventArgs;
            {
                TransitionId = transitionId,
                FromCameraId = fromCameraId,
                ToCameraId = toCameraId,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCameraShakeApplied(string cameraId, CameraShakeParams shakeParams)
        {
            CameraShakeApplied?.Invoke(this, new CameraShakeAppliedEventArgs;
            {
                CameraId = cameraId,
                ShakeParams = shakeParams,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseMetricsUpdated()
        {
            MetricsUpdated?.Invoke(this, new CameraMetricsUpdatedEventArgs;
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
                    // Clean up cameras;
                    var cameraIds = _cameras.Keys.ToList();
                    foreach (var cameraId in cameraIds)
                    {
                        try
                        {
                            UnregisterCameraAsync(cameraId).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error unregistering camera during disposal: {ex.Message}");
                        }
                    }

                    _cameras.Clear();
                    _cameraShots.Clear();
                    _activeTransitions.Clear();
                    _commandQueue.Clear();

                    _shakeHandler?.Dispose();
                    _noiseHandler?.Dispose();
                    _framingHandler?.Dispose();
                    _orbitalHandler?.Dispose();
                }

                _isDisposed = true;
            }
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    public enum CameraType;
    {
        Free,
        Orbital,
        Follow,
        FirstPerson,
        Cinematic,
        Static;
    }

    public enum CameraState;
    {
        Idle,
        Active,
        Transitioning,
        Shaking,
        Orbiting;
    }

    public enum AnimationCurve;
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
        Exponential;
    }

    public enum CameraCommandType;
    {
        SetTransform,
        SetFieldOfView,
        ApplyShake,
        StartTransition,
        StopTransition,
        SetFraming,
        StartOrbital,
        StopOrbital;
    }

    public class CameraControllerSettings;
    {
        public float DefaultMovementSpeed { get; set; } = 5.0f;
        public float DefaultRotationSpeed { get; set; } = 2.0f;
        public float DefaultFieldOfView { get; set; } = 60.0f;
        public float DefaultTransitionDuration { get; set; } = 1.0f;
        public int MaxCommandsPerFrame { get; set; } = 10;
        public int MaxActiveTransitions { get; set; } = 5;
        public bool EnableCameraShake { get; set; } = true;
        public bool EnableCameraNoise { get; set; } = true;
    }

    public class CameraMetrics;
    {
        public int CamerasRegistered { get; set; }
        public int CamerasUnregistered { get; set; }
        public int ActiveCameras { get; set; }
        public int ActiveCameraChanges { get; set; }
        public int TransitionsStarted { get; set; }
        public int TransitionsCompleted { get; set; }
        public int ActiveTransitions { get; set; }
        public int TransformsSet { get; set; }
        public int FieldOfViewChanges { get; set; }
        public int FieldOfViewUpdates { get; set; }
        public int ShakesApplied { get; set; }
        public int NoiseApplied { get; set; }
        public int OrbitalMovementsStarted { get; set; }
        public int OrbitalMovementsStopped { get; set; }
        public int FramingSets { get; set; }
        public int ShotsCreated { get; set; }
        public int ShotsPlayed { get; set; }
        public int ActiveShots { get; set; }
        public int CommandsQueued { get; set; }
        public int CommandsProcessed { get; set; }
        public int MovementUpdates { get; set; }
        public float LastUpdateTime { get; set; }
        public float LastUpdateDuration { get; set; }
    }

    public class CameraInstance;
    {
        public string Id { get; }
        public CameraConfig Config { get; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 TargetPosition { get; set; }
        public Quaternion TargetRotation { get; set; }
        public float FieldOfView { get; set; }
        public float TargetFieldOfView { get; set; }
        public float FieldOfViewTransitionTime { get; set; }
        public float FieldOfViewTransitionProgress { get; set; }
        public float MovementSpeed { get; set; } = 5.0f;
        public float RotationSpeed { get; set; } = 2.0f;

        // Camera effects;
        public CameraShakeEffect ShakeEffect { get; set; }
        public CameraNoiseEffect NoiseEffect { get; set; }
        public CameraFraming Framing { get; set; }
        public CameraOrbitalMovement OrbitalMovement { get; set; }

        // Camera targets;
        public CameraTarget FollowTarget { get; set; }
        public Vector3 FollowOffset { get; set; }
        public float FollowSmoothness { get; set; } = 2.0f;
        public CameraTarget OrbitalTarget { get; set; }
        public CameraTarget AttachedTo { get; set; }
        public Vector3 FirstPersonOffset { get; set; }

        // Cinematic;
        public CameraPath CinematicPath { get; set; }
        public float CinematicProgress { get; set; }
        public float CinematicSpeed { get; set; } = 1.0f;

        public CameraInstance(string id, CameraConfig config)
        {
            Id = id;
            Config = config;
            Position = config.Position;
            Rotation = config.Rotation;
            TargetPosition = config.Position;
            TargetRotation = config.Rotation;
            FieldOfView = config.FieldOfView;
            TargetFieldOfView = config.FieldOfView;
        }
    }

    public class CameraConfig;
    {
        public CameraType CameraType { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public float FieldOfView { get; set; } = 60.0f;
        public float NearClipPlane { get; set; } = 0.1f;
        public float FarClipPlane { get; set; } = 1000.0f;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class CameraTransition;
    {
        public string TransitionId { get; }
        public CameraInstance FromCamera { get; }
        public CameraInstance ToCamera { get; }
        public CameraTransitionParams TransitionParams { get; }
        public float ElapsedTime { get; set; }

        public CameraTransition(string transitionId, CameraInstance fromCamera, CameraInstance toCamera, CameraTransitionParams transitionParams)
        {
            TransitionId = transitionId;
            FromCamera = fromCamera;
            ToCamera = toCamera;
            TransitionParams = transitionParams;
        }
    }

    public class CameraTransitionParams;
    {
        public float Duration { get; set; } = 1.0f;
        public AnimationCurve Curve { get; set; } = AnimationCurve.EaseInOut;
        public bool InterpolateFieldOfView { get; set; } = true;
        public Dictionary<string, object> AdditionalParams { get; set; } = new Dictionary<string, object>();
    }

    public class CameraShakeParams;
    {
        public float Magnitude { get; set; } = 1.0f;
        public float Frequency { get; set; } = 10.0f;
        public float Duration { get; set; } = 1.0f;
        public AnimationCurve Attenuation { get; set; } = AnimationCurve.EaseOut;
        public Vector3 Direction { get; set; } = Vector3.One;
        public bool IsPerlinNoise { get; set; } = true;
    }

    public class CameraNoiseParams;
    {
        public float Amplitude { get; set; } = 0.1f;
        public float Frequency { get; set; } = 1.0f;
        public NoiseType NoiseType { get; set; } = NoiseType.Perlin;
        public Dictionary<string, object> AdditionalParams { get; set; } = new Dictionary<string, object>();
    }

    public class OrbitalMovementParams;
    {
        public CameraTarget Target { get; set; }
        public float Distance { get; set; } = 5.0f;
        public float Height { get; set; } = 2.0f;
        public float Speed { get; set; } = 1.0f;
        public bool AutoRotate { get; set; } = true;
        public Vector2 RotationLimits { get; set; } = new Vector2(-89, 89);
    }

    public class FramingParams;
    {
        public CameraTarget Target { get; set; }
        public FramingType FramingType { get; set; }
        public float Padding { get; set; } = 1.0f;
        public bool MaintainAspectRatio { get; set; } = true;
        public Dictionary<string, object> AdditionalParams { get; set; } = new Dictionary<string, object>();
    }

    public class CameraShot;
    {
        public string Id { get; }
        public CameraShotConfig Config { get; }

        public CameraShot(string id, CameraShotConfig config)
        {
            Id = id;
            Config = config;
        }
    }

    public class CameraShotConfig;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public float FieldOfView { get; set; } = 60.0f;
        public CameraShakeParams ShakeParams { get; set; }
        public CameraNoiseParams NoiseParams { get; set; }
        public float Duration { get; set; } = 5.0f;
        public Dictionary<string, object> AdditionalConfig { get; set; } = new Dictionary<string, object>();
    }

    public class CameraCommand;
    {
        public CameraCommandType CommandType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public DateTime QueueTime { get; set; } = DateTime.UtcNow;
    }

    // Event args classes;
    public class CameraRegisteredEventArgs : EventArgs;
    {
        public string CameraId { get; set; }
        public CameraConfig Config { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CameraUnregisteredEventArgs : EventArgs;
    {
        public string CameraId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActiveCameraChangedEventArgs : EventArgs;
    {
        public string PreviousCameraId { get; set; }
        public string NewCameraId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CameraTransitionStartedEventArgs : EventArgs;
    {
        public string TransitionId { get; set; }
        public string FromCameraId { get; set; }
        public string ToCameraId { get; set; }
        public CameraTransitionParams TransitionParams { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CameraTransitionCompletedEventArgs : EventArgs;
    {
        public string TransitionId { get; set; }
        public string FromCameraId { get; set; }
        public string ToCameraId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CameraShakeAppliedEventArgs : EventArgs;
    {
        public string CameraId { get; set; }
        public CameraShakeParams ShakeParams { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CameraMetricsUpdatedEventArgs : EventArgs;
    {
        public CameraMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CameraControllerException : Exception
    {
        public CameraControllerException(string message) : base(message) { }
        public CameraControllerException(string message, Exception innerException) : base(message, innerException) { }
    }

    // Supporting classes for camera effects and behaviors;
    public class CameraShakeEffect;
    {
        public CameraShakeParams Params { get; set; }
        public float ElapsedTime { get; set; }
        public Vector3 CurrentOffset { get; set; }
    }

    public class CameraNoiseEffect;
    {
        public CameraNoiseParams Params { get; set; }
        public Vector3 CurrentOffset { get; set; }
    }

    public class CameraFraming;
    {
        public FramingParams Params { get; set; }
        public Vector3 TargetPosition { get; set; }
    }

    public class CameraOrbitalMovement;
    {
        public OrbitalMovementParams Params { get; set; }
        public float CurrentAngle { get; set; }
        public float CurrentHeight { get; set; }
    }

    public class CameraTarget;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public string TargetId { get; set; }
    }

    public class CameraPath;
    {
        public List<CameraPathPoint> Points { get; set; } = new List<CameraPathPoint>();
        public bool IsLooping { get; set; }

        public CameraPathPoint Sample(float progress)
        {
            if (Points.Count == 0)
                return new CameraPathPoint();

            var index = (int)(progress * (Points.Count - 1));
            return Points[Math.Clamp(index, 0, Points.Count - 1)];
        }
    }

    public class CameraPathPoint;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public float FieldOfView { get; set; } = 60.0f;
    }

    public enum NoiseType;
    {
        Perlin,
        Simplex,
        Value,
        White;
    }

    public enum FramingType;
    {
        CloseUp,
        MediumShot,
        FullShot,
        WideShot,
        Custom;
    }

    // Internal subsystem implementations;
    internal class CameraShakeHandler : IDisposable
    {
        private readonly ILogger _logger;
        private CameraControllerSettings _settings;
        private readonly Random _random;

        public CameraShakeHandler(ILogger logger)
        {
            _logger = logger;
            _random = new Random();
        }

        public void Initialize(CameraControllerSettings settings)
        {
            _settings = settings;
        }

        public async Task ApplyShakeAsync(CameraInstance camera, CameraShakeParams shakeParams)
        {
            camera.ShakeEffect = new CameraShakeEffect;
            {
                Params = shakeParams,
                ElapsedTime = 0.0f;
            };

            await Task.CompletedTask;
        }

        public async Task UpdateAsync(float deltaTime)
        {
            // Global shake updates if needed;
            await Task.CompletedTask;
        }

        public async Task UpdateCameraShakeAsync(CameraInstance camera, float deltaTime)
        {
            if (camera.ShakeEffect == null) return;

            var shake = camera.ShakeEffect;
            shake.ElapsedTime += deltaTime;

            if (shake.ElapsedTime >= shake.Params.Duration)
            {
                camera.ShakeEffect = null;
                return;
            }

            // Calculate attenuation;
            var progress = shake.ElapsedTime / shake.Params.Duration;
            var attenuation = CalculateAttenuation(progress, shake.Params.Attenuation);
            var magnitude = shake.Params.Magnitude * attenuation;

            // Generate shake offset;
            var offset = GenerateShakeOffset(magnitude, shake.Params.Frequency, shake.Params.IsPerlinNoise);
            camera.ShakeEffect.CurrentOffset = offset;

            await Task.CompletedTask;
        }

        private Vector3 GenerateShakeOffset(float magnitude, float frequency, bool usePerlinNoise)
        {
            var time = (float)DateTime.Now.TimeOfDay.TotalSeconds * frequency;

            if (usePerlinNoise)
            {
                return new Vector3(
                    PerlinNoise(time, 0) * magnitude,
                    PerlinNoise(time, 100) * magnitude,
                    PerlinNoise(time, 200) * magnitude;
                );
            }
            else;
            {
                return new Vector3(
                    (float)(_random.NextDouble() - 0.5) * 2.0f * magnitude,
                    (float)(_random.NextDouble() - 0.5) * 2.0f * magnitude,
                    (float)(_random.NextDouble() - 0.5) * 2.0f * magnitude;
                );
            }
        }

        private float PerlinNoise(float x, float y)
        {
            // Simplified Perlin noise implementation;
            return (float)(Math.Sin(x + y) * 0.5 + 0.5);
        }

        private float CalculateAttenuation(float progress, AnimationCurve curve)
        {
            return 1.0f - progress; // Simplified attenuation;
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }

    internal class CameraNoiseHandler : IDisposable
    {
        private readonly ILogger _logger;
        private CameraControllerSettings _settings;

        public CameraNoiseHandler(ILogger logger)
        {
            _logger = logger;
        }

        public void Initialize(CameraControllerSettings settings)
        {
            _settings = settings;
        }

        public async Task ApplyNoiseAsync(CameraInstance camera, CameraNoiseParams noiseParams)
        {
            camera.NoiseEffect = new CameraNoiseEffect;
            {
                Params = noiseParams;
            };

            await Task.CompletedTask;
        }

        public async Task UpdateAsync(float deltaTime)
        {
            await Task.CompletedTask;
        }

        public async Task UpdateCameraNoiseAsync(CameraInstance camera, float deltaTime)
        {
            if (camera.NoiseEffect == null) return;

            // Update noise effect;
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }

    internal class CameraFramingHandler : IDisposable
    {
        private readonly ILogger _logger;
        private CameraControllerSettings _settings;

        public CameraFramingHandler(ILogger logger)
        {
            _logger = logger;
        }

        public void Initialize(CameraControllerSettings settings)
        {
            _settings = settings;
        }

        public async Task SetFramingAsync(CameraInstance camera, FramingParams framingParams)
        {
            camera.Framing = new CameraFraming;
            {
                Params = framingParams;
            };

            await Task.CompletedTask;
        }

        public async Task UpdateAsync(float deltaTime)
        {
            await Task.CompletedTask;
        }

        public async Task UpdateFramingAsync(CameraInstance camera, float deltaTime)
        {
            if (camera.Framing == null) return;

            // Update framing;
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }

    internal class CameraOrbitalHandler : IDisposable
    {
        private readonly ILogger _logger;
        private CameraControllerSettings _settings;

        public CameraOrbitalHandler(ILogger logger)
        {
            _logger = logger;
        }

        public void Initialize(CameraControllerSettings settings)
        {
            _settings = settings;
        }

        public async Task StartOrbitalMovementAsync(CameraInstance camera, OrbitalMovementParams orbitalParams)
        {
            camera.OrbitalMovement = new CameraOrbitalMovement;
            {
                Params = orbitalParams,
                CurrentAngle = 0.0f,
                CurrentHeight = orbitalParams.Height;
            };

            camera.OrbitalTarget = orbitalParams.Target;

            await Task.CompletedTask;
        }

        public async Task StopOrbitalMovementAsync(CameraInstance camera)
        {
            camera.OrbitalMovement = null;
            camera.OrbitalTarget = null;

            await Task.CompletedTask;
        }

        public async Task UpdateAsync(float deltaTime)
        {
            await Task.CompletedTask;
        }

        public async Task UpdateOrbitalMovementAsync(CameraInstance camera, float deltaTime)
        {
            if (camera.OrbitalMovement == null || camera.OrbitalTarget == null) return;

            var orbital = camera.OrbitalMovement;

            if (orbital.Params.AutoRotate)
            {
                orbital.CurrentAngle += orbital.Params.Speed * deltaTime;
            }

            // Calculate camera position;
            var targetPos = camera.OrbitalTarget.Position;
            var cameraPos = CalculateOrbitalPosition(targetPos, orbital.CurrentAngle, orbital.Params.Distance, orbital.CurrentHeight);

            camera.TargetPosition = cameraPos;

            // Look at target;
            var direction = Vector3.Normalize(targetPos - cameraPos);
            camera.TargetRotation = Quaternion.CreateFromYawPitchRoll(
                MathF.Atan2(direction.X, direction.Z),
                MathF.Asin(-direction.Y),
                0.0f;
            );

            await Task.CompletedTask;
        }

        public async Task UpdateOrbitalCameraAsync(CameraInstance camera, float deltaTime)
        {
            await UpdateOrbitalMovementAsync(camera, deltaTime);
        }

        private Vector3 CalculateOrbitalPosition(Vector3 target, float angle, float distance, float height)
        {
            var x = target.X + MathF.Cos(angle) * distance;
            var z = target.Z + MathF.Sin(angle) * distance;
            return new Vector3(x, target.Y + height, z);
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }

    // Math helper class;
    public static class MathHelper;
    {
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        public static float DegreesToRadians(float degrees)
        {
            return degrees * (MathF.PI / 180.0f);
        }

        public static float RadiansToDegrees(float radians)
        {
            return radians * (180.0f / MathF.PI);
        }
    }

    // Ray structure for screen-to-world conversion;
    public struct Ray;
    {
        public Vector3 Origin;
        public Vector3 Direction;

        public Ray(Vector3 origin, Vector3 direction)
        {
            Origin = origin;
            Direction = direction;
        }
    }
    #endregion;
}
