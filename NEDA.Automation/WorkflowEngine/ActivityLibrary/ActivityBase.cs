using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace NEDA.Automation.WorkflowEngine.ActivityLibrary;
{
    /// <summary>
    /// Activity execution states;
    /// </summary>
    public enum ActivityState;
    {
        /// <summary>
        /// Activity is created but not yet executed;
        /// </summary>
        Created = 0,

        /// <summary>
        /// Activity is currently executing;
        /// </summary>
        Executing = 1,

        /// <summary>
        /// Activity execution completed successfully;
        /// </summary>
        Completed = 2,

        /// <summary>
        /// Activity execution failed;
        /// </summary>
        Failed = 3,

        /// <summary>
        /// Activity execution was cancelled;
        /// </summary>
        Cancelled = 4,

        /// <summary>
        /// Activity is waiting for external input or event;
        /// </summary>
        Waiting = 5,

        /// <summary>
        /// Activity is paused and can be resumed;
        /// </summary>
        Paused = 6,

        /// <summary>
        /// Activity is scheduled for future execution;
        /// </summary>
        Scheduled = 7,

        /// <summary>
        /// Activity execution timed out;
        /// </summary>
        TimedOut = 8,

        /// <summary>
        /// Activity is being compensated (rolled back)
        /// </summary>
        Compensating = 9,

        /// <summary>
        /// Activity compensation completed;
        /// </summary>
        Compensated = 10;
    }

    /// <summary>
    /// Activity execution results;
    /// </summary>
    public class ActivityResult;
    {
        public Guid ActivityId { get; set; }
        public string ActivityName { get; set; }
        public ActivityState State { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public Exception Error { get; set; }
        public Dictionary<string, object> OutputData { get; } = new();
        public Dictionary<string, object> ExecutionMetrics { get; } = new();
        public List<string> LogMessages { get; } = new();
        public bool Success => State == ActivityState.Completed || State == ActivityState.Compensated;

        /// <summary>
        /// Creates a successful activity result;
        /// </summary>
        public static ActivityResult SuccessResult(Guid activityId, string activityName,
            Dictionary<string, object> outputData = null)
        {
            var now = DateTime.UtcNow;
            return new ActivityResult;
            {
                ActivityId = activityId,
                ActivityName = activityName,
                State = ActivityState.Completed,
                StartTime = now,
                EndTime = now,
                OutputData = outputData ?? new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Creates a failed activity result;
        /// </summary>
        public static ActivityResult FailureResult(Guid activityId, string activityName,
            Exception error, Dictionary<string, object> outputData = null)
        {
            var now = DateTime.UtcNow;
            return new ActivityResult;
            {
                ActivityId = activityId,
                ActivityName = activityName,
                State = ActivityState.Failed,
                StartTime = now,
                EndTime = now,
                Error = error,
                OutputData = outputData ?? new Dictionary<string, object>()
            };
        }

        public override string ToString()
        {
            return $"{ActivityName} -> {State} ({Duration.TotalMilliseconds}ms)";
        }
    }

    /// <summary>
    /// Activity execution context;
    /// </summary>
    public class ActivityExecutionContext;
    {
        public Guid ContextId { get; } = Guid.NewGuid();
        public Guid WorkflowInstanceId { get; set; }
        public Guid ActivityInstanceId { get; set; }
        public ActivityBase Activity { get; set; }
        public Dictionary<string, object> InputData { get; } = new();
        public Dictionary<string, object> OutputData { get; } = new();
        public Dictionary<string, object> SharedData { get; } = new();
        public CancellationToken CancellationToken { get; set; }
        public ILogger Logger { get; set; }
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public List<ActivityResult> PreviousResults { get; } = new();
        public Dictionary<string, object> ContextProperties { get; } = new();

        /// <summary>
        /// Gets a typed input value;
        /// </summary>
        public T GetInput<T>(string key, T defaultValue = default)
        {
            if (InputData.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                    return typedValue;

                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Sets an output value;
        /// </summary>
        public void SetOutput(string key, object value)
        {
            OutputData[key] = value;
        }

        /// <summary>
        /// Gets a typed output value;
        /// </summary>
        public T GetOutput<T>(string key, T defaultValue = default)
        {
            if (OutputData.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                    return typedValue;

                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Shares data between activities;
        /// </summary>
        public void ShareData(string key, object value)
        {
            SharedData[key] = value;
        }

        /// <summary>
        /// Gets shared data;
        /// </summary>
        public T GetSharedData<T>(string key, T defaultValue = default)
        {
            if (SharedData.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                    return typedValue;

                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Logs a message to activity context;
        /// </summary>
        public void Log(string message, LogLevel level = LogLevel.Information)
        {
            Logger?.Log(level, $"[Activity:{Activity?.Name}] {message}");
            ContextProperties[$"Log_{DateTime.UtcNow:HHmmssfff}"] = $"[{level}] {message}";
        }
    }

    /// <summary>
    /// Activity compensation context;
    /// </summary>
    public class CompensationContext;
    {
        public Guid OriginalActivityId { get; set; }
        public ActivityResult OriginalResult { get; set; }
        public Dictionary<string, object> CompensationData { get; } = new();
        public Exception CompensationError { get; set; }
        public bool IsCompensated { get; set; }

        public static CompensationContext FromActivityResult(ActivityResult result)
        {
            return new CompensationContext;
            {
                OriginalActivityId = result.ActivityId,
                OriginalResult = result,
                CompensationData = result.OutputData.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };
        }
    }

    /// <summary>
    /// Activity retry policy;
    /// </summary>
    public class ActivityRetryPolicy;
    {
        public int MaxRetries { get; set; } = 3;
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
        public double BackoffMultiplier { get; set; } = 2.0;
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);
        public List<Type> RetryableExceptions { get; } = new();
        public Func<Exception, bool> ShouldRetry { get; set; }

        public TimeSpan GetRetryDelay(int retryCount)
        {
            var delayMs = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, retryCount);
            delayMs = Math.Min(delayMs, MaxDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(delayMs);
        }

        public bool CanRetry(Exception exception, int currentRetry)
        {
            if (currentRetry >= MaxRetries)
                return false;

            if (ShouldRetry != null)
                return ShouldRetry(exception);

            return RetryableExceptions.Any(t => t.IsInstanceOfType(exception));
        }
    }

    /// <summary>
    /// Activity timeout policy;
    /// </summary>
    public class ActivityTimeoutPolicy;
    {
        public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool ContinueOnTimeout { get; set; } = false;
        public Action<ActivityExecutionContext> OnTimeout { get; set; }

        public static ActivityTimeoutPolicy Infinite => new ActivityTimeoutPolicy;
        {
            ExecutionTimeout = Timeout.InfiniteTimeSpan,
            StartupTimeout = Timeout.InfiniteTimeSpan,
            CleanupTimeout = Timeout.InfiniteTimeSpan;
        };
    }

    /// <summary>
    /// Activity validation result;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public Dictionary<string, object> ValidationData { get; } = new();

        public static ValidationResult Valid() => new ValidationResult { IsValid = true };

        public static ValidationResult Invalid(params string[] errors)
        {
            var result = new ValidationResult { IsValid = false };
            result.Errors.AddRange(errors);
            return result;
        }

        public void AddError(string error)
        {
            IsValid = false;
            Errors.Add(error);
        }

        public void AddWarning(string warning)
        {
            Warnings.Add(warning);
        }

        public override string ToString()
        {
            if (IsValid && Warnings.Count == 0)
                return "Valid";

            var messages = new List<string>();
            if (!IsValid)
                messages.Add($"Errors: {string.Join("; ", Errors)}");
            if (Warnings.Count > 0)
                messages.Add($"Warnings: {string.Join("; ", Warnings)}");

            return string.Join(" | ", messages);
        }
    }

    /// <summary>
    /// Base class for all workflow activities;
    /// </summary>
    [DataContract]
    [XmlRoot("Activity")]
    [KnownType(typeof(ActivityBase))]
    public abstract class ActivityBase : INotifyPropertyChanged, IDisposable;
    {
        private ActivityState _state = ActivityState.Created;
        private int _executionCount = 0;
        private readonly object _stateLock = new();
        private readonly List<ActivityResult> _executionHistory = new();
        private bool _isDisposed;

        /// <summary>
        /// Activity unique identifier;
        /// </summary>
        [DataMember]
        [XmlAttribute]
        public Guid Id { get; private set; } = Guid.NewGuid();

        /// <summary>
        /// Activity name;
        /// </summary>
        [DataMember]
        [XmlAttribute]
        public string Name { get; set; }

        /// <summary>
        /// Activity description;
        /// </summary>
        [DataMember]
        [XmlAttribute]
        public string Description { get; set; }

        /// <summary>
        /// Activity version;
        /// </summary>
        [DataMember]
        [XmlAttribute]
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Activity category;
        /// </summary>
        [DataMember]
        [XmlAttribute]
        public string Category { get; set; } = "General";

        /// <summary>
        /// Activity tags for categorization;
        /// </summary>
        [DataMember]
        [XmlArray("Tags")]
        public List<string> Tags { get; } = new();

        /// <summary>
        /// Activity execution state;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public ActivityState State;
        {
            get { lock (_stateLock) return _state; }
            protected set;
            {
                lock (_stateLock)
                {
                    if (_state != value)
                    {
                        _state = value;
                        OnPropertyChanged(nameof(State));
                        OnStateChanged(value);
                    }
                }
            }
        }

        /// <summary>
        /// Activity input parameters definition;
        /// </summary>
        [DataMember]
        [XmlArray("InputParameters")]
        public Dictionary<string, ActivityParameter> InputParameters { get; } = new();

        /// <summary>
        /// Activity output parameters definition;
        /// </summary>
        [DataMember]
        [XmlArray("OutputParameters")]
        public Dictionary<string, ActivityParameter> OutputParameters { get; } = new();

        /// <summary>
        /// Activity retry policy;
        /// </summary>
        [DataMember]
        [XmlElement("RetryPolicy")]
        public ActivityRetryPolicy RetryPolicy { get; set; } = new();

        /// <summary>
        /// Activity timeout policy;
        /// </summary>
        [DataMember]
        [XmlElement("TimeoutPolicy")]
        public ActivityTimeoutPolicy TimeoutPolicy { get; set; } = new();

        /// <summary>
        /// Activity configuration properties;
        /// </summary>
        [DataMember]
        [XmlArray("Configuration")]
        public Dictionary<string, object> Configuration { get; } = new();

        /// <summary>
        /// Activity metadata;
        /// </summary>
        [DataMember]
        [XmlArray("Metadata")]
        public Dictionary<string, object> Metadata { get; } = new();

        /// <summary>
        /// Activity dependencies (activities that must complete before this)
        /// </summary>
        [DataMember]
        [XmlArray("Dependencies")]
        public List<Guid> Dependencies { get; } = new();

        /// <summary>
        /// Activity execution history;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public IReadOnlyList<ActivityResult> ExecutionHistory => _executionHistory.AsReadOnly();

        /// <summary>
        /// Total execution count;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public int ExecutionCount => _executionCount;

        /// <summary>
        /// Last execution result;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public ActivityResult LastResult => _executionHistory.LastOrDefault();

        /// <summary>
        /// Is activity currently executing;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public bool IsExecuting => State == ActivityState.Executing;

        /// <summary>
        /// Is activity completed successfully;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public bool IsCompleted => State == ActivityState.Completed;

        /// <summary>
        /// Is activity failed;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public bool IsFailed => State == ActivityState.Failed;

        /// <summary>
        /// Is activity cancelled;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public bool IsCancelled => State == ActivityState.Cancelled;

        /// <summary>
        /// Activity creation time;
        /// </summary>
        [DataMember]
        [XmlAttribute]
        public DateTime CreatedTime { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// Last execution time;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public DateTime? LastExecutionTime { get; private set; }

        /// <summary>
        /// Total execution time across all runs;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public TimeSpan TotalExecutionTime { get; private set; }

        /// <summary>
        /// Average execution time;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public TimeSpan AverageExecutionTime => _executionCount > 0 ?
            TimeSpan.FromTicks(TotalExecutionTime.Ticks / _executionCount) : TimeSpan.Zero;

        /// <summary>
        /// Activity success rate (0-100)
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public double SuccessRate => _executionCount > 0 ?
            (_executionHistory.Count(r => r.Success) / (double)_executionCount) * 100 : 0;

        /// <summary>
        /// Event fired when activity state changes;
        /// </summary>
        public event EventHandler<ActivityStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Event fired when activity execution completes;
        /// </summary>
        public event EventHandler<ActivityExecutionCompletedEventArgs> ExecutionCompleted;

        /// <summary>
        /// Event fired when activity validation occurs;
        /// </summary>
        public event EventHandler<ActivityValidationEventArgs> Validated;

        /// <summary>
        /// Event fired when activity compensation occurs;
        /// </summary>
        public event EventHandler<ActivityCompensationEventArgs> Compensated;

        /// <summary>
        /// INotifyPropertyChanged implementation;
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Constructor;
        /// </summary>
        protected ActivityBase()
        {
            InitializeDefaultParameters();
        }

        /// <summary>
        /// Constructor with name;
        /// </summary>
        protected ActivityBase(string name, string description = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description;
            InitializeDefaultParameters();
        }

        /// <summary>
        /// Execute activity asynchronously;
        /// </summary>
        public async Task<ActivityResult> ExecuteAsync(ActivityExecutionContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (State == ActivityState.Executing)
                throw new InvalidOperationException($"Activity {Name} is already executing");

            var executionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;
            ActivityResult result = null;
            int retryCount = 0;

            try
            {
                // Validate inputs;
                var validationResult = ValidateInputs(context);
                if (!validationResult.IsValid)
                {
                    result = ActivityResult.FailureResult(executionId, Name,
                        new ValidationException($"Input validation failed: {validationResult}"));
                    result.OutputData["ValidationResult"] = validationResult;
                    return result;
                }

                OnValidated(validationResult);

                // Update state;
                State = ActivityState.Executing;
                Interlocked.Increment(ref _executionCount);
                LastExecutionTime = startTime;

                // Execute with retry policy;
                while (true)
                {
                    try
                    {
                        // Check cancellation;
                        context.CancellationToken.ThrowIfCancellationRequested();

                        // Create execution task with timeout;
                        var executionTask = ExecuteCoreAsync(context);
                        var timeoutTask = Task.Delay(TimeoutPolicy.ExecutionTimeout, context.CancellationToken);

                        var completedTask = await Task.WhenAny(executionTask, timeoutTask);

                        if (completedTask == timeoutTask)
                        {
                            throw new TimeoutException($"Activity execution timed out after {TimeoutPolicy.ExecutionTimeout}");
                        }

                        // Get result from execution task;
                        var outputData = await executionTask;

                        // Create successful result;
                        result = ActivityResult.SuccessResult(executionId, Name, outputData);
                        result.OutputData["RetryCount"] = retryCount;
                        result.OutputData["ExecutionId"] = executionId;

                        State = ActivityState.Completed;
                        break;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        retryCount++;

                        // Check if we should retry
                        if (RetryPolicy.CanRetry(ex, retryCount))
                        {
                            var delay = RetryPolicy.GetRetryDelay(retryCount);
                            context.Log($"Execution failed, retry {retryCount}/{RetryPolicy.MaxRetries} after {delay.TotalMilliseconds}ms", LogLevel.Warning);

                            await Task.Delay(delay, context.CancellationToken);
                            continue;
                        }

                        // Retry limit exceeded or non-retryable exception;
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = ActivityResult.FailureResult(executionId, Name,
                    new TaskCanceledException("Activity execution was cancelled"));
                State = ActivityState.Cancelled;
            }
            catch (TimeoutException ex)
            {
                result = ActivityResult.FailureResult(executionId, Name, ex);
                State = ActivityState.TimedOut;

                // Handle timeout according to policy;
                if (TimeoutPolicy.OnTimeout != null)
                {
                    try
                    {
                        TimeoutPolicy.OnTimeout(context);
                    }
                    catch (Exception timeoutEx)
                    {
                        context.Log($"Timeout handler failed: {timeoutEx.Message}", LogLevel.Error);
                    }
                }

                if (!TimeoutPolicy.ContinueOnTimeout)
                    throw;
            }
            catch (Exception ex)
            {
                result = ActivityResult.FailureResult(executionId, Name, ex);
                State = ActivityState.Failed;
            }
            finally
            {
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                if (result != null)
                {
                    result.StartTime = startTime;
                    result.EndTime = endTime;
                    result.ExecutionMetrics["DurationMs"] = duration.TotalMilliseconds;
                    result.ExecutionMetrics["RetryCount"] = retryCount;
                    result.ExecutionMetrics["ThreadId"] = Thread.CurrentThread.ManagedThreadId;
                    result.ExecutionMetrics["MemoryUsageMB"] = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

                    // Add context logs to result;
                    foreach (var kvp in context.ContextProperties.Where(k => k.Key.StartsWith("Log_")))
                    {
                        result.LogMessages.Add(kvp.Value.ToString());
                    }

                    // Update activity statistics;
                    TotalExecutionTime += duration;

                    // Add to history;
                    _executionHistory.Add(result);

                    // Fire completion event;
                    OnExecutionCompleted(result);
                }

                // Cleanup;
                try
                {
                    await CleanupAsync(context, result);
                }
                catch (Exception cleanupEx)
                {
                    context.Log($"Cleanup failed: {cleanupEx.Message}", LogLevel.Error);
                }
            }

            return result;
        }

        /// <summary>
        /// Validate activity inputs;
        /// </summary>
        public virtual ValidationResult ValidateInputs(ActivityExecutionContext context)
        {
            var result = new ValidationResult { IsValid = true };

            // Check required inputs;
            foreach (var param in InputParameters.Values.Where(p => p.IsRequired))
            {
                if (!context.InputData.ContainsKey(param.Name))
                {
                    result.AddError($"Required input '{param.Name}' is missing");
                }
                else;
                {
                    // Validate type;
                    var value = context.InputData[param.Name];
                    if (!param.IsValidType(value))
                    {
                        result.AddError($"Input '{param.Name}' has invalid type. Expected: {param.Type}, Got: {value?.GetType().Name}");
                    }

                    // Validate constraints;
                    var validationError = param.Validate(value);
                    if (!string.IsNullOrEmpty(validationError))
                    {
                        result.AddError($"Input '{param.Name}' validation failed: {validationError}");
                    }
                }
            }

            // Validate configuration;
            foreach (var config in Configuration)
            {
                // Add configuration validation logic here;
                if (config.Value == null)
                {
                    result.AddWarning($"Configuration '{config.Key}' is null");
                }
            }

            return result;
        }

        /// <summary>
        /// Validate activity configuration;
        /// </summary>
        public virtual ValidationResult ValidateConfiguration()
        {
            var result = new ValidationResult { IsValid = true };

            // Check required configuration;
            if (string.IsNullOrEmpty(Name))
                result.AddError("Activity name is required");

            if (TimeoutPolicy == null)
                result.AddError("Timeout policy is required");

            if (RetryPolicy == null)
                result.AddError("Retry policy is required");

            return result;
        }

        /// <summary>
        /// Compensate (rollback) activity execution;
        /// </summary>
        public virtual async Task<CompensationContext> CompensateAsync(ActivityResult result,
            ActivityExecutionContext context)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var compensationContext = CompensationContext.FromActivityResult(result);

            try
            {
                State = ActivityState.Compensating;
                context.Log($"Starting compensation for activity {Name}");

                await CompensateCoreAsync(result, context, compensationContext);

                compensationContext.IsCompensated = true;
                State = ActivityState.Compensated;

                context.Log($"Compensation completed successfully for activity {Name}");
            }
            catch (Exception ex)
            {
                compensationContext.CompensationError = ex;
                compensationContext.IsCompensated = false;
                State = ActivityState.Failed;

                context.Log($"Compensation failed for activity {Name}: {ex.Message}", LogLevel.Error);
                throw;
            }
            finally
            {
                OnCompensated(compensationContext);
            }

            return compensationContext;
        }

        /// <summary>
        /// Reset activity state;
        /// </summary>
        public virtual void Reset()
        {
            lock (_stateLock)
            {
                _state = ActivityState.Created;
                _executionHistory.Clear();
                _executionCount = 0;
                TotalExecutionTime = TimeSpan.Zero;
                LastExecutionTime = null;

                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(ExecutionHistory));
                OnPropertyChanged(nameof(ExecutionCount));
                OnPropertyChanged(nameof(LastResult));
                OnPropertyChanged(nameof(TotalExecutionTime));
                OnPropertyChanged(nameof(AverageExecutionTime));
                OnPropertyChanged(nameof(SuccessRate));
            }
        }

        /// <summary>
        /// Clone activity with new ID;
        /// </summary>
        public virtual ActivityBase Clone()
        {
            var clone = (ActivityBase)MemberwiseClone();
            clone.Id = Guid.NewGuid();
            clone.CreatedTime = DateTime.UtcNow;
            clone._executionHistory.Clear();
            clone._executionCount = 0;
            clone.TotalExecutionTime = TimeSpan.Zero;
            clone.LastExecutionTime = null;
            clone._state = ActivityState.Created;

            return clone;
        }

        /// <summary>
        /// Add input parameter definition;
        /// </summary>
        public void AddInputParameter(string name, Type type, bool isRequired = true,
            string description = null, object defaultValue = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            if (type == null)
                throw new ArgumentNullException(nameof(type));

            InputParameters[name] = new ActivityParameter;
            {
                Name = name,
                Type = type,
                IsRequired = isRequired,
                Description = description,
                DefaultValue = defaultValue;
            };
        }

        /// <summary>
        /// Add output parameter definition;
        /// </summary>
        public void AddOutputParameter(string name, Type type, string description = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            if (type == null)
                throw new ArgumentNullException(nameof(type));

            OutputParameters[name] = new ActivityParameter;
            {
                Name = name,
                Type = type,
                IsRequired = false,
                Description = description;
            };
        }

        /// <summary>
        /// Get activity statistics;
        /// </summary>
        public ActivityStatistics GetStatistics()
        {
            lock (_stateLock)
            {
                return new ActivityStatistics;
                {
                    ActivityId = Id,
                    ActivityName = Name,
                    ExecutionCount = _executionCount,
                    SuccessCount = _executionHistory.Count(r => r.Success),
                    FailureCount = _executionHistory.Count(r => !r.Success),
                    TotalExecutionTime = TotalExecutionTime,
                    AverageExecutionTime = AverageExecutionTime,
                    SuccessRate = SuccessRate,
                    LastExecutionTime = LastExecutionTime,
                    CurrentState = State,
                    CreatedTime = CreatedTime;
                };
            }
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Core activity execution logic (to be implemented by derived classes)
        /// </summary>
        protected abstract Task<Dictionary<string, object>> ExecuteCoreAsync(ActivityExecutionContext context);

        /// <summary>
        /// Core compensation logic (can be overridden by derived classes)
        /// </summary>
        protected virtual Task CompensateCoreAsync(ActivityResult result,
            ActivityExecutionContext context, CompensationContext compensationContext)
        {
            // Default implementation does nothing;
            // Derived classes should override if compensation is needed;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Cleanup after execution (can be overridden by derived classes)
        /// </summary>
        protected virtual Task CleanupAsync(ActivityExecutionContext context, ActivityResult result)
        {
            // Default cleanup logic;
            // Release resources, close connections, etc.
            return Task.CompletedTask;
        }

        /// <summary>
        /// Initialize activity (can be overridden by derived classes)
        /// </summary>
        protected virtual Task InitializeAsync(ActivityExecutionContext context)
        {
            // Default initialization logic;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when activity state changes;
        /// </summary>
        protected virtual void OnStateChanged(ActivityState newState)
        {
            // Can be overridden by derived classes;
        }

        /// <summary>
        /// Initialize default parameters;
        /// </summary>
        private void InitializeDefaultParameters()
        {
            // Add common input parameters;
            AddInputParameter("ActivityId", typeof(Guid), false, "Activity instance identifier");
            AddInputParameter("CorrelationId", typeof(Guid), false, "Correlation identifier for tracking");
            AddInputParameter("Priority", typeof(int), false, "Execution priority");

            // Add common output parameters;
            AddOutputParameter("ExecutionId", typeof(Guid), "Unique execution identifier");
            AddOutputParameter("DurationMs", typeof(double), "Execution duration in milliseconds");
            AddOutputParameter("Success", typeof(bool), "Execution success status");

            // Default metadata;
            Metadata["CreatedBy"] = Environment.UserName;
            Metadata["CreationHost"] = Environment.MachineName;
            Metadata["FrameworkVersion"] = GetType().Assembly.GetName().Version.ToString();
        }

        /// <summary>
        /// Dispose pattern implementation;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    _executionHistory.Clear();
                    InputParameters.Clear();
                    OutputParameters.Clear();
                    Configuration.Clear();
                    Metadata.Clear();
                    Dependencies.Clear();
                    Tags.Clear();
                }

                _isDisposed = true;
            }
        }

        /// <summary>
        /// Raise property changed event;
        /// </summary>
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Raise state changed event;
        /// </summary>
        protected virtual void OnStateChanged(ActivityStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Raise execution completed event;
        /// </summary>
        protected virtual void OnExecutionCompleted(ActivityExecutionCompletedEventArgs e)
        {
            ExecutionCompleted?.Invoke(this, e);
        }

        /// <summary>
        /// Raise validated event;
        /// </summary>
        protected virtual void OnValidated(ActivityValidationEventArgs e)
        {
            Validated?.Invoke(this, e);
        }

        /// <summary>
        /// Raise compensated event;
        /// </summary>
        protected virtual void OnCompensated(ActivityCompensationEventArgs e)
        {
            Compensated?.Invoke(this, e);
        }

        /// <summary>
        /// Helper methods for event raising;
        /// </summary>
        private void OnStateChanged(ActivityState newState)
        {
            OnStateChanged(new ActivityStateChangedEventArgs;
            {
                ActivityId = Id,
                ActivityName = Name,
                PreviousState = State,
                NewState = newState,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnExecutionCompleted(ActivityResult result)
        {
            OnExecutionCompleted(new ActivityExecutionCompletedEventArgs;
            {
                ActivityId = Id,
                ActivityName = Name,
                Result = result,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnValidated(ValidationResult validationResult)
        {
            OnValidated(new ActivityValidationEventArgs;
            {
                ActivityId = Id,
                ActivityName = Name,
                ValidationResult = validationResult,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnCompensated(CompensationContext compensationContext)
        {
            OnCompensated(new ActivityCompensationEventArgs;
            {
                ActivityId = Id,
                ActivityName = Name,
                CompensationContext = compensationContext,
                Timestamp = DateTime.UtcNow;
            });
        }
    }

    /// <summary>
    /// Activity parameter definition;
    /// </summary>
    [DataContract]
    public class ActivityParameter;
    {
        [DataMember]
        [XmlAttribute]
        public string Name { get; set; }

        [DataMember]
        [XmlAttribute]
        public string TypeName { get; set; }

        [JsonIgnore]
        [XmlIgnore]
        public Type Type;
        {
            get => Type.GetType(TypeName) ?? typeof(object);
            set => TypeName = value?.AssemblyQualifiedName;
        }

        [DataMember]
        [XmlAttribute]
        public bool IsRequired { get; set; }

        [DataMember]
        [XmlAttribute]
        public string Description { get; set; }

        [DataMember]
        [XmlElement]
        public object DefaultValue { get; set; }

        [DataMember]
        [XmlArray("Constraints")]
        public List<ParameterConstraint> Constraints { get; } = new();

        public bool IsValidType(object value)
        {
            if (value == null)
                return !IsRequired || DefaultValue != null;

            return Type.IsInstanceOfType(value) ||
                   (Type.IsGenericType && Type.GetGenericTypeDefinition() == typeof(Nullable<>));
        }

        public string Validate(object value)
        {
            foreach (var constraint in Constraints)
            {
                var error = constraint.Validate(value);
                if (!string.IsNullOrEmpty(error))
                    return error;
            }

            return null;
        }

        public T GetValue<T>(object rawValue, T defaultValue = default)
        {
            if (rawValue == null)
                return defaultValue;

            if (rawValue is T typedValue)
                return typedValue;

            try
            {
                return (T)Convert.ChangeType(rawValue, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    /// <summary>
    /// Parameter constraint;
    /// </summary>
    [DataContract]
    public abstract class ParameterConstraint;
    {
        [DataMember]
        [XmlAttribute]
        public string Name { get; set; }

        [DataMember]
        [XmlAttribute]
        public string Message { get; set; }

        public abstract string Validate(object value);
    }

    /// <summary>
    /// Activity statistics;
    /// </summary>
    public class ActivityStatistics;
    {
        public Guid ActivityId { get; set; }
        public string ActivityName { get; set; }
        public int ExecutionCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public double SuccessRate { get; set; }
        public DateTime? LastExecutionTime { get; set; }
        public ActivityState CurrentState { get; set; }
        public DateTime CreatedTime { get; set; }

        public override string ToString()
        {
            return $"{ActivityName}: Executions={ExecutionCount}, Success={SuccessRate:F1}%, " +
                   $"AvgTime={AverageExecutionTime.TotalMilliseconds:F0}ms, State={CurrentState}";
        }
    }

    #region Event Arguments Classes;

    /// <summary>
    /// Activity state changed event arguments;
    /// </summary>
    public class ActivityStateChangedEventArgs : EventArgs;
    {
        public Guid ActivityId { get; set; }
        public string ActivityName { get; set; }
        public ActivityState PreviousState { get; set; }
        public ActivityState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Activity execution completed event arguments;
    /// </summary>
    public class ActivityExecutionCompletedEventArgs : EventArgs;
    {
        public Guid ActivityId { get; set; }
        public string ActivityName { get; set; }
        public ActivityResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Activity validation event arguments;
    /// </summary>
    public class ActivityValidationEventArgs : EventArgs;
    {
        public Guid ActivityId { get; set; }
        public string ActivityName { get; set; }
        public ValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Activity compensation event arguments;
    /// </summary>
    public class ActivityCompensationEventArgs : EventArgs;
    {
        public Guid ActivityId { get; set; }
        public string ActivityName { get; set; }
        public CompensationContext CompensationContext { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Common Constraints;

    /// <summary>
    /// Range constraint for numeric values;
    /// </summary>
    [DataContract]
    public class RangeConstraint : ParameterConstraint;
    {
        [DataMember]
        public double MinValue { get; set; } = double.MinValue;

        [DataMember]
        public double MaxValue { get; set; } = double.MaxValue;

        [DataMember]
        public bool Inclusive { get; set; } = true;

        public override string Validate(object value)
        {
            if (value == null) return null;

            try
            {
                var numericValue = Convert.ToDouble(value);

                if (Inclusive)
                {
                    if (numericValue < MinValue || numericValue > MaxValue)
                        return $"Value must be between {MinValue} and {MaxValue} (inclusive)";
                }
                else;
                {
                    if (numericValue <= MinValue || numericValue >= MaxValue)
                        return $"Value must be between {MinValue} and {MaxValue} (exclusive)";
                }
            }
            catch
            {
                return "Value must be numeric";
            }

            return null;
        }
    }

    /// <summary>
    /// String length constraint;
    /// </summary>
    [DataContract]
    public class StringLengthConstraint : ParameterConstraint;
    {
        [DataMember]
        public int MinLength { get; set; } = 0;

        [DataMember]
        public int MaxLength { get; set; } = int.MaxValue;

        public override string Validate(object value)
        {
            if (value == null) return null;

            var str = value.ToString();
            if (str.Length < MinLength || str.Length > MaxLength)
                return $"Length must be between {MinLength} and {MaxLength} characters";

            return null;
        }
    }

    /// <summary>
    /// Regular expression constraint;
    /// </summary>
    [DataContract]
    public class RegexConstraint : ParameterConstraint;
    {
        [DataMember]
        public string Pattern { get; set; }

        [DataMember]
        public string Options { get; set; }

        public override string Validate(object value)
        {
            if (value == null || string.IsNullOrEmpty(Pattern)) return null;

            var str = value.ToString();
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(Pattern);
                if (!regex.IsMatch(str))
                    return $"Value must match pattern: {Pattern}";
            }
            catch (Exception ex)
            {
                return $"Invalid regex pattern: {ex.Message}";
            }

            return null;
        }
    }

    /// <summary>
    /// Enum constraint for value validation;
    /// </summary>
    [DataContract]
    public class EnumConstraint : ParameterConstraint;
    {
        [DataMember]
        public List<string> AllowedValues { get; } = new();

        public override string Validate(object value)
        {
            if (value == null || AllowedValues.Count == 0) return null;

            var strValue = value.ToString();
            if (!AllowedValues.Contains(strValue))
                return $"Value must be one of: {string.Join(", ", AllowedValues)}";

            return null;
        }
    }

    #endregion;
}
