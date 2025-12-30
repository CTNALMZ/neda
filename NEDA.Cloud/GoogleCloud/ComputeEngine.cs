using Google.Apis.Auth.OAuth2;
using Google.Apis.Compute.v1;
using Google.Apis.Compute.v1.Data;
using Google.Apis.Services;
using Google.Cloud.Compute.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.Middleware;
using NEDA.Automation.ScenarioPlanner;
using NEDA.Cloud.AWS;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Services.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.Cloud.GoogleCloud.ComputeEngine;
using static System.Net.Mime.MediaTypeNames;

namespace NEDA.Cloud.GoogleCloud;
{
    /// <summary>
    /// Google Cloud Compute Engine Client - Manages GCP VM instances and compute resources;
    /// </summary>
    public class ComputeEngine : IComputeService, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger<ComputeEngine> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly IEventBus _eventBus;
        private readonly GoogleCloudOptions _options;

        private ComputeService _computeService;
        private InstancesClient _instancesClient;
        private InstancesClient _instancesClientV1;
        private ProjectsClient _projectsClient;
        private DisksClient _disksClient;
        private ImagesClient _imagesClient;
        private MachineTypesClient _machineTypesClient;

        private GoogleCredential _credential;
        private bool _isInitialized;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, object> _resourceCache;
        private readonly TimeSpan _operationTimeout = TimeSpan.FromMinutes(10);
        private readonly CancellationTokenSource _globalCts = new CancellationTokenSource();

        #endregion;

        #region Properties;

        /// <summary>
        /// Google Cloud Project ID;
        /// </summary>
        public string ProjectId { get; private set; }

        /// <summary>
        /// Default region for compute resources;
        /// </summary>
        public string DefaultRegion { get; set; } = "us-central1";

        /// <summary>
        /// Default zone for compute resources;
        /// </summary>
        public string DefaultZone { get; set; } = "us-central1-a";

        /// <summary>
        /// Indicates if the client is initialized and authenticated;
        /// </summary>
        public bool IsInitialized => _isInitialized && _computeService != null;

        /// <summary>
        /// Service account email being used;
        /// </summary>
        public string ServiceAccountEmail { get; private set; }

