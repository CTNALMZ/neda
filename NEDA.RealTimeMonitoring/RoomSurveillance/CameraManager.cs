using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Net.Http;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;
using NEDA.SecurityModules.Monitoring;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.AI.ComputerVision;
using NEDA.AI.PatternRecognition;
using NEDA.Communication.EventBus;
using NEDA.Storage.FileService;
using NEDA.Services.Messaging;
using NEDA.RealTimeMonitoring.PresenceAnalysis;
using NEDA.RealTimeMonitoring.IntrusionDetection;

namespace NEDA.RealTimeMonitoring.RoomSurveillance;
{
    /// <summary>
    /// Camera types supported by the system;
    /// </summary>
    public enum CameraType;
    {
        IPCamera = 0,
        USBWebcam = 1,
        RTSPStream = 2,
        ONVIFCamera = 3,
        CCTVSystem = 4,
        ThermalCamera = 5,
        PanoramicCamera = 6,
        PTZCamera = 7,
        BodyWornCamera = 8,
        DroneCamera = 9,
        MobileCamera = 10;
    }

    /// <summary>
    /// Camera connection status;
    /// </summary>
    public enum CameraStatus;
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Streaming = 3,
        Recording = 4,
        Error = 5,
        Maintenance = 6,
        Offline = 7;
    }

    /// <summary>
    /// Camera resolution settings;
    /// </summary>
    public class CameraResolution;
    {
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public int FrameRate { get; set; } = 30;
        public int BitRate { get; set; } = 4000; // kbps;
        public string Codec { get; set; } = "H264";
        public int Quality { get; set; } = 85; // Percentage;

        public override string ToString() => $"{Width}x{Height}@{FrameRate}fps";
    }

    /// <summary>
    /// Camera configuration;
    /// </summary>
    public class CameraConfig;
    {
        public string CameraId { get; set; }
        public string Name { get; set; }
        public CameraType Type { get; set; }
        public string ConnectionString { get; set; }
        public string RTSPUrl { get; set; }
        public string IPAddress { get; set; }
        public int Port { get; set; } = 554;
        public string Username { get; set; }
        public string Password { get; set; }
        public CameraResolution Resolution { get; set; }
        public bool EnableRecording { get; set; } = true;
        public bool EnableMotionDetection { get; set; } = true;
        public bool EnableFaceRecognition { get; set; } = false;
        public bool EnableObjectDetection { get; set; } = false;
        public TimeSpan RecordingDuration { get; set; } = TimeSpan.FromHours(24);
        public string StoragePath { get; set; }
        public List<string> Zones { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public bool IsPrimary { get; set; } = false;
        public double PanRange { get; set; } = 360.0;
        public double TiltRange { get; set; } = 180.0;
        public double ZoomLevel { get; set; } = 1.0;

        public CameraConfig()
        {
            Resolution = new CameraResolution();
            Zones = new List<string>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Camera frame data with metadata;
    /// </summary>
    public class CameraFrame;
    {
        public string FrameId { get; set; }
        public string CameraId { get; set; }
        public byte[] ImageData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public PixelFormat Format { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public double FrameRate { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public List<DetectionResult> Detections { get; set; }
        public byte[] Thumbnail { get; set; }

        public CameraFrame()
        {
            FrameId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
            Metadata = new Dictionary<string, object>();
            Detections = new List<DetectionResult>();
        }
    }

    /// <summary>
    /// Camera detection result from AI models;
    /// </summary>
    public class DetectionResult;
    {
        public string ObjectType { get; set; }
        public double Confidence { get; set; }
        public Rectangle BoundingBox { get; set; }
        public List<Point> Contour { get; set; }
        public Dictionary<string, object> Attributes { get; set; }
        public string TrackId { get; set; }
        public DateTime DetectionTime { get; set; }

        public DetectionResult()
        {
            Attributes = new Dictionary<string, object>();
            Contour = new List<Point>();
            DetectionTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Camera health metrics;
    /// </summary>
    public class CameraHealthMetrics;
    {
        public string CameraId { get; set; }
        public DateTime Timestamp { get; set; }
        public double UptimePercentage { get; set; }
        public double AverageFrameRate { get; set; }
        public double PacketLossRate { get; set; }
        public double Latency { get; set; }
        public double BandwidthUsage { get; set; }
        public int TotalFramesProcessed { get; set; }
        public int FailedFrames { get; set; }
        public double MemoryUsage { get; set; }
        public double CPUUsage { get; set; }
        public List<string> ActiveAlerts { get; set; }
        public CameraStatus Status { get; set; }

        public CameraHealthMetrics()
        {
            ActiveAlerts = new List<string>();
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Camera event arguments;
    /// </summary>
    public class CameraEventArgs : EventArgs;
    {
        public string CameraId { get; set; }
        public CameraEventType EventType { get; set; }
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public CameraFrame Frame { get; set; }

        public CameraEventArgs()
        {
            Data = new Dictionary<string, object>();
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Camera event types;
    /// </summary>
    public enum CameraEventType;
    {
        Connected = 0,
        Disconnected = 1,
        MotionDetected = 2,
        FaceDetected = 3,
        IntrusionDetected = 4,
        RecordingStarted = 5,
        RecordingStopped = 6,
        FrameReceived = 7,
        ErrorOccurred = 8,
        MaintenanceRequired = 9,
        QualityDegraded = 10,
        BandwidthExceeded = 11,
        StorageFull = 12,
        TamperingDetected = 13,
        CameraRebooted = 14,
        SettingsChanged = 15;
    }

    /// <summary>
    /// Interface for camera device abstraction;
    /// </summary>
    public interface ICameraDevice : IDisposable
    {
        string CameraId { get; }
        CameraConfig Config { get; }
        CameraStatus Status { get; }

        Task<bool> ConnectAsync();
        Task DisconnectAsync();
        Task<CameraFrame> CaptureFrameAsync();
        Task StartStreamingAsync(CancellationToken cancellationToken);
        Task StopStreamingAsync();
        Task StartRecordingAsync(string outputPath);
        Task StopRecordingAsync();
        Task<CameraHealthMetrics> GetHealthMetricsAsync();
        Task<bool> SetPresetAsync(string presetName, double pan, double tilt, double zoom);
        Task<bool> GoToPresetAsync(string presetName);
        Task<bool> PTZControlAsync(double panDelta, double tiltDelta, double zoomDelta);
    }

    /// <summary>
    /// Advanced camera management system for surveillance and monitoring;
    /// </summary>
    public class CameraManager : ICameraManager, IHostedService, IDisposable;
    {
        private readonly ILogger<CameraManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly IVisionEngine _visionEngine;
        private readonly IEventBus _eventBus;
        private readonly IFileManager _fileManager;
        private readonly ISecurityMonitor _securityMonitor;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IActivityAnalyzer _activityAnalyzer;
        private readonly IIntrusionSensor _intrusionSensor;
        private readonly CameraManagerConfig _managerConfig;

        private readonly Dictionary<string, ICameraDevice> _cameras;
        private readonly Dictionary<string, CameraConfig> _cameraConfigs;
        private readonly Dictionary<string, Task> _streamingTasks;
        private readonly Dictionary<string, CancellationTokenSource> _cancellationTokens;
        private readonly Dictionary<string, DateTime> _cameraLastSeen;
        private readonly Dictionary<string, Queue<CameraFrame>> _frameBuffers;
        private readonly Dictionary<string, CameraHealthMetrics> _healthMetrics;

        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _connectionSemaphore;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        private bool _isInitialized;
        private bool _isDisposed;
        private Task _healthMonitorTask;
        private Task _maintenanceTask;
        private CancellationTokenSource _globalCancellationTokenSource;
        private Timer _cleanupTimer;

        private const int MAX_FRAME_BUFFER_SIZE = 1000;
        private const int MAX_CONCURRENT_CONNECTIONS = 10;

        /// <summary>
        /// Event triggered when camera status changes;
        /// </summary>
        public event EventHandler<CameraEventArgs> CameraStatusChanged;

        /// <summary>
        /// Event triggered when motion is detected;
        /// </summary>
        public event EventHandler<CameraEventArgs> MotionDetected;

        /// <summary>
        /// Event triggered when a frame is processed;
        /// </summary>
        public event EventHandler<CameraEventArgs> FrameProcessed;

        /// <summary>
        /// Event triggered when camera health metrics are updated;
        /// </summary>
        public event EventHandler<CameraEventArgs> HealthMetricsUpdated;

        /// <summary>
        /// Initialize a new instance of CameraManager;
        /// </summary>
        public CameraManager(
            ILogger<CameraManager> logger,
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            IVisionEngine visionEngine,
            IEventBus eventBus,
            IFileManager fileManager,
            ISecurityMonitor securityMonitor,
            IPerformanceMonitor performanceMonitor,
            IActivityAnalyzer activityAnalyzer,
            IIntrusionSensor intrusionSensor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _visionEngine = visionEngine ?? throw new ArgumentNullException(nameof(visionEngine));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _activityAnalyzer = activityAnalyzer ?? throw new ArgumentNullException(nameof(activityAnalyzer));
            _intrusionSensor = intrusionSensor ?? throw new ArgumentNullException(nameof(intrusionSensor));

            _managerConfig = new CameraManagerConfig();
            _cameras = new Dictionary<string, ICameraDevice>();
            _cameraConfigs = new Dictionary<string, CameraConfig>();
            _streamingTasks = new Dictionary<string, Task>();
            _cancellationTokens = new Dictionary<string, CancellationTokenSource>();
            _cameraLastSeen = new Dictionary<string, DateTime>();
            _frameBuffers = new Dictionary<string, Queue<CameraFrame>>();
            _healthMetrics = new Dictionary<string, CameraHealthMetrics>();

            _connectionSemaphore = new SemaphoreSlim(MAX_CONCURRENT_CONNECTIONS);
            _httpClient = new HttpClient();
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true;
            };

            _globalCancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("CameraManager initialized with advanced surveillance capabilities");
        }

        /// <summary>
        /// Initialize camera manager with configuration;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("CameraManager is already initialized");
                return;
            }

            try
            {
                _logger.LogInformation("Initializing CameraManager...");

                // Load configuration;
                await LoadConfigurationAsync();

                // Initialize cameras;
                await InitializeCamerasAsync();

                // Start health monitoring;
                _healthMonitorTask = Task.Run(() => HealthMonitoringLoop(_globalCancellationTokenSource.Token));

                // Start maintenance task;
                _maintenanceTask = Task.Run(() => MaintenanceLoop(_globalCancellationTokenSource.Token));

                // Start cleanup timer;
                _cleanupTimer = new Timer(CleanupOldData, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

                _isInitialized = true;

                _logger.LogInformation("CameraManager initialized successfully with {Count} cameras", _cameras.Count);

                // Subscribe to events;
                await SubscribeToEventsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize CameraManager");
                throw new CameraManagerException("Failed to initialize CameraManager", ex);
            }
        }

        /// <summary>
        /// Add a new camera to the system;
        /// </summary>
        public async Task<CameraConfig> AddCameraAsync(CameraConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (string.IsNullOrEmpty(config.CameraId))
                config.CameraId = Guid.NewGuid().ToString();

            try
            {
                _logger.LogInformation("Adding camera {CameraId} - {Name}", config.CameraId, config.Name);

                lock (_syncLock)
                {
                    if (_cameraConfigs.ContainsKey(config.CameraId))
                        throw new CameraConfigurationException($"Camera with ID {config.CameraId} already exists");

                    _cameraConfigs[config.CameraId] = config;
                }

                // Create camera device;
                var cameraDevice = CreateCameraDevice(config);
                if (cameraDevice == null)
                    throw new CameraDeviceException($"Failed to create camera device for {config.CameraId}");

                lock (_syncLock)
                {
                    _cameras[config.CameraId] = cameraDevice;
                    _frameBuffers[config.CameraId] = new Queue<CameraFrame>();
                    _cameraLastSeen[config.CameraId] = DateTime.UtcNow;
                }

                // Initialize camera;
                if (_managerConfig.AutoConnect)
                {
                    await ConnectCameraAsync(config.CameraId);
                }

                // Save configuration;
                await SaveCameraConfigurationAsync(config);

                // Publish camera added event;
                await PublishCameraEventAsync(config.CameraId, CameraEventType.SettingsChanged,
                    $"Camera {config.Name} added to system");

                _logger.LogInformation("Camera {CameraId} added successfully", config.CameraId);

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add camera {CameraId}", config.CameraId);
                throw new CameraManagerException($"Failed to add camera {config.CameraId}", ex);
            }
        }

        /// <summary>
        /// Remove a camera from the system;
        /// </summary>
        public async Task<bool> RemoveCameraAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            try
            {
                _logger.LogInformation("Removing camera {CameraId}", cameraId);

                // Stop camera if running;
                if (IsCameraRunning(cameraId))
                {
                    await StopCameraAsync(cameraId);
                }

                // Dispose camera device;
                lock (_syncLock)
                {
                    if (_cameras.ContainsKey(cameraId))
                    {
                        _cameras[cameraId].Dispose();
                        _cameras.Remove(cameraId);
                    }

                    _cameraConfigs.Remove(cameraId);
                    _frameBuffers.Remove(cameraId);
                    _cameraLastSeen.Remove(cameraId);
                    _healthMetrics.Remove(cameraId);

                    if (_cancellationTokens.ContainsKey(cameraId))
                    {
                        _cancellationTokens[cameraId].Dispose();
                        _cancellationTokens.Remove(cameraId);
                    }
                }

                // Remove configuration file;
                var configPath = GetCameraConfigPath(cameraId);
                if (await _fileManager.FileExistsAsync(configPath))
                {
                    await _fileManager.DeleteFileAsync(configPath);
                }

                // Publish camera removed event;
                await PublishCameraEventAsync(cameraId, CameraEventType.SettingsChanged,
                    $"Camera removed from system");

                _logger.LogInformation("Camera {CameraId} removed successfully", cameraId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to remove camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Connect to a specific camera;
        /// </summary>
        public async Task<bool> ConnectCameraAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                await _connectionSemaphore.WaitAsync();

                _logger.LogInformation("Connecting to camera {CameraId}", cameraId);

                var camera = _cameras[cameraId];

                if (camera.Status == CameraStatus.Connected ||
                    camera.Status == CameraStatus.Streaming ||
                    camera.Status == CameraStatus.Recording)
                {
                    _logger.LogWarning("Camera {CameraId} is already in {Status} state", cameraId, camera.Status);
                    return true;
                }

                // Update status;
                OnCameraStatusChanged(cameraId, CameraEventType.Connected, "Connecting to camera");

                var connected = await camera.ConnectAsync();

                if (connected)
                {
                    _logger.LogInformation("Camera {CameraId} connected successfully", cameraId);

                    // Update last seen;
                    lock (_syncLock)
                    {
                        _cameraLastSeen[cameraId] = DateTime.UtcNow;
                    }

                    // Start streaming if configured;
                    if (_cameraConfigs[cameraId].EnableMotionDetection || _managerConfig.AutoStartStreaming)
                    {
                        await StartCameraStreamingAsync(cameraId);
                    }

                    // Start recording if enabled;
                    if (_cameraConfigs[cameraId].EnableRecording)
                    {
                        await StartCameraRecordingAsync(cameraId);
                    }

                    OnCameraStatusChanged(cameraId, CameraEventType.Connected, "Camera connected and operational");

                    return true;
                }
                else;
                {
                    _logger.LogError("Failed to connect to camera {CameraId}", cameraId);
                    OnCameraStatusChanged(cameraId, CameraEventType.ErrorOccurred, "Failed to connect to camera");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to camera {CameraId}", cameraId);
                OnCameraStatusChanged(cameraId, CameraEventType.ErrorOccurred,
                    $"Connection error: {ex.Message}");
                throw new CameraConnectionException($"Failed to connect to camera {cameraId}", ex);
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        /// <summary>
        /// Disconnect from a specific camera;
        /// </summary>
        public async Task<bool> DisconnectCameraAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                _logger.LogInformation("Disconnecting camera {CameraId}", cameraId);

                var camera = _cameras[cameraId];

                // Stop streaming if active;
                if (_streamingTasks.ContainsKey(cameraId) && !_streamingTasks[cameraId].IsCompleted)
                {
                    await StopCameraStreamingAsync(cameraId);
                }

                // Stop recording if active;
                await camera.StopRecordingAsync();

                // Disconnect camera;
                await camera.DisconnectAsync();

                OnCameraStatusChanged(cameraId, CameraEventType.Disconnected, "Camera disconnected");

                _logger.LogInformation("Camera {CameraId} disconnected successfully", cameraId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to disconnect camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Start streaming from a camera;
        /// </summary>
        public async Task<bool> StartCameraStreamingAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                _logger.LogInformation("Starting streaming for camera {CameraId}", cameraId);

                var camera = _cameras[cameraId];

                if (camera.Status == CameraStatus.Streaming || camera.Status == CameraStatus.Recording)
                {
                    _logger.LogWarning("Camera {CameraId} is already streaming", cameraId);
                    return true;
                }

                // Create cancellation token for this stream;
                var cts = new CancellationTokenSource();
                lock (_syncLock)
                {
                    _cancellationTokens[cameraId] = cts;
                }

                // Start streaming task;
                var streamingTask = Task.Run(() => CameraStreamingLoop(cameraId, cts.Token), cts.Token);

                lock (_syncLock)
                {
                    _streamingTasks[cameraId] = streamingTask;
                }

                OnCameraStatusChanged(cameraId, CameraEventType.RecordingStarted, "Camera streaming started");

                _logger.LogInformation("Streaming started for camera {CameraId}", cameraId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting streaming for camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to start streaming for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Stop streaming from a camera;
        /// </summary>
        public async Task<bool> StopCameraStreamingAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            try
            {
                _logger.LogInformation("Stopping streaming for camera {CameraId}", cameraId);

                // Cancel streaming task;
                if (_cancellationTokens.ContainsKey(cameraId))
                {
                    _cancellationTokens[cameraId].Cancel();

                    // Wait for task to complete;
                    if (_streamingTasks.ContainsKey(cameraId))
                    {
                        try
                        {
                            await _streamingTasks[cameraId].WaitAsync(TimeSpan.FromSeconds(10));
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error waiting for streaming task to complete for camera {CameraId}", cameraId);
                        }
                    }

                    // Clean up;
                    lock (_syncLock)
                    {
                        _cancellationTokens.Remove(cameraId);
                        _streamingTasks.Remove(cameraId);
                    }
                }

                OnCameraStatusChanged(cameraId, CameraEventType.RecordingStopped, "Camera streaming stopped");

                _logger.LogInformation("Streaming stopped for camera {CameraId}", cameraId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping streaming for camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to stop streaming for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Start recording from a camera;
        /// </summary>
        public async Task<bool> StartCameraRecordingAsync(string cameraId, string customPath = null)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                _logger.LogInformation("Starting recording for camera {CameraId}", cameraId);

                var camera = _cameras[cameraId];
                var config = _cameraConfigs[cameraId];

                // Create recording directory;
                var recordingPath = customPath ?? GetRecordingPath(cameraId);
                await EnsureDirectoryExistsAsync(recordingPath);

                // Start recording;
                await camera.StartRecordingAsync(recordingPath);

                OnCameraStatusChanged(cameraId, CameraEventType.RecordingStarted,
                    $"Recording started at {recordingPath}");

                _logger.LogInformation("Recording started for camera {CameraId} at {Path}", cameraId, recordingPath);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting recording for camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to start recording for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Capture a single frame from a camera;
        /// </summary>
        public async Task<CameraFrame> CaptureFrameAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                var camera = _cameras[cameraId];

                if (camera.Status == CameraStatus.Disconnected || camera.Status == CameraStatus.Error)
                {
                    throw new CameraNotConnectedException($"Camera {cameraId} is not connected");
                }

                var frame = await camera.CaptureFrameAsync();
                frame.CameraId = cameraId;

                // Process frame if needed;
                if (_cameraConfigs[cameraId].EnableMotionDetection ||
                    _cameraConfigs[cameraId].EnableObjectDetection)
                {
                    await ProcessFrameAsync(frame);
                }

                // Store in buffer;
                StoreFrameInBuffer(cameraId, frame);

                // Update last seen;
                lock (_syncLock)
                {
                    _cameraLastSeen[cameraId] = DateTime.UtcNow;
                }

                // Trigger frame processed event;
                OnFrameProcessed(cameraId, frame);

                return frame;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing frame from camera {CameraId}", cameraId);
                throw new CameraCaptureException($"Failed to capture frame from camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Get camera status;
        /// </summary>
        public CameraStatus GetCameraStatus(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            lock (_syncLock)
            {
                if (!_cameras.ContainsKey(cameraId))
                    return CameraStatus.Disconnected;

                return _cameras[cameraId].Status;
            }
        }

        /// <summary>
        /// Get all camera statuses;
        /// </summary>
        public Dictionary<string, CameraStatus> GetAllCameraStatuses()
        {
            lock (_syncLock)
            {
                return _cameras.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Status;
                );
            }
        }

        /// <summary>
        /// Get camera configuration;
        /// </summary>
        public CameraConfig GetCameraConfig(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            lock (_syncLock)
            {
                if (!_cameraConfigs.ContainsKey(cameraId))
                    throw new CameraNotFoundException($"Camera {cameraId} not found");

                return _cameraConfigs[cameraId];
            }
        }

        /// <summary>
        /// Update camera configuration;
        /// </summary>
        public async Task<bool> UpdateCameraConfigAsync(string cameraId, Action<CameraConfig> updateAction)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (updateAction == null)
                throw new ArgumentNullException(nameof(updateAction));

            try
            {
                _logger.LogInformation("Updating configuration for camera {CameraId}", cameraId);

                CameraConfig config;
                lock (_syncLock)
                {
                    if (!_cameraConfigs.ContainsKey(cameraId))
                        throw new CameraNotFoundException($"Camera {cameraId} not found");

                    config = _cameraConfigs[cameraId];
                }

                // Apply updates;
                updateAction(config);

                // Save configuration;
                await SaveCameraConfigurationAsync(config);

                // Reinitialize camera if needed;
                if (_cameras.ContainsKey(cameraId))
                {
                    await DisconnectCameraAsync(cameraId);
                    _cameras[cameraId].Dispose();

                    var newCamera = CreateCameraDevice(config);
                    lock (_syncLock)
                    {
                        _cameras[cameraId] = newCamera;
                    }

                    if (_managerConfig.AutoConnect)
                    {
                        await ConnectCameraAsync(cameraId);
                    }
                }

                OnCameraStatusChanged(cameraId, CameraEventType.SettingsChanged, "Camera configuration updated");

                _logger.LogInformation("Configuration updated for camera {CameraId}", cameraId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating configuration for camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to update configuration for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Get camera health metrics;
        /// </summary>
        public async Task<CameraHealthMetrics> GetCameraHealthAsync(string cameraId)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                var camera = _cameras[cameraId];
                var metrics = await camera.GetHealthMetricsAsync();

                // Update stored metrics;
                lock (_syncLock)
                {
                    _healthMetrics[cameraId] = metrics;
                }

                // Trigger health metrics event;
                OnHealthMetricsUpdated(cameraId, metrics);

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting health metrics for camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to get health metrics for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Get all camera health metrics;
        /// </summary>
        public Dictionary<string, CameraHealthMetrics> GetAllCameraHealth()
        {
            lock (_syncLock)
            {
                return _healthMetrics.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value;
                );
            }
        }

        /// <summary>
        /// Control PTZ camera movement;
        /// </summary>
        public async Task<bool> ControlPTZAsync(string cameraId, double panDelta, double tiltDelta, double zoomDelta)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                var camera = _cameras[cameraId];

                if (camera.Status == CameraStatus.Disconnected || camera.Status == CameraStatus.Error)
                {
                    throw new CameraNotConnectedException($"Camera {cameraId} is not connected");
                }

                var result = await camera.PTZControlAsync(panDelta, tiltDelta, zoomDelta);

                if (result)
                {
                    _logger.LogDebug("PTZ control executed for camera {CameraId}: Pan={Pan}, Tilt={Tilt}, Zoom={Zoom}",
                        cameraId, panDelta, tiltDelta, zoomDelta);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error controlling PTZ for camera {CameraId}", cameraId);
                throw new CameraControlException($"Failed to control PTZ for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Set PTZ preset position;
        /// </summary>
        public async Task<bool> SetPTZPresetAsync(string cameraId, string presetName, double pan, double tilt, double zoom)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (string.IsNullOrEmpty(presetName))
                throw new ArgumentNullException(nameof(presetName));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                var camera = _cameras[cameraId];
                var result = await camera.SetPresetAsync(presetName, pan, tilt, zoom);

                if (result)
                {
                    _logger.LogInformation("PTZ preset {PresetName} set for camera {CameraId}", presetName, cameraId);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting PTZ preset for camera {CameraId}", cameraId);
                throw new CameraControlException($"Failed to set PTZ preset for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Go to PTZ preset position;
        /// </summary>
        public async Task<bool> GoToPTZPresetAsync(string cameraId, string presetName)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            if (string.IsNullOrEmpty(presetName))
                throw new ArgumentNullException(nameof(presetName));

            if (!_cameras.ContainsKey(cameraId))
                throw new CameraNotFoundException($"Camera {cameraId} not found");

            try
            {
                var camera = _cameras[cameraId];
                var result = await camera.GoToPresetAsync(presetName);

                if (result)
                {
                    _logger.LogInformation("Camera {CameraId} moved to preset {PresetName}", cameraId, presetName);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving to PTZ preset for camera {CameraId}", cameraId);
                throw new CameraControlException($"Failed to move to PTZ preset for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Get recent frames from camera buffer;
        /// </summary>
        public List<CameraFrame> GetRecentFrames(string cameraId, int count = 10)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            lock (_syncLock)
            {
                if (!_frameBuffers.ContainsKey(cameraId))
                    return new List<CameraFrame>();

                return _frameBuffers[cameraId]
                    .Take(count)
                    .ToList();
            }
        }

        /// <summary>
        /// Save frame to storage;
        /// </summary>
        public async Task<string> SaveFrameAsync(CameraFrame frame, string directory = null)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            try
            {
                var timestamp = frame.Timestamp.ToString("yyyyMMdd_HHmmssfff");
                var filename = $"frame_{frame.CameraId}_{timestamp}.jpg";

                var saveDirectory = directory ?? GetFramesPath(frame.CameraId);
                await EnsureDirectoryExistsAsync(saveDirectory);

                var filePath = Path.Combine(saveDirectory, filename);

                // Convert to JPEG and save;
                using var ms = new MemoryStream(frame.ImageData);
                using var image = Image.FromStream(ms);
                image.Save(filePath, ImageFormat.Jpeg);

                // Save metadata;
                var metadataPath = Path.ChangeExtension(filePath, ".json");
                var metadata = new;
                {
                    frame.FrameId,
                    frame.CameraId,
                    frame.Timestamp,
                    frame.Width,
                    frame.Height,
                    frame.Duration,
                    Detections = frame.Detections.Select(d => new;
                    {
                        d.ObjectType,
                        d.Confidence,
                        d.BoundingBox;
                    })
                };

                var metadataJson = JsonSerializer.Serialize(metadata, _jsonOptions);
                await File.WriteAllTextAsync(metadataPath, metadataJson);

                _logger.LogDebug("Frame saved to {FilePath}", filePath);

                return filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving frame {FrameId}", frame.FrameId);
                throw new CameraManagerException($"Failed to save frame {frame.FrameId}", ex);
            }
        }

        /// <summary>
        /// Search for frames with specific criteria;
        /// </summary>
        public async Task<List<CameraFrame>> SearchFramesAsync(string cameraId, DateTime startTime, DateTime endTime,
            string objectType = null, double minConfidence = 0.5)
        {
            if (string.IsNullOrEmpty(cameraId))
                throw new ArgumentNullException(nameof(cameraId));

            try
            {
                var framesDirectory = GetFramesPath(cameraId);
                if (!Directory.Exists(framesDirectory))
                    return new List<CameraFrame>();

                var results = new List<CameraFrame>();
                var frameFiles = Directory.GetFiles(framesDirectory, "*.jpg");

                foreach (var frameFile in frameFiles)
                {
                    var metadataFile = Path.ChangeExtension(frameFile, ".json");
                    if (!File.Exists(metadataFile))
                        continue;

                    var metadataJson = await File.ReadAllTextAsync(metadataFile);
                    var metadata = JsonSerializer.Deserialize<FrameMetadata>(metadataJson, _jsonOptions);

                    if (metadata == null)
                        continue;

                    // Check time range;
                    if (metadata.Timestamp < startTime || metadata.Timestamp > endTime)
                        continue;

                    // Check object type if specified;
                    if (!string.IsNullOrEmpty(objectType))
                    {
                        if (!metadata.Detections.Any(d =>
                            d.ObjectType.Equals(objectType, StringComparison.OrdinalIgnoreCase) &&
                            d.Confidence >= minConfidence))
                        {
                            continue;
                        }
                    }

                    // Load frame;
                    var frame = new CameraFrame;
                    {
                        FrameId = metadata.FrameId,
                        CameraId = metadata.CameraId,
                        Timestamp = metadata.Timestamp,
                        Width = metadata.Width,
                        Height = metadata.Height,
                        ImageData = await File.ReadAllBytesAsync(frameFile),
                        Detections = metadata.Detections.Select(d => new DetectionResult;
                        {
                            ObjectType = d.ObjectType,
                            Confidence = d.Confidence,
                            BoundingBox = d.BoundingBox;
                        }).ToList()
                    };

                    results.Add(frame);
                }

                return results.OrderBy(f => f.Timestamp).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching frames for camera {CameraId}", cameraId);
                throw new CameraManagerException($"Failed to search frames for camera {cameraId}", ex);
            }
        }

        /// <summary>
        /// Start all cameras;
        /// </summary>
        public async Task<bool> StartAllCamerasAsync()
        {
            try
            {
                _logger.LogInformation("Starting all cameras...");

                var tasks = new List<Task<bool>>();

                lock (_syncLock)
                {
                    foreach (var cameraId in _cameras.Keys)
                    {
                        tasks.Add(ConnectCameraAsync(cameraId));
                    }
                }

                var results = await Task.WhenAll(tasks);
                var successCount = results.Count(r => r);

                _logger.LogInformation("Started {SuccessCount} out of {TotalCount} cameras",
                    successCount, tasks.Count);

                return successCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting all cameras");
                throw new CameraManagerException("Failed to start all cameras", ex);
            }
        }

        /// <summary>
        /// Stop all cameras;
        /// </summary>
        public async Task<bool> StopAllCamerasAsync()
        {
            try
            {
                _logger.LogInformation("Stopping all cameras...");

                var tasks = new List<Task<bool>>();

                lock (_syncLock)
                {
                    foreach (var cameraId in _cameras.Keys)
                    {
                        tasks.Add(StopCameraAsync(cameraId));
                    }
                }

                var results = await Task.WhenAll(tasks);
                var successCount = results.Count(r => r);

                _logger.LogInformation("Stopped {SuccessCount} out of {TotalCount} cameras",
                    successCount, tasks.Count);

                return successCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping all cameras");
                throw new CameraManagerException("Failed to stop all cameras", ex);
            }
        }

        /// <summary>
        /// Stop a specific camera;
        /// </summary>
        private async Task<bool> StopCameraAsync(string cameraId)
        {
            try
            {
                // Stop streaming;
                if (_streamingTasks.ContainsKey(cameraId))
                {
                    await StopCameraStreamingAsync(cameraId);
                }

                // Stop recording;
                if (_cameras.ContainsKey(cameraId))
                {
                    await _cameras[cameraId].StopRecordingAsync();
                    await _cameras[cameraId].DisconnectAsync();
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping camera {CameraId}", cameraId);
                return false;
            }
        }

        #region Private Methods;

        private async Task LoadConfigurationAsync()
        {
            try
            {
                var configSection = _configuration.GetSection("CameraManager");
                configSection.Bind(_managerConfig);

                // Load camera configurations;
                var camerasPath = Path.Combine(_managerConfig.ConfigDirectory, "cameras");
                if (Directory.Exists(camerasPath))
                {
                    var configFiles = Directory.GetFiles(camerasPath, "*.json");

                    foreach (var configFile in configFiles)
                    {
                        try
                        {
                            var configJson = await File.ReadAllTextAsync(configFile);
                            var config = JsonSerializer.Deserialize<CameraConfig>(configJson, _jsonOptions);

                            if (config != null)
                            {
                                lock (_syncLock)
                                {
                                    _cameraConfigs[config.CameraId] = config;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load camera configuration from {File}", configFile);
                        }
                    }
                }

                _logger.LogInformation("Loaded {Count} camera configurations", _cameraConfigs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load camera manager configuration");
                throw;
            }
        }

        private async Task InitializeCamerasAsync()
        {
            foreach (var config in _cameraConfigs.Values)
            {
                try
                {
                    var cameraDevice = CreateCameraDevice(config);
                    if (cameraDevice != null)
                    {
                        lock (_syncLock)
                        {
                            _cameras[config.CameraId] = cameraDevice;
                            _frameBuffers[config.CameraId] = new Queue<CameraFrame>();
                            _cameraLastSeen[config.CameraId] = DateTime.UtcNow;
                        }

                        if (_managerConfig.AutoConnect)
                        {
                            await ConnectCameraAsync(config.CameraId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize camera {CameraId}", config.CameraId);
                }
            }
        }

        private ICameraDevice CreateCameraDevice(CameraConfig config)
        {
            try
            {
                ICameraDevice device = null;

                switch (config.Type)
                {
                    case CameraType.IPCamera:
                    case CameraType.RTSPStream:
                        device = new IPCameraDevice(config, _logger);
                        break;

                    case CameraType.ONVIFCamera:
                        device = new ONVIFCameraDevice(config, _logger, _httpClient);
                        break;

                    case CameraType.PTZCamera:
                        device = new PTZCameraDevice(config, _logger);
                        break;

                    case CameraType.USBWebcam:
                        device = new USBWebcamDevice(config, _logger);
                        break;

                    case CameraType.ThermalCamera:
                        device = new ThermalCameraDevice(config, _logger);
                        break;

                    default:
                        device = new GenericCameraDevice(config, _logger);
                        break;
                }

                return device;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create camera device for {CameraId}", config.CameraId);
                throw new CameraDeviceException($"Failed to create camera device for {config.CameraId}", ex);
            }
        }

        private async Task CameraStreamingLoop(string cameraId, CancellationToken cancellationToken)
        {
            var camera = _cameras[cameraId];
            var config = _cameraConfigs[cameraId];

            _logger.LogInformation("Starting streaming loop for camera {CameraId}", cameraId);

            try
            {
                var frameCount = 0;
                var lastFrameTime = DateTime.UtcNow;
                var fpsCalculator = new FPSCalculator();

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var frame = await camera.CaptureFrameAsync();
                        if (frame == null)
                        {
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }

                        frame.CameraId = cameraId;
                        frameCount++;

                        // Calculate FPS;
                        var currentTime = DateTime.UtcNow;
                        var frameTime = currentTime - lastFrameTime;
                        frame.FrameRate = fpsCalculator.CalculateFPS(frameTime);
                        lastFrameTime = currentTime;

                        // Process frame;
                        await ProcessFrameAsync(frame);

                        // Store in buffer;
                        StoreFrameInBuffer(cameraId, frame);

                        // Update last seen;
                        lock (_syncLock)
                        {
                            _cameraLastSeen[cameraId] = DateTime.UtcNow;
                        }

                        // Trigger frame processed event;
                        OnFrameProcessed(cameraId, frame);

                        // Check for motion;
                        if (config.EnableMotionDetection && frame.Detections.Any())
                        {
                            await CheckForMotionAsync(cameraId, frame);
                        }

                        // Throttle if needed;
                        var targetFrameTime = TimeSpan.FromSeconds(1.0 / config.Resolution.FrameRate);
                        var processingTime = DateTime.UtcNow - currentTime;

                        if (processingTime < targetFrameTime)
                        {
                            await Task.Delay(targetFrameTime - processingTime, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in streaming loop for camera {CameraId}", cameraId);
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming loop terminated for camera {CameraId}", cameraId);
            }
            finally
            {
                _logger.LogInformation("Streaming loop stopped for camera {CameraId}", cameraId);
            }
        }

        private async Task ProcessFrameAsync(CameraFrame frame)
        {
            try
            {
                var config = GetCameraConfig(frame.CameraId);

                // Create thumbnail;
                frame.Thumbnail = CreateThumbnail(frame.ImageData, 320, 240);

                // Run AI analysis if enabled;
                if (config.EnableObjectDetection || config.EnableFaceRecognition)
                {
                    var detectionTasks = new List<Task>();

                    if (config.EnableObjectDetection)
                    {
                        detectionTasks.Add(DetectObjectsAsync(frame));
                    }

                    if (config.EnableFaceRecognition)
                    {
                        detectionTasks.Add(DetectFacesAsync(frame));
                    }

                    await Task.WhenAll(detectionTasks);
                }

                // Add metadata;
                frame.Metadata["ProcessingTime"] = DateTime.UtcNow;
                frame.Metadata["DetectionCount"] = frame.Detections.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing frame {FrameId}", frame.FrameId);
            }
        }

        private async Task DetectObjectsAsync(CameraFrame frame)
        {
            try
            {
                var detections = await _visionEngine.DetectObjectsAsync(frame.ImageData);

                foreach (var detection in detections)
                {
                    frame.Detections.Add(new DetectionResult;
                    {
                        ObjectType = detection.Label,
                        Confidence = detection.Confidence,
                        BoundingBox = new Rectangle(
                            detection.X, detection.Y,
                            detection.Width, detection.Height;
                        ),
                        Attributes = detection.Attributes;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting objects in frame {FrameId}", frame.FrameId);
            }
        }

        private async Task DetectFacesAsync(CameraFrame frame)
        {
            try
            {
                var faces = await _visionEngine.DetectFacesAsync(frame.ImageData);

                foreach (var face in faces)
                {
                    frame.Detections.Add(new DetectionResult;
                    {
                        ObjectType = "Face",
                        Confidence = face.Confidence,
                        BoundingBox = new Rectangle(
                            face.X, face.Y,
                            face.Width, face.Height;
                        ),
                        Attributes = new Dictionary<string, object>
                        {
                            { "Age", face.Age },
                            { "Gender", face.Gender },
                            { "Emotion", face.Emotion }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting faces in frame {FrameId}", frame.FrameId);
            }
        }

        private async Task CheckForMotionAsync(string cameraId, CameraFrame frame)
        {
            try
            {
                var motionDetected = frame.Detections.Any(d =>
                    d.ObjectType == "Person" || d.ObjectType == "Vehicle" ||
                    d.ObjectType == "Animal");

                if (motionDetected)
                {
                    OnMotionDetected(cameraId, frame);

                    // Notify security system;
                    await _securityMonitor.LogSecurityEventAsync(new SecurityEvent;
                    {
                        EventType = SecurityEventType.MotionDetected,
                        Severity = SecuritySeverity.Medium,
                        Description = $"Motion detected by camera {cameraId}",
                        Location = GetCameraConfig(cameraId).Zones.FirstOrDefault(),
                        Timestamp = DateTime.UtcNow,
                        Metadata = new Dictionary<string, object>
                        {
                            { "CameraId", cameraId },
                            { "FrameId", frame.FrameId },
                            { "Detections", frame.Detections.Select(d => d.ObjectType) }
                        }
                    });

                    // Notify activity analyzer;
                    if (_activityAnalyzer != null)
                    {
                        await _activityAnalyzer.AnalyzeMotionAsync(new MotionDataPoint;
                        {
                            EntityId = $"camera_{cameraId}",
                            Timestamp = frame.Timestamp,
                            X = 0, // These would come from camera positioning;
                            Y = 0,
                            ZoneId = GetCameraConfig(cameraId).Zones.FirstOrDefault(),
                            Confidence = frame.Detections.Max(d => d.Confidence)
                        });
                    }

                    // Notify intrusion sensor;
                    if (_intrusionSensor != null)
                    {
                        await _intrusionSensor.DetectIntrusionAsync(new IntrusionData;
                        {
                            CameraId = cameraId,
                            Timestamp = frame.Timestamp,
                            DetectionCount = frame.Detections.Count,
                            Zone = GetCameraConfig(cameraId).Zones.FirstOrDefault()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for motion in camera {CameraId}", cameraId);
            }
        }

        private void StoreFrameInBuffer(string cameraId, CameraFrame frame)
        {
            lock (_syncLock)
            {
                if (!_frameBuffers.ContainsKey(cameraId))
                    return;

                var buffer = _frameBuffers[cameraId];
                buffer.Enqueue(frame);

                // Limit buffer size;
                while (buffer.Count > MAX_FRAME_BUFFER_SIZE)
                {
                    buffer.Dequeue();
                }
            }
        }

        private async Task HealthMonitoringLoop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting health monitoring loop");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

                    var monitoringTasks = new List<Task>();

                    lock (_syncLock)
                    {
                        foreach (var cameraId in _cameras.Keys)
                        {
                            monitoringTasks.Add(MonitorCameraHealthAsync(cameraId));
                        }
                    }

                    await Task.WhenAll(monitoringTasks);

                    // Check for disconnected cameras;
                    await CheckDisconnectedCamerasAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in health monitoring loop");
                    await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                }
            }

            _logger.LogInformation("Health monitoring loop stopped");
        }

        private async Task MonitorCameraHealthAsync(string cameraId)
        {
            try
            {
                var camera = _cameras[cameraId];
                var metrics = await camera.GetHealthMetricsAsync();

                lock (_syncLock)
                {
                    _healthMetrics[cameraId] = metrics;
                }

                // Check for issues;
                if (metrics.PacketLossRate > 0.1) // 10% packet loss;
                {
                    OnCameraStatusChanged(cameraId, CameraEventType.QualityDegraded,
                        $"High packet loss: {metrics.PacketLossRate:P}");
                }

                if (metrics.Latency > 1000) // 1 second latency;
                {
                    OnCameraStatusChanged(cameraId, CameraEventType.QualityDegraded,
                        $"High latency: {metrics.Latency}ms");
                }

                if (metrics.AverageFrameRate < 10) // Less than 10 FPS;
                {
                    OnCameraStatusChanged(cameraId, CameraEventType.QualityDegraded,
                        $"Low frame rate: {metrics.AverageFrameRate:F1} FPS");
                }

                // Update performance monitor;
                await _performanceMonitor.RecordMetricAsync(
                    $"camera.{cameraId}.fps", metrics.AverageFrameRate);
                await _performanceMonitor.RecordMetricAsync(
                    $"camera.{cameraId}.latency", metrics.Latency);
                await _performanceMonitor.RecordMetricAsync(
                    $"camera.{cameraId}.packet_loss", metrics.PacketLossRate);

                // Trigger health metrics event;
                OnHealthMetricsUpdated(cameraId, metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring health for camera {CameraId}", cameraId);
            }
        }

        private async Task CheckDisconnectedCamerasAsync()
        {
            try
            {
                var disconnectedCameras = new List<string>();
                var timeout = TimeSpan.FromMinutes(5);

                lock (_syncLock)
                {
                    foreach (var kvp in _cameraLastSeen)
                    {
                        if (DateTime.UtcNow - kvp.Value > timeout)
                        {
                            disconnectedCameras.Add(kvp.Key);
                        }
                    }
                }

                foreach (var cameraId in disconnectedCameras)
                {
                    _logger.LogWarning("Camera {CameraId} appears to be disconnected, attempting to reconnect", cameraId);

                    try
                    {
                        await ConnectCameraAsync(cameraId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reconnect camera {CameraId}", cameraId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking disconnected cameras");
            }
        }

        private async Task MaintenanceLoop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting maintenance loop");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), cancellationToken);

                    // Perform maintenance tasks;
                    await CleanupOldRecordingsAsync();
                    await CheckStorageUsageAsync();
                    await UpdateCameraFirmwareAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in maintenance loop");
                }
            }

            _logger.LogInformation("Maintenance loop stopped");
        }

        private async Task CleanupOldRecordingsAsync()
        {
            try
            {
                var recordingsPath = Path.Combine(_managerConfig.StorageDirectory, "recordings");
                if (!Directory.Exists(recordingsPath))
                    return;

                var cutoffTime = DateTime.UtcNow - TimeSpan.FromDays(30); // Keep 30 days;

                foreach (var cameraDir in Directory.GetDirectories(recordingsPath))
                {
                    var videoFiles = Directory.GetFiles(cameraDir, "*.mp4");

                    foreach (var videoFile in videoFiles)
                    {
                        var creationTime = File.GetCreationTimeUtc(videoFile);
                        if (creationTime < cutoffTime)
                        {
                            try
                            {
                                File.Delete(videoFile);
                                _logger.LogDebug("Deleted old recording: {File}", videoFile);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete old recording: {File}", videoFile);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old recordings");
            }
        }

        private async Task CheckStorageUsageAsync()
        {
            try
            {
                var storagePath = _managerConfig.StorageDirectory;
                if (!Directory.Exists(storagePath))
                    return;

                var drive = new DriveInfo(Path.GetPathRoot(storagePath));
                var freeSpacePercent = (double)drive.AvailableFreeSpace / drive.TotalSize;

                if (freeSpacePercent < 0.1) // Less than 10% free space;
                {
                    _logger.LogWarning("Low disk space: {FreePercent:P}", freeSpacePercent);

                    // Trigger storage full event for all cameras;
                    lock (_syncLock)
                    {
                        foreach (var cameraId in _cameras.Keys)
                        {
                            OnCameraStatusChanged(cameraId, CameraEventType.StorageFull,
                                $"Low disk space: {freeSpacePercent:P}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking storage usage");
            }
        }

        private async Task UpdateCameraFirmwareAsync()
        {
            // This would typically check for firmware updates and apply them;
            // For now, just log that maintenance was performed;
            _logger.LogDebug("Camera firmware maintenance check performed");
        }

        private void CleanupOldData(object state)
        {
            try
            {
                // Clean up old frame buffers;
                var cutoffTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);

                lock (_syncLock)
                {
                    foreach (var cameraId in _frameBuffers.Keys.ToList())
                    {
                        var buffer = _frameBuffers[cameraId];
                        while (buffer.Count > 0 && buffer.Peek().Timestamp < cutoffTime)
                        {
                            buffer.Dequeue();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup timer");
            }
        }

        private bool IsCameraRunning(string cameraId)
        {
            lock (_syncLock)
            {
                return _cameras.ContainsKey(cameraId) &&
                       (_cameras[cameraId].Status == CameraStatus.Streaming ||
                        _cameras[cameraId].Status == CameraStatus.Recording);
            }
        }

        private string GetCameraConfigPath(string cameraId)
        {
            var camerasPath = Path.Combine(_managerConfig.ConfigDirectory, "cameras");
            Directory.CreateDirectory(camerasPath);
            return Path.Combine(camerasPath, $"{cameraId}.json");
        }

        private string GetRecordingPath(string cameraId)
        {
            var recordingsPath = Path.Combine(_managerConfig.StorageDirectory, "recordings", cameraId);
            return recordingsPath;
        }

        private string GetFramesPath(string cameraId)
        {
            var framesPath = Path.Combine(_managerConfig.StorageDirectory, "frames", cameraId);
            return framesPath;
        }

        private async Task EnsureDirectoryExistsAsync(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                await Task.CompletedTask;
            }
        }

        private async Task SaveCameraConfigurationAsync(CameraConfig config)
        {
            try
            {
                var configPath = GetCameraConfigPath(config.CameraId);
                var configJson = JsonSerializer.Serialize(config, _jsonOptions);
                await File.WriteAllTextAsync(configPath, configJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save camera configuration for {CameraId}", config.CameraId);
                throw;
            }
        }

        private byte[] CreateThumbnail(byte[] imageData, int width, int height)
        {
            try
            {
                using var ms = new MemoryStream(imageData);
                using var image = Image.FromStream(ms);

                var thumbnail = image.GetThumbnailImage(width, height, null, IntPtr.Zero);
                using var thumbnailMs = new MemoryStream();
                thumbnail.Save(thumbnailMs, ImageFormat.Jpeg);
                return thumbnailMs.ToArray();
            }
            catch (Exception)
            {
                return new byte[0];
            }
        }

        private async Task SubscribeToEventsAsync()
        {
            try
            {
                // Subscribe to security events;
                await _eventBus.SubscribeAsync<SecurityEvent>("camera_manager",
                    async securityEvent =>
                    {
                        if (securityEvent.EventType == SecurityEventType.IntrusionDetected)
                        {
                            // Handle intrusion events;
                            await HandleIntrusionEventAsync(securityEvent);
                        }
                    });

                // Subscribe to system events;
                await _eventBus.SubscribeAsync<SystemEvent>("camera_manager",
                    async systemEvent =>
                    {
                        if (systemEvent.EventType == SystemEventType.Shutdown)
                        {
                            await StopAllCamerasAsync();
                        }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to events");
            }
        }

        private async Task HandleIntrusionEventAsync(SecurityEvent securityEvent)
        {
            try
            {
                // Find cameras in the affected zone;
                var affectedZone = securityEvent.Location;
                var affectedCameras = new List<string>();

                lock (_syncLock)
                {
                    foreach (var config in _cameraConfigs.Values)
                    {
                        if (config.Zones.Contains(affectedZone))
                        {
                            affectedCameras.Add(config.CameraId);
                        }
                    }
                }

                // Focus cameras on intrusion area;
                foreach (var cameraId in affectedCameras)
                {
                    if (GetCameraConfig(cameraId).Type == CameraType.PTZCamera)
                    {
                        // Move PTZ camera to predefined intrusion preset;
                        await GoToPTZPresetAsync(cameraId, "intrusion_zone");
                    }

                    // Increase recording quality;
                    await UpdateCameraConfigAsync(cameraId, config =>
                    {
                        config.Resolution.Quality = 100;
                        config.Resolution.FrameRate = 60;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling intrusion event");
            }
        }

        private async Task PublishCameraEventAsync(string cameraId, CameraEventType eventType, string message)
        {
            try
            {
                var cameraEvent = new CameraEvent;
                {
                    CameraId = cameraId,
                    EventType = eventType,
                    Timestamp = DateTime.UtcNow,
                    Message = message,
                    CameraStatus = GetCameraStatus(cameraId)
                };

                await _eventBus.PublishAsync(cameraEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish camera event");
            }
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnCameraStatusChanged(string cameraId, CameraEventType eventType, string message)
        {
            var args = new CameraEventArgs;
            {
                CameraId = cameraId,
                EventType = eventType,
                Message = message,
                Timestamp = DateTime.UtcNow;
            };

            CameraStatusChanged?.Invoke(this, args);
        }

        protected virtual void OnMotionDetected(string cameraId, CameraFrame frame)
        {
            var args = new CameraEventArgs;
            {
                CameraId = cameraId,
                EventType = CameraEventType.MotionDetected,
                Frame = frame,
                Message = $"Motion detected: {frame.Detections.Count} objects",
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    { "DetectionCount", frame.Detections.Count },
                    { "ObjectTypes", frame.Detections.Select(d => d.ObjectType).Distinct() }
                }
            };

            MotionDetected?.Invoke(this, args);
        }

        protected virtual void OnFrameProcessed(string cameraId, CameraFrame frame)
        {
            var args = new CameraEventArgs;
            {
                CameraId = cameraId,
                EventType = CameraEventType.FrameReceived,
                Frame = frame,
                Message = $"Frame processed: {frame.FrameId}",
                Timestamp = DateTime.UtcNow;
            };

            FrameProcessed?.Invoke(this, args);
        }

        protected virtual void OnHealthMetricsUpdated(string cameraId, CameraHealthMetrics metrics)
        {
            var args = new CameraEventArgs;
            {
                CameraId = cameraId,
                EventType = CameraEventType.MaintenanceRequired,
                Message = $"Health metrics updated: {metrics.UptimePercentage:P} uptime",
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    { "Metrics", metrics }
                }
            };

            HealthMetricsUpdated?.Invoke(this, args);
        }

        #endregion;

        #region IHostedService Implementation;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting CameraManager service...");
            await InitializeAsync();
            _logger.LogInformation("CameraManager service started");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping CameraManager service...");

            _globalCancellationTokenSource.Cancel();
            await StopAllCamerasAsync();

            _logger.LogInformation("CameraManager service stopped");
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
                    _globalCancellationTokenSource?.Cancel();
                    _globalCancellationTokenSource?.Dispose();

                    _cleanupTimer?.Dispose();

                    _httpClient?.Dispose();
                    _connectionSemaphore?.Dispose();

                    // Dispose all cameras;
                    foreach (var camera in _cameras.Values)
                    {
                        try
                        {
                            camera.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error disposing camera");
                        }
                    }

                    _cameras.Clear();
                    _cameraConfigs.Clear();
                    _streamingTasks.Clear();

                    foreach (var cts in _cancellationTokens.Values)
                    {
                        cts.Dispose();
                    }
                    _cancellationTokens.Clear();

                    _frameBuffers.Clear();
                    _cameraLastSeen.Clear();
                    _healthMetrics.Clear();
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Nested Classes;

        /// <summary>
        /// Camera manager configuration;
        /// </summary>
        public class CameraManagerConfig;
        {
            public string ConfigDirectory { get; set; } = "Config/Cameras";
            public string StorageDirectory { get; set; } = "Storage/Cameras";
            public bool AutoConnect { get; set; } = true;
            public bool AutoStartStreaming { get; set; } = true;
            public int MaxFrameBufferSize { get; set; } = 1000;
            public int HealthCheckInterval { get; set; } = 30; // seconds;
            public int MaintenanceInterval { get; set; } = 3600; // seconds;
            public double LowDiskSpaceThreshold { get; set; } = 0.1; // 10%
            public List<string> DefaultZones { get; set; }
            public Dictionary<string, string> CameraTemplates { get; set; }

            public CameraManagerConfig()
            {
                DefaultZones = new List<string>();
                CameraTemplates = new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// FPS calculator for camera streams;
        /// </summary>
        private class FPSCalculator;
        {
            private readonly Queue<DateTime> _frameTimes;
            private const int SAMPLE_SIZE = 30;

            public FPSCalculator()
            {
                _frameTimes = new Queue<DateTime>();
            }

            public double CalculateFPS(TimeSpan frameTime)
            {
                _frameTimes.Enqueue(DateTime.UtcNow);

                if (_frameTimes.Count > SAMPLE_SIZE)
                {
                    _frameTimes.Dequeue();
                }

                if (_frameTimes.Count < 2)
                    return 0;

                var timeSpan = _frameTimes.Last() - _frameTimes.First();
                if (timeSpan.TotalSeconds == 0)
                    return 0;

                return (_frameTimes.Count - 1) / timeSpan.TotalSeconds;
            }
        }

        /// <summary>
        /// Frame metadata for storage;
        /// </summary>
        private class FrameMetadata;
        {
            public string FrameId { get; set; }
            public string CameraId { get; set; }
            public DateTime Timestamp { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public List<DetectionMetadata> Detections { get; set; }

            public FrameMetadata()
            {
                Detections = new List<DetectionMetadata>();
            }
        }

        /// <summary>
        /// Detection metadata for storage;
        /// </summary>
        private class DetectionMetadata;
        {
            public string ObjectType { get; set; }
            public double Confidence { get; set; }
            public Rectangle BoundingBox { get; set; }
        }

        #endregion;
    }

    #region Camera Device Implementations;

    /// <summary>
    /// Base implementation for camera devices;
    /// </summary>
    public abstract class BaseCameraDevice : ICameraDevice;
    {
        protected readonly ILogger _logger;
        protected readonly CameraConfig _config;
        protected CameraStatus _status;
        protected bool _isDisposed;
        protected HttpClient _httpClient;
        protected CancellationTokenSource _streamingCts;
        protected Task _streamingTask;
        protected int _frameCount;
        protected DateTime _startTime;
        protected DateTime _lastFrameTime;
        protected double _totalLatency;

        public string CameraId => _config.CameraId;
        public CameraConfig Config => _config;
        public CameraStatus Status => _status;

        protected BaseCameraDevice(CameraConfig config, ILogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _status = CameraStatus.Disconnected;
            _httpClient = new HttpClient();
        }

        public virtual async Task<bool> ConnectAsync()
        {
            if (_status != CameraStatus.Disconnected && _status != CameraStatus.Error)
                return true;

            try
            {
                _status = CameraStatus.Connecting;
                _logger.LogInformation("Connecting to camera {CameraId}...", CameraId);

                var connected = await PerformConnectionAsync();

                if (connected)
                {
                    _status = CameraStatus.Connected;
                    _startTime = DateTime.UtcNow;
                    _frameCount = 0;
                    _totalLatency = 0;
                    _logger.LogInformation("Camera {CameraId} connected successfully", CameraId);
                }
                else;
                {
                    _status = CameraStatus.Error;
                    _logger.LogError("Failed to connect to camera {CameraId}", CameraId);
                }

                return connected;
            }
            catch (Exception ex)
            {
                _status = CameraStatus.Error;
                _logger.LogError(ex, "Error connecting to camera {CameraId}", CameraId);
                return false;
            }
        }

        public virtual async Task DisconnectAsync()
        {
            if (_status == CameraStatus.Disconnected)
                return;

            try
            {
                await StopStreamingAsync();
                await StopRecordingAsync();

                await PerformDisconnectionAsync();

                _status = CameraStatus.Disconnected;
                _logger.LogInformation("Camera {CameraId} disconnected", CameraId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting camera {CameraId}", CameraId);
                throw;
            }
        }

        public abstract Task<CameraFrame> CaptureFrameAsync();

        public virtual async Task StartStreamingAsync(CancellationToken cancellationToken)
        {
            if (_status != CameraStatus.Connected)
                throw new InvalidOperationException("Camera must be connected before streaming");

            if (_status == CameraStatus.Streaming || _status == CameraStatus.Recording)
                return;

            try
            {
                _streamingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _streamingTask = Task.Run(() => InternalStreamingLoop(_streamingCts.Token), _streamingCts.Token);

                _status = CameraStatus.Streaming;
                _logger.LogInformation("Streaming started for camera {CameraId}", CameraId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting streaming for camera {CameraId}", CameraId);
                throw;
            }
        }

        public virtual async Task StopStreamingAsync()
        {
            if (_status != CameraStatus.Streaming && _status != CameraStatus.Recording)
                return;

            try
            {
                _streamingCts?.Cancel();

                if (_streamingTask != null && !_streamingTask.IsCompleted)
                {
                    await _streamingTask.WaitAsync(TimeSpan.FromSeconds(10));
                }

                _status = CameraStatus.Connected;
                _logger.LogInformation("Streaming stopped for camera {CameraId}", CameraId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping streaming for camera {CameraId}", CameraId);
                throw;
            }
        }

        public virtual async Task StartRecordingAsync(string outputPath)
        {
            if (_status != CameraStatus.Connected && _status != CameraStatus.Streaming)
                throw new InvalidOperationException("Camera must be connected before recording");

            _logger.LogInformation("Recording started for camera {CameraId} at {Path}", CameraId, outputPath);
            // Implementation would start writing frames to file;
            _status = CameraStatus.Recording;
            await Task.CompletedTask;
        }

        public virtual async Task StopRecordingAsync()
        {
            if (_status != CameraStatus.Recording)
                return;

            _logger.LogInformation("Recording stopped for camera {CameraId}", CameraId);
            _status = CameraStatus.Connected;
            await Task.CompletedTask;
        }

        public virtual async Task<CameraHealthMetrics> GetHealthMetricsAsync()
        {
            var metrics = new CameraHealthMetrics;
            {
                CameraId = CameraId,
                Timestamp = DateTime.UtcNow,
                Status = _status;
            };

            if (_startTime != default)
            {
                var uptime = DateTime.UtcNow - _startTime;
                metrics.UptimePercentage = 100.0; // Simplified;
            }

            if (_frameCount > 0 && _lastFrameTime != default)
            {
                var elapsed = DateTime.UtcNow - _startTime;
                metrics.AverageFrameRate = _frameCount / elapsed.TotalSeconds;

                if (_frameCount > 0)
                {
                    metrics.Latency = _totalLatency / _frameCount;
                }
            }

            // Simulate other metrics;
            metrics.PacketLossRate = 0.01; // 1% packet loss;
            metrics.BandwidthUsage = _config.Resolution.Width * _config.Resolution.Height *
                                    _config.Resolution.FrameRate * 0.1; // Simplified;
            metrics.TotalFramesProcessed = _frameCount;
            metrics.FailedFrames = 0;
            metrics.MemoryUsage = 100; // MB;
            metrics.CPUUsage = 5; // Percentage;

            return await Task.FromResult(metrics);
        }

        public virtual async Task<bool> SetPresetAsync(string presetName, double pan, double tilt, double zoom)
        {
            _logger.LogDebug("Setting preset {PresetName} for camera {CameraId}: Pan={Pan}, Tilt={Tilt}, Zoom={Zoom}",
                presetName, CameraId, pan, tilt, zoom);

            // Implementation would save preset to camera;
            return await Task.FromResult(true);
        }

        public virtual async Task<bool> GoToPresetAsync(string presetName)
        {
            _logger.LogDebug("Moving camera {CameraId} to preset {PresetName}", CameraId, presetName);

            // Implementation would move camera to preset;
            return await Task.FromResult(true);
        }

        public virtual async Task<bool> PTZControlAsync(double panDelta, double tiltDelta, double zoomDelta)
        {
            _logger.LogDebug("PTZ control for camera {CameraId}: PanDelta={PanDelta}, TiltDelta={TiltDelta}, ZoomDelta={ZoomDelta}",
                CameraId, panDelta, tiltDelta, zoomDelta);

            // Implementation would send PTZ commands;
            return await Task.FromResult(true);
        }

        protected abstract Task<bool> PerformConnectionAsync();
        protected abstract Task PerformDisconnectionAsync();
        protected abstract Task InternalStreamingLoop(CancellationToken cancellationToken);

        public virtual void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;

                _streamingCts?.Cancel();
                _streamingCts?.Dispose();

                _httpClient?.Dispose();

                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// IP Camera device implementation;
    /// </summary>
    public class IPCameraDevice : BaseCameraDevice;
    {
        public IPCameraDevice(CameraConfig config, ILogger logger)
            : base(config, logger)
        {
        }

        protected override async Task<bool> PerformConnectionAsync()
        {
            try
            {
                // Test connection by making a HTTP request;
                var testUrl = $"http://{_config.IPAddress}:{_config.Port}";
                var response = await _httpClient.GetAsync(testUrl);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        protected override async Task PerformDisconnectionAsync()
        {
            await Task.CompletedTask;
        }

        public override async Task<CameraFrame> CaptureFrameAsync()
        {
            try
            {
                var frameUrl = $"{_config.RTSPUrl}/snapshot";
                var response = await _httpClient.GetAsync(frameUrl);

                if (!response.IsSuccessStatusCode)
                    throw new CameraCaptureException($"Failed to capture frame: {response.StatusCode}");

                var imageData = await response.Content.ReadAsByteArrayAsync();
                var frameTime = DateTime.UtcNow;

                _frameCount++;
                _lastFrameTime = frameTime;
                _totalLatency += (frameTime - _lastFrameTime).TotalMilliseconds;

                return new CameraFrame;
                {
                    CameraId = CameraId,
                    ImageData = imageData,
                    Width = _config.Resolution.Width,
                    Height = _config.Resolution.Height,
                    Timestamp = frameTime,
                    Format = PixelFormat.Format24bppRgb;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing frame from IP camera {CameraId}", CameraId);
                throw new CameraCaptureException($"Failed to capture frame from camera {CameraId}", ex);
            }
        }

        protected override async Task InternalStreamingLoop(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting streaming loop for IP camera {CameraId}", CameraId);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CaptureFrameAsync();
                    await Task.Delay(1000 / _config.Resolution.FrameRate, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in IP camera streaming loop for {CameraId}", CameraId);
                    await Task.Delay(1000, cancellationToken);
                }
            }

            _logger.LogInformation("Streaming loop stopped for IP camera {CameraId}", CameraId);
        }
    }

    /// <summary>
    /// ONVIF camera device implementation;
    /// </summary>
    public class ONVIFCameraDevice : BaseCameraDevice;
    {
        public ONVIFCameraDevice(CameraConfig config, ILogger logger, HttpClient httpClient)
            : base(config, logger)
        {
            _httpClient = httpClient;
        }

        protected override async Task<bool> PerformConnectionAsync()
        {
            // ONVIF specific connection logic;
            return await Task.FromResult(true);
        }

        protected override async Task PerformDisconnectionAsync()
        {
            await Task.CompletedTask;
        }

        public override async Task<CameraFrame> CaptureFrameAsync()
        {
            // ONVIF specific frame capture;
            return await Task.FromResult(new CameraFrame;
            {
                CameraId = CameraId,
                ImageData = new byte[0],
                Width = _config.Resolution.Width,
                Height = _config.Resolution.Height,
                Timestamp = DateTime.UtcNow;
            });
        }

        protected override async Task InternalStreamingLoop(CancellationToken cancellationToken)
        {
            // ONVIF specific streaming;
            await Task.CompletedTask;
        }
    }

    // Additional camera device implementations would follow similar patterns...

    #endregion;

    #region Custom Exceptions;

    public class CameraManagerException : Exception
    {
        public CameraManagerException(string message) : base(message) { }
        public CameraManagerException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CameraConfigurationException : CameraManagerException;
    {
        public CameraConfigurationException(string message) : base(message) { }
    }

    public class CameraDeviceException : CameraManagerException;
    {
        public CameraDeviceException(string message) : base(message) { }
        public CameraDeviceException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CameraNotFoundException : CameraManagerException;
    {
        public CameraNotFoundException(string message) : base(message) { }
    }

    public class CameraConnectionException : CameraManagerException;
    {
        public CameraConnectionException(string message) : base(message) { }
        public CameraConnectionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CameraCaptureException : CameraManagerException;
    {
        public CameraCaptureException(string message) : base(message) { }
        public CameraCaptureException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CameraControlException : CameraManagerException;
    {
        public CameraControlException(string message) : base(message) { }
        public CameraControlException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CameraNotConnectedException : CameraManagerException;
    {
        public CameraNotConnectedException(string message) : base(message) { }
    }

    #endregion;

    #region Interfaces;

    public interface ICameraManager;
    {
        Task<CameraConfig> AddCameraAsync(CameraConfig config);
        Task<bool> RemoveCameraAsync(string cameraId);
        Task<bool> ConnectCameraAsync(string cameraId);
        Task<bool> DisconnectCameraAsync(string cameraId);
        Task<bool> StartCameraStreamingAsync(string cameraId);
        Task<bool> StopCameraStreamingAsync(string cameraId);
        Task<bool> StartCameraRecordingAsync(string cameraId, string customPath = null);
        Task<CameraFrame> CaptureFrameAsync(string cameraId);
        CameraStatus GetCameraStatus(string cameraId);
        Task<CameraHealthMetrics> GetCameraHealthAsync(string cameraId);
        Task<bool> ControlPTZAsync(string cameraId, double panDelta, double tiltDelta, double zoomDelta);
        Task<bool> SetPTZPresetAsync(string cameraId, string presetName, double pan, double tilt, double zoom);
        Task<bool> GoToPTZPresetAsync(string cameraId, string presetName);
        Task<bool> StartAllCamerasAsync();
        Task<bool> StopAllCamerasAsync();
    }

    #endregion;
}
