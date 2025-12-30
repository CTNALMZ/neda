using DynamicData;
using DynamicData.Binding;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Services.Messaging.EventBus;
using ReactiveUI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.VisualInterface.RealTimeFeedback;
{
    /// <summary>
    /// Status update priority levels;
    /// </summary>
    public enum StatusPriority;
    {
        Critical = 0,      // System-critical updates (errors, crashes)
        High = 1,         // Important updates (warnings, major state changes)
        Normal = 2,       // Regular status updates (progress, state changes)
        Low = 3,          // Informational updates (background tasks)
        Debug = 4         // Debug information;
    }

    /// <summary>
    /// Status update categories;
    /// </summary>
    public enum StatusCategory;
    {
        System = 0,       // System-level status;
        Application = 1,  // Application-level status;
        User = 2,        // User-related status;
        Task = 3,        // Task/progress status;
        Security = 4,    // Security-related status;
        Network = 5,     // Network status;
        Hardware = 6,    // Hardware status;
        Database = 7,    // Database status;
        AI = 8,          // AI/ML status;
        Plugin = 9,      // Plugin status;
        Custom = 10      // Custom category;
    }

    /// <summary>
    /// Represents a status update;
    /// </summary>
    public class StatusUpdate : ReactiveObject, IEquatable<StatusUpdate>
    {
        private string _id;
        private string _title;
        private string _message;
        private StatusPriority _priority;
        private StatusCategory _category;
        private DateTime _timestamp;
        private DateTime? _expiresAt;
        private string _source;
        private string _context;
        private bool _isPersistent;
        private bool _isAcknowledged;
        private Dictionary<string, object> _metadata;
        private object _data;

        /// <summary>
        /// Unique identifier for the status update;
        /// </summary>
        public string Id;
        {
            get => _id;
            set => this.RaiseAndSetIfChanged(ref _id, value);
        }

        /// <summary>
        /// Title of the status update;
        /// </summary>
        public string Title;
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        /// <summary>
        /// Detailed message;
        /// </summary>
        public string Message;
        {
            get => _message;
            set => this.RaiseAndSetIfChanged(ref _message, value);
        }

        /// <summary>
        /// Priority level;
        /// </summary>
        public StatusPriority Priority;
        {
            get => _priority;
            set => this.RaiseAndSetIfChanged(ref _priority, value);
        }

        /// <summary>
        /// Category of the status;
        /// </summary>
        public StatusCategory Category;
        {
            get => _category;
            set => this.RaiseAndSetIfChanged(ref _category, value);
        }

        /// <summary>
        /// When the status was created;
        /// </summary>
        public DateTime Timestamp;
        {
            get => _timestamp;
            set => this.RaiseAndSetIfChanged(ref _timestamp, value);
        }

        /// <summary>
        /// When the status expires (null = never expires)
        /// </summary>
        public DateTime? ExpiresAt;
        {
            get => _expiresAt;
            set => this.RaiseAndSetIfChanged(ref _expiresAt, value);
        }

        /// <summary>
        /// Source of the status update;
        /// </summary>
        public string Source;
        {
            get => _source;
            set => this.RaiseAndSetIfChanged(ref _source, value);
        }

        /// <summary>
        /// Context information (e.g., component, module)
        /// </summary>
        public string Context;
        {
            get => _context;
            set => this.RaiseAndSetIfChanged(ref _context, value);
        }

        /// <summary>
        /// Whether the status is persistent (survives restarts)
        /// </summary>
        public bool IsPersistent;
        {
            get => _isPersistent;
            set => this.RaiseAndSetIfChanged(ref _isPersistent, value);
        }

        /// <summary>
        /// Whether the status has been acknowledged by user;
        /// </summary>
        public bool IsAcknowledged;
        {
            get => _isAcknowledged;
            set => this.RaiseAndSetIfChanged(ref _isAcknowledged, value);
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
        /// Associated data object;
        /// </summary>
        public object Data;
        {
            get => _data;
            set => this.RaiseAndSetIfChanged(ref _data, value);
        }

        /// <summary>
        /// Whether the status has expired;
        /// </summary>
        public bool IsExpired => ExpiresAt.HasValue && DateTime.Now > ExpiresAt.Value;

        /// <summary>
        /// Age of the status update;
        /// </summary>
        public TimeSpan Age => DateTime.Now - Timestamp;

        /// <summary>
        /// Creates a new status update;
        /// </summary>
        public StatusUpdate(string id, string title, string message, StatusPriority priority, StatusCategory category, string source)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Priority = priority;
            Category = category;
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Timestamp = DateTime.Now;
            Context = string.Empty;
            IsPersistent = priority <= StatusPriority.High; // Critical/High priority updates are persistent by default;
            IsAcknowledged = false;
        }

        /// <summary>
        /// Creates a status update with auto-generated ID;
        /// </summary>
        public StatusUpdate(string title, string message, StatusPriority priority, StatusCategory category, string source)
            : this(Guid.NewGuid().ToString(), title, message, priority, category, source)
        {
        }

        /// <summary>
        /// Acknowledges the status update;
        /// </summary>
        public void Acknowledge(string acknowledgedBy = null, string note = null)
        {
            IsAcknowledged = true;
            if (!string.IsNullOrEmpty(acknowledgedBy))
            {
                Metadata["AcknowledgedBy"] = acknowledgedBy;
            }
            if (!string.IsNullOrEmpty(note))
            {
                Metadata["AcknowledgmentNote"] = note;
            }
        }

        /// <summary>
        /// Sets expiration time;
        /// </summary>
        public void SetExpiration(TimeSpan duration)
        {
            ExpiresAt = DateTime.Now + duration;
        }

        /// <summary>
        /// Adds metadata to the status update;
        /// </summary>
        public void AddMetadata(string key, object value)
        {
            Metadata[key] = value;
        }

        /// <summary>
        /// Gets metadata value;
        /// </summary>
        public T GetMetadata<T>(string key, T defaultValue = default)
        {
            return Metadata.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;
        }

        public bool Equals(StatusUpdate other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Id == other.Id;
        }

        public override bool Equals(object obj) => Equals(obj as StatusUpdate);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => $"[{Priority}] {Title}: {Message} ({Timestamp:HH:mm:ss})";
    }

    /// <summary>
    /// Status update event arguments;
    /// </summary>
    public class StatusUpdateEventArgs : EventArgs;
    {
        public StatusUpdate StatusUpdate { get; }
        public StatusUpdateAction Action { get; }

        public StatusUpdateEventArgs(StatusUpdate statusUpdate, StatusUpdateAction action)
        {
            StatusUpdate = statusUpdate ?? throw new ArgumentNullException(nameof(statusUpdate));
            Action = action;
        }
    }

    /// <summary>
    /// Status update actions;
    /// </summary>
    public enum StatusUpdateAction;
    {
        Added,
        Updated,
        Removed,
        Acknowledged,
        Expired;
    }

    /// <summary>
    /// Status source interface;
    /// </summary>
    public interface IStatusSource;
    {
        string SourceId { get; }
        string SourceName { get; }
        IObservable<StatusUpdate> StatusUpdates { get; }
        Task<StatusUpdate> GetCurrentStatusAsync();
    }

    /// <summary>
    /// Status aggregation configuration;
    /// </summary>
    public class StatusAggregationConfig;
    {
        public TimeSpan AggregationWindow { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxUpdatesPerWindow { get; set; } = 100;
        public bool EnableDeduplication { get; set; } = true;
        public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Status filtering configuration;
    /// </summary>
    public class StatusFilterConfig;
    {
        public List<StatusPriority> AllowedPriorities { get; set; } = new List<StatusPriority>();
        public List<StatusCategory> AllowedCategories { get; set; } = new List<StatusCategory>();
        public List<string> AllowedSources { get; set; } = new List<string>();
        public List<string> AllowedContexts { get; set; } = new List<string>();
        public bool IncludeAcknowledged { get; set; } = false;
        public bool IncludeExpired { get; set; } = false;
        public TimeSpan? MaxAge { get; set; }
        public string SearchText { get; set; }

        public bool IsMatch(StatusUpdate update)
        {
            if (update == null) return false;

            // Check priority;
            if (AllowedPriorities.Any() && !AllowedPriorities.Contains(update.Priority))
                return false;

            // Check category;
            if (AllowedCategories.Any() && !AllowedCategories.Contains(update.Category))
                return false;

            // Check source;
            if (AllowedSources.Any() && !AllowedSources.Contains(update.Source))
                return false;

            // Check context;
            if (AllowedContexts.Any() && !AllowedContexts.Contains(update.Context))
                return false;

            // Check acknowledged;
            if (!IncludeAcknowledged && update.IsAcknowledged)
                return false;

            // Check expired;
            if (!IncludeExpired && update.IsExpired)
                return false;

            // Check age;
            if (MaxAge.HasValue && update.Age > MaxAge.Value)
                return false;

            // Check search text;
            if (!string.IsNullOrEmpty(SearchText))
            {
                var searchLower = SearchText.ToLowerInvariant();
                if (!update.Title.ToLowerInvariant().Contains(searchLower) &&
                    !update.Message.ToLowerInvariant().Contains(searchLower) &&
                    !update.Context.ToLowerInvariant().Contains(searchLower))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Main status updater class for real-time status management and synchronization;
    /// </summary>
    public class StatusUpdater : ReactiveObject, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly CompositeDisposable _disposables;
        private readonly ConcurrentDictionary<string, StatusUpdate> _statusUpdates;
        private readonly SourceCache<StatusUpdate, string> _statusCache;
        private readonly ReadOnlyObservableCollection<StatusUpdate> _visibleStatusUpdates;
        private readonly Subject<StatusUpdateEventArgs> _statusUpdateSubject;
        private readonly Dictionary<string, IStatusSource> _statusSources;
        private readonly ConcurrentQueue<StatusUpdate> _updateQueue;
        private readonly object _syncLock = new object();
        private StatusAggregationConfig _aggregationConfig;
        private StatusFilterConfig _filterConfig;
        private int _maxHistorySize;
        private bool _isRunning;
        private bool _isPaused;
        private CancellationTokenSource _processingCts;
        private Task _processingTask;
        private DateTime _lastCleanupTime;
        private readonly Dictionary<string, DateTime> _lastUpdateTimes;
        private readonly Dictionary<string, int> _updateCounts;
        private bool _isDisposed;

        /// <summary>
        /// Status aggregation configuration;
        /// </summary>
        public StatusAggregationConfig AggregationConfig;
        {
            get => _aggregationConfig;
            set => this.RaiseAndSetIfChanged(ref _aggregationConfig, value);
        }

        /// <summary>
        /// Status filtering configuration;
        /// </summary>
        public StatusFilterConfig FilterConfig;
        {
            get => _filterConfig;
            set;
            {
                this.RaiseAndSetIfChanged(ref _filterConfig, value);
                ApplyFilter();
            }
        }

        /// <summary>
        /// Maximum number of status updates to keep in history;
        /// </summary>
        public int MaxHistorySize;
        {
            get => _maxHistorySize;
            set;
            {
                this.RaiseAndSetIfChanged(ref _maxHistorySize, value);
                TrimHistory();
            }
        }

        /// <summary>
        /// Whether the status updater is running;
        /// </summary>
        public bool IsRunning;
        {
            get => _isRunning;
            private set => this.RaiseAndSetIfChanged(ref _isRunning, value);
        }

        /// <summary>
        /// Whether the status updater is paused;
        /// </summary>
        public bool IsPaused;
        {
            get => _isPaused;
            private set => this.RaiseAndSetIfChanged(ref _isPaused, value);
        }

        /// <summary>
        /// Observable collection of visible status updates;
        /// </summary>
        public ReadOnlyObservableCollection<StatusUpdate> VisibleStatusUpdates => _visibleStatusUpdates;

        /// <summary>
        /// Total number of status updates;
        /// </summary>
        public int TotalStatusCount => _statusCache.Count;

        /// <summary>
        /// Number of unacknowledged status updates;
        /// </summary>
        public int UnacknowledgedCount => _statusCache.Items.Count(s => !s.IsAcknowledged && !s.IsExpired);

        /// <summary>
        /// Number of critical status updates;
        /// </summary>
        public int CriticalCount => _statusCache.Items.Count(s => s.Priority == StatusPriority.Critical && !s.IsAcknowledged && !s.IsExpired);

        /// <summary>
        /// Number of high priority status updates;
        /// </summary>
        public int HighPriorityCount => _statusCache.Items.Count(s => s.Priority == StatusPriority.High && !s.IsAcknowledged && !s.IsExpired);

        /// <summary>
        /// Observable stream of status update events;
        /// </summary>
        public IObservable<StatusUpdateEventArgs> StatusUpdateStream => _statusUpdateSubject.AsObservable();

        /// <summary>
        /// Event raised when a status update is added;
        /// </summary>
        public event EventHandler<StatusUpdateEventArgs> StatusAdded;

        /// <summary>
        /// Event raised when a status update is updated;
        /// </summary>
        public event EventHandler<StatusUpdateEventArgs> StatusUpdated;

        /// <summary>
        /// Event raised when a status update is removed;
        /// </summary>
        public event EventHandler<StatusUpdateEventArgs> StatusRemoved;

        /// <summary>
        /// Event raised when a status update is acknowledged;
        /// </summary>
        public event EventHandler<StatusUpdateEventArgs> StatusAcknowledged;

        /// <summary>
        /// Creates a new StatusUpdater instance;
        /// </summary>
        public StatusUpdater(ILogger logger, IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _disposables = new CompositeDisposable();
            _statusUpdates = new ConcurrentDictionary<string, StatusUpdate>();
            _statusCache = new SourceCache<StatusUpdate, string>(s => s.Id);
            _statusUpdateSubject = new Subject<StatusUpdateEventArgs>();
            _statusSources = new Dictionary<string, IStatusSource>();
            _updateQueue = new ConcurrentQueue<StatusUpdate>();
            _lastUpdateTimes = new Dictionary<string, DateTime>();
            _updateCounts = new Dictionary<string, int>();

            InitializeConfig();
            SetupFiltering();
            SubscribeToEvents();

            _logger.LogInformation("StatusUpdater initialized successfully");
        }

        /// <summary>
        /// Starts the status updater;
        /// </summary>
        public async Task StartAsync()
        {
            if (IsRunning)
            {
                _logger.LogWarning("StatusUpdater is already running");
                return;
            }

            try
            {
                _processingCts = new CancellationTokenSource();
                IsRunning = true;
                IsPaused = false;

                // Start processing task;
                _processingTask = Task.Run(async () => await ProcessUpdatesAsync(_processingCts.Token));

                // Start all status sources;
                await StartStatusSourcesAsync();

                _logger.LogInformation("StatusUpdater started");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error starting StatusUpdater: {ex.Message}", ex);
                IsRunning = false;
                throw;
            }
        }

        /// <summary>
        /// Stops the status updater;
        /// </summary>
        public async Task StopAsync()
        {
            if (!IsRunning)
            {
                _logger.LogWarning("StatusUpdater is not running");
                return;
            }

            try
            {
                // Stop processing;
                _processingCts?.Cancel();
                if (_processingTask != null)
                {
                    await _processingTask;
                }

                // Stop all status sources;
                await StopStatusSourcesAsync();

                // Cleanup;
                _processingCts?.Dispose();
                _processingCts = null;
                _processingTask = null;

                IsRunning = false;
                IsPaused = false;

                _logger.LogInformation("StatusUpdater stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping StatusUpdater: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Pauses status updates;
        /// </summary>
        public void Pause()
        {
            if (!IsRunning || IsPaused)
                return;

            IsPaused = true;
            _logger.LogInformation("StatusUpdater paused");
        }

        /// <summary>
        /// Resumes status updates;
        /// </summary>
        public void Resume()
        {
            if (!IsRunning || !IsPaused)
                return;

            IsPaused = false;
            _logger.LogInformation("StatusUpdater resumed");
        }

        /// <summary>
        /// Registers a status source;
        /// </summary>
        public void RegisterStatusSource(IStatusSource source)
        {
            try
            {
                if (source == null) throw new ArgumentNullException(nameof(source));
                if (_statusSources.ContainsKey(source.SourceId))
                    throw new InvalidOperationException($"Status source '{source.SourceId}' is already registered");

                _statusSources[source.SourceId] = source;

                // Subscribe to status updates from the source;
                var subscription = source.StatusUpdates.Subscribe(update =>
                {
                    if (!IsPaused)
                    {
                        EnqueueUpdate(update);
                    }
                });

                _disposables.Add(subscription);

                _logger.LogInformation($"Status source registered: {source.SourceName} ({source.SourceId})");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error registering status source: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Unregisters a status source;
        /// </summary>
        public void UnregisterStatusSource(string sourceId)
        {
            try
            {
                if (_statusSources.TryGetValue(sourceId, out var source))
                {
                    _statusSources.Remove(sourceId);
                    _logger.LogInformation($"Status source unregistered: {source.SourceName} ({sourceId})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error unregistering status source: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Adds a status update;
        /// </summary>
        public void AddStatusUpdate(StatusUpdate statusUpdate)
        {
            if (statusUpdate == null) throw new ArgumentNullException(nameof(statusUpdate));

            try
            {
                // Apply deduplication if enabled;
                if (AggregationConfig.EnableDeduplication)
                {
                    var deduplicationKey = GetDeduplicationKey(statusUpdate);
                    if (ShouldDeduplicate(deduplicationKey, statusUpdate))
                    {
                        _logger.LogDebug($"Duplicate status update suppressed: {statusUpdate.Title}");
                        return;
                    }
                }

                // Check rate limiting;
                if (IsRateLimited(statusUpdate.Source))
                {
                    _logger.LogWarning($"Status update rate limited for source: {statusUpdate.Source}");
                    return;
                }

                // Add to cache and dictionary;
                _statusUpdates[statusUpdate.Id] = statusUpdate;
                _statusCache.AddOrUpdate(statusUpdate);

                // Update statistics;
                UpdateSourceStatistics(statusUpdate.Source);

                // Publish event;
                var args = new StatusUpdateEventArgs(statusUpdate, StatusUpdateAction.Added);
                _statusUpdateSubject.OnNext(args);
                StatusAdded?.Invoke(this, args);

                // Publish to event bus;
                _eventBus.Publish(new StatusUpdateEvent(statusUpdate, StatusUpdateAction.Added));

                _logger.LogDebug($"Status update added: {statusUpdate.Title}");

                // Trim history if needed;
                if (_statusCache.Count > MaxHistorySize)
                {
                    TrimHistory();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding status update: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Updates an existing status update;
        /// </summary>
        public void UpdateStatusUpdate(StatusUpdate statusUpdate)
        {
            if (statusUpdate == null) throw new ArgumentNullException(nameof(statusUpdate));

            try
            {
                if (!_statusUpdates.ContainsKey(statusUpdate.Id))
                {
                    _logger.LogWarning($"Cannot update non-existent status update: {statusUpdate.Id}");
                    return;
                }

                _statusUpdates[statusUpdate.Id] = statusUpdate;
                _statusCache.AddOrUpdate(statusUpdate);

                // Publish event;
                var args = new StatusUpdateEventArgs(statusUpdate, StatusUpdateAction.Updated);
                _statusUpdateSubject.OnNext(args);
                StatusUpdated?.Invoke(this, args);

                // Publish to event bus;
                _eventBus.Publish(new StatusUpdateEvent(statusUpdate, StatusUpdateAction.Updated));

                _logger.LogDebug($"Status update updated: {statusUpdate.Title}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating status update: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Removes a status update;
        /// </summary>
        public bool RemoveStatusUpdate(string statusId)
        {
            try
            {
                if (!_statusUpdates.TryRemove(statusId, out var statusUpdate))
                    return false;

                _statusCache.Remove(statusId);

                // Publish event;
                var args = new StatusUpdateEventArgs(statusUpdate, StatusUpdateAction.Removed);
                _statusUpdateSubject.OnNext(args);
                StatusRemoved?.Invoke(this, args);

                // Publish to event bus;
                _eventBus.Publish(new StatusUpdateEvent(statusUpdate, StatusUpdateAction.Removed));

                _logger.LogDebug($"Status update removed: {statusUpdate.Title}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing status update: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Acknowledges a status update;
        /// </summary>
        public bool AcknowledgeStatusUpdate(string statusId, string acknowledgedBy = null, string note = null)
        {
            try
            {
                if (!_statusUpdates.TryGetValue(statusId, out var statusUpdate))
                    return false;

                statusUpdate.Acknowledge(acknowledgedBy, note);
                _statusCache.AddOrUpdate(statusUpdate);

                // Publish event;
                var args = new StatusUpdateEventArgs(statusUpdate, StatusUpdateAction.Acknowledged);
                _statusUpdateSubject.OnNext(args);
                StatusAcknowledged?.Invoke(this, args);

                // Publish to event bus;
                _eventBus.Publish(new StatusUpdateEvent(statusUpdate, StatusUpdateAction.Acknowledged));

                _logger.LogInformation($"Status update acknowledged: {statusUpdate.Title}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error acknowledging status update: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Acknowledges all status updates;
        /// </summary>
        public int AcknowledgeAll(string acknowledgedBy = null)
        {
            var count = 0;
            foreach (var statusUpdate in _statusCache.Items.Where(s => !s.IsAcknowledged && !s.IsExpired))
            {
                if (AcknowledgeStatusUpdate(statusUpdate.Id, acknowledgedBy))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Gets a status update by ID;
        /// </summary>
        public StatusUpdate GetStatusUpdate(string statusId)
        {
            return _statusUpdates.TryGetValue(statusId, out var statusUpdate) ? statusUpdate : null;
        }

        /// <summary>
        /// Gets all status updates matching the filter;
        /// </summary>
        public IEnumerable<StatusUpdate> GetStatusUpdates(StatusFilterConfig filterConfig = null)
        {
            var filter = filterConfig ?? FilterConfig;
            return _statusCache.Items.Where(filter.IsMatch);
        }

        /// <summary>
        /// Gets status updates by category;
        /// </summary>
        public IEnumerable<StatusUpdate> GetStatusUpdatesByCategory(StatusCategory category)
        {
            return _statusCache.Items.Where(s => s.Category == category);
        }

        /// <summary>
        /// Gets status updates by priority;
        /// </summary>
        public IEnumerable<StatusUpdate> GetStatusUpdatesByPriority(StatusPriority priority)
        {
            return _statusCache.Items.Where(s => s.Priority == priority);
        }

        /// <summary>
        /// Gets status updates by source;
        /// </summary>
        public IEnumerable<StatusUpdate> GetStatusUpdatesBySource(string source)
        {
            return _statusCache.Items.Where(s => s.Source == source);
        }

        /// <summary>
        /// Gets the latest status update for each source;
        /// </summary>
        public Dictionary<string, StatusUpdate> GetLatestStatusBySource()
        {
            var latestBySource = new Dictionary<string, StatusUpdate>();
            foreach (var statusUpdate in _statusCache.Items.OrderByDescending(s => s.Timestamp))
            {
                if (!latestBySource.ContainsKey(statusUpdate.Source))
                {
                    latestBySource[statusUpdate.Source] = statusUpdate;
                }
            }
            return latestBySource;
        }

        /// <summary>
        /// Gets status statistics;
        /// </summary>
        public StatusStatistics GetStatistics()
        {
            var updates = _statusCache.Items.ToList();
            return new StatusStatistics;
            {
                TotalCount = updates.Count,
                UnacknowledgedCount = updates.Count(s => !s.IsAcknowledged && !s.IsExpired),
                CriticalCount = updates.Count(s => s.Priority == StatusPriority.Critical && !s.IsAcknowledged && !s.IsExpired),
                HighPriorityCount = updates.Count(s => s.Priority == StatusPriority.High && !s.IsAcknowledged && !s.IsExpired),
                ByCategory = updates.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.Count()),
                ByPriority = updates.GroupBy(s => s.Priority).ToDictionary(g => g.Key, g => g.Count()),
                BySource = updates.GroupBy(s => s.Source).ToDictionary(g => g.Key, g => g.Count()),
                OldestUpdate = updates.OrderBy(s => s.Timestamp).FirstOrDefault(),
                NewestUpdate = updates.OrderByDescending(s => s.Timestamp).FirstOrDefault(),
                AverageAge = updates.Any() ? TimeSpan.FromSeconds(updates.Average(s => s.Age.TotalSeconds)) : TimeSpan.Zero;
            };
        }

        /// <summary>
        /// Cleans up expired status updates;
        /// </summary>
        public int CleanupExpired()
        {
            var expiredUpdates = _statusCache.Items.Where(s => s.IsExpired).ToList();
            foreach (var expired in expiredUpdates)
            {
                RemoveStatusUpdate(expired.Id);
            }
            return expiredUpdates.Count;
        }

        /// <summary>
        /// Clears all status updates;
        /// </summary>
        public void ClearAll()
        {
            try
            {
                var updates = _statusCache.Items.ToList();
                foreach (var update in updates)
                {
                    RemoveStatusUpdate(update.Id);
                }
                _logger.LogInformation("All status updates cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error clearing status updates: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Exports status updates to file;
        /// </summary>
        public async Task ExportStatusUpdatesAsync(ExportFormat format, string filePath, StatusFilterConfig filterConfig = null)
        {
            try
            {
                var updates = GetStatusUpdates(filterConfig).ToList();
                var exportData = new StatusExportData;
                {
                    ExportTime = DateTime.Now,
                    Count = updates.Count,
                    Updates = updates,
                    Statistics = GetStatistics()
                };

                switch (format)
                {
                    case ExportFormat.JSON:
                        await ExportToJsonAsync(exportData, filePath);
                        break;
                    case ExportFormat.CSV:
                        await ExportToCsvAsync(exportData, filePath);
                        break;
                    case ExportFormat.XML:
                        await ExportToXmlAsync(exportData, filePath);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(format));
                }

                _logger.LogInformation($"Exported {updates.Count} status updates to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error exporting status updates: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Imports status updates from file;
        /// </summary>
        public async Task ImportStatusUpdatesAsync(ExportFormat format, string filePath)
        {
            try
            {
                if (!System.IO.File.Exists(filePath))
                    throw new FileNotFoundException($"File not found: {filePath}");

                StatusExportData importData = format switch;
                {
                    ExportFormat.JSON => await ImportFromJsonAsync(filePath),
                    ExportFormat.CSV => await ImportFromCsvAsync(filePath),
                    ExportFormat.XML => await ImportFromXmlAsync(filePath),
                    _ => throw new ArgumentOutOfRangeException(nameof(format))
                };

                foreach (var update in importData.Updates)
                {
                    AddStatusUpdate(update);
                }

                _logger.LogInformation($"Imported {importData.Count} status updates from {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error importing status updates: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Sets up a status subscription for real-time updates;
        /// </summary>
        public IDisposable SubscribeToStatusUpdates(Action<StatusUpdateEventArgs> onNext, Action<Exception> onError = null, Action onCompleted = null)
        {
            return StatusUpdateStream.Subscribe(onNext, onError, onCompleted);
        }

        /// <summary>
        /// Sets up a filtered status subscription;
        /// </summary>
        public IDisposable SubscribeToFilteredStatusUpdates(StatusFilterConfig filterConfig, Action<StatusUpdateEventArgs> onNext)
        {
            return StatusUpdateStream;
                .Where(args => filterConfig.IsMatch(args.StatusUpdate))
                .Subscribe(onNext);
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            try
            {
                if (IsRunning)
                {
                    StopAsync().Wait(5000);
                }

                _disposables?.Dispose();
                _processingCts?.Dispose();
                _statusUpdateSubject?.OnCompleted();
                _statusUpdateSubject?.Dispose();
                _statusCache?.Dispose();

                _isDisposed = true;
                GC.SuppressFinalize(this);

                _logger.LogInformation("StatusUpdater disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error disposing StatusUpdater: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Initializes configuration;
        /// </summary>
        private void InitializeConfig()
        {
            AggregationConfig = new StatusAggregationConfig;
            {
                AggregationWindow = TimeSpan.FromSeconds(5),
                MaxUpdatesPerWindow = 100,
                EnableDeduplication = true,
                DeduplicationWindow = TimeSpan.FromSeconds(30)
            };

            FilterConfig = new StatusFilterConfig;
            {
                AllowedPriorities = Enum.GetValues(typeof(StatusPriority)).Cast<StatusPriority>().ToList(),
                AllowedCategories = Enum.GetValues(typeof(StatusCategory)).Cast<StatusCategory>().ToList(),
                IncludeAcknowledged = false,
                IncludeExpired = false;
            };

            MaxHistorySize = 10000; // Keep last 10,000 status updates;
            _lastCleanupTime = DateTime.Now;
        }

        /// <summary>
        /// Sets up filtering for status updates;
        /// </summary>
        private void SetupFiltering()
        {
            _statusCache.Connect()
                .Filter(update => FilterConfig.IsMatch(update))
                .Sort(SortExpressionComparer<StatusUpdate>
                    .Descending(s => s.Priority)
                    .ThenByDescending(s => s.Timestamp))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _visibleStatusUpdates)
                .Subscribe()
                .DisposeWith(_disposables);
        }

        /// <summary>
        /// Subscribes to system events;
        /// </summary>
        private void SubscribeToEvents()
        {
            // Subscribe to system events from event bus;
            _eventBus.Subscribe<SystemStatusEvent>(OnSystemStatusEvent);
            _eventBus.Subscribe<TaskStatusEvent>(OnTaskStatusEvent);
            _eventBus.Subscribe<ErrorEvent>(OnErrorEvent);
            _eventBus.Subscribe<PerformanceEvent>(OnPerformanceEvent);
        }

        /// <summary>
        /// Starts all registered status sources;
        /// </summary>
        private async Task StartStatusSourcesAsync()
        {
            foreach (var source in _statusSources.Values)
            {
                try
                {
                    // Get current status from source;
                    var currentStatus = await source.GetCurrentStatusAsync();
                    if (currentStatus != null)
                    {
                        AddStatusUpdate(currentStatus);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error starting status source {source.SourceId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Stops all registered status sources;
        /// </summary>
        private async Task StopStatusSourcesAsync()
        {
            // No specific stop action needed for sources in this implementation;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Main processing loop for status updates;
        /// </summary>
        private async Task ProcessUpdatesAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Status update processing started");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (IsPaused)
                    {
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    // Process queued updates;
                    ProcessQueuedUpdates();

                    // Cleanup expired updates periodically;
                    if (DateTime.Now - _lastCleanupTime > TimeSpan.FromMinutes(5))
                    {
                        CleanupExpired();
                        _lastCleanupTime = DateTime.Now;
                    }

                    // Apply rate limiting window;
                    CleanupRateLimitingData();

                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Status update processing cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in status update processing: {ex.Message}", ex);
            }
            finally
            {
                _logger.LogInformation("Status update processing ended");
            }
        }

        /// <summary>
        /// Processes queued status updates;
        /// </summary>
        private void ProcessQueuedUpdates()
        {
            var processedCount = 0;
            var batch = new List<StatusUpdate>();

            // Dequeue updates in batch;
            while (_updateQueue.TryDequeue(out var update) && processedCount < AggregationConfig.MaxUpdatesPerWindow)
            {
                batch.Add(update);
                processedCount++;
            }

            // Process batch;
            if (batch.Any())
            {
                foreach (var update in batch.OrderBy(u => u.Priority).ThenBy(u => u.Timestamp))
                {
                    AddStatusUpdate(update);
                }
            }
        }

        /// <summary>
        /// Enqueues a status update for processing;
        /// </summary>
        private void EnqueueUpdate(StatusUpdate update)
        {
            _updateQueue.Enqueue(update);
        }

        /// <summary>
        /// Gets deduplication key for a status update;
        /// </summary>
        private string GetDeduplicationKey(StatusUpdate update)
        {
            return $"{update.Source}:{update.Title}:{update.Message}";
        }

        /// <summary>
        /// Checks if a status update should be deduplicated;
        /// </summary>
        private bool ShouldDeduplicate(string deduplicationKey, StatusUpdate update)
        {
            // Check if similar update was recently processed;
            var recentUpdate = _statusCache.Items;
                .Where(s => s.Source == update.Source && s.Title == update.Title && s.Message == update.Message)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefault();

            if (recentUpdate == null) return false;

            // Check if within deduplication window;
            return DateTime.Now - recentUpdate.Timestamp < AggregationConfig.DeduplicationWindow;
        }

        /// <summary>
        /// Checks if a source is rate limited;
        /// </summary>
        private bool IsRateLimited(string source)
        {
            lock (_syncLock)
            {
                if (!_lastUpdateTimes.TryGetValue(source, out var lastUpdateTime) ||
                    !_updateCounts.TryGetValue(source, out var updateCount))
                {
                    return false;
                }

                // Reset count if outside aggregation window;
                if (DateTime.Now - lastUpdateTime > AggregationConfig.AggregationWindow)
                {
                    _updateCounts[source] = 0;
                    return false;
                }

                // Check if exceeds max updates per window;
                return updateCount >= AggregationConfig.MaxUpdatesPerWindow;
            }
        }

        /// <summary>
        /// Updates source statistics for rate limiting;
        /// </summary>
        private void UpdateSourceStatistics(string source)
        {
            lock (_syncLock)
            {
                _lastUpdateTimes[source] = DateTime.Now;
                _updateCounts[source] = _updateCounts.TryGetValue(source, out var count) ? count + 1 : 1;
            }
        }

        /// <summary>
        /// Cleans up rate limiting data;
        /// </summary>
        private void CleanupRateLimitingData()
        {
            lock (_syncLock)
            {
                var cutoffTime = DateTime.Now - AggregationConfig.AggregationWindow * 2;
                var oldSources = _lastUpdateTimes.Where(kv => kv.Value < cutoffTime).Select(kv => kv.Key).ToList();

                foreach (var source in oldSources)
                {
                    _lastUpdateTimes.Remove(source);
                    _updateCounts.Remove(source);
                }
            }
        }

        /// <summary>
        /// Applies current filter to status cache;
        /// </summary>
        private void ApplyFilter()
        {
            _statusCache.Refresh();
        }

        /// <summary>
        /// Trims status history to max size;
        /// </summary>
        private void TrimHistory()
        {
            if (_statusCache.Count <= MaxHistorySize) return;

            var toRemove = _statusCache.Items;
                .OrderBy(s => s.Priority)  // Remove low priority first;
                .ThenBy(s => s.Timestamp)  // Remove oldest first;
                .Take(_statusCache.Count - MaxHistorySize)
                .ToList();

            foreach (var update in toRemove)
            {
                RemoveStatusUpdate(update.Id);
            }

            _logger.LogDebug($"Trimmed {toRemove.Count} status updates from history");
        }

        // Event handlers for system events;
        private void OnSystemStatusEvent(SystemStatusEvent e)
        {
            var statusUpdate = new StatusUpdate(
                $"SYSTEM_{e.Status}",
                e.Message,
                GetPriorityForSystemStatus(e.Status),
                StatusCategory.System,
                e.Source)
            {
                Context = e.Component,
                Data = e.Data;
            };

            AddStatusUpdate(statusUpdate);
        }

        private void OnTaskStatusEvent(TaskStatusEvent e)
        {
            var statusUpdate = new StatusUpdate(
                $"TASK_{e.TaskId}",
                $"{e.TaskName}: {e.Status}",
                GetPriorityForTaskStatus(e.Status),
                StatusCategory.Task,
                "TaskManager")
            {
                Context = e.TaskType,
                Data = new { e.TaskId, e.Progress, e.Duration }
            };

            AddStatusUpdate(statusUpdate);
        }

        private void OnErrorEvent(ErrorEvent e)
        {
            var priority = e.Exception != null ? StatusPriority.Critical : StatusPriority.High;
            var statusUpdate = new StatusUpdate(
                $"ERROR_{e.ErrorCode}",
                e.Message,
                priority,
                StatusCategory.System,
                e.Source ?? "ErrorHandler")
            {
                Context = "Error",
                Data = e.Exception,
                IsPersistent = true;
            };

            AddStatusUpdate(statusUpdate);
        }

        private void OnPerformanceEvent(PerformanceEvent e)
        {
            var priority = e.Value > 90 ? StatusPriority.High :
                          e.Value > 70 ? StatusPriority.Normal :
                          StatusPriority.Low;

            var statusUpdate = new StatusUpdate(
                $"PERF_{e.MetricName}",
                $"{e.MetricName}: {e.Value:F1}%",
                priority,
                StatusCategory.System,
                "PerformanceMonitor")
            {
                Context = "Performance",
                Data = e,
                SetExpiration(TimeSpan.FromMinutes(5)) // Performance metrics expire after 5 minutes;
            };

            AddStatusUpdate(statusUpdate);
        }

        // Helper methods for priority mapping;
        private StatusPriority GetPriorityForSystemStatus(string status)
        {
            return status.ToUpperInvariant() switch;
            {
                "ERROR" => StatusPriority.Critical,
                "WARNING" => StatusPriority.High,
                "DEGRADED" => StatusPriority.Normal,
                _ => StatusPriority.Low;
            };
        }

        private StatusPriority GetPriorityForTaskStatus(string status)
        {
            return status.ToUpperInvariant() switch;
            {
                "FAILED" => StatusPriority.Critical,
                "CANCELLED" => StatusPriority.High,
                "RUNNING" => StatusPriority.Normal,
                _ => StatusPriority.Low;
            };
        }

        // Export/Import methods;
        private async Task ExportToJsonAsync(StatusExportData data, string filePath)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);
        }

        private async Task ExportToCsvAsync(StatusExportData data, string filePath)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Timestamp,Priority,Category,Source,Title,Message,Context,Acknowledged,Expired");

            foreach (var update in data.Updates)
            {
                csv.AppendLine($"{update.Timestamp:yyyy-MM-dd HH:mm:ss},{update.Priority},{update.Category},{update.Source}," +
                    $"\"{update.Title}\",\"{update.Message}\",{update.Context},{update.IsAcknowledged},{update.IsExpired}");
            }

            await System.IO.File.WriteAllTextAsync(filePath, csv.ToString());
        }

        private async Task ExportToXmlAsync(StatusExportData data, string filePath)
        {
            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine("<StatusExport>");
            xml.AppendLine($"<ExportTime>{data.ExportTime:yyyy-MM-dd HH:mm:ss}</ExportTime>");
            xml.AppendLine($"<Count>{data.Count}</Count>");
            xml.AppendLine("</StatusExport>");

            await System.IO.File.WriteAllTextAsync(filePath, xml.ToString());
        }

        private async Task<StatusExportData> ImportFromJsonAsync(string filePath)
        {
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            return System.Text.Json.JsonSerializer.Deserialize<StatusExportData>(json);
        }

        private async Task<StatusExportData> ImportFromCsvAsync(string filePath)
        {
            var updates = new List<StatusUpdate>();
            var lines = await System.IO.File.ReadAllLinesAsync(filePath);

            for (int i = 1; i < lines.Length; i++) // Skip header;
            {
                var parts = ParseCsvLine(lines[i]);
                if (parts.Length >= 9)
                {
                    var update = new StatusUpdate(
                        Guid.NewGuid().ToString(),
                        parts[4].Trim('"'),
                        parts[5].Trim('"'),
                        Enum.Parse<StatusPriority>(parts[1]),
                        Enum.Parse<StatusCategory>(parts[2]),
                        parts[3])
                    {
                        Timestamp = DateTime.Parse(parts[0]),
                        Context = parts[6],
                        IsAcknowledged = bool.Parse(parts[7]),
                        IsPersistent = bool.Parse(parts[8])
                    };

                    updates.Add(update);
                }
            }

            return new StatusExportData;
            {
                ExportTime = DateTime.Now,
                Count = updates.Count,
                Updates = updates;
            };
        }

        private async Task<StatusExportData> ImportFromXmlAsync(string filePath)
        {
            // Simplified XML import;
            await Task.CompletedTask;
            return new StatusExportData;
            {
                ExportTime = DateTime.Now,
                Count = 0,
                Updates = new List<StatusUpdate>()
            };
        }

        private string[] ParseCsvLine(string line)
        {
            var parts = new List<string>();
            var inQuotes = false;
            var currentPart = new System.Text.StringBuilder();

            foreach (var c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    parts.Add(currentPart.ToString());
                    currentPart.Clear();
                }
                else;
                {
                    currentPart.Append(c);
                }
            }

            parts.Add(currentPart.ToString());
            return parts.ToArray();
        }
    }

    /// <summary>
    /// Status statistics;
    /// </summary>
    public class StatusStatistics;
    {
        public int TotalCount { get; set; }
        public int UnacknowledgedCount { get; set; }
        public int CriticalCount { get; set; }
        public int HighPriorityCount { get; set; }
        public Dictionary<StatusCategory, int> ByCategory { get; set; }
        public Dictionary<StatusPriority, int> ByPriority { get; set; }
        public Dictionary<string, int> BySource { get; set; }
        public StatusUpdate OldestUpdate { get; set; }
        public StatusUpdate NewestUpdate { get; set; }
        public TimeSpan AverageAge { get; set; }
    }

    /// <summary>
    /// Status export data;
    /// </summary>
    public class StatusExportData;
    {
        public DateTime ExportTime { get; set; }
        public int Count { get; set; }
        public List<StatusUpdate> Updates { get; set; }
        public StatusStatistics Statistics { get; set; }
    }

    // Event bus events for status updates;
    public class StatusUpdateEvent : IEvent;
    {
        public StatusUpdate StatusUpdate { get; }
        public StatusUpdateAction Action { get; }

        public StatusUpdateEvent(StatusUpdate statusUpdate, StatusUpdateAction action)
        {
            StatusUpdate = statusUpdate ?? throw new ArgumentNullException(nameof(statusUpdate));
            Action = action;
        }
    }

    public class SystemStatusEvent : IEvent;
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public string Source { get; set; }
        public string Component { get; set; }
        public object Data { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TaskStatusEvent : IEvent;
    {
        public string TaskId { get; set; }
        public string TaskName { get; set; }
        public string Status { get; set; }
        public double Progress { get; set; }
        public TimeSpan Duration { get; set; }
        public string TaskType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ErrorEvent : IEvent;
    {
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public string Source { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PerformanceEvent : IEvent;
    {
        public string MetricName { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Export format enumeration;
    /// </summary>
    public enum ExportFormat;
    {
        JSON,
        CSV,
        XML;
    }
}
