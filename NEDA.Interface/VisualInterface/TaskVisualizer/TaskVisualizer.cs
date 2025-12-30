using DynamicData;
using DynamicData.Binding;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.Core.SystemControl;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Services.Messaging.EventBus;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NEDA.Interface.VisualInterface.TaskVisualizer;
{
    /// <summary>
    /// Represents a task in the visualization system;
    /// </summary>
    public class TaskItem : ReactiveObject;
    {
        private string _id;
        private string _name;
        private string _description;
        private TaskStatus _status;
        private double _progress;
        private DateTime _startTime;
        private DateTime? _endTime;
        private PriorityLevel _priority;
        private string _assignedTo;
        private Dictionary<string, object> _metadata;
        private Color _statusColor;
        private BitmapImage _icon;
        private List<TaskItem> _dependencies;
        private List<TaskItem> _subTasks;
        private object _tag;

        /// <summary>
        /// Unique identifier for the task;
        /// </summary>
        public string Id;
        {
            get => _id;
            set => this.RaiseAndSetIfChanged(ref _id, value);
        }

        /// <summary>
        /// Task name;
        /// </summary>
        public string Name;
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        /// <summary>
        /// Detailed description;
        /// </summary>
        public string Description;
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        /// <summary>
        /// Current status;
        /// </summary>
        public TaskStatus Status;
        {
            get => _status;
            set;
            {
                this.RaiseAndSetIfChanged(ref _status, value);
                UpdateStatusColor();
            }
        }

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public double Progress;
        {
            get => _progress;
            set => this.RaiseAndSetIfChanged(ref _progress, Math.Clamp(value, 0, 100));
        }

        /// <summary>
        /// Task start time;
        /// </summary>
        public DateTime StartTime;
        {
            get => _startTime;
            set => this.RaiseAndSetIfChanged(ref _startTime, value);
        }

        /// <summary>
        /// Task completion time;
        /// </summary>
        public DateTime? EndTime;
        {
            get => _endTime;
            set => this.RaiseAndSetIfChanged(ref _endTime, value);
        }

        /// <summary>
        /// Task priority;
        /// </summary>
        public PriorityLevel Priority;
        {
            get => _priority;
            set => this.RaiseAndSetIfChanged(ref _priority, value);
        }

        /// <summary>
        /// Assigned user or system;
        /// </summary>
        public string AssignedTo;
        {
            get => _assignedTo;
            set => this.RaiseAndSetIfChanged(ref _assignedTo, value);
        }

        /// <summary>
        /// Additional metadata;
        /// </summary>
        public Dictionary<string, object> Metadata;
        {
            get => _metadata ??= new Dictionary<string, object>();
            set => this.RaiseAndSetIfChanged(ref _metadata, value);
        }

        /// <summary>
        /// Status color for visualization;
        /// </summary>
        public Color StatusColor;
        {
            get => _statusColor;
            private set => this.RaiseAndSetIfChanged(ref _statusColor, value);
        }

        /// <summary>
        /// Task icon;
        /// </summary>
        public BitmapImage Icon;
        {
            get => _icon;
            set => this.RaiseAndSetIfChanged(ref _icon, value);
        }

        /// <summary>
        /// Task dependencies;
        /// </summary>
        public List<TaskItem> Dependencies;
        {
            get => _dependencies ??= new List<TaskItem>();
            set => this.RaiseAndSetIfChanged(ref _dependencies, value);
        }

        /// <summary>
        /// Sub-tasks;
        /// </summary>
        public List<TaskItem> SubTasks;
        {
            get => _subTasks ??= new List<TaskItem>();
            set => this.RaiseAndSetIfChanged(ref _subTasks, value);
        }

        /// <summary>
        /// Custom tag object;
        /// </summary>
        public object Tag;
        {
            get => _tag;
            set => this.RaiseAndSetIfChanged(ref _tag, value);
        }

        /// <summary>
        /// Estimated duration;
        /// </summary>
        public TimeSpan? EstimatedDuration { get; set; }

        /// <summary>
        /// Actual duration;
        /// </summary>
        public TimeSpan? ActualDuration => EndTime.HasValue ? EndTime.Value - StartTime : null;

        /// <summary>
        /// Creates a new task item;
        /// </summary>
        public TaskItem(string id, string name)
        {
            Id = id;
            Name = name;
            Status = TaskStatus.Pending;
            Progress = 0;
            StartTime = DateTime.Now;
            Priority = PriorityLevel.Normal;
            UpdateStatusColor();
        }

        private void UpdateStatusColor()
        {
            StatusColor = _status switch;
            {
                TaskStatus.Pending => Colors.Gray,
                TaskStatus.Queued => Colors.LightBlue,
                TaskStatus.Running => Colors.Blue,
                TaskStatus.Paused => Colors.Orange,
                TaskStatus.Completed => Colors.Green,
                TaskStatus.Failed => Colors.Red,
                TaskStatus.Cancelled => Colors.DarkGray,
                TaskStatus.Blocked => Colors.DarkOrange,
                _ => Colors.Gray;
            };
        }

        /// <summary>
        /// Starts the task;
        /// </summary>
        public void Start()
        {
            Status = TaskStatus.Running;
            StartTime = DateTime.Now;
            EndTime = null;
        }

        /// <summary>
        /// Completes the task;
        /// </summary>
        public void Complete()
        {
            Status = TaskStatus.Completed;
            Progress = 100;
            EndTime = DateTime.Now;
        }

        /// <summary>
        /// Fails the task;
        /// </summary>
        public void Fail(string errorMessage = null)
        {
            Status = TaskStatus.Failed;
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Metadata["ErrorMessage"] = errorMessage;
            }
        }

        /// <summary>
        /// Updates progress;
        /// </summary>
        public void UpdateProgress(double progress)
        {
            Progress = progress;
            if (Math.Abs(progress - 100) < 0.01)
            {
                Complete();
            }
        }

        /// <summary>
        /// Adds a dependency;
        /// </summary>
        public void AddDependency(TaskItem task)
        {
            if (!Dependencies.Contains(task))
            {
                Dependencies.Add(task);
            }
        }

        /// <summary>
        /// Adds a sub-task;
        /// </summary>
        public void AddSubTask(TaskItem subTask)
        {
            if (!SubTasks.Contains(subTask))
            {
                SubTasks.Add(subTask);
            }
        }

        /// <summary>
        /// Gets all nested tasks (recursive)
        /// </summary>
        public IEnumerable<TaskItem> GetAllNestedTasks()
        {
            yield return this;
            foreach (var subTask in SubTasks)
            {
                foreach (var nested in subTask.GetAllNestedTasks())
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Task status enumeration;
    /// </summary>
    public enum TaskStatus;
    {
        Pending,
        Queued,
        Running,
        Paused,
        Completed,
        Failed,
        Cancelled,
        Blocked;
    }

    /// <summary>
    /// Priority level enumeration;
    /// </summary>
    public enum PriorityLevel;
    {
        Critical,
        High,
        Normal,
        Low,
        Background;
    }

    /// <summary>
    /// Visualization options for tasks;
    /// </summary>
    public class VisualizationOptions;
    {
        public bool ShowProgressBars { get; set; } = true;
        public bool ShowIcons { get; set; } = true;
        public bool ShowStatusColors { get; set; } = true;
        public bool ShowDependencies { get; set; } = true;
        public bool GroupByStatus { get; set; }
        public bool GroupByPriority { get; set; }
        public bool GroupByAssignee { get; set; }
        public bool ShowTimeline { get; set; } = true;
        public bool AutoRefresh { get; set; } = true;
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxVisibleTasks { get; set; } = 100;
        public ColorScheme ColorScheme { get; set; } = ColorScheme.Default;
        public LayoutType LayoutType { get; set; } = LayoutType.Hierarchical;
        public AnimationSpeed AnimationSpeed { get; set; } = AnimationSpeed.Normal;
    }

    /// <summary>
    /// Color scheme for visualization;
    /// </summary>
    public enum ColorScheme;
    {
        Default,
        HighContrast,
        DarkMode,
        ColorBlind,
        Custom;
    }

    /// <summary>
    /// Layout type for task visualization;
    /// </summary>
    public enum LayoutType;
    {
        Hierarchical,
        Timeline,
        Kanban,
        Gantt,
        Network,
        Radial;
    }

    /// <summary>
    /// Animation speed;
    /// </summary>
    public enum AnimationSpeed;
    {
        None,
        Slow,
        Normal,
        Fast,
        Instant;
    }

    /// <summary>
    /// Main Task Visualizer class responsible for visualizing tasks in various formats;
    /// </summary>
    public class TaskVisualizer : ReactiveObject, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ISystemManager _systemManager;
        private readonly SourceCache<TaskItem, string> _tasksCache;
        private readonly ReadOnlyObservableCollection<TaskItem> _filteredTasks;
        private readonly IDisposable _cleanup;
        private VisualizationOptions _options;
        private TaskItem _selectedTask;
        private string _filterText;
        private TaskStatus? _statusFilter;
        private PriorityLevel? _priorityFilter;
        private DateTime? _dateRangeStart;
        private DateTime? _dateRangeEnd;
        private bool _isDisposed;
        private IDisposable _refreshSubscription;
        private Dictionary<string, BitmapImage> _iconCache;

        /// <summary>
        /// All tasks in the system;
        /// </summary>
        public IObservableCache<TaskItem, string> Tasks => _tasksCache;

        /// <summary>
        /// Filtered tasks based on current filters;
        /// </summary>
        public ReadOnlyObservableCollection<TaskItem> FilteredTasks => _filteredTasks;

        /// <summary>
        /// Visualization options;
        /// </summary>
        public VisualizationOptions Options;
        {
            get => _options;
            set => this.RaiseAndSetIfChanged(ref _options, value);
        }

        /// <summary>
        /// Currently selected task;
        /// </summary>
        public TaskItem SelectedTask;
        {
            get => _selectedTask;
            set => this.RaiseAndSetIfChanged(ref _selectedTask, value);
        }

        /// <summary>
        /// Filter text for searching tasks;
        /// </summary>
        public string FilterText;
        {
            get => _filterText;
            set => this.RaiseAndSetIfChanged(ref _filterText, value);
        }

        /// <summary>
        /// Status filter;
        /// </summary>
        public TaskStatus? StatusFilter;
        {
            get => _statusFilter;
            set => this.RaiseAndSetIfChanged(ref _statusFilter, value);
        }

        /// <summary>
        /// Priority filter;
        /// </summary>
        public PriorityLevel? PriorityFilter;
        {
            get => _priorityFilter;
            set => this.RaiseAndSetIfChanged(ref _priorityFilter, value);
        }

        /// <summary>
        /// Date range start for filtering;
        /// </summary>
        public DateTime? DateRangeStart;
        {
            get => _dateRangeStart;
            set => this.RaiseAndSetIfChanged(ref _dateRangeStart, value);
        }

        /// <summary>
        /// Date range end for filtering;
        /// </summary>
        public DateTime? DateRangeEnd;
        {
            get => _dateRangeEnd;
            set => this.RaiseAndSetIfChanged(ref _dateRangeEnd, value);
        }

        /// <summary>
        /// Total task count;
        /// </summary>
        public int TotalTaskCount => _tasksCache.Count;

        /// <summary>
        /// Completed task count;
        /// </summary>
        public int CompletedTaskCount => _tasksCache.Items.Count(t => t.Status == TaskStatus.Completed);

        /// <summary>
        /// Running task count;
        /// </summary>
        public int RunningTaskCount => _tasksCache.Items.Count(t => t.Status == TaskStatus.Running);

        /// <summary>
        /// Failed task count;
        /// </summary>
        public int FailedTaskCount => _tasksCache.Items.Count(t => t.Status == TaskStatus.Failed);

        /// <summary>
        /// Overall progress percentage;
        /// </summary>
        public double OverallProgress;
        {
            get;
            {
                var tasks = _tasksCache.Items.Where(t => t.Status != TaskStatus.Cancelled).ToList();
                if (tasks.Count == 0) return 0;
                return tasks.Average(t => t.Progress);
            }
        }

        /// <summary>
        /// Creates a new TaskVisualizer instance;
        /// </summary>
        public TaskVisualizer(ILogger logger, IEventBus eventBus, ISystemManager systemManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));

            _tasksCache = new SourceCache<TaskItem, string>(t => t.Id);
            _iconCache = new Dictionary<string, BitmapImage>();
            Options = new VisualizationOptions();

            // Setup filtering with DynamicData;
            _cleanup = _tasksCache.Connect()
                .Filter(ApplyFilters)
                .Sort(SortExpressionComparer<TaskItem>.Ascending(t => t.Priority)
                    .ThenByDescending(t => t.StartTime))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _filteredTasks)
                .Subscribe();

            // Setup auto-refresh if enabled;
            SetupAutoRefresh();

            // Subscribe to system events;
            SubscribeToEvents();

            _logger.LogInformation("TaskVisualizer initialized successfully");
        }

        /// <summary>
        /// Adds a task to visualization;
        /// </summary>
        public void AddTask(TaskItem task)
        {
            try
            {
                if (task == null) throw new ArgumentNullException(nameof(task));

                _tasksCache.AddOrUpdate(task);
                LoadIconForTask(task);

                _logger.LogDebug($"Task added to visualization: {task.Name} ({task.Id})");

                // Publish event;
                _eventBus.Publish(new TaskAddedEvent(task));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding task to visualization: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Removes a task from visualization;
        /// </summary>
        public bool RemoveTask(string taskId)
        {
            try
            {
                var removed = _tasksCache.Remove(taskId);
                if (removed)
                {
                    _logger.LogDebug($"Task removed from visualization: {taskId}");
                    _eventBus.Publish(new TaskRemovedEvent(taskId));
                }
                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing task from visualization: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Updates an existing task;
        /// </summary>
        public void UpdateTask(TaskItem task)
        {
            try
            {
                if (task == null) throw new ArgumentNullException(nameof(task));

                _tasksCache.AddOrUpdate(task);

                _logger.LogDebug($"Task updated in visualization: {task.Name} ({task.Id})");

                // Publish update event;
                _eventBus.Publish(new TaskUpdatedEvent(task));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating task in visualization: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Gets a task by ID;
        /// </summary>
        public TaskItem GetTask(string taskId)
        {
            return _tasksCache.Lookup(taskId).ValueOrDefault();
        }

        /// <summary>
        /// Gets all tasks with a specific status;
        /// </summary>
        public IEnumerable<TaskItem> GetTasksByStatus(TaskStatus status)
        {
            return _tasksCache.Items.Where(t => t.Status == status);
        }

        /// <summary>
        /// Gets all tasks with a specific priority;
        /// </summary>
        public IEnumerable<TaskItem> GetTasksByPriority(PriorityLevel priority)
        {
            return _tasksCache.Items.Where(t => t.Priority == priority);
        }

        /// <summary>
        /// Gets tasks assigned to a specific user/system;
        /// </summary>
        public IEnumerable<TaskItem> GetTasksByAssignee(string assignee)
        {
            return _tasksCache.Items.Where(t => t.AssignedTo == assignee);
        }

        /// <summary>
        /// Clears all tasks;
        /// </summary>
        public void ClearAllTasks()
        {
            try
            {
                _tasksCache.Clear();
                _iconCache.Clear();
                _logger.LogInformation("All tasks cleared from visualization");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error clearing tasks: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Exports tasks to a specific format;
        /// </summary>
        public async Task<string> ExportTasks(ExportFormat format, string filePath = null)
        {
            try
            {
                var tasks = _tasksCache.Items.ToList();

                switch (format)
                {
                    case ExportFormat.JSON:
                        return await ExportToJson(tasks, filePath);
                    case ExportFormat.XML:
                        return await ExportToXml(tasks, filePath);
                    case ExportFormat.CSV:
                        return await ExportToCsv(tasks, filePath);
                    case ExportFormat.HTML:
                        return await ExportToHtml(tasks, filePath);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(format), format, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error exporting tasks: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Imports tasks from a file;
        /// </summary>
        public async Task ImportTasks(ExportFormat format, string filePath)
        {
            try
            {
                if (!System.IO.File.Exists(filePath))
                    throw new FileNotFoundException($"File not found: {filePath}");

                var tasks = format switch;
                {
                    ExportFormat.JSON => await ImportFromJson(filePath),
                    ExportFormat.XML => await ImportFromXml(filePath),
                    ExportFormat.CSV => await ImportFromCsv(filePath),
                    _ => throw new ArgumentOutOfRangeException(nameof(format))
                };

                foreach (var task in tasks)
                {
                    AddTask(task);
                }

                _logger.LogInformation($"Imported {tasks.Count} tasks from {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error importing tasks: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Generates a visualization report;
        /// </summary>
        public async Task<VisualizationReport> GenerateReport()
        {
            try
            {
                var tasks = _tasksCache.Items.ToList();
                var report = new VisualizationReport;
                {
                    GeneratedAt = DateTime.Now,
                    TotalTasks = TotalTaskCount,
                    CompletedTasks = CompletedTaskCount,
                    RunningTasks = RunningTaskCount,
                    FailedTasks = FailedTaskCount,
                    OverallProgress = OverallProgress,
                    TasksByStatus = tasks.GroupBy(t => t.Status)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    TasksByPriority = tasks.GroupBy(t => t.Priority)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    AverageCompletionTime = CalculateAverageCompletionTime(tasks),
                    BusiestTime = CalculateBusiestTime(tasks),
                    MostCommonTaskTypes = CalculateMostCommonTaskTypes(tasks)
                };

                await Task.CompletedTask;
                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating report: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Applies visualization theme;
        /// </summary>
        public void ApplyTheme(ColorScheme scheme)
        {
            try
            {
                Options.ColorScheme = scheme;

                // Update color scheme for all tasks;
                foreach (var task in _tasksCache.Items)
                {
                    task.UpdateStatusColor();
                }

                _logger.LogInformation($"Applied theme: {scheme}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error applying theme: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sets up auto-refresh based on options;
        /// </summary>
        private void SetupAutoRefresh()
        {
            _refreshSubscription?.Dispose();

            if (Options.AutoRefresh)
            {
                _refreshSubscription = Observable.Interval(Options.RefreshInterval)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshVisualization());
            }
        }

        /// <summary>
        /// Applies all active filters;
        /// </summary>
        private bool ApplyFilters(TaskItem task)
        {
            var filter = true;

            // Text filter;
            if (!string.IsNullOrEmpty(FilterText))
            {
                filter = filter && (task.Name?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) == true ||
                                  task.Description?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) == true ||
                                  task.Id?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) == true);
            }

            // Status filter;
            if (StatusFilter.HasValue)
            {
                filter = filter && task.Status == StatusFilter.Value;
            }

            // Priority filter;
            if (PriorityFilter.HasValue)
            {
                filter = filter && task.Priority == PriorityFilter.Value;
            }

            // Date range filter;
            if (DateRangeStart.HasValue)
            {
                filter = filter && task.StartTime >= DateRangeStart.Value;
            }

            if (DateRangeEnd.HasValue)
            {
                filter = filter && task.StartTime <= DateRangeEnd.Value;
            }

            return filter;
        }

        /// <summary>
        /// Refreshes the visualization;
        /// </summary>
        private void RefreshVisualization()
        {
            try
            {
                // Force property change notifications;
                this.RaisePropertyChanged(nameof(OverallProgress));
                this.RaisePropertyChanged(nameof(CompletedTaskCount));
                this.RaisePropertyChanged(nameof(RunningTaskCount));
                this.RaisePropertyChanged(nameof(FailedTaskCount));

                _logger.LogDebug("Visualization refreshed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error refreshing visualization: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads icon for a task;
        /// </summary>
        private void LoadIconForTask(TaskItem task)
        {
            try
            {
                if (!Options.ShowIcons || task.Icon != null) return;

                // Determine icon based on task type/status;
                var iconKey = DetermineIconKey(task);

                if (!_iconCache.TryGetValue(iconKey, out var icon))
                {
                    icon = LoadIconFromResources(iconKey);
                    if (icon != null)
                    {
                        _iconCache[iconKey] = icon;
                    }
                }

                task.Icon = icon;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error loading icon for task: {ex.Message}");
            }
        }

        /// <summary>
        /// Determines icon key for a task;
        /// </summary>
        private string DetermineIconKey(TaskItem task)
        {
            // This is a simplified version - in real implementation,
            // you would have a more sophisticated icon selection logic;
            return task.Status switch;
            {
                TaskStatus.Running => "Running",
                TaskStatus.Completed => "Completed",
                TaskStatus.Failed => "Failed",
                TaskStatus.Paused => "Paused",
                _ => "Default"
            };
        }

        /// <summary>
        /// Loads icon from resources;
        /// </summary>
        private BitmapImage LoadIconFromResources(string iconKey)
        {
            // In real implementation, load from application resources;
            // This is a placeholder implementation;
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Icons/{iconKey}.png", UriKind.Absolute);
                return new BitmapImage(uri);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Subscribes to system events;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<TaskProgressEvent>(OnTaskProgressChanged);
            _eventBus.Subscribe<TaskStatusEvent>(OnTaskStatusChanged);
            _eventBus.Subscribe<SystemPerformanceEvent>(OnSystemPerformanceChanged);
        }

        /// <summary>
        /// Handles task progress changes;
        /// </summary>
        private void OnTaskProgressChanged(TaskProgressEvent e)
        {
            var task = GetTask(e.TaskId);
            if (task != null)
            {
                task.UpdateProgress(e.Progress);
            }
        }

        /// <summary>
        /// Handles task status changes;
        /// </summary>
        private void OnTaskStatusChanged(TaskStatusEvent e)
        {
            var task = GetTask(e.TaskId);
            if (task != null)
            {
                task.Status = e.NewStatus;
                if (e.NewStatus == TaskStatus.Completed)
                {
                    task.Complete();
                }
                else if (e.NewStatus == TaskStatus.Failed)
                {
                    task.Fail(e.ErrorMessage);
                }
            }
        }

        /// <summary>
        /// Handles system performance changes;
        /// </summary>
        private void OnSystemPerformanceChanged(SystemPerformanceEvent e)
        {
            // Adjust visualization based on system performance;
            if (e.CpuUsage > 80)
            {
                Options.AnimationSpeed = AnimationSpeed.Slow;
            }
            else if (e.CpuUsage > 60)
            {
                Options.AnimationSpeed = AnimationSpeed.Normal;
            }
            else;
            {
                Options.AnimationSpeed = AnimationSpeed.Fast;
            }
        }

        /// <summary>
        /// Calculates average completion time;
        /// </summary>
        private TimeSpan? CalculateAverageCompletionTime(List<TaskItem> tasks)
        {
            var completedTasks = tasks.Where(t => t.Status == TaskStatus.Completed && t.ActualDuration.HasValue).ToList();
            if (completedTasks.Count == 0) return null;

            var totalTicks = completedTasks.Sum(t => t.ActualDuration.Value.Ticks);
            return TimeSpan.FromTicks(totalTicks / completedTasks.Count);
        }

        /// <summary>
        /// Calculates busiest time;
        /// </summary>
        private DateTime? CalculateBusiestTime(List<TaskItem> tasks)
        {
            if (tasks.Count == 0) return null;

            var hourGroups = tasks.GroupBy(t => t.StartTime.Hour)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            return hourGroups?.First().StartTime.Date.AddHours(hourGroups.Key);
        }

        /// <summary>
        /// Calculates most common task types;
        /// </summary>
        private Dictionary<string, int> CalculateMostCommonTaskTypes(List<TaskItem> tasks)
        {
            return tasks.GroupBy(t => t.GetType().Name)
                .ToDictionary(g => g.Key, g => g.Count())
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        // Export/Import methods (simplified for brevity)
        private async Task<string> ExportToJson(List<TaskItem> tasks, string filePath)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(tasks, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            if (!string.IsNullOrEmpty(filePath))
            {
                await System.IO.File.WriteAllTextAsync(filePath, json);
            }

            return json;
        }

        private async Task<string> ExportToXml(List<TaskItem> tasks, string filePath)
        {
            // XML serialization implementation;
            await Task.CompletedTask;
            return "<Tasks></Tasks>"; // Simplified;
        }

        private async Task<string> ExportToCsv(List<TaskItem> tasks, string filePath)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Id,Name,Status,Progress,StartTime,EndTime,Priority");

            foreach (var task in tasks)
            {
                csv.AppendLine($"{task.Id},{task.Name},{task.Status},{task.Progress},{task.StartTime},{task.EndTime},{task.Priority}");
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                await System.IO.File.WriteAllTextAsync(filePath, csv.ToString());
            }

            return csv.ToString();
        }

        private async Task<string> ExportToHtml(List<TaskItem> tasks, string filePath)
        {
            var html = new System.Text.StringBuilder();
            html.AppendLine("<html><head><style>table { border-collapse: collapse; } td, th { border: 1px solid black; padding: 5px; }</style></head><body>");
            html.AppendLine("<h1>Task Export</h1>");
            html.AppendLine("<table>");
            html.AppendLine("<tr><th>ID</th><th>Name</th><th>Status</th><th>Progress</th><th>Start Time</th><th>End Time</th><th>Priority</th></tr>");

            foreach (var task in tasks)
            {
                html.AppendLine($"<tr><td>{task.Id}</td><td>{task.Name}</td><td>{task.Status}</td><td>{task.Progress}%</td><td>{task.StartTime}</td><td>{task.EndTime}</td><td>{task.Priority}</td></tr>");
            }

            html.AppendLine("</table></body></html>");

            if (!string.IsNullOrEmpty(filePath))
            {
                await System.IO.File.WriteAllTextAsync(filePath, html.ToString());
            }

            return html.ToString();
        }

        private async Task<List<TaskItem>> ImportFromJson(string filePath)
        {
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            return System.Text.Json.JsonSerializer.Deserialize<List<TaskItem>>(json) ?? new List<TaskItem>();
        }

        private async Task<List<TaskItem>> ImportFromXml(string filePath)
        {
            // XML deserialization implementation;
            await Task.CompletedTask;
            return new List<TaskItem>();
        }

        private async Task<List<TaskItem>> ImportFromCsv(string filePath)
        {
            var tasks = new List<TaskItem>();
            var lines = await System.IO.File.ReadAllLinesAsync(filePath);

            for (int i = 1; i < lines.Length; i++) // Skip header;
            {
                var parts = lines[i].Split(',');
                if (parts.Length >= 7)
                {
                    var task = new TaskItem(parts[0], parts[1])
                    {
                        Status = Enum.Parse<TaskStatus>(parts[2]),
                        Progress = double.Parse(parts[3]),
                        StartTime = DateTime.Parse(parts[4]),
                        Priority = Enum.Parse<PriorityLevel>(parts[6])
                    };

                    if (DateTime.TryParse(parts[5], out var endTime))
                    {
                        task.EndTime = endTime;
                    }

                    tasks.Add(task);
                }
            }

            return tasks;
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _cleanup?.Dispose();
            _refreshSubscription?.Dispose();
            _tasksCache?.Dispose();
            _iconCache?.Clear();

            _isDisposed = true;
            GC.SuppressFinalize(this);

            _logger.LogInformation("TaskVisualizer disposed");
        }
    }

    /// <summary>
    /// Export format enumeration;
    /// </summary>
    public enum ExportFormat;
    {
        JSON,
        XML,
        CSV,
        HTML;
    }

    /// <summary>
    /// Visualization report;
    /// </summary>
    public class VisualizationReport;
    {
        public DateTime GeneratedAt { get; set; }
        public int TotalTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int RunningTasks { get; set; }
        public int FailedTasks { get; set; }
        public double OverallProgress { get; set; }
        public Dictionary<TaskStatus, int> TasksByStatus { get; set; }
        public Dictionary<PriorityLevel, int> TasksByPriority { get; set; }
        public TimeSpan? AverageCompletionTime { get; set; }
        public DateTime? BusiestTime { get; set; }
        public Dictionary<string, int> MostCommonTaskTypes { get; set; }
    }

    // Event classes for task visualization;
    public class TaskAddedEvent : IEvent;
    {
        public TaskItem Task { get; }

        public TaskAddedEvent(TaskItem task)
        {
            Task = task;
        }
    }

    public class TaskRemovedEvent : IEvent;
    {
        public string TaskId { get; }

        public TaskRemovedEvent(string taskId)
        {
            TaskId = taskId;
        }
    }

    public class TaskUpdatedEvent : IEvent;
    {
        public TaskItem Task { get; }

        public TaskUpdatedEvent(TaskItem task)
        {
            Task = task;
        }
    }

    public class TaskProgressEvent : IEvent;
    {
        public string TaskId { get; }
        public double Progress { get; }

        public TaskProgressEvent(string taskId, double progress)
        {
            TaskId = taskId;
            Progress = progress;
        }
    }

    public class TaskStatusEvent : IEvent;
    {
        public string TaskId { get; }
        public TaskStatus NewStatus { get; }
        public string ErrorMessage { get; }

        public TaskStatusEvent(string taskId, TaskStatus newStatus, string errorMessage = null)
        {
            TaskId = taskId;
            NewStatus = newStatus;
            ErrorMessage = errorMessage;
        }
    }

    public class SystemPerformanceEvent : IEvent;
    {
        public double CpuUsage { get; }
        public double MemoryUsage { get; }
        public double DiskUsage { get; }

        public SystemPerformanceEvent(double cpuUsage, double memoryUsage, double diskUsage)
        {
            CpuUsage = cpuUsage;
            MemoryUsage = memoryUsage;
            DiskUsage = diskUsage;
        }
    }
}
