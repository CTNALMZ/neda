// NEDA.Build/CI_CD/PipelineManager.cs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security;
using System.Security.Cryptography;
using System.ComponentModel;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;
using NEDA.Build.CI_CD.Configuration;
using NEDA.Build.CI_CD.Contracts;
using NEDA.Build.CI_CD.Exceptions;
using NEDA.Build.CI_CD.Events;
using NEDA.Build.CI_CD.Models;
using NEDA.Build.CI_CD.Providers;
using NEDA.Build.CI_CD.Validators;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Build.CI_CD;
{
    /// <summary>
    /// Advanced CI/CD pipeline manager with support for multi-stage pipelines,
    /// parallel execution, artifact management, and integration with various build systems.
    /// </summary>
    public class PipelineManager : IPipelineManager, IDisposable;
    {
        #region Constants;

        private const int DEFAULT_MAX_CONCURRENT_PIPELINES = 5;
        private const int DEFAULT_PIPELINE_TIMEOUT_MINUTES = 120;
        private const int DEFAULT_HEARTBEAT_INTERVAL_SECONDS = 30;
        private const string PIPELINE_STATE_FILE_EXTENSION = ".pipeline.state";
        private const string ARTIFACT_MANIFEST_FILE = "artifact-manifest.json";
        private const string PIPELINE_LOG_FILE_PREFIX = "pipeline_";

        #endregion;

        #region Private Fields;

        private readonly PipelineManagerConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IEventBus _eventBus;
        private readonly PipelineValidator _validator;
        private readonly Dictionary<string, IPipelineProvider> _providers;
        private readonly Dictionary<string, PipelineExecution> _activeExecutions;
        private readonly Dictionary<string, PipelineDefinition> _pipelineDefinitions;
        private readonly Dictionary<string, PipelineArtifactRegistry> _artifactRegistries;
        private readonly Dictionary<string, PipelineEnvironment> _environments;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly object _syncLock = new object();
        private readonly Timer _heartbeatTimer;
        private readonly Timer _cleanupTimer;
        private readonly CancellationTokenSource _globalCancellation;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _disposed;
        private bool _isInitialized;
        private int _totalExecutions;
        private long _totalExecutionTimeMs;

        #endregion;

        #region Properties;

        /// <summary>
        /// Gets the configuration used by this pipeline manager.
        /// </summary>
        public PipelineManagerConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets the number of currently active pipeline executions.
        /// </summary>
        public int ActiveExecutionCount => _activeExecutions.Count;

        /// <summary>
        /// Gets the total number of pipeline executions since startup.
        /// </summary>
        public int TotalExecutionCount => _totalExecutions;

        /// <summary>
        /// Gets the number of registered pipeline definitions.
        /// </summary>
        public int PipelineDefinitionCount => _pipelineDefinitions.Count;

        /// <summary>
        /// Gets the number of registered environments.
        /// </summary>
        public int EnvironmentCount => _environments.Count;

        /// <summary>
        /// Gets whether the pipeline manager is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets the average pipeline execution time in milliseconds.
        /// </summary>
        public double AverageExecutionTimeMs => _totalExecutions > 0 ?
            (double)_totalExecutionTimeMs / _totalExecutions : 0;

        /// <summary>
        /// Gets the pipeline execution statistics.
        /// </summary>
        public PipelineExecutionStatistics Statistics => GetStatistics();

        #endregion;

        #region Events;

        /// <summary>
        /// Occurs when a pipeline execution starts.
        /// </summary>
        public event EventHandler<PipelineExecutionStartedEventArgs> PipelineExecutionStarted;

        /// <summary>
        /// Occurs when a pipeline execution completes.
        /// </summary>
        public event EventHandler<PipelineExecutionCompletedEventArgs> PipelineExecutionCompleted;

        /// <summary>
        /// Occurs when a pipeline execution fails.
        /// </summary>
        public event EventHandler<PipelineExecutionFailedEventArgs> PipelineExecutionFailed;

        /// <summary>
        /// Occurs when a pipeline stage starts.
        /// </summary>
        public event EventHandler<PipelineStageStartedEventArgs> PipelineStageStarted;

        /// <summary>
        /// Occurs when a pipeline stage completes.
        /// </summary>
        public event EventHandler<PipelineStageCompletedEventArgs> PipelineStageCompleted;

        /// <summary>
        /// Occurs when a pipeline artifact is created.
        /// </summary>
        public event EventHandler<PipelineArtifactCreatedEventArgs> PipelineArtifactCreated;

        /// <summary>
        /// Occurs when pipeline status changes.
        /// </summary>
        public event EventHandler<PipelineStatusChangedEventArgs> PipelineStatusChanged;

        /// <summary>
        /// Occurs when a pipeline is queued.
        /// </summary>
        public event EventHandler<PipelineQueuedEventArgs> PipelineQueued;

        /// <summary>
        /// Occurs when a pipeline is cancelled.
        /// </summary>
        public event EventHandler<PipelineCancelledEventArgs> PipelineCancelled;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of the PipelineManager class with default configuration.
        /// </summary>
        public PipelineManager()
            : this(new PipelineManagerConfiguration(), null, null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the PipelineManager class with specified configuration.
        /// </summary>
        /// <param name="configuration">The pipeline manager configuration.</param>
        /// <param name="logger">The logger instance (optional).</param>
        /// <param name="performanceMonitor">The performance monitor (optional).</param>
        /// <param name="eventBus">The event bus (optional).</param>
        public PipelineManager(
            PipelineManagerConfiguration configuration,
            ILogger logger = null,
            IPerformanceMonitor performanceMonitor = null,
            IEventBus eventBus = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? LogManager.GetLogger(typeof(PipelineManager));
            _performanceMonitor = performanceMonitor;
            _eventBus = eventBus;

            _validator = new PipelineValidator();
            _providers = new Dictionary<string, IPipelineProvider>();
            _activeExecutions = new Dictionary<string, PipelineExecution>();
            _pipelineDefinitions = new Dictionary<string, PipelineDefinition>();
            _artifactRegistries = new Dictionary<string, PipelineArtifactRegistry>();
            _environments = new Dictionary<string, PipelineEnvironment>();

            _concurrencySemaphore = new SemaphoreSlim(
                _configuration.MaxConcurrentPipelines,
                _configuration.MaxConcurrentPipelines);

            _globalCancellation = new CancellationTokenSource();

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };

            // Initialize timers;
            _heartbeatTimer = new Timer(
                HeartbeatCallback,
                null,
                TimeSpan.FromSeconds(DEFAULT_HEARTBEAT_INTERVAL_SECONDS),
                TimeSpan.FromSeconds(DEFAULT_HEARTBEAT_INTERVAL_SECONDS));

            _cleanupTimer = new Timer(
                CleanupCallback,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));

            InitializeDefaultProviders();
        }

        #endregion;

        #region Public Methods - Initialization;

        /// <summary>
        /// Initializes the pipeline manager asynchronously.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.Info("Initializing PipelineManager...");

                // Create required directories;
                CreateRequiredDirectories();

                // Load saved pipeline definitions;
                await LoadPipelineDefinitionsAsync(cancellationToken);

                // Load saved environments;
                await LoadEnvironmentsAsync(cancellationToken);

                // Load artifact registries;
                await LoadArtifactRegistriesAsync(cancellationToken);

                // Restore interrupted executions;
                await RestoreInterruptedExecutionsAsync(cancellationToken);

                // Initialize providers;
                await InitializeProvidersAsync(cancellationToken);

                _isInitialized = true;
                _logger.Info("PipelineManager initialized successfully.");

                OnPipelineManagerInitialized();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize PipelineManager: {ex.Message}", ex);
                throw new PipelineManagerInitializationException(
                    "Failed to initialize PipelineManager", ex);
            }
        }

        /// <summary>
        /// Registers a pipeline provider.
        /// </summary>
        /// <param name="provider">The pipeline provider to register.</param>
        public void RegisterProvider(IPipelineProvider provider)
        {
            ValidateNotDisposed();

            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            lock (_syncLock)
            {
                string providerName = provider.Name;
                if (_providers.ContainsKey(providerName))
                {
                    throw new InvalidOperationException(
                        $"Provider '{providerName}' is already registered");
                }

                _providers[providerName] = provider;
                _logger.Info($"Registered pipeline provider: {providerName}");
            }
        }

        /// <summary>
        /// Unregisters a pipeline provider.
        /// </summary>
        /// <param name="providerName">The name of the provider to unregister.</param>
        /// <returns>True if the provider was unregistered; otherwise, false.</returns>
        public bool UnregisterProvider(string providerName)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(providerName))
                return false;

            lock (_syncLock)
            {
                if (_providers.TryGetValue(providerName, out var provider))
                {
                    provider.Dispose();
                    _providers.Remove(providerName);
                    _logger.Info($"Unregistered pipeline provider: {providerName}");
                    return true;
                }

                return false;
            }
        }

        #endregion;

        #region Public Methods - Pipeline Definition Management;

        /// <summary>
        /// Creates a new pipeline definition.
        /// </summary>
        /// <param name="definition">The pipeline definition to create.</param>
        /// <returns>The created pipeline definition.</returns>
        public PipelineDefinition CreatePipelineDefinition(PipelineDefinition definition)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            _validator.ValidatePipelineDefinition(definition);

            lock (_syncLock)
            {
                if (_pipelineDefinitions.ContainsKey(definition.Id))
                {
                    throw new InvalidOperationException(
                        $"Pipeline definition with ID '{definition.Id}' already exists");
                }

                // Set creation timestamp;
                definition.CreatedAt = DateTime.UtcNow;
                definition.UpdatedAt = DateTime.UtcNow;

                _pipelineDefinitions[definition.Id] = definition;

                // Save to disk;
                SavePipelineDefinitionAsync(definition).ConfigureAwait(false);

                _logger.Info($"Created pipeline definition: {definition.Name} ({definition.Id})");

                return definition;
            }
        }

        /// <summary>
        /// Updates an existing pipeline definition.
        /// </summary>
        /// <param name="definition">The updated pipeline definition.</param>
        /// <returns>The updated pipeline definition.</returns>
        public PipelineDefinition UpdatePipelineDefinition(PipelineDefinition definition)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            _validator.ValidatePipelineDefinition(definition);

            lock (_syncLock)
            {
                if (!_pipelineDefinitions.ContainsKey(definition.Id))
                {
                    throw new KeyNotFoundException(
                        $"Pipeline definition with ID '{definition.Id}' not found");
                }

                definition.UpdatedAt = DateTime.UtcNow;
                _pipelineDefinitions[definition.Id] = definition;

                // Save to disk;
                SavePipelineDefinitionAsync(definition).ConfigureAwait(false);

                _logger.Info($"Updated pipeline definition: {definition.Name} ({definition.Id})");

                return definition;
            }
        }

        /// <summary>
        /// Deletes a pipeline definition.
        /// </summary>
        /// <param name="pipelineId">The ID of the pipeline definition to delete.</param>
        /// <returns>True if the definition was deleted; otherwise, false.</returns>
        public bool DeletePipelineDefinition(string pipelineId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(pipelineId))
                return false;

            lock (_syncLock)
            {
                if (!_pipelineDefinitions.ContainsKey(pipelineId))
                    return false;

                // Check if any executions are using this definition;
                if (_activeExecutions.Values.Any(e => e.PipelineId == pipelineId))
                {
                    throw new InvalidOperationException(
                        $"Cannot delete pipeline definition '{pipelineId}' because it has active executions");
                }

                _pipelineDefinitions.Remove(pipelineId);

                // Delete from disk;
                DeletePipelineDefinitionFileAsync(pipelineId).ConfigureAwait(false);

                _logger.Info($"Deleted pipeline definition: {pipelineId}");

                return true;
            }
        }

        /// <summary>
        /// Gets a pipeline definition by ID.
        /// </summary>
        /// <param name="pipelineId">The pipeline definition ID.</param>
        /// <returns>The pipeline definition, or null if not found.</returns>
        public PipelineDefinition GetPipelineDefinition(string pipelineId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(pipelineId))
                return null;

            lock (_syncLock)
            {
                return _pipelineDefinitions.TryGetValue(pipelineId, out var definition)
                    ? definition;
                    : null;
            }
        }

        /// <summary>
        /// Gets all pipeline definitions.
        /// </summary>
        /// <returns>A list of all pipeline definitions.</returns>
        public IReadOnlyList<PipelineDefinition> GetAllPipelineDefinitions()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                return _pipelineDefinitions.Values.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Searches for pipeline definitions matching the specified criteria.
        /// </summary>
        /// <param name="criteria">The search criteria.</param>
        /// <returns>A list of matching pipeline definitions.</returns>
        public IReadOnlyList<PipelineDefinition> SearchPipelineDefinitions(PipelineSearchCriteria criteria)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (criteria == null)
                return GetAllPipelineDefinitions();

            lock (_syncLock)
            {
                var query = _pipelineDefinitions.Values.AsEnumerable();

                if (!string.IsNullOrWhiteSpace(criteria.Name))
                {
                    query = query.Where(d => d.Name.Contains(criteria.Name,
                        StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(criteria.Description))
                {
                    query = query.Where(d => d.Description != null &&
                        d.Description.Contains(criteria.Description,
                        StringComparison.OrdinalIgnoreCase));
                }

                if (criteria.PipelineType.HasValue)
                {
                    query = query.Where(d => d.PipelineType == criteria.PipelineType.Value);
                }

                if (criteria.CreatedAfter.HasValue)
                {
                    query = query.Where(d => d.CreatedAt >= criteria.CreatedAfter.Value);
                }

                if (criteria.CreatedBefore.HasValue)
                {
                    query = query.Where(d => d.CreatedAt <= criteria.CreatedBefore.Value);
                }

                if (criteria.Tags != null && criteria.Tags.Count > 0)
                {
                    query = query.Where(d => d.Tags != null &&
                        criteria.Tags.Any(t => d.Tags.Contains(t)));
                }

                if (criteria.IsActive.HasValue)
                {
                    query = query.Where(d => d.IsActive == criteria.IsActive.Value);
                }

                return query.ToList().AsReadOnly();
            }
        }

        #endregion;

        #region Public Methods - Environment Management;

        /// <summary>
        /// Creates a new pipeline environment.
        /// </summary>
        /// <param name="environment">The environment to create.</param>
        /// <returns>The created environment.</returns>
        public PipelineEnvironment CreateEnvironment(PipelineEnvironment environment)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (environment == null)
                throw new ArgumentNullException(nameof(environment));

            _validator.ValidateEnvironment(environment);

            lock (_syncLock)
            {
                if (_environments.ContainsKey(environment.Id))
                {
                    throw new InvalidOperationException(
                        $"Environment with ID '{environment.Id}' already exists");
                }

                // Set creation timestamp;
                environment.CreatedAt = DateTime.UtcNow;
                environment.UpdatedAt = DateTime.UtcNow;

                _environments[environment.Id] = environment;

                // Save to disk;
                SaveEnvironmentAsync(environment).ConfigureAwait(false);

                _logger.Info($"Created environment: {environment.Name} ({environment.Id})");

                return environment;
            }
        }

        /// <summary>
        /// Updates an existing environment.
        /// </summary>
        /// <param name="environment">The updated environment.</param>
        /// <returns>The updated environment.</returns>
        public PipelineEnvironment UpdateEnvironment(PipelineEnvironment environment)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (environment == null)
                throw new ArgumentNullException(nameof(environment));

            _validator.ValidateEnvironment(environment);

            lock (_syncLock)
            {
                if (!_environments.ContainsKey(environment.Id))
                {
                    throw new KeyNotFoundException(
                        $"Environment with ID '{environment.Id}' not found");
                }

                environment.UpdatedAt = DateTime.UtcNow;
                _environments[environment.Id] = environment;

                // Save to disk;
                SaveEnvironmentAsync(environment).ConfigureAwait(false);

                _logger.Info($"Updated environment: {environment.Name} ({environment.Id})");

                return environment;
            }
        }

        /// <summary>
        /// Deletes an environment.
        /// </summary>
        /// <param name="environmentId">The ID of the environment to delete.</param>
        /// <returns>True if the environment was deleted; otherwise, false.</returns>
        public bool DeleteEnvironment(string environmentId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(environmentId))
                return false;

            lock (_syncLock)
            {
                if (!_environments.ContainsKey(environmentId))
                    return false;

                // Check if any executions are using this environment;
                if (_activeExecutions.Values.Any(e => e.EnvironmentId == environmentId))
                {
                    throw new InvalidOperationException(
                        $"Cannot delete environment '{environmentId}' because it has active executions");
                }

                _environments.Remove(environmentId);

                // Delete from disk;
                DeleteEnvironmentFileAsync(environmentId).ConfigureAwait(false);

                _logger.Info($"Deleted environment: {environmentId}");

                return true;
            }
        }

        /// <summary>
        /// Gets an environment by ID.
        /// </summary>
        /// <param name="environmentId">The environment ID.</param>
        /// <returns>The environment, or null if not found.</returns>
        public PipelineEnvironment GetEnvironment(string environmentId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(environmentId))
                return null;

            lock (_syncLock)
            {
                return _environments.TryGetValue(environmentId, out var environment)
                    ? environment;
                    : null;
            }
        }

        /// <summary>
        /// Gets all environments.
        /// </summary>
        /// <returns>A list of all environments.</returns>
        public IReadOnlyList<PipelineEnvironment> GetAllEnvironments()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                return _environments.Values.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Validates an environment configuration.
        /// </summary>
        /// <param name="environmentId">The environment ID.</param>
        /// <returns>Validation result.</returns>
        public async Task<EnvironmentValidationResult> ValidateEnvironmentAsync(string environmentId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(environmentId))
                throw new ArgumentException("Environment ID cannot be null or empty", nameof(environmentId));

            var environment = GetEnvironment(environmentId);
            if (environment == null)
                throw new KeyNotFoundException($"Environment '{environmentId}' not found");

            var result = new EnvironmentValidationResult;
            {
                EnvironmentId = environmentId,
                IsValid = true,
                ValidationTime = DateTime.UtcNow;
            };

            try
            {
                // Validate environment variables;
                if (environment.Variables != null)
                {
                    foreach (var variable in environment.Variables)
                    {
                        if (string.IsNullOrWhiteSpace(variable.Value) && variable.IsRequired)
                        {
                            result.AddError($"Required variable '{variable.Key}' is empty");
                        }
                    }
                }

                // Validate provider-specific configuration;
                if (!string.IsNullOrWhiteSpace(environment.ProviderName))
                {
                    if (_providers.TryGetValue(environment.ProviderName, out var provider))
                    {
                        var providerValidation = await provider.ValidateEnvironmentAsync(environment);
                        if (!providerValidation.IsValid)
                        {
                            foreach (var error in providerValidation.Errors)
                            {
                                result.AddError($"Provider validation error: {error}");
                            }
                        }
                    }
                    else;
                    {
                        result.AddError($"Provider '{environment.ProviderName}' is not registered");
                    }
                }

                // Validate connectivity if configured;
                if (environment.ValidateConnectivity)
                {
                    await ValidateEnvironmentConnectivityAsync(environment, result);
                }

                result.IsValid = !result.Errors.Any();
            }
            catch (Exception ex)
            {
                result.AddError($"Validation failed: {ex.Message}");
                result.IsValid = false;
            }

            return result;
        }

        #endregion;

        #region Public Methods - Pipeline Execution;

        /// <summary>
        /// Executes a pipeline asynchronously.
        /// </summary>
        /// <param name="request">The pipeline execution request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The pipeline execution result.</returns>
        public async Task<PipelineExecutionResult> ExecutePipelineAsync(
            PipelineExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _validator.ValidateExecutionRequest(request);

            // Check if pipeline definition exists;
            var pipelineDefinition = GetPipelineDefinition(request.PipelineId);
            if (pipelineDefinition == null)
                throw new KeyNotFoundException($"Pipeline definition '{request.PipelineId}' not found");

            // Check if environment exists;
            var environment = GetEnvironment(request.EnvironmentId);
            if (environment == null)
                throw new KeyNotFoundException($"Environment '{request.EnvironmentId}' not found");

            // Check if pipeline is active;
            if (!pipelineDefinition.IsActive)
                throw new InvalidOperationException($"Pipeline '{pipelineDefinition.Name}' is not active");

            // Wait for semaphore slot;
            var semaphoreAcquired = await _concurrencySemaphore.WaitAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);

            if (!semaphoreAcquired)
            {
                throw new PipelineExecutionException(
                    "Could not acquire pipeline execution slot within timeout");
            }

            string executionId = GenerateExecutionId(pipelineDefinition);
            PipelineExecution execution = null;

            try
            {
                // Create execution context;
                execution = new PipelineExecution;
                {
                    Id = executionId,
                    PipelineId = pipelineDefinition.Id,
                    PipelineName = pipelineDefinition.Name,
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    Status = PipelineExecutionStatus.Queued,
                    QueuedAt = DateTime.UtcNow,
                    CreatedBy = request.CreatedBy ?? "system",
                    Trigger = request.Trigger,
                    Parameters = request.Parameters ?? new Dictionary<string, string>(),
                    Variables = MergeVariables(pipelineDefinition, environment, request),
                    Metadata = request.Metadata ?? new Dictionary<string, object>()
                };

                // Add to active executions;
                lock (_syncLock)
                {
                    _activeExecutions[executionId] = execution;
                }

                // Fire queued event;
                OnPipelineQueued(new PipelineQueuedEventArgs;
                {
                    ExecutionId = executionId,
                    PipelineId = pipelineDefinition.Id,
                    PipelineName = pipelineDefinition.Name,
                    QueuedAt = execution.QueuedAt,
                    CreatedBy = execution.CreatedBy;
                });

                // Start execution in background;
                var executionTask = ExecutePipelineInternalAsync(
                    execution,
                    pipelineDefinition,
                    environment,
                    cancellationToken);

                // Return immediately with execution ID;
                return new PipelineExecutionResult;
                {
                    ExecutionId = executionId,
                    Status = PipelineExecutionStatus.Queued,
                    Message = "Pipeline execution queued successfully",
                    QueuedAt = execution.QueuedAt;
                };
            }
            catch (Exception ex)
            {
                // Release semaphore on error;
                _concurrencySemaphore.Release();

                // Clean up execution;
                if (execution != null)
                {
                    lock (_syncLock)
                    {
                        _activeExecutions.Remove(executionId);
                    }
                }

                _logger.Error($"Failed to queue pipeline execution: {ex.Message}", ex);
                throw new PipelineExecutionException(
                    $"Failed to queue pipeline execution: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets a pipeline execution by ID.
        /// </summary>
        /// <param name="executionId">The execution ID.</param>
        /// <returns>The pipeline execution, or null if not found.</returns>
        public PipelineExecution GetPipelineExecution(string executionId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                return null;

            lock (_syncLock)
            {
                return _activeExecutions.TryGetValue(executionId, out var execution)
                    ? execution;
                    : null;
            }
        }

        /// <summary>
        /// Gets all active pipeline executions.
        /// </summary>
        /// <returns>A list of all active pipeline executions.</returns>
        public IReadOnlyList<PipelineExecution> GetActiveExecutions()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                return _activeExecutions.Values.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Gets pipeline execution history.
        /// </summary>
        /// <param name="criteria">The search criteria.</param>
        /// <returns>A list of historical pipeline executions.</returns>
        public async Task<IReadOnlyList<PipelineExecutionHistory>> GetExecutionHistoryAsync(
            ExecutionHistoryCriteria criteria)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var history = new List<PipelineExecutionHistory>();
            var historyDir = Path.Combine(_configuration.WorkingDirectory, "history");

            if (!Directory.Exists(historyDir))
                return history.AsReadOnly();

            try
            {
                var historyFiles = Directory.GetFiles(historyDir, "*.history.json");

                foreach (var file in historyFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var executionHistory = JsonSerializer.Deserialize<PipelineExecutionHistory>(
                            json, _jsonOptions);

                        if (executionHistory != null && MatchesCriteria(executionHistory, criteria))
                        {
                            history.Add(executionHistory);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to load history file {file}: {ex.Message}");
                    }
                }

                // Sort by execution time (newest first)
                return history;
                    .OrderByDescending(h => h.StartedAt)
                    .ToList()
                    .AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load execution history: {ex.Message}", ex);
                throw new PipelineManagerException("Failed to load execution history", ex);
            }
        }

        /// <summary>
        /// Cancels a pipeline execution.
        /// </summary>
        /// <param name="executionId">The execution ID to cancel.</param>
        /// <param name="reason">The reason for cancellation.</param>
        /// <returns>True if the execution was cancelled; otherwise, false.</returns>
        public async Task<bool> CancelPipelineExecutionAsync(
            string executionId,
            string reason = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                return false;

            PipelineExecution execution;

            lock (_syncLock)
            {
                if (!_activeExecutions.TryGetValue(executionId, out execution))
                    return false;

                // Check if execution can be cancelled;
                if (!execution.CanCancel)
                    return false;

                // Update status;
                execution.Status = PipelineExecutionStatus.Cancelling;
                execution.CancellationRequestedAt = DateTime.UtcNow;
                execution.CancellationReason = reason;
            }

            try
            {
                // Notify execution task to cancel;
                execution.CancellationSource?.Cancel();

                // Wait for cancellation to complete;
                await Task.WhenAny(
                    execution.CompletionTask,
                    Task.Delay(TimeSpan.FromSeconds(30)));

                OnPipelineCancelled(new PipelineCancelledEventArgs;
                {
                    ExecutionId = executionId,
                    PipelineId = execution.PipelineId,
                    PipelineName = execution.PipelineName,
                    CancelledAt = DateTime.UtcNow,
                    Reason = reason;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to cancel pipeline execution {executionId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Retries a failed pipeline execution.
        /// </summary>
        /// <param name="executionId">The execution ID to retry.</param>
        /// <param name="parameters">Optional override parameters.</param>
        /// <returns>The new execution result.</returns>
        public async Task<PipelineExecutionResult> RetryPipelineExecutionAsync(
            string executionId,
            Dictionary<string, string> parameters = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution ID cannot be null or empty", nameof(executionId));

            // Load execution history;
            var history = await LoadExecutionHistoryAsync(executionId);
            if (history == null)
                throw new KeyNotFoundException($"Execution history for '{executionId}' not found");

            // Create retry request;
            var request = new PipelineExecutionRequest;
            {
                PipelineId = history.PipelineId,
                EnvironmentId = history.EnvironmentId,
                CreatedBy = history.CreatedBy,
                Trigger = PipelineTrigger.Manual,
                Parameters = parameters ?? history.Parameters,
                Metadata = new Dictionary<string, object>
                {
                    ["retryOf"] = executionId,
                    ["originalExecutionTime"] = history.StartedAt;
                }
            };

            return await ExecutePipelineAsync(request);
        }

        #endregion;

        #region Public Methods - Artifact Management;

        /// <summary>
        /// Gets artifacts for a pipeline execution.
        /// </summary>
        /// <param name="executionId">The execution ID.</param>
        /// <returns>A list of artifacts.</returns>
        public async Task<IReadOnlyList<PipelineArtifact>> GetArtifactsAsync(string executionId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                return new List<PipelineArtifact>().AsReadOnly();

            var artifactDir = GetArtifactDirectory(executionId);
            if (!Directory.Exists(artifactDir))
                return new List<PipelineArtifact>().AsReadOnly();

            var manifestFile = Path.Combine(artifactDir, ARTIFACT_MANIFEST_FILE);
            if (!File.Exists(manifestFile))
                return new List<PipelineArtifact>().AsReadOnly();

            try
            {
                var json = await File.ReadAllTextAsync(manifestFile);
                var artifacts = JsonSerializer.Deserialize<List<PipelineArtifact>>(json, _jsonOptions);
                return artifacts?.AsReadOnly() ?? new List<PipelineArtifact>().AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load artifacts for execution {executionId}: {ex.Message}", ex);
                return new List<PipelineArtifact>().AsReadOnly();
            }
        }

        /// <summary>
        /// Downloads a pipeline artifact.
        /// </summary>
        /// <param name="executionId">The execution ID.</param>
        /// <param name="artifactId">The artifact ID.</param>
        /// <param name="outputStream">The output stream to write to.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task DownloadArtifactAsync(
            string executionId,
            string artifactId,
            Stream outputStream)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution ID cannot be null or empty", nameof(executionId));

            if (string.IsNullOrWhiteSpace(artifactId))
                throw new ArgumentException("Artifact ID cannot be null or empty", nameof(artifactId));

            if (outputStream == null)
                throw new ArgumentNullException(nameof(outputStream));

            var artifacts = await GetArtifactsAsync(executionId);
            var artifact = artifacts.FirstOrDefault(a => a.Id == artifactId);

            if (artifact == null)
                throw new KeyNotFoundException($"Artifact '{artifactId}' not found for execution '{executionId}'");

            var artifactPath = GetArtifactPath(executionId, artifact);
            if (!File.Exists(artifactPath))
                throw new FileNotFoundException($"Artifact file not found: {artifactPath}");

            try
            {
                using var fileStream = File.OpenRead(artifactPath);
                await fileStream.CopyToAsync(outputStream);
            }
            catch (Exception ex)
            {
                throw new PipelineArtifactException(
                    $"Failed to download artifact '{artifactId}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Publishes a pipeline artifact.
        /// </summary>
        /// <param name="executionId">The execution ID.</param>
        /// <param name="artifact">The artifact to publish.</param>
        /// <param name="sourcePath">The source file path.</param>
        /// <returns>The published artifact.</returns>
        public async Task<PipelineArtifact> PublishArtifactAsync(
            string executionId,
            PipelineArtifact artifact,
            string sourcePath)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution ID cannot be null or empty", nameof(executionId));

            if (artifact == null)
                throw new ArgumentNullException(nameof(artifact));

            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be null or empty", nameof(sourcePath));

            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Source file not found: {sourcePath}");

            var execution = GetPipelineExecution(executionId);
            if (execution == null)
                throw new KeyNotFoundException($"Execution '{executionId}' not found");

            // Ensure artifact directory exists;
            var artifactDir = GetArtifactDirectory(executionId);
            Directory.CreateDirectory(artifactDir);

            // Generate artifact ID if not provided;
            if (string.IsNullOrWhiteSpace(artifact.Id))
            {
                artifact.Id = GenerateArtifactId(artifact.Name);
            }

            // Set metadata;
            artifact.ExecutionId = executionId;
            artifact.PipelineId = execution.PipelineId;
            artifact.CreatedAt = DateTime.UtcNow;

            // Copy file to artifact directory;
            var fileName = GetArtifactFileName(artifact);
            var targetPath = Path.Combine(artifactDir, fileName);

            File.Copy(sourcePath, targetPath, true);

            // Calculate file hash;
            artifact.Hash = await CalculateFileHashAsync(targetPath);
            artifact.Size = new FileInfo(targetPath).Length;
            artifact.FilePath = targetPath;

            // Update artifact registry
            await RegisterArtifactAsync(artifact);

            // Update execution artifacts;
            execution.Artifacts.Add(artifact);

            // Save artifact manifest;
            await SaveArtifactManifestAsync(executionId);

            // Fire artifact created event;
            OnPipelineArtifactCreated(new PipelineArtifactCreatedEventArgs;
            {
                ExecutionId = executionId,
                Artifact = artifact,
                CreatedAt = artifact.CreatedAt;
            });

            _logger.Info($"Published artifact '{artifact.Name}' for execution {executionId}");

            return artifact;
        }

        /// <summary>
        /// Cleans up old artifacts.
        /// </summary>
        /// <param name="retentionDays">The number of days to retain artifacts.</param>
        /// <returns>The cleanup result.</returns>
        public async Task<ArtifactCleanupResult> CleanupArtifactsAsync(int retentionDays = 30)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var result = new ArtifactCleanupResult;
            {
                StartTime = DateTime.UtcNow,
                RetentionDays = retentionDays;
            };

            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var artifactBaseDir = Path.Combine(_configuration.WorkingDirectory, "artifacts");

            if (!Directory.Exists(artifactBaseDir))
                return result;

            try
            {
                var executionDirs = Directory.GetDirectories(artifactBaseDir);

                foreach (var executionDir in executionDirs)
                {
                    var executionId = Path.GetFileName(executionDir);

                    // Check execution history;
                    var history = await LoadExecutionHistoryAsync(executionId);
                    if (history != null && history.CompletedAt < cutoffDate)
                    {
                        try
                        {
                            Directory.Delete(executionDir, true);
                            result.DeletedExecutionCount++;

                            // Remove from registry
                            await RemoveArtifactsFromRegistryAsync(executionId);
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Failed to delete artifacts for {executionId}: {ex.Message}");
                        }
                    }
                }

                result.EndTime = DateTime.UtcNow;
                result.Success = result.Errors.Count == 0;

                _logger.Info($"Artifact cleanup completed: {result.DeletedExecutionCount} executions cleaned up");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Artifact cleanup failed: {ex.Message}", ex);
                throw new PipelineManagerException("Artifact cleanup failed", ex);
            }
        }

        #endregion;

        #region Public Methods - Monitoring and Diagnostics;

        /// <summary>
        /// Gets pipeline execution statistics.
        /// </summary>
        /// <returns>Pipeline execution statistics.</returns>
        public PipelineExecutionStatistics GetStatistics()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                var activeExecutions = _activeExecutions.Values.ToList();

                return new PipelineExecutionStatistics;
                {
                    TotalExecutions = _totalExecutions,
                    ActiveExecutions = activeExecutions.Count,
                    QueuedExecutions = activeExecutions.Count(e => e.Status == PipelineExecutionStatus.Queued),
                    RunningExecutions = activeExecutions.Count(e => e.Status == PipelineExecutionStatus.Running),
                    SuccessfulExecutions = _totalExecutions - activeExecutions.Count, // Simplified;
                    FailedExecutions = 0, // Would need to track in history;
                    CancelledExecutions = 0, // Would need to track in history;
                    AverageExecutionTimeMs = AverageExecutionTimeMs,
                    MaxConcurrentPipelines = _configuration.MaxConcurrentPipelines,
                    AvailableSlots = _concurrencySemaphore.CurrentCount,
                    LastUpdated = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Gets pipeline execution logs.
        /// </summary>
        /// <param name="executionId">The execution ID.</param>
        /// <param name="stageId">Optional stage ID to filter logs.</param>
        /// <param name="logLevel">Optional log level to filter.</param>
        /// <param name="maxLines">Maximum number of lines to return.</param>
        /// <returns>The pipeline execution logs.</returns>
        public async Task<IReadOnlyList<PipelineLogEntry>> GetExecutionLogsAsync(
            string executionId,
            string stageId = null,
            LogLevel logLevel = LogLevel.All,
            int maxLines = 1000)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution ID cannot be null or empty", nameof(executionId));

            var logFile = GetLogFilePath(executionId);
            if (!File.Exists(logFile))
                return new List<PipelineLogEntry>().AsReadOnly();

            try
            {
                var logs = new List<PipelineLogEntry>();
                var lines = await File.ReadAllLinesAsync(logFile);

                foreach (var line in lines.Take(maxLines))
                {
                    try
                    {
                        var logEntry = JsonSerializer.Deserialize<PipelineLogEntry>(line, _jsonOptions);
                        if (logEntry != null && MatchesLogFilter(logEntry, stageId, logLevel))
                        {
                            logs.Add(logEntry);
                        }
                    }
                    catch
                    {
                        // Skip invalid log entries;
                    }
                }

                return logs.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to read logs for execution {executionId}: {ex.Message}", ex);
                throw new PipelineManagerException("Failed to read execution logs", ex);
            }
        }

        /// <summary>
        /// Gets pipeline execution metrics.
        /// </summary>
        /// <param name="executionId">The execution ID.</param>
        /// <returns>Pipeline execution metrics.</returns>
        public async Task<PipelineExecutionMetrics> GetExecutionMetricsAsync(string executionId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution ID cannot be null or empty", nameof(executionId));

            var execution = GetPipelineExecution(executionId);
            if (execution == null)
            {
                // Try to load from history;
                var history = await LoadExecutionHistoryAsync(executionId);
                if (history == null)
                    throw new KeyNotFoundException($"Execution '{executionId}' not found");

                execution = history.ToPipelineExecution();
            }

            var metrics = new PipelineExecutionMetrics;
            {
                ExecutionId = executionId,
                PipelineId = execution.PipelineId,
                PipelineName = execution.PipelineName,
                StartTime = execution.StartedAt,
                EndTime = execution.CompletedAt,
                Duration = execution.Duration,
                Status = execution.Status,
                StageCount = execution.Stages.Count,
                CompletedStageCount = execution.Stages.Count(s => s.Status == PipelineStageStatus.Completed),
                FailedStageCount = execution.Stages.Count(s => s.Status == PipelineStageStatus.Failed),
                ArtifactCount = execution.Artifacts.Count,
                TotalArtifactSize = execution.Artifacts.Sum(a => a.Size),
                ResourceUsage = execution.ResourceUsage,
                ErrorCount = execution.Errors.Count,
                WarningCount = execution.Warnings.Count;
            };

            // Calculate stage statistics;
            if (execution.Stages.Any())
            {
                metrics.AverageStageDuration = execution.Stages;
                    .Where(s => s.CompletedAt.HasValue && s.StartedAt.HasValue)
                    .Average(s => (s.CompletedAt.Value - s.StartedAt.Value).TotalMilliseconds);

                metrics.LongestStage = execution.Stages;
                    .OrderByDescending(s => s.Duration)
                    .FirstOrDefault()?.Name;
            }

            return metrics;
        }

        /// <summary>
        /// Gets pipeline health status.
        /// </summary>
        /// <returns>Pipeline health status.</returns>
        public PipelineHealthStatus GetHealthStatus()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var status = new PipelineHealthStatus;
            {
                Timestamp = DateTime.UtcNow,
                IsHealthy = true,
                Components = new Dictionary<string, ComponentHealth>()
            };

            // Check providers;
            foreach (var provider in _providers.Values)
            {
                var componentHealth = provider.GetHealthStatus();
                status.Components[$"Provider.{provider.Name}"] = componentHealth;

                if (!componentHealth.IsHealthy)
                {
                    status.IsHealthy = false;
                    status.Errors.Add($"Provider '{provider.Name}' is unhealthy: {componentHealth.Message}");
                }
            }

            // Check disk space;
            var driveInfo = new DriveInfo(Path.GetPathRoot(_configuration.WorkingDirectory));
            var freeSpacePercent = (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;

            status.Components["Disk"] = new ComponentHealth;
            {
                IsHealthy = freeSpacePercent > 10,
                Message = $"Free disk space: {freeSpacePercent:F1}%",
                Metrics = new Dictionary<string, double>
                {
                    ["FreeSpacePercent"] = freeSpacePercent,
                    ["FreeSpaceGB"] = driveInfo.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                }
            };

            if (freeSpacePercent <= 10)
            {
                status.IsHealthy = false;
                status.Errors.Add($"Low disk space: {freeSpacePercent:F1}% free");
            }

            // Check memory usage;
            var process = Process.GetCurrentProcess();
            var memoryUsageMB = process.WorkingSet64 / 1024.0 / 1024;

            status.Components["Memory"] = new ComponentHealth;
            {
                IsHealthy = memoryUsageMB < 1024, // 1GB threshold;
                Message = $"Memory usage: {memoryUsageMB:F1} MB",
                Metrics = new Dictionary<string, double>
                {
                    ["MemoryUsageMB"] = memoryUsageMB;
                }
            };

            if (memoryUsageMB >= 1024)
            {
                status.Warnings.Add($"High memory usage: {memoryUsageMB:F1} MB");
            }

            return status;
        }

        #endregion;

        #region Private Methods - Pipeline Execution;

        private async Task ExecutePipelineInternalAsync(
            PipelineExecution execution,
            PipelineDefinition pipelineDefinition,
            PipelineEnvironment environment,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _globalCancellation.Token);

            execution.CancellationSource = executionCancellation;
            execution.StartedAt = DateTime.UtcNow;
            execution.Status = PipelineExecutionStatus.Running;

            // Create log file;
            var logFile = GetLogFilePath(execution.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(logFile));

            using var logWriter = new StreamWriter(logFile, append: true);

            try
            {
                // Fire execution started event;
                OnPipelineExecutionStarted(new PipelineExecutionStartedEventArgs;
                {
                    ExecutionId = execution.Id,
                    PipelineId = pipelineDefinition.Id,
                    PipelineName = pipelineDefinition.Name,
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    StartedAt = execution.StartedAt,
                    CreatedBy = execution.CreatedBy,
                    Trigger = execution.Trigger;
                });

                // Log execution start;
                await LogExecutionEventAsync(logWriter, execution,
                    "Pipeline execution started", LogLevel.Info);

                // Select provider;
                var provider = SelectProvider(pipelineDefinition, environment);

                // Prepare execution context;
                var context = new PipelineExecutionContext;
                {
                    Execution = execution,
                    PipelineDefinition = pipelineDefinition,
                    Environment = environment,
                    Provider = provider,
                    WorkingDirectory = GetExecutionWorkingDirectory(execution.Id),
                    LogWriter = logWriter,
                    CancellationToken = executionCancellation.Token;
                };

                // Create working directory;
                Directory.CreateDirectory(context.WorkingDirectory);

                // Execute pipeline stages;
                await ExecutePipelineStagesAsync(context);

                // Mark execution as completed;
                execution.CompletedAt = DateTime.UtcNow;
                execution.Status = PipelineExecutionStatus.Completed;
                execution.Duration = stopwatch.Elapsed;

                // Update statistics;
                Interlocked.Increment(ref _totalExecutions);
                Interlocked.Add(ref _totalExecutionTimeMs, (long)stopwatch.Elapsed.TotalMilliseconds);

                // Save execution history;
                await SaveExecutionHistoryAsync(execution);

                // Log completion;
                await LogExecutionEventAsync(logWriter, execution,
                    $"Pipeline execution completed successfully in {execution.Duration.TotalSeconds:F1} seconds",
                    LogLevel.Info);

                // Fire completion event;
                OnPipelineExecutionCompleted(new PipelineExecutionCompletedEventArgs;
                {
                    ExecutionId = execution.Id,
                    PipelineId = pipelineDefinition.Id,
                    PipelineName = pipelineDefinition.Name,
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    StartedAt = execution.StartedAt,
                    CompletedAt = execution.CompletedAt,
                    Duration = execution.Duration,
                    Status = execution.Status,
                    Artifacts = execution.Artifacts;
                });
            }
            catch (OperationCanceledException)
            {
                // Execution was cancelled;
                execution.CompletedAt = DateTime.UtcNow;
                execution.Status = PipelineExecutionStatus.Cancelled;
                execution.Duration = stopwatch.Elapsed;

                await LogExecutionEventAsync(logWriter, execution,
                    $"Pipeline execution cancelled after {execution.Duration.TotalSeconds:F1} seconds",
                    LogLevel.Warning);

                await SaveExecutionHistoryAsync(execution);

                OnPipelineCancelled(new PipelineCancelledEventArgs;
                {
                    ExecutionId = execution.Id,
                    PipelineId = pipelineDefinition.Id,
                    PipelineName = pipelineDefinition.Name,
                    CancelledAt = execution.CompletedAt.Value,
                    Reason = execution.CancellationReason;
                });
            }
            catch (Exception ex)
            {
                // Execution failed;
                execution.CompletedAt = DateTime.UtcNow;
                execution.Status = PipelineExecutionStatus.Failed;
                execution.Duration = stopwatch.Elapsed;
                execution.Errors.Add(ex.Message);

                await LogExecutionEventAsync(logWriter, execution,
                    $"Pipeline execution failed after {execution.Duration.TotalSeconds:F1} seconds: {ex.Message}",
                    LogLevel.Error, ex);

                await SaveExecutionHistoryAsync(execution);

                OnPipelineExecutionFailed(new PipelineExecutionFailedEventArgs;
                {
                    ExecutionId = execution.Id,
                    PipelineId = pipelineDefinition.Id,
                    PipelineName = pipelineDefinition.Name,
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    StartedAt = execution.StartedAt,
                    FailedAt = execution.CompletedAt.Value,
                    Duration = execution.Duration,
                    Error = ex.Message,
                    Exception = ex;
                });
            }
            finally
            {
                // Clean up;
                execution.CompletionTask = Task.CompletedTask;
                executionCancellation.Dispose();

                // Remove from active executions;
                lock (_syncLock)
                {
                    _activeExecutions.Remove(execution.Id);
                }

                // Release semaphore;
                _concurrencySemaphore.Release();

                stopwatch.Stop();

                // Update status;
                OnPipelineStatusChanged(new PipelineStatusChangedEventArgs;
                {
                    ExecutionId = execution.Id,
                    OldStatus = PipelineExecutionStatus.Running,
                    NewStatus = execution.Status,
                    ChangedAt = DateTime.UtcNow;
                });
            }
        }

        private async Task ExecutePipelineStagesAsync(PipelineExecutionContext context)
        {
            var pipelineDefinition = context.PipelineDefinition;
            var execution = context.Execution;

            foreach (var stageDefinition in pipelineDefinition.Stages)
            {
                // Check cancellation;
                context.CancellationToken.ThrowIfCancellationRequested();

                var stage = new PipelineStageExecution;
                {
                    Id = GenerateStageId(stageDefinition.Name),
                    Name = stageDefinition.Name,
                    StageDefinitionId = stageDefinition.Id,
                    Status = PipelineStageStatus.Pending,
                    StartedAt = null,
                    CompletedAt = null;
                };

                execution.Stages.Add(stage);

                // Update stage status;
                stage.Status = PipelineStageStatus.Running;
                stage.StartedAt = DateTime.UtcNow;

                // Fire stage started event;
                OnPipelineStageStarted(new PipelineStageStartedEventArgs;
                {
                    ExecutionId = execution.Id,
                    StageId = stage.Id,
                    StageName = stage.Name,
                    StartedAt = stage.StartedAt.Value;
                });

                await LogExecutionEventAsync(context.LogWriter, execution,
                    $"Stage '{stage.Name}' started",
                    LogLevel.Info, stageId: stage.Id);

                try
                {
                    // Execute stage;
                    await ExecuteStageAsync(context, stage, stageDefinition);

                    // Mark stage as completed;
                    stage.Status = PipelineStageStatus.Completed;
                    stage.CompletedAt = DateTime.UtcNow;

                    await LogExecutionEventAsync(context.LogWriter, execution,
                        $"Stage '{stage.Name}' completed in {stage.Duration.TotalSeconds:F1} seconds",
                        LogLevel.Info, stageId: stage.Id);

                    // Fire stage completed event;
                    OnPipelineStageCompleted(new PipelineStageCompletedEventArgs;
                    {
                        ExecutionId = execution.Id,
                        StageId = stage.Id,
                        StageName = stage.Name,
                        StartedAt = stage.StartedAt.Value,
                        CompletedAt = stage.CompletedAt.Value,
                        Duration = stage.Duration,
                        Status = stage.Status;
                    });
                }
                catch (Exception ex)
                {
                    // Mark stage as failed;
                    stage.Status = PipelineStageStatus.Failed;
                    stage.CompletedAt = DateTime.UtcNow;
                    stage.Error = ex.Message;

                    await LogExecutionEventAsync(context.LogWriter, execution,
                        $"Stage '{stage.Name}' failed after {stage.Duration.TotalSeconds:F1} seconds: {ex.Message}",
                        LogLevel.Error, ex, stage.Id);

                    // Check if pipeline should continue on stage failure;
                    if (!stageDefinition.ContinueOnError)
                    {
                        throw new PipelineStageException(
                            $"Stage '{stage.Name}' failed: {ex.Message}", ex);
                    }

                    execution.Warnings.Add($"Stage '{stage.Name}' failed but pipeline continued: {ex.Message}");
                }
            }
        }

        private async Task ExecuteStageAsync(
            PipelineExecutionContext context,
            PipelineStageExecution stage,
            PipelineStageDefinition stageDefinition)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Execute stage tasks;
                foreach (var task in stageDefinition.Tasks)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    await ExecuteStageTaskAsync(context, stage, task);
                }

                // Execute post-stage actions;
                if (stageDefinition.Actions != null)
                {
                    foreach (var action in stageDefinition.Actions)
                    {
                        await ExecuteStageActionAsync(context, stage, action);
                    }
                }
            }
            finally
            {
                stopwatch.Stop();
                stage.Duration = stopwatch.Elapsed;
            }
        }

        private async Task ExecuteStageTaskAsync(
            PipelineExecutionContext context,
            PipelineStageExecution stage,
            PipelineTask task)
        {
            var provider = context.Provider;

            await LogExecutionEventAsync(context.LogWriter, context.Execution,
                $"Executing task: {task.Name}",
                LogLevel.Debug, stageId: stage.Id);

            try
            {
                // Execute task using provider;
                var result = await provider.ExecuteTaskAsync(task, context);

                if (!result.Success)
                {
                    throw new PipelineTaskException(
                        $"Task '{task.Name}' failed: {result.ErrorMessage}");
                }

                // Collect artifacts if any;
                if (result.Artifacts != null)
                {
                    foreach (var artifact in result.Artifacts)
                    {
                        await PublishArtifactAsync(
                            context.Execution.Id,
                            artifact,
                            artifact.FilePath);
                    }
                }

                // Update resource usage;
                if (result.ResourceUsage != null)
                {
                    context.Execution.ResourceUsage.Merge(result.ResourceUsage);
                }
            }
            catch (Exception ex)
            {
                await LogExecutionEventAsync(context.LogWriter, context.Execution,
                    $"Task '{task.Name}' failed: {ex.Message}",
                    LogLevel.Error, ex, stage.Id);

                throw;
            }
        }

        private async Task ExecuteStageActionAsync(
            PipelineExecutionContext context,
            PipelineStageExecution stage,
            PipelineAction action)
        {
            await LogExecutionEventAsync(context.LogWriter, context.Execution,
                $"Executing action: {action.Type}",
                LogLevel.Debug, stageId: stage.Id);

            try
            {
                switch (action.Type)
                {
                    case PipelineActionType.SendNotification:
                        await ExecuteNotificationActionAsync(context, stage, action);
                        break;

                    case PipelineActionType.UpdateStatus:
                        await ExecuteStatusUpdateActionAsync(context, stage, action);
                        break;

                    case PipelineActionType.ExecuteScript:
                        await ExecuteScriptActionAsync(context, stage, action);
                        break;

                    case PipelineActionType.Wait:
                        await ExecuteWaitActionAsync(context, stage, action);
                        break;

                    default:
                        _logger.Warn($"Unknown action type: {action.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                await LogExecutionEventAsync(context.LogWriter, context.Execution,
                    $"Action '{action.Type}' failed: {ex.Message}",
                    LogLevel.Warning, ex, stage.Id);

                // Actions typically shouldn't fail the stage;
            }
        }

        private async Task ExecuteNotificationActionAsync(
            PipelineExecutionContext context,
            PipelineStageExecution stage,
            PipelineAction action)
        {
            // Implementation for sending notifications;
            // Could integrate with email, Slack, Teams, etc.
            _logger.Info($"Notification action: {action.Parameters.GetValueOrDefault("message")}");

            await Task.CompletedTask;
        }

        private async Task ExecuteStatusUpdateActionAsync(
            PipelineExecutionContext context,
            PipelineStageExecution stage,
            PipelineAction action)
        {
            // Update external status (e.g., GitHub commit status)
            _logger.Info($"Status update action for stage '{stage.Name}'");

            await Task.CompletedTask;
        }

        private async Task ExecuteScriptActionAsync(
            PipelineExecutionContext context,
            PipelineStageExecution stage,
            PipelineAction action)
        {
            var scriptPath = action.Parameters.GetValueOrDefault("scriptPath");
            if (string.IsNullOrWhiteSpace(scriptPath))
                return;

            var fullScriptPath = Path.Combine(context.WorkingDirectory, scriptPath);
            if (!File.Exists(fullScriptPath))
            {
                _logger.Warn($"Script not found: {fullScriptPath}");
                return;
            }

            // Execute script based on extension;
            var extension = Path.GetExtension(fullScriptPath).ToLowerInvariant();

            var process = new Process;
            {
                StartInfo = new ProcessStartInfo;
                {
                    FileName = GetScriptInterpreter(extension),
                    Arguments = GetScriptArguments(extension, fullScriptPath),
                    WorkingDirectory = context.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true;
                }
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(context.CancellationToken);

            if (process.ExitCode != 0)
            {
                _logger.Warn($"Script exited with code {process.ExitCode}: {error}");
            }

            await LogExecutionEventAsync(context.LogWriter, context.Execution,
                $"Script output: {output}",
                LogLevel.Info, stageId: stage.Id);
        }

        private async Task ExecuteWaitActionAsync(
            PipelineExecutionContext context,
            PipelineStageExecution stage,
            PipelineAction action)
        {
            if (action.Parameters.TryGetValue("duration", out var durationStr) &&
                int.TryParse(durationStr, out var durationSeconds))
            {
                await LogExecutionEventAsync(context.LogWriter, context.Execution,
                    $"Waiting for {durationSeconds} seconds",
                    LogLevel.Info, stageId: stage.Id);

                await Task.Delay(TimeSpan.FromSeconds(durationSeconds), context.CancellationToken);
            }
        }

        #endregion;

        #region Private Methods - Helper Methods;

        private IPipelineProvider SelectProvider(
            PipelineDefinition pipelineDefinition,
            PipelineEnvironment environment)
        {
            // First, try environment-specific provider;
            if (!string.IsNullOrWhiteSpace(environment.ProviderName))
            {
                if (_providers.TryGetValue(environment.ProviderName, out var provider))
                {
                    return provider;
                }
            }

            // Then, try pipeline-specific provider;
            if (!string.IsNullOrWhiteSpace(pipelineDefinition.ProviderName))
            {
                if (_providers.TryGetValue(pipelineDefinition.ProviderName, out var provider))
                {
                    return provider;
                }
            }

            // Finally, use default provider;
            if (_providers.TryGetValue("Default", out var defaultProvider))
            {
                return defaultProvider;
            }

            throw new PipelineProviderException("No suitable pipeline provider found");
        }

        private Dictionary<string, string> MergeVariables(
            PipelineDefinition pipelineDefinition,
            PipelineEnvironment environment,
            PipelineExecutionRequest request)
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Add environment variables;
            if (environment.Variables != null)
            {
                foreach (var variable in environment.Variables)
                {
                    variables[variable.Key] = variable.Value;
                }
            }

            // Add pipeline variables;
            if (pipelineDefinition.Variables != null)
            {
                foreach (var variable in pipelineDefinition.Variables)
                {
                    variables[variable.Key] = variable.Value;
                }
            }

            // Add execution parameters (override existing)
            if (request.Parameters != null)
            {
                foreach (var parameter in request.Parameters)
                {
                    variables[parameter.Key] = parameter.Value;
                }
            }

            // Add system variables;
            variables["Pipeline.Id"] = pipelineDefinition.Id;
            variables["Pipeline.Name"] = pipelineDefinition.Name;
            variables["Environment.Id"] = environment.Id;
            variables["Environment.Name"] = environment.Name;
            variables["Execution.StartTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            variables["WorkingDirectory"] = _configuration.WorkingDirectory;

            return variables;
        }

        private string GenerateExecutionId(PipelineDefinition pipelineDefinition)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            var prefix = pipelineDefinition.Id.Replace("-", "").Substring(0, 4).ToUpper();

            return $"{prefix}-{timestamp}-{random}";
        }

        private string GenerateStageId(string stageName)
        {
            var sanitized = stageName.ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("_", "-");

            var timestamp = DateTime.UtcNow.ToString("HHmmss");
            var random = new Random().Next(100, 999);

            return $"{sanitized}-{timestamp}-{random}";
        }

        private string GenerateArtifactId(string artifactName)
        {
            var sanitized = artifactName.ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("_", "-")
                .Replace(".", "-");

            var timestamp = DateTime.UtcNow.ToString("HHmmss");
            var random = new Random().Next(1000, 9999);

            return $"{sanitized}-{timestamp}-{random}";
        }

        private async Task<string> CalculateFileHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private string GetScriptInterpreter(string extension)
        {
            return extension switch;
            {
                ".ps1" => "powershell",
                ".sh" => "bash",
                ".bat" => "cmd",
                ".cmd" => "cmd",
                ".py" => "python",
                ".js" => "node",
                _ => throw new NotSupportedException($"Unsupported script extension: {extension}")
            };
        }

        private string GetScriptArguments(string extension, string scriptPath)
        {
            return extension switch;
            {
                ".ps1" => $"-File \"{scriptPath}\"",
                ".sh" => $"\"{scriptPath}\"",
                ".bat" => $"/C \"{scriptPath}\"",
                ".cmd" => $"/C \"{scriptPath}\"",
                ".py" => $"\"{scriptPath}\"",
                ".js" => $"\"{scriptPath}\"",
                _ => $"\"{scriptPath}\""
            };
        }

        private bool MatchesCriteria(PipelineExecutionHistory history, ExecutionHistoryCriteria criteria)
        {
            if (criteria == null)
                return true;

            if (!string.IsNullOrWhiteSpace(criteria.PipelineId) &&
                history.PipelineId != criteria.PipelineId)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.EnvironmentId) &&
                history.EnvironmentId != criteria.EnvironmentId)
                return false;

            if (criteria.Status.HasValue && history.Status != criteria.Status.Value)
                return false;

            if (criteria.StartedAfter.HasValue && history.StartedAt < criteria.StartedAfter.Value)
                return false;

            if (criteria.StartedBefore.HasValue && history.StartedAt > criteria.StartedBefore.Value)
                return false;

            if (criteria.MinDuration.HasValue && history.Duration < criteria.MinDuration.Value)
                return false;

            if (criteria.MaxDuration.HasValue && history.Duration > criteria.MaxDuration.Value)
                return false;

            return true;
        }

        private bool MatchesLogFilter(PipelineLogEntry logEntry, string stageId, LogLevel logLevel)
        {
            if (!string.IsNullOrWhiteSpace(stageId) && logEntry.StageId != stageId)
                return false;

            if (logLevel != LogLevel.All && logEntry.Level < logLevel)
                return false;

            return true;
        }

        #endregion;

        #region Private Methods - File System Operations;

        private void CreateRequiredDirectories()
        {
            var directories = new[]
            {
                _configuration.WorkingDirectory,
                Path.Combine(_configuration.WorkingDirectory, "pipelines"),
                Path.Combine(_configuration.WorkingDirectory, "environments"),
                Path.Combine(_configuration.WorkingDirectory, "artifacts"),
                Path.Combine(_configuration.WorkingDirectory, "history"),
                Path.Combine(_configuration.WorkingDirectory, "logs"),
                Path.Combine(_configuration.WorkingDirectory, "state")
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }

        private string GetPipelineDefinitionPath(string pipelineId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "pipelines",
                $"{pipelineId}.json");
        }

        private string GetEnvironmentPath(string environmentId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "environments",
                $"{environmentId}.json");
        }

        private string GetArtifactDirectory(string executionId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "artifacts",
                executionId);
        }

        private string GetArtifactPath(string executionId, PipelineArtifact artifact)
        {
            var artifactDir = GetArtifactDirectory(executionId);
            var fileName = GetArtifactFileName(artifact);
            return Path.Combine(artifactDir, fileName);
        }

        private string GetArtifactFileName(PipelineArtifact artifact)
        {
            var extension = Path.GetExtension(artifact.FilePath);
            return $"{artifact.Id}{extension}";
        }

        private string GetLogFilePath(string executionId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "logs",
                $"{PIPELINE_LOG_FILE_PREFIX}{executionId}.log");
        }

        private string GetExecutionWorkingDirectory(string executionId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "workspace",
                executionId);
        }

        private string GetStateFilePath(string executionId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "state",
                $"{executionId}{PIPELINE_STATE_FILE_EXTENSION}");
        }

        private async Task SavePipelineDefinitionAsync(PipelineDefinition definition)
        {
            var filePath = GetPipelineDefinitionPath(definition.Id);
            var json = JsonSerializer.Serialize(definition, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }

        private async Task SaveEnvironmentAsync(PipelineEnvironment environment)
        {
            var filePath = GetEnvironmentPath(environment.Id);
            var json = JsonSerializer.Serialize(environment, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }

        private async Task SaveArtifactManifestAsync(string executionId)
        {
            var artifacts = await GetArtifactsAsync(executionId);
            var manifestFile = Path.Combine(GetArtifactDirectory(executionId), ARTIFACT_MANIFEST_FILE);

            var json = JsonSerializer.Serialize(artifacts, _jsonOptions);
            await File.WriteAllTextAsync(manifestFile, json);
        }

        private async Task SaveExecutionHistoryAsync(PipelineExecution execution)
        {
            var history = execution.ToHistory();
            var historyFile = Path.Combine(_configuration.WorkingDirectory,
                "history",
                $"{execution.Id}.history.json");

            var json = JsonSerializer.Serialize(history, _jsonOptions);
            await File.WriteAllTextAsync(historyFile, json);
        }

        private async Task DeletePipelineDefinitionFileAsync(string pipelineId)
        {
            var filePath = GetPipelineDefinitionPath(pipelineId);
            if (File.Exists(filePath))
            {
                await Task.Run(() => File.Delete(filePath));
            }
        }

        private async Task DeleteEnvironmentFileAsync(string environmentId)
        {
            var filePath = GetEnvironmentPath(environmentId);
            if (File.Exists(filePath))
            {
                await Task.Run(() => File.Delete(filePath));
            }
        }

        private async Task LoadPipelineDefinitionsAsync(CancellationToken cancellationToken)
        {
            var pipelinesDir = Path.Combine(_configuration.WorkingDirectory, "pipelines");
            if (!Directory.Exists(pipelinesDir))
                return;

            var files = Directory.GetFiles(pipelinesDir, "*.json");

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var definition = JsonSerializer.Deserialize<PipelineDefinition>(json, _jsonOptions);

                    if (definition != null)
                    {
                        _pipelineDefinitions[definition.Id] = definition;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to load pipeline definition from {file}: {ex.Message}");
                }
            }
        }

        private async Task LoadEnvironmentsAsync(CancellationToken cancellationToken)
        {
            var environmentsDir = Path.Combine(_configuration.WorkingDirectory, "environments");
            if (!Directory.Exists(environmentsDir))
                return;

            var files = Directory.GetFiles(environmentsDir, "*.json");

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var environment = JsonSerializer.Deserialize<PipelineEnvironment>(json, _jsonOptions);

                    if (environment != null)
                    {
                        _environments[environment.Id] = environment;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to load environment from {file}: {ex.Message}");
                }
            }
        }

        private async Task LoadArtifactRegistriesAsync(CancellationToken cancellationToken)
        {
            var registryFile = Path.Combine(_configuration.WorkingDirectory, "artifact-registry.json");
            if (!File.Exists(registryFile))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(registryFile, cancellationToken);
                var registries = JsonSerializer.Deserialize<Dictionary<string, PipelineArtifactRegistry>>(
                    json, _jsonOptions);

                if (registries != null)
                {
                    foreach (var kvp in registries)
                    {
                        _artifactRegistries[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load artifact registries: {ex.Message}");
            }
        }

        private async Task RestoreInterruptedExecutionsAsync(CancellationToken cancellationToken)
        {
            var stateDir = Path.Combine(_configuration.WorkingDirectory, "state");
            if (!Directory.Exists(stateDir))
                return;

            var stateFiles = Directory.GetFiles(stateDir, $"*{PIPELINE_STATE_FILE_EXTENSION}");

            foreach (var stateFile in stateFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var json = await File.ReadAllTextAsync(stateFile, cancellationToken);
                    var execution = JsonSerializer.Deserialize<PipelineExecution>(json, _jsonOptions);

                    if (execution != null && execution.Status == PipelineExecutionStatus.Running)
                    {
                        // Mark as failed since we don't know the actual state;
                        execution.Status = PipelineExecutionStatus.Failed;
                        execution.CompletedAt = DateTime.UtcNow;
                        execution.Errors.Add("Pipeline interrupted due to system restart");

                        // Save to history;
                        await SaveExecutionHistoryAsync(execution);

                        // Delete state file;
                        File.Delete(stateFile);

                        _logger.Warn($"Restored interrupted execution: {execution.Id}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to restore execution state from {stateFile}: {ex.Message}");
                }
            }
        }

        private async Task<PipelineExecutionHistory> LoadExecutionHistoryAsync(string executionId)
        {
            var historyFile = Path.Combine(_configuration.WorkingDirectory,
                "history",
                $"{executionId}.history.json");

            if (!File.Exists(historyFile))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(historyFile);
                return JsonSerializer.Deserialize<PipelineExecutionHistory>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load execution history for {executionId}: {ex.Message}");
                return null;
            }
        }

        private async Task RegisterArtifactAsync(PipelineArtifact artifact)
        {
            var registryKey = $"{artifact.PipelineId}_{artifact.Name}";

            if (!_artifactRegistries.TryGetValue(registryKey, out var registry))
            {
                registry = new PipelineArtifactRegistry
                {
                    PipelineId = artifact.PipelineId,
                    ArtifactName = artifact.Name,
                    LatestVersion = artifact.Version,
                    LatestExecutionId = artifact.ExecutionId,
                    LatestCreatedAt = artifact.CreatedAt;
                };

                _artifactRegistries[registryKey] = registry
            }
            else;
            {
                registry.LatestVersion = artifact.Version;
                registry.LatestExecutionId = artifact.ExecutionId;
                registry.LatestCreatedAt = artifact.CreatedAt;
            }

            registry.Versions.Add(new ArtifactVersion;
            {
                Version = artifact.Version,
                ExecutionId = artifact.ExecutionId,
                CreatedAt = artifact.CreatedAt,
                Hash = artifact.Hash,
                Size = artifact.Size;
            });

            // Keep only last 10 versions;
            if (registry.Versions.Count > 10)
            {
                registry.Versions = registry.Versions;
                    .OrderByDescending(v => v.CreatedAt)
                    .Take(10)
                    .ToList();
            }

            // Save registry
            await SaveArtifactRegistryAsync();
        }

        private async Task RemoveArtifactsFromRegistryAsync(string executionId)
        {
            var registriesToUpdate = _artifactRegistries.Values;
                .Where(r => r.Versions.Any(v => v.ExecutionId == executionId))
                .ToList();

            foreach (var registry in registriesToUpdate)
            {
                registry.Versions.RemoveAll(v => v.ExecutionId == executionId);

                // Update latest if needed;
                if (registry.LatestExecutionId == executionId)
                {
                    var latestVersion = registry.Versions;
                        .OrderByDescending(v => v.CreatedAt)
                        .FirstOrDefault();

                    if (latestVersion != null)
                    {
                        registry.LatestVersion = latestVersion.Version;
                        registry.LatestExecutionId = latestVersion.ExecutionId;
                        registry.LatestCreatedAt = latestVersion.CreatedAt;
                    }
                    else;
                    {
                        _artifactRegistries.Remove($"{registry.PipelineId}_{registry.ArtifactName}");
                    }
                }
            }

            await SaveArtifactRegistryAsync();
        }

        private async Task SaveArtifactRegistryAsync()
        {
            var registryFile = Path.Combine(_configuration.WorkingDirectory, "artifact-registry.json");
            var json = JsonSerializer.Serialize(_artifactRegistries, _jsonOptions);
            await File.WriteAllTextAsync(registryFile, json);
        }

        private async Task LogExecutionEventAsync(
            StreamWriter logWriter,
            PipelineExecution execution,
            string message,
            LogLevel level,
            Exception exception = null,
            string stageId = null)
        {
            var logEntry = new PipelineLogEntry
            {
                Timestamp = DateTime.UtcNow,
                ExecutionId = execution.Id,
                StageId = stageId,
                Level = level,
                Message = message,
                Exception = exception?.ToString()
            };

            var json = JsonSerializer.Serialize(logEntry, _jsonOptions);
            await logWriter.WriteLineAsync(json);
            await logWriter.FlushAsync();

            // Also log to system logger;
            switch (level)
            {
                case LogLevel.Debug:
                    _logger.Debug($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Info:
                    _logger.Info($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Warning:
                    _logger.Warn($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Error:
                    _logger.Error($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Fatal:
                    _logger.Fatal($"[{execution.Id}] {message}", exception);
                    break;
            }
        }

        #endregion;

        #region Private Methods - Provider Management;

        private void InitializeDefaultProviders()
        {
            // Register default providers;
            RegisterProvider(new LocalPipelineProvider(_configuration, _logger));
            RegisterProvider(new ScriptPipelineProvider(_configuration, _logger));

            // Try to register Docker provider if available;
            try
            {
                var dockerProvider = new DockerPipelineProvider(_configuration, _logger);
                RegisterProvider(dockerProvider);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Docker provider not available: {ex.Message}");
            }
        }

        private async Task InitializeProvidersAsync(CancellationToken cancellationToken)
        {
            foreach (var provider in _providers.Values)
            {
                try
                {
                    await provider.InitializeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to initialize provider '{provider.Name}': {ex.Message}", ex);
                }
            }
        }

        private async Task ValidateEnvironmentConnectivityAsync(
            PipelineEnvironment environment,
            EnvironmentValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(environment.ProviderName))
                return;

            if (!_providers.TryGetValue(environment.ProviderName, out var provider))
                return;

            try
            {
                var connectivity = await provider.TestConnectivityAsync(environment);

                if (!connectivity.IsConnected)
                {
                    result.AddError($"Connectivity test failed: {connectivity.ErrorMessage}");
                }
                else;
                {
                    result.Metrics["ConnectivityLatencyMs"] = connectivity.LatencyMs;
                }
            }
            catch (Exception ex)
            {
                result.AddError($"Connectivity test failed with exception: {ex.Message}");
            }
        }

        #endregion;

        #region Private Methods - Timer Callbacks;

        private void HeartbeatCallback(object state)
        {
            if (_disposed || !_isInitialized)
                return;

            try
            {
                // Save state of active executions;
                SaveActiveExecutionsState();

                // Update performance metrics;
                UpdatePerformanceMetrics();

                // Check for stalled executions;
                CheckStalledExecutions();
            }
            catch (Exception ex)
            {
                _logger.Error($"Heartbeat callback failed: {ex.Message}", ex);
            }
        }

        private void CleanupCallback(object state)
        {
            if (_disposed || !_isInitialized)
                return;

            try
            {
                // Clean up old workspace directories;
                CleanupWorkspaceDirectories();

                // Clean up old logs;
                CleanupOldLogs();

                // Compact history files if needed;
                CompactHistoryFiles();
            }
            catch (Exception ex)
            {
                _logger.Error($"Cleanup callback failed: {ex.Message}", ex);
            }
        }

        private void SaveActiveExecutionsState()
        {
            lock (_syncLock)
            {
                foreach (var execution in _activeExecutions.Values)
                {
                    if (execution.Status == PipelineExecutionStatus.Running)
                    {
                        try
                        {
                            var stateFile = GetStateFilePath(execution.Id);
                            var json = JsonSerializer.Serialize(execution, _jsonOptions);
                            File.WriteAllText(stateFile, json);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"Failed to save state for execution {execution.Id}: {ex.Message}");
                        }
                    }
                }
            }
        }

        private void UpdatePerformanceMetrics()
        {
            if (_performanceMonitor == null)
                return;

            try
            {
                _performanceMonitor.UpdateCounter("PipelineManager.ActiveExecutions", ActiveExecutionCount);
                _performanceMonitor.UpdateCounter("PipelineManager.TotalExecutions", TotalExecutionCount);
                _performanceMonitor.UpdateCounter("PipelineManager.AverageExecutionTime", AverageExecutionTimeMs);

                var stats = GetStatistics();
                _performanceMonitor.UpdateCounter("PipelineManager.QueuedExecutions", stats.QueuedExecutions);
                _performanceMonitor.UpdateCounter("PipelineManager.RunningExecutions", stats.RunningExecutions);
                _performanceMonitor.UpdateCounter("PipelineManager.AvailableSlots", stats.AvailableSlots);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to update performance metrics: {ex.Message}");
            }
        }

        private void CheckStalledExecutions()
        {
            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromMinutes(_configuration.ExecutionTimeoutMinutes);

            lock (_syncLock)
            {
                foreach (var execution in _activeExecutions.Values)
                {
                    if (execution.Status == PipelineExecutionStatus.Running &&
                        execution.StartedAt.HasValue &&
                        now - execution.StartedAt.Value > timeout)
                    {
                        _logger.Warn($"Execution {execution.Id} appears to be stalled, timing out...");

                        // Try to cancel the execution;
                        execution.CancellationSource?.Cancel();

                        // Update status;
                        execution.Status = PipelineExecutionStatus.Failed;
                        execution.CompletedAt = now;
                        execution.Errors.Add($"Execution timed out after {timeout.TotalMinutes} minutes");

                        // Remove from active executions;
                        _activeExecutions.Remove(execution.Id);

                        // Save to history;
                        SaveExecutionHistoryAsync(execution).ConfigureAwait(false);

                        // Release semaphore;
                        _concurrencySemaphore.Release();
                    }
                }
            }
        }

        private void CleanupWorkspaceDirectories()
        {
            var workspaceDir = Path.Combine(_configuration.WorkingDirectory, "workspace");
            if (!Directory.Exists(workspaceDir))
                return;

            var cutoffDate = DateTime.UtcNow.AddHours(-24); // Keep for 24 hours;

            foreach (var dir in Directory.GetDirectories(workspaceDir))
            {
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.LastWriteTimeUtc < cutoffDate)
                    {
                        Directory.Delete(dir, true);
                        _logger.Debug($"Cleaned up workspace directory: {dir}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to clean up workspace directory {dir}: {ex.Message}");
                }
            }
        }

        private void CleanupOldLogs()
        {
            var logsDir = Path.Combine(_configuration.WorkingDirectory, "logs");
            if (!Directory.Exists(logsDir))
                return;

            var cutoffDate = DateTime.UtcNow.AddDays(-7); // Keep for 7 days;

            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffDate)
                    {
                        File.Delete(file);
                        _logger.Debug($"Cleaned up log file: {file}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to clean up log file {file}: {ex.Message}");
                }
            }
        }

        private void CompactHistoryFiles()
        {
            // Implementation for compacting history files;
            // Could merge old history files into archives;
        }

        #endregion;

        #region Private Methods - Event Handling;

        private void OnPipelineManagerInitialized()
        {
            _eventBus?.Publish(new PipelineManagerInitializedEvent;
            {
                Timestamp = DateTime.UtcNow,
                InstanceId = GetHashCode().ToString("X"),
                Configuration = _configuration;
            });
        }

        private void OnPipelineExecutionStarted(PipelineExecutionStartedEventArgs e)
        {
            PipelineExecutionStarted?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineExecutionCompleted(PipelineExecutionCompletedEventArgs e)
        {
            PipelineExecutionCompleted?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineExecutionFailed(PipelineExecutionFailedEventArgs e)
        {
            PipelineExecutionFailed?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineStageStarted(PipelineStageStartedEventArgs e)
        {
            PipelineStageStarted?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineStageCompleted(PipelineStageCompletedEventArgs e)
        {
            PipelineStageCompleted?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineArtifactCreated(PipelineArtifactCreatedEventArgs e)
        {
            PipelineArtifactCreated?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineStatusChanged(PipelineStatusChangedEventArgs e)
        {
            PipelineStatusChanged?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineQueued(PipelineQueuedEventArgs e)
        {
            PipelineQueued?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        private void OnPipelineCancelled(PipelineCancelledEventArgs e)
        {
            PipelineCancelled?.Invoke(this, e);
            _eventBus?.Publish(e);
        }

        #endregion;

        #region Validation Methods;

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PipelineManager));
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("PipelineManager is not initialized");
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cancel all operations;
                    _globalCancellation.Cancel();

                    // Dispose timers;
                    _heartbeatTimer?.Dispose();
                    _cleanupTimer?.Dispose();

                    // Wait for active executions to complete;
                    Task.WhenAll(_activeExecutions.Values;
                        .Select(e => e.CompletionTask ?? Task.CompletedTask))
                        .Wait(TimeSpan.FromSeconds(30));

                    // Dispose providers;
                    foreach (var provider in _providers.Values)
                    {
                        provider.Dispose();
                    }

                    // Dispose semaphore;
                    _concurrencySemaphore?.Dispose();

                    // Dispose cancellation token source;
                    _globalCancellation.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PipelineManager()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Pipeline execution context.
        /// </summary>
        private class PipelineExecutionContext;
        {
            public PipelineExecution Execution { get; set; }
            public PipelineDefinition PipelineDefinition { get; set; }
            public PipelineEnvironment Environment { get; set; }
            public IPipelineProvider Provider { get; set; }
            public string WorkingDirectory { get; set; }
            public StreamWriter LogWriter { get; set; }
            public CancellationToken CancellationToken { get; set; }
        }

        #endregion;
    }
}
