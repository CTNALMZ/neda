using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using NEDA.Core.Logging;
using NEDA.Core.Common;
using NEDA.Core.Configuration;
using NEDA.Services.Messaging.EventBus;
using NEDA.Automation.Executors;
using NEDA.Automation.WorkflowEngine;
using System.Diagnostics;
using System.ComponentModel;

namespace NEDA.Interface.VisualInterface.ProgressMonitor;
{
    /// <summary>
    /// Görev izleme sistemi - Çalışan tüm görevleri gerçek zamanlı izler ve yönetir;
    /// </summary>
    public class TaskMonitor : ITaskMonitor, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ITaskExecutor _taskExecutor;

        private readonly ConcurrentDictionary<string, MonitoredTask> _activeTasks;
        private readonly ConcurrentDictionary<string, TaskStatistics> _taskStatistics;
        private readonly ConcurrentDictionary<string, List<TaskEvent>> _taskEvents;
        private readonly ConcurrentQueue<TaskAlert> _taskAlerts;

        private readonly Subject<TaskUpdate> _taskUpdates;
        private readonly Subject<TaskProgress> _progressUpdates;
        private readonly Subject<TaskAlert> _alertUpdates;
        private readonly Subject<TaskSummary> _summaryUpdates;

        private Timer _monitoringTimer;
        private Timer _cleanupTimer;
        private Timer _reportingTimer;

        private readonly object _initializationLock = new object();
        private bool _isInitialized;
        private bool _isMonitoring;
        private TaskMonitorConfiguration _configuration;
        private MonitorStatistics _statistics;

        /// <summary>
        /// Görev izleyici durumu;
        /// </summary>
        public TaskMonitorState State { get; private set; }

        /// <summary>
        /// Görev özeti;
        /// </summary>
        public TaskMonitorSummary Summary { get; private set; }

        /// <summary>
        /// Görev güncelleme olayları (reaktif)
        /// </summary>
        public IObservable<TaskUpdate> TaskUpdates => _taskUpdates.AsObservable();
        public IObservable<TaskProgress> ProgressUpdates => _progressUpdates.AsObservable();
        public IObservable<TaskAlert> AlertUpdates => _alertUpdates.AsObservable();
        public IObservable<TaskSummary> SummaryUpdates => _summaryUpdates.AsObservable();

        /// <summary>
        /// Aktif görev sayısı;
        /// </summary>
        public int ActiveTaskCount => _activeTasks.Count;

        /// <summary>
        /// Yeni bir TaskMonitor örneği oluşturur;
        /// </summary>
        public TaskMonitor(
            ILogger logger,
            IEventBus eventBus,
            ITaskExecutor taskExecutor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _taskExecutor = taskExecutor ?? throw new ArgumentNullException(nameof(taskExecutor));

            _activeTasks = new ConcurrentDictionary<string, MonitoredTask>();
            _taskStatistics = new ConcurrentDictionary<string, TaskStatistics>();
            _taskEvents = new ConcurrentDictionary<string, List<TaskEvent>>();
            _taskAlerts = new ConcurrentQueue<TaskAlert>();

            _taskUpdates = new Subject<TaskUpdate>();
            _progressUpdates = new Subject<TaskProgress>();
            _alertUpdates = new Subject<TaskAlert>();
            _summaryUpdates = new Subject<TaskSummary>();

            _configuration = new TaskMonitorConfiguration();
            Summary = new TaskMonitorSummary();
            _statistics = new MonitorStatistics();
            State = TaskMonitorState.Stopped;
        }

        /// <summary>
        /// Görev izleyiciyi başlatır;
        /// </summary>
        public async Task InitializeAsync(TaskMonitorConfiguration configuration = null)
        {
            if (_isInitialized)
                return;

            lock (_initializationLock)
            {
                if (_isInitialized)
                    return;

                try
                {
                    _logger.LogInformation("Görev izleyici başlatılıyor...");

                    // Yapılandırmayı güncelle;
                    if (configuration != null)
                        _configuration = configuration;
                    else;
                        LoadConfiguration();

                    State = TaskMonitorState.Initializing;

                    // Event bus kaydı;
                    await RegisterEventHandlersAsync();

                    // İzleme timer'larını başlat;
                    InitializeMonitoringTimers();

                    // İstatistikleri sıfırla;
                    ResetStatistics();

                    _isInitialized = true;
                    State = TaskMonitorState.Ready;

                    _logger.LogInformation("Görev izleyici başarıyla başlatıldı");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Görev izleyici başlatma hatası: {ex.Message}", ex);
                    State = TaskMonitorState.Error;
                    throw new TaskMonitorException("Görev izleyici başlatılamadı", ex);
                }
            }
        }

        /// <summary>
        /// İzlemeyi başlatır;
        /// </summary>
        public async Task StartMonitoringAsync(MonitoringOptions options = null)
        {
            ValidateMonitorState();

            try
            {
                _logger.LogInformation("Görev izleme başlatılıyor...");

                var monitoringOptions = options ?? new MonitoringOptions();
                State = TaskMonitorState.Monitoring;
                _isMonitoring = true;

                // İzleme seviyesini ayarla;
                _configuration.MonitoringLevel = monitoringOptions.MonitoringLevel;

                // Timer'ları başlat;
                StartMonitoringTimers();

                // Başlangıç durum kontrolü;
                await PerformInitialTaskScanAsync();

                // Gerçek zamanlı izlemeyi başlat;
                await StartRealTimeMonitoringAsync();

                // Olay tetikle;
                await _eventBus.PublishAsync(new TaskMonitoringStartedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    Options = monitoringOptions,
                    MonitorId = GetMonitorId()
                });

                _logger.LogInformation("Görev izleme başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"İzleme başlatma hatası: {ex.Message}", ex);
                State = TaskMonitorState.Error;
                throw;
            }
        }

