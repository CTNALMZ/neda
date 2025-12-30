using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring;
using NEDA.ContentCreation.AssetPipeline.BatchProcessors.Models;
using NEDA.ContentCreation.AssetPipeline.BatchProcessors.Enums;

namespace NEDA.ContentCreation.AssetPipeline.BatchProcessors;
{
    /// <summary>
    /// Advanced distributed queue management system for batch processing;
    /// Supports priority queues, load balancing, failover, and real-time monitoring;
    /// </summary>
    public class QueueManager : IQueueManager, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IQueueStorage _queueStorage;
        private readonly ILoadBalancer _loadBalancer;
        private readonly IRetryPolicy _retryPolicy;

        private readonly ConcurrentDictionary<string, ProcessingQueue> _queues;
        private readonly ConcurrentDictionary<string, WorkerNode> _workerNodes;
        private readonly ConcurrentDictionary<string, ProcessingJob> _activeJobs;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens;

        private readonly PriorityQueue<QueueItem, int> _priorityQueue;
        private readonly QueueConfiguration _configuration;
        private readonly SemaphoreSlim _queueSemaphore;
        private readonly object _lockObject = new object();

        private Timer _healthCheckTimer;
        private Timer _metricsTimer;
        private Timer _cleanupTimer;
        private bool _isDisposed;

        public QueueStatus Status { get; private set; }
        public QueueStatistics Statistics { get; private set; }
        public int TotalQueues => _queues.Count;
        public int TotalWorkers => _workerNodes.Count;
        public int ActiveJobsCount => _activeJobs.Count;
        public long TotalProcessedItems { get; private set; }

        #endregion;

        #region Events;

        public event EventHandler<QueueItemAddedEventArgs> QueueItemAdded;
        public event EventHandler<QueueItemProcessedEventArgs> QueueItemProcessed;
        public event EventHandler<QueueItemFailedEventArgs> QueueItemFailed;
        public event EventHandler<WorkerStatusChangedEventArgs> WorkerStatusChanged;
        public event EventHandler<QueueStatusChangedEventArgs> QueueStatusChanged;
        public event EventHandler<QueueMetricsUpdatedEventArgs> QueueMetricsUpdated;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of QueueManager;
        /// </summary>
        public QueueManager(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IQueueStorage queueStorage = null,
            ILoadBalancer loadBalancer = null,
            IRetryPolicy retryPolicy = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _queueStorage = queueStorage ?? new InMemoryQueueStorage();
            _loadBalancer = loadBalancer ?? new RoundRobinLoadBalancer();
            _retryPolicy = retryPolicy ?? new ExponentialBackoffRetryPolicy();

            _queues = new ConcurrentDictionary<string, ProcessingQueue>();
            _workerNodes = new ConcurrentDictionary<string, WorkerNode>();
            _activeJobs = new ConcurrentDictionary<string, ProcessingJob>();
            _cancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

            _priorityQueue = new PriorityQueue<QueueItem, int>();
            _configuration = QueueConfiguration.Default;
            _queueSemaphore = new SemaphoreSlim(_configuration.MaxConcurrentOperations);

            Statistics = new QueueStatistics();
            Status = QueueStatus.Stopped;

            InitializeTimers();

            _logger.LogInformation("QueueManager initialized successfully");
        }

        private void InitializeTimers()
        {
            _healthCheckTimer = new Timer(PerformHealthChecks, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

            _metricsTimer = new Timer(CollectMetrics, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _cleanupTimer = new Timer(CleanupOldItems, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        #endregion;

        #region Queue Management;

        /// <summary>
        /// Creates a new processing queue with specified configuration;
        /// </summary>
        public async Task<ProcessingQueue> CreateQueueAsync(string queueName, QueueConfiguration config = null)
        {
            ValidateQueueName(queueName);

            if (_queues.ContainsKey(queueName))
                throw new QueueException($"Queue '{queueName}' already exists");

            config ??= _configuration;

            var queue = new ProcessingQueue;
            {
                Id = Guid.NewGuid().ToString(),
                Name = queueName,
                Configuration = config,
                Status = QueueStatus.Running,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Metrics = new QueueMetrics()
            };

            if (_queues.TryAdd(queueName, queue))
            {
                await _queueStorage.SaveQueueAsync(queue);
                _logger.LogInformation($"Queue '{queueName}' created successfully");
                return queue;
            }

            throw new QueueException($"Failed to create queue '{queueName}'");
        }

        /// <summary>
        /// Deletes a queue and all its items;
        /// </summary>
        public async Task<bool> DeleteQueueAsync(string queueName, bool force = false)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            if (!force && queue.Metrics.PendingItems > 0)
                throw new QueueException($"Cannot delete queue with {queue.Metrics.PendingItems} pending items");

            // Cancel all active jobs in this queue;
            var queueJobs = _activeJobs.Values.Where(j => j.QueueName == queueName);
            foreach (var job in queueJobs)
            {
                await CancelJobAsync(job.Id);
            }

            // Clear queue items;
            await _queueStorage.ClearQueueAsync(queueName);

            // Remove from memory;
            _queues.TryRemove(queueName, out _);

            _logger.LogInformation($"Queue '{queueName}' deleted successfully");
            return true;
        }

        /// <summary>
        /// Pauses processing for a specific queue;
        /// </summary>
        public async Task PauseQueueAsync(string queueName)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            queue.Status = QueueStatus.Paused;
            queue.UpdatedAt = DateTime.UtcNow;

            await _queueStorage.UpdateQueueAsync(queue);

            OnQueueStatusChanged(new QueueStatusChangedEventArgs(queueName, QueueStatus.Paused));
            _logger.LogInformation($"Queue '{queueName}' paused");
        }

        /// <summary>
        /// Resumes processing for a paused queue;
        /// </summary>
        public async Task ResumeQueueAsync(string queueName)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            queue.Status = QueueStatus.Running;
            queue.UpdatedAt = DateTime.UtcNow;

            await _queueStorage.UpdateQueueAsync(queue);

            OnQueueStatusChanged(new QueueStatusChangedEventArgs(queueName, QueueStatus.Running));
            _logger.LogInformation($"Queue '{queueName}' resumed");
        }

        /// <summary>
        /// Gets queue information and statistics;
        /// </summary>
        public async Task<ProcessingQueue> GetQueueInfoAsync(string queueName)
        {
            ValidateQueueName(queueName);

            if (_queues.TryGetValue(queueName, out var queue))
            {
                queue.Metrics = await CalculateQueueMetricsAsync(queueName);
                return queue;
            }

            throw new QueueException($"Queue '{queueName}' not found");
        }

        /// <summary>
        /// Lists all available queues;
        /// </summary>
        public async Task<IEnumerable<QueueInfo>> ListQueuesAsync(QueueFilter filter = null)
        {
            filter ??= new QueueFilter();

            var queues = _queues.Values;
                .Where(q => FilterQueue(q, filter))
                .Select(q => new QueueInfo;
                {
                    Id = q.Id,
                    Name = q.Name,
                    Status = q.Status,
                    CreatedAt = q.CreatedAt,
                    UpdatedAt = q.UpdatedAt,
                    ItemCount = q.Metrics.TotalItems,
                    PendingCount = q.Metrics.PendingItems,
                    ProcessingCount = q.Metrics.ProcessingItems,
                    FailedCount = q.Metrics.FailedItems;
                })
                .OrderByDescending(q => q.UpdatedAt)
                .ToList();

            await Task.CompletedTask;
            return queues;
        }

        private bool FilterQueue(ProcessingQueue queue, QueueFilter filter)
        {
            if (!string.IsNullOrEmpty(filter.NameContains) &&
                !queue.Name.Contains(filter.NameContains, StringComparison.OrdinalIgnoreCase))
                return false;

            if (filter.Status.HasValue && queue.Status != filter.Status.Value)
                return false;

            if (filter.MinItems.HasValue && queue.Metrics.TotalItems < filter.MinItems.Value)
                return false;

            if (filter.CreatedAfter.HasValue && queue.CreatedAt < filter.CreatedAfter.Value)
                return false;

            return true;
        }

        #endregion;

        #region Item Management;

        /// <summary>
        /// Adds an item to the specified queue;
        /// </summary>
        public async Task<QueueItem> EnqueueItemAsync(string queueName, QueueItem item, int priority = 0)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            if (queue.Status != QueueStatus.Running)
                throw new QueueException($"Queue '{queueName}' is not running");

            // Validate item;
            ValidateQueueItem(item);

            // Set item properties;
            item.Id = Guid.NewGuid().ToString();
            item.QueueName = queueName;
            item.Priority = priority;
            item.Status = QueueItemStatus.Pending;
            item.CreatedAt = DateTime.UtcNow;
            item.UpdatedAt = DateTime.UtcNow;
            item.Attempts = 0;
            item.MaxAttempts = queue.Configuration.MaxRetryAttempts;

            // Add to priority queue;
            _priorityQueue.Enqueue(item, -priority); // Negative for higher priority first;

            // Save to storage;
            await _queueStorage.AddItemAsync(queueName, item);

            // Update queue metrics;
            queue.Metrics.TotalItems++;
            queue.Metrics.PendingItems++;
            queue.UpdatedAt = DateTime.UtcNow;

            // Trigger processing if workers available;
            if (_workerNodes.Count > 0 && queue.Configuration.AutoStartProcessing)
            {
                _ = Task.Run(() => ProcessNextItemAsync(queueName));
            }

            OnQueueItemAdded(new QueueItemAddedEventArgs(queueName, item));
            _logger.LogDebug($"Item '{item.Id}' added to queue '{queueName}' with priority {priority}");

            return item;
        }

