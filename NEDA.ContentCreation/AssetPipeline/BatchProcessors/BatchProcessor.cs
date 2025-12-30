using NEDA.ContentCreation.AssetPipeline.Common;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.ContentCreation.AssetPipeline.BatchProcessors.BatchProcessor;

namespace NEDA.ContentCreation.AssetPipeline.BatchProcessors;
{
    /// <summary>
    /// Advanced batch processing engine for asset pipeline operations;
    /// Supports parallel processing, dependency resolution, progress tracking, and error recovery;
    /// </summary>
    public class BatchProcessor : IDisposable
    {
        #region Nested Types;

        /// <summary>
        /// Batch job definition with processing parameters;
        /// </summary>
        public class BatchJob;
        {
            /// <summary>
            /// Unique job identifier;
            /// </summary>
            public Guid Id { get; }

            /// <summary>
            /// Job name for identification;
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Job description;
            /// </summary>
            public string Description { get; set; }

            /// <summary>
            /// List of processing tasks;
            /// </summary>
            public List<ProcessingTask> Tasks { get; }

            /// <summary>
            /// Job priority (Higher = more important)
            /// </summary>
            public int Priority { get; set; }

            /// <summary>
            /// Maximum degree of parallelism for this job;
            /// </summary>
            public int MaxDegreeOfParallelism { get; set; }

            /// <summary>
            /// Timeout in milliseconds;
            /// </summary>
            public TimeSpan Timeout { get; set; }

            /// <summary>
            /// Job creation timestamp;
            /// </summary>
            public DateTime CreatedAt { get; }

            /// <summary>
            /// Job scheduled execution time;
            /// </summary>
            public DateTime? ScheduledTime { get; set; }

            /// <summary>
            /// Job expiration time;
            /// </summary>
            public DateTime? ExpirationTime { get; set; }

            /// <summary>
            /// Job tags for categorization;
            /// </summary>
            public HashSet<string> Tags { get; }

            /// <summary>
            /// Job metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; }

            /// <summary>
            /// Job dependencies (other job IDs that must complete first)
            /// </summary>
            public HashSet<Guid> Dependencies { get; }

            /// <summary>
            /// Retry policy for failed tasks;
            /// </summary>
            public RetryPolicy RetryPolicy { get; set; }

            /// <summary>
            /// Notification settings;
            /// </summary>
            public NotificationSettings NotificationSettings { get; set; }

            /// <summary>
            /// Job status;
            /// </summary>
            public JobStatus Status { get; internal set; }

            /// <summary>
            /// Job progress (0-100)
            /// </summary>
            public float Progress { get; internal set; }

            /// <summary>
            /// Job start time;
            /// </summary>
            public DateTime? StartTime { get; internal set; }

            /// <summary>
            /// Job completion time;
            /// </summary>
            public DateTime? CompletionTime { get; internal set; }

            /// <summary>
            /// Total execution time;
            /// </summary>
            public TimeSpan? ExecutionTime => StartTime.HasValue && CompletionTime.HasValue;
                ? CompletionTime.Value - StartTime.Value;
                : null;

            /// <summary>
            /// Error information if job failed;
            /// </summary>
            public Exception Error { get; internal set; }

            /// <summary>
            /// Processing statistics;
            /// </summary>
            public JobStatistics Statistics { get; internal set; }

            public BatchJob(string name)
            {
                Id = Guid.NewGuid();
                Name = name ?? throw new ArgumentNullException(nameof(name));
                Tasks = new List<ProcessingTask>();
                Priority = 1;
                MaxDegreeOfParallelism = Environment.ProcessorCount;
                Timeout = TimeSpan.FromHours(1);
                CreatedAt = DateTime.UtcNow;
                Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Metadata = new Dictionary<string, object>();
                Dependencies = new HashSet<Guid>();
                RetryPolicy = new RetryPolicy();
                NotificationSettings = new NotificationSettings();
                Status = JobStatus.Pending;
                Progress = 0f;
                Statistics = new JobStatistics();
            }

            /// <summary>
            /// Add task to job;
            /// </summary>
            public void AddTask(ProcessingTask task)
            {
                if (task == null)
                    throw new ArgumentNullException(nameof(task));

                Tasks.Add(task);
                task.JobId = Id;
            }

            /// <summary>
            /// Add multiple tasks;
            /// </summary>
            public void AddTasks(IEnumerable<ProcessingTask> tasks)
            {
                foreach (var task in tasks)
                {
                    AddTask(task);
                }
            }

            /// <summary>
            /// Validate job configuration;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (string.IsNullOrWhiteSpace(Name))
                    errors.Add("Job name cannot be empty");

                if (Tasks.Count == 0)
                    errors.Add("Job must contain at least one task");

                if (MaxDegreeOfParallelism < 1)
                    errors.Add("MaxDegreeOfParallelism must be at least 1");

                if (Timeout <= TimeSpan.Zero)
                    errors.Add("Timeout must be positive");

                foreach (var task in Tasks)
                {
                    if (!task.Validate(out var taskErrors))
                    {
                        errors.AddRange(taskErrors.Select(e => $"Task '{task.Name}': {e}"));
                    }
                }

                return errors.Count == 0;
            }

            /// <summary>
            /// Create a copy of the job;
            /// </summary>
            public BatchJob Clone(string newName = null)
            {
                var clone = new BatchJob(newName ?? $"{Name}_Copy")
                {
                    Description = Description,
                    Priority = Priority,
                    MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                    Timeout = Timeout,
                    ScheduledTime = ScheduledTime,
                    ExpirationTime = ExpirationTime,
                    RetryPolicy = RetryPolicy.Clone(),
                    NotificationSettings = NotificationSettings.Clone()
                };

                foreach (var tag in Tags)
                    clone.Tags.Add(tag);

                foreach (var dependency in Dependencies)
                    clone.Dependencies.Add(dependency);

                foreach (var metadata in Metadata)
                    clone.Metadata[metadata.Key] = metadata.Value;

                foreach (var task in Tasks)
                    clone.AddTask(task.Clone());

                return clone;
            }
        }

        /// <summary>
        /// Individual processing task within a batch job;
        /// </summary>
        public class ProcessingTask;
        {
            /// <summary>
            /// Unique task identifier;
            /// </summary>
            public Guid Id { get; }

            /// <summary>
            /// Task name;
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Task description;
            /// </summary>
            public string Description { get; set; }

            /// <summary>
            /// Task type identifier;
            /// </summary>
            public string TaskType { get; set; }

            /// <summary>
            /// Task parameters;
            /// </summary>
            public Dictionary<string, object> Parameters { get; }

            /// <summary>
            /// Input data for processing;
            /// </summary>
            public object InputData { get; set; }

            /// <summary>
            /// Output data after processing;
            /// </summary>
            public object OutputData { get; internal set; }

            /// <summary>
            /// Task dependencies within the same job;
            /// </summary>
            public List<Guid> TaskDependencies { get; }

            /// <summary>
            /// Task priority within job;
            /// </summary>
            public int Priority { get; set; }

            /// <summary>
            /// Estimated processing weight (for load balancing)
            /// </summary>
            public int ProcessingWeight { get; set; }

            /// <summary>
            /// Required memory in MB;
            /// </summary>
            public long RequiredMemoryMB { get; set; }

            /// <summary>
            /// Required disk space in MB;
            /// </summary>
            public long RequiredDiskSpaceMB { get; set; }

            /// <summary>
            /// Task timeout;
            /// </summary>
            public TimeSpan Timeout { get; set; }

            /// <summary>
            /// Parent job ID;
            /// </summary>
            public Guid JobId { get; internal set; }

            /// <summary>
            /// Task status;
            /// </summary>
            public TaskStatus Status { get; internal set; }

            /// <summary>
            /// Task progress (0-100)
            /// </summary>
            public float Progress { get; internal set; }

            /// <summary>
            /// Task start time;
            /// </summary>
            public DateTime? StartTime { get; internal set; }

            /// <summary>
            /// Task completion time;
            /// </summary>
            public DateTime? CompletionTime { get; internal set; }

            /// <summary>
            /// Task execution time;
            /// </summary>
            public TimeSpan? ExecutionTime => StartTime.HasValue && CompletionTime.HasValue;
                ? CompletionTime.Value - StartTime.Value;
                : null;

            /// <summary>
            /// Error if task failed;
            /// </summary>
            public Exception Error { get; internal set; }

            /// <summary>
            /// Retry attempt count;
            /// </summary>
            public int RetryCount { get; internal set; }

            /// <summary>
            /// Task result metadata;
            /// </summary>
            public Dictionary<string, object> ResultMetadata { get; internal set; }

            public ProcessingTask(string name, string taskType)
            {
                Id = Guid.NewGuid();
                Name = name ?? throw new ArgumentNullException(nameof(name));
                TaskType = taskType ?? throw new ArgumentNullException(nameof(taskType));
                Parameters = new Dictionary<string, object>();
                TaskDependencies = new List<Guid>();
                Priority = 1;
                ProcessingWeight = 1;
                RequiredMemoryMB = 100;
                RequiredDiskSpaceMB = 10;
                Timeout = TimeSpan.FromMinutes(5);
                Status = TaskStatus.Pending;
                Progress = 0f;
                ResultMetadata = new Dictionary<string, object>();
            }

            /// <summary>
            /// Validate task configuration;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (string.IsNullOrWhiteSpace(Name))
                    errors.Add("Task name cannot be empty");

                if (string.IsNullOrWhiteSpace(TaskType))
                    errors.Add("Task type cannot be empty");

                if (ProcessingWeight < 1)
                    errors.Add("Processing weight must be at least 1");

                if (RequiredMemoryMB < 0)
                    errors.Add("Required memory cannot be negative");

                if (RequiredDiskSpaceMB < 0)
                    errors.Add("Required disk space cannot be negative");

                if (Timeout <= TimeSpan.Zero)
                    errors.Add("Timeout must be positive");

                return errors.Count == 0;
            }

