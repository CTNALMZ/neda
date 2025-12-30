using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Common.Utilities;
using NEDA.Monitoring.Diagnostics;
using NEDA.AI.PatternRecognition;
using NEDA.AI.MachineLearning;
using NEDA.SecurityModules.Monitoring;
using NEDA.Communication.EmotionalIntelligence;
using NEDA.Brain.MemorySystem;

namespace NEDA.RealTimeMonitoring.PresenceAnalysis;
{
    /// <summary>
    /// Activity types recognized by the analyzer;
    /// </summary>
    public enum ActivityType;
    {
        NormalMovement = 0,
        AggressiveMotion = 1,
        SuspiciousLoitering = 2,
        FallingMotion = 3,
        Running = 4,
        Crawling = 5,
        Jumping = 6,
        Fighting = 7,
        TheftAttempt = 8,
        Vandalism = 9,
        UnauthorizedAccess = 10,
        EquipmentTampering = 11,
        CrowdFormation = 12,
        EvacuationMovement = 13,
        MedicalEmergency = 14;
    }

    /// <summary>
    /// Activity severity levels;
    /// </summary>
    public enum ActivitySeverity;
    {
        Informational = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    /// <summary>
    /// Activity analysis result;
    /// </summary>
    public class ActivityAnalysisResult;
    {
        public string ActivityId { get; set; }
        public ActivityType ActivityType { get; set; }
        public ActivitySeverity Severity { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, double> PatternScores { get; set; }
        public List<string> InvolvedEntities { get; set; }
        public string Location { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public List<string> RecommendedActions { get; set; }
        public bool RequiresImmediateAttention { get; set; }

        public ActivityAnalysisResult()
        {
            PatternScores = new Dictionary<string, double>();
            InvolvedEntities = new List<string>();
            Metadata = new Dictionary<string, object>();
            RecommendedActions = new List<string>();
            ActivityId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Real-time activity analysis configuration;
    /// </summary>
    public class ActivityAnalyzerConfig;
    {
        public double SuspiciousDurationThreshold { get; set; } = 300; // 5 minutes;
        public double AggressionVelocityThreshold { get; set; } = 5.0; // m/s;
        public double FallingAccelerationThreshold { get; set; } = 9.0; // m/s²
        public int MinimumEntitiesForCrowd { get; set; } = 5;
        public TimeSpan AnalysisWindow { get; set; } = TimeSpan.FromSeconds(10);
        public double PatternRecognitionThreshold { get; set; } = 0.75;
        public bool EnableBehavioralAnalysis { get; set; } = true;
        public bool EnablePredictiveAnalysis { get; set; } = false;
        public List<string> RestrictedZones { get; set; }
        public Dictionary<ActivityType, ActivitySeverity> SeverityMapping { get; set; }

        public ActivityAnalyzerConfig()
        {
            RestrictedZones = new List<string>();
            SeverityMapping = new Dictionary<ActivityType, ActivitySeverity>
            {
                { ActivityType.NormalMovement, ActivitySeverity.Informational },
                { ActivityType.AggressiveMotion, ActivitySeverity.High },
                { ActivityType.SuspiciousLoitering, ActivitySeverity.Medium },
                { ActivityType.FallingMotion, ActivitySeverity.Critical },
                { ActivityType.Fighting, ActivitySeverity.Critical },
                { ActivityType.TheftAttempt, ActivitySeverity.High },
                { ActivityType.MedicalEmergency, ActivitySeverity.Critical }
            };
        }
    }

    /// <summary>
    /// Motion data point for activity analysis;
    /// </summary>
    public class MotionDataPoint;
    {
        public DateTime Timestamp { get; set; }
        public string EntityId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double VelocityZ { get; set; }
        public double Acceleration { get; set; }
        public double Jerk { get; set; }
        public Dictionary<string, double> SensorReadings { get; set; }
        public string ZoneId { get; set; }
        public double Confidence { get; set; }

        public MotionDataPoint()
        {
            SensorReadings = new Dictionary<string, double>();
        }
    }

    /// <summary>
    /// Interface for activity pattern recognition;
    /// </summary>
    public interface IActivityPatternRecognizer;
    {
        Task<ActivityType> RecognizePatternAsync(List<MotionDataPoint> motionData);
        Task<double> CalculatePatternScoreAsync(ActivityType activityType, List<MotionDataPoint> motionData);
        Task TrainModelAsync(List<TrainingSample> trainingData);
    }

    /// <summary>
    /// Training sample for activity recognition;
    /// </summary>
    public class TrainingSample;
    {
        public List<MotionDataPoint> MotionData { get; set; }
        public ActivityType Label { get; set; }
        public Dictionary<string, object> Features { get; set; }

        public TrainingSample()
        {
            MotionData = new List<MotionDataPoint>();
            Features = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Advanced real-time activity analyzer with machine learning and pattern recognition;
    /// </summary>
    public class ActivityAnalyzer : IActivityAnalyzer, IDisposable;
    {
        private readonly ILogger<ActivityAnalyzer> _logger;
        private readonly IActivityPatternRecognizer _patternRecognizer;
        private readonly ISecurityMonitor _securityMonitor;
        private readonly IEmotionDetector _emotionDetector;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ActivityAnalyzerConfig _config;
        private readonly Dictionary<string, List<MotionDataPoint>> _motionHistory;
        private readonly Dictionary<string, ActivityAnalysisResult> _ongoingActivities;
        private readonly Dictionary<string, DateTime> _entityLastSeen;
        private readonly MLModel _activityModel;
        private readonly PatternDetector _patternDetector;
        private readonly object _syncLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        private Task _backgroundAnalysisTask;
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// Event triggered when a significant activity is detected;
        /// </summary>
        public event EventHandler<ActivityDetectedEventArgs> ActivityDetected;

        /// <summary>
        /// Event triggered when activity analysis is completed;
        /// </summary>
        public event EventHandler<ActivityAnalysisCompletedEventArgs> AnalysisCompleted;

        /// <summary>
        /// Event triggered when anomaly is detected;
        /// </summary>
        public event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;

        /// <summary>
        /// Initialize a new instance of ActivityAnalyzer;
        /// </summary>
        public ActivityAnalyzer(
            ILogger<ActivityAnalyzer> logger,
            IActivityPatternRecognizer patternRecognizer,
            ISecurityMonitor securityMonitor,
            IEmotionDetector emotionDetector,
            IShortTermMemory shortTermMemory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _patternRecognizer = patternRecognizer ?? throw new ArgumentNullException(nameof(patternRecognizer));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));

            _config = new ActivityAnalyzerConfig();
            _motionHistory = new Dictionary<string, List<MotionDataPoint>>();
            _ongoingActivities = new Dictionary<string, ActivityAnalysisResult>();
            _entityLastSeen = new Dictionary<string, DateTime>();
            _activityModel = new MLModel("ActivityRecognition");
            _patternDetector = new PatternDetector();

            _cancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("ActivityAnalyzer initialized with advanced pattern recognition");
        }

        /// <summary>
        /// Configure the activity analyzer with custom settings;
        /// </summary>
        public void Configure(Action<ActivityAnalyzerConfig> configureAction)
        {
            if (configureAction == null)
                throw new ArgumentNullException(nameof(configureAction));

            lock (_syncLock)
            {
                configureAction(_config);
                _logger.LogInformation("ActivityAnalyzer configuration updated");
            }
        }

        /// <summary>
        /// Start real-time activity analysis;
        /// </summary>
        public async Task StartAnalysisAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("Activity analyzer is already running");
                return;
            }

            try
            {
                await InitializeModelAsync();
                _backgroundAnalysisTask = Task.Run(() => BackgroundAnalysisLoop(_cancellationTokenSource.Token));
                _isInitialized = true;

                _logger.LogInformation("Activity analyzer started successfully");

                // Start monitoring for anomalies;
                _ = Task.Run(async () => await MonitorForAnomaliesAsync(_cancellationTokenSource.Token));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start activity analyzer");
                throw new ActivityAnalyzerException("Failed to start activity analyzer", ex);
            }
        }

        /// <summary>
        /// Stop activity analysis;
        /// </summary>
        public async Task StopAnalysisAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                _cancellationTokenSource.Cancel();

                if (_backgroundAnalysisTask != null && !_backgroundAnalysisTask.IsCompleted)
                {
                    await _backgroundAnalysisTask;
                }

                _isInitialized = false;
                ClearAllData();

                _logger.LogInformation("Activity analyzer stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping activity analyzer");
            }
        }

