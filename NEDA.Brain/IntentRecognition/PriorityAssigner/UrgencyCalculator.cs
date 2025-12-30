using System;
using System.Collections.Generic;
using System.Linq;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;

namespace NEDA.Brain.IntentRecognition.PriorityAssigner;
{
    /// <summary>
    /// Calculates urgency scores for tasks and requests based on multiple factors;
    /// including context, user behavior, time sensitivity, and system conditions.
    /// </summary>
    public class UrgencyCalculator : IUrgencyCalculator, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IContextAnalyzer _contextAnalyzer;
        private readonly ISystemMonitor _systemMonitor;
        private readonly UrgencyConfiguration _configuration;
        private bool _disposed = false;

        // Caches for performance optimization;
        private readonly Dictionary<string, UrgencyProfile> _userProfiles;
        private readonly Dictionary<string, double> _taskTypeWeights;
        private readonly object _syncLock = new object();

        // Time-based urgency decay;
        private readonly TimeSpan _urgencyDecayInterval = TimeSpan.FromMinutes(5);
        private DateTime _lastDecayTime = DateTime.UtcNow;

        /// <summary>
        /// Initializes a new instance of UrgencyCalculator;
        /// </summary>
        public UrgencyCalculator(
            ILogger logger,
            IContextAnalyzer contextAnalyzer,
            ISystemMonitor systemMonitor,
            UrgencyConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contextAnalyzer = contextAnalyzer ?? throw new ArgumentNullException(nameof(contextAnalyzer));
            _systemMonitor = systemMonitor ?? throw new ArgumentNullException(nameof(systemMonitor));
            _configuration = configuration ?? UrgencyConfiguration.Default;

            _userProfiles = new Dictionary<string, UrgencyProfile>();
            _taskTypeWeights = new Dictionary<string, double>();

            InitializeTaskTypeWeights();
            LoadDefaultProfiles();

            _logger.LogInformation("UrgencyCalculator initialized");
        }

        /// <summary>
        /// Initializes default weights for different task types;
        /// </summary>
        private void InitializeTaskTypeWeights()
        {
            // Critical system operations;
            _taskTypeWeights["emergency_shutdown"] = 10.0;
            _taskTypeWeights["security_breach"] = 9.5;
            _taskTypeWeights["system_failure"] = 9.0;

            // High priority user requests;
            _taskTypeWeights["real_time_rendering"] = 8.5;
            _taskTypeWeights["live_streaming"] = 8.0;
            _taskTypeWeights["critical_bug_fix"] = 7.5;

            // Normal operations;
            _taskTypeWeights["3d_modeling"] = 6.0;
            _taskTypeWeights["texture_creation"] = 5.5;
            _taskTypeWeights["animation"] = 5.0;
            _taskTypeWeights["rendering"] = 4.5;

            // Background tasks;
            _taskTypeWeights["data_backup"] = 3.0;
            _taskTypeWeights["file_cleanup"] = 2.0;
            _taskTypeWeights["system_optimization"] = 1.5;

            // Information requests;
            _taskTypeWeights["status_query"] = 1.0;
            _taskTypeWeights["help_request"] = 0.8;

            _logger.LogDebug($"Initialized {_taskTypeWeights.Count} task type weights");
        }

        /// <summary>
        /// Loads default user urgency profiles;
        /// </summary>
        private void LoadDefaultProfiles()
        {
            // Administrator profile - higher urgency tolerance;
            _userProfiles["admin"] = new UrgencyProfile;
            {
                UserId = "admin",
                BaseUrgencyMultiplier = 1.2,
                TimeSensitivity = 0.8,
                HistoricalUrgencyAverage = 6.5,
                ResponseTimeExpectation = TimeSpan.FromSeconds(2),
                MaxConcurrentUrgentTasks = 10;
            };

            // Developer profile - medium urgency;
            _userProfiles["developer"] = new UrgencyProfile;
            {
                UserId = "developer",
                BaseUrgencyMultiplier = 1.0,
                TimeSensitivity = 0.6,
                HistoricalUrgencyAverage = 5.0,
                ResponseTimeExpectation = TimeSpan.FromSeconds(5),
                MaxConcurrentUrgentTasks = 5;
            };

            // Artist profile - lower urgency for creative tasks;
            _userProfiles["artist"] = new UrgencyProfile;
            {
                UserId = "artist",
                BaseUrgencyMultiplier = 0.8,
                TimeSensitivity = 0.4,
                HistoricalUrgencyAverage = 3.5,
                ResponseTimeExpectation = TimeSpan.FromSeconds(10),
                MaxConcurrentUrgentTasks = 3;
            };

            _logger.LogDebug($"Loaded {_userProfiles.Count} default user profiles");
        }

