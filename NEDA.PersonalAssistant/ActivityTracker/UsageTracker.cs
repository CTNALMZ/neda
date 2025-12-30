using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Services.EventBus;
using NEDA.Monitoring.MetricsCollector;

namespace NEDA.PersonalAssistant.ActivityTracker;
{
    /// <summary>
    /// Kullanıcı aktivite ve kullanım takibi için gelişmiş izleme motoru.
    /// </summary>
    public interface IUsageTracker : IDisposable
    {
        /// <summary>
        /// Belirli bir aktiviteyi başlatır.
        /// </summary>
        /// <param name="activity">Aktivite türü</param>
        /// <param name="context">Bağlamsal bilgiler</param>
        void StartActivity(ActivityType activity, ActivityContext context);

        /// <summary>
        /// Belirli bir aktiviteyi sonlandırır.
        /// </summary>
        /// <param name="activity">Aktivite türü</param>
        /// <param name="context">Bağlamsal bilgiler</param>
        void StopActivity(ActivityType activity, ActivityContext context);

        /// <summary>
        /// Belirli bir aktivite için metrik kaydeder.
        /// </summary>
        /// <param name="activity">Aktivite türü</param>
        /// <param name="metric">Metrik adı</param>
        /// <param name="value">Metrik değeri</param>
        void RecordMetric(ActivityType activity, string metric, double value);

        /// <summary>
        /// Belirli bir kullanıcı için aktivite özeti getirir.
        /// </summary>
        /// <param name="userId">Kullanıcı ID</param>
        /// <param name="timeRange">Zaman aralığı</param>
        /// <returns>Aktivite özeti</returns>
        UsageSummary GetUsageSummary(Guid userId, TimeRange timeRange);

        /// <summary>
        /// Aktivite trend analizi yapar.
        /// </summary>
        /// <param name="userId">Kullanıcı ID</param>
        /// <param name="activity">Aktivite türü</param>
        /// <param name="days">Gün sayısı</param>
        /// <returns>Trend analizi</returns>
        ActivityTrend AnalyzeTrend(Guid userId, ActivityType activity, int days);

        /// <summary>
        /// Kullanım alışkanlıklarını tespit eder.
        /// </summary>
        /// <param name="userId">Kullanıcı ID</param>
        /// <returns>Kullanım alışkanlıkları</returns>
        UsagePatterns DetectPatterns(Guid userId);

        /// <summary>
        /// Anomalik kullanım tespiti yapar.
        /// </summary>
        /// <param name="userId">Kullanıcı ID</param>
        /// <returns>Anomalik aktiviteler</returns>
        IEnumerable<ActivityAnomaly> DetectAnomalies(Guid userId);

        /// <summary>
        /// Kullanım istatistiklerini getirir.
        /// </summary>
        /// <param name="timeRange">Zaman aralığı</param>
        /// <returns>Kullanım istatistikleri</returns>
        UsageStatistics GetStatistics(TimeRange timeRange);

        /// <summary>
        /// Kullanım verilerini dışa aktarır.
        /// </summary>
        /// <param name="userId">Kullanıcı ID</param>
        /// <param name="format">Dışa aktarma formatı</param>
        /// <returns>Dışa aktarılan veri</returns>
        byte[] ExportUsageData(Guid userId, ExportFormat format);
    }