        /// <summary>
        /// Process motion data for activity analysis;
        /// </summary>
        public async Task<ActivityAnalysisResult> AnalyzeMotionAsync(MotionDataPoint motionData)
        {
            if (motionData == null)
                throw new ArgumentNullException(nameof(motionData));

            if (!_isInitialized)
                throw new InvalidOperationException("Activity analyzer is not initialized. Call StartAnalysisAsync first.");

            try
            {
                // Store motion data in history;
                StoreMotionData(motionData);

                // Update entity tracking;
                UpdateEntityTracking(motionData);

                // Analyze motion patterns;
                var motionHistory = GetRecentMotionHistory(motionData.EntityId);
                var patternAnalysis = await AnalyzeMotionPatternAsync(motionHistory);

                // Check for restricted zone violations;
                var zoneViolation = CheckZoneViolation(motionData);

                // Detect anomalies;
                var anomalyScore = await DetectAnomaliesAsync(motionData, motionHistory);

                // Perform behavioral analysis if enabled;
                Dictionary<string, double> behavioralScores = null;
                if (_config.EnableBehavioralAnalysis)
                {
                    behavioralScores = await PerformBehavioralAnalysisAsync(motionHistory);
                }

                // Create analysis result;
                var result = new ActivityAnalysisResult;
                {
                    ActivityType = patternAnalysis.ActivityType,
                    Confidence = patternAnalysis.Confidence,
                    Duration = CalculateActivityDuration(motionData.EntityId),
                    Location = motionData.ZoneId,
                    PatternScores = patternAnalysis.PatternScores;
                };

                // Apply severity mapping;
                result.Severity = DetermineSeverity(result.ActivityType, anomalyScore, zoneViolation);

                // Add involved entities;
                result.InvolvedEntities.Add(motionData.EntityId);
                result.InvolvedEntities.AddRange(FindNearbyEntities(motionData));

                // Generate recommendations;
                result.RecommendedActions = GenerateRecommendations(result);
                result.RequiresImmediateAttention = result.Severity >= ActivitySeverity.High;

                // Store ongoing activity if significant;
                if (result.Severity >= ActivitySeverity.Medium)
                {
                    StoreOngoingActivity(motionData.EntityId, result);
                }

                // Trigger events;
                OnActivityDetected(new ActivityDetectedEventArgs;
                {
                    ActivityResult = result,
                    MotionData = motionData,
                    Timestamp = DateTime.UtcNow;
                });

                if (anomalyScore > 0.8)
                {
                    OnAnomalyDetected(new AnomalyDetectedEventArgs;
                    {
                        ActivityResult = result,
                        AnomalyScore = anomalyScore,
                        AnomalyType = "Motion Pattern Anomaly",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing motion data for entity {EntityId}", motionData.EntityId);
                throw new ActivityAnalysisException("Failed to analyze motion data", ex, motionData.EntityId);
            }
        }

        /// <summary>
        /// Analyze batch of motion data points;
        /// </summary>
        public async Task<List<ActivityAnalysisResult>> AnalyzeBatchAsync(List<MotionDataPoint> motionDataBatch)
        {
            if (motionDataBatch == null || !motionDataBatch.Any())
                return new List<ActivityAnalysisResult>();

            var results = new List<ActivityAnalysisResult>();
            var tasks = new List<Task<ActivityAnalysisResult>>();

            foreach (var motionData in motionDataBatch)
            {
                tasks.Add(AnalyzeMotionAsync(motionData));
            }

            var completedTasks = await Task.WhenAll(tasks);
            results.AddRange(completedTasks);

            // Perform cross-entity analysis;
            if (results.Count > 1)
            {
                await PerformCrossEntityAnalysisAsync(results);
            }

            return results;
        }

        /// <summary>
        /// Get ongoing activities in specified zone;
        /// </summary>
        public List<ActivityAnalysisResult> GetOngoingActivities(string zoneId = null)
        {
            lock (_syncLock)
            {
                if (string.IsNullOrEmpty(zoneId))
                {
                    return _ongoingActivities.Values.ToList();
                }

                return _ongoingActivities.Values;
                    .Where(a => a.Location == zoneId)
                    .ToList();
            }
        }

        /// <summary>
        /// Get activity statistics;
        /// </summary>
        public ActivityStatistics GetStatistics(TimeSpan timeWindow)
        {
            var cutoffTime = DateTime.UtcNow - timeWindow;

            lock (_syncLock)
            {
                var recentActivities = _ongoingActivities.Values;
                    .Where(a => a.Timestamp >= cutoffTime)
                    .ToList();

                return new ActivityStatistics;
                {
                    TotalActivities = recentActivities.Count,
                    CriticalActivities = recentActivities.Count(a => a.Severity == ActivitySeverity.Critical),
                    HighPriorityActivities = recentActivities.Count(a => a.Severity == ActivitySeverity.High),
                    AverageConfidence = recentActivities.Any() ? recentActivities.Average(a => a.Confidence) : 0,
                    MostCommonActivity = recentActivities;
                        .GroupBy(a => a.ActivityType)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key ?? ActivityType.NormalMovement,
                    ActivityTrend = CalculateActivityTrend(recentActivities)
                };
            }
        }

        /// <summary>
        /// Train the activity recognition model with new data;
        /// </summary>
        public async Task TrainModelAsync(List<TrainingSample> trainingData)
        {
            if (trainingData == null || !trainingData.Any())
                throw new ArgumentException("Training data cannot be null or empty", nameof(trainingData));

            try
            {
                _logger.LogInformation("Starting model training with {Count} samples", trainingData.Count);

                await _patternRecognizer.TrainModelAsync(trainingData);

                // Update internal model;
                var features = trainingData.SelectMany(t => t.MotionData)
                    .Select(m => new { m.VelocityX, m.VelocityY, m.Acceleration })
                    .ToList();

                await _activityModel.UpdateAsync(features);

                _logger.LogInformation("Model training completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Model training failed");
                throw new ModelTrainingException("Failed to train activity recognition model", ex);
            }
        }

        /// <summary>
        /// Get motion history for specific entity;
        /// </summary>
        public List<MotionDataPoint> GetMotionHistory(string entityId, TimeSpan? timeWindow = null)
        {
            lock (_syncLock)
            {
                if (!_motionHistory.ContainsKey(entityId))
                    return new List<MotionDataPoint>();

                var history = _motionHistory[entityId];

                if (timeWindow.HasValue)
                {
                    var cutoffTime = DateTime.UtcNow - timeWindow.Value;
                    return history.Where(m => m.Timestamp >= cutoffTime).ToList();
                }

                return history.ToList();
            }
        }

        /// <summary>
        /// Clear all stored data;
        /// </summary>
        public void ClearAllData()
        {
            lock (_syncLock)
            {
                _motionHistory.Clear();
                _ongoingActivities.Clear();
                _entityLastSeen.Clear();

                _logger.LogInformation("All activity data cleared");
            }
        }

        #region Private Methods;

        private async Task InitializeModelAsync()
        {
            try
            {
                // Load pre-trained model if available;
                if (await _activityModel.LoadAsync("activity_model"))
                {
                    _logger.LogInformation("Pre-trained activity model loaded");
                }
                else;
                {
                    // Initialize with default patterns;
                    await InitializeDefaultPatternsAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load pre-trained model, using default patterns");
                await InitializeDefaultPatternsAsync();
            }
        }

        private async Task InitializeDefaultPatternsAsync()
        {
            var defaultPatterns = new List<TrainingSample>
            {
                CreateTrainingSample(ActivityType.NormalMovement, 1.0, 0.5),
                CreateTrainingSample(ActivityType.Running, 3.0, 2.0),
                CreateTrainingSample(ActivityType.FallingMotion, 0.0, 9.8)
            };

            await TrainModelAsync(defaultPatterns);
        }

        private TrainingSample CreateTrainingSample(ActivityType activityType, double velocity, double acceleration)
        {
            return new TrainingSample;
            {
                Label = activityType,
                MotionData = new List<MotionDataPoint>
                {
                    new MotionDataPoint;
                    {
                        VelocityX = velocity,
                        VelocityY = velocity * 0.5,
                        Acceleration = acceleration,
                        Timestamp = DateTime.UtcNow;
                    }
                }
            };
        }

        private async Task BackgroundAnalysisLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                    // Clean up old data;
                    CleanupOldData();

                    // Check for suspicious patterns;
                    await CheckForSuspiciousPatternsAsync();

                    // Update ongoing activities;
                    UpdateOngoingActivities();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in background analysis loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }

        private void StoreMotionData(MotionDataPoint motionData)
        {
            lock (_syncLock)
            {
                if (!_motionHistory.ContainsKey(motionData.EntityId))
                {
                    _motionHistory[motionData.EntityId] = new List<MotionDataPoint>();
                }

                _motionHistory[motionData.EntityId].Add(motionData);

                // Keep only recent data;
                var cutoffTime = DateTime.UtcNow - _config.AnalysisWindow;
                _motionHistory[motionData.EntityId] = _motionHistory[motionData.EntityId]
                    .Where(m => m.Timestamp >= cutoffTime)
                    .ToList();
            }
        }

        private void UpdateEntityTracking(MotionDataPoint motionData)
        {
            lock (_syncLock)
            {
                _entityLastSeen[motionData.EntityId] = motionData.Timestamp;
            }
        }

        private List<MotionDataPoint> GetRecentMotionHistory(string entityId)
        {
            lock (_syncLock)
            {
                if (!_motionHistory.ContainsKey(entityId))
                    return new List<MotionDataPoint>();

                var cutoffTime = DateTime.UtcNow - _config.AnalysisWindow;
                return _motionHistory[entityId]
                    .Where(m => m.Timestamp >= cutoffTime)
                    .ToList();
            }
        }

        private async Task<(ActivityType ActivityType, double Confidence, Dictionary<string, double> PatternScores)>
            AnalyzeMotionPatternAsync(List<MotionDataPoint> motionHistory)
        {
            if (!motionHistory.Any())
                return (ActivityType.NormalMovement, 0.0, new Dictionary<string, double>());

            try
            {
                // Use pattern recognizer;
                var activityType = await _patternRecognizer.RecognizePatternAsync(motionHistory);
                var confidence = await _patternRecognizer.CalculatePatternScoreAsync(activityType, motionHistory);

                // Calculate additional pattern scores;
                var patternScores = new Dictionary<string, double>
                {
                    { "VelocityPattern", CalculateVelocityPatternScore(motionHistory) },
                    { "AccelerationPattern", CalculateAccelerationPatternScore(motionHistory) },
                    { "TrajectoryPattern", CalculateTrajectoryPatternScore(motionHistory) }
                };

                // Combine scores;
                var combinedConfidence = (confidence + patternScores.Values.Average()) / 2.0;

                return (activityType, combinedConfidence, patternScores);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pattern recognition failed, using basic analysis");
                return await PerformBasicMotionAnalysisAsync(motionHistory);
            }
        }

        private async Task<(ActivityType ActivityType, double Confidence, Dictionary<string, double> PatternScores)>
            PerformBasicMotionAnalysisAsync(List<MotionDataPoint> motionHistory)
        {
            var averageVelocity = motionHistory.Average(m => Math.Sqrt(m.VelocityX * m.VelocityX + m.VelocityY * m.VelocityY));
            var maxAcceleration = motionHistory.Max(m => m.Acceleration);
            var directionChanges = CalculateDirectionChanges(motionHistory);

            ActivityType activityType;
            double confidence;

            // Basic rule-based classification;
            if (maxAcceleration >= _config.FallingAccelerationThreshold)
            {
                activityType = ActivityType.FallingMotion;
                confidence = 0.85;
            }
            else if (averageVelocity >= _config.AggressionVelocityThreshold)
            {
                activityType = ActivityType.AggressiveMotion;
                confidence = 0.75;
            }
            else if (directionChanges > 10 && averageVelocity < 1.0)
            {
                activityType = ActivityType.SuspiciousLoitering;
                confidence = 0.65;
            }
            else;
            {
                activityType = ActivityType.NormalMovement;
                confidence = 0.95;
            }

            return (activityType, confidence, new Dictionary<string, double>());
        }

        private double CalculateVelocityPatternScore(List<MotionDataPoint> motionHistory)
        {
            if (!motionHistory.Any())
                return 0.0;

            var velocities = motionHistory.Select(m => Math.Sqrt(m.VelocityX * m.VelocityX + m.VelocityY * m.VelocityY)).ToList();
            var mean = velocities.Average();
            var stdDev = Math.Sqrt(velocities.Select(v => Math.Pow(v - mean, 2)).Average());

            // Normalize to 0-1 range;
            return 1.0 / (1.0 + stdDev);
        }

        private double CalculateAccelerationPatternScore(List<MotionDataPoint> motionHistory)
        {
            if (!motionHistory.Any())
                return 0.0;

            var accelerations = motionHistory.Select(m => m.Acceleration).ToList();
            var jerk = motionHistory.Select(m => m.Jerk).ToList();

            // Calculate pattern consistency;
            var accelPattern = Math.Abs(accelerations.Max() - accelerations.Min());
            return 1.0 / (1.0 + accelPattern);
        }

        private double CalculateTrajectoryPatternScore(List<MotionDataPoint> motionHistory)
        {
            if (motionHistory.Count < 2)
                return 0.0;

            var positions = motionHistory.Select(m => new { m.X, m.Y }).ToList();
            var directionChanges = CalculateDirectionChanges(motionHistory);

            // Calculate trajectory smoothness;
            var smoothness = 1.0 / (1.0 + directionChanges);
            return smoothness;
        }

        private int CalculateDirectionChanges(List<MotionDataPoint> motionHistory)
        {
            if (motionHistory.Count < 3)
                return 0;

            int changes = 0;
            for (int i = 2; i < motionHistory.Count; i++)
            {
                var dx1 = motionHistory[i - 1].X - motionHistory[i - 2].X;
                var dy1 = motionHistory[i - 1].Y - motionHistory[i - 2].Y;
                var dx2 = motionHistory[i].X - motionHistory[i - 1].X;
                var dy2 = motionHistory[i].Y - motionHistory[i - 1].Y;

                var angle1 = Math.Atan2(dy1, dx1);
                var angle2 = Math.Atan2(dy2, dx2);
                var angleDiff = Math.Abs(angle1 - angle2);

                if (angleDiff > Math.PI / 4) // 45 degrees;
                {
                    changes++;
                }
            }

            return changes;
        }

        private bool CheckZoneViolation(MotionDataPoint motionData)
        {
            if (_config.RestrictedZones == null || !_config.RestrictedZones.Any())
                return false;

            return _config.RestrictedZones.Contains(motionData.ZoneId);
        }

        private async Task<double> DetectAnomaliesAsync(MotionDataPoint motionData, List<MotionDataPoint> motionHistory)
        {
            try
            {
                var features = new Dictionary<string, object>
                {
                    { "velocity", Math.Sqrt(motionData.VelocityX * motionData.VelocityX + motionData.VelocityY * motionData.VelocityY) },
                    { "acceleration", motionData.Acceleration },
                    { "jerk", motionData.Jerk },
                    { "zone", motionData.ZoneId }
                };

                // Use pattern detector for anomaly detection;
                var anomalyScore = await _patternDetector.DetectAnomalyAsync(features);

                // Check for behavioral anomalies;
                if (motionHistory.Count > 10)
                {
                    var behaviorAnomaly = await DetectBehavioralAnomalyAsync(motionHistory);
                    anomalyScore = Math.Max(anomalyScore, behaviorAnomaly);
                }

                return anomalyScore;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anomaly detection failed");
                return 0.0;
            }
        }

        private async Task<double> DetectBehavioralAnomalyAsync(List<MotionDataPoint> motionHistory)
        {
            // Analyze behavioral patterns;
            var velocities = motionHistory.Select(m =>
                Math.Sqrt(m.VelocityX * m.VelocityX + m.VelocityY * m.VelocityY)).ToList();

            var accelerations = motionHistory.Select(m => m.Acceleration).ToList();

            // Calculate statistical anomalies;
            var meanVelocity = velocities.Average();
            var stdVelocity = Math.Sqrt(velocities.Select(v => Math.Pow(v - meanVelocity, 2)).Average());

            var meanAcceleration = accelerations.Average();
            var stdAcceleration = Math.Sqrt(accelerations.Select(a => Math.Pow(a - meanAcceleration, 2)).Average());

            // Detect outliers;
            var velocityAnomalies = velocities.Count(v => Math.Abs(v - meanVelocity) > 3 * stdVelocity);
            var accelerationAnomalies = accelerations.Count(a => Math.Abs(a - meanAcceleration) > 3 * stdAcceleration);

            var anomalyScore = (velocityAnomalies + accelerationAnomalies) / (double)(velocities.Count + accelerations.Count);

            return anomalyScore;
        }

        private async Task<Dictionary<string, double>> PerformBehavioralAnalysisAsync(List<MotionDataPoint> motionHistory)
        {
            var scores = new Dictionary<string, double>();

            if (!motionHistory.Any())
                return scores;

            try
            {
                // Analyze movement patterns;
                scores["MovementConsistency"] = CalculateMovementConsistency(motionHistory);
                scores["Predictability"] = CalculatePredictability(motionHistory);
                scores["IntentScore"] = await CalculateIntentScoreAsync(motionHistory);

                // Add emotional analysis if available;
                if (_emotionDetector != null)
                {
                    var emotionalState = await _emotionDetector.AnalyzeBehaviorAsync(motionHistory);
                    scores["EmotionalStability"] = emotionalState.StabilityScore;
                    scores["StressLevel"] = emotionalState.StressScore;
                }

                return scores;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Behavioral analysis failed");
                return scores;
            }
        }

        private double CalculateMovementConsistency(List<MotionDataPoint> motionHistory)
        {
            if (motionHistory.Count < 2)
                return 1.0;

            var velocityChanges = new List<double>();
            for (int i = 1; i < motionHistory.Count; i++)
            {
                var v1 = Math.Sqrt(
                    motionHistory[i - 1].VelocityX * motionHistory[i - 1].VelocityX +
                    motionHistory[i - 1].VelocityY * motionHistory[i - 1].VelocityY);

                var v2 = Math.Sqrt(
                    motionHistory[i].VelocityX * motionHistory[i].VelocityX +
                    motionHistory[i].VelocityY * motionHistory[i].VelocityY);

                velocityChanges.Add(Math.Abs(v2 - v1));
            }

            var avgChange = velocityChanges.Average();
            return 1.0 / (1.0 + avgChange);
        }

        private double CalculatePredictability(List<MotionDataPoint> motionHistory)
        {
            // Simplified predictability calculation;
            // In real implementation, this would use more sophisticated algorithms;
            var directionChanges = CalculateDirectionChanges(motionHistory);
            var totalPoints = motionHistory.Count;

            var predictability = 1.0 - (directionChanges / (double)totalPoints);
            return Math.Max(0, Math.Min(1, predictability));
        }

        private async Task<double> CalculateIntentScoreAsync(List<MotionDataPoint> motionHistory)
        {
            // Analyze movement intent based on trajectory and acceleration patterns;
            var trajectoryAnalysis = await AnalyzeTrajectoryIntentAsync(motionHistory);
            var accelerationAnalysis = AnalyzeAccelerationIntent(motionHistory);

            return (trajectoryAnalysis + accelerationAnalysis) / 2.0;
        }

        private async Task<double> AnalyzeTrajectoryIntentAsync(List<MotionDataPoint> motionHistory)
        {
            // Analyze if movement is goal-directed or random;
            var positions = motionHistory.Select(m => new { m.X, m.Y }).ToList();

            // Calculate linearity of path;
            var startPos = positions.First();
            var endPos = positions.Last();
            var directDistance = Math.Sqrt(
                Math.Pow(endPos.X - startPos.X, 2) +
                Math.Pow(endPos.Y - startPos.Y, 2));

            var actualDistance = 0.0;
            for (int i = 1; i < positions.Count; i++)
            {
                actualDistance += Math.Sqrt(
                    Math.Pow(positions[i].X - positions[i - 1].X, 2) +
                    Math.Pow(positions[i].Y - positions[i - 1].Y, 2));
            }

            if (actualDistance == 0)
                return 0.0;

            var linearity = directDistance / actualDistance;
            return linearity;
        }

        private double AnalyzeAccelerationIntent(List<MotionDataPoint> motionHistory)
        {
            // Analyze acceleration patterns for intent;
            var accelerations = motionHistory.Select(m => m.Acceleration).ToList();
            var positiveAccelerations = accelerations.Where(a => a > 0).ToList();
            var negativeAccelerations = accelerations.Where(a => a < 0).ToList();

            // Intent is stronger when accelerations are purposeful (not random)
            var accelerationRatio = positiveAccelerations.Count / (double)accelerations.Count;
            var decelerationRatio = negativeAccelerations.Count / (double)accelerations.Count;

            var intentScore = Math.Abs(accelerationRatio - decelerationRatio);
            return intentScore;
        }

        private TimeSpan CalculateActivityDuration(string entityId)
        {
            lock (_syncLock)
            {
                if (!_entityLastSeen.ContainsKey(entityId) || !_motionHistory.ContainsKey(entityId))
                    return TimeSpan.Zero;

                var firstMotion = _motionHistory[entityId].FirstOrDefault();
                if (firstMotion == null)
                    return TimeSpan.Zero;

                return DateTime.UtcNow - firstMotion.Timestamp;
            }
        }

        private ActivitySeverity DetermineSeverity(ActivityType activityType, double anomalyScore, bool zoneViolation)
        {
            var baseSeverity = _config.SeverityMapping.ContainsKey(activityType)
                ? _config.SeverityMapping[activityType]
                : ActivitySeverity.Low;

            // Adjust based on anomaly score;
            if (anomalyScore > 0.8)
                baseSeverity = IncreaseSeverity(baseSeverity, 2);
            else if (anomalyScore > 0.6)
                baseSeverity = IncreaseSeverity(baseSeverity, 1);

            // Adjust for zone violations;
            if (zoneViolation)
                baseSeverity = IncreaseSeverity(baseSeverity, 1);

            return baseSeverity;
        }

        private ActivitySeverity IncreaseSeverity(ActivitySeverity currentSeverity, int levels)
        {
            var newLevel = (int)currentSeverity + levels;
            var maxLevel = Enum.GetValues(typeof(ActivitySeverity)).Length - 1;
            newLevel = Math.Min(newLevel, maxLevel);
            return (ActivitySeverity)newLevel;
        }

        private List<string> FindNearbyEntities(MotionDataPoint motionData)
        {
            var nearbyEntities = new List<string>();

            lock (_syncLock)
            {
                var cutoffTime = DateTime.UtcNow - TimeSpan.FromSeconds(5);

                foreach (var entity in _motionHistory.Keys)
                {
                    if (entity == motionData.EntityId)
                        continue;

                    var recentMotions = _motionHistory[entity]
                        .Where(m => m.Timestamp >= cutoffTime)
                        .ToList();

                    if (!recentMotions.Any())
                        continue;

                    var lastMotion = recentMotions.Last();
                    var distance = Math.Sqrt(
                        Math.Pow(lastMotion.X - motionData.X, 2) +
                        Math.Pow(lastMotion.Y - motionData.Y, 2));

                    if (distance < 5.0) // 5 meters;
                    {
                        nearbyEntities.Add(entity);
                    }
                }
            }

            return nearbyEntities;
        }

        private List<string> GenerateRecommendations(ActivityAnalysisResult result)
        {
            var recommendations = new List<string>();

            switch (result.ActivityType)
            {
                case ActivityType.FallingMotion:
                case ActivityType.MedicalEmergency:
                    recommendations.Add("Alert emergency services");
                    recommendations.Add("Dispatch first aid");
                    recommendations.Add("Clear area for medical access");
                    break;

                case ActivityType.AggressiveMotion:
                case ActivityType.Fighting:
                    recommendations.Add("Alert security personnel");
                    recommendations.Add("Activate nearby cameras");
                    recommendations.Add("Prepare to lock down area");
                    break;

                case ActivityType.SuspiciousLoitering:
                    recommendations.Add("Monitor continuously");
                    recommendations.Add("Check identification");
                    recommendations.Add("Record activity for analysis");
                    break;

                case ActivityType.TheftAttempt:
                case ActivityType.Vandalism:
                    recommendations.Add("Activate alarms");
                    recommendations.Add("Lock down affected area");
                    recommendations.Add("Notify law enforcement");
                    break;

                case ActivityType.CrowdFormation:
                    recommendations.Add("Monitor crowd density");
                    recommendations.Add("Prepare crowd control");
                    recommendations.Add("Check emergency exits");
                    break;
            }

            // Add severity-based recommendations;
            if (result.Severity >= ActivitySeverity.High)
            {
                recommendations.Add("Prioritize immediate response");
                recommendations.Add("Notify supervisor");
            }

            if (result.Severity >= ActivitySeverity.Critical)
            {
                recommendations.Add("Initiate emergency protocols");
                recommendations.Add("Evacuate if necessary");
            }

            return recommendations;
        }

        private void StoreOngoingActivity(string entityId, ActivityAnalysisResult result)
        {
            lock (_syncLock)
            {
                _ongoingActivities[entityId] = result;
            }
        }

        private async Task CheckForSuspiciousPatternsAsync()
        {
            try
            {
                var suspiciousActivities = new List<ActivityAnalysisResult>();

                lock (_syncLock)
                {
                    suspiciousActivities = _ongoingActivities.Values;
                        .Where(a => a.Severity >= ActivitySeverity.Medium)
                        .ToList();
                }

                foreach (var activity in suspiciousActivities)
                {
                    // Check if activity has been ongoing for too long;
                    var duration = DateTime.UtcNow - activity.Timestamp;
                    if (duration.TotalSeconds > _config.SuspiciousDurationThreshold)
                    {
                        await EscalateActivityAsync(activity);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for suspicious patterns");
            }
        }

        private async Task EscalateActivityAsync(ActivityAnalysisResult activity)
        {
            try
            {
                _logger.LogWarning(
                    "Escalating activity {ActivityId} of type {ActivityType} due to extended duration",
                    activity.ActivityId, activity.ActivityType);

                // Notify security monitor;
                await _securityMonitor.LogSecurityEventAsync(new SecurityEvent;
                {
                    EventType = SecurityEventType.SuspiciousActivity,
                    Severity = SecuritySeverity.High,
                    Description = $"Extended suspicious activity detected: {activity.ActivityType}",
                    Location = activity.Location,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        { "ActivityId", activity.ActivityId },
                        { "Duration", activity.Duration },
                        { "InvolvedEntities", activity.InvolvedEntities }
                    }
                });

                // Store in short-term memory for quick recall;
                await _shortTermMemory.StoreAsync($"escalated_activity_{activity.ActivityId}", activity, TimeSpan.FromMinutes(30));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error escalating activity {ActivityId}", activity.ActivityId);
            }
        }

        private void UpdateOngoingActivities()
        {
            lock (_syncLock)
            {
                var cutoffTime = DateTime.UtcNow - TimeSpan.FromMinutes(5);
                var expiredActivities = _ongoingActivities;
                    .Where(kvp => kvp.Value.Timestamp < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var entityId in expiredActivities)
                {
                    _ongoingActivities.Remove(entityId);
                }
            }
        }

        private void CleanupOldData()
        {
            lock (_syncLock)
            {
                var cutoffTime = DateTime.UtcNow - TimeSpan.FromMinutes(30);

                // Clean motion history;
                foreach (var entityId in _motionHistory.Keys.ToList())
                {
                    _motionHistory[entityId] = _motionHistory[entityId]
                        .Where(m => m.Timestamp >= cutoffTime)
                        .ToList();

                    if (!_motionHistory[entityId].Any())
                    {
                        _motionHistory.Remove(entityId);
                    }
                }

                // Clean entity tracking;
                var expiredEntities = _entityLastSeen;
                    .Where(kvp => kvp.Value < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var entityId in expiredEntities)
                {
                    _entityLastSeen.Remove(entityId);
                }
            }
        }

        private async Task MonitorForAnomaliesAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

                    var anomalyReport = await GenerateAnomalyReportAsync();
                    if (anomalyReport.AnomalyScore > 0.7)
                    {
                        _logger.LogWarning(
                            "High anomaly score detected: {Score}. Activity count: {Count}",
                            anomalyReport.AnomalyScore, anomalyReport.SuspiciousActivityCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in anomaly monitoring");
                }
            }
        }

        private async Task<AnomalyReport> GenerateAnomalyReportAsync()
        {
            var report = new AnomalyReport();

            lock (_syncLock)
            {
                var recentActivities = _ongoingActivities.Values;
                    .Where(a => a.Timestamp >= DateTime.UtcNow - TimeSpan.FromMinutes(10))
                    .ToList();

                report.SuspiciousActivityCount = recentActivities.Count(a => a.Severity >= ActivitySeverity.Medium);
                report.TotalActivities = recentActivities.Count;
                report.AverageConfidence = recentActivities.Any() ? recentActivities.Average(a => a.Confidence) : 0;
            }

            // Calculate anomaly score based on various factors;
            var activityDensity = await CalculateActivityDensityAsync();
            var patternConsistency = await CalculatePatternConsistencyAsync();

            report.AnomalyScore = (report.SuspiciousActivityCount * 0.4) +
                                 (activityDensity * 0.3) +
                                 ((1 - patternConsistency) * 0.3);

            report.Timestamp = DateTime.UtcNow;

            return report;
        }

        private async Task<double> CalculateActivityDensityAsync()
        {
            lock (_syncLock)
            {
                var activeEntities = _entityLastSeen.Count(e =>
                    DateTime.UtcNow - e.Value < TimeSpan.FromMinutes(1));

                var maxExpectedEntities = 100; // This should be configurable;
                return Math.Min(1.0, activeEntities / (double)maxExpectedEntities);
            }
        }

        private async Task<double> CalculatePatternConsistencyAsync()
        {
            // Calculate consistency of activity patterns over time;
            var consistencyScores = new List<double>();

            lock (_syncLock)
            {
                foreach (var entityId in _motionHistory.Keys)
                {
                    var recentMotions = GetRecentMotionHistory(entityId);
                    if (recentMotions.Count > 5)
                    {
                        var consistency = CalculateMovementConsistency(recentMotions);
                        consistencyScores.Add(consistency);
                    }
                }
            }

            return consistencyScores.Any() ? consistencyScores.Average() : 1.0;
        }

        private ActivityTrend CalculateActivityTrend(List<ActivityAnalysisResult> recentActivities)
        {
            if (recentActivities.Count < 2)
                return ActivityTrend.Stable;

            var criticalActivities = recentActivities.Count(a => a.Severity == ActivitySeverity.Critical);
            var previousCritical = recentActivities;
                .Where(a => a.Timestamp < DateTime.UtcNow - TimeSpan.FromMinutes(5))
                .Count(a => a.Severity == ActivitySeverity.Critical);

            if (criticalActivities > previousCritical * 1.5)
                return ActivityTrend.Increasing;
            else if (criticalActivities < previousCritical * 0.5)
                return ActivityTrend.Decreasing;
            else;
                return ActivityTrend.Stable;
        }

        private async Task PerformCrossEntityAnalysisAsync(List<ActivityAnalysisResult> results)
        {
            try
            {
                // Group activities by location;
                var activitiesByLocation = results;
                    .GroupBy(r => r.Location)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var locationGroup in activitiesByLocation)
                {
                    var locationActivities = locationGroup.Value;

                    // Check for crowd formation;
                    if (locationActivities.Count >= _config.MinimumEntitiesForCrowd)
                    {
                        var crowdResult = new ActivityAnalysisResult;
                        {
                            ActivityType = ActivityType.CrowdFormation,
                            Severity = ActivitySeverity.Medium,
                            Confidence = 0.8,
                            Location = locationGroup.Key,
                            InvolvedEntities = locationActivities.SelectMany(a => a.InvolvedEntities).Distinct().ToList(),
                            RecommendedActions = new List<string>
                            {
                                "Monitor crowd density",
                                "Prepare crowd control measures",
                                "Check emergency exits"
                            }
                        };

                        OnActivityDetected(new ActivityDetectedEventArgs;
                        {
                            ActivityResult = crowdResult,
                            Timestamp = DateTime.UtcNow;
                        });
                    }

                    // Check for coordinated activities;
                    var coordinatedActivities = await DetectCoordinatedActivitiesAsync(locationActivities);
                    if (coordinatedActivities.Any())
                    {
                        await HandleCoordinatedActivitiesAsync(coordinatedActivities);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cross-entity analysis");
            }
        }

        private async Task<List<ActivityAnalysisResult>> DetectCoordinatedActivitiesAsync(List<ActivityAnalysisResult> activities)
        {
            var coordinated = new List<ActivityAnalysisResult>();

            // Simple coordination detection based on timing and activity type;
            var activityGroups = activities;
                .GroupBy(a => a.ActivityType)
                .Where(g => g.Count() > 1);

            foreach (var group in activityGroups)
            {
                var times = group.Select(a => a.Timestamp).ToList();
                var timeSpan = times.Max() - times.Min();

                if (timeSpan.TotalSeconds < 10) // Activities within 10 seconds;
                {
                    coordinated.AddRange(group);
                }
            }

            return coordinated;
        }

        private async Task HandleCoordinatedActivitiesAsync(List<ActivityAnalysisResult> coordinatedActivities)
        {
            _logger.LogWarning(
                "Detected {Count} coordinated activities of type {ActivityType}",
                coordinatedActivities.Count, coordinatedActivities.First().ActivityType);

            // Notify security system;
            await _securityMonitor.LogSecurityEventAsync(new SecurityEvent;
            {
                EventType = SecurityEventType.CoordinatedActivity,
                Severity = SecuritySeverity.High,
                Description = $"Coordinated activities detected: {coordinatedActivities.First().ActivityType}",
                Location = coordinatedActivities.First().Location,
                Timestamp = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    { "ActivityCount", coordinatedActivities.Count },
                    { "ActivityTypes", coordinatedActivities.Select(a => a.ActivityType.ToString()).Distinct() },
                    { "InvolvedEntities", coordinatedActivities.SelectMany(a => a.InvolvedEntities).Distinct() }
                }
            });
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnActivityDetected(ActivityDetectedEventArgs e)
        {
            ActivityDetected?.Invoke(this, e);

            // Also trigger analysis completed event;
            OnAnalysisCompleted(new ActivityAnalysisCompletedEventArgs;
            {
                ActivityResult = e.ActivityResult,
                Timestamp = e.Timestamp;
            });
        }

        protected virtual void OnAnalysisCompleted(ActivityAnalysisCompletedEventArgs e)
        {
            AnalysisCompleted?.Invoke(this, e);
        }

        protected virtual void OnAnomalyDetected(AnomalyDetectedEventArgs e)
        {
            AnomalyDetected?.Invoke(this, e);
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
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();

                    // Wait for background task to complete;
                    if (_backgroundAnalysisTask != null && !_backgroundAnalysisTask.IsCompleted)
                    {
                        try
                        {
                            _backgroundAnalysisTask.Wait(TimeSpan.FromSeconds(5));
                        }
                        catch (AggregateException)
                        {
                            // Expected when task is cancelled;
                        }
                    }

                    ClearAllData();
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Nested Classes;

        public class ActivityDetectedEventArgs : EventArgs;
        {
            public ActivityAnalysisResult ActivityResult { get; set; }
            public MotionDataPoint MotionData { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ActivityAnalysisCompletedEventArgs : EventArgs;
        {
            public ActivityAnalysisResult ActivityResult { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class AnomalyDetectedEventArgs : EventArgs;
        {
            public ActivityAnalysisResult ActivityResult { get; set; }
            public double AnomalyScore { get; set; }
            public string AnomalyType { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ActivityStatistics;
        {
            public int TotalActivities { get; set; }
            public int CriticalActivities { get; set; }
            public int HighPriorityActivities { get; set; }
            public double AverageConfidence { get; set; }
            public ActivityType MostCommonActivity { get; set; }
            public ActivityTrend ActivityTrend { get; set; }
        }

        public class AnomalyReport;
        {
            public double AnomalyScore { get; set; }
            public int SuspiciousActivityCount { get; set; }
            public int TotalActivities { get; set; }
            public double AverageConfidence { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public enum ActivityTrend;
        {
            Decreasing = -1,
            Stable = 0,
            Increasing = 1;
        }

        #endregion;
    }

    #region Custom Exceptions;

    public class ActivityAnalyzerException : Exception
    {
        public ActivityAnalyzerException(string message) : base(message) { }
        public ActivityAnalyzerException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ActivityAnalysisException : ActivityAnalyzerException;
    {
        public string EntityId { get; }

        public ActivityAnalysisException(string message, Exception innerException, string entityId)
            : base(message, innerException)
        {
            EntityId = entityId;
        }
    }

    public class ModelTrainingException : ActivityAnalyzerException;
    {
        public ModelTrainingException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
