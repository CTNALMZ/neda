using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.VisualInterface.ProgressMonitor;
{
    /// <summary>
    /// Görev ilerlemesini takip etmek, raporlamak ve görselleştirmek için gelişmiş izleme sistemi.
    /// Çoklu görev, hiyerarşik ilerleme ve gerçek zamanlı güncelleme desteği.
    /// </summary>
    public class ProgressTracker : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;

        private readonly ConcurrentDictionary<string, ProgressTask> _activeTasks;
        private readonly ConcurrentDictionary<string, ProgressReport> _completedTasks;
        private readonly ConcurrentQueue<ProgressEvent> _eventQueue;

        private readonly ProgressConfiguration _configuration;
        private readonly ProgressAggregator _aggregator;
        private readonly ProgressVisualizer _visualizer;

        private readonly Timer _cleanupTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly SemaphoreSlim _processingLock;

        private bool _isInitialized;
        private bool _isProcessing;
        private long _totalTasksProcessed;

        /// <summary>
        /// ProgressTracker'ı başlatır.
        /// </summary>
        public ProgressTracker(
            ILogger logger,
            IEventBus eventBus = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus;

            _activeTasks = new ConcurrentDictionary<string, ProgressTask>();
            _completedTasks = new ConcurrentDictionary<string, ProgressReport>();
            _eventQueue = new ConcurrentQueue<ProgressEvent>();

            _configuration = new ProgressConfiguration();
            _aggregator = new ProgressAggregator(logger);
            _visualizer = new ProgressVisualizer(logger);

            _cancellationTokenSource = new CancellationTokenSource();
            _processingLock = new SemaphoreSlim(1, 1);

            // Temizleme timer'ı (her 10 dakikada bir)
            _cleanupTimer = new Timer(CleanupOldTasks, null,
                TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

            _logger.LogInformation("ProgressTracker initialized.");
        }

        /// <summary>
        /// ProgressTracker'ı yapılandırır ve başlatır.
        /// </summary>
        public async Task InitializeAsync(ProgressConfiguration configuration = null)
        {
            try
            {
                if (configuration != null)
                {
                    _configuration.Merge(configuration);
                }

                // Aggregator ve visualizer'ı başlat;
                await _aggregator.InitializeAsync();
                await _visualizer.InitializeAsync();

                // Event bus'a abone ol;
                if (_eventBus != null)
                {
                    await SubscribeToEventsAsync();
                }

                _isInitialized = true;

                // Event queue processing'ı başlat;
                _ = Task.Run(() => ProcessEventQueueAsync(_cancellationTokenSource.Token));

                _logger.LogInformation("ProgressTracker initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ProgressTracker.");
                throw new ProgressTrackerException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Yeni bir ilerleme görevi başlatır.
        /// </summary>
        public async Task<ProgressTask> StartTaskAsync(
            string taskId,
            string title,
            string description = null,
            ProgressTask parentTask = null,
            int totalUnits = 100,
            ProgressUnit unit = ProgressUnit.Percentage)
        {
            ValidateInitialization();

            if (string.IsNullOrEmpty(taskId))
            {
                throw new ArgumentException("Task ID cannot be null or empty.", nameof(taskId));
            }

            if (string.IsNullOrEmpty(title))
            {
                throw new ArgumentException("Title cannot be null or empty.", nameof(title));
            }

            // Eğer görev zaten varsa;
            if (_activeTasks.ContainsKey(taskId))
            {
                throw new ProgressTrackerException($"Task already exists: {taskId}");
            }

            var task = new ProgressTask;
            {
                Id = taskId,
                Title = title,
                Description = description,
                ParentTaskId = parentTask?.Id,
                TotalUnits = totalUnits,
                CurrentUnits = 0,
                Unit = unit,
                Status = ProgressStatus.Running,
                StartTime = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>()
            };

            // Hiyerarşik görev yapısı;
            if (parentTask != null)
            {
                parentTask.ChildTasks.Add(taskId);
                task.Depth = parentTask.Depth + 1;
            }

            // Aktif görevlere ekle;
            if (!_activeTasks.TryAdd(taskId, task))
            {
                throw new ProgressTrackerException($"Failed to add task: {taskId}");
            }

            // Event oluştur;
            var startEvent = new ProgressEvent;
            {
                EventType = ProgressEventType.TaskStarted,
                TaskId = taskId,
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["title"] = title,
                    ["description"] = description,
                    ["totalUnits"] = totalUnits;
                }
            };

            _eventQueue.Enqueue(startEvent);

            _logger.LogInformation($"Task started: {taskId} - {title}");

            return task;
        }

        /// <summary>
        /// Görevin ilerlemesini günceller.
        /// </summary>
        public async Task UpdateProgressAsync(
            string taskId,
            int completedUnits,
            string message = null,
            Dictionary<string, object> metadata = null)
        {
            ValidateInitialization();

            if (!_activeTasks.TryGetValue(taskId, out var task))
            {
                throw new ProgressTrackerException($"Task not found: {taskId}");
            }

            // Geçerlilik kontrolü;
            if (completedUnits < 0 || completedUnits > task.TotalUnits)
            {
                throw new ArgumentOutOfRangeException(nameof(completedUnits),
                    "Completed units must be between 0 and total units.");
            }

            // İlerlemeyi güncelle;
            task.CurrentUnits = completedUnits;
            task.LastUpdated = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(message))
            {
                task.CurrentMessage = message;
            }

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    task.Metadata[kvp.Key] = kvp.Value;
                }
            }

            // Yüzde hesapla;
            task.Percentage = task.TotalUnits > 0;
                ? (double)task.CurrentUnits / task.TotalUnits * 100.0;
                : 0.0;

            // Event oluştur;
            var updateEvent = new ProgressEvent;
            {
                EventType = ProgressEventType.ProgressUpdated,
                TaskId = taskId,
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["completedUnits"] = completedUnits,
                    ["percentage"] = task.Percentage,
                    ["message"] = message,
                    ["metadata"] = metadata;
                }
            };

            _eventQueue.Enqueue(updateEvent);

            // Eğer görev tamamlandıysa;
            if (completedUnits >= task.TotalUnits)
            {
                await CompleteTaskAsync(taskId, ProgressCompletionStatus.Success, "Task completed successfully.");
            }

            _logger.LogDebug($"Progress updated for task {taskId}: {task.Percentage:F2}%");
        }

        /// <summary>
        /// Görevi tamamlanmış olarak işaretler.
        /// </summary>
        public async Task CompleteTaskAsync(
            string taskId,
            ProgressCompletionStatus status = ProgressCompletionStatus.Success,
            string completionMessage = null)
        {
            if (!_activeTasks.TryRemove(taskId, out var task))
            {
                throw new ProgressTrackerException($"Task not found: {taskId}");
            }

            // Görevi tamamla;
            task.Status = ProgressStatus.Completed;
            task.CompletionStatus = status;
            task.CompletionMessage = completionMessage;
            task.EndTime = DateTime.UtcNow;
            task.Duration = task.EndTime - task.StartTime;

            // Rapor oluştur;
            var report = new ProgressReport;
            {
                TaskId = taskId,
                Title = task.Title,
                Description = task.Description,
                StartTime = task.StartTime,
                EndTime = task.EndTime.Value,
                Duration = task.Duration.Value,
                TotalUnits = task.TotalUnits,
                CompletedUnits = task.CurrentUnits,
                Percentage = task.Percentage,
                Status = status,
                CompletionMessage = completionMessage,
                Metadata = new Dictionary<string, object>(task.Metadata)
            };

            // Tamamlanan görevlere ekle;
            _completedTasks[taskId] = report;

            // Event oluştur;
            var completeEvent = new ProgressEvent;
            {
                EventType = ProgressEventType.TaskCompleted,
                TaskId = taskId,
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["status"] = status.ToString(),
                    ["completionMessage"] = completionMessage,
                    ["duration"] = task.Duration.Value.TotalSeconds,
                    ["percentage"] = task.Percentage;
                }
            };

            _eventQueue.Enqueue(completeEvent);

            // Ana görevin ilerlemesini güncelle (eğer varsa)
            if (!string.IsNullOrEmpty(task.ParentTaskId))
            {
                await UpdateParentTaskProgressAsync(task.ParentTaskId);
            }

            _logger.LogInformation($"Task completed: {taskId} - Status: {status}");
        }

        /// <summary>
        /// Görevi hata ile tamamlar.
        /// </summary>
        public async Task FailTaskAsync(string taskId, string errorMessage, Exception exception = null)
        {
            if (!_activeTasks.TryRemove(taskId, out var task))
            {
                throw new ProgressTrackerException($"Task not found: {taskId}");
            }

            task.Status = ProgressStatus.Completed;
            task.CompletionStatus = ProgressCompletionStatus.Failed;
            task.CompletionMessage = errorMessage;
            task.EndTime = DateTime.UtcNow;
            task.Duration = task.EndTime - task.StartTime;
            task.Error = exception;

            // Rapor oluştur;
            var report = new ProgressReport;
            {
                TaskId = taskId,
                Title = task.Title,
                Description = task.Description,
                StartTime = task.StartTime,
                EndTime = task.EndTime.Value,
                Duration = task.Duration.Value,
                TotalUnits = task.TotalUnits,
                CompletedUnits = task.CurrentUnits,
                Percentage = task.Percentage,
                Status = ProgressCompletionStatus.Failed,
                CompletionMessage = errorMessage,
                ErrorDetails = exception?.ToString(),
                Metadata = new Dictionary<string, object>(task.Metadata)
            };

            _completedTasks[taskId] = report;

            // Event oluştur;
            var failEvent = new ProgressEvent;
            {
                EventType = ProgressEventType.TaskFailed,
                TaskId = taskId,
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["errorMessage"] = errorMessage,
                    ["exception"] = exception?.Message,
                    ["duration"] = task.Duration.Value.TotalSeconds;
                }
            };

            _eventQueue.Enqueue(failEvent);

            _logger.LogError($"Task failed: {taskId} - Error: {errorMessage}", exception);
        }

        /// <summary>
        /// Görevi iptal eder.
        /// </summary>
        public async Task CancelTaskAsync(string taskId, string cancellationReason = null)
        {
            if (!_activeTasks.TryRemove(taskId, out var task))
            {
                throw new ProgressTrackerException($"Task not found: {taskId}");
            }

            task.Status = ProgressStatus.Cancelled;
            task.CompletionStatus = ProgressCompletionStatus.Cancelled;
            task.CompletionMessage = cancellationReason ?? "Task was cancelled.";
            task.EndTime = DateTime.UtcNow;
            task.Duration = task.EndTime - task.StartTime;

            // Rapor oluştur;
            var report = new ProgressReport;
            {
                TaskId = taskId,
                Title = task.Title,
                Description = task.Description,
                StartTime = task.StartTime,
                EndTime = task.EndTime.Value,
                Duration = task.Duration.Value,
                TotalUnits = task.TotalUnits,
                CompletedUnits = task.CurrentUnits,
                Percentage = task.Percentage,
                Status = ProgressCompletionStatus.Cancelled,
                CompletionMessage = cancellationReason,
                Metadata = new Dictionary<string, object>(task.Metadata)
            };

            _completedTasks[taskId] = report;

            // Event oluştur;
            var cancelEvent = new ProgressEvent;
            {
                EventType = ProgressEventType.TaskCancelled,
                TaskId = taskId,
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["cancellationReason"] = cancellationReason,
                    ["duration"] = task.Duration.Value.TotalSeconds,
                    ["percentage"] = task.Percentage;
                }
            };

            _eventQueue.Enqueue(cancelEvent);

            _logger.LogInformation($"Task cancelled: {taskId} - Reason: {cancellationReason}");
        }

        /// <summary>
        /// Belirli bir görevin durumunu getirir.
        /// </summary>
        public ProgressTask GetTask(string taskId)
        {
            if (_activeTasks.TryGetValue(taskId, out var task))
            {
                return task;
            }

            throw new ProgressTrackerException($"Task not found: {taskId}");
        }

        /// <summary>
        /// Tüm aktif görevleri listeler.
        /// </summary>
        public List<ProgressTask> GetActiveTasks(string parentTaskId = null)
        {
            var tasks = _activeTasks.Values.ToList();

            if (!string.IsNullOrEmpty(parentTaskId))
            {
                tasks = tasks.Where(t => t.ParentTaskId == parentTaskId).ToList();
            }

            return tasks.OrderBy(t => t.StartTime).ToList();
        }

        /// <summary>
        /// Tamamlanan görevleri listeler.
        /// </summary>
        public List<ProgressReport> GetCompletedTasks(
            DateTime? fromDate = null,
            DateTime? toDate = null,
            ProgressCompletionStatus? status = null)
        {
            var reports = _completedTasks.Values.ToList();

            if (fromDate.HasValue)
            {
                reports = reports.Where(r => r.StartTime >= fromDate.Value).ToList();
            }

            if (toDate.HasValue)
            {
                reports = reports.Where(r => r.EndTime <= toDate.Value).ToList();
            }

            if (status.HasValue)
            {
                reports = reports.Where(r => r.Status == status.Value).ToList();
            }

            return reports.OrderByDescending(r => r.EndTime).ToList();
        }

        /// <summary>
        /// Görev hiyerarşisini getirir.
        /// </summary>
        public TaskHierarchy GetTaskHierarchy(string rootTaskId)
        {
            if (!_activeTasks.ContainsKey(rootTaskId) && !_completedTasks.ContainsKey(rootTaskId))
            {
                throw new ProgressTrackerException($"Task not found: {rootTaskId}");
            }

            var hierarchy = new TaskHierarchy;
            {
                RootTaskId = rootTaskId,
                Tasks = new Dictionary<string, ProgressTask>(),
                ChildRelationships = new Dictionary<string, List<string>>()
            };

            // Tüm görevleri topla (aktif ve tamamlanan)
            var allTasks = new Dictionary<string, ProgressTask>();
            foreach (var task in _activeTasks.Values)
            {
                allTasks[task.Id] = task;
            }

            // Raporlardan görev bilgilerini çıkar (tamamlananlar için)
            foreach (var report in _completedTasks.Values)
            {
                if (!allTasks.ContainsKey(report.TaskId))
                {
                    var task = new ProgressTask;
                    {
                        Id = report.TaskId,
                        Title = report.Title,
                        Description = report.Description,
                        Status = ProgressStatus.Completed,
                        StartTime = report.StartTime,
                        EndTime = report.EndTime,
                        Duration = report.Duration,
                        TotalUnits = report.TotalUnits,
                        CurrentUnits = report.CompletedUnits,
                        Percentage = report.Percentage,
                        CompletionStatus = report.Status,
                        CompletionMessage = report.CompletionMessage;
                    };

                    allTasks[task.Id] = task;
                }
            }

            // Hiyerarşiyi oluştur;
            BuildHierarchy(rootTaskId, allTasks, hierarchy);

            return hierarchy;
        }

        /// <summary>
        /// İlerleme istatistiklerini getirir.
        /// </summary>
        public ProgressStatistics GetStatistics()
        {
            var completedReports = _completedTasks.Values.ToList();
            var activeTasks = _activeTasks.Values.ToList();

            return new ProgressStatistics;
            {
                TotalTasksProcessed = _totalTasksProcessed,
                ActiveTasksCount = activeTasks.Count,
                CompletedTasksCount = completedReports.Count,
                SuccessCount = completedReports.Count(r => r.Status == ProgressCompletionStatus.Success),
                FailedCount = completedReports.Count(r => r.Status == ProgressCompletionStatus.Failed),
                CancelledCount = completedReports.Count(r => r.Status == ProgressCompletionStatus.Cancelled),
                AverageDuration = completedReports.Any()
                    ? TimeSpan.FromSeconds(completedReports.Average(r => r.Duration.TotalSeconds))
                    : TimeSpan.Zero,
                TotalDuration = TimeSpan.FromSeconds(completedReports.Sum(r => r.Duration.TotalSeconds)),
                TasksByStatus = completedReports;
                    .GroupBy(r => r.Status)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                RecentTasks = completedReports;
                    .OrderByDescending(r => r.EndTime)
                    .Take(10)
                    .Select(r => r.TaskId)
                    .ToList()
            };
        }

        /// <summary>
        /// Gerçek zamanlı ilerleme verilerini getirir.
        /// </summary>
        public RealTimeProgress GetRealTimeProgress()
        {
            var activeTasks = _activeTasks.Values.ToList();

            return new RealTimeProgress;
            {
                Timestamp = DateTime.UtcNow,
                ActiveTasks = activeTasks.Count,
                TasksByStatus = activeTasks;
                    .GroupBy(t => t.Status)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                AverageProgress = activeTasks.Any()
                    ? activeTasks.Average(t => t.Percentage)
                    : 0,
                ProgressByTask = activeTasks;
                    .ToDictionary(t => t.Id, t => t.Percentage),
                ProcessingRate = CalculateProcessingRate()
            };
        }

        /// <summary>
        /// İlerleme raporu oluşturur.
        /// </summary>
        public async Task<ProgressSummaryReport> GenerateReportAsync(
            DateTime startDate,
            DateTime endDate,
            ReportFormat format = ReportFormat.Detailed)
        {
            var reports = GetCompletedTasks(startDate, endDate);
            var activeTasks = GetActiveTasks();

            var summary = new ProgressSummaryReport;
            {
                PeriodStart = startDate,
                PeriodEnd = endDate,
                TotalTasks = reports.Count + activeTasks.Count,
                CompletedTasks = reports.Count,
                ActiveTasks = activeTasks.Count,
                SuccessRate = reports.Any()
                    ? (double)reports.Count(r => r.Status == ProgressCompletionStatus.Success) / reports.Count * 100;
                    : 0,
                AverageTaskDuration = reports.Any()
                    ? TimeSpan.FromSeconds(reports.Average(r => r.Duration.TotalSeconds))
                    : TimeSpan.Zero,
                Tasks = reports,
                ActiveTaskSummaries = activeTasks.Select(t => new TaskSummary;
                {
                    TaskId = t.Id,
                    Title = t.Title,
                    Progress = t.Percentage,
                    Status = t.Status.ToString(),
                    StartTime = t.StartTime;
                }).ToList()
            };

            // Format'a göre detaylandır;
            if (format == ReportFormat.Detailed)
            {
                summary.DetailedMetrics = CalculateDetailedMetrics(reports, activeTasks);
            }

            await Task.CompletedTask;
            return summary;
        }

        /// <summary>
        /// Görevleri gruplandırarak toplu ilerleme hesaplar.
        /// </summary>
        public async Task<GroupedProgress> GetGroupedProgressAsync(string groupBy = "category")
        {
            var activeTasks = _activeTasks.Values.ToList();

            return await _aggregator.GroupProgressAsync(activeTasks, groupBy);
        }

        /// <summary>
        /// İlerleme görselleştirmesi oluşturur.
        /// </summary>
        public async Task<VisualizationData> GetVisualizationAsync(
            VisualizationType type = VisualizationType.ProgressChart,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            var reports = GetCompletedTasks(fromDate, toDate);
            var activeTasks = GetActiveTasks();

            return await _visualizer.CreateVisualizationAsync(type, reports, activeTasks);
        }

        #region Private Methods;

        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("ProgressTracker must be initialized before use.");
            }
        }

        private async Task ProcessEventQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _processingLock.WaitAsync(cancellationToken);
                    _isProcessing = true;

                    if (_eventQueue.TryDequeue(out var progressEvent))
                    {
                        await ProcessEventAsync(progressEvent, cancellationToken);
                        Interlocked.Increment(ref _totalTasksProcessed);
                    }
                    else;
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing event queue.");
                }
                finally
                {
                    _isProcessing = false;
                    _processingLock.Release();
                }
            }
        }

        private async Task ProcessEventAsync(ProgressEvent progressEvent, CancellationToken cancellationToken)
        {
            try
            {
                // Event bus'a gönder;
                if (_eventBus != null)
                {
                    await _eventBus.PublishAsync(progressEvent);
                }

                // Aggregator'a işle;
                await _aggregator.ProcessEventAsync(progressEvent);

                // Visualizer'a güncelle;
                await _visualizer.UpdateVisualizationAsync(progressEvent);

                _logger.LogDebug($"Processed event: {progressEvent.EventType} for task: {progressEvent.TaskId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process event: {progressEvent.EventType}");
            }
        }

        private async Task UpdateParentTaskProgressAsync(string parentTaskId)
        {
            if (!_activeTasks.TryGetValue(parentTaskId, out var parentTask))
            {
                return;
            }

            // Çocuk görevlerin ilerlemesini topla;
            var childTasks = _activeTasks.Values;
                .Where(t => t.ParentTaskId == parentTaskId)
                .Concat(_completedTasks.Values;
                    .Where(r => r.TaskId.StartsWith($"{parentTaskId}."))
                    .Select(r => new ProgressTask;
                    {
                        Id = r.TaskId,
                        CurrentUnits = r.CompletedUnits,
                        TotalUnits = r.TotalUnits,
                        Percentage = r.Percentage;
                    }))
                .ToList();

            if (!childTasks.Any())
            {
                return;
            }

            // Toplam ilerlemeyi hesapla;
            int totalCompleted = childTasks.Sum(t => t.CurrentUnits);
            int totalUnits = childTasks.Sum(t => t.TotalUnits);

            double percentage = totalUnits > 0 ? (double)totalCompleted / totalUnits * 100.0 : 0.0;

            // Ana görevi güncelle;
            parentTask.CurrentUnits = totalCompleted;
            parentTask.TotalUnits = totalUnits;
            parentTask.Percentage = percentage;
            parentTask.LastUpdated = DateTime.UtcNow;

            // Event oluştur;
            var updateEvent = new ProgressEvent;
            {
                EventType = ProgressEventType.ProgressUpdated,
                TaskId = parentTaskId,
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["completedUnits"] = totalCompleted,
                    ["totalUnits"] = totalUnits,
                    ["percentage"] = percentage,
                    ["childTaskCount"] = childTasks.Count;
                }
            };

            _eventQueue.Enqueue(updateEvent);

            // Eğer tüm çocuk görevler tamamlandıysa;
            if (percentage >= 100.0)
            {
                await CompleteTaskAsync(parentTaskId, ProgressCompletionStatus.Success,
                    "All child tasks completed.");
            }

            await Task.CompletedTask;
        }

        private void BuildHierarchy(
            string taskId,
            Dictionary<string, ProgressTask> allTasks,
            TaskHierarchy hierarchy)
        {
            if (!allTasks.TryGetValue(taskId, out var task))
            {
                return;
            }

            hierarchy.Tasks[taskId] = task;

            // Çocuk görevleri bul;
            var childTasks = allTasks.Values;
                .Where(t => t.ParentTaskId == taskId)
                .ToList();

            if (childTasks.Any())
            {
                hierarchy.ChildRelationships[taskId] = childTasks.Select(t => t.Id).ToList();

                // Recursively build hierarchy for children;
                foreach (var childTask in childTasks)
                {
                    BuildHierarchy(childTask.Id, allTasks, hierarchy);
                }
            }
        }

        private async Task SubscribeToEventsAsync()
        {
            // Event bus'tan ilerleme ile ilgili event'ları dinle;
            // Örnek: Sistem olayları, görev tamamlanmaları, vs.
            await Task.CompletedTask;
        }

        private void CleanupOldTasks(object state)
        {
            try
            {
                var retentionThreshold = DateTime.UtcNow.AddHours(-_configuration.HistoryRetentionHours);

                // Eski tamamlanmış görevleri temizle;
                var oldCompleted = _completedTasks.Where(kvp =>
                    kvp.Value.EndTime < retentionThreshold).ToList();

                foreach (var kvp in oldCompleted)
                {
                    _completedTasks.TryRemove(kvp.Key, out _);
                    _logger.LogDebug($"Cleaned up old completed task: {kvp.Key}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during task cleanup.");
            }
        }

        private double CalculateProcessingRate()
        {
            // Son 5 dakikadaki işlenen event sayısını hesapla;
            var recentEvents = _completedTasks.Values;
                .Where(r => r.EndTime > DateTime.UtcNow.AddMinutes(-5))
                .Count();

            return recentEvents / 300.0; // events per second;
        }

        private DetailedMetrics CalculateDetailedMetrics(
            List<ProgressReport> reports,
            List<ProgressTask> activeTasks)
        {
            return new DetailedMetrics;
            {
                TasksByType = reports;
                    .GroupBy(r => r.Metadata.ContainsKey("type") ? r.Metadata["type"].ToString() : "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count()),
                AverageProgressByCategory = activeTasks;
                    .GroupBy(t => t.Metadata.ContainsKey("category") ? t.Metadata["category"].ToString() : "General")
                    .ToDictionary(g => g.Key, g => g.Average(t => t.Percentage)),
                DurationDistribution = reports;
                    .GroupBy(r => (int)(r.Duration.TotalMinutes / 5) * 5) // 5 dakikalık gruplar;
                    .ToDictionary(g => $"{g.Key}-{g.Key + 5} min", g => g.Count()),
                SuccessRateByHour = reports;
                    .GroupBy(r => r.StartTime.Hour)
                    .ToDictionary(g => g.Key.ToString(), g =>
                        g.Any() ? (double)g.Count(r => r.Status == ProgressCompletionStatus.Success) / g.Count() * 100 : 0)
            };
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();

                    _cleanupTimer?.Dispose();
                    _processingLock?.Dispose();

                    _activeTasks.Clear();
                    _completedTasks.Clear();
                    while (_eventQueue.TryDequeue(out _)) { }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ProgressTracker()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// İlerleme yapılandırması;
    /// </summary>
    public class ProgressConfiguration;
    {
        public int HistoryRetentionHours { get; set; } = 24;
        public int MaxActiveTasks { get; set; } = 1000;
        public int MaxCompletedTasks { get; set; } = 10000;
        public bool EnableRealTimeUpdates { get; set; } = true;
        public bool EnableEventPublishing { get; set; } = true;
        public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(1);
        public string DefaultUnit { get; set; } = "Percentage";
        public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();

        public void Merge(ProgressConfiguration other)
        {
            if (other == null) return;

            HistoryRetentionHours = other.HistoryRetentionHours;
            MaxActiveTasks = other.MaxActiveTasks;
            MaxCompletedTasks = other.MaxCompletedTasks;
            EnableRealTimeUpdates = other.EnableRealTimeUpdates;
            EnableEventPublishing = other.EnableEventPublishing;
            UpdateInterval = other.UpdateInterval;
            DefaultUnit = other.DefaultUnit;

            foreach (var setting in other.AdvancedSettings)
            {
                AdvancedSettings[setting.Key] = setting.Value;
            }
        }
    }

    /// <summary>
    /// İlerleme görevi;
    /// </summary>
    public class ProgressTask;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string ParentTaskId { get; set; }
        public List<string> ChildTasks { get; set; } = new List<string>();
        public int TotalUnits { get; set; }
        public int CurrentUnits { get; set; }
        public double Percentage { get; set; }
        public ProgressUnit Unit { get; set; }
        public ProgressStatus Status { get; set; }
        public ProgressCompletionStatus? CompletionStatus { get; set; }
        public string CompletionMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Duration { get; set; }
        public string CurrentMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public Exception Error { get; set; }
        public int Depth { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string Category { get; set; } = "General";
    }

    /// <summary>
    /// İlerleme raporu;
    /// </summary>
    public class ProgressReport;
    {
        public string TaskId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public int TotalUnits { get; set; }
        public int CompletedUnits { get; set; }
        public double Percentage { get; set; }
        public ProgressCompletionStatus Status { get; set; }
        public string CompletionMessage { get; set; }
        public string ErrorDetails { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public List<string> Tags { get; set; } = new List<string>();
        public string Category { get; set; }
    }

    /// <summary>
    /// İlerleme birimleri;
    /// </summary>
    public enum ProgressUnit;
    {
        Percentage,
        Items,
        Bytes,
        Milliseconds,
        Custom;
    }

    /// <summary>
    /// Görev durumları;
    /// </summary>
    public enum ProgressStatus;
    {
        Pending,
        Running,
        Paused,
        Completed,
        Cancelled;
    }

    /// <summary>
    /// Tamamlama durumları;
    /// </summary>
    public enum ProgressCompletionStatus;
    {
        Success,
        Failed,
        Cancelled,
        Warning;
    }

    /// <summary>
    /// İlerleme event'ı;
    /// </summary>
    public class ProgressEvent : IEvent;
    {
        public ProgressEventType EventType { get; set; }
        public string TaskId { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        public string EventType => "ProgressEvent";
    }

    /// <summary>
    /// İlerleme event türleri;
    /// </summary>
    public enum ProgressEventType;
    {
        TaskStarted,
        ProgressUpdated,
        TaskPaused,
        TaskResumed,
        TaskCompleted,
        TaskFailed,
        TaskCancelled;
    }

    /// <summary>
    /// Görev hiyerarşisi;
    /// </summary>
    public class TaskHierarchy;
    {
        public string RootTaskId { get; set; }
        public Dictionary<string, ProgressTask> Tasks { get; set; }
        public Dictionary<string, List<string>> ChildRelationships { get; set; }
        public Dictionary<string, int> TaskDepths { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// İlerleme istatistikleri;
    /// </summary>
    public class ProgressStatistics;
    {
        public long TotalTasksProcessed { get; set; }
        public int ActiveTasksCount { get; set; }
        public int CompletedTasksCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int CancelledCount { get; set; }
        public TimeSpan AverageDuration { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public Dictionary<string, int> TasksByStatus { get; set; } = new Dictionary<string, int>();
        public List<string> RecentTasks { get; set; } = new List<string>();
    }

    /// <summary>
    /// Gerçek zamanlı ilerleme;
    /// </summary>
    public class RealTimeProgress;
    {
        public DateTime Timestamp { get; set; }
        public int ActiveTasks { get; set; }
        public Dictionary<string, int> TasksByStatus { get; set; } = new Dictionary<string, int>();
        public double AverageProgress { get; set; }
        public Dictionary<string, double> ProgressByTask { get; set; } = new Dictionary<string, double>();
        public double ProcessingRate { get; set; }
    }

    /// <summary>
    /// İlerleme özet raporu;
    /// </summary>
    public class ProgressSummaryReport;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int TotalTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int ActiveTasks { get; set; }
        public double SuccessRate { get; set; }
        public TimeSpan AverageTaskDuration { get; set; }
        public List<ProgressReport> Tasks { get; set; } = new List<ProgressReport>();
        public List<TaskSummary> ActiveTaskSummaries { get; set; } = new List<TaskSummary>();
        public DetailedMetrics DetailedMetrics { get; set; }
    }

    /// <summary>
    /// Görev özeti;
    /// </summary>
    public class TaskSummary;
    {
        public string TaskId { get; set; }
        public string Title { get; set; }
        public double Progress { get; set; }
        public string Status { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan? Duration { get; set; }
    }

    /// <summary>
    /// Detaylı metrikler;
    /// </summary>
    public class DetailedMetrics;
    {
        public Dictionary<string, int> TasksByType { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, double> AverageProgressByCategory { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, int> DurationDistribution { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, double> SuccessRateByHour { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Gruplanmış ilerleme;
    /// </summary>
    public class GroupedProgress;
    {
        public string GroupKey { get; set; }
        public Dictionary<string, double> GroupProgress { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, int> TaskCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, double> AverageProgress { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Görselleştirme verisi;
    /// </summary>
    public class VisualizationData;
    {
        public VisualizationType Type { get; set; }
        public object ChartData { get; set; }
        public object TableData { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Görselleştirme türleri;
    /// </summary>
    public enum VisualizationType;
    {
        ProgressChart,
        Timeline,
        Heatmap,
        Distribution,
        Comparison;
    }

    /// <summary>
    /// Rapor formatları;
    /// </summary>
    public enum ReportFormat;
    {
        Summary,
        Detailed,
        Executive,
        Technical;
    }

    #endregion;

    #region Internal Components;

    /// <summary>
    /// İlerleme toplayıcı;
    /// </summary>
    internal class ProgressAggregator;
    {
        private readonly ILogger _logger;

        public ProgressAggregator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            await Task.CompletedTask;
            _logger.LogDebug("ProgressAggregator initialized.");
        }

        public async Task ProcessEventAsync(ProgressEvent progressEvent)
        {
            // Event'ları işle ve toplu verileri güncelle;
            await Task.CompletedTask;
        }

        public async Task<GroupedProgress> GroupProgressAsync(List<ProgressTask> tasks, string groupBy)
        {
            var groupedProgress = new GroupedProgress;
            {
                GroupKey = groupBy;
            };

            if (!tasks.Any())
            {
                return groupedProgress;
            }

            var groups = tasks.GroupBy(t =>
            {
                return groupBy switch;
                {
                    "category" => t.Category,
                    "status" => t.Status.ToString(),
                    "depth" => t.Depth.ToString(),
                    _ => t.Metadata.ContainsKey(groupBy) ? t.Metadata[groupBy].ToString() : "Unknown"
                };
            });

            foreach (var group in groups)
            {
                groupedProgress.GroupProgress[group.Key] = group.Average(t => t.Percentage);
                groupedProgress.TaskCounts[group.Key] = group.Count();
                groupedProgress.AverageProgress[group.Key] = group.Average(t => t.Percentage);
            }

            await Task.CompletedTask;
            return groupedProgress;
        }
    }

    /// <summary>
    /// İlerleme görselleştirici;
    /// </summary>
    internal class ProgressVisualizer;
    {
        private readonly ILogger _logger;

        public ProgressVisualizer(ILogger logger)
        {
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            await Task.CompletedTask;
            _logger.LogDebug("ProgressVisualizer initialized.");
        }

        public async Task UpdateVisualizationAsync(ProgressEvent progressEvent)
        {
            // Görselleştirmeyi gerçek zamanlı güncelle;
            await Task.CompletedTask;
        }

        public async Task<VisualizationData> CreateVisualizationAsync(
            VisualizationType type,
            List<ProgressReport> reports,
            List<ProgressTask> activeTasks)
        {
            var visualization = new VisualizationData;
            {
                Type = type,
                Metadata = new Dictionary<string, object>
                {
                    ["generatedAt"] = DateTime.UtcNow,
                    ["reportCount"] = reports.Count,
                    ["activeTaskCount"] = activeTasks.Count;
                }
            };

            // Visualization türüne göre veri hazırla;
            switch (type)
            {
                case VisualizationType.ProgressChart:
                    visualization.ChartData = CreateProgressChartData(reports, activeTasks);
                    break;
                case VisualizationType.Timeline:
                    visualization.ChartData = CreateTimelineData(reports);
                    break;
                case VisualizationType.Heatmap:
                    visualization.ChartData = CreateHeatmapData(reports);
                    break;
                default:
                    visualization.ChartData = new { message = "Visualization type not implemented" };
                    break;
            }

            await Task.CompletedTask;
            return visualization;
        }

        private object CreateProgressChartData(List<ProgressReport> reports, List<ProgressTask> activeTasks)
        {
            return new;
            {
                labels = activeTasks.Select(t => t.Title).Concat(reports.Select(r => r.Title)).ToList(),
                datasets = new[]
                {
                    new { label = "Progress (%)", data = activeTasks.Select(t => t.Percentage)
                        .Concat(reports.Select(r => r.Percentage)).ToList() }
                }
            };
        }

        private object CreateTimelineData(List<ProgressReport> reports)
        {
            return reports.Select(r => new;
            {
                task = r.Title,
                start = r.StartTime,
                end = r.EndTime,
                duration = r.Duration.TotalHours,
                status = r.Status.ToString()
            }).ToList();
        }

        private object CreateHeatmapData(List<ProgressReport> reports)
        {
            // Haftanın günlerine ve saatlerine göre görev dağılımı;
            var heatmapData = new Dictionary<string, Dictionary<string, int>>();

            foreach (var report in reports)
            {
                var day = report.StartTime.DayOfWeek.ToString();
                var hour = report.StartTime.Hour.ToString();

                if (!heatmapData.ContainsKey(day))
                {
                    heatmapData[day] = new Dictionary<string, int>();
                }

                if (!heatmapData[day].ContainsKey(hour))
                {
                    heatmapData[day][hour] = 0;
                }

                heatmapData[day][hour]++;
            }

            return heatmapData;
        }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// ProgressTracker istisnası;
    /// </summary>
    public class ProgressTrackerException : Exception
    {
        public string TaskId { get; }
        public DateTime Timestamp { get; }

        public ProgressTrackerException(string message) : base(message)
        {
            Timestamp = DateTime.UtcNow;
        }

        public ProgressTrackerException(string message, Exception innerException)
            : base(message, innerException)
        {
            Timestamp = DateTime.UtcNow;
        }

        public ProgressTrackerException(string taskId, string message)
            : base(message)
        {
            TaskId = taskId;
            Timestamp = DateTime.UtcNow;
        }
    }

    #endregion;
}