    /// <summary>
    /// Kullanım takip motoru implementasyonu.
    /// </summary>
    public class UsageTracker : IUsageTracker;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly ConcurrentDictionary<Guid, UserActivitySession> _activeSessions;
        private readonly ActivityStore _activityStore;
        private readonly PatternAnalyzer _patternAnalyzer;
        private readonly AnomalyDetector _anomalyDetector;
        private readonly StatisticsEngine _statisticsEngine;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        /// <summary>
        /// Kullanım takip motoru oluşturur.
        /// </summary>
        /// <param name="logger">Logger</param>
        /// <param name="metricsCollector">Metrik toplayıcı</param>
        /// <param name="eventBus">Event bus</param>
        public UsageTracker(ILogger logger, IMetricsCollector metricsCollector, IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _activeSessions = new ConcurrentDictionary<Guid, UserActivitySession>();
            _activityStore = new ActivityStore();
            _patternAnalyzer = new PatternAnalyzer();
            _anomalyDetector = new AnomalyDetector();
            _statisticsEngine = new StatisticsEngine();

            // Her 5 dakikada bir temizlik yap;
            _cleanupTimer = new Timer(CleanupStaleSessions, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            _eventBus.Subscribe<ActivityStartedEvent>(HandleActivityStarted);
            _eventBus.Subscribe<ActivityCompletedEvent>(HandleActivityCompleted);

            _logger.LogInformation("UsageTracker initialized");
        }

        /// <summary>
        /// Aktivite başlatma işlemi.
        /// </summary>
        public void StartActivity(ActivityType activity, ActivityContext context)
        {
            ValidateContext(context);

            var sessionId = Guid.NewGuid();
            var session = new UserActivitySession(sessionId, context.UserId, activity, context);

            if (_activeSessions.TryAdd(sessionId, session))
            {
                session.Start();
                _activityStore.RecordStart(session);
                _metricsCollector.RecordMetric($"activity.{activity}.started", 1);

                var @event = new ActivityStartedEvent;
                {
                    SessionId = sessionId,
                    UserId = context.UserId,
                    Activity = activity,
                    StartTime = DateTime.UtcNow,
                    Context = context;
                };

                _eventBus.Publish(@event);
                _logger.LogDebug($"Activity started: {activity} for user {context.UserId}");
            }
        }

        /// <summary>
        /// Aktivite sonlandırma işlemi.
        /// </summary>
        public void StopActivity(ActivityType activity, ActivityContext context)
        {
            ValidateContext(context);

            // Kullanıcının aktif oturumlarını bul;
            var userSessions = _activeSessions.Values;
                .Where(s => s.UserId == context.UserId && s.Activity == activity && s.IsActive)
                .ToList();

            foreach (var session in userSessions)
            {
                if (_activeSessions.TryRemove(session.SessionId, out _))
                {
                    session.End();
                    _activityStore.RecordEnd(session);
                    _metricsCollector.RecordMetric($"activity.{activity}.completed", 1);
                    _metricsCollector.RecordMetric($"activity.{activity}.duration",
                        session.Duration.TotalSeconds);

                    var @event = new ActivityCompletedEvent;
                    {
                        SessionId = session.SessionId,
                        UserId = context.UserId,
                        Activity = activity,
                        StartTime = session.StartTime,
                        EndTime = session.EndTime.Value,
                        Duration = session.Duration,
                        Context = context;
                    };

                    _eventBus.Publish(@event);
                    _logger.LogDebug($"Activity completed: {activity} for user {context.UserId}, duration: {session.Duration}");
                }
            }
        }

        /// <summary>
        /// Metrik kaydetme işlemi.
        /// </summary>
        public void RecordMetric(ActivityType activity, string metric, double value)
        {
            if (string.IsNullOrWhiteSpace(metric))
                throw new ArgumentException("Metric name cannot be null or empty", nameof(metric));

            var fullMetricName = $"activity.{activity}.{metric}";
            _metricsCollector.RecordMetric(fullMetricName, value);

            _activityStore.RecordMetric(activity, metric, value, DateTime.UtcNow);

            _logger.LogTrace($"Metric recorded: {fullMetricName} = {value}");
        }

        /// <summary>
        /// Kullanım özeti getirme işlemi.
        /// </summary>
        public UsageSummary GetUsageSummary(Guid userId, TimeRange timeRange)
        {
            var activities = _activityStore.GetActivities(userId, timeRange.Start, timeRange.End);

            return new UsageSummary;
            {
                UserId = userId,
                TimeRange = timeRange,
                TotalActivities = activities.Count,
                TotalDuration = TimeSpan.FromSeconds(
                    activities.Sum(a => a.Duration?.TotalSeconds ?? 0)),
                ActivitiesByType = activities;
                    .GroupBy(a => a.Activity)
                    .ToDictionary(g => g.Key, g => g.Count()),
                DailyAverage = CalculateDailyAverage(activities),
                PeakUsageHours = CalculatePeakHours(activities),
                MostUsedFeatures = GetMostUsedFeatures(activities)
            };
        }

        /// <summary>
        /// Trend analizi işlemi.
        /// </summary>
        public ActivityTrend AnalyzeTrend(Guid userId, ActivityType activity, int days)
        {
            var endDate = DateTime.UtcNow;
            var startDate = endDate.AddDays(-days);

            var dailyStats = _activityStore.GetDailyActivityStats(userId, activity, startDate, endDate);

            return new ActivityTrend;
            {
                UserId = userId,
                Activity = activity,
                PeriodDays = days,
                DailyUsage = dailyStats,
                GrowthRate = CalculateGrowthRate(dailyStats),
                Seasonality = DetectSeasonality(dailyStats),
                Predictions = PredictFutureUsage(dailyStats)
            };
        }

        /// <summary>
        /// Kullanım alışkanlıkları tespit işlemi.
        /// </summary>
        public UsagePatterns DetectPatterns(Guid userId)
        {
            var activities = _activityStore.GetAllActivities(userId);

            return new UsagePatterns;
            {
                UserId = userId,
                PreferredTimeSlots = _patternAnalyzer.DetectPreferredTimes(activities),
                FrequentActivities = _patternAnalyzer.DetectFrequentActivities(activities),
                ActivitySequences = _patternAnalyzer.DetectActivitySequences(activities),
                SessionLengthPatterns = _patternAnalyzer.AnalyzeSessionLengths(activities),
                BehavioralClusters = _patternAnalyzer.ClusterBehaviors(activities)
            };
        }

        /// <summary>
        /// Anomalik kullanım tespit işlemi.
        /// </summary>
        public IEnumerable<ActivityAnomaly> DetectAnomalies(Guid userId)
        {
            var activities = _activityStore.GetActivities(userId,
                DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

            var anomalies = _anomalyDetector.Detect(activities);

            foreach (var anomaly in anomalies)
            {
                _logger.LogWarning($"Anomaly detected for user {userId}: {anomaly.Type} at {anomaly.Timestamp}");

                // Anomaliler için event yayınla;
                var @event = new AnomalyDetectedEvent;
                {
                    UserId = userId,
                    Anomaly = anomaly,
                    Timestamp = DateTime.UtcNow;
                };

                _eventBus.Publish(@event);
            }

            return anomalies;
        }

        /// <summary>
        /// Kullanım istatistikleri getirme işlemi.
        /// </summary>
        public UsageStatistics GetStatistics(TimeRange timeRange)
        {
            return _statisticsEngine.GenerateStatistics(_activityStore, timeRange);
        }

        /// <summary>
        /// Kullanım verilerini dışa aktarma işlemi.
        /// </summary>
        public byte[] ExportUsageData(Guid userId, ExportFormat format)
        {
            var activities = _activityStore.GetAllActivities(userId);
            var patterns = DetectPatterns(userId);
            var summary = GetUsageSummary(userId,
                new TimeRange(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow));

            return format switch;
            {
                ExportFormat.Json => ExportToJson(userId, activities, patterns, summary),
                ExportFormat.Csv => ExportToCsv(activities),
                ExportFormat.Xml => ExportToXml(userId, activities, patterns, summary),
                _ => throw new NotSupportedException($"Export format {format} is not supported")
            };
        }

        /// <summary>
        /// Dispose pattern implementasyonu.
        /// </summary>
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
                    _cleanupTimer?.Dispose();
                    _activityStore?.Dispose();

                    if (_eventBus != null)
                    {
                        _eventBus.Unsubscribe<ActivityStartedEvent>(HandleActivityStarted);
                        _eventBus.Unsubscribe<ActivityCompletedEvent>(HandleActivityCompleted);
                    }
                }

                _disposed = true;
            }
        }

        #region Private Methods;

        private void ValidateContext(ActivityContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (context.UserId == Guid.Empty)
                throw new ArgumentException("UserId cannot be empty", nameof(context.UserId));

            if (string.IsNullOrWhiteSpace(context.SessionId))
                throw new ArgumentException("SessionId cannot be null or empty", nameof(context.SessionId));
        }

        private void CleanupStaleSessions(object state)
        {
            try
            {
                var staleThreshold = TimeSpan.FromHours(1);
                var now = DateTime.UtcNow;

                var staleSessions = _activeSessions.Values;
                    .Where(s => s.IsActive && (now - s.LastActivity) > staleThreshold)
                    .ToList();

                foreach (var session in staleSessions)
                {
                    if (_activeSessions.TryRemove(session.SessionId, out _))
                    {
                        session.End(force: true);
                        _activityStore.RecordEnd(session);

                        _logger.LogWarning($"Stale session cleaned up: {session.SessionId} for user {session.UserId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up stale sessions");
            }
        }

        private void HandleActivityStarted(ActivityStartedEvent @event)
        {
            // Event işleme mantığı;
            // Örneğin, real-time monitoring için;
        }

        private void HandleActivityCompleted(ActivityCompletedEvent @event)
        {
            // Event işleme mantığı;
            // Örneğin, aktivite tamamlandığında analiz yap;
        }

        private double CalculateDailyAverage(List<ActivityRecord> activities)
        {
            if (activities.Count == 0) return 0;

            var groupedByDay = activities;
                .GroupBy(a => a.StartTime.Date)
                .Select(g => g.Count())
                .ToList();

            return groupedByDay.Any() ? groupedByDay.Average() : 0;
        }

        private Dictionary<int, int> CalculatePeakHours(List<ActivityRecord> activities)
        {
            return activities;
                .GroupBy(a => a.StartTime.Hour)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private List<string> GetMostUsedFeatures(List<ActivityRecord> activities)
        {
            // Özelliklere göre grupla ve en çok kullanılanları getir;
            return activities;
                .Where(a => a.Context?.Features != null)
                .SelectMany(a => a.Context.Features)
                .GroupBy(f => f)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => g.Key)
                .ToList();
        }

        private double CalculateGrowthRate(Dictionary<DateTime, int> dailyStats)
        {
            if (dailyStats.Count < 2) return 0;

            var sorted = dailyStats.OrderBy(kvp => kvp.Key).ToList();
            var first = sorted.First().Value;
            var last = sorted.Last().Value;

            if (first == 0) return last > 0 ? 100 : 0;

            return ((last - first) / (double)first) * 100;
        }

        private SeasonalityPattern DetectSeasonality(Dictionary<DateTime, int> dailyStats)
        {
            // Haftalık/aylık mevsimsellik tespiti;
            return _patternAnalyzer.DetectSeasonality(dailyStats);
        }

        private Dictionary<DateTime, int> PredictFutureUsage(Dictionary<DateTime, int> historicalData)
        {
            // Gelecek kullanım tahmini;
            return _statisticsEngine.Predict(historicalData, 7); // 7 gün tahmini;
        }

        private byte[] ExportToJson(Guid userId, List<ActivityRecord> activities,
            UsagePatterns patterns, UsageSummary summary)
        {
            var exportData = new ExportData;
            {
                UserId = userId,
                Activities = activities,
                Patterns = patterns,
                Summary = summary,
                ExportTimestamp = DateTime.UtcNow;
            };

            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        private byte[] ExportToCsv(List<ActivityRecord> activities)
        {
            using var memoryStream = new System.IO.MemoryStream();
            using var writer = new System.IO.StreamWriter(memoryStream);

            // CSV başlığı;
            writer.WriteLine("SessionId,UserId,Activity,StartTime,EndTime,DurationSeconds,Context");

            foreach (var activity in activities)
            {
                writer.WriteLine($"\"{activity.SessionId}\",\"{activity.UserId}\",\"{activity.Activity}\"," +
                               $"\"{activity.StartTime:o}\",\"{activity.EndTime:o}\"," +
                               $"\"{activity.Duration?.TotalSeconds}\",\"{activity.Context?.ToJson()}\"");
            }

            writer.Flush();
            return memoryStream.ToArray();
        }

        private byte[] ExportToXml(Guid userId, List<ActivityRecord> activities,
            UsagePatterns patterns, UsageSummary summary)
        {
            // XML export implementasyonu;
            var xmlDocument = new System.Xml.XmlDocument();
            var root = xmlDocument.CreateElement("UsageData");
            xmlDocument.AppendChild(root);

            var userIdElement = xmlDocument.CreateElement("UserId");
            userIdElement.InnerText = userId.ToString();
            root.AppendChild(userIdElement);

            var activitiesElement = xmlDocument.CreateElement("Activities");
            root.AppendChild(activitiesElement);

            foreach (var activity in activities)
            {
                var activityElement = xmlDocument.CreateElement("Activity");
                activityElement.SetAttribute("Type", activity.Activity.ToString());
                activityElement.SetAttribute("StartTime", activity.StartTime.ToString("o"));
                activitiesElement.AppendChild(activityElement);
            }

            using var memoryStream = new System.IO.MemoryStream();
            xmlDocument.Save(memoryStream);
            return memoryStream.ToArray();
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Aktivite türleri enum'u.
    /// </summary>
    public enum ActivityType;
    {
        CommandExecution,
        FileEditing,
        ProjectNavigation,
        CodeReview,
        Debugging,
        BuildProcess,
        Testing,
        Deployment,
        Monitoring,
        SystemConfiguration,
        DataAnalysis,
        MachineLearning,
        ContentCreation,
        GameDevelopment,
        SecurityScan,
        BackupOperation,
        UserInterface,
        Documentation,
        Communication,
        Other;
    }

    /// <summary>
    /// Dışa aktarma formatları.
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Csv,
        Xml;
    }

    /// <summary>
    /// Zaman aralığı.
    /// </summary>
    public struct TimeRange;
    {
        public DateTime Start { get; }
        public DateTime End { get; }

        public TimeRange(DateTime start, DateTime end)
        {
            if (start >= end)
                throw new ArgumentException("Start time must be before end time");

            Start = start;
            End = end;
        }
    }

    /// <summary>
    /// Aktivite bağlamı.
    /// </summary>
    public class ActivityContext;
    {
        public Guid UserId { get; set; }
        public string SessionId { get; set; }
        public string Application { get; set; }
        public string Module { get; set; }
        public string Action { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public List<string> Features { get; set; }
        public Dictionary<string, double> Metrics { get; set; }

        public string ToJson()
        {
            return System.Text.Json.JsonSerializer.Serialize(this);
        }
    }

    /// <summary>
    /// Kullanıcı aktivite oturumu.
    /// </summary>
    public class UserActivitySession;
    {
        public Guid SessionId { get; }
        public Guid UserId { get; }
        public ActivityType Activity { get; }
        public ActivityContext Context { get; }
        public DateTime StartTime { get; private set; }
        public DateTime? EndTime { get; private set; }
        public TimeSpan? Duration => EndTime.HasValue ? EndTime - StartTime : null;
        public DateTime LastActivity { get; private set; }
        public bool IsActive => !EndTime.HasValue;

        private readonly object _lock = new object();

        public UserActivitySession(Guid sessionId, Guid userId, ActivityType activity, ActivityContext context)
        {
            SessionId = sessionId;
            UserId = userId;
            Activity = activity;
            Context = context ?? throw new ArgumentNullException(nameof(context));
            LastActivity = DateTime.UtcNow;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (StartTime != default)
                    throw new InvalidOperationException("Session already started");

                StartTime = DateTime.UtcNow;
                LastActivity = StartTime;
            }
        }

        public void End(bool force = false)
        {
            lock (_lock)
            {
                if (EndTime.HasValue && !force)
                    throw new InvalidOperationException("Session already ended");

                EndTime = DateTime.UtcNow;
            }
        }

        public void UpdateActivity()
        {
            lock (_lock)
            {
                if (IsActive)
                {
                    LastActivity = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>
    /// Aktivite kaydı.
    /// </summary>
    public class ActivityRecord;
    {
        public Guid SessionId { get; set; }
        public Guid UserId { get; set; }
        public ActivityType Activity { get; set; }
        public ActivityContext Context { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Duration => EndTime.HasValue ? EndTime - StartTime : null;
        public Dictionary<string, double> Metrics { get; set; }
    }

    /// <summary>
    /// Kullanım özeti.
    /// </summary>
    public class UsageSummary;
    {
        public Guid UserId { get; set; }
        public TimeRange TimeRange { get; set; }
        public int TotalActivities { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public Dictionary<ActivityType, int> ActivitiesByType { get; set; }
        public double DailyAverage { get; set; }
        public Dictionary<int, int> PeakUsageHours { get; set; }
        public List<string> MostUsedFeatures { get; set; }
    }

    /// <summary>
    /// Aktivite trend analizi.
    /// </summary>
    public class ActivityTrend;
    {
        public Guid UserId { get; set; }
        public ActivityType Activity { get; set; }
        public int PeriodDays { get; set; }
        public Dictionary<DateTime, int> DailyUsage { get; set; }
        public double GrowthRate { get; set; }
        public SeasonalityPattern Seasonality { get; set; }
        public Dictionary<DateTime, int> Predictions { get; set; }
    }

    /// <summary>
    /// Kullanım alışkanlıkları.
    /// </summary>
    public class UsagePatterns;
    {
        public Guid UserId { get; set; }
        public Dictionary<string, int> PreferredTimeSlots { get; set; }
        public Dictionary<ActivityType, int> FrequentActivities { get; set; }
        public List<List<ActivityType>> ActivitySequences { get; set; }
        public Dictionary<TimeSpan, int> SessionLengthPatterns { get; set; }
        public List<BehavioralCluster> BehavioralClusters { get; set; }
    }

    /// <summary>
    /// Aktivite anomalisi.
    /// </summary>
    public class ActivityAnomaly;
    {
        public Guid SessionId { get; set; }
        public Guid UserId { get; set; }
        public ActivityType Activity { get; set; }
        public AnomalyType Type { get; set; }
        public DateTime Timestamp { get; set; }
        public double Severity { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Kullanım istatistikleri.
    /// </summary>
    public class UsageStatistics;
    {
        public int TotalUsers { get; set; }
        public int TotalActivities { get; set; }
        public TimeSpan AverageSessionDuration { get; set; }
        public Dictionary<ActivityType, int> ActivityDistribution { get; set; }
        public Dictionary<TimeSpan, int> SessionDurationDistribution { get; set; }
        public Dictionary<string, int> ApplicationUsage { get; set; }
        public List<Guid> TopActiveUsers { get; set; }
    }

    /// <summary>
    /// Anomali türleri.
    /// </summary>
    public enum AnomalyType;
    {
        UnusualDuration,
        AbnormalFrequency,
        StrangeTime,
        UncommonSequence,
        ExcessiveUsage,
        SecurityRisk;
    }

    /// <summary>
    /// Mevsimsellik deseni.
    /// </summary>
    public class SeasonalityPattern;
    {
        public bool HasWeeklyPattern { get; set; }
        public bool HasMonthlyPattern { get; set; }
        public Dictionary<DayOfWeek, double> WeeklyPattern { get; set; }
        public List<int> PeakDays { get; set; }
    }

    /// <summary>
    /// Davranışsal küme.
    /// </summary>
    public class BehavioralCluster;
    {
        public int ClusterId { get; set; }
        public string PatternName { get; set; }
        public List<ActivityType> CharacteristicActivities { get; set; }
        public TimeSpan TypicalDuration { get; set; }
        public List<Guid> UserIds { get; set; }
    }

    /// <summary>
    /// Aktivite başlangıç event'i.
    /// </summary>
    public class ActivityStartedEvent : IEvent;
    {
        public Guid SessionId { get; set; }
        public Guid UserId { get; set; }
        public ActivityType Activity { get; set; }
        public DateTime StartTime { get; set; }
        public ActivityContext Context { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Aktivite tamamlama event'i.
    /// </summary>
    public class ActivityCompletedEvent : IEvent;
    {
        public Guid SessionId { get; set; }
        public Guid UserId { get; set; }
        public ActivityType Activity { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public ActivityContext Context { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Anomali tespit event'i.
    /// </summary>
    public class AnomalyDetectedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public ActivityAnomaly Anomaly { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Dışa aktarma verisi.
    /// </summary>
    public class ExportData;
    {
        public Guid UserId { get; set; }
        public List<ActivityRecord> Activities { get; set; }
        public UsagePatterns Patterns { get; set; }
        public UsageSummary Summary { get; set; }
        public DateTime ExportTimestamp { get; set; }
    }

    #endregion;

    #region Internal Components;

    /// <summary>
    /// Aktivite saklama motoru.
    /// </summary>
    internal class ActivityStore : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, List<ActivityRecord>> _userActivities;
        private readonly System.Collections.Concurrent.ConcurrentQueue<ActivityRecord> _activityQueue;
        private readonly System.Threading.Tasks.Task _processingTask;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly System.Data.Common.DbConnection _databaseConnection;

        public ActivityStore()
        {
            _userActivities = new ConcurrentDictionary<Guid, List<ActivityRecord>>();
            _activityQueue = new System.Collections.Concurrent.ConcurrentQueue<ActivityRecord>();
            _cancellationTokenSource = new CancellationTokenSource();

            // Veritabanı bağlantısı (örnek - gerçek implementasyonda config'den gelmeli)
            var connectionString = "Data Source=activities.db";
            _databaseConnection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            _databaseConnection.Open();

            InitializeDatabase();

            _processingTask = Task.Run(async () => await ProcessQueueAsync(_cancellationTokenSource.Token));
        }

        public void RecordStart(UserActivitySession session)
        {
            var record = new ActivityRecord;
            {
                SessionId = session.SessionId,
                UserId = session.UserId,
                Activity = session.Activity,
                Context = session.Context,
                StartTime = session.StartTime,
                Metrics = new Dictionary<string, double>()
            };

            _activityQueue.Enqueue(record);

            var userActivities = _userActivities.GetOrAdd(session.UserId, _ => new List<ActivityRecord>());
            lock (userActivities)
            {
                userActivities.Add(record);
            }
        }

        public void RecordEnd(UserActivitySession session)
        {
            var userActivities = _userActivities.GetOrAdd(session.UserId, _ => new List<ActivityRecord>());

            lock (userActivities)
            {
                var record = userActivities.FirstOrDefault(r => r.SessionId == session.SessionId);
                if (record != null)
                {
                    record.EndTime = session.EndTime;
                }
            }

            // Veritabanına kaydet;
            SaveToDatabase(session);
        }

        public void RecordMetric(ActivityType activity, string metric, double value, DateTime timestamp)
        {
            // Metrikleri özel bir koleksiyonda sakla;
            // Burada implementasyon detayları;
        }

        public List<ActivityRecord> GetActivities(Guid userId, DateTime start, DateTime end)
        {
            if (_userActivities.TryGetValue(userId, out var activities))
            {
                lock (activities)
                {
                    return activities;
                        .Where(a => a.StartTime >= start && a.StartTime <= end)
                        .ToList();
                }
            }

            return new List<ActivityRecord>();
        }

        public List<ActivityRecord> GetAllActivities(Guid userId)
        {
            if (_userActivities.TryGetValue(userId, out var activities))
            {
                lock (activities)
                {
                    return new List<ActivityRecord>(activities);
                }
            }

            return new List<ActivityRecord>();
        }

        public Dictionary<DateTime, int> GetDailyActivityStats(Guid userId, ActivityType activity,
            DateTime start, DateTime end)
        {
            var activities = GetActivities(userId, start, end)
                .Where(a => a.Activity == activity)
                .ToList();

            return activities;
                .GroupBy(a => a.StartTime.Date)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_activityQueue.TryDequeue(out var record))
                    {
                        await SaveRecordToDatabaseAsync(record);
                    }
                    else;
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error processing activity queue: {ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private void InitializeDatabase()
        {
            using var command = _databaseConnection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Activities (
                    SessionId TEXT PRIMARY KEY,
                    UserId TEXT NOT NULL,
                    ActivityType INTEGER NOT NULL,
                    StartTime DATETIME NOT NULL,
                    EndTime DATETIME,
                    ContextJson TEXT,
                    MetricsJson TEXT;
                );
                
                CREATE INDEX IF NOT EXISTS idx_activities_user ON Activities(UserId);
                CREATE INDEX IF NOT EXISTS idx_activities_time ON Activities(StartTime);
                CREATE INDEX IF NOT EXISTS idx_activities_type ON Activities(ActivityType);
            ";
            command.ExecuteNonQuery();
        }

        private void SaveToDatabase(UserActivitySession session)
        {
            using var command = _databaseConnection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO Activities; 
                (SessionId, UserId, ActivityType, StartTime, EndTime, ContextJson)
                VALUES (@sessionId, @userId, @activityType, @startTime, @endTime, @contextJson)
            ";

            command.Parameters.AddWithValue("@sessionId", session.SessionId.ToString());
            command.Parameters.AddWithValue("@userId", session.UserId.ToString());
            command.Parameters.AddWithValue("@activityType", (int)session.Activity);
            command.Parameters.AddWithValue("@startTime", session.StartTime);
            command.Parameters.AddWithValue("@endTime", session.EndTime);
            command.Parameters.AddWithValue("@contextJson", session.Context?.ToJson() ?? "{}");

            command.ExecuteNonQuery();
        }

        private async Task SaveRecordToDatabaseAsync(ActivityRecord record)
        {
            // Asenkron veritabanı kaydetme işlemi;
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _processingTask.Wait(TimeSpan.FromSeconds(5));
            _cancellationTokenSource.Dispose();
            _databaseConnection?.Close();
            _databaseConnection?.Dispose();
        }
    }

    /// <summary>
    /// Desen analiz motoru.
    /// </summary>
    internal class PatternAnalyzer;
    {
        public Dictionary<string, int> DetectPreferredTimes(List<ActivityRecord> activities)
        {
            var timeSlots = new Dictionary<string, int>();

            foreach (var activity in activities)
            {
                var hour = activity.StartTime.Hour;
                var slot = GetTimeSlot(hour);

                if (timeSlots.ContainsKey(slot))
                    timeSlots[slot]++;
                else;
                    timeSlots[slot] = 1;
            }

            return timeSlots.OrderByDescending(kvp => kvp.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        public Dictionary<ActivityType, int> DetectFrequentActivities(List<ActivityRecord> activities)
        {
            return activities;
                .GroupBy(a => a.Activity)
                .ToDictionary(g => g.Key, g => g.Count())
                .OrderByDescending(kvp => kvp.Value)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        public List<List<ActivityType>> DetectActivitySequences(List<ActivityRecord> activities)
        {
            var sequences = new List<List<ActivityType>>();
            var sortedActivities = activities.OrderBy(a => a.StartTime).ToList();

            for (int i = 0; i < sortedActivities.Count - 1; i++)
            {
                var current = sortedActivities[i];
                var next = sortedActivities[i + 1];

                // 5 dakika içindeki aktiviteleri sıralı olarak grupla;
                if ((next.StartTime - current.StartTime) <= TimeSpan.FromMinutes(5))
                {
                    var sequence = new List<ActivityType> { current.Activity, next.Activity };
                    sequences.Add(sequence);
                }
            }

            return sequences;
        }

        public Dictionary<TimeSpan, int> AnalyzeSessionLengths(List<ActivityRecord> activities)
        {
            return activities;
                .Where(a => a.Duration.HasValue)
                .GroupBy(a => RoundDuration(a.Duration.Value))
                .ToDictionary(g => g.Key, g => g.Count())
                .OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        public List<BehavioralCluster> ClusterBehaviors(List<ActivityRecord> activities)
        {
            // Basit kümelenme algoritması (gerçek implementasyonda daha karmaşık olacak)
            var clusters = new List<BehavioralCluster>();

            var byDuration = activities;
                .Where(a => a.Duration.HasValue)
                .GroupBy(a => a.Duration.Value.TotalMinutes < 5 ? "Short" :
                             a.Duration.Value.TotalMinutes < 30 ? "Medium" : "Long")
                .ToList();

            int clusterId = 1;
            foreach (var group in byDuration)
            {
                var cluster = new BehavioralCluster;
                {
                    ClusterId = clusterId++,
                    PatternName = $"{group.Key} Sessions",
                    CharacteristicActivities = group;
                        .GroupBy(a => a.Activity)
                        .OrderByDescending(g => g.Count())
                        .Take(3)
                        .Select(g => g.Key)
                        .ToList(),
                    TypicalDuration = group.Any() ?
                        TimeSpan.FromMinutes(group.Average(a => a.Duration.Value.TotalMinutes)) :
                        TimeSpan.Zero,
                    UserIds = group.Select(a => a.UserId).Distinct().ToList()
                };

                clusters.Add(cluster);
            }

            return clusters;
        }

        public SeasonalityPattern DetectSeasonality(Dictionary<DateTime, int> dailyStats)
        {
            var pattern = new SeasonalityPattern();

            if (dailyStats.Count >= 7)
            {
                // Haftalık desen analizi;
                var byDayOfWeek = dailyStats;
                    .GroupBy(kvp => kvp.Key.DayOfWeek)
                    .ToDictionary(g => g.Key, g => g.Average(kvp => kvp.Value));

                pattern.HasWeeklyPattern = byDayOfWeek.Values.Max() > byDayOfWeek.Values.Min() * 1.5;
                pattern.WeeklyPattern = byDayOfWeek.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            if (dailyStats.Count >= 30)
            {
                // Aylık desen analizi;
                var peakDays = dailyStats;
                    .Where(kvp => kvp.Value > dailyStats.Values.Average() * 1.2)
                    .Select(kvp => kvp.Key.Day)
                    .Distinct()
                    .ToList();

                pattern.PeakDays = peakDays;
                pattern.HasMonthlyPattern = peakDays.Any();
            }

            return pattern;
        }

        private string GetTimeSlot(int hour)
        {
            return hour switch;
            {
                >= 0 and < 6 => "Night (0-6)",
                >= 6 and < 12 => "Morning (6-12)",
                >= 12 and < 18 => "Afternoon (12-18)",
                _ => "Evening (18-24)"
            };
        }

        private TimeSpan RoundDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes < 1) return TimeSpan.FromMinutes(1);
            if (duration.TotalMinutes < 5) return TimeSpan.FromMinutes(5);
            if (duration.TotalMinutes < 15) return TimeSpan.FromMinutes(15);
            if (duration.TotalMinutes < 30) return TimeSpan.FromMinutes(30);
            if (duration.TotalMinutes < 60) return TimeSpan.FromMinutes(60);
            return TimeSpan.FromHours(2);
        }
    }

    /// <summary>
    /// Anomali tespit motoru.
    /// </summary>
    internal class AnomalyDetector;
    {
        public List<ActivityAnomaly> Detect(List<ActivityRecord> activities)
        {
            var anomalies = new List<ActivityAnomaly>();

            if (activities.Count == 0) return anomalies;

            // 1. Olağandışı süre tespiti;
            var durations = activities;
                .Where(a => a.Duration.HasValue)
                .Select(a => a.Duration.Value.TotalMinutes)
                .ToList();

            if (durations.Any())
            {
                var avgDuration = durations.Average();
                var stdDev = CalculateStandardDeviation(durations);

                foreach (var activity in activities.Where(a => a.Duration.HasValue))
                {
                    var zScore = Math.Abs(activity.Duration.Value.TotalMinutes - avgDuration) / stdDev;

                    if (zScore > 3.0) // 3 standart sapma dışı;
                    {
                        anomalies.Add(new ActivityAnomaly;
                        {
                            SessionId = activity.SessionId,
                            UserId = activity.UserId,
                            Activity = activity.Activity,
                            Type = activity.Duration.Value.TotalMinutes > avgDuration ?
                                AnomalyType.UnusualDuration : AnomalyType.ExcessiveUsage,
                            Timestamp = activity.StartTime,
                            Severity = Math.Min(zScore / 10.0, 1.0),
                            Description = $"Unusual activity duration: {activity.Duration.Value.TotalMinutes:F1} minutes"
                        });
                    }
                }
            }

            // 2. Olağandışı zaman tespiti;
            var nightActivities = activities;
                .Where(a => a.StartTime.Hour >= 0 && a.StartTime.Hour < 6)
                .ToList();

            if (nightActivities.Count > activities.Count * 0.3) // %30'undan fazla gece aktivitesi;
            {
                anomalies.AddRange(nightActivities.Select(activity => new ActivityAnomaly;
                {
                    SessionId = activity.SessionId,
                    UserId = activity.UserId,
                    Activity = activity.Activity,
                    Type = AnomalyType.StrangeTime,
                    Timestamp = activity.StartTime,
                    Severity = 0.7,
                    Description = "Unusual time for activity"
                }));
            }

            return anomalies.DistinctBy(a => a.SessionId).ToList();
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            if (values.Count < 2) return 0;

            var avg = values.Average();
            var sumOfSquares = values.Sum(v => Math.Pow(v - avg, 2));
            return Math.Sqrt(sumOfSquares / (values.Count - 1));
        }
    }

    /// <summary>
    /// İstatistik motoru.
    /// </summary>
    internal class StatisticsEngine;
    {
        public UsageStatistics GenerateStatistics(ActivityStore store, TimeRange timeRange)
        {
            // Tüm kullanıcıların aktivitelerini getir;
            // Burada implementasyon detayları;

            return new UsageStatistics;
            {
                TotalUsers = 0,
                TotalActivities = 0,
                AverageSessionDuration = TimeSpan.Zero,
                ActivityDistribution = new Dictionary<ActivityType, int>(),
                SessionDurationDistribution = new Dictionary<TimeSpan, int>(),
                ApplicationUsage = new Dictionary<string, int>(),
                TopActiveUsers = new List<Guid>()
            };
        }

        public Dictionary<DateTime, int> Predict(Dictionary<DateTime, int> historicalData, int daysAhead)
        {
            var predictions = new Dictionary<DateTime, int>();

            if (historicalData.Count < 7) return predictions;

            // Basit lineer regresyon tahmini;
            var dates = historicalData.Keys.Select(d => d.ToOADate()).ToList();
            var values = historicalData.Values.ToList();

            var n = dates.Count;
            var sumX = dates.Sum();
            var sumY = values.Sum();
            var sumXY = dates.Zip(values, (x, y) => x * y).Sum();
            var sumX2 = dates.Sum(x => x * x);

            var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            var intercept = (sumY - slope * sumX) / n;

            var lastDate = historicalData.Keys.Max();
            for (int i = 1; i <= daysAhead; i++)
            {
                var predictionDate = lastDate.AddDays(i);
                var predictionValue = Math.Max(0, intercept + slope * predictionDate.ToOADate());

                predictions[predictionDate] = (int)predictionValue;
            }

            return predictions;
        }
    }

    #endregion;
}