            /// <summary>
            /// Create a copy of the task;
            /// </summary>
            public ProcessingTask Clone()
            {
                var clone = new ProcessingTask(Name, TaskType)
                {
                    Description = Description,
                    InputData = InputData,
                    Priority = Priority,
                    ProcessingWeight = ProcessingWeight,
                    RequiredMemoryMB = RequiredMemoryMB,
                    RequiredDiskSpaceMB = RequiredDiskSpaceMB,
                    Timeout = Timeout;
                };

                foreach (var param in Parameters)
                    clone.Parameters[param.Key] = param.Value;

                foreach (var dependency in TaskDependencies)
                    clone.TaskDependencies.Add(dependency);

                return clone;
            }

            /// <summary>
            /// Set task parameter;
            /// </summary>
            public void SetParameter(string key, object value)
            {
                Parameters[key] = value;
            }

            /// <summary>
            /// Get task parameter with type safety;
            /// </summary>
            public T GetParameter<T>(string key, T defaultValue = default)
            {
                if (Parameters.TryGetValue(key, out var value) && value is T typedValue)
                    return typedValue;

                return defaultValue;
            }
        }

        /// <summary>
        /// Retry policy for failed tasks;
        /// </summary>
        public class RetryPolicy;
        {
            /// <summary>
            /// Maximum retry attempts;
            /// </summary>
            public int MaxRetryCount { get; set; }

            /// <summary>
            /// Retry delay strategy;
            /// </summary>
            public RetryDelayStrategy DelayStrategy { get; set; }

            /// <summary>
            /// Base delay between retries;
            /// </summary>
            public TimeSpan BaseDelay { get; set; }

            /// <summary>
            /// Maximum delay between retries;
            /// </summary>
            public TimeSpan MaxDelay { get; set; }

            /// <summary>
            /// Backoff multiplier;
            /// </summary>
            public double BackoffMultiplier { get; set; }

            /// <summary>
            /// Retry on specific exception types;
            /// </summary>
            public List<Type> RetryOnExceptions { get; }

            /// <summary>
            /// Do not retry on specific exception types;
            /// </summary>
            public List<Type> DoNotRetryOnExceptions { get; }

            public RetryPolicy()
            {
                MaxRetryCount = 3;
                DelayStrategy = RetryDelayStrategy.ExponentialBackoff;
                BaseDelay = TimeSpan.FromSeconds(1);
                MaxDelay = TimeSpan.FromMinutes(5);
                BackoffMultiplier = 2.0;
                RetryOnExceptions = new List<Type>();
                DoNotRetryOnExceptions = new List<Type>();
            }

            /// <summary>
            /// Calculate delay for retry attempt;
            /// </summary>
            public TimeSpan CalculateDelay(int retryCount)
            {
                switch (DelayStrategy)
                {
                    case RetryDelayStrategy.Fixed:
                        return BaseDelay;

                    case RetryDelayStrategy.Linear:
                        var linearDelay = TimeSpan.FromTicks(BaseDelay.Ticks * (retryCount + 1));
                        return linearDelay > MaxDelay ? MaxDelay : linearDelay;

                    case RetryDelayStrategy.ExponentialBackoff:
                        var exponentialTicks = (long)(BaseDelay.Ticks * Math.Pow(BackoffMultiplier, retryCount));
                        var exponentialDelay = TimeSpan.FromTicks(exponentialTicks);
                        return exponentialDelay > MaxDelay ? MaxDelay : exponentialDelay;

                    case RetryDelayStrategy.Random:
                        var random = new Random();
                        var randomFactor = 0.8 + (random.NextDouble() * 0.4); // 0.8 to 1.2;
                        var randomDelay = TimeSpan.FromTicks((long)(BaseDelay.Ticks * randomFactor * (retryCount + 1)));
                        return randomDelay > MaxDelay ? MaxDelay : randomDelay;

                    default:
                        return BaseDelay;
                }
            }

            /// <summary>
            /// Check if exception should be retried;
            /// </summary>
            public bool ShouldRetry(Exception exception, int currentRetryCount)
            {
                if (currentRetryCount >= MaxRetryCount)
                    return false;

                var exceptionType = exception.GetType();

                // Check DoNotRetry list first;
                if (DoNotRetryOnExceptions.Any(t => t.IsAssignableFrom(exceptionType)))
                    return false;

                // If RetryOn list is empty, retry all except those in DoNotRetry
                if (RetryOnExceptions.Count == 0)
                    return true;

                // Otherwise only retry if in RetryOn list;
                return RetryOnExceptions.Any(t => t.IsAssignableFrom(exceptionType));
            }

            /// <summary>
            /// Create a copy of the retry policy;
            /// </summary>
            public RetryPolicy Clone()
            {
                var clone = new RetryPolicy;
                {
                    MaxRetryCount = MaxRetryCount,
                    DelayStrategy = DelayStrategy,
                    BaseDelay = BaseDelay,
                    MaxDelay = MaxDelay,
                    BackoffMultiplier = BackoffMultiplier;
                };

                foreach (var type in RetryOnExceptions)
                    clone.RetryOnExceptions.Add(type);

                foreach (var type in DoNotRetryOnExceptions)
                    clone.DoNotRetryOnExceptions.Add(type);

                return clone;
            }
        }

        /// <summary>
        /// Notification settings for job completion;
        /// </summary>
        public class NotificationSettings;
        {
            /// <summary>
            /// Send notification on job completion;
            /// </summary>
            public bool NotifyOnCompletion { get; set; }

            /// <summary>
            /// Send notification on job failure;
            /// </summary>
            public bool NotifyOnFailure { get; set; }

            /// <summary>
            /// Send notification on job cancellation;
            /// </summary>
            public bool NotifyOnCancellation { get; set; }

            /// <summary>
            /// Send progress notifications;
            /// </summary>
            public bool NotifyOnProgress { get; set; }

            /// <summary>
            /// Progress notification interval (percentage)
            /// </summary>
            public float ProgressNotificationInterval { get; set; }

            /// <summary>
            /// Notification channels;
            /// </summary>
            public List<NotificationChannel> Channels { get; }

            /// <summary>
            /// Recipient emails;
            /// </summary>
            public List<string> EmailRecipients { get; }

            /// <summary>
            /// Webhook URLs;
            /// </summary>
            public List<string> WebhookUrls { get; }

            public NotificationSettings()
            {
                NotifyOnCompletion = true;
                NotifyOnFailure = true;
                NotifyOnCancellation = false;
                NotifyOnProgress = false;
                ProgressNotificationInterval = 10f; // 10%
                Channels = new List<NotificationChannel>();
                EmailRecipients = new List<string>();
                WebhookUrls = new List<string>();
            }

            public NotificationSettings Clone()
            {
                var clone = new NotificationSettings;
                {
                    NotifyOnCompletion = NotifyOnCompletion,
                    NotifyOnFailure = NotifyOnFailure,
                    NotifyOnCancellation = NotifyOnCancellation,
                    NotifyOnProgress = NotifyOnProgress,
                    ProgressNotificationInterval = ProgressNotificationInterval;
                };

                foreach (var channel in Channels)
                    clone.Channels.Add(channel);

                foreach (var email in EmailRecipients)
                    clone.EmailRecipients.Add(email);

                foreach (var webhook in WebhookUrls)
                    clone.WebhookUrls.Add(webhook);

                return clone;
            }
        }

        /// <summary>
        /// Job execution statistics;
        /// </summary>
        public class JobStatistics;
        {
            /// <summary>
            /// Total tasks processed;
            /// </summary>
            public int TotalTasks { get; internal set; }

            /// <summary>
            /// Completed tasks;
            /// </summary>
            public int CompletedTasks { get; internal set; }

            /// <summary>
            /// Failed tasks;
            /// </summary>
            public int FailedTasks { get; internal set; }

            /// <summary>
            /// Cancelled tasks;
            /// </summary>
            public int CancelledTasks { get; internal set; }

            /// <summary>
            /// Total retry attempts;
            /// </summary>
            public int TotalRetries { get; internal set; }

            /// <summary>
            /// Total processing time;
            /// </summary>
            public TimeSpan TotalProcessingTime { get; internal set; }

            /// <summary>
            /// Average task processing time;
            /// </summary>
            public TimeSpan AverageTaskTime { get; internal set; }

            /// <summary>
            /// Peak memory usage in MB;
            /// </summary>
            public long PeakMemoryUsageMB { get; internal set; }

            /// <summary>
            /// Peak CPU usage percentage;
            /// </summary>
            public float PeakCpuUsage { get; internal set; }

            /// <summary>
            /// Total data processed in MB;
            /// </summary>
            public long TotalDataProcessedMB { get; internal set; }

            public JobStatistics()
            {
                TotalTasks = 0;
                CompletedTasks = 0;
                FailedTasks = 0;
                CancelledTasks = 0;
                TotalRetries = 0;
                TotalProcessingTime = TimeSpan.Zero;
                AverageTaskTime = TimeSpan.Zero;
                PeakMemoryUsageMB = 0;
                PeakCpuUsage = 0;
                TotalDataProcessedMB = 0;
            }

            /// <summary>
            /// Reset statistics;
            /// </summary>
            public void Reset()
            {
                TotalTasks = 0;
                CompletedTasks = 0;
                FailedTasks = 0;
                CancelledTasks = 0;
                TotalRetries = 0;
                TotalProcessingTime = TimeSpan.Zero;
                AverageTaskTime = TimeSpan.Zero;
                PeakMemoryUsageMB = 0;
                PeakCpuUsage = 0;
                TotalDataProcessedMB = 0;
            }
        }

        /// <summary>
        /// Processor configuration;
        /// </summary>
        public class ProcessorConfiguration;
        {
            /// <summary>
            /// Maximum concurrent jobs;
            /// </summary>
            public int MaxConcurrentJobs { get; set; }

            /// <summary>
            /// Maximum total degree of parallelism;
            /// </summary>
            public int MaxTotalParallelism { get; set; }

            /// <summary>
            /// Maximum memory usage in MB;
            /// </summary>
            public long MaxMemoryUsageMB { get; set; }

            /// <summary>
            /// Temporary directory for processing;
            /// </summary>
            public string TempDirectory { get; set; }

            /// <summary>
            /// Enable disk space monitoring;
            /// </summary>
            public bool MonitorDiskSpace { get; set; }

            /// <summary>
            /// Minimum free disk space in MB;
            /// </summary>
            public long MinFreeDiskSpaceMB { get; set; }

            /// <summary>
            /// Enable CPU usage monitoring;
            /// </summary>
            public bool MonitorCpuUsage { get; set; }

            /// <summary>
            /// Maximum CPU usage percentage;
            /// </summary>
            public float MaxCpuUsage { get; set; }

            /// <summary>
            /// Enable automatic cleanup;
            /// </summary>
            public bool AutoCleanup { get; set; }

            /// <summary>
            /// Cleanup older than days;
            /// </summary>
            public int CleanupOlderThanDays { get; set; }

            /// <summary>
            /// Enable job history;
            /// </summary>
            public bool EnableJobHistory { get; set; }

            /// <summary>
            /// Maximum history entries;
            /// </summary>
            public int MaxHistoryEntries { get; set; }

            /// <summary>
            /// Enable performance logging;
            /// </summary>
            public bool EnablePerformanceLogging { get; set; }

            /// <summary>
            /// Performance logging interval;
            /// </summary>
            public TimeSpan PerformanceLoggingInterval { get; set; }

            public ProcessorConfiguration()
            {
                MaxConcurrentJobs = 5;
                MaxTotalParallelism = Environment.ProcessorCount * 2;
                MaxMemoryUsageMB = 1024 * 8; // 8 GB;
                TempDirectory = Path.Combine(Path.GetTempPath(), "NEDA_BatchProcessor");
                MonitorDiskSpace = true;
                MinFreeDiskSpaceMB = 1024; // 1 GB;
                MonitorCpuUsage = true;
                MaxCpuUsage = 80f; // 80%
                AutoCleanup = true;
                CleanupOlderThanDays = 7;
                EnableJobHistory = true;
                MaxHistoryEntries = 1000;
                EnablePerformanceLogging = true;
                PerformanceLoggingInterval = TimeSpan.FromMinutes(5);
            }

            /// <summary>
            /// Validate configuration;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (MaxConcurrentJobs < 1)
                    errors.Add("MaxConcurrentJobs must be at least 1");

                if (MaxTotalParallelism < 1)
                    errors.Add("MaxTotalParallelism must be at least 1");

                if (MaxMemoryUsageMB < 100)
                    errors.Add("MaxMemoryUsageMB must be at least 100 MB");

                if (string.IsNullOrWhiteSpace(TempDirectory))
                    errors.Add("TempDirectory cannot be empty");

                if (MinFreeDiskSpaceMB < 100)
                    errors.Add("MinFreeDiskSpaceMB must be at least 100 MB");

                if (MaxCpuUsage <= 0 || MaxCpuUsage > 100)
                    errors.Add("MaxCpuUsage must be between 1 and 100");

                if (CleanupOlderThanDays < 0)
                    errors.Add("CleanupOlderThanDays cannot be negative");

                if (MaxHistoryEntries < 0)
                    errors.Add("MaxHistoryEntries cannot be negative");

                if (PerformanceLoggingInterval <= TimeSpan.Zero)
                    errors.Add("PerformanceLoggingInterval must be positive");

                return errors.Count == 0;
            }
        }

        /// <summary>
        /// Task processor interface;
        /// </summary>
        public interface ITaskProcessor;
        {
            /// <summary>
            /// Process a single task;
            /// </summary>
            Task<ProcessingResult> ProcessAsync(ProcessingTask task, CancellationToken cancellationToken);

            /// <summary>
            /// Validate task parameters;
            /// </summary>
            bool ValidateTask(ProcessingTask task, out List<string> errors);

            /// <summary>
            /// Get processor capabilities;
            /// </summary>
            ProcessorCapabilities GetCapabilities();
        }

        /// <summary>
        /// Processing result;
        /// </summary>
        public class ProcessingResult;
        {
            /// <summary>
            /// Success status;
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// Output data;
            /// </summary>
            public object Output { get; set; }

            /// <summary>
            /// Error message if failed;
            /// </summary>
            public string ErrorMessage { get; set; }

            /// <summary>
            /// Exception if failed;
            /// </summary>
            public Exception Exception { get; set; }

            /// <summary>
            /// Processing statistics;
            /// </summary>
            public ProcessingStatistics Statistics { get; set; }

            /// <summary>
            /// Result metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; set; }

            public ProcessingResult()
            {
                Success = false;
                Metadata = new Dictionary<string, object>();
                Statistics = new ProcessingStatistics();
            }

            public static ProcessingResult SuccessResult(object output = null)
            {
                return new ProcessingResult;
                {
                    Success = true,
                    Output = output;
                };
            }

            public static ProcessingResult FailureResult(string error, Exception exception = null)
            {
                return new ProcessingResult;
                {
                    Success = false,
                    ErrorMessage = error,
                    Exception = exception;
                };
            }
        }

        /// <summary>
        /// Processing statistics;
        /// </summary>
        public class ProcessingStatistics;
        {
            public TimeSpan ProcessingTime { get; set; }
            public long MemoryUsedMB { get; set; }
            public float CpuUsage { get; set; }
            public long DiskReadMB { get; set; }
            public long DiskWriteMB { get; set; }
            public int ItemsProcessed { get; set; }
            public int ItemsFailed { get; set; }
        }

        /// <summary>
        /// Processor capabilities;
        /// </summary>
        public class ProcessorCapabilities;
        {
            public string ProcessorType { get; set; }
            public string[] SupportedTaskTypes { get; set; }
            public long MaxMemoryMB { get; set; }
            public int MaxParallelism { get; set; }
            public bool SupportsCancellation { get; set; }
            public bool SupportsProgressReporting { get; set; }
            public bool SupportsPartialResults { get; set; }
        }

        /// <summary>
        /// Job queue item;
        /// </summary>
        private class JobQueueItem;
        {
            public BatchJob Job { get; }
            public TaskCompletionSource<JobResult> CompletionSource { get; }
            public CancellationTokenSource CancellationTokenSource { get; }
            public DateTime QueueTime { get; }

            public JobQueueItem(BatchJob job)
            {
                Job = job;
                CompletionSource = new TaskCompletionSource<JobResult>();
                CancellationTokenSource = new CancellationTokenSource();
                QueueTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Job execution context;
        /// </summary>
        private class JobExecutionContext;
        {
            public BatchJob Job { get; }
            public JobQueueItem QueueItem { get; }
            public Dictionary<Guid, TaskExecutionContext> TaskContexts { get; }
            public ConcurrentDictionary<Guid, float> TaskProgress { get; }
            public DateTime StartTime { get; }
            public volatile bool IsCancelled;
            public volatile bool IsCompleted;
            public int ActiveTaskCount;
            public object Lock = new object();

            public JobExecutionContext(BatchJob job, JobQueueItem queueItem)
            {
                Job = job;
                QueueItem = queueItem;
                TaskContexts = new Dictionary<Guid, TaskExecutionContext>();
                TaskProgress = new ConcurrentDictionary<Guid, float>();
                StartTime = DateTime.UtcNow;
                IsCancelled = false;
                IsCompleted = false;
                ActiveTaskCount = 0;
            }
        }

        /// <summary>
        /// Task execution context;
        /// </summary>
        private class TaskExecutionContext;
        {
            public ProcessingTask Task { get; }
            public Task<ProcessingResult> ExecutionTask { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; }
            public int RetryCount { get; set; }
            public List<Exception> Errors { get; }
            public volatile bool IsCompleted;
            public volatile bool IsRunning;

            public TaskExecutionContext(ProcessingTask task)
            {
                Task = task;
                CancellationTokenSource = new CancellationTokenSource();
                RetryCount = 0;
                Errors = new List<Exception>();
                IsCompleted = false;
                IsRunning = false;
            }
        }

        /// <summary>
        /// Job result;
        /// </summary>
        public class JobResult;
        {
            public Guid JobId { get; set; }
            public string JobName { get; set; }
            public JobStatus Status { get; set; }
            public float Progress { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? CompletionTime { get; set; }
            public TimeSpan? ExecutionTime { get; set; }
            public Exception Error { get; set; }
            public JobStatistics Statistics { get; set; }
            public Dictionary<Guid, TaskResult> TaskResults { get; set; }

            public JobResult()
            {
                TaskResults = new Dictionary<Guid, TaskResult>();
                Statistics = new JobStatistics();
            }
        }

        /// <summary>
        /// Task result;
        /// </summary>
        public class TaskResult;
        {
            public Guid TaskId { get; set; }
            public string TaskName { get; set; }
            public TaskStatus Status { get; set; }
            public float Progress { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? CompletionTime { get; set; }
            public TimeSpan? ExecutionTime { get; set; }
            public Exception Error { get; set; }
            public int RetryCount { get; set; }
            public object Output { get; set; }
            public Dictionary<string, object> Metadata { get; set; }

            public TaskResult()
            {
                Metadata = new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Job history entry
        /// </summary>
        public class JobHistoryEntry
        {
            public Guid JobId { get; set; }
            public string JobName { get; set; }
            public JobStatus Status { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? CompletionTime { get; set; }
            public TimeSpan? ExecutionTime { get; set; }
            public int TotalTasks { get; set; }
            public int CompletedTasks { get; set; }
            public int FailedTasks { get; set; }
            public Exception Error { get; set; }
        }

        /// <summary>
        /// Enums;
        /// </summary>
        public enum JobStatus;
        {
            Pending,
            Queued,
            Running,
            Paused,
            Completed,
            Failed,
            Cancelled,
            TimedOut;
        }

        public enum TaskStatus;
        {
            Pending,
            Running,
            Completed,
            Failed,
            Cancelled,
            WaitingForDependencies,
            Retrying;
        }

        public enum RetryDelayStrategy;
        {
            Fixed,
            Linear,
            ExponentialBackoff,
            Random;
        }

        public enum NotificationChannel;
        {
            Email,
            Webhook,
            Database,
            File,
            EventBus;
        }

        #endregion;

        #region Fields;

        private readonly ConcurrentDictionary<Guid, JobExecutionContext> _activeJobs;
        private readonly PriorityQueue<JobQueueItem, int> _jobQueue;
        private readonly Dictionary<string, ITaskProcessor> _taskProcessors;
        private readonly ConcurrentDictionary<Guid, JobHistoryEntry> _jobHistory;
        private readonly ProcessorConfiguration _configuration;
        private readonly ILogger _logger;

        private readonly object _queueLock = new object();
        private readonly object _processorLock = new object();
        private readonly SemaphoreSlim _concurrentJobsSemaphore;
        private readonly SemaphoreSlim _parallelismSemaphore;

        private CancellationTokenSource _globalCancellationTokenSource;
        private Task _queueProcessorTask;
        private Task _monitoringTask;
        private Task _cleanupTask;

        private volatile bool _isRunning;
        private volatile bool _isDisposed;
        private int _totalJobsProcessed;
        private long _totalTasksProcessed;

        // Performance monitoring;
        private PerformanceMonitor _performanceMonitor;
        private ResourceMonitor _resourceMonitor;

        #endregion;

        #region Properties;

        /// <summary>
        /// Processor configuration;
        /// </summary>
        public ProcessorConfiguration Configuration => _configuration;

        /// <summary>
        /// Is processor running;
        /// </summary>
        public bool IsRunning => _isRunning && !_isDisposed;

        /// <summary>
        /// Active job count;
        /// </summary>
        public int ActiveJobCount => _activeJobs.Count;

        /// <summary>
        /// Queued job count;
        /// </summary>
        public int QueuedJobCount;
        {
            get;
            {
                lock (_queueLock)
                {
                    return _jobQueue.Count;
                }
            }
        }

        /// <summary>
        /// Total jobs processed;
        /// </summary>
        public int TotalJobsProcessed => _totalJobsProcessed;

        /// <summary>
        /// Total tasks processed;
        /// </summary>
        public long TotalTasksProcessed => _totalTasksProcessed;

        /// <summary>
        /// Registered task processor count;
        /// </summary>
        public int ProcessorCount => _taskProcessors.Count;

        /// <summary>
        /// Available memory in MB;
        /// </summary>
        public long AvailableMemoryMB => _resourceMonitor?.AvailableMemoryMB ?? 0;

        /// <summary>
        /// Available disk space in MB;
        /// </summary>
        public long AvailableDiskSpaceMB => _resourceMonitor?.AvailableDiskSpaceMB ?? 0;

        /// <summary>
        /// Current CPU usage percentage;
        /// </summary>
        public float CurrentCpuUsage => _resourceMonitor?.CurrentCpuUsage ?? 0;

        #endregion;

        #region Events;

        /// <summary>
        /// Job started event;
        /// </summary>
        public event EventHandler<BatchJob> OnJobStarted;

        /// <summary>
        /// Job completed event;
        /// </summary>
        public event EventHandler<JobResult> OnJobCompleted;

        /// <summary>
        /// Job progress changed event;
        /// </summary>
        public event EventHandler<JobProgressEventArgs> OnJobProgressChanged;

        /// <summary>
        /// Task started event;
        /// </summary>
        public event EventHandler<ProcessingTask> OnTaskStarted;

        /// <summary>
        /// Task completed event;
        /// </summary>
        public event EventHandler<TaskResult> OnTaskCompleted;

        /// <summary>
        /// Task progress changed event;
        /// </summary>
        public event EventHandler<TaskProgressEventArgs> OnTaskProgressChanged;

        /// <summary>
        /// Processor error event;
        /// </summary>
        public event EventHandler<ProcessorErrorEventArgs> OnProcessorError;

        /// <summary>
        /// Resource warning event;
        /// </summary>
        public event EventHandler<ResourceWarningEventArgs> OnResourceWarning;

        /// <summary>
        /// Queue status changed event;
        /// </summary>
        public event EventHandler<QueueStatusEventArgs> OnQueueStatusChanged;

        /// <summary>
        /// Event args classes;
        /// </summary>
        public class JobProgressEventArgs : EventArgs;
        {
            public Guid JobId { get; set; }
            public string JobName { get; set; }
            public float Progress { get; set; }
            public JobStatus Status { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class TaskProgressEventArgs : EventArgs;
        {
            public Guid JobId { get; set; }
            public Guid TaskId { get; set; }
            public string TaskName { get; set; }
            public float Progress { get; set; }
            public TaskStatus Status { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ProcessorErrorEventArgs : EventArgs;
        {
            public Guid? JobId { get; set; }
            public Guid? TaskId { get; set; }
            public Exception Error { get; set; }
            public string Context { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ResourceWarningEventArgs : EventArgs;
        {
            public ResourceType ResourceType { get; set; }
            public float CurrentUsage { get; set; }
            public float Threshold { get; set; }
            public string WarningMessage { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class QueueStatusEventArgs : EventArgs;
        {
            public int QueuedJobs { get; set; }
            public int ActiveJobs { get; set; }
            public int TotalJobs { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public enum ResourceType;
        {
            Memory,
            DiskSpace,
            Cpu,
            Network;
        }

        #endregion;

        #region Constructor;

        /// <summary>
        /// Create batch processor with default configuration;
        /// </summary>
        public BatchProcessor() : this(new ProcessorConfiguration())
        {
        }

        /// <summary>
        /// Create batch processor with custom configuration;
        /// </summary>
        public BatchProcessor(ProcessorConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            // Validate configuration;
            if (!configuration.Validate(out var errors))
            {
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", errors)}");
            }

            _activeJobs = new ConcurrentDictionary<Guid, JobExecutionContext>();
            _jobQueue = new PriorityQueue<JobQueueItem, int>();
            _taskProcessors = new Dictionary<string, ITaskProcessor>(StringComparer.OrdinalIgnoreCase);
            _jobHistory = new ConcurrentDictionary<Guid, JobHistoryEntry>();

            _concurrentJobsSemaphore = new SemaphoreSlim(configuration.MaxConcurrentJobs, configuration.MaxConcurrentJobs);
            _parallelismSemaphore = new SemaphoreSlim(configuration.MaxTotalParallelism, configuration.MaxTotalParallelism);

            _globalCancellationTokenSource = new CancellationTokenSource();

            _logger = LogManager.GetLogger("BatchProcessor");
            _performanceMonitor = new PerformanceMonitor();
            _resourceMonitor = new ResourceMonitor();

            _isRunning = false;
            _isDisposed = false;
            _totalJobsProcessed = 0;
            _totalTasksProcessed = 0;

            // Create temp directory;
            Directory.CreateDirectory(_configuration.TempDirectory);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Start batch processor;
        /// </summary>
        public void Start()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(BatchProcessor));

            if (_isRunning)
                return;

            _isRunning = true;
            _globalCancellationTokenSource = new CancellationTokenSource();

            // Start queue processor;
            _queueProcessorTask = Task.Run(() => ProcessJobQueueAsync(_globalCancellationTokenSource.Token));

            // Start monitoring task;
            _monitoringTask = Task.Run(() => MonitorResourcesAsync(_globalCancellationTokenSource.Token));

            // Start cleanup task;
            _cleanupTask = Task.Run(() => PerformCleanupAsync(_globalCancellationTokenSource.Token));

            _logger.Info($"BatchProcessor started with configuration: MaxConcurrentJobs={_configuration.MaxConcurrentJobs}, MaxTotalParallelism={_configuration.MaxTotalParallelism}");
        }

        /// <summary>
        /// Stop batch processor;
        /// </summary>
        public async Task StopAsync(bool waitForCompletion = false)
        {
            if (!_isRunning || _isDisposed)
                return;

            _logger.Info("Stopping BatchProcessor...");

            // Signal cancellation;
            _globalCancellationTokenSource.Cancel();

            if (waitForCompletion)
            {
                _logger.Info("Waiting for active jobs to complete...");

                // Wait for active jobs to complete with timeout;
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
                var completionTask = Task.WhenAll(
                    WaitForActiveJobsCompletionAsync(),
                    _queueProcessorTask,
                    _monitoringTask,
                    _cleanupTask;
                );

                await Task.WhenAny(completionTask, timeoutTask);

                if (timeoutTask.IsCompleted)
                {
                    _logger.Warning("Timeout while waiting for jobs to complete");
                }
            }

            _isRunning = false;

            // Cancel all active jobs;
            foreach (var context in _activeJobs.Values)
            {
                context.QueueItem.CancellationTokenSource.Cancel();
                context.IsCancelled = true;
            }

            _logger.Info("BatchProcessor stopped");
        }

        /// <summary>
        /// Submit job for processing;
        /// </summary>
        public Task<JobResult> SubmitJobAsync(BatchJob job)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(BatchProcessor));

            if (job == null)
                throw new ArgumentNullException(nameof(job));

            // Validate job;
            if (!job.Validate(out var errors))
            {
                throw new ArgumentException($"Invalid job: {string.Join(", ", errors)}");
            }

            // Check if job has expired;
            if (job.ExpirationTime.HasValue && DateTime.UtcNow > job.ExpirationTime.Value)
            {
                throw new InvalidOperationException($"Job '{job.Name}' has expired");
            }

            lock (_queueLock)
            {
                // Check if job is already queued or running;
                if (_activeJobs.ContainsKey(job.Id) || _jobQueue.UnorderedItems.Any(item => item.Element.Job.Id == job.Id))
                {
                    throw new InvalidOperationException($"Job with ID {job.Id} is already queued or running");
                }

                var queueItem = new JobQueueItem(job);
                _jobQueue.Enqueue(queueItem, -job.Priority); // Negative for max-heap behavior;

                _logger.Info($"Job '{job.Name}' submitted with {job.Tasks.Count} tasks, Priority={job.Priority}");
                OnQueueStatusChanged?.Invoke(this, new QueueStatusEventArgs;
                {
                    QueuedJobs = _jobQueue.Count,
                    ActiveJobs = _activeJobs.Count,
                    TotalJobs = _jobQueue.Count + _activeJobs.Count,
                    Timestamp = DateTime.UtcNow;
                });

                return queueItem.CompletionSource.Task;
            }
        }

        /// <summary>
        /// Cancel job;
        /// </summary>
        public bool CancelJob(Guid jobId)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(BatchProcessor));

            lock (_queueLock)
            {
                // Check if job is in queue;
                var queuedItem = _jobQueue.UnorderedItems.FirstOrDefault(item => item.Element.Job.Id == jobId).Element;
                if (queuedItem != null)
                {
                    _jobQueue.Remove(queuedItem);
                    queuedItem.CompletionSource.SetCanceled();
                    _logger.Info($"Job {jobId} removed from queue");
                    return true;
                }
            }

            // Check if job is active;
            if (_activeJobs.TryGetValue(jobId, out var context))
            {
                context.IsCancelled = true;
                context.QueueItem.CancellationTokenSource.Cancel();
                _logger.Info($"Job {jobId} cancellation requested");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pause job;
        /// </summary>
        public bool PauseJob(Guid jobId)
        {
            if (_activeJobs.TryGetValue(jobId, out var context) && !context.IsCancelled && !context.IsCompleted)
            {
                context.Job.Status = JobStatus.Paused;

                // Pause all running tasks;
                foreach (var taskContext in context.TaskContexts.Values.Where(tc => tc.IsRunning && !tc.IsCompleted))
                {
                    taskContext.CancellationTokenSource.Cancel();
                }

                _logger.Info($"Job {jobId} paused");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resume job;
        /// </summary>
        public bool ResumeJob(Guid jobId)
        {
            if (_activeJobs.TryGetValue(jobId, out var context) && context.Job.Status == JobStatus.Paused)
            {
                context.Job.Status = JobStatus.Running;

                // Continue processing remaining tasks;
                Task.Run(() => ContinueJobProcessingAsync(context));

                _logger.Info($"Job {jobId} resumed");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get job status;
        /// </summary>
        public JobStatus GetJobStatus(Guid jobId)
        {
            if (_activeJobs.TryGetValue(jobId, out var context))
                return context.Job.Status;

            lock (_queueLock)
            {
                var queuedItem = _jobQueue.UnorderedItems.FirstOrDefault(item => item.Element.Job.Id == jobId).Element;
                if (queuedItem != null)
                    return JobStatus.Queued;
            }

            if (_jobHistory.TryGetValue(jobId, out var history))
                return history.Status;

            return JobStatus.Pending;
        }

        /// <summary>
        /// Get job progress;
        /// </summary>
        public float GetJobProgress(Guid jobId)
        {
            if (_activeJobs.TryGetValue(jobId, out var context))
                return context.Job.Progress;

            return 0f;
        }

        /// <summary>
        /// Get job result;
        /// </summary>
        public JobResult GetJobResult(Guid jobId)
        {
            if (_activeJobs.TryGetValue(jobId, out var context))
            {
                return CreateJobResult(context);
            }

            return null;
        }

        /// <summary>
        /// Get active jobs;
        /// </summary>
        public List<BatchJob> GetActiveJobs()
        {
            return _activeJobs.Values.Select(context => context.Job).ToList();
        }

        /// <summary>
        /// Get queued jobs;
        /// </summary>
        public List<BatchJob> GetQueuedJobs()
        {
            lock (_queueLock)
            {
                return _jobQueue.UnorderedItems.Select(item => item.Element.Job).ToList();
            }
        }

        /// <summary>
        /// Get job history;
        /// </summary>
        public List<JobHistoryEntry> GetJobHistory(int count = 100)
        {
            return _jobHistory.Values;
                .OrderByDescending(h => h.StartTime)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Clear job history;
        /// </summary>
        public void ClearHistory()
        {
            _jobHistory.Clear();
            _logger.Info("Job history cleared");
        }

        /// <summary>
        /// Register task processor;
        /// </summary>
        public void RegisterProcessor(string taskType, ITaskProcessor processor)
        {
            if (string.IsNullOrWhiteSpace(taskType))
                throw new ArgumentException("Task type cannot be empty", nameof(taskType));

            if (processor == null)
                throw new ArgumentNullException(nameof(processor));

            lock (_processorLock)
            {
                if (_taskProcessors.ContainsKey(taskType))
                {
                    _logger.Warning($"Overwriting existing processor for task type: {taskType}");
                }

                _taskProcessors[taskType] = processor;
                _logger.Info($"Registered processor for task type: {taskType}");
            }
        }

        /// <summary>
        /// Unregister task processor;
        /// </summary>
        public bool UnregisterProcessor(string taskType)
        {
            lock (_processorLock)
            {
                var removed = _taskProcessors.Remove(taskType);
                if (removed)
                {
                    _logger.Info($"Unregistered processor for task type: {taskType}");
                }
                return removed;
            }
        }

        /// <summary>
        /// Get processor for task type;
        /// </summary>
        public ITaskProcessor GetProcessor(string taskType)
        {
            lock (_processorLock)
            {
                _taskProcessors.TryGetValue(taskType, out var processor);
                return processor;
            }
        }

        /// <summary>
        /// Get all registered task types;
        /// </summary>
        public List<string> GetRegisteredTaskTypes()
        {
            lock (_processorLock)
            {
                return _taskProcessors.Keys.ToList();
            }
        }

        /// <summary>
        /// Validate job before submission;
        /// </summary>
        public bool ValidateJob(BatchJob job, out List<string> errors)
        {
            errors = new List<string>();

            if (!job.Validate(out errors))
                return false;

            // Validate each task has a processor;
            foreach (var task in job.Tasks)
            {
                var processor = GetProcessor(task.TaskType);
                if (processor == null)
                {
                    errors.Add($"No processor registered for task type: {task.TaskType}");
                    continue;
                }

                if (!processor.ValidateTask(task, out var taskErrors))
                {
                    errors.AddRange(taskErrors.Select(e => $"Task '{task.Name}': {e}"));
                }
            }

            return errors.Count == 0;
        }

        /// <summary>
        /// Get processor statistics;
        /// </summary>
        public ProcessorStatistics GetStatistics()
        {
            return new ProcessorStatistics;
            {
                TotalJobsProcessed = _totalJobsProcessed,
                TotalTasksProcessed = _totalTasksProcessed,
                ActiveJobs = _activeJobs.Count,
                QueuedJobs = QueuedJobCount,
                AvailableMemoryMB = AvailableMemoryMB,
                AvailableDiskSpaceMB = AvailableDiskSpaceMB,
                CurrentCpuUsage = CurrentCpuUsage,
                RegisteredProcessors = _taskProcessors.Count,
                Uptime = _performanceMonitor?.Uptime ?? TimeSpan.Zero;
            };
        }

        /// <summary>
        /// Processor statistics;
        /// </summary>
        public class ProcessorStatistics;
        {
            public int TotalJobsProcessed { get; set; }
            public long TotalTasksProcessed { get; set; }
            public int ActiveJobs { get; set; }
            public int QueuedJobs { get; set; }
            public long AvailableMemoryMB { get; set; }
            public long AvailableDiskSpaceMB { get; set; }
            public float CurrentCpuUsage { get; set; }
            public int RegisteredProcessors { get; set; }
            public TimeSpan Uptime { get; set; }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Process job queue;
        /// </summary>
        private async Task ProcessJobQueueAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Job queue processor started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for available slot;
                    await _concurrentJobsSemaphore.WaitAsync(cancellationToken);

                    JobQueueItem queueItem = null;

                    lock (_queueLock)
                    {
                        if (_jobQueue.Count > 0)
                        {
                            queueItem = _jobQueue.Dequeue();
                        }
                    }

                    if (queueItem == null)
                    {
                        _concurrentJobsSemaphore.Release();
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    // Check job dependencies;
                    if (queueItem.Job.Dependencies.Count > 0)
                    {
                        var unresolvedDependencies = queueItem.Job.Dependencies;
                            .Where(depId => !IsJobCompleted(depId))
                            .ToList();

                        if (unresolvedDependencies.Count > 0)
                        {
                            _logger.Info($"Job '{queueItem.Job.Name}' waiting for dependencies: {string.Join(", ", unresolvedDependencies)}");

                            // Re-queue with lower priority;
                            lock (_queueLock)
                            {
                                _jobQueue.Enqueue(queueItem, -queueItem.Job.Priority - 1000);
                            }

                            _concurrentJobsSemaphore.Release();
                            await Task.Delay(5000, cancellationToken);
                            continue;
                        }
                    }

                    // Check if job has expired;
                    if (queueItem.Job.ExpirationTime.HasValue && DateTime.UtcNow > queueItem.Job.ExpirationTime.Value)
                    {
                        _logger.Warning($"Job '{queueItem.Job.Name}' expired, skipping");
                        queueItem.CompletionSource.SetException(new TimeoutException("Job expired before execution"));
                        _concurrentJobsSemaphore.Release();
                        continue;
                    }

                    // Execute job;
                    _ = Task.Run(() => ExecuteJobAsync(queueItem, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in queue processor: {ex.Message}", ex);
                    await Task.Delay(5000, cancellationToken);
                }
            }

            _logger.Info("Job queue processor stopped");
        }

        /// <summary>
        /// Execute a job;
        /// </summary>
        private async Task ExecuteJobAsync(JobQueueItem queueItem, CancellationToken cancellationToken)
        {
            var job = queueItem.Job;
            var jobCancellationToken = queueItem.CancellationTokenSource.Token;
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, jobCancellationToken).Token;

            JobExecutionContext context = null;

            try
            {
                // Create execution context;
                context = new JobExecutionContext(job, queueItem);
                _activeJobs[job.Id] = context;

                // Update job status;
                job.Status = JobStatus.Running;
                job.StartTime = DateTime.UtcNow;
                job.Progress = 0f;

                _logger.Info($"Starting job '{job.Name}' with {job.Tasks.Count} tasks");
                OnJobStarted?.Invoke(this, job);

                // Check resource availability;
                if (!await CheckResourceAvailabilityAsync(job))
                {
                    throw new InvalidOperationException("Insufficient resources to execute job");
                }

                // Process tasks;
                var jobResult = await ProcessJobTasksAsync(context, linkedCancellationToken);

                // Complete job;
                await CompleteJobAsync(context, jobResult, linkedCancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.Info($"Job '{job.Name}' cancelled");
                await CancelJobAsync(context, "Job cancelled by user", linkedCancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Job '{job.Name}' failed: {ex.Message}", ex);
                await FailJobAsync(context, ex, linkedCancellationToken);
            }
            finally
            {
                if (context != null)
                {
                    _activeJobs.TryRemove(job.Id, out _);
                    _concurrentJobsSemaphore.Release();

                    // Update queue status;
                    OnQueueStatusChanged?.Invoke(this, new QueueStatusEventArgs;
                    {
                        QueuedJobs = QueuedJobCount,
                        ActiveJobs = _activeJobs.Count,
                        TotalJobs = QueuedJobCount + _activeJobs.Count,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Process job tasks;
        /// </summary>
        private async Task<JobResult> ProcessJobTasksAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            var job = context.Job;
            var tasks = job.Tasks;

            // Initialize task contexts;
            foreach (var task in tasks)
            {
                context.TaskContexts[task.Id] = new TaskExecutionContext(task);
            }

            // Calculate total processing weight;
            var totalWeight = tasks.Sum(t => t.ProcessingWeight);
            var processedWeight = 0;

            // Process tasks based on dependencies and priority;
            var processedTasks = new HashSet<Guid>();
            var taskQueue = new PriorityQueue<ProcessingTask, int>();

            // Initialize queue with tasks that have no dependencies;
            foreach (var task in tasks.Where(t => t.TaskDependencies.Count == 0))
            {
                taskQueue.Enqueue(task, -task.Priority);
            }

            while (taskQueue.Count > 0 && !context.IsCancelled && !cancellationToken.IsCancellationRequested)
            {
                var task = taskQueue.Dequeue();

                if (processedTasks.Contains(task.Id))
                    continue;

                // Check if all dependencies are processed;
                var unprocessedDependencies = task.TaskDependencies;
                    .Where(depId => !processedTasks.Contains(depId))
                    .ToList();

                if (unprocessedDependencies.Count > 0)
                {
                    // Re-queue with lower priority;
                    taskQueue.Enqueue(task, -task.Priority - 1000);
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                // Execute task;
                var taskResult = await ExecuteTaskAsync(context, task, cancellationToken);
                processedTasks.Add(task.Id);
                processedWeight += task.ProcessingWeight;

                // Update job progress;
                context.Job.Progress = (float)processedWeight / totalWeight * 100f;
                OnJobProgressChanged?.Invoke(this, new JobProgressEventArgs;
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    Progress = context.Job.Progress,
                    Status = job.Status,
                    Timestamp = DateTime.UtcNow;
                });

                // Add dependent tasks to queue;
                foreach (var dependentTask in tasks.Where(t =>
                    t.TaskDependencies.Contains(task.Id) &&
                    !processedTasks.Contains(t.Id)))
                {
                    taskQueue.Enqueue(dependentTask, -dependentTask.Priority);
                }

                // Check for timeout;
                if (job.Timeout > TimeSpan.Zero &&
                    DateTime.UtcNow - context.StartTime > job.Timeout)
                {
                    throw new TimeoutException($"Job timeout after {job.Timeout}");
                }
            }

            // Create job result;
            return CreateJobResult(context);
        }

        /// <summary>
        /// Execute a single task;
        /// </summary>
        private async Task<TaskResult> ExecuteTaskAsync(JobExecutionContext jobContext, ProcessingTask task, CancellationToken cancellationToken)
        {
            var taskContext = jobContext.TaskContexts[task.Id];
            taskContext.IsRunning = true;

            var taskResult = new TaskResult;
            {
                TaskId = task.Id,
                TaskName = task.Name,
                StartTime = DateTime.UtcNow,
                Status = TaskStatus.Running,
                Progress = 0f;
            };

            _logger.Info($"Starting task '{task.Name}' (Type: {task.TaskType})");
            OnTaskStarted?.Invoke(this, task);

            // Get processor;
            var processor = GetProcessor(task.TaskType);
            if (processor == null)
            {
                throw new InvalidOperationException($"No processor registered for task type: {task.TaskType}");
            }

            // Acquire parallelism slot;
            await _parallelismSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Update task status;
                task.Status = TaskStatus.Running;
                task.StartTime = DateTime.UtcNow;

                // Execute with retry logic;
                ProcessingResult processingResult = null;
                while (taskContext.RetryCount <= jobContext.Job.RetryPolicy.MaxRetryCount)
                {
                    try
                    {
                        var taskCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken, taskContext.CancellationTokenSource.Token).Token;

                        processingResult = await processor.ProcessAsync(task, taskCancellationToken);

                        if (processingResult.Success)
                        {
                            break;
                        }
                        else if (!jobContext.Job.RetryPolicy.ShouldRetry(processingResult.Exception, taskContext.RetryCount))
                        {
                            break;
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        processingResult = ProcessingResult.FailureResult(ex.Message, ex);

                        if (!jobContext.Job.RetryPolicy.ShouldRetry(ex, taskContext.RetryCount))
                        {
                            break;
                        }
                    }

                    // Retry logic;
                    taskContext.RetryCount++;
                    taskContext.Errors.Add(processingResult?.Exception);

                    if (taskContext.RetryCount <= jobContext.Job.RetryPolicy.MaxRetryCount)
                    {
                        var delay = jobContext.Job.RetryPolicy.CalculateDelay(taskContext.RetryCount - 1);
                        _logger.Info($"Task '{task.Name}' failed, retry {taskContext.RetryCount}/{jobContext.Job.RetryPolicy.MaxRetryCount} after {delay}");

                        task.Status = TaskStatus.Retrying;
                        await Task.Delay(delay, cancellationToken);
                    }
                }

                // Update task result;
                task.CompletionTime = DateTime.UtcNow;
                task.OutputData = processingResult?.Output;
                task.ResultMetadata = processingResult?.Metadata ?? new Dictionary<string, object>();

                if (processingResult?.Success == true)
                {
                    task.Status = TaskStatus.Completed;
                    taskResult.Status = TaskStatus.Completed;
                    taskResult.Output = processingResult.Output;
                    _logger.Info($"Task '{task.Name}' completed successfully in {task.ExecutionTime}");
                }
                else;
                {
                    task.Status = TaskStatus.Failed;
                    taskResult.Status = TaskStatus.Failed;
                    taskResult.Error = processingResult?.Exception ?? new Exception("Task failed");
                    _logger.Error($"Task '{task.Name}' failed: {taskResult.Error.Message}", taskResult.Error);
                }

                taskResult.CompletionTime = task.CompletionTime;
                taskResult.ExecutionTime = task.ExecutionTime;
                taskResult.RetryCount = taskContext.RetryCount;
                taskResult.Metadata = task.ResultMetadata;

                // Update statistics;
                Interlocked.Increment(ref _totalTasksProcessed);
                jobContext.Job.Statistics.TotalTasks++;

                if (taskResult.Status == TaskStatus.Completed)
                    jobContext.Job.Statistics.CompletedTasks++;
                else;
                    jobContext.Job.Statistics.FailedTasks++;

                jobContext.Job.Statistics.TotalRetries += taskContext.RetryCount;

                // Fire completion event;
                OnTaskCompleted?.Invoke(this, taskResult);

                return taskResult;
            }
            catch (OperationCanceledException)
            {
                task.Status = TaskStatus.Cancelled;
                taskResult.Status = TaskStatus.Cancelled;
                taskResult.Error = new OperationCanceledException("Task cancelled");

                jobContext.Job.Statistics.CancelledTasks++;
                OnTaskCompleted?.Invoke(this, taskResult);

                throw;
            }
            finally
            {
                taskContext.IsRunning = false;
                taskContext.IsCompleted = true;
                _parallelismSemaphore.Release();
            }
        }

        /// <summary>
        /// Complete job processing;
        /// </summary>
        private async Task CompleteJobAsync(JobExecutionContext context, JobResult jobResult, CancellationToken cancellationToken)
        {
            var job = context.Job;
            context.IsCompleted = true;

            job.CompletionTime = DateTime.UtcNow;
            job.Status = JobStatus.Completed;
            job.Progress = 100f;

            // Calculate final statistics;
            if (job.StartTime.HasValue && job.CompletionTime.HasValue)
            {
                job.Statistics.TotalProcessingTime = job.CompletionTime.Value - job.StartTime.Value;

                var completedTasks = job.Tasks.Count(t => t.Status == TaskStatus.Completed);
                if (completedTasks > 0)
                {
                    var totalTime = job.Tasks;
                        .Where(t => t.Status == TaskStatus.Completed && t.ExecutionTime.HasValue)
                        .Sum(t => t.ExecutionTime.Value.Ticks);
                    job.Statistics.AverageTaskTime = TimeSpan.FromTicks(totalTime / completedTasks);
                }
            }

            // Update job history;
            if (_configuration.EnableJobHistory)
            {
                var historyEntry = new JobHistoryEntry
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    Status = job.Status,
                    StartTime = job.StartTime.Value,
                    CompletionTime = job.CompletionTime,
                    ExecutionTime = job.ExecutionTime,
                    TotalTasks = job.Tasks.Count,
                    CompletedTasks = job.Tasks.Count(t => t.Status == TaskStatus.Completed),
                    FailedTasks = job.Tasks.Count(t => t.Status == TaskStatus.Failed)
                };

                _jobHistory[job.Id] = historyEntry

                // Trim history if needed;
                if (_jobHistory.Count > _configuration.MaxHistoryEntries)
                {
                    var oldestEntries = _jobHistory.OrderBy(h => h.Value.StartTime)
                        .Take(_jobHistory.Count - _configuration.MaxHistoryEntries)
                        .Select(h => h.Key)
                        .ToList();

                    foreach (var oldKey in oldestEntries)
                    {
                        _jobHistory.TryRemove(oldKey, out _);
                    }
                }
            }

            // Complete job;
            context.QueueItem.CompletionSource.SetResult(jobResult);

            _totalJobsProcessed++;
            _logger.Info($"Job '{job.Name}' completed successfully in {job.ExecutionTime}");

            // Send notifications;
            if (job.NotificationSettings.NotifyOnCompletion)
            {
                await SendNotificationsAsync(job, jobResult, cancellationToken);
            }

            // Fire completion event;
            OnJobCompleted?.Invoke(this, jobResult);
        }

        /// <summary>
        /// Cancel job;
        /// </summary>
        private async Task CancelJobAsync(JobExecutionContext context, string reason, CancellationToken cancellationToken)
        {
            var job = context.Job;
            context.IsCancelled = true;

            job.CompletionTime = DateTime.UtcNow;
            job.Status = JobStatus.Cancelled;
            job.Error = new OperationCanceledException(reason);

            var jobResult = CreateJobResult(context);
            context.QueueItem.CompletionSource.SetCanceled();

            _logger.Info($"Job '{job.Name}' cancelled: {reason}");

            // Send notifications;
            if (job.NotificationSettings.NotifyOnCancellation)
            {
                await SendNotificationsAsync(job, jobResult, cancellationToken);
            }

            OnJobCompleted?.Invoke(this, jobResult);
        }

        /// <summary>
        /// Fail job;
        /// </summary>
        private async Task FailJobAsync(JobExecutionContext context, Exception error, CancellationToken cancellationToken)
        {
            var job = context.Job;

            job.CompletionTime = DateTime.UtcNow;
            job.Status = JobStatus.Failed;
            job.Error = error;

            var jobResult = CreateJobResult(context);
            context.QueueItem.CompletionSource.SetException(error);

            _logger.Error($"Job '{job.Name}' failed: {error.Message}", error);

            // Send notifications;
            if (job.NotificationSettings.NotifyOnFailure)
            {
                await SendNotificationsAsync(job, jobResult, cancellationToken);
            }

            OnJobCompleted?.Invoke(this, jobResult);
        }

        /// <summary>
        /// Create job result from context;
        /// </summary>
        private JobResult CreateJobResult(JobExecutionContext context)
        {
            var job = context.Job;

            var jobResult = new JobResult;
            {
                JobId = job.Id,
                JobName = job.Name,
                Status = job.Status,
                Progress = job.Progress,
                StartTime = job.StartTime ?? DateTime.UtcNow,
                CompletionTime = job.CompletionTime,
                ExecutionTime = job.ExecutionTime,
                Error = job.Error,
                Statistics = job.Statistics;
            };

            // Add task results;
            foreach (var task in job.Tasks)
            {
                var taskResult = new TaskResult;
                {
                    TaskId = task.Id,
                    TaskName = task.Name,
                    Status = task.Status,
                    Progress = task.Progress,
                    StartTime = task.StartTime ?? DateTime.MinValue,
                    CompletionTime = task.CompletionTime,
                    ExecutionTime = task.ExecutionTime,
                    Error = task.Error,
                    RetryCount = task.RetryCount,
                    Output = task.OutputData,
                    Metadata = task.ResultMetadata;
                };

                jobResult.TaskResults[task.Id] = taskResult;
            }

            return jobResult;
        }

        /// <summary>
        /// Check resource availability;
        /// </summary>
        private async Task<bool> CheckResourceAvailabilityAsync(BatchJob job)
        {
            // Check memory;
            var requiredMemory = job.Tasks.Sum(t => t.RequiredMemoryMB);
            if (requiredMemory > AvailableMemoryMB)
            {
                _logger.Warning($"Insufficient memory for job '{job.Name}': Required={requiredMemory}MB, Available={AvailableMemoryMB}MB");
                OnResourceWarning?.Invoke(this, new ResourceWarningEventArgs;
                {
                    ResourceType = ResourceType.Memory,
                    CurrentUsage = 100f - (float)AvailableMemoryMB / requiredMemory * 100f,
                    Threshold = 100f,
                    WarningMessage = $"Insufficient memory for job '{job.Name}'",
                    Timestamp = DateTime.UtcNow;
                });
                return false;
            }

            // Check disk space;
            var requiredDiskSpace = job.Tasks.Sum(t => t.RequiredDiskSpaceMB);
            if (requiredDiskSpace > AvailableDiskSpaceMB)
            {
                _logger.Warning($"Insufficient disk space for job '{job.Name}': Required={requiredDiskSpace}MB, Available={AvailableDiskSpaceMB}MB");
                OnResourceWarning?.Invoke(this, new ResourceWarningEventArgs;
                {
                    ResourceType = ResourceType.DiskSpace,
                    CurrentUsage = 100f - (float)AvailableDiskSpaceMB / requiredDiskSpace * 100f,
                    Threshold = 100f,
                    WarningMessage = $"Insufficient disk space for job '{job.Name}'",
                    Timestamp = DateTime.UtcNow;
                });
                return false;
            }

            // Check CPU usage;
            if (_configuration.MonitorCpuUsage && CurrentCpuUsage > _configuration.MaxCpuUsage)
            {
                _logger.Warning($"High CPU usage: {CurrentCpuUsage}% > {_configuration.MaxCpuUsage}%");
                await Task.Delay(5000); // Wait and recheck;

                if (_resourceMonitor.CurrentCpuUsage > _configuration.MaxCpuUsage)
                {
                    OnResourceWarning?.Invoke(this, new ResourceWarningEventArgs;
                    {
                        ResourceType = ResourceType.Cpu,
                        CurrentUsage = CurrentCpuUsage,
                        Threshold = _configuration.MaxCpuUsage,
                        WarningMessage = "High CPU usage, delaying job execution",
                        Timestamp = DateTime.UtcNow;
                    });
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Continue job processing after pause;
        /// </summary>
        private async Task ContinueJobProcessingAsync(JobExecutionContext context)
        {
            try
            {
                var remainingTasks = context.Job.Tasks;
                    .Where(t => t.Status != TaskStatus.Completed && t.Status != TaskStatus.Failed)
                    .ToList();

                foreach (var task in remainingTasks)
                {
                    if (context.IsCancelled || !_isRunning)
                        break;

                    await ExecuteTaskAsync(context, task, context.QueueItem.CancellationTokenSource.Token);
                }

                // Re-evaluate job completion;
                var allCompleted = context.Job.Tasks.All(t =>
                    t.Status == TaskStatus.Completed ||
                    t.Status == TaskStatus.Failed ||
                    t.Status == TaskStatus.Cancelled);

                if (allCompleted && !context.IsCompleted)
                {
                    var jobResult = CreateJobResult(context);
                    await CompleteJobAsync(context, jobResult, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error continuing job {context.Job.Id}: {ex.Message}", ex);
                await FailJobAsync(context, ex, CancellationToken.None);
            }
        }

        /// <summary>
        /// Monitor system resources;
        /// </summary>
        private async Task MonitorResourcesAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Resource monitor started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Update resource monitor;
                    await _resourceMonitor.UpdateAsync();

                    // Check for resource warnings;
                    if (_configuration.MonitorDiskSpace &&
                        AvailableDiskSpaceMB < _configuration.MinFreeDiskSpaceMB)
                    {
                        OnResourceWarning?.Invoke(this, new ResourceWarningEventArgs;
                        {
                            ResourceType = ResourceType.DiskSpace,
                            CurrentUsage = 100f - (float)AvailableDiskSpaceMB / _configuration.MinFreeDiskSpaceMB * 100f,
                            Threshold = 100f,
                            WarningMessage = $"Low disk space: {AvailableDiskSpaceMB}MB < {_configuration.MinFreeDiskSpaceMB}MB",
                            Timestamp = DateTime.UtcNow;
                        });
                    }

                    if (_configuration.MonitorCpuUsage &&
                        CurrentCpuUsage > _configuration.MaxCpuUsage)
                    {
                        OnResourceWarning?.Invoke(this, new ResourceWarningEventArgs;
                        {
                            ResourceType = ResourceType.Cpu,
                            CurrentUsage = CurrentCpuUsage,
                            Threshold = _configuration.MaxCpuUsage,
                            WarningMessage = $"High CPU usage: {CurrentCpuUsage}% > {_configuration.MaxCpuUsage}%",
                            Timestamp = DateTime.UtcNow;
                        });
                    }

                    await Task.Delay(5000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in resource monitor: {ex.Message}", ex);
                    await Task.Delay(10000, cancellationToken);
                }
            }

            _logger.Info("Resource monitor stopped");
        }

        /// <summary>
        /// Perform cleanup operations;
        /// </summary>
        private async Task PerformCleanupAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Cleanup task started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_configuration.AutoCleanup)
                    {
                        await CleanupTempFilesAsync();
                        await CleanupOldHistoryAsync();
                    }

                    await Task.Delay(TimeSpan.FromHours(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in cleanup task: {ex.Message}", ex);
                    await Task.Delay(TimeSpan.FromMinutes(30), cancellationToken);
                }
            }

            _logger.Info("Cleanup task stopped");
        }

        /// <summary>
        /// Cleanup temporary files;
        /// </summary>
        private async Task CleanupTempFilesAsync()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddDays(-_configuration.CleanupOlderThanDays);
                var tempDir = new DirectoryInfo(_configuration.TempDirectory);

                if (!tempDir.Exists)
                    return;

                var oldFiles = tempDir.GetFiles("*", SearchOption.AllDirectories)
                    .Where(f => f.LastWriteTimeUtc < cutoffTime)
                    .ToList();

                foreach (var file in oldFiles)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Could not delete temp file {file.FullName}: {ex.Message}");
                    }
                }

                var oldDirs = tempDir.GetDirectories("*", SearchOption.AllDirectories)
                    .Where(d => d.GetFiles("*", SearchOption.AllDirectories).Length == 0)
                    .ToList();

                foreach (var dir in oldDirs)
                {
                    try
                    {
                        dir.Delete(true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Could not delete temp directory {dir.FullName}: {ex.Message}");
                    }
                }

                if (oldFiles.Count > 0 || oldDirs.Count > 0)
                {
                    _logger.Info($"Cleaned up {oldFiles.Count} files and {oldDirs.Count} directories");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error cleaning up temp files: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Cleanup old history entries;
        /// </summary>
        private async Task CleanupOldHistoryAsync()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddDays(-_configuration.CleanupOlderThanDays);
                var oldEntries = _jobHistory.Where(h => h.Value.StartTime < cutoffTime).ToList();

                foreach (var entry in oldEntries)
                {
                    _jobHistory.TryRemove(entry.Key, out _);
                }

                if (oldEntries.Count > 0)
                {
                    _logger.Info($"Cleaned up {oldEntries.Count} old history entries");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error cleaning up history: {ex.Message}", ex);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Send notifications;
        /// </summary>
        private async Task SendNotificationsAsync(BatchJob job, JobResult result, CancellationToken cancellationToken)
        {
            try
            {
                // Implementation would integrate with NEDA.NotificationService;
                // For now, just log the notification;
                _logger.Info($"Notification for job '{job.Name}': Status={result.Status}, Tasks={job.Tasks.Count(t => t.Status == TaskStatus.Completed)}/{job.Tasks.Count}");

                // Example integration with NotificationService;
                // var notificationService = ServiceLocator.GetService<INotificationService>();
                // await notificationService.SendJobNotificationAsync(job, result);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error sending notifications: {ex.Message}", ex);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Check if job is completed;
        /// </summary>
        private bool IsJobCompleted(Guid jobId)
        {
            return _jobHistory.TryGetValue(jobId, out var history) &&
                   (history.Status == JobStatus.Completed ||
                    history.Status == JobStatus.Failed ||
                    history.Status == JobStatus.Cancelled);
        }

        /// <summary>
        /// Wait for active jobs to complete;
        /// </summary>
        private async Task WaitForActiveJobsCompletionAsync()
        {
            while (_activeJobs.Count > 0)
            {
                await Task.Delay(1000);
            }
        }

        #endregion;

        #region Helper Classes;

        /// <summary>
        /// Performance monitor;
        /// </summary>
        private class PerformanceMonitor;
        {
            public DateTime StartTime { get; }
            public TimeSpan Uptime => DateTime.UtcNow - StartTime;

            public PerformanceMonitor()
            {
                StartTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Resource monitor;
        /// </summary>
        private class ResourceMonitor;
        {
            public long AvailableMemoryMB { get; private set; }
            public long AvailableDiskSpaceMB { get; private set; }
            public float CurrentCpuUsage { get; private set; }

            public async Task UpdateAsync()
            {
                await Task.Run(() =>
                {
                    try
                    {
                        // Memory info;
                        var gcMemory = GC.GetTotalMemory(false) / (1024 * 1024);
                        var totalMemory = GetTotalMemoryMB();
                        AvailableMemoryMB = totalMemory - gcMemory;

                        // Disk space;
                        var tempDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                        if (!string.IsNullOrEmpty(tempDrive))
                        {
                            var driveInfo = new DriveInfo(tempDrive);
                            AvailableDiskSpaceMB = driveInfo.AvailableFreeSpace / (1024 * 1024);
                        }

                        // CPU usage (simplified - in production use PerformanceCounter or similar)
                        CurrentCpuUsage = GetCpuUsage();
                    }
                    catch
                    {
                        // Fallback values;
                        AvailableMemoryMB = 1024 * 4; // 4 GB;
                        AvailableDiskSpaceMB = 1024 * 10; // 10 GB;
                        CurrentCpuUsage = 20f;
                    }
                });
            }

            private long GetTotalMemoryMB()
            {
                // Implementation would use System.Management or similar;
                // For cross-platform, consider using System.Runtime.InteropServices;
                return 1024 * 16; // Assume 16 GB for now;
            }

            private float GetCpuUsage()
            {
                // Simplified implementation;
                // In production, use PerformanceCounter on Windows or /proc/stat on Linux;
                return 20f; // Assume 20% for now;
            }
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
                    // Stop processor;
                    _ = StopAsync(false).ConfigureAwait(false);

                    // Dispose resources;
                    _globalCancellationTokenSource?.Dispose();
                    _concurrentJobsSemaphore?.Dispose();
                    _parallelismSemaphore?.Dispose();

                    // Clear collections;
                    _activeJobs.Clear();
                    lock (_queueLock)
                    {
                        while (_jobQueue.Count > 0)
                        {
                            var item = _jobQueue.Dequeue();
                            item.CompletionSource.TrySetCanceled();
                            item.CancellationTokenSource.Dispose();
                        }
                    }

                    _taskProcessors.Clear();
                    _jobHistory.Clear();

                    // Clear events;
                    OnJobStarted = null;
                    OnJobCompleted = null;
                    OnJobProgressChanged = null;
                    OnTaskStarted = null;
                    OnTaskCompleted = null;
                    OnTaskProgressChanged = null;
                    OnProcessorError = null;
                    OnResourceWarning = null;
                    OnQueueStatusChanged = null;
                }

                _isDisposed = true;
            }
        }

        ~BatchProcessor()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Built-in Processors;

    /// <summary>
    /// Built-in file copy processor;
    /// </summary>
    public class FileCopyProcessor : ITaskProcessor;
    {
        public async Task<ProcessingResult> ProcessAsync(ProcessingTask task, CancellationToken cancellationToken)
        {
            var sourcePath = task.GetParameter<string>("SourcePath");
            var targetPath = task.GetParameter<string>("TargetPath");
            var overwrite = task.GetParameter<bool>("Overwrite", false);
            var createDirectory = task.GetParameter<bool>("CreateDirectory", true);

            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetPath))
            {
                return ProcessingResult.FailureResult("SourcePath and TargetPath are required");
            }

            try
            {
                if (createDirectory)
                {
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                }

                await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite), cancellationToken);

                return ProcessingResult.SuccessResult(targetPath);
            }
            catch (Exception ex)
            {
                return ProcessingResult.FailureResult($"File copy failed: {ex.Message}", ex);
            }
        }

        public bool ValidateTask(ProcessingTask task, out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrEmpty(task.GetParameter<string>("SourcePath")))
                errors.Add("SourcePath is required");

            if (string.IsNullOrEmpty(task.GetParameter<string>("TargetPath")))
                errors.Add("TargetPath is required");

            return errors.Count == 0;
        }

        public ProcessorCapabilities GetCapabilities()
        {
            return new ProcessorCapabilities;
            {
                ProcessorType = "FileCopy",
                SupportedTaskTypes = new[] { "FileCopy", "CopyFile" },
                MaxMemoryMB = 100,
                MaxParallelism = 10,
                SupportsCancellation = true,
                SupportsProgressReporting = false,
                SupportsPartialResults = false;
            };
        }
    }

    /// <summary>
    /// Built-in image conversion processor;
    /// </summary>
    public class ImageConversionProcessor : ITaskProcessor;
    {
        public async Task<ProcessingResult> ProcessAsync(ProcessingTask task, CancellationToken cancellationToken)
        {
            // Implementation would use System.Drawing or ImageSharp;
            // This is a simplified example;
            await Task.Delay(1000, cancellationToken); // Simulate processing;

            return ProcessingResult.SuccessResult();
        }

        public bool ValidateTask(ProcessingTask task, out List<string> errors)
        {
            errors = new List<string>();
            return errors.Count == 0;
        }

        public ProcessorCapabilities GetCapabilities()
        {
            return new ProcessorCapabilities;
            {
                ProcessorType = "ImageConversion",
                SupportedTaskTypes = new[] { "ConvertImage", "ResizeImage", "OptimizeImage" },
                MaxMemoryMB = 500,
                MaxParallelism = 4,
                SupportsCancellation = true,
                SupportsProgressReporting = true,
                SupportsPartialResults = true;
            };
        }
    }

    /// <summary>
    /// Built-in data export processor;
    /// </summary>
    public class DataExportProcessor : ITaskProcessor;
    {
        public async Task<ProcessingResult> ProcessAsync(ProcessingTask task, CancellationToken cancellationToken)
        {
            var data = task.InputData;
            var format = task.GetParameter<string>("Format", "JSON");
            var outputPath = task.GetParameter<string>("OutputPath");

            if (data == null)
            {
                return ProcessingResult.FailureResult("Input data is required");
            }

            try
            {
                string result = null;

                switch (format.ToUpperInvariant())
                {
                    case "JSON":
                        result = System.Text.Json.JsonSerializer.Serialize(data);
                        break;
                    case "XML":
                        // XML serialization;
                        break;
                    case "CSV":
                        // CSV conversion;
                        break;
                }

                if (!string.IsNullOrEmpty(outputPath))
                {
                    await File.WriteAllTextAsync(outputPath, result, cancellationToken);
                }

                return ProcessingResult.SuccessResult(result);
            }
            catch (Exception ex)
            {
                return ProcessingResult.FailureResult($"Data export failed: {ex.Message}", ex);
            }
        }

        public bool ValidateTask(ProcessingTask task, out List<string> errors)
        {
            errors = new List<string>();

            if (task.InputData == null)
                errors.Add("InputData is required");

            return errors.Count == 0;
        }

        public ProcessorCapabilities GetCapabilities()
        {
            return new ProcessorCapabilities;
            {
                ProcessorType = "DataExport",
                SupportedTaskTypes = new[] { "ExportData", "SerializeData" },
                MaxMemoryMB = 1000,
                MaxParallelism = 2,
                SupportsCancellation = true,
                SupportsProgressReporting = true,
                SupportsPartialResults = false;
            };
        }
    }

    #endregion;
}
