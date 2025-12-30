using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Services.Messaging.EventBus;
using NEDA.Automation.ScenarioPlanner;

namespace NEDA.ContentCreation.AssetPipeline.BatchProcessors;
{
    /// <summary>
    /// Job Scheduler for batch processing operations;
    /// Supports priority-based scheduling, dependency management, and fault tolerance;
    /// </summary>
    public class JobScheduler : IJobScheduler, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ISettingsManager _settingsManager;
        private readonly PriorityQueue<BatchJob, JobPriority> _jobQueue;
        private readonly Dictionary<Guid, BatchJob> _activeJobs;
        private readonly Dictionary<Guid, JobExecutionResult> _jobResults;
        private readonly List<JobDependency> _jobDependencies;
        private readonly SemaphoreSlim _queueLock;
        private readonly CancellationTokenSource _schedulerCts;
        private Task _schedulerTask;
        private bool _isRunning;
        private int _maxConcurrentJobs;
        private int _currentJobCount;

        public string SchedulerId { get; }
        public JobSchedulerStatus Status { get; private set; }
        public DateTime LastExecutionTime { get; private set; }
        public int TotalJobsProcessed { get; private set; }
        public int TotalJobsFailed { get; private set; }

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of JobScheduler;
        /// </summary>
        public JobScheduler(
            ILogger logger,
            IEventBus eventBus,
            ISettingsManager settingsManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));

            SchedulerId = Guid.NewGuid().ToString("N");
            _jobQueue = new PriorityQueue<BatchJob, JobPriority>();
            _activeJobs = new Dictionary<Guid, BatchJob>();
            _jobResults = new Dictionary<Guid, JobExecutionResult>();
            _jobDependencies = new List<JobDependency>();
            _queueLock = new SemaphoreSlim(1, 1);
            _schedulerCts = new CancellationTokenSource();

            InitializeConfiguration();
            Status = JobSchedulerStatus.Stopped;

            _logger.LogInformation($"JobScheduler initialized with ID: {SchedulerId}");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Starts the job scheduler;
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning)
            {
                _logger.LogWarning($"JobScheduler {SchedulerId} is already running");
                return;
            }

            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                _isRunning = true;
                Status = JobSchedulerStatus.Running;
                _schedulerTask = Task.Run(() => ProcessJobsAsync(_schedulerCts.Token), cancellationToken);

                await _eventBus.PublishAsync(new SchedulerStartedEvent;
                {
                    SchedulerId = SchedulerId,
                    Timestamp = DateTime.UtcNow,
                    MaxConcurrentJobs = _maxConcurrentJobs;
                });

                _logger.LogInformation($"JobScheduler {SchedulerId} started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start JobScheduler {SchedulerId}");
                Status = JobSchedulerStatus.Error;
                throw new JobSchedulerException($"Failed to start scheduler: {ex.Message}", ex);
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Stops the job scheduler;
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_isRunning)
            {
                return;
            }

            try
            {
                _isRunning = false;
                Status = JobSchedulerStatus.Stopping;

                _schedulerCts.Cancel();

                if (_schedulerTask != null && !_schedulerTask.IsCompleted)
                {
                    await _schedulerTask.WithCancellation(cancellationToken);
                }

                await CancelAllJobsAsync(cancellationToken);

                Status = JobSchedulerStatus.Stopped;

                await _eventBus.PublishAsync(new SchedulerStoppedEvent;
                {
                    SchedulerId = SchedulerId,
                    Timestamp = DateTime.UtcNow,
                    TotalJobsProcessed = TotalJobsProcessed,
                    TotalJobsFailed = TotalJobsFailed;
                });

                _logger.LogInformation($"JobScheduler {SchedulerId} stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error while stopping JobScheduler {SchedulerId}");
                Status = JobSchedulerStatus.Error;
                throw new JobSchedulerException($"Failed to stop scheduler: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Schedules a new batch job;
        /// </summary>
        public async Task<Guid> ScheduleJobAsync(
            BatchJob job,
            JobPriority priority = JobPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            ValidateJob(job);

            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                job.JobId = Guid.NewGuid();
                job.Priority = priority;
                job.Status = JobStatus.Scheduled;
                job.ScheduledTime = DateTime.UtcNow;
                job.SchedulerId = SchedulerId;

                _jobQueue.Enqueue(job, priority);

                await _eventBus.PublishAsync(new JobScheduledEvent;
                {
                    JobId = job.JobId,
                    JobType = job.JobType,
                    Priority = priority,
                    ScheduledTime = job.ScheduledTime,
                    SchedulerId = SchedulerId;
                });

                _logger.LogDebug($"Job {job.JobId} scheduled with priority {priority}");

                return job.JobId;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Schedules multiple jobs with dependencies;
        /// </summary>
        public async Task<List<Guid>> ScheduleJobBatchAsync(
            IEnumerable<BatchJob> jobs,
            IEnumerable<JobDependency> dependencies = null,
            CancellationToken cancellationToken = default)
        {
            var jobIds = new List<Guid>();

            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                foreach (var job in jobs)
                {
                    ValidateJob(job);
                    job.JobId = Guid.NewGuid();
                    job.Status = JobStatus.Scheduled;
                    job.ScheduledTime = DateTime.UtcNow;
                    job.SchedulerId = SchedulerId;

                    _jobQueue.Enqueue(job, job.Priority);
                    jobIds.Add(job.JobId);

                    _logger.LogDebug($"Job {job.JobId} added to batch");
                }

                if (dependencies != null)
                {
                    _jobDependencies.AddRange(dependencies);
                    _logger.LogDebug($"Added {dependencies.Count()} dependencies to batch");
                }

                await _eventBus.PublishAsync(new JobBatchScheduledEvent;
                {
                    JobIds = jobIds,
                    BatchSize = jobs.Count(),
                    DependenciesCount = dependencies?.Count() ?? 0,
                    SchedulerId = SchedulerId,
                    Timestamp = DateTime.UtcNow;
                });

                return jobIds;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Cancels a scheduled job;
        /// </summary>
        public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                var job = FindJob(jobId);
                if (job == null)
                {
                    _logger.LogWarning($"Job {jobId} not found for cancellation");
                    return false;
                }

                if (job.Status == JobStatus.Running)
                {
                    job.CancellationTokenSource?.Cancel();
                    _logger.LogInformation($"Cancellation requested for running job {jobId}");
                }

                job.Status = JobStatus.Cancelled;
                job.CompletionTime = DateTime.UtcNow;

                await _eventBus.PublishAsync(new JobCancelledEvent;
                {
                    JobId = jobId,
                    SchedulerId = SchedulerId,
                    CancellationTime = DateTime.UtcNow;
                });

                _logger.LogInformation($"Job {jobId} cancelled successfully");
                return true;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Gets job status;
        /// </summary>
        public async Task<JobStatus> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                var job = FindJob(jobId);
                return job?.Status ?? JobStatus.Unknown;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Gets job execution result;
        /// </summary>
        public async Task<JobExecutionResult> GetJobResultAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                if (_jobResults.TryGetValue(jobId, out var result))
                {
                    return result;
                }

                var job = FindJob(jobId);
                if (job != null && job.Status == JobStatus.Completed)
                {
                    return new JobExecutionResult;
                    {
                        JobId = jobId,
                        Success = true,
                        CompletionTime = job.CompletionTime ?? DateTime.UtcNow;
                    };
                }

                return null;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Gets scheduler statistics;
        /// </summary>
        public async Task<SchedulerStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                return new SchedulerStatistics;
                {
                    SchedulerId = SchedulerId,
                    Status = Status,
                    QueuedJobs = _jobQueue.Count,
                    ActiveJobs = _activeJobs.Count,
                    TotalProcessed = TotalJobsProcessed,
                    TotalFailed = TotalJobsFailed,
                    MaxConcurrentJobs = _maxConcurrentJobs,
                    CurrentConcurrentJobs = _currentJobCount,
                    LastExecutionTime = LastExecutionTime,
                    Uptime = _isRunning ? DateTime.UtcNow - (LastExecutionTime != default ? LastExecutionTime : DateTime.UtcNow) : TimeSpan.Zero;
                };
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Pauses the scheduler;
        /// </summary>
        public async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            if (Status != JobSchedulerStatus.Running)
            {
                throw new JobSchedulerException("Scheduler must be running to pause");
            }

            Status = JobSchedulerStatus.Paused;

            await _eventBus.PublishAsync(new SchedulerPausedEvent;
            {
                SchedulerId = SchedulerId,
                Timestamp = DateTime.UtcNow;
            });

            _logger.LogInformation($"JobScheduler {SchedulerId} paused");
        }

        /// <summary>
        /// Resumes the scheduler;
        /// </summary>
        public async Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            if (Status != JobSchedulerStatus.Paused)
            {
                throw new JobSchedulerException("Scheduler must be paused to resume");
            }

            Status = JobSchedulerStatus.Running;

            await _eventBus.PublishAsync(new SchedulerResumedEvent;
            {
                SchedulerId = SchedulerId,
                Timestamp = DateTime.UtcNow;
            });

            _logger.LogInformation($"JobScheduler {SchedulerId} resumed");
        }

        #endregion;

        #region Private Methods;

        private void InitializeConfiguration()
        {
            var config = _settingsManager.GetSection<BatchProcessorConfig>("BatchProcessor");
            _maxConcurrentJobs = config?.MaxConcurrentJobs ?? Environment.ProcessorCount * 2;

            _logger.LogDebug($"JobScheduler configured with max {_maxConcurrentJobs} concurrent jobs");
        }

        private async Task ProcessJobsAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Job processing started for scheduler {SchedulerId}");

            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _queueLock.WaitAsync(cancellationToken);

                    while (_currentJobCount < _maxConcurrentJobs && _jobQueue.Count > 0)
                    {
                        var job = _jobQueue.Dequeue();

                        if (CanExecuteJob(job))
                        {
                            _ = ExecuteJobAsync(job, cancellationToken);
                            _currentJobCount++;
                        }
                        else;
                        {
                            _jobQueue.Enqueue(job, job.Priority);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation($"Job processing cancelled for scheduler {SchedulerId}");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error in job processing loop for scheduler {SchedulerId}");
                    await Task.Delay(1000, cancellationToken);
                }
                finally
                {
                    _queueLock.Release();
                }

                LastExecutionTime = DateTime.UtcNow;
                await Task.Delay(100, cancellationToken);
            }

            _logger.LogInformation($"Job processing stopped for scheduler {SchedulerId}");
        }

        private async Task ExecuteJobAsync(BatchJob job, CancellationToken schedulerCancellationToken)
        {
            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(schedulerCancellationToken);
            job.CancellationTokenSource = jobCts;

            try
            {
                await _queueLock.WaitAsync(schedulerCancellationToken);
                _activeJobs[job.JobId] = job;
                job.Status = JobStatus.Running;
                job.StartTime = DateTime.UtcNow;
                _queueLock.Release();

                await _eventBus.PublishAsync(new JobStartedEvent;
                {
                    JobId = job.JobId,
                    JobType = job.JobType,
                    StartTime = job.StartTime.Value,
                    SchedulerId = SchedulerId;
                });

                _logger.LogInformation($"Job {job.JobId} started execution");

                var result = await job.ExecuteAsync(jobCts.Token);

                await _queueLock.WaitAsync(schedulerCancellationToken);

                job.Status = result.Success ? JobStatus.Completed : JobStatus.Failed;
                job.CompletionTime = DateTime.UtcNow;
                job.ExecutionResult = result;

                _jobResults[job.JobId] = result;
                _activeJobs.Remove(job.JobId);

                if (result.Success)
                {
                    TotalJobsProcessed++;
                }
                else;
                {
                    TotalJobsFailed++;
                    _logger.LogError($"Job {job.JobId} failed: {result.ErrorMessage}");
                }

                _queueLock.Release();

                await _eventBus.PublishAsync(new JobCompletedEvent;
                {
                    JobId = job.JobId,
                    Success = result.Success,
                    CompletionTime = job.CompletionTime.Value,
                    ProcessingTime = job.CompletionTime.Value - job.StartTime.Value,
                    SchedulerId = SchedulerId,
                    ErrorMessage = result.ErrorMessage;
                });

                _logger.LogInformation($"Job {job.JobId} completed with status: {job.Status}");
            }
            catch (OperationCanceledException)
            {
                await HandleJobCancellationAsync(job, "Job execution was cancelled");
            }
            catch (Exception ex)
            {
                await HandleJobFailureAsync(job, ex);
            }
            finally
            {
                await _queueLock.WaitAsync(schedulerCancellationToken);
                _currentJobCount--;
                job.CancellationTokenSource?.Dispose();
                job.CancellationTokenSource = null;
                _queueLock.Release();
            }
        }

        private async Task HandleJobCancellationAsync(BatchJob job, string reason)
        {
            try
            {
                await _queueLock.WaitAsync();

                job.Status = JobStatus.Cancelled;
                job.CompletionTime = DateTime.UtcNow;
                _activeJobs.Remove(job.JobId);

                _queueLock.Release();

                await _eventBus.PublishAsync(new JobCancelledEvent;
                {
                    JobId = job.JobId,
                    SchedulerId = SchedulerId,
                    CancellationTime = DateTime.UtcNow,
                    Reason = reason;
                });

                _logger.LogInformation($"Job {job.JobId} cancelled: {reason}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling job cancellation for job {job.JobId}");
            }
        }

        private async Task HandleJobFailureAsync(BatchJob job, Exception exception)
        {
            try
            {
                await _queueLock.WaitAsync();

                job.Status = JobStatus.Failed;
                job.CompletionTime = DateTime.UtcNow;
                job.ExecutionResult = new JobExecutionResult;
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorMessage = exception.Message,
                    Exception = exception,
                    CompletionTime = DateTime.UtcNow;
                };

                _jobResults[job.JobId] = job.ExecutionResult;
                _activeJobs.Remove(job.JobId);
                TotalJobsFailed++;

                _queueLock.Release();

                await _eventBus.PublishAsync(new JobFailedEvent;
                {
                    JobId = job.JobId,
                    SchedulerId = SchedulerId,
                    FailureTime = DateTime.UtcNow,
                    ErrorMessage = exception.Message,
                    ExceptionType = exception.GetType().Name;
                });

                _logger.LogError(exception, $"Job {job.JobId} failed with exception");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling job failure for job {job.JobId}");
            }
        }

        private async Task CancelAllJobsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _queueLock.WaitAsync(cancellationToken);

                foreach (var job in _activeJobs.Values.ToList())
                {
                    job.CancellationTokenSource?.Cancel();
                }

                _jobQueue.Clear();
                _activeJobs.Clear();
                _jobDependencies.Clear();
                _currentJobCount = 0;

                _queueLock.Release();

                _logger.LogInformation($"All jobs cancelled for scheduler {SchedulerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error cancelling all jobs for scheduler {SchedulerId}");
                throw;
            }
        }

        private bool CanExecuteJob(BatchJob job)
        {
            if (job.Dependencies == null || !job.Dependencies.Any())
            {
                return true;
            }

            var dependencies = _jobDependencies;
                .Where(d => d.DependentJobId == job.JobId)
                .ToList();

            foreach (var dependency in dependencies)
            {
                var prerequisiteJob = FindJob(dependency.PrerequisiteJobId);
                if (prerequisiteJob == null || prerequisiteJob.Status != JobStatus.Completed)
                {
                    return false;
                }
            }

            return true;
        }

        private BatchJob FindJob(Guid jobId)
        {
            if (_activeJobs.TryGetValue(jobId, out var activeJob))
            {
                return activeJob;
            }

            var queuedJob = _jobQueue.UnorderedItems;
                .FirstOrDefault(item => item.Element.JobId == jobId)
                .Element;

            return queuedJob;
        }

        private void ValidateJob(BatchJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (job.ExecuteAsync == null)
            {
                throw new JobSchedulerException("Job must have an execution delegate");
            }
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
                    _schedulerCts?.Cancel();
                    _schedulerCts?.Dispose();
                    _queueLock?.Dispose();

                    foreach (var job in _activeJobs.Values)
                    {
                        job.CancellationTokenSource?.Dispose();
                    }
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
    /// Batch job representation;
    /// </summary>
    public class BatchJob;
    {
        public Guid JobId { get; set; }
        public string JobType { get; set; }
        public string Description { get; set; }
        public JobPriority Priority { get; set; }
        public JobStatus Status { get; set; }
        public DateTime? ScheduledTime { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? CompletionTime { get; set; }
        public string SchedulerId { get; set; }
        public JobExecutionResult ExecutionResult { get; set; }
        public List<Guid> Dependencies { get; set; }
        public Func<CancellationToken, Task<JobExecutionResult>> ExecuteAsync { get; set; }
        internal CancellationTokenSource CancellationTokenSource { get; set; }

        public BatchJob()
        {
            Dependencies = new List<Guid>();
            Priority = JobPriority.Normal;
        }
    }

    /// <summary>
    /// Job execution result;
    /// </summary>
    public class JobExecutionResult;
    {
        public Guid JobId { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public DateTime CompletionTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public JobExecutionResult()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Job dependency definition;
    /// </summary>
    public class JobDependency;
    {
        public Guid DependentJobId { get; set; }
        public Guid PrerequisiteJobId { get; set; }
        public DependencyType Type { get; set; }
    }

    /// <summary>
    /// Scheduler statistics;
    /// </summary>
    public class SchedulerStatistics;
    {
        public string SchedulerId { get; set; }
        public JobSchedulerStatus Status { get; set; }
        public int QueuedJobs { get; set; }
        public int ActiveJobs { get; set; }
        public int TotalProcessed { get; set; }
        public int TotalFailed { get; set; }
        public int MaxConcurrentJobs { get; set; }
        public int CurrentConcurrentJobs { get; set; }
        public DateTime LastExecutionTime { get; set; }
        public TimeSpan Uptime { get; set; }
    }

    /// <summary>
    /// Batch processor configuration;
    /// </summary>
    public class BatchProcessorConfig;
    {
        public int MaxConcurrentJobs { get; set; } = 8;
        public int QueueCapacity { get; set; } = 1000;
        public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public bool EnableRetry { get; set; } = true;
        public int MaxRetryAttempts { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Job priority levels;
    /// </summary>
    public enum JobPriority;
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3;
    }

    /// <summary>
    /// Job status;
    /// </summary>
    public enum JobStatus;
    {
        Unknown = 0,
        Scheduled = 1,
        Running = 2,
        Completed = 3,
        Failed = 4,
        Cancelled = 5;
    }

    /// <summary>
    /// Scheduler status;
    /// </summary>
    public enum JobSchedulerStatus;
    {
        Stopped = 0,
        Running = 1,
        Paused = 2,
        Stopping = 3,
        Error = 4;
    }

    /// <summary>
    /// Dependency types;
    /// </summary>
    public enum DependencyType;
    {
        StartAfter = 0,
        FinishBefore = 1,
        SuccessRequired = 2;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Base scheduler event;
    /// </summary>
    public abstract class SchedulerEvent : IEvent;
    {
        public string SchedulerId { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    public class SchedulerStartedEvent : SchedulerEvent;
    {
        public int MaxConcurrentJobs { get; set; }
    }

    public class SchedulerStoppedEvent : SchedulerEvent;
    {
        public int TotalJobsProcessed { get; set; }
        public int TotalJobsFailed { get; set; }
    }

    public class SchedulerPausedEvent : SchedulerEvent { }

    public class SchedulerResumedEvent : SchedulerEvent { }

    public class JobScheduledEvent : SchedulerEvent;
    {
        public Guid JobId { get; set; }
        public string JobType { get; set; }
        public JobPriority Priority { get; set; }
        public DateTime ScheduledTime { get; set; }
    }

    public class JobBatchScheduledEvent : SchedulerEvent;
    {
        public List<Guid> JobIds { get; set; }
        public int BatchSize { get; set; }
        public int DependenciesCount { get; set; }
    }

    public class JobStartedEvent : SchedulerEvent;
    {
        public Guid JobId { get; set; }
        public string JobType { get; set; }
        public DateTime StartTime { get; set; }
    }

    public class JobCompletedEvent : SchedulerEvent;
    {
        public Guid JobId { get; set; }
        public bool Success { get; set; }
        public DateTime CompletionTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class JobFailedEvent : SchedulerEvent;
    {
        public Guid JobId { get; set; }
        public DateTime FailureTime { get; set; }
        public string ErrorMessage { get; set; }
        public string ExceptionType { get; set; }
    }

    public class JobCancelledEvent : SchedulerEvent;
    {
        public Guid JobId { get; set; }
        public DateTime CancellationTime { get; set; }
        public string Reason { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Job scheduler exception;
    /// </summary>
    public class JobSchedulerException : Exception
    {
        public JobSchedulerException(string message) : base(message) { }
        public JobSchedulerException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// Job scheduler interface;
    /// </summary>
    public interface IJobScheduler;
    {
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        Task<Guid> ScheduleJobAsync(BatchJob job, JobPriority priority = JobPriority.Normal,
            CancellationToken cancellationToken = default);
        Task<List<Guid>> ScheduleJobBatchAsync(IEnumerable<BatchJob> jobs,
            IEnumerable<JobDependency> dependencies = null,
            CancellationToken cancellationToken = default);
        Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
        Task<JobStatus> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default);
        Task<JobExecutionResult> GetJobResultAsync(Guid jobId, CancellationToken cancellationToken = default);
        Task<SchedulerStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
        Task PauseAsync(CancellationToken cancellationToken = default);
        Task ResumeAsync(CancellationToken cancellationToken = default);
    }

    #endregion;
}
