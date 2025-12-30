using Microsoft.Extensions.Logging;
using NEDA.AI.ComputerVision;
using NEDA.Biometrics.MotionTracking;
using NEDA.Core.Common;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Monitoring.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YourNamespace.Monitoring.PeopleCounting;

namespace NEDA.RealTimeMonitoring.RoomSurveillance;
{
    /// <summary>
    /// Advanced motion detection engine for real-time surveillance;
    /// Supports multiple detection algorithms and adaptive sensitivity;
    /// </summary>
    public class MotionDetector : IMotionDetector, IDisposable;
    {
        private readonly ILogger<MotionDetector> _logger;
        private readonly IComputerVisionEngine _visionEngine;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly MotionDetectionConfig _config;

        private ConcurrentQueue<VideoFrame> _frameQueue;
        private BlockingCollection<MotionEvent> _eventQueue;
        private CancellationTokenSource _processingTokenSource;
        private Task _processingTask;
        private bool _isInitialized;
        private bool _isProcessing;
        private int _frameCount;
        private DateTime _lastMotionTime;

        // Detection algorithms;
        private IMotionDetectionAlgorithm _currentAlgorithm;
        private readonly Dictionary<MotionAlgorithmType, IMotionDetectionAlgorithm> _algorithms;

        // Motion regions and zones;
        private List<SurveillanceZone> _surveillanceZones;
        private MotionCalibration _calibrationData;

        // Performance monitoring;
        private PerformanceMetrics _metrics;
        private object _syncLock = new object();

        public event EventHandler<MotionDetectedEventArgs> OnMotionDetected;
        public event EventHandler<MotionZoneEventArgs> OnZoneActivityChanged;
        public event EventHandler<DetectionStatusChangedEventArgs> OnDetectionStatusChanged;

        /// <summary>
        /// Initializes a new instance of MotionDetector;
        /// </summary>
        public MotionDetector(
            ILogger<MotionDetector> logger,
            IComputerVisionEngine visionEngine,
            IDiagnosticTool diagnosticTool,
            MotionDetectionConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _visionEngine = visionEngine ?? throw new ArgumentNullException(nameof(visionEngine));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _config = config ?? MotionDetectionConfig.Default;

            InitializeComponents();
        }

        private void InitializeComponents()
        {
            try
            {
                _logger.LogInformation("Initializing Motion Detector...");

                _frameQueue = new ConcurrentQueue<VideoFrame>();
                _eventQueue = new BlockingCollection<MotionEvent>(new ConcurrentQueue<MotionEvent>());
                _processingTokenSource = new CancellationTokenSource();

                _surveillanceZones = new List<SurveillanceZone>();
                _calibrationData = new MotionCalibration();
                _metrics = new PerformanceMetrics();
                _lastMotionTime = DateTime.MinValue;

                InitializeAlgorithms();
                InitializeZones();

                _isInitialized = true;

                _logger.LogInformation("Motion Detector initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Motion Detector");
                throw new MotionDetectionInitializationException(
                    "Failed to initialize motion detection system", ex);
            }
        }

        private void InitializeAlgorithms()
        {
            _algorithms = new Dictionary<MotionAlgorithmType, IMotionDetectionAlgorithm>
            {
                [MotionAlgorithmType.FrameDifference] = new FrameDifferenceAlgorithm(_config),
                [MotionAlgorithmType.BackgroundSubtraction] = new BackgroundSubtractionAlgorithm(_config),
                [MotionAlgorithmType.OpticalFlow] = new OpticalFlowAlgorithm(_config),
                [MotionAlgorithmType.AdaptiveThresholding] = new AdaptiveThresholdingAlgorithm(_config),
                [MotionAlgorithmType.DeepLearning] = new DeepLearningMotionAlgorithm(_config, _visionEngine)
            };

            _currentAlgorithm = _algorithms[_config.DefaultAlgorithm];
        }