        /// <summary>
        /// Compute service quota information;
        /// </summary>
        public QuotaInfo CurrentQuota { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Raised when initialization state changes;
        /// </summary>
        public event EventHandler<InitializationStateChangedEventArgs> InitializationStateChanged;

        /// <summary>
        /// Raised when a compute operation starts;
        /// </summary>
        public event EventHandler<ComputeOperationStartedEventArgs> OperationStarted;

        /// <summary>
        /// Raised when a compute operation completes;
        /// </summary>
        public event EventHandler<ComputeOperationCompletedEventArgs> OperationCompleted;

        /// <summary>
        /// Raised when operation progress updates;
        /// </summary>
        public event EventHandler<ComputeProgressEventArgs> OperationProgress;

        /// <summary>
        /// Raised when instance state changes;
        /// </summary>
        public event EventHandler<InstanceStateChangedEventArgs> InstanceStateChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of ComputeEngine;
        /// </summary>
        public ComputeEngine(
            ILogger<ComputeEngine> logger,
            IErrorReporter errorReporter,
            IEventBus eventBus,
            IOptions<GoogleCloudOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _httpClient = new HttpClient;
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

            _resourceCache = new Dictionary<string, object>();
            CurrentQuota = new QuotaInfo();

            _logger.LogInformation("ComputeEngine initialized");
        }

        #endregion;

        #region Initialization Methods;

        /// <summary>
        /// Initialize with service account credentials from JSON file;
        /// </summary>
        public async Task<bool> InitializeWithServiceAccountAsync(string credentialsPath, string projectId)
        {
            ValidateCredentialsPath(credentialsPath);
            ValidateProjectId(projectId);

            await _initLock.WaitAsync();

            try
            {
                _logger.LogInformation($"Initializing ComputeEngine with service account from: {credentialsPath}");

                _credential = GoogleCredential.FromFile(credentialsPath)
                    .CreateScoped(ComputeService.Scope.Compute);

                ProjectId = projectId;

                return await InternalInitializeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Service account initialization failed");
                await _errorReporter.ReportErrorAsync(
                    "GCP_SA_INIT_FAILED",
                    $"Service account initialization failed: {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    "Service account initialization failed",
                    ErrorCodes.GoogleCloud.ServiceAccountInitFailed,
                    ex);
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Initialize with service account JSON content;
        /// </summary>
        public async Task<bool> InitializeWithServiceAccountJsonAsync(string jsonContent, string projectId)
        {
            ValidateJsonContent(jsonContent);
            ValidateProjectId(projectId);

            await _initLock.WaitAsync();

            try
            {
                _logger.LogInformation("Initializing ComputeEngine with service account JSON");

                _credential = GoogleCredential.FromJson(jsonContent)
                    .CreateScoped(ComputeService.Scope.Compute);

                ProjectId = projectId;

                return await InternalInitializeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Service account JSON initialization failed");
                await _errorReporter.ReportErrorAsync(
                    "GCP_JSON_INIT_FAILED",
                    $"Service account JSON initialization failed: {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    "Service account JSON initialization failed",
                    ErrorCodes.GoogleCloud.JsonInitFailed,
                    ex);
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Initialize with application default credentials;
        /// </summary>
        public async Task<bool> InitializeWithDefaultCredentialsAsync(string projectId)
        {
            ValidateProjectId(projectId);

            await _initLock.WaitAsync();

            try
            {
                _logger.LogInformation("Initializing ComputeEngine with default credentials");

                _credential = GoogleCredential.GetApplicationDefault()
                    .CreateScoped(ComputeService.Scope.Compute);

                ProjectId = projectId;

                return await InternalInitializeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Default credentials initialization failed");
                await _errorReporter.ReportErrorAsync(
                    "GCP_DEFAULT_INIT_FAILED",
                    $"Default credentials initialization failed: {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    "Default credentials initialization failed",
                    ErrorCodes.GoogleCloud.DefaultInitFailed,
                    ex);
            }
            finally
            {
                _initLock.Release();
            }
        }

        private async Task<bool> InternalInitializeAsync()
        {
            try
            {
                // Initialize v1 (legacy) client;
                _computeService = new ComputeService(new BaseClientService.Initializer;
                {
                    HttpClientInitializer = _credential,
                    ApplicationName = "NEDA-ComputeEngine",
                    GZipEnabled = true;
                });

                // Initialize v1 (new) clients;
                var clientBuilder = new InstancesClientBuilder;
                {
                    Credential = _credential;
                };

                _instancesClientV1 = await clientBuilder.BuildAsync();
                _projectsClient = await new ProjectsClientBuilder { Credential = _credential }.BuildAsync();
                _disksClient = await new DisksClientBuilder { Credential = _credential }.BuildAsync();
                _imagesClient = await new ImagesClientBuilder { Credential = _credential }.BuildAsync();
                _machineTypesClient = await new MachineTypesClientBuilder { Credential = _credential }.BuildAsync();

                // Get service account email;
                ServiceAccountEmail = await GetServiceAccountEmailAsync();

                // Load quota information;
                await LoadQuotaInformationAsync();

                // Test connectivity;
                await TestConnectivityAsync();

                _isInitialized = true;

                _logger.LogInformation($"ComputeEngine initialized successfully. Project: {ProjectId}, SA: {ServiceAccountEmail}");

                OnInitializationStateChanged(new InitializationStateChangedEventArgs;
                {
                    IsInitialized = true,
                    ProjectId = ProjectId,
                    ServiceAccountEmail = ServiceAccountEmail,
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new ComputeEngineInitializedEvent;
                {
                    ProjectId = ProjectId,
                    ServiceAccountEmail = ServiceAccountEmail,
                    Region = DefaultRegion,
                    Zone = DefaultZone,
                    Timestamp = DateTime.UtcNow;
                });

                return true;
            }
            catch (Exception ex)
            {
                _isInitialized = false;
                _computeService = null;

                _logger.LogError(ex, "Internal initialization failed");
                throw;
            }
        }

        private async Task<string> GetServiceAccountEmailAsync()
        {
            try
            {
                if (_credential.UnderlyingCredential is ServiceAccountCredential saCredential)
                {
                    return saCredential.Id;
                }

                // Try to get from metadata server;
                var response = await _httpClient.GetAsync(
                    "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/email",
                    _globalCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }

                return "unknown-service-account";
            }
            catch
            {
                return "unknown-service-account";
            }
        }

        private async Task LoadQuotaInformationAsync()
        {
            try
            {
                var request = _computeService.Regions.Get(ProjectId, DefaultRegion);
                var region = await request.ExecuteAsync(_globalCts.Token);

                CurrentQuota = new QuotaInfo;
                {
                    Region = DefaultRegion,
                    Quotas = region.Quotas?.ToDictionary(
                        q => q.Metric,
                        q => new QuotaLimit;
                        {
                            Limit = q.Limit ?? 0,
                            Usage = q.Usage ?? 0,
                            Available = (q.Limit ?? 0) - (q.Usage ?? 0)
                        }) ?? new Dictionary<string, QuotaLimit>()
                };

                _logger.LogDebug($"Quota loaded for region {DefaultRegion}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load quota information");
            }
        }

        private async Task TestConnectivityAsync()
        {
            try
            {
                // Try to list zones to test connectivity;
                var zones = await _computeService.Zones.List(ProjectId).ExecuteAsync(_globalCts.Token);

                if (zones.Items == null || !zones.Items.Any())
                {
                    throw new ComputeEngineException(
                        "No zones found in project",
                        ErrorCodes.GoogleCloud.NoZonesFound);
                }

                _logger.LogDebug($"Connectivity test successful. Found {zones.Items.Count} zones");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connectivity test failed");
                throw new ComputeEngineException(
                    "Connectivity test failed",
                    ErrorCodes.GoogleCloud.ConnectivityTestFailed,
                    ex);
            }
        }

        #endregion;

        #region Instance Operations;

        /// <summary>
        /// Create a new VM instance;
        /// </summary>
        public async Task<Instance> CreateInstanceAsync(
            string instanceName,
            string machineType = "n1-standard-1",
            string imageFamily = "debian-11",
            string imageProject = "debian-cloud",
            int diskSizeGb = 10,
            string zone = null,
            IDictionary<string, string> metadata = null,
            IList<string> tags = null,
            bool preemptible = false,
            bool enableExternalIp = true)
        {
            ValidateInstanceName(instanceName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;

            try
            {
                _logger.LogInformation($"Creating instance: {instanceName} in zone: {targetZone}");

                OnOperationStarted(new ComputeOperationStartedEventArgs;
                {
                    Operation = "CreateInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Timestamp = DateTime.UtcNow;
                });

                OnOperationProgress(new ComputeProgressEventArgs;
                {
                    Operation = "CreateInstance",
                    Progress = 10,
                    Message = $"Starting instance creation for '{instanceName}'",
                    Timestamp = DateTime.UtcNow;
                });

                // Get image;
                var image = await GetImageAsync(imageFamily, imageProject);

                OnOperationProgress(new ComputeProgressEventArgs;
                {
                    Operation = "CreateInstance",
                    Progress = 30,
                    Message = $"Image resolved for '{instanceName}'",
                    Timestamp = DateTime.UtcNow;
                });

                // Create instance;
                var instance = new Instance;
                {
                    Name = instanceName,
                    MachineType = $"zones/{targetZone}/machineTypes/{machineType}",
                    Disks = new List<AttachedDisk>
                    {
                        new AttachedDisk;
                        {
                            AutoDelete = true,
                            Boot = true,
                            InitializeParams = new AttachedDiskInitializeParams;
                            {
                                DiskSizeGb = diskSizeGb,
                                SourceImage = image.SelfLink,
                                DiskType = $"zones/{targetZone}/diskTypes/pd-ssd"
                            }
                        }
                    },
                    NetworkInterfaces = new List<NetworkInterface>
                    {
                        new NetworkInterface;
                        {
                            Network = "global/networks/default",
                            AccessConfigs = enableExternalIp ? new List<AccessConfig>
                            {
                                new AccessConfig;
                                {
                                    Name = "External NAT",
                                    Type = "ONE_TO_ONE_NAT"
                                }
                            } : new List<AccessConfig>()
                        }
                    },
                    Metadata = metadata != null ? new Metadata;
                    {
                        Items = metadata.Select(kv => new Metadata.ItemsData;
                        {
                            Key = kv.Key,
                            Value = kv.Value;
                        }).ToList()
                    } : null,
                    Tags = tags != null ? new Tags { Items = tags } : null,
                    Scheduling = new Scheduling;
                    {
                        Preemptible = preemptible,
                        AutomaticRestart = !preemptible,
                        OnHostMaintenance = preemptible ? "TERMINATE" : "MIGRATE"
                    },
                    Labels = new Dictionary<string, string>
                    {
                        ["created-by"] = "neda",
                        ["created-at"] = DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
                        ["project"] = ProjectId;
                    }
                };

                OnOperationProgress(new ComputeProgressEventArgs;
                {
                    Operation = "CreateInstance",
                    Progress = 50,
                    Message = $"Instance configuration prepared for '{instanceName}'",
                    Timestamp = DateTime.UtcNow;
                });

                // Execute creation;
                var insertRequest = _computeService.Instances.Insert(instance, ProjectId, targetZone);
                var operation = await insertRequest.ExecuteAsync(_globalCts.Token);

                OnOperationProgress(new ComputeProgressEventArgs;
                {
                    Operation = "CreateInstance",
                    Progress = 70,
                    Message = $"Instance creation operation started for '{instanceName}'",
                    Timestamp = DateTime.UtcNow;
                });

                // Wait for operation to complete;
                await WaitForZoneOperationAsync(operation.Name, targetZone);

                OnOperationProgress(new ComputeProgressEventArgs;
                {
                    Operation = "CreateInstance",
                    Progress = 90,
                    Message = $"Instance creation completed for '{instanceName}'",
                    Timestamp = DateTime.UtcNow;
                });

                // Get the created instance;
                var getRequest = _computeService.Instances.Get(ProjectId, targetZone, instanceName);
                var createdInstance = await getRequest.ExecuteAsync(_globalCts.Token);

                _logger.LogInformation($"Instance created: {instanceName} with IP: {createdInstance.NetworkInterfaces?.FirstOrDefault()?.AccessConfigs?.FirstOrDefault()?.NatIP}");

                OnOperationCompleted(new ComputeOperationCompletedEventArgs;
                {
                    Operation = "CreateInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Status = "SUCCESS",
                    Duration = DateTime.UtcNow - operation.InsertTime ?? TimeSpan.Zero,
                    Timestamp = DateTime.UtcNow;
                });

                OnInstanceStateChanged(new InstanceStateChangedEventArgs;
                {
                    InstanceName = instanceName,
                    Zone = targetZone,
                    OldState = "NONE",
                    NewState = "RUNNING",
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new InstanceCreatedEvent;
                {
                    InstanceName = instanceName,
                    ProjectId = ProjectId,
                    Zone = targetZone,
                    MachineType = machineType,
                    ImageFamily = imageFamily,
                    ExternalIp = createdInstance.NetworkInterfaces?.FirstOrDefault()?.AccessConfigs?.FirstOrDefault()?.NatIP,
                    Timestamp = DateTime.UtcNow;
                });

                // Cache the instance;
                var cacheKey = $"Instance_{instanceName}_{targetZone}";
                _resourceCache[cacheKey] = createdInstance;

                return createdInstance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create instance: {instanceName}");
                await _errorReporter.ReportErrorAsync(
                    "GCP_INSTANCE_CREATE_FAILED",
                    $"Failed to create instance '{instanceName}': {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    $"Failed to create instance '{instanceName}'",
                    ErrorCodes.GoogleCloud.InstanceCreateFailed,
                    ex);
            }
        }

        /// <summary>
        /// Start a stopped instance;
        /// </summary>
        public async Task StartInstanceAsync(string instanceName, string zone = null)
        {
            ValidateInstanceName(instanceName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;

            try
            {
                _logger.LogInformation($"Starting instance: {instanceName} in zone: {targetZone}");

                OnOperationStarted(new ComputeOperationStartedEventArgs;
                {
                    Operation = "StartInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Timestamp = DateTime.UtcNow;
                });

                var request = _computeService.Instances.Start(ProjectId, targetZone, instanceName);
                var operation = await request.ExecuteAsync(_globalCts.Token);

                await WaitForZoneOperationAsync(operation.Name, targetZone);

                _logger.LogInformation($"Instance started: {instanceName}");

                OnOperationCompleted(new ComputeOperationCompletedEventArgs;
                {
                    Operation = "StartInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Status = "SUCCESS",
                    Timestamp = DateTime.UtcNow;
                });

                OnInstanceStateChanged(new InstanceStateChangedEventArgs;
                {
                    InstanceName = instanceName,
                    Zone = targetZone,
                    OldState = "STOPPED",
                    NewState = "RUNNING",
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new InstanceStateChangedEvent;
                {
                    InstanceName = instanceName,
                    ProjectId = ProjectId,
                    Zone = targetZone,
                    OldState = "STOPPED",
                    NewState = "RUNNING",
                    Operation = "START",
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start instance: {instanceName}");
                await _errorReporter.ReportErrorAsync(
                    "GCP_INSTANCE_START_FAILED",
                    $"Failed to start instance '{instanceName}': {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    $"Failed to start instance '{instanceName}'",
                    ErrorCodes.GoogleCloud.InstanceStartFailed,
                    ex);
            }
        }

        /// <summary>
        /// Stop a running instance;
        /// </summary>
        public async Task StopInstanceAsync(string instanceName, string zone = null)
        {
            ValidateInstanceName(instanceName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;

            try
            {
                _logger.LogInformation($"Stopping instance: {instanceName} in zone: {targetZone}");

                OnOperationStarted(new ComputeOperationStartedEventArgs;
                {
                    Operation = "StopInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Timestamp = DateTime.UtcNow;
                });

                var request = _computeService.Instances.Stop(ProjectId, targetZone, instanceName);
                var operation = await request.ExecuteAsync(_globalCts.Token);

                await WaitForZoneOperationAsync(operation.Name, targetZone);

                _logger.LogInformation($"Instance stopped: {instanceName}");

                OnOperationCompleted(new ComputeOperationCompletedEventArgs;
                {
                    Operation = "StopInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Status = "SUCCESS",
                    Timestamp = DateTime.UtcNow;
                });

                OnInstanceStateChanged(new InstanceStateChangedEventArgs;
                {
                    InstanceName = instanceName,
                    Zone = targetZone,
                    OldState = "RUNNING",
                    NewState = "STOPPED",
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new InstanceStateChangedEvent;
                {
                    InstanceName = instanceName,
                    ProjectId = ProjectId,
                    Zone = targetZone,
                    OldState = "RUNNING",
                    NewState = "STOPPED",
                    Operation = "STOP",
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to stop instance: {instanceName}");
                await _errorReporter.ReportErrorAsync(
                    "GCP_INSTANCE_STOP_FAILED",
                    $"Failed to stop instance '{instanceName}': {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    $"Failed to stop instance '{instanceName}'",
                    ErrorCodes.GoogleCloud.InstanceStopFailed,
                    ex);
            }
        }

        /// <summary>
        /// Delete an instance;
        /// </summary>
        public async Task DeleteInstanceAsync(string instanceName, string zone = null)
        {
            ValidateInstanceName(instanceName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;

            try
            {
                _logger.LogInformation($"Deleting instance: {instanceName} in zone: {targetZone}");

                OnOperationStarted(new ComputeOperationStartedEventArgs;
                {
                    Operation = "DeleteInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Timestamp = DateTime.UtcNow;
                });

                var request = _computeService.Instances.Delete(ProjectId, targetZone, instanceName);
                var operation = await request.ExecuteAsync(_globalCts.Token);

                await WaitForZoneOperationAsync(operation.Name, targetZone);

                _logger.LogInformation($"Instance deleted: {instanceName}");

                // Remove from cache;
                var cacheKey = $"Instance_{instanceName}_{targetZone}";
                _resourceCache.Remove(cacheKey);

                OnOperationCompleted(new ComputeOperationCompletedEventArgs;
                {
                    Operation = "DeleteInstance",
                    InstanceName = instanceName,
                    Zone = targetZone,
                    Status = "SUCCESS",
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new InstanceDeletedEvent;
                {
                    InstanceName = instanceName,
                    ProjectId = ProjectId,
                    Zone = targetZone,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete instance: {instanceName}");
                await _errorReporter.ReportErrorAsync(
                    "GCP_INSTANCE_DELETE_FAILED",
                    $"Failed to delete instance '{instanceName}': {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    $"Failed to delete instance '{instanceName}'",
                    ErrorCodes.GoogleCloud.InstanceDeleteFailed,
                    ex);
            }
        }

        /// <summary>
        /// Get instance details;
        /// </summary>
        public async Task<Instance> GetInstanceAsync(string instanceName, string zone = null)
        {
            ValidateInstanceName(instanceName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;

            try
            {
                var cacheKey = $"Instance_{instanceName}_{targetZone}";
                if (_resourceCache.TryGetValue(cacheKey, out var cached) && cached is Instance cachedInstance)
                {
                    return cachedInstance;
                }

                var request = _computeService.Instances.Get(ProjectId, targetZone, instanceName);
                var instance = await request.ExecuteAsync(_globalCts.Token);

                if (instance != null)
                {
                    _resourceCache[cacheKey] = instance;
                }

                return instance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get instance: {instanceName}");
                throw new ComputeEngineException(
                    $"Failed to get instance '{instanceName}'",
                    ErrorCodes.GoogleCloud.InstanceGetFailed,
                    ex);
            }
        }

        /// <summary>
        /// List all instances in project;
        /// </summary>
        public async Task<IList<Instance>> ListInstancesAsync(string filter = null, int? maxResults = null)
        {
            EnsureInitialized();

            try
            {
                var instances = new List<Instance>();

                // List instances across all zones;
                var zonesRequest = _computeService.Zones.List(ProjectId);
                var zones = await zonesRequest.ExecuteAsync(_globalCts.Token);

                foreach (var zone in zones.Items)
                {
                    var request = _computeService.Instances.List(ProjectId, zone.Name);

                    if (!string.IsNullOrEmpty(filter))
                    {
                        request.Filter = filter;
                    }

                    if (maxResults.HasValue)
                    {
                        request.MaxResults = maxResults.Value;
                    }

                    var response = await request.ExecuteAsync(_globalCts.Token);

                    if (response.Items != null)
                    {
                        instances.AddRange(response.Items);

                        // Cache each instance;
                        foreach (var instance in response.Items)
                        {
                            var cacheKey = $"Instance_{instance.Name}_{zone.Name}";
                            _resourceCache[cacheKey] = instance;
                        }
                    }
                }

                _logger.LogDebug($"Listed {instances.Count} instances");
                return instances;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list instances");
                throw new ComputeEngineException(
                    "Failed to list instances",
                    ErrorCodes.GoogleCloud.InstanceListFailed,
                    ex);
            }
        }

        /// <summary>
        /// Resize an instance (change machine type)
        /// </summary>
        public async Task ResizeInstanceAsync(
            string instanceName,
            string newMachineType,
            string zone = null)
        {
            ValidateInstanceName(instanceName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;

            try
            {
                _logger.LogInformation($"Resizing instance: {instanceName} to {newMachineType}");

                // Stop instance first if running;
                var instance = await GetInstanceAsync(instanceName, targetZone);
                var wasRunning = instance.Status == "RUNNING";

                if (wasRunning)
                {
                    await StopInstanceAsync(instanceName, targetZone);
                }

                // Resize instance;
                var request = _computeService.Instances.Stop(ProjectId, targetZone, instanceName);
                var operation = await request.ExecuteAsync(_globalCts.Token);
                await WaitForZoneOperationAsync(operation.Name, targetZone);

                var setMachineTypeRequest = _computeService.Instances.SetMachineType(
                    new InstancesSetMachineTypeRequest;
                    {
                        MachineType = $"zones/{targetZone}/machineTypes/{newMachineType}"
                    },
                    ProjectId,
                    targetZone,
                    instanceName);

                var resizeOperation = await setMachineTypeRequest.ExecuteAsync(_globalCts.Token);
                await WaitForZoneOperationAsync(resizeOperation.Name, targetZone);

                // Start instance if it was running;
                if (wasRunning)
                {
                    await StartInstanceAsync(instanceName, targetZone);
                }

                _logger.LogInformation($"Instance resized: {instanceName} to {newMachineType}");

                await _eventBus.PublishAsync(new InstanceResizedEvent;
                {
                    InstanceName = instanceName,
                    ProjectId = ProjectId,
                    Zone = targetZone,
                    OldMachineType = instance.MachineType,
                    NewMachineType = newMachineType,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to resize instance: {instanceName}");
                await _errorReporter.ReportErrorAsync(
                    "GCP_INSTANCE_RESIZE_FAILED",
                    $"Failed to resize instance '{instanceName}': {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    $"Failed to resize instance '{instanceName}'",
                    ErrorCodes.GoogleCloud.InstanceResizeFailed,
                    ex);
            }
        }

        #endregion;

        #region Disk Operations;

        /// <summary>
        /// Create a persistent disk;
        /// </summary>
        public async Task<Disk> CreateDiskAsync(
            string diskName,
            int sizeGb,
            string diskType = "pd-ssd",
            string zone = null,
            string snapshot = null,
            string image = null)
        {
            ValidateDiskName(diskName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;

            try
            {
                _logger.LogInformation($"Creating disk: {diskName} ({sizeGb}GB) in zone: {targetZone}");

                var disk = new Disk;
                {
                    Name = diskName,
                    SizeGb = sizeGb,
                    Type = $"zones/{targetZone}/diskTypes/{diskType}",
                    Labels = new Dictionary<string, string>
                    {
                        ["created-by"] = "neda",
                        ["created-at"] = DateTime.UtcNow.ToString("yyyyMMddHHmmss")
                    }
                };

                if (!string.IsNullOrEmpty(snapshot))
                {
                    disk.SourceSnapshot = snapshot;
                }
                else if (!string.IsNullOrEmpty(image))
                {
                    disk.SourceImage = image;
                }

                var request = _computeService.Disks.Insert(disk, ProjectId, targetZone);
                var operation = await request.ExecuteAsync(_globalCts.Token);

                await WaitForZoneOperationAsync(operation.Name, targetZone);

                _logger.LogInformation($"Disk created: {diskName}");

                await _eventBus.PublishAsync(new DiskCreatedEvent;
                {
                    DiskName = diskName,
                    ProjectId = ProjectId,
                    Zone = targetZone,
                    SizeGb = sizeGb,
                    DiskType = diskType,
                    Timestamp = DateTime.UtcNow;
                });

                return await GetDiskAsync(diskName, targetZone);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create disk: {diskName}");
                await _errorReporter.ReportErrorAsync(
                    "GCP_DISK_CREATE_FAILED",
                    $"Failed to create disk '{diskName}': {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    $"Failed to create disk '{diskName}'",
                    ErrorCodes.GoogleCloud.DiskCreateFailed,
                    ex);
            }
        }

        /// <summary>
        /// Attach a disk to an instance;
        /// </summary>
        public async Task AttachDiskAsync(
            string instanceName,
            string diskName,
            string zone = null,
            string deviceName = null,
            DiskMode mode = DiskMode.READ_WRITE)
        {
            ValidateInstanceName(instanceName);
            ValidateDiskName(diskName);
            EnsureInitialized();

            var targetZone = zone ?? DefaultZone;
            var targetDeviceName = deviceName ?? diskName;

            try
            {
                _logger.LogInformation($"Attaching disk: {diskName} to instance: {instanceName}");

                var attachedDisk = new AttachedDisk;
                {
                    Source = $"projects/{ProjectId}/zones/{targetZone}/disks/{diskName}",
                    DeviceName = targetDeviceName,
                    Mode = mode == DiskMode.READ_ONLY ? "READ_ONLY" : "READ_WRITE"
                };

                var request = _computeService.Instances.AttachDisk(
                    attachedDisk,
                    ProjectId,
                    targetZone,
                    instanceName);

                var operation = await request.ExecuteAsync(_globalCts.Token);
                await WaitForZoneOperationAsync(operation.Name, targetZone);

                _logger.LogInformation($"Disk attached: {diskName} to {instanceName}");

                await _eventBus.PublishAsync(new DiskAttachedEvent;
                {
                    InstanceName = instanceName,
                    DiskName = diskName,
                    ProjectId = ProjectId,
                    Zone = targetZone,
                    DeviceName = targetDeviceName,
                    Mode = mode.ToString(),
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to attach disk: {diskName} to instance: {instanceName}");
                await _errorReporter.ReportErrorAsync(
                    "GCP_DISK_ATTACH_FAILED",
                    $"Failed to attach disk '{diskName}' to instance '{instanceName}': {ex.Message}",
                    ex);

                throw new ComputeEngineException(
                    $"Failed to attach disk '{diskName}' to instance '{instanceName}'",
                    ErrorCodes.GoogleCloud.DiskAttachFailed,
                    ex);
            }
        }

        #endregion;

        #region Helper Methods;

        private async Task<Image> GetImageAsync(string imageFamily, string imageProject)
        {
            try
            {
                var cacheKey = $"Image_{imageProject}_{imageFamily}";
                if (_resourceCache.TryGetValue(cacheKey, out var cached) && cached is Image cachedImage)
                {
                    return cachedImage;
                }

                var request = _computeService.Images.GetFromFamily(imageProject, imageFamily);
                var image = await request.ExecuteAsync(_globalCts.Token);

                if (image != null)
                {
                    _resourceCache[cacheKey] = image;
                }

                return image;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get image: {imageFamily} from project: {imageProject}");
                throw new ComputeEngineException(
                    $"Failed to get image '{imageFamily}'",
                    ErrorCodes.GoogleCloud.ImageGetFailed,
                    ex);
            }
        }

        private async Task<Disk> GetDiskAsync(string diskName, string zone)
        {
            try
            {
                var request = _computeService.Disks.Get(ProjectId, zone, diskName);
                return await request.ExecuteAsync(_globalCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get disk: {diskName}");
                throw new ComputeEngineException(
                    $"Failed to get disk '{diskName}'",
                    ErrorCodes.GoogleCloud.DiskGetFailed,
                    ex);
            }
        }

        private async Task WaitForZoneOperationAsync(string operationName, string zone)
        {
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < _operationTimeout)
            {
                var request = _computeService.ZoneOperations.Get(ProjectId, zone, operationName);
                var operation = await request.ExecuteAsync(_globalCts.Token);

                if (operation.Status == "DONE")
                {
                    if (operation.Error != null)
                    {
                        var error = operation.Error.Errors?.FirstOrDefault();
                        throw new ComputeEngineException(
                            $"Operation failed: {error?.Message ?? "Unknown error"}",
                            ErrorCodes.GoogleCloud.OperationFailed,
                            null);
                    }

                    return;
                }

                await Task.Delay(2000); // Wait 2 seconds before checking again;
            }

            throw new ComputeEngineException(
                "Operation timeout",
                ErrorCodes.GoogleCloud.OperationTimeout);
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized)
            {
                throw new ComputeEngineException(
                    "ComputeEngine is not initialized",
                    ErrorCodes.GoogleCloud.NotInitialized);
            }
        }

        #endregion;

        #region Validation Methods;

        private void ValidateCredentialsPath(string credentialsPath)
        {
            if (string.IsNullOrWhiteSpace(credentialsPath))
                throw new ArgumentException("Credentials path cannot be null or empty", nameof(credentialsPath));

            if (!File.Exists(credentialsPath))
                throw new FileNotFoundException("Credentials file not found", credentialsPath);
        }

        private void ValidateProjectId(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be null or empty", nameof(projectId));

            if (projectId.Length > 30)
                throw new ArgumentException("Project ID cannot exceed 30 characters", nameof(projectId));
        }

        private void ValidateJsonContent(string jsonContent)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
                throw new ArgumentException("JSON content cannot be null or empty", nameof(jsonContent));

            try
            {
                Newtonsoft.Json.Linq.JObject.Parse(jsonContent);
            }
            catch
            {
                throw new ArgumentException("Invalid JSON content", nameof(jsonContent));
            }
        }

        private void ValidateInstanceName(string instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
                throw new ArgumentException("Instance name cannot be null or empty", nameof(instanceName));

            if (instanceName.Length > 63)
                throw new ArgumentException("Instance name cannot exceed 63 characters", nameof(instanceName));

            if (!instanceName.All(c => char.IsLower(c) || char.IsDigit(c) || c == '-'))
                throw new ArgumentException("Instance name must contain only lowercase letters, digits, and hyphens", nameof(instanceName));

            if (instanceName.StartsWith("-") || instanceName.EndsWith("-"))
                throw new ArgumentException("Instance name cannot start or end with a hyphen", nameof(instanceName));
        }

        private void ValidateDiskName(string diskName)
        {
            if (string.IsNullOrWhiteSpace(diskName))
                throw new ArgumentException("Disk name cannot be null or empty", nameof(diskName));

            if (diskName.Length > 63)
                throw new ArgumentException("Disk name cannot exceed 63 characters", nameof(diskName));
        }

        #endregion;

        #region Event Methods;

        protected virtual void OnInitializationStateChanged(InitializationStateChangedEventArgs e)
        {
            InitializationStateChanged?.Invoke(this, e);
        }

        protected virtual void OnOperationStarted(ComputeOperationStartedEventArgs e)
        {
            OperationStarted?.Invoke(this, e);
        }

        protected virtual void OnOperationCompleted(ComputeOperationCompletedEventArgs e)
        {
            OperationCompleted?.Invoke(this, e);
        }

        protected virtual void OnOperationProgress(ComputeProgressEventArgs e)
        {
            OperationProgress?.Invoke(this, e);
        }

        protected virtual void OnInstanceStateChanged(InstanceStateChangedEventArgs e)
        {
            InstanceStateChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

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
                    _globalCts?.Cancel();
                    _globalCts?.Dispose();
                    _httpClient?.Dispose();
                    _initLock?.Dispose();
                    _computeService?.Dispose();

                    _instancesClientV1?.Dispose();
                    _projectsClient?.Dispose();
                    _disksClient?.Dispose();
                    _imagesClient?.Dispose();
                    _machineTypesClient?.Dispose();
                }

                _disposed = true;
            }
        }

        ~ComputeEngine()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Disk attachment modes;
        /// </summary>
        public enum DiskMode;
        {
            READ_WRITE,
            READ_ONLY;
        }

        /// <summary>
        /// Quota information;
        /// </summary>
        public class QuotaInfo;
        {
            public string Region { get; set; }
            public Dictionary<string, QuotaLimit> Quotas { get; set; } = new Dictionary<string, QuotaLimit>();

            public QuotaLimit GetQuota(string metric)
            {
                return Quotas.TryGetValue(metric, out var quota) ? quota : new QuotaLimit();
            }
        }

        /// <summary>
        /// Quota limit details;
        /// </summary>
        public class QuotaLimit;
        {
            public double Limit { get; set; }
            public double Usage { get; set; }
            public double Available { get; set; }

            public double UsagePercentage => Limit > 0 ? (Usage / Limit) * 100 : 0;
        }

        /// <summary>
        /// Google Cloud options;
        /// </summary>
        public class GoogleCloudOptions;
        {
            public string DefaultProjectId { get; set; }
            public string DefaultRegion { get; set; } = "us-central1";
            public string DefaultZone { get; set; } = "us-central1-a";
            public string CredentialsPath { get; set; }
            public int OperationTimeoutMinutes { get; set; } = 10;
            public bool EnableDetailedLogging { get; set; }
        }

        /// <summary>
        /// Event args for initialization state changes;
        /// </summary>
        public class InitializationStateChangedEventArgs : EventArgs;
        {
            public bool IsInitialized { get; set; }
            public string ProjectId { get; set; }
            public string ServiceAccountEmail { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Event args for compute operation start;
        /// </summary>
        public class ComputeOperationStartedEventArgs : EventArgs;
        {
            public string Operation { get; set; }
            public string InstanceName { get; set; }
            public string Zone { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Event args for compute operation completion;
        /// </summary>
        public class ComputeOperationCompletedEventArgs : EventArgs;
        {
            public string Operation { get; set; }
            public string InstanceName { get; set; }
            public string Zone { get; set; }
            public string Status { get; set; }
            public TimeSpan Duration { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Event args for compute progress updates;
        /// </summary>
        public class ComputeProgressEventArgs : EventArgs;
        {
            public string Operation { get; set; }
            public int Progress { get; set; }
            public string Message { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Event args for instance state changes;
        /// </summary>
        public class InstanceStateChangedEventArgs : EventArgs;
        {
            public string InstanceName { get; set; }
            public string Zone { get; set; }
            public string OldState { get; set; }
            public string NewState { get; set; }
            public DateTime Timestamp { get; set; }
        }

        // Event classes for EventBus;
        public class ComputeEngineInitializedEvent;
        {
            public string ProjectId { get; set; }
            public string ServiceAccountEmail { get; set; }
            public string Region { get; set; }
            public string Zone { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class InstanceCreatedEvent;
        {
            public string InstanceName { get; set; }
            public string ProjectId { get; set; }
            public string Zone { get; set; }
            public string MachineType { get; set; }
            public string ImageFamily { get; set; }
            public string ExternalIp { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class InstanceStateChangedEvent;
        {
            public string InstanceName { get; set; }
            public string ProjectId { get; set; }
            public string Zone { get; set; }
            public string OldState { get; set; }
            public string NewState { get; set; }
            public string Operation { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class InstanceDeletedEvent;
        {
            public string InstanceName { get; set; }
            public string ProjectId { get; set; }
            public string Zone { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class InstanceResizedEvent;
        {
            public string InstanceName { get; set; }
            public string ProjectId { get; set; }
            public string Zone { get; set; }
            public string OldMachineType { get; set; }
            public string NewMachineType { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class DiskCreatedEvent;
        {
            public string DiskName { get; set; }
            public string ProjectId { get; set; }
            public string Zone { get; set; }
            public int SizeGb { get; set; }
            public string DiskType { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class DiskAttachedEvent;
        {
            public string InstanceName { get; set; }
            public string DiskName { get; set; }
            public string ProjectId { get; set; }
            public string Zone { get; set; }
            public string DeviceName { get; set; }
            public string Mode { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion;
    }

    /// <summary>
    /// Interface for compute services;
    /// </summary>
    public interface IComputeService : IDisposable
    {
        bool IsInitialized { get; }
        string ProjectId { get; }
        string DefaultRegion { get; set; }
        string DefaultZone { get; set; }

        Task<bool> InitializeWithServiceAccountAsync(string credentialsPath, string projectId);
        Task<bool> InitializeWithServiceAccountJsonAsync(string jsonContent, string projectId);
        Task<bool> InitializeWithDefaultCredentialsAsync(string projectId);

        Task<Instance> CreateInstanceAsync(
            string instanceName,
            string machineType = "n1-standard-1",
            string imageFamily = "debian-11",
            string imageProject = "debian-cloud",
            int diskSizeGb = 10,
            string zone = null,
            IDictionary<string, string> metadata = null,
            IList<string> tags = null,
            bool preemptible = false,
            bool enableExternalIp = true);

        Task StartInstanceAsync(string instanceName, string zone = null);
        Task StopInstanceAsync(string instanceName, string zone = null);
        Task DeleteInstanceAsync(string instanceName, string zone = null);
        Task<Instance> GetInstanceAsync(string instanceName, string zone = null);
        Task<IList<Instance>> ListInstancesAsync(string filter = null, int? maxResults = null);
        Task ResizeInstanceAsync(string instanceName, string newMachineType, string zone = null);

        Task<Disk> CreateDiskAsync(
            string diskName,
            int sizeGb,
            string diskType = "pd-ssd",
            string zone = null,
            string snapshot = null,
            string image = null);

        Task AttachDiskAsync(
            string instanceName,
            string diskName,
            string zone = null,
            string deviceName = null,
            DiskMode mode = DiskMode.READ_WRITE);
    }
}