        /// <summary>
        /// Calculates urgency score for a task request;
        /// </summary>
        public UrgencyResult CalculateUrgency(TaskRequest request, CalculationContext context)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                lock (_syncLock)
                {
                    // Apply urgency decay if needed;
                    ApplyUrgencyDecay();

                    _logger.LogDebug($"Calculating urgency for task: {request.TaskType}");

                    // Get base urgency from task type;
                    var baseUrgency = GetBaseUrgencyScore(request.TaskType);

                    // Apply context modifiers;
                    var contextModifier = CalculateContextModifier(request, context);

                    // Apply time sensitivity;
                    var timeModifier = CalculateTimeModifier(request);

                    // Apply user profile modifier;
                    var userModifier = CalculateUserModifier(request.UserId, context);

                    // Apply system condition modifier;
                    var systemModifier = CalculateSystemModifier();

                    // Apply dependency modifier;
                    var dependencyModifier = CalculateDependencyModifier(request.Dependencies);

                    // Calculate final urgency score;
                    var rawUrgency = baseUrgency *
                                    contextModifier *
                                    timeModifier *
                                    userModifier *
                                    systemModifier *
                                    dependencyModifier;

                    // Apply bounds and normalization;
                    var normalizedUrgency = NormalizeUrgencyScore(rawUrgency);

                    // Determine urgency level;
                    var urgencyLevel = DetermineUrgencyLevel(normalizedUrgency);

                    // Calculate priority score (urgency + importance)
                    var priorityScore = CalculatePriorityScore(normalizedUrgency, request.Importance);

                    var result = new UrgencyResult;
                    {
                        UrgencyScore = normalizedUrgency,
                        UrgencyLevel = urgencyLevel,
                        PriorityScore = priorityScore,
                        BaseUrgency = baseUrgency,
                        ContextModifier = contextModifier,
                        TimeModifier = timeModifier,
                        UserModifier = userModifier,
                        SystemModifier = systemModifier,
                        DependencyModifier = dependencyModifier,
                        RecommendedAction = GetRecommendedAction(urgencyLevel, request),
                        EstimatedProcessingTime = EstimateProcessingTime(normalizedUrgency, request),
                        CalculatedAt = DateTime.UtcNow,
                        IsValid = true;
                    };

                    // Update user profile with this calculation;
                    UpdateUserProfile(request.UserId, result);

                    _logger.LogInformation($"Urgency calculated: {request.TaskType} = {normalizedUrgency:F2} ({urgencyLevel})");

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Urgency calculation failed: {ex.Message}");
                return UrgencyResult.Error($"Calculation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates urgency for multiple tasks and returns sorted by priority;
        /// </summary>
        public List<UrgencyResult> CalculateBatchUrgency(List<TaskRequest> requests, CalculationContext context)
        {
            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            if (requests.Count == 0)
                return new List<UrgencyResult>();

            var results = new List<UrgencyResult>();

            foreach (var request in requests)
            {
                try
                {
                    var result = CalculateUrgency(request, context);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to calculate urgency for task {request.TaskId}: {ex.Message}");
                    results.Add(UrgencyResult.Error($"Failed: {ex.Message}"));
                }
            }

            // Sort by priority score descending;
            return results;
                .Where(r => r.IsValid)
                .OrderByDescending(r => r.PriorityScore)
                .ThenByDescending(r => r.UrgencyScore)
                .ToList();
        }

        /// <summary>
        /// Adjusts urgency based on real-time feedback;
        /// </summary>
        public UrgencyResult AdjustUrgency(string taskId, double feedbackScore, AdjustmentReason reason)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("Task ID cannot be null or empty", nameof(taskId));

            lock (_syncLock)
            {
                try
                {
                    // In a real implementation, you would look up the task;
                    // and adjust its urgency based on feedback;

                    var adjustmentFactor = CalculateFeedbackAdjustment(feedbackScore, reason);

                    _logger.LogInformation($"Urgency adjustment for task {taskId}: factor={adjustmentFactor:F2}, reason={reason}");

                    return new UrgencyResult;
                    {
                        UrgencyScore = adjustmentFactor,
                        UrgencyLevel = DetermineUrgencyLevel(adjustmentFactor),
                        AdjustmentApplied = true,
                        AdjustmentFactor = adjustmentFactor,
                        AdjustmentReason = reason,
                        CalculatedAt = DateTime.UtcNow,
                        IsValid = true;
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Urgency adjustment failed: {ex.Message}");
                    return UrgencyResult.Error($"Adjustment failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Predicts future urgency based on patterns and trends;
        /// </summary>
        public UrgencyPrediction PredictFutureUrgency(string taskType, string userId, PredictionWindow window)
        {
            if (string.IsNullOrWhiteSpace(taskType))
                throw new ArgumentException("Task type cannot be null or empty", nameof(taskType));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogDebug($"Predicting future urgency for {taskType} over {window}");

                    var baseScore = GetBaseUrgencyScore(taskType);
                    var userProfile = GetUserProfile(userId);
                    var historicalPattern = AnalyzeHistoricalPattern(taskType, userId);

                    var prediction = new UrgencyPrediction;
                    {
                        TaskType = taskType,
                        UserId = userId,
                        Window = window,
                        BasePrediction = baseScore,
                        Confidence = CalculatePredictionConfidence(taskType, userId, window),
                        PeakUrgencyTime = PredictPeakTime(taskType, window),
                        RecommendedPreemptiveAction = DeterminePreemptiveAction(baseScore, window),
                        PredictedAt = DateTime.UtcNow;
                    };

                    // Calculate predictions for different time points;
                    var timePoints = GetPredictionTimePoints(window);
                    foreach (var timePoint in timePoints)
                    {
                        var predictedScore = CalculateTimeBasedPrediction(baseScore, timePoint, historicalPattern);
                        prediction.TimeBasedPredictions[timePoint] = predictedScore;
                    }

                    _logger.LogInformation($"Future urgency predicted for {taskType}: {prediction.BasePrediction:F2}");

                    return prediction;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Urgency prediction failed: {ex.Message}");
                    throw new UrgencyCalculationException($"Prediction failed: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Gets the base urgency score for a task type;
        /// </summary>
        private double GetBaseUrgencyScore(string taskType)
        {
            if (_taskTypeWeights.TryGetValue(taskType, out var weight))
            {
                return weight;
            }

            // Default weight for unknown task types;
            return _configuration.DefaultTaskUrgency;
        }

        /// <summary>
        /// Calculates context-based urgency modifier;
        /// </summary>
        private double CalculateContextModifier(TaskRequest request, CalculationContext context)
        {
            double modifier = 1.0;

            // Check for emergency keywords in context;
            if (ContainsEmergencyKeywords(request.Description))
            {
                modifier *= _configuration.EmergencyKeywordMultiplier;
            }

            // Check for time-sensitive context;
            if (context.HasDeadline)
            {
                var timeUntilDeadline = context.Deadline - DateTime.UtcNow;
                if (timeUntilDeadline.TotalHours < 1)
                {
                    modifier *= _configuration.ImminentDeadlineMultiplier;
                }
                else if (timeUntilDeadline.TotalHours < 24)
                {
                    modifier *= _configuration.NearDeadlineMultiplier;
                }
            }

            // Check for user stress indicators;
            if (context.UserStressLevel > 0.7)
            {
                modifier *= (1.0 + (context.UserStressLevel * 0.3));
            }

            // Check for concurrent urgent tasks;
            var concurrentUrgent = GetConcurrentUrgentTasks(request.UserId);
            if (concurrentUrgent > 0)
            {
                modifier *= (1.0 + (concurrentUrgent * 0.1));
            }

            return Math.Clamp(modifier, _configuration.MinContextModifier, _configuration.MaxContextModifier);
        }

        /// <summary>
        /// Calculates time-based urgency modifier;
        /// </summary>
        private double CalculateTimeModifier(TaskRequest request)
        {
            double modifier = 1.0;

            // Time of day adjustment;
            var currentHour = DateTime.UtcNow.Hour;
            if (currentHour >= 22 || currentHour <= 6) // Night hours;
            {
                modifier *= _configuration.NightTimeMultiplier;
            }
            else if (currentHour >= 9 && currentHour <= 17) // Business hours;
            {
                modifier *= _configuration.BusinessHoursMultiplier;
            }

            // Day of week adjustment;
            var dayOfWeek = DateTime.UtcNow.DayOfWeek;
            if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
            {
                modifier *= _configuration.WeekendMultiplier;
            }

            // Time since request was made;
            var timeSinceRequest = DateTime.UtcNow - request.CreatedAt;
            if (timeSinceRequest > TimeSpan.FromMinutes(30))
            {
                modifier *= (1.0 + (timeSinceRequest.TotalHours * 0.1));
            }

            return Math.Clamp(modifier, _configuration.MinTimeModifier, _configuration.MaxTimeModifier);
        }

        /// <summary>
        /// Calculates user-specific urgency modifier;
        /// </summary>
        private double CalculateUserModifier(string userId, CalculationContext context)
        {
            var profile = GetUserProfile(userId);

            double modifier = profile.BaseUrgencyMultiplier;

            // Adjust based on user's historical behavior;
            if (profile.ResponseTimeExpectation != TimeSpan.Zero)
            {
                var expectedResponseFactor = _configuration.ExpectedResponseTime / profile.ResponseTimeExpectation;
                modifier *= expectedResponseFactor;
            }

            // Adjust based on user role/privileges;
            if (context.UserRole == UserRole.Administrator)
            {
                modifier *= _configuration.AdminMultiplier;
            }
            else if (context.UserRole == UserRole.PowerUser)
            {
                modifier *= _configuration.PowerUserMultiplier;
            }

            return Math.Clamp(modifier, _configuration.MinUserModifier, _configuration.MaxUserModifier);
        }

        /// <summary>
        /// Calculates system condition-based urgency modifier;
        /// </summary>
        private double CalculateSystemModifier()
        {
            double modifier = 1.0;

            try
            {
                var systemHealth = _systemMonitor.GetSystemHealth();

                // High system load reduces ability to handle urgent tasks;
                if (systemHealth.CpuUsage > 80)
                {
                    modifier *= (1.0 + ((systemHealth.CpuUsage - 80) * 0.01));
                }

                if (systemHealth.MemoryUsage > 85)
                {
                    modifier *= (1.0 + ((systemHealth.MemoryUsage - 85) * 0.015));
                }

                // System emergencies increase urgency;
                if (systemHealth.HasCriticalErrors)
                {
                    modifier *= _configuration.SystemEmergencyMultiplier;
                }

                // Network latency affects real-time tasks;
                if (systemHealth.NetworkLatency > 100) // ms;
                {
                    modifier *= (1.0 + (systemHealth.NetworkLatency * 0.001));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to get system health: {ex.Message}");
                // Use neutral modifier if system monitoring fails;
            }

            return Math.Clamp(modifier, _configuration.MinSystemModifier, _configuration.MaxSystemModifier);
        }

        /// <summary>
        /// Calculates dependency-based urgency modifier;
        /// </summary>
        private double CalculateDependencyModifier(List<TaskDependency> dependencies)
        {
            if (dependencies == null || dependencies.Count == 0)
                return 1.0;

            double modifier = 1.0;
            int criticalDependencies = 0;
            int blockedDependencies = 0;

            foreach (var dependency in dependencies)
            {
                if (dependency.IsCritical)
                {
                    criticalDependencies++;

                    if (dependency.Status == DependencyStatus.Blocked)
                    {
                        blockedDependencies++;
                        modifier *= _configuration.BlockedCriticalDependencyMultiplier;
                    }
                    else if (dependency.Status == DependencyStatus.Pending)
                    {
                        modifier *= _configuration.PendingCriticalDependencyMultiplier;
                    }
                }
            }

            // Multiple critical dependencies increase urgency;
            if (criticalDependencies > 1)
            {
                modifier *= (1.0 + (criticalDependencies * 0.1));
            }

            return Math.Clamp(modifier, _configuration.MinDependencyModifier, _configuration.MaxDependencyModifier);
        }

        /// <summary>
        /// Normalizes urgency score to configured range;
        /// </summary>
        private double NormalizeUrgencyScore(double rawScore)
        {
            // Apply sigmoid normalization for better distribution;
            var normalized = 1.0 / (1.0 + Math.Exp(-rawScore / _configuration.NormalizationFactor));

            // Scale to configured range;
            var scaled = _configuration.MinUrgencyScore +
                        (normalized * (_configuration.MaxUrgencyScore - _configuration.MinUrgencyScore));

            return Math.Round(scaled, 2);
        }

        /// <summary>
        /// Determines urgency level from score;
        /// </summary>
        private UrgencyLevel DetermineUrgencyLevel(double score)
        {
            if (score >= _configuration.CriticalThreshold)
                return UrgencyLevel.Critical;
            if (score >= _configuration.HighThreshold)
                return UrgencyLevel.High;
            if (score >= _configuration.MediumThreshold)
                return UrgencyLevel.Medium;
            if (score >= _configuration.LowThreshold)
                return UrgencyLevel.Low;

            return UrgencyLevel.None;
        }

        /// <summary>
        /// Calculates combined priority score (urgency + importance)
        /// </summary>
        private double CalculatePriorityScore(double urgencyScore, double importanceScore)
        {
            var weightedUrgency = urgencyScore * _configuration.UrgencyWeight;
            var weightedImportance = importanceScore * _configuration.ImportanceWeight;

            return (weightedUrgency + weightedImportance) /
                   (_configuration.UrgencyWeight + _configuration.ImportanceWeight);
        }

        /// <summary>
        /// Gets recommended action based on urgency level;
        /// </summary>
        private RecommendedAction GetRecommendedAction(UrgencyLevel level, TaskRequest request)
        {
            return level switch;
            {
                UrgencyLevel.Critical => new RecommendedAction;
                {
                    ActionType = ActionType.ImmediateExecution,
                    PreemptOtherTasks = true,
                    NotifyUsers = true,
                    EscalateTo = "system_admin",
                    Timeout = TimeSpan.FromSeconds(30)
                },
                UrgencyLevel.High => new RecommendedAction;
                {
                    ActionType = ActionType.PrioritizedExecution,
                    PreemptOtherTasks = false,
                    NotifyUsers = true,
                    EscalateTo = "team_lead",
                    Timeout = TimeSpan.FromMinutes(5)
                },
                UrgencyLevel.Medium => new RecommendedAction;
                {
                    ActionType = ActionType.NormalExecution,
                    PreemptOtherTasks = false,
                    NotifyUsers = false,
                    EscalateTo = null,
                    Timeout = TimeSpan.FromMinutes(30)
                },
                UrgencyLevel.Low => new RecommendedAction;
                {
                    ActionType = ActionType.BackgroundExecution,
                    PreemptOtherTasks = false,
                    NotifyUsers = false,
                    EscalateTo = null,
                    Timeout = TimeSpan.FromHours(2)
                },
                _ => new RecommendedAction;
                {
                    ActionType = ActionType.DeferredExecution,
                    PreemptOtherTasks = false,
                    NotifyUsers = false,
                    EscalateTo = null,
                    Timeout = TimeSpan.FromDays(1)
                }
            };
        }

        /// <summary>
        /// Estimates processing time based on urgency;
        /// </summary>
        private TimeSpan EstimateProcessingTime(double urgencyScore, TaskRequest request)
        {
            var baseTime = GetBaseProcessingTime(request.TaskType);

            // Higher urgency gets faster processing;
            var speedFactor = 1.0 / (urgencyScore * 0.1);

            var estimatedTime = baseTime * speedFactor;

            // Apply minimum and maximum bounds;
            return TimeSpan.FromSeconds(Math.Clamp(
                estimatedTime.TotalSeconds,
                _configuration.MinProcessingTime.TotalSeconds,
                _configuration.MaxProcessingTime.TotalSeconds));
        }

        /// <summary>
        /// Gets user profile, creates default if not exists;
        /// </summary>
        private UrgencyProfile GetUserProfile(string userId)
        {
            if (_userProfiles.TryGetValue(userId, out var profile))
            {
                return profile;
            }

            // Create default profile for new user;
            var defaultProfile = new UrgencyProfile;
            {
                UserId = userId,
                BaseUrgencyMultiplier = 1.0,
                TimeSensitivity = 0.5,
                HistoricalUrgencyAverage = _configuration.DefaultTaskUrgency,
                ResponseTimeExpectation = TimeSpan.FromSeconds(10),
                MaxConcurrentUrgentTasks = 3;
            };

            _userProfiles[userId] = defaultProfile;
            return defaultProfile;
        }

        /// <summary>
        /// Updates user profile with calculation results;
        /// </summary>
        private void UpdateUserProfile(string userId, UrgencyResult result)
        {
            var profile = GetUserProfile(userId);

            // Update historical average (moving average)
            profile.HistoricalUrgencyAverage =
                (profile.HistoricalUrgencyAverage * 0.9) + (result.UrgencyScore * 0.1);

            // Adjust user's urgency sensitivity based on results;
            if (result.UrgencyLevel >= UrgencyLevel.High)
            {
                profile.TimeSensitivity = Math.Min(1.0, profile.TimeSensitivity + 0.05);
            }
            else if (result.UrgencyLevel <= UrgencyLevel.Low)
            {
                profile.TimeSensitivity = Math.Max(0.0, profile.TimeSensitivity - 0.02);
            }

            _userProfiles[userId] = profile;
        }

        /// <summary>
        /// Applies time-based decay to urgency profiles;
        /// </summary>
        private void ApplyUrgencyDecay()
        {
            var now = DateTime.UtcNow;
            if (now - _lastDecayTime < _urgencyDecayInterval)
                return;

            foreach (var userId in _userProfiles.Keys.ToList())
            {
                var profile = _userProfiles[userId];

                // Decay time sensitivity towards neutral;
                if (profile.TimeSensitivity > 0.5)
                {
                    profile.TimeSensitivity *= 0.99;
                }
                else if (profile.TimeSensitivity < 0.5)
                {
                    profile.TimeSensitivity = 0.5 + (profile.TimeSensitivity - 0.5) * 0.99;
                }

                _userProfiles[userId] = profile;
            }

            _lastDecayTime = now;
            _logger.LogDebug("Applied urgency decay to user profiles");
        }

        #region Helper Methods;

        private bool ContainsEmergencyKeywords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var emergencyKeywords = new[]
            {
                "emergency", "critical", "urgent", "asap", "immediately",
                "broken", "failed", "crash", "down", "error",
                "security", "breach", "attack", "hack", "intrusion"
            };

            return emergencyKeywords.Any(keyword =>
                text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private int GetConcurrentUrgentTasks(string userId)
        {
            // This would query a task manager in real implementation;
            // For now, return a simulated count;
            return 0;
        }

        private TimeSpan GetBaseProcessingTime(string taskType)
        {
            // Base processing times for different task types;
            var processingTimes = new Dictionary<string, TimeSpan>
            {
                ["emergency_shutdown"] = TimeSpan.FromSeconds(10),
                ["real_time_rendering"] = TimeSpan.FromSeconds(5),
                ["3d_modeling"] = TimeSpan.FromMinutes(30),
                ["data_backup"] = TimeSpan.FromHours(1),
                ["status_query"] = TimeSpan.FromMilliseconds(100)
            };

            return processingTimes.TryGetValue(taskType, out var time)
                ? time;
                : TimeSpan.FromMinutes(5);
        }

        private double CalculateFeedbackAdjustment(double feedbackScore, AdjustmentReason reason)
        {
            var adjustment = 1.0;

            switch (reason)
            {
                case AdjustmentReason.UserFeedback:
                    adjustment += (feedbackScore - 0.5) * 0.2;
                    break;
                case AdjustmentReason.SystemPerformance:
                    adjustment += feedbackScore * 0.15;
                    break;
                case AdjustmentReason.ExternalEvent:
                    adjustment += feedbackScore * 0.25;
                    break;
            }

            return Math.Clamp(adjustment, 0.5, 2.0);
        }

        private List<DateTime> GetPredictionTimePoints(PredictionWindow window)
        {
            var points = new List<DateTime>();
            var now = DateTime.UtcNow;

            switch (window)
            {
                case PredictionWindow.NextHour:
                    for (int i = 1; i <= 6; i++)
                        points.Add(now.AddMinutes(i * 10));
                    break;
                case PredictionWindow.NextDay:
                    for (int i = 1; i <= 24; i++)
                        points.Add(now.AddHours(i));
                    break;
                case PredictionWindow.NextWeek:
                    for (int i = 1; i <= 7; i++)
                        points.Add(now.AddDays(i));
                    break;
            }

            return points;
        }

        private double CalculateTimeBasedPrediction(double baseScore, DateTime timePoint, HistoricalPattern pattern)
        {
            // Simplified prediction - in reality would use ML models;
            var timeFactor = 1.0;
            var hour = timePoint.Hour;

            // Adjust based on time of day pattern;
            if (hour >= 9 && hour <= 17) // Business hours;
                timeFactor *= 1.2;
            else if (hour >= 18 && hour <= 22) // Evening;
                timeFactor *= 0.9;
            else // Night;
                timeFactor *= 0.7;

            return baseScore * timeFactor;
        }

        private HistoricalPattern AnalyzeHistoricalPattern(string taskType, string userId)
        {
            // This would analyze historical data in real implementation;
            return new HistoricalPattern();
        }

        private double CalculatePredictionConfidence(string taskType, string userId, PredictionWindow window)
        {
            // Simplified confidence calculation;
            var baseConfidence = 0.7;

            // More confidence for shorter prediction windows;
            if (window == PredictionWindow.NextHour)
                baseConfidence += 0.2;
            else if (window == PredictionWindow.NextDay)
                baseConfidence += 0.1;

            return Math.Clamp(baseConfidence, 0.0, 1.0);
        }

        private DateTime PredictPeakTime(string taskType, PredictionWindow window)
        {
            // Simplified peak time prediction;
            var now = DateTime.UtcNow;

            return taskType switch;
            {
                "real_time_rendering" => now.AddHours(2), // Peak in 2 hours;
                "data_backup" => now.AddHours(6), // Peak in 6 hours;
                "system_optimization" => now.AddDays(1).Date.AddHours(3), // 3 AM tomorrow;
                _ => now.AddHours(12) // Default: noon;
            };
        }

        private string DeterminePreemptiveAction(double baseScore, PredictionWindow window)
        {
            if (baseScore > 7.0 && window == PredictionWindow.NextHour)
                return "Allocate additional resources";
            else if (baseScore > 5.0 && window == PredictionWindow.NextDay)
                return "Schedule maintenance window";
            else;
                return "Monitor situation";
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources;
                    _userProfiles.Clear();
                    _taskTypeWeights.Clear();
                    _logger.LogInformation("UrgencyCalculator disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~UrgencyCalculator()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IUrgencyCalculator;
    {
        UrgencyResult CalculateUrgency(TaskRequest request, CalculationContext context);
        List<UrgencyResult> CalculateBatchUrgency(List<TaskRequest> requests, CalculationContext context);
        UrgencyResult AdjustUrgency(string taskId, double feedbackScore, AdjustmentReason reason);
        UrgencyPrediction PredictFutureUrgency(string taskType, string userId, PredictionWindow window);
    }

    public interface IContextAnalyzer;
    {
        bool ContainsEmergencyKeywords(string text);
        double AnalyzeStressLevel(string text);
        Dictionary<string, object> ExtractContextFactors(string input);
    }

    public interface ISystemMonitor;
    {
        SystemHealth GetSystemHealth();
        ResourceAvailability GetResourceAvailability();
        List<SystemAlert> GetActiveAlerts();
    }

    /// <summary>
    /// Represents a task request for urgency calculation;
    /// </summary>
    public class TaskRequest;
    {
        public string TaskId { get; set; }
        public string TaskType { get; set; }
        public string UserId { get; set; }
        public string Description { get; set; }
        public double Importance { get; set; } // 0.0 to 10.0;
        public DateTime CreatedAt { get; set; }
        public DateTime? Deadline { get; set; }
        public List<TaskDependency> Dependencies { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public TaskRequest()
        {
            TaskId = Guid.NewGuid().ToString();
            CreatedAt = DateTime.UtcNow;
            Dependencies = new List<TaskDependency>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Calculation context for urgency assessment;
    /// </summary>
    public class CalculationContext;
    {
        public string SessionId { get; set; }
        public bool HasDeadline { get; set; }
        public DateTime? Deadline { get; set; }
        public double UserStressLevel { get; set; } // 0.0 to 1.0;
        public UserRole UserRole { get; set; }
        public TimeSpan ExpectedResponseTime { get; set; }
        public Dictionary<string, object> AdditionalContext { get; set; }

        public CalculationContext()
        {
            AdditionalContext = new Dictionary<string, object>();
            ExpectedResponseTime = TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>
    /// Result of urgency calculation;
    /// </summary>
    public class UrgencyResult;
    {
        public double UrgencyScore { get; set; } // 0.0 to 10.0;
        public UrgencyLevel UrgencyLevel { get; set; }
        public double PriorityScore { get; set; } // 0.0 to 10.0;

        // Modifier breakdown for transparency;
        public double BaseUrgency { get; set; }
        public double ContextModifier { get; set; }
        public double TimeModifier { get; set; }
        public double UserModifier { get; set; }
        public double SystemModifier { get; set; }
        public double DependencyModifier { get; set; }

        public RecommendedAction RecommendedAction { get; set; }
        public TimeSpan EstimatedProcessingTime { get; set; }
        public DateTime CalculatedAt { get; set; }
        public bool IsValid { get; set; }

        // Adjustment information;
        public bool AdjustmentApplied { get; set; }
        public double? AdjustmentFactor { get; set; }
        public AdjustmentReason? AdjustmentReason { get; set; }

        public static UrgencyResult Error(string errorMessage)
        {
            return new UrgencyResult;
            {
                UrgencyScore = 0,
                UrgencyLevel = UrgencyLevel.None,
                IsValid = false;
            };
        }
    }

    /// <summary>
    /// Urgency prediction for future planning;
    /// </summary>
    public class UrgencyPrediction;
    {
        public string TaskType { get; set; }
        public string UserId { get; set; }
        public PredictionWindow Window { get; set; }
        public double BasePrediction { get; set; }
        public double Confidence { get; set; } // 0.0 to 1.0;
        public DateTime PeakUrgencyTime { get; set; }
        public string RecommendedPreemptiveAction { get; set; }
        public Dictionary<DateTime, double> TimeBasedPredictions { get; set; }
        public DateTime PredictedAt { get; set; }

        public UrgencyPrediction()
        {
            TimeBasedPredictions = new Dictionary<DateTime, double>();
        }
    }

    /// <summary>
    /// User-specific urgency profile;
    /// </summary>
    public class UrgencyProfile;
    {
        public string UserId { get; set; }
        public double BaseUrgencyMultiplier { get; set; }
        public double TimeSensitivity { get; set; } // 0.0 to 1.0;
        public double HistoricalUrgencyAverage { get; set; }
        public TimeSpan ResponseTimeExpectation { get; set; }
        public int MaxConcurrentUrgentTasks { get; set; }
        public Dictionary<string, object> BehavioralPatterns { get; set; }

        public UrgencyProfile()
        {
            BehavioralPatterns = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Task dependency information;
    /// </summary>
    public class TaskDependency;
    {
        public string DependencyId { get; set; }
        public string Description { get; set; }
        public bool IsCritical { get; set; }
        public DependencyStatus Status { get; set; }
        public DateTime? EstimatedCompletion { get; set; }
    }

    /// <summary>
    /// Recommended action based on urgency;
    /// </summary>
    public class RecommendedAction;
    {
        public ActionType ActionType { get; set; }
        public bool PreemptOtherTasks { get; set; }
        public bool NotifyUsers { get; set; }
        public string EscalateTo { get; set; }
        public TimeSpan Timeout { get; set; }
        public List<string> RequiredResources { get; set; }

        public RecommendedAction()
        {
            RequiredResources = new List<string>();
        }
    }

    /// <summary>
    /// Configuration for urgency calculation;
    /// </summary>
    public class UrgencyConfiguration;
    {
        public static UrgencyConfiguration Default => new UrgencyConfiguration();

        // Scoring ranges;
        public double MinUrgencyScore { get; set; } = 0.0;
        public double MaxUrgencyScore { get; set; } = 10.0;
        public double DefaultTaskUrgency { get; set; } = 5.0;

        // Thresholds for urgency levels;
        public double CriticalThreshold { get; set; } = 8.5;
        public double HighThreshold { get; set; } = 6.5;
        public double MediumThreshold { get; set; } = 4.0;
        public double LowThreshold { get; set; } = 2.0;

        // Modifier bounds;
        public double MinContextModifier { get; set; } = 0.5;
        public double MaxContextModifier { get; set; } = 3.0;
        public double MinTimeModifier { get; set; } = 0.5;
        public double MaxTimeModifier { get; set; } = 2.0;
        public double MinUserModifier { get; set; } = 0.5;
        public double MaxUserModifier { get; set; } = 2.0;
        public double MinSystemModifier { get; set; } = 0.5;
        public double MaxSystemModifier { get; set; } = 2.0;
        public double MinDependencyModifier { get; set; } = 0.3;
        public double MaxDependencyModifier { get; set; } = 3.0;

        // Special multipliers;
        public double EmergencyKeywordMultiplier { get; set; } = 1.5;
        public double ImminentDeadlineMultiplier { get; set; } = 1.8;
        public double NearDeadlineMultiplier { get; set; } = 1.3;
        public double SystemEmergencyMultiplier { get; set; } = 2.0;
        public double BlockedCriticalDependencyMultiplier { get; set; } = 1.7;
        public double PendingCriticalDependencyMultiplier { get; set; } = 1.3;

        // Time-based multipliers;
        public double NightTimeMultiplier { get; set; } = 0.8;
        public double BusinessHoursMultiplier { get; set; } = 1.2;
        public double WeekendMultiplier { get; set; } = 0.7;

        // User role multipliers;
        public double AdminMultiplier { get; set; } = 1.3;
        public double PowerUserMultiplier { get; set; } = 1.1;

        // Priority calculation weights;
        public double UrgencyWeight { get; set; } = 0.7;
        public double ImportanceWeight { get; set; } = 0.3;

        // Normalization factor;
        public double NormalizationFactor { get; set; } = 2.0;

        // Expected response times;
        public TimeSpan ExpectedResponseTime { get; set; } = TimeSpan.FromSeconds(5);

        // Processing time bounds;
        public TimeSpan MinProcessingTime { get; set; } = TimeSpan.FromMilliseconds(100);
        public TimeSpan MaxProcessingTime { get; set; } = TimeSpan.FromHours(24);
    }

    /// <summary>
    /// System health information;
    /// </summary>
    public class SystemHealth;
    {
        public double CpuUsage { get; set; } // Percentage;
        public double MemoryUsage { get; set; } // Percentage;
        public double DiskUsage { get; set; } // Percentage;
        public double NetworkLatency { get; set; } // Milliseconds;
        public bool HasCriticalErrors { get; set; }
        public List<string> ActiveWarnings { get; set; }
        public DateTime MeasuredAt { get; set; }

        public SystemHealth()
        {
            ActiveWarnings = new List<string>();
            MeasuredAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Historical pattern for prediction;
    /// </summary>
    public class HistoricalPattern;
    {
        public Dictionary<DayOfWeek, double> DailyPatterns { get; set; }
        public Dictionary<int, double> HourlyPatterns { get; set; } // Hour -> urgency;
        public List<double> RecentScores { get; set; }
        public double Trend { get; set; } // -1.0 to 1.0;

        public HistoricalPattern()
        {
            DailyPatterns = new Dictionary<DayOfWeek, double>();
            HourlyPatterns = new Dictionary<int, double>();
            RecentScores = new List<double>();
        }
    }

    #region Enumerations;

    public enum UrgencyLevel;
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    public enum UserRole;
    {
        Guest = 0,
        User = 1,
        PowerUser = 2,
        Administrator = 3,
        System = 4;
    }

    public enum DependencyStatus;
    {
        Pending = 0,
        InProgress = 1,
        Completed = 2,
        Blocked = 3,
        Failed = 4;
    }

    public enum ActionType;
    {
        DeferredExecution = 0,
        BackgroundExecution = 1,
        NormalExecution = 2,
        PrioritizedExecution = 3,
        ImmediateExecution = 4;
    }

    public enum AdjustmentReason;
    {
        UserFeedback = 0,
        SystemPerformance = 1,
        ExternalEvent = 2,
        ManualOverride = 3,
        TimeDecay = 4;
    }

    public enum PredictionWindow;
    {
        NextHour = 0,
        NextDay = 1,
        NextWeek = 2,
        NextMonth = 3;
    }

    #endregion;

    /// <summary>
    /// Custom exception for urgency calculation errors;
    /// </summary>
    public class UrgencyCalculationException : Exception
    {
        public UrgencyCalculationException(string message) : base(message) { }
        public UrgencyCalculationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