        private void InitializeZones()
        {
            // Create default zones based on configuration;
            if (_config.Zones?.Any() == true)
            {
                foreach (var zoneConfig in _config.Zones)
                {
                    var zone = new SurveillanceZone(
                        zoneConfig.Id,
                        zoneConfig.Name,
                        zoneConfig.Bounds,
                        zoneConfig.Sensitivity,
                        zoneConfig.AlertThreshold);

                    zone.OnActivityChanged += HandleZoneActivityChanged;
                    _surveillanceZones.Add(zone);
                }
            }
            else;
            {
                // Create a single full-frame zone;
                var defaultZone = new SurveillanceZone(
                    "default",
                    "Full Frame",
                    new Rectangle(0, 0, _config.FrameWidth, _config.FrameHeight),
                    _config.DefaultSensitivity,
                    _config.DefaultAlertThreshold);

                defaultZone.OnActivityChanged += HandleZoneActivityChanged;
                _surveillanceZones.Add(defaultZone);
            }
        }

        /// <summary>
        /// Starts motion detection processing;
        /// </summary>
        public void StartDetection()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Motion detector not initialized");

            if (_isProcessing)
                return;

            lock (_syncLock)
            {
                _isProcessing = true;
                _processingTask = Task.Run(() => ProcessFramesAsync(_processingTokenSource.Token));

                OnDetectionStatusChanged?.Invoke(this,
                    new DetectionStatusChangedEventArgs(true, "Motion detection started"));

                _logger.LogInformation("Motion detection started");
            }
        }

        /// <summary>
        /// Stops motion detection processing;
        /// </summary>
        public void StopDetection()
        {
            if (!_isProcessing)
                return;

            lock (_syncLock)
            {
                _isProcessing = false;
                _processingTokenSource?.Cancel();

                try
                {
                    _processingTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException ex)
                {
                    _logger.LogWarning(ex, "Error while stopping motion detection");
                }

                OnDetectionStatusChanged?.Invoke(this,
                    new DetectionStatusChangedEventArgs(false, "Motion detection stopped"));

                _logger.LogInformation("Motion detection stopped");
            }
        }