        /// <summary>
        /// Görev kaydı oluşturur;
        /// </summary>
        public async Task<TaskRegistrationResult> RegisterTaskAsync(
            TaskRegistrationRequest request,
            TaskOptions options = null)
        {
            ValidateMonitorState();

            try
            {
                if (request == null)
                    throw new ArgumentNullException(nameof(request));

                if (string.IsNullOrWhiteSpace(request.TaskId))
                    throw new ArgumentException("Görev ID'si belirtilmelidir", nameof(request.TaskId));

                _logger.LogDebug($"Görev kaydı oluşturuluyor: {request.TaskId}");

                var taskOptions = options ?? new TaskOptions();
                var registrationTime = DateTime.UtcNow;

                // Görev nesnesi oluştur;
                var monitoredTask = new MonitoredTask;
                {
                    TaskId = request.TaskId,
                    Name = request.Name,
                    Description = request.Description,
                    Category = request.Category,
                    Priority = request.Priority,
                    State = TaskState.Pending,
                    Status = TaskStatus.Created,
                    CreatedAt = registrationTime,
                    UpdatedAt = registrationTime,
                    Options = taskOptions,
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    Owner = request.Owner,
                    Tags = request.Tags ?? new List<string>()
                };

                // İlerleme izleyicisi oluştur;
                monitoredTask.ProgressTracker = new TaskProgressTracker;
                {
                    TaskId = request.TaskId,
                    Current = 0,
                    Total = request.TotalSteps ?? 100,
                    Unit = request.ProgressUnit ?? "percent",
                    Message = "Görev başlatılıyor..."
                };

                // Görevi kaydet;
                if (!_activeTasks.TryAdd(request.TaskId, monitoredTask))
                {
                    return new TaskRegistrationResult;
                    {
                        Success = false,
                        TaskId = request.TaskId,
                        Error = "Görev zaten kayıtlı"
                    };
                }

                // İstatistik kaydı oluştur;
                _taskStatistics[request.TaskId] = new TaskStatistics;
                {
                    TaskId = request.TaskId,
                    CreatedAt = registrationTime,
                    StateChanges = new List<TaskStateChange>(),
                    PerformanceMetrics = new List<PerformanceMetric>()
                };

                // Olay kaydı oluştur;
                _taskEvents[request.TaskId] = new List<TaskEvent>();

                // Görev güncelleme olayı;
                var taskUpdate = new TaskUpdate;
                {
                    TaskId = request.TaskId,
                    Task = monitoredTask,
                    UpdateType = TaskUpdateType.Registered,
                    Timestamp = registrationTime,
                    Details = new Dictionary<string, object>
                    {
                        ["request"] = request,
                        ["options"] = taskOptions;
                    }
                };

                // Yayınla;
                _taskUpdates.OnNext(taskUpdate);

                // Event bus'a gönder;
                await _eventBus.PublishAsync(new TaskRegisteredEvent;
                {
                    TaskId = request.TaskId,
                    Task = monitoredTask,
                    Timestamp = registrationTime;
                });

                // İstatistikleri güncelle;
                UpdateStatisticsForRegistration(monitoredTask);

                // Özeti güncelle;
                await UpdateTaskSummaryAsync();

                _logger.LogInformation($"Görev kaydedildi: {request.TaskId} - {request.Name}");

                return new TaskRegistrationResult;
                {
                    Success = true,
                    TaskId = request.TaskId,
                    Task = monitoredTask,
                    RegistrationTime = registrationTime,
                    Message = "Görev başarıyla kaydedildi"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev kayıt hatası: {ex.Message}", ex);
                throw new TaskRegistrationException($"Görev kaydedilemedi: {request?.TaskId}", ex);
            }
        }

        /// <summary>
        /// Görev durumunu günceller;
        /// </summary>
        public async Task<TaskUpdateResult> UpdateTaskStateAsync(
            string taskId,
            TaskState newState,
            TaskStatus newStatus = TaskStatus.Running,
            string message = null,
            object details = null)
        {
            ValidateMonitorState();

            try
            {
                if (string.IsNullOrWhiteSpace(taskId))
                    throw new ArgumentException("Görev ID'si belirtilmelidir", nameof(taskId));

                if (!_activeTasks.TryGetValue(taskId, out var task))
                {
                    return new TaskUpdateResult;
                    {
                        Success = false,
                        TaskId = taskId,
                        Error = "Görev bulunamadı"
                    };
                }

                var previousState = task.State;
                var previousStatus = task.Status;
                var updateTime = DateTime.UtcNow;

                _logger.LogDebug($"Görev durumu güncelleniyor: {taskId} ({previousState} -> {newState})");

                // Durumu güncelle;
                task.State = newState;
                task.Status = newStatus;
                task.UpdatedAt = updateTime;

                if (!string.IsNullOrWhiteSpace(message))
                    task.LastMessage = message;

                // Durum değişikliği kaydı;
                var stateChange = new TaskStateChange;
                {
                    PreviousState = previousState,
                    NewState = newState,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    Timestamp = updateTime,
                    Message = message,
                    Details = details;
                };

                // İstatistiklere ekle;
                if (_taskStatistics.TryGetValue(taskId, out var stats))
                {
                    stats.StateChanges.Add(stateChange);
                    stats.LastStateChange = updateTime;
                }

                // Olay kaydı;
                var taskEvent = new TaskEvent;
                {
                    EventType = TaskEventType.StateChanged,
                    TaskId = taskId,
                    TaskState = newState,
                    TaskStatus = newStatus,
                    Timestamp = updateTime,
                    Message = message,
                    Details = details;
                };

                AddTaskEvent(taskId, taskEvent);

                // Görev güncelleme olayı;
                var taskUpdate = new TaskUpdate;
                {
                    TaskId = taskId,
                    Task = task,
                    UpdateType = TaskUpdateType.StateChanged,
                    PreviousState = previousState,
                    NewState = newState,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    Timestamp = updateTime,
                    Message = message,
                    Details = details;
                };

                // Yayınla;
                _taskUpdates.OnNext(taskUpdate);

                // Event bus'a gönder;
                await _eventBus.PublishAsync(new TaskStateChangedEvent;
                {
                    TaskId = taskId,
                    PreviousState = previousState,
                    NewState = newState,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    Task = task,
                    Timestamp = updateTime;
                });

                // Eğer kritik durum değişikliği varsa alert oluştur;
                if (IsCriticalStateChange(previousState, newState))
                {
                    await CreateAlertForStateChangeAsync(taskId, previousState, newState, message);
                }

                // İstatistikleri güncelle;
                UpdateStatisticsForStateChange(task, stateChange);

                // Özeti güncelle;
                await UpdateTaskSummaryAsync();

                _logger.LogInformation($"Görev durumu güncellendi: {taskId} ({previousState} -> {newState})");

                return new TaskUpdateResult;
                {
                    Success = true,
                    TaskId = taskId,
                    Task = task,
                    StateChange = stateChange,
                    UpdateTime = updateTime,
                    Message = "Görev durumu başarıyla güncellendi"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev durumu güncelleme hatası: {ex.Message}", ex);
                throw new TaskUpdateException($"Görev durumu güncellenemedi: {taskId}", ex);
            }
        }

        /// <summary>
        /// Görev ilerlemesini günceller;
        /// </summary>
        public async Task<TaskProgressResult> UpdateTaskProgressAsync(
            string taskId,
            TaskProgressUpdate progressUpdate)
        {
            ValidateMonitorState();

            try
            {
                if (string.IsNullOrWhiteSpace(taskId))
                    throw new ArgumentException("Görev ID'si belirtilmelidir", nameof(taskId));

                if (progressUpdate == null)
                    throw new ArgumentNullException(nameof(progressUpdate));

                if (!_activeTasks.TryGetValue(taskId, out var task))
                {
                    return new TaskProgressResult;
                    {
                        Success = false,
                        TaskId = taskId,
                        Error = "Görev bulunamadı"
                    };
                }

                var updateTime = DateTime.UtcNow;

                // İlerlemeyi güncelle;
                if (task.ProgressTracker == null)
                {
                    task.ProgressTracker = new TaskProgressTracker;
                    {
                        TaskId = taskId,
                        Current = progressUpdate.Current,
                        Total = progressUpdate.Total,
                        Unit = progressUpdate.Unit,
                        Message = progressUpdate.Message;
                    };
                }
                else;
                {
                    task.ProgressTracker.Current = progressUpdate.Current;
                    task.ProgressTracker.Total = progressUpdate.Total;
                    if (!string.IsNullOrWhiteSpace(progressUpdate.Unit))
                        task.ProgressTracker.Unit = progressUpdate.Unit;
                    if (!string.IsNullOrWhiteSpace(progressUpdate.Message))
                        task.ProgressTracker.Message = progressUpdate.Message;
                }

                task.ProgressTracker.LastUpdated = updateTime;
                task.ProgressTracker.Percentage = (double)progressUpdate.Current / progressUpdate.Total * 100;

                // Performans metriği ekle;
                if (progressUpdate.Metrics != null && progressUpdate.Metrics.Any())
                {
                    foreach (var metric in progressUpdate.Metrics)
                    {
                        var performanceMetric = new PerformanceMetric;
                        {
                            MetricName = metric.Key,
                            Value = metric.Value,
                            Unit = "units",
                            Timestamp = updateTime,
                            TaskId = taskId;
                        };

                        AddPerformanceMetric(taskId, performanceMetric);
                    }
                }

                // İlerleme olayı;
                var progressEvent = new TaskProgress;
                {
                    TaskId = taskId,
                    Current = progressUpdate.Current,
                    Total = progressUpdate.Total,
                    Percentage = task.ProgressTracker.Percentage,
                    Message = progressUpdate.Message,
                    Timestamp = updateTime,
                    IsIndeterminate = progressUpdate.IsIndeterminate,
                    EstimatedTimeRemaining = CalculateEstimatedTimeRemaining(task)
                };

                // Yayınla;
                _progressUpdates.OnNext(progressEvent);

                // Event bus'a gönder;
                await _eventBus.PublishAsync(new TaskProgressUpdatedEvent;
                {
                    TaskId = taskId,
                    Progress = progressEvent,
                    Task = task,
                    Timestamp = updateTime;
                });

                // Eşik kontrolü;
                await CheckProgressThresholdsAsync(task, progressEvent);

                // İstatistikleri güncelle;
                UpdateStatisticsForProgress(task, progressEvent);

                // Özeti güncelle;
                await UpdateTaskSummaryAsync();

                _logger.LogDebug($"Görev ilerlemesi güncellendi: {taskId} - {progressEvent.Percentage:F1}%");

                return new TaskProgressResult;
                {
                    Success = true,
                    TaskId = taskId,
                    Progress = progressEvent,
                    UpdateTime = updateTime,
                    Message = "Görev ilerlemesi başarıyla güncellendi"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev ilerlemesi güncelleme hatası: {ex.Message}", ex);
                throw new TaskProgressException($"Görev ilerlemesi güncellenemedi: {taskId}", ex);
            }
        }

        /// <summary>
        /// Görev tamamlandı olarak işaretler;
        /// </summary>
        public async Task<TaskCompletionResult> CompleteTaskAsync(
            string taskId,
            TaskCompletionStatus completionStatus = TaskCompletionStatus.Success,
            string resultMessage = null,
            object resultData = null)
        {
            ValidateMonitorState();

            try
            {
                if (string.IsNullOrWhiteSpace(taskId))
                    throw new ArgumentException("Görev ID'si belirtilmelidir", nameof(taskId));

                if (!_activeTasks.TryGetValue(taskId, out var task))
                {
                    return new TaskCompletionResult;
                    {
                        Success = false,
                        TaskId = taskId,
                        Error = "Görev bulunamadı"
                    };
                }

                var completionTime = DateTime.UtcNow;

                // Tamamlama durumunu belirle;
                TaskState finalState;
                TaskStatus finalStatus;

                switch (completionStatus)
                {
                    case TaskCompletionStatus.Success:
                        finalState = TaskState.Completed;
                        finalStatus = TaskStatus.Success;
                        break;
                    case TaskCompletionStatus.Failed:
                        finalState = TaskState.Failed;
                        finalStatus = TaskStatus.Failed;
                        break;
                    case TaskCompletionStatus.Cancelled:
                        finalState = TaskState.Cancelled;
                        finalStatus = TaskStatus.Cancelled;
                        break;
                    case TaskCompletionStatus.TimedOut:
                        finalState = TaskState.Timeout;
                        finalStatus = TaskStatus.Timeout;
                        break;
                    default:
                        finalState = TaskState.Completed;
                        finalStatus = TaskStatus.Success;
                        break;
                }

                // İlerlemeyi tamamla;
                if (task.ProgressTracker != null)
                {
                    task.ProgressTracker.Current = task.ProgressTracker.Total;
                    task.ProgressTracker.Percentage = 100;
                    task.ProgressTracker.Message = resultMessage ?? "Görev tamamlandı";
                    task.ProgressTracker.LastUpdated = completionTime;
                    task.ProgressTracker.CompletedAt = completionTime;
                }

                // Görev durumunu güncelle;
                var updateResult = await UpdateTaskStateAsync(
                    taskId,
                    finalState,
                    finalStatus,
                    resultMessage,
                    resultData);

                if (!updateResult.Success)
                {
                    return new TaskCompletionResult;
                    {
                        Success = false,
                        TaskId = taskId,
                        Error = "Görev durumu güncellenemedi"
                    };
                }

                // Tamamlama zamanını ayarla;
                task.CompletedAt = completionTime;
                task.CompletionStatus = completionStatus;
                task.ResultData = resultData;

                // İstatistikleri tamamla;
                CompleteTaskStatistics(taskId, completionTime, completionStatus);

                // Aktif görevlerden kaldır (belirli bir süre sonra)
                ScheduleTaskRemoval(taskId);

                _logger.LogInformation($"Görev tamamlandı: {taskId} - {completionStatus}");

                return new TaskCompletionResult;
                {
                    Success = true,
                    TaskId = taskId,
                    CompletionStatus = completionStatus,
                    CompletionTime = completionTime,
                    ResultMessage = resultMessage,
                    ResultData = resultData,
                    Message = "Görev başarıyla tamamlandı"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev tamamlama hatası: {ex.Message}", ex);
                throw new TaskCompletionException($"Görev tamamlanamadı: {taskId}", ex);
            }
        }

        /// <summary>
        /// Görev ayrıntılarını alır;
        /// </summary>
        public TaskDetails GetTaskDetails(string taskId)
        {
            ValidateMonitorState();

            try
            {
                if (!_activeTasks.TryGetValue(taskId, out var task))
                {
                    throw new KeyNotFoundException($"Görev bulunamadı: {taskId}");
                }

                var details = new TaskDetails;
                {
                    Task = task,
                    Statistics = _taskStatistics.ContainsKey(taskId) ? _taskStatistics[taskId] : null,
                    Events = _taskEvents.ContainsKey(taskId) ? _taskEvents[taskId] : new List<TaskEvent>(),
                    PerformanceMetrics = GetTaskPerformanceMetrics(taskId),
                    Dependencies = GetTaskDependencies(taskId),
                    RelatedTasks = GetRelatedTasks(taskId),
                    Timeline = BuildTaskTimeline(taskId),
                    Analysis = AnalyzeTaskPerformance(taskId),
                    Recommendations = GenerateTaskRecommendations(taskId)
                };

                return details;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev ayrıntıları alma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Görev analizi yapar;
        /// </summary>
        public async Task<TaskAnalysis> AnalyzeTaskAsync(string taskId, AnalysisOptions options = null)
        {
            ValidateMonitorState();

            try
            {
                if (!_activeTasks.TryGetValue(taskId, out var task))
                {
                    throw new KeyNotFoundException($"Görev bulunamadı: {taskId}");
                }

                var analysisOptions = options ?? new AnalysisOptions();
                var startTime = DateTime.UtcNow;

                // Analiz nesnesi oluştur;
                var analysis = new TaskAnalysis;
                {
                    TaskId = taskId,
                    TaskName = task.Name,
                    Category = task.Category,
                    AnalysisTime = startTime,
                    AnalysisDuration = TimeSpan.Zero;
                };

                // Durum analizi;
                analysis.StateAnalysis = await AnalyzeTaskStateAsync(taskId);

                // Performans analizi;
                analysis.PerformanceAnalysis = await AnalyzeTaskPerformanceAsync(taskId);

                // Kaynak kullanım analizi;
                analysis.ResourceAnalysis = await AnalyzeResourceUsageAsync(taskId);

                // Bağımlılık analizi;
                analysis.DependencyAnalysis = await AnalyzeDependenciesAsync(taskId);

                // Risk analizi;
                analysis.RiskAnalysis = await AnalyzeTaskRisksAsync(taskId);

                // Optimizasyon önerileri;
                analysis.OptimizationSuggestions = await GenerateOptimizationSuggestionsAsync(taskId, analysis);

                // Analiz süresi;
                analysis.AnalysisDuration = DateTime.UtcNow - startTime;

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev analizi hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Görev özetini alır;
        /// </summary>
        public TaskMonitorSummary GetTaskSummary(TaskSummaryOptions options = null)
        {
            ValidateMonitorState();

            try
            {
                var summaryOptions = options ?? new TaskSummaryOptions();

                var summary = new TaskMonitorSummary;
                {
                    Timestamp = DateTime.UtcNow,
                    TotalTasks = _activeTasks.Count,
                    ActiveTasks = _activeTasks.Values.Count(t => t.State == TaskState.Running),
                    PendingTasks = _activeTasks.Values.Count(t => t.State == TaskState.Pending),
                    CompletedTasks = _statistics.CompletedTasks,
                    FailedTasks = _statistics.FailedTasks,
                    CancelledTasks = _statistics.CancelledTasks,
                    AverageCompletionTime = _statistics.AverageCompletionTime,
                    SuccessRate = CalculateSuccessRate(),
                    SystemLoad = CalculateSystemLoad(),
                    TaskDistribution = GetTaskDistribution(),
                    RecentActivity = GetRecentActivity(20),
                    PerformanceMetrics = GetPerformanceMetrics(),
                    Alerts = _taskAlerts.ToList(),
                    Statistics = _statistics;
                };

                return summary;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev özeti alma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// İzlemeyi durdurur;
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (!_isMonitoring)
                return;

            try
            {
                _logger.LogInformation("Görev izleme durduruluyor...");

                State = TaskMonitorState.Stopping;
                _isMonitoring = false;

                // Timer'ları durdur;
                StopMonitoringTimers();

                // Son durum güncellemesini yap;
                await FinalizeMonitoringAsync();

                // Rapor oluştur;
                await GenerateMonitoringReportAsync();

                // Event bus'a gönder;
                await _eventBus.PublishAsync(new TaskMonitoringStoppedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    Duration = _statistics.MonitoringDuration,
                    MonitorId = GetMonitorId()
                });

                State = TaskMonitorState.Ready;

                _logger.LogInformation("Görev izleme durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError($"İzleme durdurma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Görev izleyiciyi durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Görev izleyici durduruluyor...");

                State = TaskMonitorState.Stopping;

                // İzlemeyi durdur;
                await StopMonitoringAsync();

                // Event bus kaydını kaldır;
                await UnregisterEventHandlersAsync();

                // Timer'ları temizle;
                CleanupTimers();

                // Kaynakları serbest bırak;
                await ReleaseResourcesAsync();

                // İstatistikleri kaydet;
                await SaveStatisticsAsync();

                _isInitialized = false;
                State = TaskMonitorState.Stopped;

                _logger.LogInformation("Görev izleyici başarıyla durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev izleyici durdurma hatası: {ex.Message}", ex);
                throw;
            }
        }

        #region Private Methods;

        private void ValidateMonitorState()
        {
            if (!_isInitialized || State != TaskMonitorState.Ready)
                throw new InvalidOperationException("Görev izleyici hazır durumda değil");
        }

        private void InitializeMonitoringTimers()
        {
            // Durum kontrol timer'ı;
            _monitoringTimer = new Timer(
                async _ => await PerformPeriodicTaskCheckAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            // Temizlik timer'ı;
            _cleanupTimer = new Timer(
                async _ => await PerformCleanupAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            // Raporlama timer'ı;
            _reportingTimer = new Timer(
                async _ => await GeneratePeriodicReportAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }

        private void StartMonitoringTimers()
        {
            // Timer'ları başlat;
            _monitoringTimer.Change(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(_configuration.MonitoringIntervalSeconds));

            _cleanupTimer.Change(
                TimeSpan.FromMinutes(_configuration.CleanupIntervalMinutes),
                TimeSpan.FromMinutes(_configuration.CleanupIntervalMinutes));

            _reportingTimer.Change(
                TimeSpan.FromMinutes(_configuration.ReportingIntervalMinutes),
                TimeSpan.FromMinutes(_configuration.ReportingIntervalMinutes));
        }

        private void StopMonitoringTimers()
        {
            _monitoringTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _reportingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void CleanupTimers()
        {
            _monitoringTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _reportingTimer?.Dispose();
        }

        private async Task PerformPeriodicTaskCheckAsync()
        {
            if (!_isMonitoring)
                return;

            try
            {
                var startTime = DateTime.UtcNow;

                // Tüm görevlerin durumunu kontrol et;
                var checkTasks = _activeTasks.Keys.Select(
                    async taskId => await CheckTaskStatusAsync(taskId));

                await Task.WhenAll(checkTasks);

                // Zaman aşımı kontrolü;
                await CheckTaskTimeoutsAsync();

                // Performans metriklerini topla;
                await CollectPerformanceMetricsAsync();

                // Özeti güncelle;
                await UpdateTaskSummaryAsync();

                // İstatistikleri güncelle;
                _statistics.PeriodicChecks++;
                _statistics.LastCheckTime = DateTime.UtcNow;
                _statistics.AverageCheckTime = (_statistics.AverageCheckTime * (_statistics.PeriodicChecks - 1) +
                    (DateTime.UtcNow - startTime).TotalMilliseconds) / _statistics.PeriodicChecks;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Periyodik görev kontrol hatası: {ex.Message}", ex);
            }
        }

        private async Task CheckTaskStatusAsync(string taskId)
        {
            try
            {
                if (!_activeTasks.TryGetValue(taskId, out var task))
                    return;

                // Görev durumunu kontrol et;
                var currentStatus = await GetActualTaskStatusAsync(task);

                // Eğer durum değiştiyse güncelle;
                if (currentStatus != task.Status)
                {
                    await UpdateTaskStateAsync(
                        taskId,
                        task.State,
                        currentStatus,
                        "Periyodik durum kontrolü");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Görev durum kontrol hatası: {ex.Message}", taskId, ex);
            }
        }

        private async Task CheckTaskTimeoutsAsync()
        {
            var now = DateTime.UtcNow;
            var timeoutThreshold = now.AddSeconds(-_configuration.TaskTimeoutSeconds);

            foreach (var task in _activeTasks.Values)
            {
                if (task.State == TaskState.Running &&
                    task.UpdatedAt < timeoutThreshold)
                {
                    await CompleteTaskAsync(
                        task.TaskId,
                        TaskCompletionStatus.TimedOut,
                        "Görev zaman aşımına uğradı");
                }
            }
        }

        private void ResetStatistics()
        {
            _statistics = new MonitorStatistics;
            {
                StartTime = DateTime.UtcNow,
                TotalTasks = 0,
                ActiveTasks = 0,
                CompletedTasks = 0,
                FailedTasks = 0,
                CancelledTasks = 0,
                PeriodicChecks = 0,
                AverageCompletionTime = TimeSpan.Zero,
                SuccessRate = 0,
                AverageCheckTime = 0;
            };
        }

        private void UpdateStatisticsForRegistration(MonitoredTask task)
        {
            lock (_statistics)
            {
                _statistics.TotalTasks++;
                _statistics.LastRegistration = DateTime.UtcNow;

                if (!_statistics.TaskCountsByCategory.ContainsKey(task.Category))
                    _statistics.TaskCountsByCategory[task.Category] = 0;
                _statistics.TaskCountsByCategory[task.Category]++;

                if (!_statistics.TaskCountsByPriority.ContainsKey(task.Priority))
                    _statistics.TaskCountsByPriority[task.Priority] = 0;
                _statistics.TaskCountsByPriority[task.Priority]++;
            }
        }

        private void UpdateStatisticsForStateChange(MonitoredTask task, TaskStateChange stateChange)
        {
            lock (_statistics)
            {
                _statistics.StateChanges++;

                if (stateChange.NewState == TaskState.Completed)
                {
                    _statistics.CompletedTasks++;
                    if (task.CreatedAt.HasValue)
                    {
                        var completionTime = DateTime.UtcNow - task.CreatedAt.Value;
                        _statistics.AverageCompletionTime = (_statistics.AverageCompletionTime * (_statistics.CompletedTasks - 1) + completionTime.TotalMilliseconds) / _statistics.CompletedTasks;
                    }
                }
                else if (stateChange.NewState == TaskState.Failed)
                {
                    _statistics.FailedTasks++;
                }
                else if (stateChange.NewState == TaskState.Cancelled)
                {
                    _statistics.CancelledTasks++;
                }

                // Başarı oranını güncelle;
                _statistics.SuccessRate = _statistics.CompletedTasks > 0 ?
                    (double)_statistics.CompletedTasks / (_statistics.CompletedTasks + _statistics.FailedTasks) : 0;
            }
        }

        private void UpdateStatisticsForProgress(MonitoredTask task, TaskProgress progress)
        {
            lock (_statistics)
            {
                _statistics.ProgressUpdates++;
                _statistics.LastProgressUpdate = DateTime.UtcNow;

                if (!_statistics.AverageProgress.ContainsKey(task.TaskId))
                    _statistics.AverageProgress[task.TaskId] = 0;

                _statistics.AverageProgress[task.TaskId] = (_statistics.AverageProgress[task.TaskId] + progress.Percentage) / 2;
            }
        }

        private void CompleteTaskStatistics(string taskId, DateTime completionTime, TaskCompletionStatus completionStatus)
        {
            lock (_statistics)
            {
                _statistics.CompletedTaskCount++;

                if (_activeTasks.TryGetValue(taskId, out var task) && task.CreatedAt.HasValue)
                {
                    var duration = completionTime - task.CreatedAt.Value;
                    _statistics.TotalTaskDuration += duration;
                    _statistics.AverageTaskDuration = _statistics.TotalTaskDuration / _statistics.CompletedTaskCount;

                    // Kategoriye göre istatistik;
                    if (!_statistics.DurationByCategory.ContainsKey(task.Category))
                        _statistics.DurationByCategory[task.Category] = TimeSpan.Zero;
                    _statistics.DurationByCategory[task.Category] += duration;
                }
            }
        }

        private bool IsCriticalStateChange(TaskState previousState, TaskState newState)
        {
            // Kritik durum değişiklikleri;
            var criticalTransitions = new[]
            {
                (TaskState.Running, TaskState.Failed),
                (TaskState.Running, TaskState.Timeout),
                (TaskState.Pending, TaskState.Failed),
                (TaskState.Pending, TaskState.Timeout)
            };

            return criticalTransitions.Contains((previousState, newState));
        }

        private async Task CreateAlertForStateChangeAsync(
            string taskId,
            TaskState previousState,
            TaskState newState,
            string message)
        {
            var alert = new TaskAlert;
            {
                TaskId = taskId,
                Title = $"{taskId} Görev Durum Değişikliği",
                Message = $"Görev durumu {previousState} -> {newState} olarak değişti: {message}",
                Severity = GetAlertSeverityForStateChange(previousState, newState),
                Category = TaskAlertCategory.StateChange,
                Source = "TaskMonitor",
                Timestamp = DateTime.UtcNow,
                Details = new Dictionary<string, object>
                {
                    ["previous_state"] = previousState,
                    ["new_state"] = newState,
                    ["task_id"] = taskId,
                    ["message"] = message;
                }
            };

            _taskAlerts.Enqueue(alert);
            _alertUpdates.OnNext(alert);
        }

        private TaskAlertSeverity GetAlertSeverityForStateChange(TaskState previousState, TaskState newState)
        {
            switch (newState)
            {
                case TaskState.Failed:
                    return TaskAlertSeverity.Critical;
                case TaskState.Timeout:
                    return TaskAlertSeverity.High;
                case TaskState.Cancelled:
                    return TaskAlertSeverity.Medium;
                default:
                    return TaskAlertSeverity.Info;
            }
        }

        private async Task CheckProgressThresholdsAsync(MonitoredTask task, TaskProgress progress)
        {
            // İlerleme eşiklerini kontrol et;
            if (task.Options?.ProgressThresholds != null)
            {
                foreach (var threshold in task.Options.ProgressThresholds)
                {
                    if (progress.Percentage >= threshold.Percentage && !threshold.Triggered)
                    {
                        threshold.Triggered = true;

                        // Eşik olayı;
                        await _eventBus.PublishAsync(new ProgressThresholdReachedEvent;
                        {
                            TaskId = task.TaskId,
                            Threshold = threshold,
                            Progress = progress,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }
            }
        }

        private TimeSpan? CalculateEstimatedTimeRemaining(MonitoredTask task)
        {
            if (task.ProgressTracker == null || task.ProgressTracker.Percentage <= 0)
                return null;

            if (!task.CreatedAt.HasValue)
                return null;

            var elapsed = DateTime.UtcNow - task.CreatedAt.Value;
            var estimatedTotal = elapsed / (task.ProgressTracker.Percentage / 100);
            return estimatedTotal - elapsed;
        }

        private void AddTaskEvent(string taskId, TaskEvent taskEvent)
        {
            if (_taskEvents.TryGetValue(taskId, out var events))
            {
                events.Add(taskEvent);
            }
        }

        private void AddPerformanceMetric(string taskId, PerformanceMetric metric)
        {
            if (_taskStatistics.TryGetValue(taskId, out var stats))
            {
                stats.PerformanceMetrics.Add(metric);
            }
        }

        private void ScheduleTaskRemoval(string taskId)
        {
            // Görevi belirli bir süre sonra aktif listeden kaldır;
            Task.Delay(_configuration.CompletedTaskRetentionMinutes * 60000).ContinueWith(async _ =>
            {
                _activeTasks.TryRemove(taskId, out _);
                _logger.LogDebug($"Görev aktif listeden kaldırıldı: {taskId}");
            });
        }

        private async Task UpdateTaskSummaryAsync()
        {
            var summary = GetTaskSummary();
            Summary = summary;
            _summaryUpdates.OnNext(summary);
        }

        private double CalculateSuccessRate()
        {
            if (_statistics.CompletedTasks + _statistics.FailedTasks == 0)
                return 0;

            return (double)_statistics.CompletedTasks / (_statistics.CompletedTasks + _statistics.FailedTasks) * 100;
        }

        private double CalculateSystemLoad()
        {
            var activeTasks = _activeTasks.Values.Count(t => t.State == TaskState.Running);
            var maxConcurrent = _configuration.MaxConcurrentTasks > 0 ? _configuration.MaxConcurrentTasks : 10;
            return (double)activeTasks / maxConcurrent * 100;
        }

        private async Task PerformCleanupAsync()
        {
            try
            {
                // Eski tamamlanmış görevleri temizle;
                await CleanupCompletedTasksAsync();

                // Eski alert'leri temizle;
                await CleanupOldAlertsAsync();

                // Tarihçe temizliği;
                await CleanupHistoryAsync();

                _logger.LogDebug("Görev temizliği tamamlandı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Temizleme hatası: {ex.Message}", ex);
            }
        }

        private async Task ReleaseResourcesAsync()
        {
            // Subject'leri tamamla;
            _taskUpdates.OnCompleted();
            _progressUpdates.OnCompleted();
            _alertUpdates.OnCompleted();
            _summaryUpdates.OnCompleted();

            // Subject'leri dispose et;
            _taskUpdates.Dispose();
            _progressUpdates.Dispose();
            _alertUpdates.Dispose();
            _summaryUpdates.Dispose();

            // Koleksiyonları temizle;
            _activeTasks.Clear();
            _taskStatistics.Clear();
            _taskEvents.Clear();

            while (_taskAlerts.TryDequeue(out _)) { }
        }

        private async Task SaveStatisticsAsync()
        {
            try
            {
                // İstatistikleri kalıcı depolama alanına kaydet;
                var statsRepository = new TaskStatisticsRepository();
                await statsRepository.SaveAsync(_statistics);
            }
            catch (Exception ex)
            {
                _logger.LogError($"İstatistik kaydetme hatası: {ex.Message}", ex);
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    CleanupTimers();
                    ReleaseResourcesAsync().Wait();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// İzlenen görev;
    /// </summary>
    public class MonitoredTask;
    {
        public string TaskId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public TaskPriority Priority { get; set; }
        public TaskState State { get; set; }
        public TaskStatus Status { get; set; }
        public TaskProgressTracker ProgressTracker { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TaskCompletionStatus? CompletionStatus { get; set; }
        public string LastMessage { get; set; }
        public TaskOptions Options { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public string Owner { get; set; }
        public List<string> Tags { get; set; }
        public object ResultData { get; set; }
    }

    /// <summary>
    /// Görev durumu;
    /// </summary>
    public enum TaskState;
    {
        Unknown,
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled,
        Timeout,
        Suspended,
        Waiting;
    }

    /// <summary>
    /// Görev durumu detayı;
    /// </summary>
    public enum TaskStatus;
    {
        Created,
        Initializing,
        Running,
        Paused,
        Resumed,
        Success,
        Failed,
        Cancelled,
        Timeout,
        Warning,
        Error;
    }

    /// <summary>
    /// Görev tamamlama durumu;
    /// </summary>
    public enum TaskCompletionStatus;
    {
        Success,
        Failed,
        Cancelled,
        TimedOut,
        Partial,
        Warning;
    }

    /// <summary>
    /// Görev önceliği;
    /// </summary>
    public enum TaskPriority;
    {
        Lowest,
        Low,
        Normal,
        High,
        Highest,
        Critical;
    }

    /// <summary>
    /// Görev izleyici durumu;
    /// </summary>
    public enum TaskMonitorState;
    {
        Stopped,
        Initializing,
        Ready,
        Monitoring,
        Stopping,
        Error;
    }

    /// <summary>
    /// Görev güncelleme tipi;
    /// </summary>
    public enum TaskUpdateType;
    {
        Registered,
        StateChanged,
        ProgressUpdated,
        MetadataUpdated,
        OptionsUpdated,
        Completed,
        Alert;
    }

    /// <summary>
    /// Görev ilerleme izleyici;
    /// </summary>
    public class TaskProgressTracker;
    {
        public string TaskId { get; set; }
        public long Current { get; set; }
        public long Total { get; set; }
        public double Percentage { get; set; }
        public string Unit { get; set; }
        public string Message { get; set; }
        public DateTime? LastUpdated { get; set; }
        public DateTime? CompletedAt { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    /// <summary>
    /// Görev seçenekleri;
    /// </summary>
    public class TaskOptions;
    {
        public bool IsBackground { get; set; }
        public bool SupportsCancellation { get; set; }
        public bool SupportsPause { get; set; }
        public int TimeoutSeconds { get; set; } = 3600;
        public int RetryCount { get; set; }
        public List<ProgressThreshold> ProgressThresholds { get; set; }
        public Dictionary<string, object> CustomOptions { get; set; }
    }

    /// <summary>
    /// Görev istatistikleri;
    /// </summary>
    public class TaskStatistics;
    {
        public string TaskId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
        public List<TaskStateChange> StateChanges { get; set; }
        public List<PerformanceMetric> PerformanceMetrics { get; set; }
        public TimeSpan? TotalDuration { get; set; }
        public double? AverageProgressRate { get; set; }
    }

    /// <summary>
    /// İzleyici istatistikleri;
    /// </summary>
    public class MonitorStatistics;
    {
        public DateTime StartTime { get; set; }
        public TimeSpan MonitoringDuration => DateTime.UtcNow - StartTime;
        public int TotalTasks { get; set; }
        public int ActiveTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int FailedTasks { get; set; }
        public int CancelledTasks { get; set; }
        public int CompletedTaskCount { get; set; }
        public TimeSpan TotalTaskDuration { get; set; }
        public TimeSpan AverageTaskDuration { get; set; }
        public TimeSpan AverageCompletionTime { get; set; }
        public double SuccessRate { get; set; }
        public int StateChanges { get; set; }
        public int ProgressUpdates { get; set; }
        public int PeriodicChecks { get; set; }
        public double AverageCheckTime { get; set; }
        public DateTime LastRegistration { get; set; }
        public DateTime LastProgressUpdate { get; set; }
        public DateTime LastCheckTime { get; set; }
        public double SystemLoad { get; set; }
        public Dictionary<string, int> TaskCountsByCategory { get; set; } = new Dictionary<string, int>();
        public Dictionary<TaskPriority, int> TaskCountsByPriority { get; set; } = new Dictionary<TaskPriority, int>();
        public Dictionary<string, double> AverageProgress { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, TimeSpan> DurationByCategory { get; set; } = new Dictionary<string, TimeSpan>();
    }

    /// <summary>
    /// Görev izleyici yapılandırması;
    /// </summary>
    public class TaskMonitorConfiguration;
    {
        public int MonitoringIntervalSeconds { get; set; } = 5;
        public int CleanupIntervalMinutes { get; set; } = 60;
        public int ReportingIntervalMinutes { get; set; } = 10;
        public int MaxConcurrentTasks { get; set; } = 50;
        public int TaskTimeoutSeconds { get; set; } = 7200;
        public int CompletedTaskRetentionMinutes { get; set; } = 60;
        public int MaxTaskHistory { get; set; } = 1000;
        public bool EnablePerformanceTracking { get; set; } = true;
        public bool EnableAlerting { get; set; } = true;
        public MonitoringLevel MonitoringLevel { get; set; } = MonitoringLevel.Normal;
        public Dictionary<string, ThresholdConfiguration> Thresholds { get; set; } = new Dictionary<string, ThresholdConfiguration>();
    }

    /// <summary>
    /// Görev özeti;
    /// </summary>
    public class TaskMonitorSummary;
    {
        public DateTime Timestamp { get; set; }
        public int TotalTasks { get; set; }
        public int ActiveTasks { get; set; }
        public int PendingTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int FailedTasks { get; set; }
        public int CancelledTasks { get; set; }
        public TimeSpan AverageCompletionTime { get; set; }
        public double SuccessRate { get; set; }
        public double SystemLoad { get; set; }
        public Dictionary<string, int> TaskDistribution { get; set; }
        public List<TaskEvent> RecentActivity { get; set; }
        public Dictionary<string, PerformanceMetric> PerformanceMetrics { get; set; }
        public List<TaskAlert> Alerts { get; set; }
        public MonitorStatistics Statistics { get; set; }
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// Görev izleyici arayüzü;
    /// </summary>
    public interface ITaskMonitor : IDisposable
    {
        Task InitializeAsync(TaskMonitorConfiguration configuration = null);
        Task StartMonitoringAsync(MonitoringOptions options = null);
        Task<TaskRegistrationResult> RegisterTaskAsync(TaskRegistrationRequest request, TaskOptions options = null);
        Task<TaskUpdateResult> UpdateTaskStateAsync(string taskId, TaskState newState, TaskStatus newStatus = TaskStatus.Running, string message = null, object details = null);
        Task<TaskProgressResult> UpdateTaskProgressAsync(string taskId, TaskProgressUpdate progressUpdate);
        Task<TaskCompletionResult> CompleteTaskAsync(string taskId, TaskCompletionStatus completionStatus = TaskCompletionStatus.Success, string resultMessage = null, object resultData = null);
        TaskDetails GetTaskDetails(string taskId);
        Task<TaskAnalysis> AnalyzeTaskAsync(string taskId, AnalysisOptions options = null);
        TaskMonitorSummary GetTaskSummary(TaskSummaryOptions options = null);
        Task StopMonitoringAsync();
        Task ShutdownAsync();
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Görev izleyici istisnası;
    /// </summary>
    public class TaskMonitorException : Exception
    {
        public TaskMonitorException() { }
        public TaskMonitorException(string message) : base(message) { }
        public TaskMonitorException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Görev kayıt istisnası;
    /// </summary>
    public class TaskRegistrationException : TaskMonitorException;
    {
        public TaskRegistrationException() { }
        public TaskRegistrationException(string message) : base(message) { }
        public TaskRegistrationException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Görev güncelleme istisnası;
    /// </summary>
    public class TaskUpdateException : TaskMonitorException;
    {
        public TaskUpdateException() { }
        public TaskUpdateException(string message) : base(message) { }
        public TaskUpdateException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Görev tamamlama istisnası;
    /// </summary>
    public class TaskCompletionException : TaskMonitorException;
    {
        public TaskCompletionException() { }
        public TaskCompletionException(string message) : base(message) { }
        public TaskCompletionException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