        /// <summary>
        /// Adds multiple items in batch;
        /// </summary>
        public async Task<BatchEnqueueResult> EnqueueBatchAsync(string queueName, IEnumerable<QueueItem> items, int priority = 0)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            var result = new BatchEnqueueResult;
            {
                QueueName = queueName,
                TotalItems = 0,
                SuccessfulItems = 0,
                FailedItems = 0,
                FailedReasons = new List<string>()
            };

            foreach (var item in items)
            {
                try
                {
                    await EnqueueItemAsync(queueName, item, priority);
                    result.SuccessfulItems++;
                }
                catch (Exception ex)
                {
                    result.FailedItems++;
                    result.FailedReasons.Add($"Item {item.Id}: {ex.Message}");
                    _logger.LogWarning(ex, $"Failed to enqueue item '{item.Id}'");
                }
                result.TotalItems++;
            }

            _logger.LogInformation($"Batch enqueued: {result.SuccessfulItems} successful, {result.FailedItems} failed");
            return result;
        }

        /// <summary>
        /// Gets the next item for processing;
        /// </summary>
        public async Task<QueueItem> DequeueItemAsync(string queueName, string workerId)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            if (!_workerNodes.ContainsKey(workerId))
                throw new QueueException($"Worker '{workerId}' not registered");

            await _queueSemaphore.WaitAsync();

            try
            {
                if (_priorityQueue.Count == 0)
                    return null;

                var item = _priorityQueue.Dequeue();

                if (item.QueueName != queueName)
                {
                    // Put back and try next;
                    _priorityQueue.Enqueue(item, -item.Priority);
                    return null;
                }

                // Update item status;
                item.Status = QueueItemStatus.Processing;
                item.StartedAt = DateTime.UtcNow;
                item.WorkerId = workerId;
                item.UpdatedAt = DateTime.UtcNow;
                item.Attempts++;

                // Update queue metrics;
                queue.Metrics.PendingItems--;
                queue.Metrics.ProcessingItems++;
                queue.UpdatedAt = DateTime.UtcNow;

                // Save changes;
                await _queueStorage.UpdateItemAsync(queueName, item);

                // Create processing job;
                var job = new ProcessingJob;
                {
                    Id = Guid.NewGuid().ToString(),
                    QueueName = queueName,
                    ItemId = item.Id,
                    WorkerId = workerId,
                    StartTime = DateTime.UtcNow,
                    Status = JobStatus.Running;
                };

                _activeJobs.TryAdd(job.Id, job);

                _logger.LogDebug($"Item '{item.Id}' dequeued by worker '{workerId}'");
                return item;
            }
            finally
            {
                _queueSemaphore.Release();
            }
        }

        /// <summary>
        /// Marks an item as completed;
        /// </summary>
        public async Task CompleteItemAsync(string queueName, string itemId, string workerId, object result = null)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            var item = await _queueStorage.GetItemAsync(queueName, itemId);
            if (item == null)
                throw new QueueException($"Item '{itemId}' not found in queue '{queueName}'");

            if (item.WorkerId != workerId)
                throw new QueueException($"Item '{itemId}' is not being processed by worker '{workerId}'");

            // Update item;
            item.Status = QueueItemStatus.Completed;
            item.CompletedAt = DateTime.UtcNow;
            item.Result = result;
            item.ErrorMessage = null;
            item.UpdatedAt = DateTime.UtcNow;

            // Update queue metrics;
            queue.Metrics.ProcessingItems--;
            queue.Metrics.CompletedItems++;
            queue.Metrics.TotalProcessingTime += (item.CompletedAt - item.StartedAt.GetValueOrDefault()).TotalMilliseconds;
            queue.UpdatedAt = DateTime.UtcNow;

            // Update statistics;
            TotalProcessedItems++;

            // Remove from active jobs;
            var job = _activeJobs.Values.FirstOrDefault(j => j.ItemId == itemId);
            if (job != null)
            {
                job.EndTime = DateTime.UtcNow;
                job.Status = JobStatus.Completed;
                job.Result = result;
                _activeJobs.TryRemove(job.Id, out _);
            }

            // Save changes;
            await _queueStorage.UpdateItemAsync(queueName, item);

            OnQueueItemProcessed(new QueueItemProcessedEventArgs(queueName, item, workerId));
            _logger.LogInformation($"Item '{itemId}' completed by worker '{workerId}'");

            // Process next item if queue is auto-processing;
            if (queue.Configuration.AutoStartProcessing && queue.Metrics.PendingItems > 0)
            {
                _ = Task.Run(() => ProcessNextItemAsync(queueName));
            }
        }

        /// <summary>
        /// Marks an item as failed;
        /// </summary>
        public async Task FailItemAsync(string queueName, string itemId, string workerId, string errorMessage, Exception exception = null)
        {
            ValidateQueueName(queueName);

            if (!_queues.TryGetValue(queueName, out var queue))
                throw new QueueException($"Queue '{queueName}' not found");

            var item = await _queueStorage.GetItemAsync(queueName, itemId);
            if (item == null)
                throw new QueueException($"Item '{itemId}' not found in queue '{queueName}'");

            // Update item;
            item.Status = QueueItemStatus.Failed;
            item.ErrorMessage = errorMessage;
            item.Exception = exception;
            item.UpdatedAt = DateTime.UtcNow;

            // Check if retry is possible;
            if (item.Attempts < item.MaxAttempts && queue.Configuration.MaxRetryAttempts > 0)
            {
                // Re-queue for retry with delay;
                var retryDelay = _retryPolicy.GetRetryDelay(item.Attempts);
                item.Status = QueueItemStatus.Pending;
                item.ScheduledFor = DateTime.UtcNow.Add(retryDelay);
                item.ErrorMessage = $"Will retry in {retryDelay.TotalSeconds} seconds. Attempt {item.Attempts + 1}/{item.MaxAttempts}. Error: {errorMessage}";

                queue.Metrics.FailedItems++;
                queue.Metrics.RetryCount++;

                _logger.LogWarning($"Item '{itemId}' failed, scheduled for retry in {retryDelay.TotalSeconds} seconds");
            }
            else;
            {
                // Final failure;
                item.Status = QueueItemStatus.Failed;
                queue.Metrics.FailedItems++;
                queue.Metrics.ProcessingItems--;

                _logger.LogError(exception, $"Item '{itemId}' failed permanently: {errorMessage}");
            }

            queue.UpdatedAt = DateTime.UtcNow;

            // Remove from active jobs;
            var job = _activeJobs.Values.FirstOrDefault(j => j.ItemId == itemId);
            if (job != null)
            {
                job.EndTime = DateTime.UtcNow;
                job.Status = JobStatus.Failed;
                job.Error = errorMessage;
                _activeJobs.TryRemove(job.Id, out _);
            }

            // Save changes;
            await _queueStorage.UpdateItemAsync(queueName, item);

            OnQueueItemFailed(new QueueItemFailedEventArgs(queueName, item, workerId, errorMessage, exception));

            // Process next item;
            if (queue.Configuration.AutoStartProcessing && queue.Metrics.PendingItems > 0)
            {
                _ = Task.Run(() => ProcessNextItemAsync(queueName));
            }
        }

        /// <summary>
        /// Cancels a pending or processing item;
        /// </summary>
        public async Task<bool> CancelItemAsync(string queueName, string itemId)
        {
            ValidateQueueName(queueName);

            var item = await _queueStorage.GetItemAsync(queueName, itemId);
            if (item == null)
                return false;

            if (item.Status == QueueItemStatus.Processing)
            {
                // Find and cancel the job;
                var job = _activeJobs.Values.FirstOrDefault(j => j.ItemId == itemId);
                if (job != null && _cancellationTokens.TryGetValue(job.Id, out var cts))
                {
                    cts.Cancel();
                }
            }

            // Update item;
            item.Status = QueueItemStatus.Cancelled;
            item.UpdatedAt = DateTime.UtcNow;

            // Update queue metrics;
            if (_queues.TryGetValue(queueName, out var queue))
            {
                if (item.Status == QueueItemStatus.Pending)
                    queue.Metrics.PendingItems--;
                else if (item.Status == QueueItemStatus.Processing)
                    queue.Metrics.ProcessingItems--;

                queue.Metrics.CancelledItems++;
                queue.UpdatedAt = DateTime.UtcNow;
            }

            await _queueStorage.UpdateItemAsync(queueName, item);

            _logger.LogInformation($"Item '{itemId}' cancelled");
            return true;
        }

        /// <summary>
        /// Gets item status and information;
        /// </summary>
        public async Task<QueueItem> GetItemStatusAsync(string queueName, string itemId)
        {
            ValidateQueueName(queueName);

            var item = await _queueStorage.GetItemAsync(queueName, itemId);
            if (item == null)
                throw new QueueException($"Item '{itemId}' not found in queue '{queueName}'");

            return item;
        }

        /// <summary>
        /// Lists items in a queue with filtering;
        /// </summary>
        public async Task<IEnumerable<QueueItem>> ListItemsAsync(string queueName, ItemFilter filter = null)
        {
            ValidateQueueName(queueName);

            filter ??= new ItemFilter();

            var items = await _queueStorage.GetItemsAsync(queueName);

            return items;
                .Where(i => FilterItem(i, filter))
                .OrderByDescending(i => i.CreatedAt)
                .Skip(filter.Skip)
                .Take(filter.Take);
        }

        private bool FilterItem(QueueItem item, ItemFilter filter)
        {
            if (filter.Status.HasValue && item.Status != filter.Status.Value)
                return false;

            if (!string.IsNullOrEmpty(filter.WorkerId) && item.WorkerId != filter.WorkerId)
                return false;

            if (filter.CreatedAfter.HasValue && item.CreatedAt < filter.CreatedAfter.Value)
                return false;

            if (filter.CreatedBefore.HasValue && item.CreatedAt > filter.CreatedBefore.Value)
                return false;

            if (!string.IsNullOrEmpty(filter.SearchText))
            {
                var searchText = filter.SearchText.ToLower();
                if (!item.Id.ToLower().Contains(searchText) &&
                    !(item.Data?.ToString()?.ToLower().Contains(searchText) ?? false) &&
                    !(item.ErrorMessage?.ToLower().Contains(searchText) ?? false))
                    return false;
            }

            return true;
        }

        #endregion;

        #region Worker Management;

        /// <summary>
        /// Registers a new worker node;
        /// </summary>
        public async Task<WorkerNode> RegisterWorkerAsync(string workerId, WorkerConfiguration config)
        {
            if (string.IsNullOrEmpty(workerId))
                throw new ArgumentException("Worker ID cannot be empty", nameof(workerId));

            if (_workerNodes.ContainsKey(workerId))
                throw new QueueException($"Worker '{workerId}' is already registered");

            var worker = new WorkerNode;
            {
                Id = workerId,
                Configuration = config,
                Status = WorkerStatus.Idle,
                RegisteredAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow,
                ProcessedItems = 0,
                FailedItems = 0,
                Metrics = new WorkerMetrics()
            };

            if (_workerNodes.TryAdd(workerId, worker))
            {
                _logger.LogInformation($"Worker '{workerId}' registered successfully");

                // Start processing if queues have items;
                var queuesWithItems = _queues.Values.Where(q => q.Metrics.PendingItems > 0);
                foreach (var queue in queuesWithItems)
                {
                    if (queue.Configuration.AutoStartProcessing)
                    {
                        _ = Task.Run(() => ProcessNextItemAsync(queue.Name));
                    }
                }

                OnWorkerStatusChanged(new WorkerStatusChangedEventArgs(workerId, WorkerStatus.Idle, null));
                return worker;
            }

            throw new QueueException($"Failed to register worker '{workerId}'");
        }

        /// <summary>
        /// Unregisters a worker node;
        /// </summary>
        public async Task<bool> UnregisterWorkerAsync(string workerId)
        {
            if (!_workerNodes.TryGetValue(workerId, out var worker))
                return false;

            // Reassign active jobs;
            var workerJobs = _activeJobs.Values.Where(j => j.WorkerId == workerId).ToList();
            foreach (var job in workerJobs)
            {
                await ReassignJobAsync(job.Id, workerId);
            }

            // Remove worker;
            _workerNodes.TryRemove(workerId, out _);

            _logger.LogInformation($"Worker '{workerId}' unregistered");
            OnWorkerStatusChanged(new WorkerStatusChangedEventArgs(workerId, WorkerStatus.Offline, "Unregistered"));

            await Task.CompletedTask;
            return true;
        }

        /// <summary>
        /// Updates worker heartbeat;
        /// </summary>
        public async Task UpdateWorkerHeartbeatAsync(string workerId)
        {
            if (_workerNodes.TryGetValue(workerId, out var worker))
            {
                worker.LastHeartbeat = DateTime.UtcNow;
                worker.Metrics.TotalUptime = (DateTime.UtcNow - worker.RegisteredAt).TotalMilliseconds;

                await Task.CompletedTask;
            }
        }

        /// <summary>
        /// Gets worker statistics;
        /// </summary>
        public async Task<WorkerNode> GetWorkerStatusAsync(string workerId)
        {
            if (_workerNodes.TryGetValue(workerId, out var worker))
            {
                // Update worker metrics;
                worker.Metrics.ActiveJobs = _activeJobs.Values.Count(j => j.WorkerId == workerId);
                worker.Metrics.Throughput = CalculateWorkerThroughput(worker);

                await Task.CompletedTask;
                return worker;
            }

            throw new QueueException($"Worker '{workerId}' not found");
        }

        /// <summary>
        /// Lists all registered workers;
        /// </summary>
        public async Task<IEnumerable<WorkerInfo>> ListWorkersAsync()
        {
            var workers = _workerNodes.Values.Select(w => new WorkerInfo;
            {
                Id = w.Id,
                Status = w.Status,
                RegisteredAt = w.RegisteredAt,
                LastHeartbeat = w.LastHeartbeat,
                ProcessedItems = w.ProcessedItems,
                FailedItems = w.FailedItems,
                ActiveJobs = _activeJobs.Values.Count(j => j.WorkerId == w.Id),
                Throughput = CalculateWorkerThroughput(w)
            }).ToList();

            await Task.CompletedTask;
            return workers;
        }

        private double CalculateWorkerThroughput(WorkerNode worker)
        {
            var uptime = (DateTime.UtcNow - worker.RegisteredAt).TotalHours;
            if (uptime <= 0) return 0;

            return worker.ProcessedItems / uptime;
        }

        #endregion;

        #region Processing;

        /// <summary>
        /// Starts processing items in all queues;
        /// </summary>
        public async Task StartProcessingAsync()
        {
            if (Status == QueueStatus.Running)
                return;

            Status = QueueStatus.Running;

            // Start processing for each queue;
            foreach (var queue in _queues.Values.Where(q => q.Status == QueueStatus.Running))
            {
                _ = Task.Run(() => ProcessQueueAsync(queue.Name));
            }

            _logger.LogInformation("Queue processing started");
            OnQueueStatusChanged(new QueueStatusChangedEventArgs("System", QueueStatus.Running));

            await Task.CompletedTask;
        }

        /// <summary>
        /// Stops processing items;
        /// </summary>
        public async Task StopProcessingAsync(bool graceful = true)
        {
            if (Status == QueueStatus.Stopped)
                return;

            Status = QueueStatus.Stopped;

            if (graceful)
            {
                // Wait for active jobs to complete;
                await WaitForActiveJobsAsync(TimeSpan.FromMinutes(5));
            }
            else;
            {
                // Cancel all active jobs;
                await CancelAllJobsAsync();
            }

            _logger.LogInformation("Queue processing stopped");
            OnQueueStatusChanged(new QueueStatusChangedEventArgs("System", QueueStatus.Stopped));
        }

        /// <summary>
        /// Processes the next available item from a queue;
        /// </summary>
        private async Task ProcessNextItemAsync(string queueName)
        {
            if (!_queues.TryGetValue(queueName, out var queue) || queue.Status != QueueStatus.Running)
                return;

            if (queue.Metrics.PendingItems == 0 || _workerNodes.Count == 0)
                return;

            try
            {
                // Select worker using load balancer;
                var workerId = _loadBalancer.SelectWorker(_workerNodes.Values);
                if (string.IsNullOrEmpty(workerId))
                    return;

                // Dequeue item;
                var item = await DequeueItemAsync(queueName, workerId);
                if (item == null)
                    return;

                // Update worker status;
                if (_workerNodes.TryGetValue(workerId, out var worker))
                {
                    worker.Status = WorkerStatus.Processing;
                    worker.Metrics.ActiveJobs++;
                }

                // Process item;
                _ = Task.Run(() => ProcessItemAsync(queueName, item, workerId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing next item in queue '{queueName}'");
            }
        }

        /// <summary>
        /// Processes a queue continuously;
        /// </summary>
        private async Task ProcessQueueAsync(string queueName)
        {
            if (!_queues.TryGetValue(queueName, out var queue))
                return;

            _logger.LogInformation($"Started processing queue '{queueName}'");

            while (queue.Status == QueueStatus.Running && Status == QueueStatus.Running)
            {
                try
                {
                    await ProcessNextItemAsync(queueName);

                    // Throttle if no items;
                    if (queue.Metrics.PendingItems == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error in queue processing loop for '{queueName}'");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            _logger.LogInformation($"Stopped processing queue '{queueName}'");
        }

        /// <summary>
        /// Processes an individual item;
        /// </summary>
        private async Task ProcessItemAsync(string queueName, QueueItem item, string workerId)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var jobId = _activeJobs.Values.FirstOrDefault(j => j.ItemId == item.Id)?.Id;

            if (!string.IsNullOrEmpty(jobId))
            {
                _cancellationTokens.TryAdd(jobId, cancellationTokenSource);
            }

            try
            {
                // Execute processing;
                var result = await ExecuteProcessingAsync(item, cancellationTokenSource.Token);

                // Mark as completed;
                await CompleteItemAsync(queueName, item.Id, workerId, result);

                // Update worker stats;
                if (_workerNodes.TryGetValue(workerId, out var worker))
                {
                    worker.ProcessedItems++;
                    worker.Status = WorkerStatus.Idle;
                    worker.Metrics.ActiveJobs--;
                }
            }
            catch (OperationCanceledException)
            {
                await CancelItemAsync(queueName, item.Id);
                _logger.LogInformation($"Item '{item.Id}' processing cancelled");
            }
            catch (Exception ex)
            {
                // Mark as failed;
                await FailItemAsync(queueName, item.Id, workerId, ex.Message, ex);

                // Update worker stats;
                if (_workerNodes.TryGetValue(workerId, out var worker))
                {
                    worker.FailedItems++;
                    worker.Status = WorkerStatus.Idle;
                    worker.Metrics.ActiveJobs--;
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(jobId))
                {
                    _cancellationTokens.TryRemove(jobId, out _);
                    cancellationTokenSource.Dispose();
                }
            }
        }

        /// <summary>
        /// Executes the actual processing logic for an item;
        /// </summary>
        private async Task<object> ExecuteProcessingAsync(QueueItem item, CancellationToken cancellationToken)
        {
            // This is where the actual processing logic would go;
            // For a real implementation, this would call specific processors based on item type;

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken); // Simulate work;

            return new { Success = true, ProcessedAt = DateTime.UtcNow };
        }

        #endregion;

        #region Job Management;

        /// <summary>
        /// Cancels a specific job;
        /// </summary>
        public async Task<bool> CancelJobAsync(string jobId)
        {
            if (!_activeJobs.TryGetValue(jobId, out var job))
                return false;

            if (_cancellationTokens.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
                _cancellationTokens.TryRemove(jobId, out _);
            }

            // Update job status;
            job.Status = JobStatus.Cancelled;
            job.EndTime = DateTime.UtcNow;

            // Update item status;
            await CancelItemAsync(job.QueueName, job.ItemId);

            _logger.LogInformation($"Job '{jobId}' cancelled");
            return true;
        }

        /// <summary>
        /// Reassigns a job to a different worker;
        /// </summary>
        private async Task ReassignJobAsync(string jobId, string oldWorkerId)
        {
            if (!_activeJobs.TryGetValue(jobId, out var job))
                return;

            // Select new worker;
            var newWorkerId = _loadBalancer.SelectWorker(_workerNodes.Values.Where(w => w.Id != oldWorkerId));
            if (string.IsNullOrEmpty(newWorkerId))
                return;

            // Update job;
            job.WorkerId = newWorkerId;

            // Update item;
            var item = await _queueStorage.GetItemAsync(job.QueueName, job.ItemId);
            if (item != null)
            {
                item.WorkerId = newWorkerId;
                await _queueStorage.UpdateItemAsync(job.QueueName, item);
            }

            _logger.LogInformation($"Job '{jobId}' reassigned from worker '{oldWorkerId}' to '{newWorkerId}'");
        }

        /// <summary>
        /// Cancels all active jobs;
        /// </summary>
        private async Task CancelAllJobsAsync()
        {
            var jobIds = _activeJobs.Keys.ToList();

            foreach (var jobId in jobIds)
            {
                await CancelJobAsync(jobId);
            }
        }

        /// <summary>
        /// Waits for active jobs to complete;
        /// </summary>
        private async Task WaitForActiveJobsAsync(TimeSpan timeout)
        {
            var startTime = DateTime.UtcNow;

            while (_activeJobs.Count > 0 && (DateTime.UtcNow - startTime) < timeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            if (_activeJobs.Count > 0)
            {
                _logger.LogWarning($"{_activeJobs.Count} jobs still active after timeout");
            }
        }

        #endregion;

        #region Metrics and Monitoring;

        /// <summary>
        /// Collects system metrics;
        /// </summary>
        private async void CollectMetrics(object state)
        {
            try
            {
                await CalculateStatisticsAsync();

                var metrics = new Dictionary<string, object>
                {
                    ["queues.total"] = TotalQueues,
                    ["workers.total"] = TotalWorkers,
                    ["jobs.active"] = ActiveJobsCount,
                    ["items.processed.total"] = TotalProcessedItems,
                    ["items.pending.total"] = _queues.Values.Sum(q => q.Metrics.PendingItems),
                    ["items.processing.total"] = _queues.Values.Sum(q => q.Metrics.ProcessingItems),
                    ["system.status"] = Status.ToString()
                };

                // Add queue-specific metrics;
                foreach (var queue in _queues.Values)
                {
                    metrics[$"queue.{queue.Name}.pending"] = queue.Metrics.PendingItems;
                    metrics[$"queue.{queue.Name}.processing"] = queue.Metrics.ProcessingItems;
                    metrics[$"queue.{queue.Name}.completed"] = queue.Metrics.CompletedItems;
                    metrics[$"queue.{queue.Name}.failed"] = queue.Metrics.FailedItems;
                }

                // Send to metrics collector;
                await _metricsCollector.RecordMetricsAsync("queue_manager", metrics);

                OnQueueMetricsUpdated(new QueueMetricsUpdatedEventArgs(Statistics));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting queue metrics");
            }
        }

        /// <summary>
        /// Calculates queue statistics;
        /// </summary>
        private async Task CalculateStatisticsAsync()
        {
            Statistics = new QueueStatistics;
            {
                TotalQueues = TotalQueues,
                TotalWorkers = TotalWorkers,
                ActiveJobs = ActiveJobsCount,
                TotalProcessedItems = TotalProcessedItems,
                TotalPendingItems = _queues.Values.Sum(q => q.Metrics.PendingItems),
                TotalProcessingItems = _queues.Values.Sum(q => q.Metrics.ProcessingItems),
                TotalFailedItems = _queues.Values.Sum(q => q.Metrics.FailedItems),
                AverageProcessingTime = _queues.Values;
                    .Where(q => q.Metrics.CompletedItems > 0)
                    .Average(q => q.Metrics.TotalProcessingTime / q.Metrics.CompletedItems),
                Uptime = (DateTime.UtcNow - _queues.Values.Min(q => q.CreatedAt)).TotalSeconds,
                LastUpdated = DateTime.UtcNow;
            };

            await Task.CompletedTask;
        }

        /// <summary>
        /// Calculates metrics for a specific queue;
        /// </summary>
        private async Task<QueueMetrics> CalculateQueueMetricsAsync(string queueName)
        {
            var items = await _queueStorage.GetItemsAsync(queueName);

            var metrics = new QueueMetrics;
            {
                TotalItems = items.Count,
                PendingItems = items.Count(i => i.Status == QueueItemStatus.Pending),
                ProcessingItems = items.Count(i => i.Status == QueueItemStatus.Processing),
                CompletedItems = items.Count(i => i.Status == QueueItemStatus.Completed),
                FailedItems = items.Count(i => i.Status == QueueItemStatus.Failed),
                CancelledItems = items.Count(i => i.Status == QueueItemStatus.Cancelled),
                RetryCount = items.Sum(i => i.Attempts - 1),
                TotalProcessingTime = items;
                    .Where(i => i.CompletedAt.HasValue && i.StartedAt.HasValue)
                    .Sum(i => (i.CompletedAt.Value - i.StartedAt.Value).TotalMilliseconds)
            };

            return metrics;
        }

        /// <summary>
        /// Performs health checks on workers and queues;
        /// </summary>
        private async void PerformHealthChecks(object state)
        {
            try
            {
                // Check worker health;
                var deadWorkers = new List<string>();
                var timeout = TimeSpan.FromMinutes(10);

                foreach (var worker in _workerNodes.Values)
                {
                    if (DateTime.UtcNow - worker.LastHeartbeat > timeout)
                    {
                        deadWorkers.Add(worker.Id);
                        _logger.LogWarning($"Worker '{worker.Id}' appears dead, last heartbeat: {worker.LastHeartbeat}");
                    }
                }

                // Clean up dead workers;
                foreach (var workerId in deadWorkers)
                {
                    await UnregisterWorkerAsync(workerId);
                }

                // Check queue health;
                foreach (var queue in _queues.Values)
                {
                    if (queue.Metrics.ProcessingItems > 0 && queue.Metrics.PendingItems > 1000)
                    {
                        _logger.LogWarning($"Queue '{queue.Name}' may be experiencing backlog: {queue.Metrics.PendingItems} pending items");
                    }
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing health checks");
            }
        }

        /// <summary>
        /// Cleans up old completed items;
        /// </summary>
        private async void CleanupOldItems(object state)
        {
            try
            {
                var cleanupThreshold = TimeSpan.FromDays(7);
                var cutoffDate = DateTime.UtcNow.Subtract(cleanupThreshold);

                foreach (var queue in _queues.Values)
                {
                    var items = await _queueStorage.GetItemsAsync(queue.Name);
                    var oldItems = items.Where(i =>
                        (i.Status == QueueItemStatus.Completed || i.Status == QueueItemStatus.Failed || i.Status == QueueItemStatus.Cancelled) &&
                        i.UpdatedAt < cutoffDate);

                    foreach (var item in oldItems)
                    {
                        await _queueStorage.RemoveItemAsync(queue.Name, item.Id);
                    }

                    if (oldItems.Any())
                    {
                        _logger.LogInformation($"Cleaned up {oldItems.Count()} old items from queue '{queue.Name}'");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old items");
            }
        }

        #endregion;

        #region Utility Methods;

        private void ValidateQueueName(string queueName)
        {
            if (string.IsNullOrEmpty(queueName))
                throw new ArgumentException("Queue name cannot be empty", nameof(queueName));

            if (queueName.Length > 100)
                throw new ArgumentException("Queue name cannot exceed 100 characters", nameof(queueName));

            if (!queueName.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                throw new ArgumentException("Queue name can only contain letters, digits, hyphens, and underscores", nameof(queueName));
        }

        private void ValidateQueueItem(QueueItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            if (string.IsNullOrEmpty(item.Type))
                throw new ArgumentException("Item type cannot be empty", nameof(item.Type));

            if (item.Data == null)
                throw new ArgumentException("Item data cannot be null", nameof(item.Data));
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnQueueItemAdded(QueueItemAddedEventArgs e)
        {
            QueueItemAdded?.Invoke(this, e);
        }

        protected virtual void OnQueueItemProcessed(QueueItemProcessedEventArgs e)
        {
            QueueItemProcessed?.Invoke(this, e);
        }

        protected virtual void OnQueueItemFailed(QueueItemFailedEventArgs e)
        {
            QueueItemFailed?.Invoke(this, e);
        }

        protected virtual void OnWorkerStatusChanged(WorkerStatusChangedEventArgs e)
        {
            WorkerStatusChanged?.Invoke(this, e);
        }

        protected virtual void OnQueueStatusChanged(QueueStatusChangedEventArgs e)
        {
            QueueStatusChanged?.Invoke(this, e);
        }

        protected virtual void OnQueueMetricsUpdated(QueueMetricsUpdatedEventArgs e)
        {
            QueueMetricsUpdated?.Invoke(this, e);
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
                    // Stop timers;
                    _healthCheckTimer?.Dispose();
                    _metricsTimer?.Dispose();
                    _cleanupTimer?.Dispose();

                    // Cancel all jobs;
                    foreach (var cts in _cancellationTokens.Values)
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }

                    // Dispose semaphore;
                    _queueSemaphore?.Dispose();

                    // Stop processing;
                    _ = StopProcessingAsync(false).ConfigureAwait(false);
                }

                _isDisposed = true;
            }
        }

        ~QueueManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IQueueManager;
    {
        // Queue Management;
        Task<ProcessingQueue> CreateQueueAsync(string queueName, QueueConfiguration config = null);
        Task<bool> DeleteQueueAsync(string queueName, bool force = false);
        Task PauseQueueAsync(string queueName);
        Task ResumeQueueAsync(string queueName);
        Task<ProcessingQueue> GetQueueInfoAsync(string queueName);
        Task<IEnumerable<QueueInfo>> ListQueuesAsync(QueueFilter filter = null);

        // Item Management;
        Task<QueueItem> EnqueueItemAsync(string queueName, QueueItem item, int priority = 0);
        Task<BatchEnqueueResult> EnqueueBatchAsync(string queueName, IEnumerable<QueueItem> items, int priority = 0);
        Task<QueueItem> DequeueItemAsync(string queueName, string workerId);
        Task CompleteItemAsync(string queueName, string itemId, string workerId, object result = null);
        Task FailItemAsync(string queueName, string itemId, string workerId, string errorMessage, Exception exception = null);
        Task<bool> CancelItemAsync(string queueName, string itemId);
        Task<QueueItem> GetItemStatusAsync(string queueName, string itemId);
        Task<IEnumerable<QueueItem>> ListItemsAsync(string queueName, ItemFilter filter = null);

        // Worker Management;
        Task<WorkerNode> RegisterWorkerAsync(string workerId, WorkerConfiguration config);
        Task<bool> UnregisterWorkerAsync(string workerId);
        Task UpdateWorkerHeartbeatAsync(string workerId);
        Task<WorkerNode> GetWorkerStatusAsync(string workerId);
        Task<IEnumerable<WorkerInfo>> ListWorkersAsync();

        // Processing Control;
        Task StartProcessingAsync();
        Task StopProcessingAsync(bool graceful = true);

        // Properties;
        QueueStatus Status { get; }
        QueueStatistics Statistics { get; }
        int TotalQueues { get; }
        int TotalWorkers { get; }
        int ActiveJobsCount { get; }
        long TotalProcessedItems { get; }

        // Events;
        event EventHandler<QueueItemAddedEventArgs> QueueItemAdded;
        event EventHandler<QueueItemProcessedEventArgs> QueueItemProcessed;
        event EventHandler<QueueItemFailedEventArgs> QueueItemFailed;
        event EventHandler<WorkerStatusChangedEventArgs> WorkerStatusChanged;
        event EventHandler<QueueStatusChangedEventArgs> QueueStatusChanged;
        event EventHandler<QueueMetricsUpdatedEventArgs> QueueMetricsUpdated;
    }

    public interface IQueueStorage;
    {
        Task SaveQueueAsync(ProcessingQueue queue);
        Task<ProcessingQueue> GetQueueAsync(string queueName);
        Task UpdateQueueAsync(ProcessingQueue queue);
        Task DeleteQueueAsync(string queueName);
        Task ClearQueueAsync(string queueName);

        Task AddItemAsync(string queueName, QueueItem item);
        Task<QueueItem> GetItemAsync(string queueName, string itemId);
        Task UpdateItemAsync(string queueName, QueueItem item);
        Task RemoveItemAsync(string queueName, string itemId);
        Task<IEnumerable<QueueItem>> GetItemsAsync(string queueName);
        Task<int> GetItemCountAsync(string queueName, QueueItemStatus? status = null);
    }

    public interface ILoadBalancer;
    {
        string SelectWorker(IEnumerable<WorkerNode> workers);
        string SelectWorker(IEnumerable<WorkerNode> workers, string queueName);
    }

    public interface IRetryPolicy;
    {
        TimeSpan GetRetryDelay(int attempt);
        bool ShouldRetry(int attempt, Exception exception);
    }

    public class InMemoryQueueStorage : IQueueStorage;
    {
        private readonly ConcurrentDictionary<string, ProcessingQueue> _queues = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, QueueItem>> _items = new();

        public Task SaveQueueAsync(ProcessingQueue queue)
        {
            _queues[queue.Name] = queue;
            return Task.CompletedTask;
        }

        public Task<ProcessingQueue> GetQueueAsync(string queueName)
        {
            _queues.TryGetValue(queueName, out var queue);
            return Task.FromResult(queue);
        }

        public Task UpdateQueueAsync(ProcessingQueue queue)
        {
            _queues[queue.Name] = queue;
            return Task.CompletedTask;
        }

        public Task DeleteQueueAsync(string queueName)
        {
            _queues.TryRemove(queueName, out _);
            _items.TryRemove(queueName, out _);
            return Task.CompletedTask;
        }

        public Task ClearQueueAsync(string queueName)
        {
            if (_items.TryGetValue(queueName, out var queueItems))
            {
                queueItems.Clear();
            }
            return Task.CompletedTask;
        }

        public Task AddItemAsync(string queueName, QueueItem item)
        {
            var queueItems = _items.GetOrAdd(queueName, _ => new ConcurrentDictionary<string, QueueItem>());
            queueItems[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<QueueItem> GetItemAsync(string queueName, string itemId)
        {
            if (_items.TryGetValue(queueName, out var queueItems) && queueItems.TryGetValue(itemId, out var item))
            {
                return Task.FromResult(item);
            }
            return Task.FromResult<QueueItem>(null);
        }

        public Task UpdateItemAsync(string queueName, QueueItem item)
        {
            if (_items.TryGetValue(queueName, out var queueItems))
            {
                queueItems[item.Id] = item;
            }
            return Task.CompletedTask;
        }

        public Task RemoveItemAsync(string queueName, string itemId)
        {
            if (_items.TryGetValue(queueName, out var queueItems))
            {
                queueItems.TryRemove(itemId, out _);
            }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<QueueItem>> GetItemsAsync(string queueName)
        {
            if (_items.TryGetValue(queueName, out var queueItems))
            {
                return Task.FromResult(queueItems.Values.AsEnumerable());
            }
            return Task.FromResult(Enumerable.Empty<QueueItem>());
        }

        public Task<int> GetItemCountAsync(string queueName, QueueItemStatus? status = null)
        {
            if (_items.TryGetValue(queueName, out var queueItems))
            {
                if (status.HasValue)
                {
                    return Task.FromResult(queueItems.Values.Count(i => i.Status == status.Value));
                }
                return Task.FromResult(queueItems.Count);
            }
            return Task.FromResult(0);
        }
    }

    public class RoundRobinLoadBalancer : ILoadBalancer;
    {
        private int _counter = 0;
        private readonly object _lock = new object();

        public string SelectWorker(IEnumerable<WorkerNode> workers)
        {
            var availableWorkers = workers.Where(w => w.Status == WorkerStatus.Idle).ToList();
            if (!availableWorkers.Any())
                return null;

            lock (_lock)
            {
                var worker = availableWorkers[_counter % availableWorkers.Count];
                _counter++;
                return worker.Id;
            }
        }

        public string SelectWorker(IEnumerable<WorkerNode> workers, string queueName)
        {
            return SelectWorker(workers);
        }
    }

    public class ExponentialBackoffRetryPolicy : IRetryPolicy;
    {
        public TimeSpan GetRetryDelay(int attempt)
        {
            var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            var jitter = new Random().NextDouble() * 0.1 * baseDelay.TotalSeconds;
            return baseDelay.Add(TimeSpan.FromSeconds(jitter));
        }

        public bool ShouldRetry(int attempt, Exception exception)
        {
            // Don't retry on certain exceptions;
            if (exception is ArgumentException || exception is InvalidOperationException)
                return false;

            return attempt < 5; // Max 5 retries;
        }
    }

    public enum QueueStatus;
    {
        Stopped,
        Running,
        Paused,
        Error;
    }

    public enum QueueItemStatus;
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Cancelled;
    }

    public enum WorkerStatus;
    {
        Idle,
        Processing,
        Busy,
        Offline,
        Error;
    }

    public enum JobStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled;
    }

    public class QueueConfiguration;
    {
        public static QueueConfiguration Default => new QueueConfiguration();

        public int MaxConcurrentOperations { get; set; } = 100;
        public int MaxQueueSize { get; set; } = 10000;
        public int MaxRetryAttempts { get; set; } = 3;
        public bool AutoStartProcessing { get; set; } = true;
        public TimeSpan ItemTimeout { get; set; } = TimeSpan.FromHours(1);
        public bool EnablePriority { get; set; } = true;
        public int DefaultPriority { get; set; } = 0;
    }

    public class WorkerConfiguration;
    {
        public string Name { get; set; }
        public int MaxConcurrentJobs { get; set; } = 5;
        public string[] Capabilities { get; set; } = Array.Empty<string>();
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }

    public class ProcessingQueue;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public QueueConfiguration Configuration { get; set; }
        public QueueStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public QueueMetrics Metrics { get; set; }
    }

    public class QueueItem;
    {
        public string Id { get; set; }
        public string QueueName { get; set; }
        public string Type { get; set; }
        public object Data { get; set; }
        public int Priority { get; set; }
        public QueueItemStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? ScheduledFor { get; set; }
        public string WorkerId { get; set; }
        public int Attempts { get; set; }
        public int MaxAttempts { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public object Result { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class WorkerNode;
    {
        public string Id { get; set; }
        public WorkerConfiguration Configuration { get; set; }
        public WorkerStatus Status { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public long ProcessedItems { get; set; }
        public long FailedItems { get; set; }
        public WorkerMetrics Metrics { get; set; }
    }

    public class ProcessingJob;
    {
        public string Id { get; set; }
        public string QueueName { get; set; }
        public string ItemId { get; set; }
        public string WorkerId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public JobStatus Status { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
    }

    public class QueueMetrics;
    {
        public long TotalItems { get; set; }
        public long PendingItems { get; set; }
        public long ProcessingItems { get; set; }
        public long CompletedItems { get; set; }
        public long FailedItems { get; set; }
        public long CancelledItems { get; set; }
        public long RetryCount { get; set; }
        public double TotalProcessingTime { get; set; } // milliseconds;
    }

    public class WorkerMetrics;
    {
        public int ActiveJobs { get; set; }
        public double TotalUptime { get; set; } // milliseconds;
        public double Throughput { get; set; } // items per hour;
        public double SuccessRate { get; set; } // percentage;
    }

    public class QueueStatistics;
    {
        public int TotalQueues { get; set; }
        public int TotalWorkers { get; set; }
        public int ActiveJobs { get; set; }
        public long TotalProcessedItems { get; set; }
        public long TotalPendingItems { get; set; }
        public long TotalProcessingItems { get; set; }
        public long TotalFailedItems { get; set; }
        public double AverageProcessingTime { get; set; } // milliseconds;
        public double Uptime { get; set; } // seconds;
        public DateTime LastUpdated { get; set; }
    }

    public class QueueInfo;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public QueueStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public long ItemCount { get; set; }
        public long PendingCount { get; set; }
        public long ProcessingCount { get; set; }
        public long FailedCount { get; set; }
    }

    public class WorkerInfo;
    {
        public string Id { get; set; }
        public WorkerStatus Status { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public long ProcessedItems { get; set; }
        public long FailedItems { get; set; }
        public int ActiveJobs { get; set; }
        public double Throughput { get; set; }
    }

    public class BatchEnqueueResult;
    {
        public string QueueName { get; set; }
        public int TotalItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public List<string> FailedReasons { get; set; }
    }

    public class QueueFilter;
    {
        public string NameContains { get; set; }
        public QueueStatus? Status { get; set; }
        public int? MinItems { get; set; }
        public DateTime? CreatedAfter { get; set; }
    }

    public class ItemFilter;
    {
        public QueueItemStatus? Status { get; set; }
        public string WorkerId { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? CreatedBefore { get; set; }
        public string SearchText { get; set; }
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 100;
    }

    #endregion;

    #region Event Args Classes;

    public class QueueItemAddedEventArgs : EventArgs;
    {
        public string QueueName { get; }
        public QueueItem Item { get; }

        public QueueItemAddedEventArgs(string queueName, QueueItem item)
        {
            QueueName = queueName;
            Item = item;
        }
    }

    public class QueueItemProcessedEventArgs : EventArgs;
    {
        public string QueueName { get; }
        public QueueItem Item { get; }
        public string WorkerId { get; }

        public QueueItemProcessedEventArgs(string queueName, QueueItem item, string workerId)
        {
            QueueName = queueName;
            Item = item;
            WorkerId = workerId;
        }
    }

    public class QueueItemFailedEventArgs : EventArgs;
    {
        public string QueueName { get; }
        public QueueItem Item { get; }
        public string WorkerId { get; }
        public string ErrorMessage { get; }
        public Exception Exception { get; }

        public QueueItemFailedEventArgs(string queueName, QueueItem item, string workerId, string errorMessage, Exception exception)
        {
            QueueName = queueName;
            Item = item;
            WorkerId = workerId;
            ErrorMessage = errorMessage;
            Exception = exception;
        }
    }

    public class WorkerStatusChangedEventArgs : EventArgs;
    {
        public string WorkerId { get; }
        public WorkerStatus NewStatus { get; }
        public string Reason { get; }

        public WorkerStatusChangedEventArgs(string workerId, WorkerStatus newStatus, string reason)
        {
            WorkerId = workerId;
            NewStatus = newStatus;
            Reason = reason;
        }
    }

    public class QueueStatusChangedEventArgs : EventArgs;
    {
        public string QueueName { get; }
        public QueueStatus NewStatus { get; }

        public QueueStatusChangedEventArgs(string queueName, QueueStatus newStatus)
        {
            QueueName = queueName;
            NewStatus = newStatus;
        }
    }

    public class QueueMetricsUpdatedEventArgs : EventArgs;
    {
        public QueueStatistics Statistics { get; }

        public QueueMetricsUpdatedEventArgs(QueueStatistics statistics)
        {
            Statistics = statistics;
        }
    }

    #endregion;

    #region Custom Exceptions;

    public class QueueException : Exception
    {
        public QueueException(string message) : base(message) { }
        public QueueException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