        /// <summary>
        /// Processes a video frame for motion detection;
        /// </summary>
        public async Task<MotionDetectionResult> ProcessFrameAsync(VideoFrame frame)
        {
            ValidateFrame(frame);

            try
            {
                _frameCount++;
                _metrics.TotalFramesProcessed++;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Add to processing queue;
                _frameQueue.Enqueue(frame);

                // Process with current algorithm;
                var algorithmResult = await _currentAlgorithm.AnalyzeFrameAsync(frame);

                // Update zones with detection results;
                UpdateZonesWithDetection(algorithmResult);

                // Check for significant motion events;
                var motionEvents = DetectMotionEvents(algorithmResult);

                // Process and raise events;
                await ProcessMotionEventsAsync(motionEvents);

                stopwatch.Stop();
                UpdatePerformanceMetrics(stopwatch.ElapsedMilliseconds, algorithmResult);

                return new MotionDetectionResult;
                {
                    FrameId = frame.Id,
                    Timestamp = frame.Timestamp,
                    MotionDetected = motionEvents.Any(),
                    MotionEvents = motionEvents,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    AlgorithmUsed = _currentAlgorithm.GetType().Name;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing frame {FrameId}", frame.Id);
                _diagnosticTool.RecordError("MotionDetection", ex);

                // Fallback to simpler algorithm on error;
                await SwitchToFallbackAlgorithmAsync();

                throw new MotionProcessingException($"Failed to process frame {frame.Id}", ex);
            }
        }

        private void ValidateFrame(VideoFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            if (frame.Data == null || frame.Data.Length == 0)
                throw new ArgumentException("Frame data is empty");

            if (frame.Width != _config.FrameWidth || frame.Height != _config.FrameHeight)
                throw new ArgumentException($"Frame dimensions {frame.Width}x{frame.Height} " +
                    $"do not match configured dimensions {_config.FrameWidth}x{_config.FrameHeight}");
        }

        private List<MotionEvent> DetectMotionEvents(MotionAnalysisResult result)
        {
            var events = new List<MotionEvent>();

            if (result.MotionIntensity < _config.MotionThreshold)
                return events;

            // Check each zone for motion;
            foreach (var zone in _surveillanceZones)
            {
                var zoneMotion = CalculateZoneMotion(zone, result.MotionAreas);

                if (zoneMotion.Intensity > zone.Sensitivity)
                {
                    var motionEvent = new MotionEvent;
                    {
                        EventId = Guid.NewGuid(),
                        Timestamp = DateTime.UtcNow,
                        ZoneId = zone.Id,
                        ZoneName = zone.Name,
                        MotionIntensity = zoneMotion.Intensity,
                        MotionAreas = zoneMotion.Areas,
                        BoundingBox = zoneMotion.BoundingBox,
                        ObjectCount = zoneMotion.ObjectCount,
                        Confidence = zoneMotion.Confidence;
                    };

                    events.Add(motionEvent);

                    // Update zone activity;
                    zone.UpdateActivity(motionEvent);
                }
            }

            return events;
        }

        private ZoneMotionResult CalculateZoneMotion(SurveillanceZone zone, List<Rectangle> motionAreas)
        {
            var result = new ZoneMotionResult();

            // Filter motion areas that intersect with the zone;
            var zoneMotionAreas = motionAreas;
                .Where(area => zone.Bounds.IntersectsWith(area))
                .Select(area => Rectangle.Intersect(zone.Bounds, area))
                .ToList();

            if (!zoneMotionAreas.Any())
                return result;

            // Calculate overall intensity;
            result.Intensity = CalculateMotionIntensity(zoneMotionAreas, zone.Bounds);
            result.Areas = zoneMotionAreas;
            result.BoundingBox = CalculateBoundingBox(zoneMotionAreas);
            result.ObjectCount = EstimateObjectCount(zoneMotionAreas);
            result.Confidence = CalculateConfidence(zoneMotionAreas, result.Intensity);

            return result;
        }

        private float CalculateMotionIntensity(List<Rectangle> motionAreas, Rectangle zoneBounds)
        {
            if (!motionAreas.Any())
                return 0f;

            // Calculate total motion area as percentage of zone;
            var totalMotionArea = motionAreas.Sum(area => area.Width * area.Height);
            var zoneArea = zoneBounds.Width * zoneBounds.Height;

            var areaRatio = (float)totalMotionArea / zoneArea;

            // Apply weighting based on area distribution;
            var intensity = areaRatio * 100f;

            // Adjust for multiple motion areas (more areas = higher intensity)
            if (motionAreas.Count > 1)
            {
                intensity *= (float)Math.Log(motionAreas.Count + 1);
            }

            return Math.Min(intensity, 100f);
        }

        private Rectangle CalculateBoundingBox(List<Rectangle> areas)
        {
            if (!areas.Any())
                return Rectangle.Empty;

            var minX = areas.Min(a => a.Left);
            var minY = areas.Min(a => a.Top);
            var maxX = areas.Max(a => a.Right);
            var maxY = areas.Max(a => a.Bottom);

            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        private int EstimateObjectCount(List<Rectangle> areas)
        {
            // Simple estimation - can be enhanced with clustering;
            if (!areas.Any())
                return 0;

            // Group overlapping or nearby rectangles;
            var groups = new List<List<Rectangle>>();

            foreach (var area in areas)
            {
                var added = false;

                foreach (var group in groups)
                {
                    if (group.Any(g => AreAreasConnected(g, area)))
                    {
                        group.Add(area);
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    groups.Add(new List<Rectangle> { area });
                }
            }

            return groups.Count;
        }

        private bool AreAreasConnected(Rectangle a, Rectangle b)
        {
            // Check if areas overlap or are within proximity;
            var proximity = _config.ObjectProximityThreshold;
            var expandedA = new Rectangle(
                a.X - proximity, a.Y - proximity,
                a.Width + proximity * 2, a.Height + proximity * 2);

            return expandedA.IntersectsWith(b);
        }

        private float CalculateConfidence(List<Rectangle> areas, float intensity)
        {
            var baseConfidence = intensity / 100f;

            // Higher confidence for multiple consistent areas;
            if (areas.Count >= 2)
            {
                baseConfidence *= 1.2f;
            }

            // Adjust based on area sizes (very small areas might be noise)
            var avgSize = areas.Average(a => a.Width * a.Height);
            if (avgSize < _config.MinMotionArea)
            {
                baseConfidence *= 0.7f;
            }

            return Math.Min(baseConfidence, 1.0f);
        }

        private async Task ProcessMotionEventsAsync(List<MotionEvent> events)
        {
            if (!events.Any())
                return;

            foreach (var motionEvent in events)
            {
                // Add to event queue for async processing;
                _eventQueue.Add(motionEvent);

                // Raise immediate event;
                RaiseMotionDetectedEvent(motionEvent);

                // Update last motion time;
                _lastMotionTime = DateTime.UtcNow;

                // Log the event;
                _logger.LogInformation("Motion detected in zone {ZoneName}: Intensity={Intensity}, Confidence={Confidence}",
                    motionEvent.ZoneName, motionEvent.MotionIntensity, motionEvent.Confidence);
            }

            // Process event queue asynchronously;
            await Task.Run(() => ProcessEventQueueAsync());
        }

        private void RaiseMotionDetectedEvent(MotionEvent motionEvent)
        {
            var args = new MotionDetectedEventArgs;
            {
                MotionEvent = motionEvent,
                DetectorId = this.GetHashCode().ToString(),
                Timestamp = DateTime.UtcNow,
                ZoneCount = _surveillanceZones.Count,
                FrameNumber = _frameCount;
            };

            OnMotionDetected?.Invoke(this, args);
        }

        private void HandleZoneActivityChanged(object sender, ZoneActivityEventArgs e)
        {
            var args = new MotionZoneEventArgs;
            {
                ZoneId = e.ZoneId,
                ZoneName = e.ZoneName,
                ActivityLevel = e.ActivityLevel,
                IsActive = e.IsActive,
                LastActivityTime = e.LastActivityTime,
                TotalActivities = e.TotalActivities;
            };

            OnZoneActivityChanged?.Invoke(this, args);
        }

        private async Task ProcessFramesAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting frame processing loop");

            while (!cancellationToken.IsCancellationRequested && _isProcessing)
            {
                try
                {
                    if (_frameQueue.TryDequeue(out var frame))
                    {
                        await ProcessFrameAsync(frame);
                    }
                    else;
                    {
                        // No frames to process, small delay;
                        await Task.Delay(10, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in frame processing loop");
                    await Task.Delay(100, cancellationToken);
                }
            }

            _logger.LogInformation("Frame processing loop stopped");
        }

        private async Task ProcessEventQueueAsync()
        {
            while (_eventQueue.TryTake(out var motionEvent, TimeSpan.FromMilliseconds(100)))
            {
                try
                {
                    // Additional event processing can be added here;
                    // For example: persistence, notifications, analytics;

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing motion event {EventId}", motionEvent.EventId);
                }
            }
        }

        /// <summary>
        /// Adds a surveillance zone;
        /// </summary>
        public void AddZone(SurveillanceZone zone)
        {
            if (zone == null)
                throw new ArgumentNullException(nameof(zone));

            lock (_syncLock)
            {
                if (_surveillanceZones.Any(z => z.Id == zone.Id))
                    throw new ArgumentException($"Zone with ID {zone.Id} already exists");

                zone.OnActivityChanged += HandleZoneActivityChanged;
                _surveillanceZones.Add(zone);

                _logger.LogInformation("Added surveillance zone: {ZoneName} (ID: {ZoneId})",
                    zone.Name, zone.Id);
            }
        }

        /// <summary>
        /// Removes a surveillance zone;
        /// </summary>
        public void RemoveZone(string zoneId)
        {
            lock (_syncLock)
            {
                var zone = _surveillanceZones.FirstOrDefault(z => z.Id == zoneId);
                if (zone == null)
                    return;

                zone.OnActivityChanged -= HandleZoneActivityChanged;
                _surveillanceZones.Remove(zone);

                _logger.LogInformation("Removed surveillance zone: {ZoneName} (ID: {ZoneId})",
                    zone.Name, zone.Id);
            }
        }

        /// <summary>
        /// Updates zone sensitivity;
        /// </summary>
        public void UpdateZoneSensitivity(string zoneId, float sensitivity)
        {
            lock (_syncLock)
            {
                var zone = _surveillanceZones.FirstOrDefault(z => z.Id == zoneId);
                if (zone == null)
                    throw new ArgumentException($"Zone with ID {zoneId} not found");

                zone.Sensitivity = Math.Clamp(sensitivity, 0.1f, 100f);

                _logger.LogInformation("Updated sensitivity for zone {ZoneName}: {Sensitivity}",
                    zone.Name, sensitivity);
            }
        }

        /// <summary>
        /// Switches to a different motion detection algorithm;
        /// </summary>
        public void SwitchAlgorithm(MotionAlgorithmType algorithmType)
        {
            lock (_syncLock)
            {
                if (!_algorithms.TryGetValue(algorithmType, out var algorithm))
                    throw new ArgumentException($"Algorithm {algorithmType} not supported");

                _currentAlgorithm = algorithm;

                _logger.LogInformation("Switched motion detection algorithm to {Algorithm}",
                    algorithmType);
            }
        }

        private async Task SwitchToFallbackAlgorithmAsync()
        {
            try
            {
                // Switch to a simpler, more robust algorithm;
                var fallback = MotionAlgorithmType.FrameDifference;
                SwitchAlgorithm(fallback);

                _logger.LogWarning("Switched to fallback algorithm: {Algorithm}", fallback);

                // Attempt to recalibrate;
                await CalibrateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to switch to fallback algorithm");
            }
        }

        /// <summary>
        /// Calibrates the motion detector with current environment;
        /// </summary>
        public async Task CalibrateAsync()
        {
            _logger.LogInformation("Starting motion detector calibration...");

            try
            {
                // Collect baseline frames for calibration;
                var baselineFrames = await CollectBaselineFramesAsync();

                // Perform calibration;
                _calibrationData = await _currentAlgorithm.CalibrateAsync(baselineFrames);

                // Update zones with calibration data;
                foreach (var zone in _surveillanceZones)
                {
                    zone.Calibrate(_calibrationData);
                }

                _logger.LogInformation("Motion detector calibration completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Motion detector calibration failed");
                throw new CalibrationException("Failed to calibrate motion detector", ex);
            }
        }

        private async Task<List<VideoFrame>> CollectBaselineFramesAsync()
        {
            var frames = new List<VideoFrame>();
            var collectionTime = TimeSpan.FromSeconds(_config.CalibrationDuration);

            _logger.LogInformation("Collecting baseline frames for {Duration} seconds...",
                collectionTime.TotalSeconds);

            // In a real implementation, this would collect actual frames;
            // For now, we'll simulate with empty frames;
            await Task.Delay(collectionTime);

            _logger.LogInformation("Baseline frame collection completed");
            return frames;
        }

        private void UpdatePerformanceMetrics(long processingTimeMs, MotionAnalysisResult result)
        {
            lock (_syncLock)
            {
                _metrics.LastProcessingTimeMs = processingTimeMs;
                _metrics.AverageProcessingTimeMs =
                    (_metrics.AverageProcessingTimeMs * (_metrics.TotalFramesProcessed - 1) + processingTimeMs)
                    / _metrics.TotalFramesProcessed;

                _metrics.LastMotionIntensity = result.MotionIntensity;
                _metrics.TotalMotionEvents += result.MotionAreas.Count;

                // Update FPS;
                var now = DateTime.UtcNow;
                var elapsed = (now - _metrics.LastMetricsUpdate).TotalSeconds;

                if (elapsed >= 1.0)
                {
                    _metrics.CurrentFps = (int)(_metrics.FramesSinceLastUpdate / elapsed);
                    _metrics.FramesSinceLastUpdate = 0;
                    _metrics.LastMetricsUpdate = now;
                }
                else;
                {
                    _metrics.FramesSinceLastUpdate++;
                }
            }
        }

        /// <summary>
        /// Gets current performance metrics;
        /// </summary>
        public PerformanceMetrics GetPerformanceMetrics()
        {
            lock (_syncLock)
            {
                return _metrics.Clone();
            }
        }

        /// <summary>
        /// Gets current surveillance zones;
        /// </summary>
        public IReadOnlyList<SurveillanceZone> GetZones()
        {
            lock (_syncLock)
            {
                return _surveillanceZones.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Gets current motion detection status;
        /// </summary>
        public MotionDetectionStatus GetStatus()
        {
            return new MotionDetectionStatus;
            {
                IsActive = _isProcessing,
                IsInitialized = _isInitialized,
                CurrentAlgorithm = _currentAlgorithm.GetType().Name,
                ZoneCount = _surveillanceZones.Count,
                FrameCount = _frameCount,
                LastMotionTime = _lastMotionTime,
                Uptime = DateTime.UtcNow - (_processingTask?.CreationTime ?? DateTime.UtcNow)
            };
        }

        /// <summary>
        /// Resets the motion detector to initial state;
        /// </summary>
        public void Reset()
        {
            lock (_syncLock)
            {
                StopDetection();

                _frameQueue = new ConcurrentQueue<VideoFrame>();
                _eventQueue = new BlockingCollection<MotionEvent>();
                _processingTokenSource?.Dispose();
                _processingTokenSource = new CancellationTokenSource();

                _frameCount = 0;
                _lastMotionTime = DateTime.MinValue;
                _metrics = new PerformanceMetrics();

                foreach (var zone in _surveillanceZones)
                {
                    zone.Reset();
                }

                _logger.LogInformation("Motion detector reset");
            }
        }

        /// <summary>
        /// Performs cleanup and resource disposal;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopDetection();

                _processingTokenSource?.Dispose();
                _eventQueue?.Dispose();

                foreach (var algorithm in _algorithms.Values)
                {
                    if (algorithm is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }

                foreach (var zone in _surveillanceZones)
                {
                    zone.OnActivityChanged -= HandleZoneActivityChanged;
                    if (zone is IDisposable disposableZone)
                    {
                        disposableZone.Dispose();
                    }
                }

                _logger.LogInformation("Motion detector disposed");
            }
        }

        ~MotionDetector()
        {
            Dispose(false);
        }
    }

    #region Supporting Types and Interfaces;

    public interface IMotionDetector;
    {
        event EventHandler<MotionDetectedEventArgs> OnMotionDetected;
        event EventHandler<MotionZoneEventArgs> OnZoneActivityChanged;

        void StartDetection();
        void StopDetection();
        Task<MotionDetectionResult> ProcessFrameAsync(VideoFrame frame);
        void AddZone(SurveillanceZone zone);
        void RemoveZone(string zoneId);
        void UpdateZoneSensitivity(string zoneId, float sensitivity);
        void SwitchAlgorithm(MotionAlgorithmType algorithmType);
        Task CalibrateAsync();
        PerformanceMetrics GetPerformanceMetrics();
        IReadOnlyList<SurveillanceZone> GetZones();
        MotionDetectionStatus GetStatus();
        void Reset();
    }

    public class VideoFrame;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public byte[] Data { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public int SequenceNumber { get; set; }
        public float FrameRate { get; set; }
        public string CameraId { get; set; }
        public Dictionary<string, object> Metadata { get; } = new Dictionary<string, object>();
    }

    public class MotionEvent;
    {
        public Guid EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public float MotionIntensity { get; set; }
        public List<Rectangle> MotionAreas { get; set; } = new List<Rectangle>();
        public Rectangle BoundingBox { get; set; }
        public int ObjectCount { get; set; }
        public float Confidence { get; set; }
        public Dictionary<string, object> AdditionalData { get; } = new Dictionary<string, object>();
    }

    public class MotionDetectedEventArgs : EventArgs;
    {
        public MotionEvent MotionEvent { get; set; }
        public string DetectorId { get; set; }
        public DateTime Timestamp { get; set; }
        public int ZoneCount { get; set; }
        public int FrameNumber { get; set; }
    }

    public class MotionZoneEventArgs : EventArgs;
    {
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public float ActivityLevel { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastActivityTime { get; set; }
        public int TotalActivities { get; set; }
    }

    public class DetectionStatusChangedEventArgs : EventArgs;
    {
        public bool IsActive { get; }
        public string StatusMessage { get; }

        public DetectionStatusChangedEventArgs(bool isActive, string statusMessage)
        {
            IsActive = isActive;
            StatusMessage = statusMessage;
        }
    }

    public class MotionDetectionResult;
    {
        public Guid FrameId { get; set; }
        public DateTime Timestamp { get; set; }
        public bool MotionDetected { get; set; }
        public List<MotionEvent> MotionEvents { get; set; } = new List<MotionEvent>();
        public long ProcessingTimeMs { get; set; }
        public string AlgorithmUsed { get; set; }
        public Exception Error { get; set; }
    }

    public class MotionDetectionStatus;
    {
        public bool IsActive { get; set; }
        public bool IsInitialized { get; set; }
        public string CurrentAlgorithm { get; set; }
        public int ZoneCount { get; set; }
        public int FrameCount { get; set; }
        public DateTime LastMotionTime { get; set; }
        public TimeSpan Uptime { get; set; }
    }

    public class PerformanceMetrics;
    {
        public int TotalFramesProcessed { get; set; }
        public int TotalMotionEvents { get; set; }
        public long LastProcessingTimeMs { get; set; }
        public long AverageProcessingTimeMs { get; set; }
        public int CurrentFps { get; set; }
        public float LastMotionIntensity { get; set; }
        public int FramesSinceLastUpdate { get; set; }
        public DateTime LastMetricsUpdate { get; set; } = DateTime.UtcNow;

        public PerformanceMetrics Clone()
        {
            return (PerformanceMetrics)this.MemberwiseClone();
        }
    }

    public class MotionDetectionConfig;
    {
        public static MotionDetectionConfig Default => new MotionDetectionConfig;
        {
            FrameWidth = 1920,
            FrameHeight = 1080,
            DefaultAlgorithm = MotionAlgorithmType.FrameDifference,
            MotionThreshold = 1.0f,
            DefaultSensitivity = 5.0f,
            DefaultAlertThreshold = 10.0f,
            MinMotionArea = 100,
            ObjectProximityThreshold = 20,
            CalibrationDuration = 5,
            EnableNoiseFiltering = true,
            EnableAdaptiveSensitivity = true;
        };

        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public MotionAlgorithmType DefaultAlgorithm { get; set; }
        public float MotionThreshold { get; set; }
        public float DefaultSensitivity { get; set; }
        public float DefaultAlertThreshold { get; set; }
        public int MinMotionArea { get; set; }
        public int ObjectProximityThreshold { get; set; }
        public int CalibrationDuration { get; set; }
        public bool EnableNoiseFiltering { get; set; }
        public bool EnableAdaptiveSensitivity { get; set; }
        public List<ZoneConfig> Zones { get; set; } = new List<ZoneConfig>();
    }

    public class ZoneConfig;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Rectangle Bounds { get; set; }
        public float Sensitivity { get; set; }
        public float AlertThreshold { get; set; }
    }

    public enum MotionAlgorithmType;
    {
        FrameDifference,
        BackgroundSubtraction,
        OpticalFlow,
        AdaptiveThresholding,
        DeepLearning;
    }

    #endregion;

    #region Exceptions;

    public class MotionDetectionException : Exception
    {
        public MotionDetectionException() { }
        public MotionDetectionException(string message) : base(message) { }
        public MotionDetectionException(string message, Exception inner) : base(message, inner) { }
    }

    public class MotionDetectionInitializationException : MotionDetectionException;
    {
        public MotionDetectionInitializationException() { }
        public MotionDetectionInitializationException(string message) : base(message) { }
        public MotionDetectionInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    public class MotionProcessingException : MotionDetectionException;
    {
        public MotionProcessingException() { }
        public MotionProcessingException(string message) : base(message) { }
        public MotionProcessingException(string message, Exception inner) : base(message, inner) { }
    }

    public class CalibrationException : MotionDetectionException;
    {
        public CalibrationException() { }
        public CalibrationException(string message) : base(message) { }
        public CalibrationException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
