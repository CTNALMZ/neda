using NEDA.Cloud.AWS.Models;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NEDA.Cloud.AWS;
{
    /// <summary>
    /// Amazon EC2 (Elastic Compute Cloud) instance management system for NEDA.
    /// Provides comprehensive control over EC2 instances, security groups, volumes, and networking.
    /// </summary>
    public class EC2Manager : IDisposable
    {
        #region Constants;

        private const string AWS_EC2_SERVICE_NAME = "ec2";
        private const string AWS_EC2_API_VERSION = "2016-11-15";
        private const string DEFAULT_REGION = "us-east-1";
        private const int DEFAULT_MAX_RETRIES = 3;
        private const int DEFAULT_TIMEOUT_SECONDS = 30;
        private const int INSTANCE_POLL_INTERVAL_MS = 5000;
        private const int MAX_INSTANCES_PER_REQUEST = 100;

        #endregion;

        #region Private Fields;

        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly AWSClient _awsClient;
        private readonly string _region;
        private readonly object _syncLock = new object();
        private bool _disposed = false;
        private bool _isInitialized = false;
        private System.Timers.Timer _instanceMonitorTimer;
        private readonly Dictionary<string, EC2Instance> _monitoredInstances;
        private readonly ConcurrentDictionary<string, EC2Operation> _activeOperations;

        #endregion;

        #region Properties;

        /// <summary>
        /// AWS region for EC2 operations;
        /// </summary>
        public string Region => _region;

        /// <summary>
        /// AWS account ID;
        /// </summary>
        public string AccountId => _awsClient?.AccountId;

        /// <summary>
        /// Number of monitored instances;
        /// </summary>
        public int MonitoredInstanceCount => _monitoredInstances.Count;

        /// <summary>
        /// Number of active operations;
        /// </summary>
        public int ActiveOperationCount => _activeOperations.Count;

        /// <summary>
        /// EC2 manager configuration;
        /// </summary>
        public EC2ManagerConfiguration Configuration { get; private set; }

        /// <summary>
        /// EC2 service statistics;
        /// </summary>
        public EC2ServiceStatistics Statistics { get; private set; }

        /// <summary>
        /// Instance type cache for pricing and capabilities;
        /// </summary>
        public InstanceTypeCache InstanceTypeCache { get; private set; }

        /// <summary>
        /// Security group manager;
        /// </summary>
        public SecurityGroupManager SecurityGroupManager { get; private set; }

        /// <summary>
        /// Volume manager;
        /// </summary>
        public VolumeManager VolumeManager { get; private set; }

        /// <summary>
        /// Network interface manager;
        /// </summary>
        public NetworkInterfaceManager NetworkInterfaceManager { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Raised when an instance state changes;
        /// </summary>
        public event EventHandler<InstanceStateChangedEventArgs> InstanceStateChanged;

        /// <summary>
        /// Raised when an instance is launched;
        /// </summary>
        public event EventHandler<InstanceLaunchedEventArgs> InstanceLaunched;

        /// <summary>
        /// Raised when an instance is terminated;
        /// </summary>
        public event EventHandler<InstanceTerminatedEventArgs> InstanceTerminated;

        /// <summary>
        /// Raised when an instance is stopped;
        /// </summary>
        public event EventHandler<InstanceStoppedEventArgs> InstanceStopped;

        /// <summary>
        /// Raised when an instance is started;
        /// </summary>
        public event EventHandler<InstanceStartedEventArgs> InstanceStarted;

        /// <summary>
        /// Raised when an instance is rebooted;
        /// </summary>
        public event EventHandler<InstanceRebootedEventArgs> InstanceRebooted;

        /// <summary>
        /// Raised when instance monitoring data is updated;
        /// </summary>
        public event EventHandler<InstanceMonitoringUpdatedEventArgs> InstanceMonitoringUpdated;

        /// <summary>
        /// Raised when an EC2 operation completes;
        /// </summary>
        public event EventHandler<EC2OperationCompletedEventArgs> OperationCompleted;

        /// <summary>
        /// Raised when an EC2 operation fails;
        /// </summary>
        public event EventHandler<EC2OperationFailedEventArgs> OperationFailed;

        /// <summary>
        /// Raised when instance tags are updated;
        /// </summary>
        public event EventHandler<InstanceTagsUpdatedEventArgs> InstanceTagsUpdated;

        /// <summary>
        /// Raised when security group rules are modified;
        /// </summary>
        public event EventHandler<SecurityGroupRulesModifiedEventArgs> SecurityGroupRulesModified;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new EC2Manager instance;
        /// </summary>
        /// <param name="awsClient">AWS client for authentication</param>
        /// <param name="region">AWS region</param>
        /// <param name="logger">Logger instance</param>
        public EC2Manager(AWSClient awsClient, string region = null, ILogger logger = null)
        {
            _awsClient = awsClient ?? throw new ArgumentNullException(nameof(awsClient));
            _region = region ?? DEFAULT_REGION;
            _logger = logger ?? LoggerFactory.CreateLogger(nameof(EC2Manager));

            _httpClient = new HttpClient;
            {
                Timeout = TimeSpan.FromSeconds(DEFAULT_TIMEOUT_SECONDS)
            };

            _monitoredInstances = new Dictionary<string, EC2Instance>();
            _activeOperations = new ConcurrentDictionary<string, EC2Operation>();

            InitializeDefaults();
            InitializeSubManagers();
        }

        /// <summary>
        /// Initializes EC2Manager with custom configuration;
        /// </summary>
        public EC2Manager(AWSClient awsClient, EC2ManagerConfiguration configuration,
                         string region = null, ILogger logger = null)
            : this(awsClient, region, logger)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            ApplyConfiguration();
        }

        #endregion;

        #region Public Methods - Initialization;

        /// <summary>
        /// Initializes the EC2 manager;
        /// </summary>
        public async Task InitializeAsync()
        {
            ValidateNotDisposed();

            try
            {
                _logger.LogInformation($"Initializing EC2Manager for region: {_region}");

                // Validate AWS client credentials;
                if (!await _awsClient.ValidateCredentialsAsync())
                {
                    throw new EC2InitializationException("AWS client credentials are invalid");
                }

                // Load instance type cache;
                await LoadInstanceTypeCacheAsync();

                // Initialize sub-managers;
                await SecurityGroupManager.InitializeAsync();
                await VolumeManager.InitializeAsync();
                await NetworkInterfaceManager.InitializeAsync();

                // Start instance monitoring;
                InitializeInstanceMonitor();

                _isInitialized = true;

                _logger.LogInformation($"EC2Manager initialized successfully for region: {_region}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize EC2Manager");
                throw new EC2InitializationException("Failed to initialize EC2Manager", ex);
            }
        }

        /// <summary>
        /// Validates EC2 manager configuration and connectivity;
        /// </summary>
        /// <returns>Validation result</returns>
        public async Task<EC2ValidationResult> ValidateAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var result = new EC2ValidationResult();

            try
            {
                _logger.LogInformation("Validating EC2 manager...");

                // Test API connectivity;
                var describeInstancesResult = await DescribeInstancesAsync(new DescribeInstancesRequest;
                {
                    MaxResults = 1;
                });

                if (describeInstancesResult != null)
                {
                    result.IsConnected = true;
                    result.ConnectivityTestPassed = true;
                }

                // Validate permissions;
                var permissionsResult = await ValidatePermissionsAsync();
                result.HasRequiredPermissions = permissionsResult.HasRequiredPermissions;
                result.MissingPermissions = permissionsResult.MissingPermissions;

                // Test sub-managers;
                result.SecurityGroupManagerValid = await SecurityGroupManager.ValidateAsync();
                result.VolumeManagerValid = await VolumeManager.ValidateAsync();
                result.NetworkInterfaceManagerValid = await NetworkInterfaceManager.ValidateAsync();

                // Check instance type cache;
                result.InstanceTypeCacheValid = InstanceTypeCache != null && InstanceTypeCache.Count > 0;

                result.IsValid = result.IsConnected &&
                               result.HasRequiredPermissions &&
                               result.SecurityGroupManagerValid &&
                               result.VolumeManagerValid &&
                               result.NetworkInterfaceManagerValid &&
                               result.InstanceTypeCacheValid;

                _logger.LogInformation($"EC2 manager validation completed. Valid: {result.IsValid}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during EC2 manager validation");
                result.Errors.Add($"Validation error: {ex.Message}");
                result.IsValid = false;
            }

            return result;
        }

        #endregion;

        #region Public Methods - Instance Management;

        /// <summary>
        /// Launches a new EC2 instance;
        /// </summary>
        /// <param name="request">Launch instance request</param>
        /// <returns>Launch instance result</returns>
        public async Task<LaunchInstanceResult> LaunchInstanceAsync(LaunchInstanceRequest request)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(request, nameof(request));

            var operationId = GenerateOperationId("launch-instance");

            try
            {
                _logger.LogInformation($"Launching EC2 instance: {request.InstanceName}");

                // Validate request;
                var validationResult = await ValidateLaunchRequestAsync(request);
                if (!validationResult.IsValid)
                {
                    throw new EC2ValidationException($"Invalid launch request: {string.Join(", ", validationResult.Errors)}");
                }

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.LaunchInstance,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "request", request }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build launch parameters;
                var launchParams = BuildLaunchParameters(request);

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("RunInstances", launchParams);

                // Parse response;
                var instances = ParseRunInstancesResponse(response);
                if (instances == null || !instances.Any())
                {
                    throw new EC2OperationException("No instances were launched");
                }

                var instance = instances.First();

                // Add tags if specified;
                if (request.Tags != null && request.Tags.Any())
                {
                    await TagResourcesAsync(new[] { instance.InstanceId }, request.Tags);
                }

                // Create tags for the instance;
                var instanceTags = new List<Tag>
                {
                    new Tag { Key = "Name", Value = request.InstanceName },
                    new Tag { Key = "LaunchTime", Value = DateTime.UtcNow.ToString("o") },
                    new Tag { Key = "LaunchRequestId", Value = operationId }
                };

                if (request.Tags != null)
                {
                    instanceTags.AddRange(request.Tags);
                }

                await CreateTagsAsync(instance.InstanceId, instanceTags);

                // Wait for instance to reach running state if requested;
                if (request.WaitForRunning)
                {
                    instance = await WaitForInstanceStateAsync(instance.InstanceId,
                                                              InstanceStateName.Running,
                                                              request.WaitTimeoutSeconds ?? 300);
                }

                // Start monitoring the instance;
                if (Configuration.AutoMonitorNewInstances)
                {
                    await StartMonitoringInstanceAsync(instance.InstanceId);
                }

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;
                operation.Result = instance;

                // Update statistics;
                Statistics.InstancesLaunched++;
                Statistics.TotalInstancesLaunched++;

                // Create result;
                var result = new LaunchInstanceResult;
                {
                    Instance = instance,
                    OperationId = operationId,
                    LaunchTime = DateTime.UtcNow,
                    Request = request;
                };

                // Raise events;
                OnInstanceLaunched(instance, request);
                OnOperationCompleted(operation);

                _logger.LogInformation($"Instance launched successfully: {instance.InstanceId} ({instance.InstanceType})");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error launching instance: {request.InstanceName}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to launch instance: {request.InstanceName}", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Terminates EC2 instances;
        /// </summary>
        /// <param name="instanceIds">Instance IDs to terminate</param>
        /// <param name="force">Force termination</param>
        /// <returns>Termination result</returns>
        public async Task<TerminateInstancesResult> TerminateInstancesAsync(IEnumerable<string> instanceIds, bool force = false)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(instanceIds, nameof(instanceIds));

            var instanceIdList = instanceIds.ToList();
            if (!instanceIdList.Any())
            {
                throw new ArgumentException("At least one instance ID is required", nameof(instanceIds));
            }

            var operationId = GenerateOperationId("terminate-instances");

            try
            {
                _logger.LogInformation($"Terminating {instanceIdList.Count} EC2 instances: {string.Join(", ", instanceIdList)}");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.TerminateInstances,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "instanceIds", instanceIdList },
                        { "force", force }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build termination parameters;
                var terminationParams = new Dictionary<string, string>
                {
                    { "Action", "TerminateInstances" },
                    { "Version", AWS_EC2_API_VERSION }
                };

                for (int i = 0; i < instanceIdList.Count; i++)
                {
                    terminationParams.Add($"InstanceId.{i + 1}", instanceIdList[i]);
                }

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("TerminateInstances", terminationParams);

                // Parse response;
                var terminationResults = ParseTerminateInstancesResponse(response);

                // Stop monitoring terminated instances;
                foreach (var instanceId in instanceIdList)
                {
                    StopMonitoringInstance(instanceId);
                }

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;
                operation.Result = terminationResults;

                // Update statistics;
                Statistics.InstancesTerminated += instanceIdList.Count;
                Statistics.TotalInstancesTerminated += instanceIdList.Count;

                // Create result;
                var result = new TerminateInstancesResult;
                {
                    InstanceTerminations = terminationResults,
                    OperationId = operationId,
                    TerminationTime = DateTime.UtcNow,
                    ForceTermination = force;
                };

                // Raise events;
                foreach (var instanceId in instanceIdList)
                {
                    OnInstanceTerminated(instanceId, force);
                }
                OnOperationCompleted(operation);

                _logger.LogInformation($"Instances terminated successfully: {string.Join(", ", instanceIdList)}");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error terminating instances: {string.Join(", ", instanceIdList)}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to terminate instances: {string.Join(", ", instanceIdList)}", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Starts stopped EC2 instances;
        /// </summary>
        /// <param name="instanceIds">Instance IDs to start</param>
        /// <returns>Start instances result</returns>
        public async Task<StartInstancesResult> StartInstancesAsync(IEnumerable<string> instanceIds)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(instanceIds, nameof(instanceIds));

            var instanceIdList = instanceIds.ToList();
            if (!instanceIdList.Any())
            {
                throw new ArgumentException("At least one instance ID is required", nameof(instanceIds));
            }

            var operationId = GenerateOperationId("start-instances");

            try
            {
                _logger.LogInformation($"Starting {instanceIdList.Count} EC2 instances: {string.Join(", ", instanceIdList)}");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.StartInstances,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "instanceIds", instanceIdList }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build start parameters;
                var startParams = new Dictionary<string, string>
                {
                    { "Action", "StartInstances" },
                    { "Version", AWS_EC2_API_VERSION }
                };

                for (int i = 0; i < instanceIdList.Count; i++)
                {
                    startParams.Add($"InstanceId.{i + 1}", instanceIdList[i]);
                }

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("StartInstances", startParams);

                // Parse response;
                var startResults = ParseStartInstancesResponse(response);

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;
                operation.Result = startResults;

                // Update statistics;
                Statistics.InstancesStarted += instanceIdList.Count;
                Statistics.TotalInstancesStarted += instanceIdList.Count;

                // Create result;
                var result = new StartInstancesResult;
                {
                    InstanceStarts = startResults,
                    OperationId = operationId,
                    StartTime = DateTime.UtcNow;
                };

                // Raise events;
                foreach (var instanceId in instanceIdList)
                {
                    OnInstanceStarted(instanceId);
                }
                OnOperationCompleted(operation);

                _logger.LogInformation($"Instances started successfully: {string.Join(", ", instanceIdList)}");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error starting instances: {string.Join(", ", instanceIdList)}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to start instances: {string.Join(", ", instanceIdList)}", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Stops running EC2 instances;
        /// </summary>
        /// <param name="instanceIds">Instance IDs to stop</param>
        /// <param name="force">Force stop</param>
        /// <returns>Stop instances result</returns>
        public async Task<StopInstancesResult> StopInstancesAsync(IEnumerable<string> instanceIds, bool force = false)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(instanceIds, nameof(instanceIds));

            var instanceIdList = instanceIds.ToList();
            if (!instanceIdList.Any())
            {
                throw new ArgumentException("At least one instance ID is required", nameof(instanceIds));
            }

            var operationId = GenerateOperationId("stop-instances");

            try
            {
                _logger.LogInformation($"Stopping {instanceIdList.Count} EC2 instances: {string.Join(", ", instanceIdList)}");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.StopInstances,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "instanceIds", instanceIdList },
                        { "force", force }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build stop parameters;
                var stopParams = new Dictionary<string, string>
                {
                    { "Action", "StopInstances" },
                    { "Version", AWS_EC2_API_VERSION }
                };

                for (int i = 0; i < instanceIdList.Count; i++)
                {
                    stopParams.Add($"InstanceId.{i + 1}", instanceIdList[i]);
                }

                if (force)
                {
                    stopParams.Add("Force", "true");
                }

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("StopInstances", stopParams);

                // Parse response;
                var stopResults = ParseStopInstancesResponse(response);

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;
                operation.Result = stopResults;

                // Update statistics;
                Statistics.InstancesStopped += instanceIdList.Count;
                Statistics.TotalInstancesStopped += instanceIdList.Count;

                // Create result;
                var result = new StopInstancesResult;
                {
                    InstanceStops = stopResults,
                    OperationId = operationId,
                    StopTime = DateTime.UtcNow,
                    ForceStop = force;
                };

                // Raise events;
                foreach (var instanceId in instanceIdList)
                {
                    OnInstanceStopped(instanceId, force);
                }
                OnOperationCompleted(operation);

                _logger.LogInformation($"Instances stopped successfully: {string.Join(", ", instanceIdList)}");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error stopping instances: {string.Join(", ", instanceIdList)}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to stop instances: {string.Join(", ", instanceIdList)}", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Reboots EC2 instances;
        /// </summary>
        /// <param name="instanceIds">Instance IDs to reboot</param>
        /// <returns>Reboot instances result</returns>
        public async Task<RebootInstancesResult> RebootInstancesAsync(IEnumerable<string> instanceIds)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(instanceIds, nameof(instanceIds));

            var instanceIdList = instanceIds.ToList();
            if (!instanceIdList.Any())
            {
                throw new ArgumentException("At least one instance ID is required", nameof(instanceIds));
            }

            var operationId = GenerateOperationId("reboot-instances");

            try
            {
                _logger.LogInformation($"Rebooting {instanceIdList.Count} EC2 instances: {string.Join(", ", instanceIdList)}");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.RebootInstances,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "instanceIds", instanceIdList }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build reboot parameters;
                var rebootParams = new Dictionary<string, string>
                {
                    { "Action", "RebootInstances" },
                    { "Version", AWS_EC2_API_VERSION }
                };

                for (int i = 0; i < instanceIdList.Count; i++)
                {
                    rebootParams.Add($"InstanceId.{i + 1}", instanceIdList[i]);
                }

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("RebootInstances", rebootParams);

                // Parse response - RebootInstances returns empty response on success;
                var rebootResults = new List<InstanceReboot>();
                foreach (var instanceId in instanceIdList)
                {
                    rebootResults.Add(new InstanceReboot;
                    {
                        InstanceId = instanceId,
                        PreviousState = GetInstanceState(instanceId)?.Name ?? InstanceStateName.Unknown,
                        RebootTime = DateTime.UtcNow;
                    });
                }

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;
                operation.Result = rebootResults;

                // Update statistics;
                Statistics.InstancesRebooted += instanceIdList.Count;
                Statistics.TotalInstancesRebooted += instanceIdList.Count;

                // Create result;
                var result = new RebootInstancesResult;
                {
                    InstanceReboots = rebootResults,
                    OperationId = operationId,
                    RebootTime = DateTime.UtcNow;
                };

                // Raise events;
                foreach (var instanceId in instanceIdList)
                {
                    OnInstanceRebooted(instanceId);
                }
                OnOperationCompleted(operation);

                _logger.LogInformation($"Instances rebooted successfully: {string.Join(", ", instanceIdList)}");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error rebooting instances: {string.Join(", ", instanceIdList)}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to reboot instances: {string.Join(", ", instanceIdList)}", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        #endregion;

        #region Public Methods - Instance Querying;

        /// <summary>
        /// Describes EC2 instances with optional filters;
        /// </summary>
        /// <param name="request">Describe instances request</param>
        /// <returns>Describe instances result</returns>
        public async Task<DescribeInstancesResult> DescribeInstancesAsync(DescribeInstancesRequest request = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            request ??= new DescribeInstancesRequest();

            try
            {
                _logger.LogDebug("Describing EC2 instances");

                // Build describe parameters;
                var describeParams = BuildDescribeInstancesParameters(request);

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("DescribeInstances", describeParams);

                // Parse response;
                var instances = ParseDescribeInstancesResponse(response);
                var reservations = GroupInstancesByReservation(instances);

                // Update statistics;
                Statistics.DescribeInstancesCalls++;
                Statistics.LastDescribeInstancesCall = DateTime.UtcNow;

                // Create result;
                var result = new DescribeInstancesResult;
                {
                    Reservations = reservations,
                    TotalInstances = instances.Count,
                    NextToken = ParseNextToken(response),
                    Request = request;
                };

                _logger.LogDebug($"Found {instances.Count} EC2 instances");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error describing EC2 instances");
                throw new EC2OperationException("Failed to describe EC2 instances", ex);
            }
        }

        /// <summary>
        /// Gets instance by ID;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <returns>EC2 instance or null</returns>
        public async Task<EC2Instance> GetInstanceAsync(string instanceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            try
            {
                _logger.LogDebug($"Getting EC2 instance: {instanceId}");

                var request = new DescribeInstancesRequest;
                {
                    InstanceIds = new List<string> { instanceId }
                };

                var result = await DescribeInstancesAsync(request);
                var instance = result.Reservations;
                    .SelectMany(r => r.Instances)
                    .FirstOrDefault();

                return instance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting instance: {instanceId}");
                throw new EC2OperationException($"Failed to get instance: {instanceId}", ex);
            }
        }

        /// <summary>
        /// Gets all instances in the region;
        /// </summary>
        /// <param name="filters">Optional filters</param>
        /// <returns>List of EC2 instances</returns>
        public async Task<List<EC2Instance>> GetAllInstancesAsync(List<Filter> filters = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                _logger.LogDebug("Getting all EC2 instances");

                var allInstances = new List<EC2Instance>();
                string nextToken = null;

                do;
                {
                    var request = new DescribeInstancesRequest;
                    {
                        MaxResults = MAX_INSTANCES_PER_REQUEST,
                        Filters = filters,
                        NextToken = nextToken;
                    };

                    var result = await DescribeInstancesAsync(request);
                    allInstances.AddRange(result.Reservations.SelectMany(r => r.Instances));
                    nextToken = result.NextToken;

                } while (!string.IsNullOrEmpty(nextToken));

                _logger.LogDebug($"Retrieved {allInstances.Count} EC2 instances");

                return allInstances;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all instances");
                throw new EC2OperationException("Failed to get all instances", ex);
            }
        }

        /// <summary>
        /// Gets instances by tag;
        /// </summary>
        /// <param name="tagKey">Tag key</param>
        /// <param name="tagValue">Tag value</param>
        /// <returns>List of EC2 instances</returns>
        public async Task<List<EC2Instance>> GetInstancesByTagAsync(string tagKey, string tagValue = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(tagKey, nameof(tagKey));

            try
            {
                _logger.LogDebug($"Getting EC2 instances by tag: {tagKey} = {tagValue}");

                var filters = new List<Filter>
                {
                    new Filter;
                    {
                        Name = $"tag:{tagKey}",
                        Values = tagValue != null ? new List<string> { tagValue } : new List<string> { "*" }
                    }
                };

                return await GetAllInstancesAsync(filters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting instances by tag: {tagKey}");
                throw new EC2OperationException($"Failed to get instances by tag: {tagKey}", ex);
            }
        }

        /// <summary>
        /// Gets instances by state;
        /// </summary>
        /// <param name="stateName">Instance state name</param>
        /// <returns>List of EC2 instances</returns>
        public async Task<List<EC2Instance>> GetInstancesByStateAsync(InstanceStateName stateName)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                _logger.LogDebug($"Getting EC2 instances by state: {stateName}");

                var filters = new List<Filter>
                {
                    new Filter;
                    {
                        Name = "instance-state-name",
                        Values = new List<string> { stateName.ToString().ToLower() }
                    }
                };

                return await GetAllInstancesAsync(filters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting instances by state: {stateName}");
                throw new EC2OperationException($"Failed to get instances by state: {stateName}", ex);
            }
        }

        /// <summary>
        /// Gets instances by instance type;
        /// </summary>
        /// <param name="instanceType">Instance type</param>
        /// <returns>List of EC2 instances</returns>
        public async Task<List<EC2Instance>> GetInstancesByTypeAsync(string instanceType)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceType, nameof(instanceType));

            try
            {
                _logger.LogDebug($"Getting EC2 instances by type: {instanceType}");

                var filters = new List<Filter>
                {
                    new Filter;
                    {
                        Name = "instance-type",
                        Values = new List<string> { instanceType }
                    }
                };

                return await GetAllInstancesAsync(filters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting instances by type: {instanceType}");
                throw new EC2OperationException($"Failed to get instances by type: {instanceType}", ex);
            }
        }

        /// <summary>
        /// Gets instance state;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <returns>Instance state or null</returns>
        public async Task<InstanceState> GetInstanceStateAsync(string instanceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            try
            {
                var instance = await GetInstanceAsync(instanceId);
                return instance?.State;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting instance state: {instanceId}");
                throw new EC2OperationException($"Failed to get instance state: {instanceId}", ex);
            }
        }

        /// <summary>
        /// Gets instance monitoring state;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <returns>Instance monitoring or null</returns>
        public async Task<InstanceMonitoring> GetInstanceMonitoringAsync(string instanceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            try
            {
                _logger.LogDebug($"Getting monitoring for instance: {instanceId}");

                var monitoringParams = new Dictionary<string, string>
                {
                    { "Action", "DescribeInstanceStatus" },
                    { "Version", AWS_EC2_API_VERSION },
                    { "InstanceId.1", instanceId },
                    { "IncludeAllInstances", "true" }
                };

                var response = await MakeEC2ApiRequestAsync("DescribeInstanceStatus", monitoringParams);
                var monitoring = ParseInstanceMonitoringResponse(response, instanceId);

                return monitoring;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting instance monitoring: {instanceId}");
                throw new EC2OperationException($"Failed to get instance monitoring: {instanceId}", ex);
            }
        }

        #endregion;

        #region Public Methods - Instance Monitoring;

        /// <summary>
        /// Starts monitoring an instance;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <returns>True if monitoring started</returns>
        public async Task<bool> StartMonitoringInstanceAsync(string instanceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            lock (_syncLock)
            {
                try
                {
                    if (_monitoredInstances.ContainsKey(instanceId))
                    {
                        _logger.LogWarning($"Instance already being monitored: {instanceId}");
                        return false;
                    }

                    _logger.LogInformation($"Starting monitoring for instance: {instanceId}");

                    var instance = GetInstanceAsync(instanceId).Result;
                    if (instance == null)
                    {
                        throw new EC2OperationException($"Instance not found: {instanceId}");
                    }

                    _monitoredInstances[instanceId] = instance;

                    // Enable detailed monitoring if configured;
                    if (Configuration.EnableDetailedMonitoring)
                    {
                        EnableDetailedMonitoringAsync(instanceId).Wait();
                    }

                    _logger.LogDebug($"Monitoring started for instance: {instanceId}");

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error starting monitoring for instance: {instanceId}");
                    throw new EC2OperationException($"Failed to start monitoring for instance: {instanceId}", ex);
                }
            }
        }

        /// <summary>
        /// Stops monitoring an instance;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <returns>True if monitoring stopped</returns>
        public bool StopMonitoringInstance(string instanceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            lock (_syncLock)
            {
                try
                {
                    if (!_monitoredInstances.ContainsKey(instanceId))
                    {
                        _logger.LogWarning($"Instance not being monitored: {instanceId}");
                        return false;
                    }

                    _logger.LogInformation($"Stopping monitoring for instance: {instanceId}");

                    _monitoredInstances.Remove(instanceId);

                    _logger.LogDebug($"Monitoring stopped for instance: {instanceId}");

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error stopping monitoring for instance: {instanceId}");
                    throw new EC2OperationException($"Failed to stop monitoring for instance: {instanceId}", ex);
                }
            }
        }

        /// <summary>
        /// Gets monitored instances;
        /// </summary>
        /// <returns>List of monitored instances</returns>
        public List<EC2Instance> GetMonitoredInstances()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                return _monitoredInstances.Values.ToList();
            }
        }

        /// <summary>
        /// Enables detailed monitoring for an instance;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <returns>Monitoring enablement result</returns>
        public async Task<MonitoringEnablementResult> EnableDetailedMonitoringAsync(string instanceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            var operationId = GenerateOperationId("enable-monitoring");

            try
            {
                _logger.LogInformation($"Enabling detailed monitoring for instance: {instanceId}");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.EnableMonitoring,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "instanceId", instanceId }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build monitoring parameters;
                var monitoringParams = new Dictionary<string, string>
                {
                    { "Action", "MonitorInstances" },
                    { "Version", AWS_EC2_API_VERSION },
                    { "InstanceId.1", instanceId }
                };

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("MonitorInstances", monitoringParams);

                // Parse response;
                var monitoringState = ParseMonitoringStateResponse(response, instanceId);

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;
                operation.Result = monitoringState;

                // Update statistics;
                Statistics.DetailedMonitoringEnabled++;

                // Create result;
                var result = new MonitoringEnablementResult;
                {
                    InstanceId = instanceId,
                    MonitoringState = monitoringState,
                    OperationId = operationId,
                    EnablementTime = DateTime.UtcNow;
                };

                // Raise event;
                OnOperationCompleted(operation);

                _logger.LogInformation($"Detailed monitoring enabled for instance: {instanceId}");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error enabling detailed monitoring for instance: {instanceId}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to enable detailed monitoring for instance: {instanceId}", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Disables detailed monitoring for an instance;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <returns>Monitoring disablement result</returns>
        public async Task<MonitoringDisablementResult> DisableDetailedMonitoringAsync(string instanceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            var operationId = GenerateOperationId("disable-monitoring");

            try
            {
                _logger.LogInformation($"Disabling detailed monitoring for instance: {instanceId}");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.DisableMonitoring,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "instanceId", instanceId }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build monitoring parameters;
                var monitoringParams = new Dictionary<string, string>
                {
                    { "Action", "UnmonitorInstances" },
                    { "Version", AWS_EC2_API_VERSION },
                    { "InstanceId.1", instanceId }
                };

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("UnmonitorInstances", monitoringParams);

                // Parse response;
                var monitoringState = ParseMonitoringStateResponse(response, instanceId);

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;
                operation.Result = monitoringState;

                // Update statistics;
                Statistics.DetailedMonitoringDisabled++;

                // Create result;
                var result = new MonitoringDisablementResult;
                {
                    InstanceId = instanceId,
                    MonitoringState = monitoringState,
                    OperationId = operationId,
                    DisablementTime = DateTime.UtcNow;
                };

                // Raise event;
                OnOperationCompleted(operation);

                _logger.LogInformation($"Detailed monitoring disabled for instance: {instanceId}");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error disabling detailed monitoring for instance: {instanceId}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to disable detailed monitoring for instance: {instanceId}", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Gets CloudWatch metrics for an instance;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <param name="metricName">Metric name</param>
        /// <param name="period">Period in seconds</param>
        /// <param name="startTime">Start time</param>
        /// <param name="endTime">End time</param>
        /// <returns>CloudWatch metrics</returns>
        public async Task<CloudWatchMetrics> GetInstanceMetricsAsync(string instanceId, string metricName = "CPUUtilization",
                                                                    int period = 300, DateTime? startTime = null,
                                                                    DateTime? endTime = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            try
            {
                _logger.LogDebug($"Getting CloudWatch metrics for instance: {instanceId}, metric: {metricName}");

                // This would integrate with CloudWatch service;
                // For now, return mock data or implement actual CloudWatch integration;

                var metrics = new CloudWatchMetrics;
                {
                    InstanceId = instanceId,
                    MetricName = metricName,
                    Period = period,
                    StartTime = startTime ?? DateTime.UtcNow.AddHours(-1),
                    EndTime = endTime ?? DateTime.UtcNow,
                    Datapoints = new List<MetricDatapoint>()
                };

                // TODO: Implement actual CloudWatch API call;

                return await Task.FromResult(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting metrics for instance: {instanceId}");
                throw new EC2OperationException($"Failed to get metrics for instance: {instanceId}", ex);
            }
        }

        #endregion;

        #region Public Methods - Instance Tags;

        /// <summary>
        /// Creates tags for EC2 resources;
        /// </summary>
        /// <param name="resourceIds">Resource IDs</param>
        /// <param name="tags">Tags to create</param>
        /// <returns>Tag creation result</returns>
        public async Task<CreateTagsResult> CreateTagsAsync(IEnumerable<string> resourceIds, IEnumerable<Tag> tags)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(resourceIds, nameof(resourceIds));
            ArgumentValidator.ThrowIfNull(tags, nameof(tags));

            var resourceIdList = resourceIds.ToList();
            var tagList = tags.ToList();

            if (!resourceIdList.Any())
            {
                throw new ArgumentException("At least one resource ID is required", nameof(resourceIds));
            }

            if (!tagList.Any())
            {
                throw new ArgumentException("At least one tag is required", nameof(tags));
            }

            var operationId = GenerateOperationId("create-tags");

            try
            {
                _logger.LogInformation($"Creating tags for {resourceIdList.Count} resources");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.CreateTags,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "resourceIds", resourceIdList },
                        { "tags", tagList }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build tag parameters;
                var tagParams = new Dictionary<string, string>
                {
                    { "Action", "CreateTags" },
                    { "Version", AWS_EC2_API_VERSION }
                };

                // Add resource IDs;
                for (int i = 0; i < resourceIdList.Count; i++)
                {
                    tagParams.Add($"ResourceId.{i + 1}", resourceIdList[i]);
                }

                // Add tags;
                for (int i = 0; i < tagList.Count; i++)
                {
                    tagParams.Add($"Tag.{i + 1}.Key", tagList[i].Key);
                    tagParams.Add($"Tag.{i + 1}.Value", tagList[i].Value);
                }

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("CreateTags", tagParams);

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;

                // Update statistics;
                Statistics.TagsCreated += tagList.Count;

                // Create result;
                var result = new CreateTagsResult;
                {
                    ResourceIds = resourceIdList,
                    Tags = tagList,
                    OperationId = operationId,
                    CreationTime = DateTime.UtcNow;
                };

                // Raise events for instance tags;
                var instanceIds = resourceIdList.Where(id => id.StartsWith("i-")).ToList();
                foreach (var instanceId in instanceIds)
                {
                    OnInstanceTagsUpdated(instanceId, tagList);
                }

                OnOperationCompleted(operation);

                _logger.LogInformation($"Tags created for {resourceIdList.Count} resources");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error creating tags for resources: {string.Join(", ", resourceIdList)}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to create tags for resources", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Deletes tags from EC2 resources;
        /// </summary>
        /// <param name="resourceIds">Resource IDs</param>
        /// <param name="tags">Tags to delete</param>
        /// <returns>Tag deletion result</returns>
        public async Task<DeleteTagsResult> DeleteTagsAsync(IEnumerable<string> resourceIds, IEnumerable<Tag> tags)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(resourceIds, nameof(resourceIds));
            ArgumentValidator.ThrowIfNull(tags, nameof(tags));

            var resourceIdList = resourceIds.ToList();
            var tagList = tags.ToList();

            if (!resourceIdList.Any())
            {
                throw new ArgumentException("At least one resource ID is required", nameof(resourceIds));
            }

            if (!tagList.Any())
            {
                throw new ArgumentException("At least one tag is required", nameof(tags));
            }

            var operationId = GenerateOperationId("delete-tags");

            try
            {
                _logger.LogInformation($"Deleting tags from {resourceIdList.Count} resources");

                // Create operation;
                var operation = new EC2Operation;
                {
                    Id = operationId,
                    Type = EC2OperationType.DeleteTags,
                    Status = EC2OperationStatus.InProgress,
                    StartTime = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object>
                    {
                        { "resourceIds", resourceIdList },
                        { "tags", tagList }
                    }
                };

                _activeOperations.TryAdd(operationId, operation);

                // Build tag parameters;
                var tagParams = new Dictionary<string, string>
                {
                    { "Action", "DeleteTags" },
                    { "Version", AWS_EC2_API_VERSION }
                };

                // Add resource IDs;
                for (int i = 0; i < resourceIdList.Count; i++)
                {
                    tagParams.Add($"ResourceId.{i + 1}", resourceIdList[i]);
                }

                // Add tags;
                for (int i = 0; i < tagList.Count; i++)
                {
                    tagParams.Add($"Tag.{i + 1}.Key", tagList[i].Key);
                    if (!string.IsNullOrEmpty(tagList[i].Value))
                    {
                        tagParams.Add($"Tag.{i + 1}.Value", tagList[i].Value);
                    }
                }

                // Make API call;
                var response = await MakeEC2ApiRequestAsync("DeleteTags", tagParams);

                // Update operation;
                operation.Status = EC2OperationStatus.Completed;
                operation.EndTime = DateTime.UtcNow;

                // Update statistics;
                Statistics.TagsDeleted += tagList.Count;

                // Create result;
                var result = new DeleteTagsResult;
                {
                    ResourceIds = resourceIdList,
                    Tags = tagList,
                    OperationId = operationId,
                    DeletionTime = DateTime.UtcNow;
                };

                // Raise events for instance tags;
                var instanceIds = resourceIdList.Where(id => id.StartsWith("i-")).ToList();
                foreach (var instanceId in instanceIds)
                {
                    OnInstanceTagsUpdated(instanceId, new List<Tag>()); // Empty list indicates tags removed;
                }

                OnOperationCompleted(operation);

                _logger.LogInformation($"Tags deleted from {resourceIdList.Count} resources");

                return result;
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error deleting tags from resources: {string.Join(", ", resourceIdList)}");

                // Update operation;
                if (_activeOperations.TryGetValue(operationId, out var operation))
                {
                    operation.Status = EC2OperationStatus.Failed;
                    operation.EndTime = DateTime.UtcNow;
                    operation.Error = ex.Message;

                    OnOperationFailed(operation, ex);
                }

                throw new EC2OperationException($"Failed to delete tags from resources", ex);
            }
            finally
            {
                _activeOperations.TryRemove(operationId, out _);
            }
        }

        /// <summary>
        /// Gets tags for a resource;
        /// </summary>
        /// <param name="resourceId">Resource ID</param>
        /// <returns>List of tags</returns>
        public async Task<List<Tag>> GetResourceTagsAsync(string resourceId)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(resourceId, nameof(resourceId));

            try
            {
                _logger.LogDebug($"Getting tags for resource: {resourceId}");

                var tagParams = new Dictionary<string, string>
                {
                    { "Action", "DescribeTags" },
                    { "Version", AWS_EC2_API_VERSION },
                    { "Filter.1.Name", "resource-id" },
                    { "Filter.1.Value.1", resourceId }
                };

                var response = await MakeEC2ApiRequestAsync("DescribeTags", tagParams);
                var tags = ParseDescribeTagsResponse(response);

                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting tags for resource: {resourceId}");
                throw new EC2OperationException($"Failed to get tags for resource: {resourceId}", ex);
            }
        }

        #endregion;

        #region Public Methods - Utility;

        /// <summary>
        /// Waits for instance to reach a specific state;
        /// </summary>
        /// <param name="instanceId">Instance ID</param>
        /// <param name="stateName">Target state name</param>
        /// <param name="timeoutSeconds">Timeout in seconds</param>
        /// <param name="pollIntervalSeconds">Poll interval in seconds</param>
        /// <returns>Instance in target state</returns>
        public async Task<EC2Instance> WaitForInstanceStateAsync(string instanceId, InstanceStateName stateName,
                                                                int timeoutSeconds = 300, int pollIntervalSeconds = 5)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceId, nameof(instanceId));

            try
            {
                _logger.LogInformation($"Waiting for instance {instanceId} to reach state: {stateName}");

                var startTime = DateTime.UtcNow;
                var timeout = TimeSpan.FromSeconds(timeoutSeconds);
                var pollInterval = TimeSpan.FromSeconds(pollIntervalSeconds);

                while (DateTime.UtcNow - startTime < timeout)
                {
                    var instance = await GetInstanceAsync(instanceId);
                    if (instance == null)
                    {
                        throw new EC2OperationException($"Instance not found: {instanceId}");
                    }

                    if (instance.State.Name == stateName)
                    {
                        _logger.LogInformation($"Instance {instanceId} reached state: {stateName}");
                        return instance;
                    }

                    if (IsTerminalState(instance.State.Name) && instance.State.Name != stateName)
                    {
                        throw new EC2OperationException($"Instance entered terminal state: {instance.State.Name}");
                    }

                    await Task.Delay(pollInterval);
                }

                throw new EC2TimeoutException($"Timeout waiting for instance {instanceId} to reach state: {stateName}");
            }
            catch (Exception ex) when (!(ex is EC2OperationException))
            {
                _logger.LogError(ex, $"Error waiting for instance state: {instanceId}");
                throw new EC2OperationException($"Failed to wait for instance state: {instanceId}", ex);
            }
        }

        /// <summary>
        /// Gets recommended instance types based on requirements;
        /// </summary>
        /// <param name="requirements">Instance requirements</param>
        /// <returns>Recommended instance types</returns>
        public async Task<List<InstanceTypeRecommendation>> GetRecommendedInstanceTypesAsync(InstanceRequirements requirements)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNull(requirements, nameof(requirements));

            try
            {
                _logger.LogDebug("Getting recommended instance types");

                var recommendations = new List<InstanceTypeRecommendation>();

                // Filter instance types based on requirements;
                var suitableTypes = InstanceTypeCache.InstanceTypes;
                    .Where(it => MeetsRequirements(it, requirements))
                    .ToList();

                // Sort by price (if available) and capabilities;
                foreach (var instanceType in suitableTypes)
                {
                    var recommendation = new InstanceTypeRecommendation;
                    {
                        InstanceType = instanceType,
                        SuitabilityScore = CalculateSuitabilityScore(instanceType, requirements),
                        EstimatedCostPerHour = await GetInstanceTypePriceAsync(instanceType.InstanceTypeName, _region),
                        Reasons = GetRecommendationReasons(instanceType, requirements)
                    };

                    recommendations.Add(recommendation);
                }

                // Sort by suitability score (descending)
                recommendations = recommendations;
                    .OrderByDescending(r => r.SuitabilityScore)
                    .ThenBy(r => r.EstimatedCostPerHour)
                    .ToList();

                return recommendations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recommended instance types");
                throw new EC2OperationException("Failed to get recommended instance types", ex);
            }
        }

        /// <summary>
        /// Gets instance type price;
        /// </summary>
        /// <param name="instanceType">Instance type</param>
        /// <param name="region">AWS region</param>
        /// <returns>Price per hour</returns>
        public async Task<decimal> GetInstanceTypePriceAsync(string instanceType, string region = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(instanceType, nameof(instanceType));

            try
            {
                // This would integrate with AWS Pricing API;
                // For now, return mock prices or implement actual pricing API integration;

                var prices = new Dictionary<string, decimal>
                {
                    { "t2.micro", 0.0116m },
                    { "t2.small", 0.023m },
                    { "t2.medium", 0.0464m },
                    { "t2.large", 0.0928m },
                    { "m5.large", 0.096m },
                    { "m5.xlarge", 0.192m },
                    { "m5.2xlarge", 0.384m },
                    { "c5.large", 0.085m },
                    { "c5.xlarge", 0.17m },
                    { "c5.2xlarge", 0.34m },
                    { "r5.large", 0.126m },
                    { "r5.xlarge", 0.252m },
                    { "r5.2xlarge", 0.504m }
                };

                var priceKey = instanceType.ToLower();
                if (prices.ContainsKey(priceKey))
                {
                    return prices[priceKey];
                }

                return 0.10m; // Default price;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting price for instance type: {instanceType}");
                throw new EC2OperationException($"Failed to get price for instance type: {instanceType}", ex);
            }
        }

        /// <summary>
        /// Gets EC2 service limits;
        /// </summary>
        /// <returns>Service limits</returns>
        public async Task<EC2ServiceLimits> GetServiceLimitsAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                _logger.LogDebug("Getting EC2 service limits");

                // This would integrate with AWS Service Quotas API;
                // For now, return mock limits or implement actual Service Quotas API integration;

                var limits = new EC2ServiceLimits;
                {
                    Region = _region,
                    AccountId = AccountId,
                    InstanceLimits = new InstanceLimits;
                    {
                        MaxRunningOnDemandInstances = 20,
                        CurrentRunningOnDemandInstances = await GetRunningInstanceCountAsync(),
                        MaxSpotInstances = 5,
                        CurrentSpotInstances = 0;
                    },
                    VolumeLimits = new VolumeLimits;
                    {
                        MaxVolumes = 100,
                        CurrentVolumes = 0,
                        MaxVolumeSizeGb = 16384,
                        MaxSnapshots = 10000;
                    },
                    NetworkLimits = new NetworkLimits;
                    {
                        MaxVpcs = 5,
                        MaxSecurityGroups = 500,
                        MaxElasticIps = 5;
                    }
                };

                return limits;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting service limits");
                throw new EC2OperationException("Failed to get service limits", ex);
            }
        }

        /// <summary>
        /// Gets EC2 service health;
        /// </summary>
        /// <returns>Service health status</returns>
        public async Task<EC2ServiceHealth> GetServiceHealthAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                _logger.LogDebug("Getting EC2 service health");

                // This would integrate with AWS Health API;
                // For now, return mock health status or implement actual Health API integration;

                var health = new EC2ServiceHealth;
                {
                    Region = _region,
                    Status = ServiceStatus.Normal,
                    LastUpdated = DateTime.UtcNow,
                    Events = new List<ServiceEvent>(),
                    Metrics = new ServiceMetrics;
                    {
                        ApiSuccessRate = 99.9,
                        AverageLatencyMs = 150,
                        ErrorRate = 0.1;
                    }
                };

                return await Task.FromResult(health);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting service health");
                throw new EC2OperationException("Failed to get service health", ex);
            }
        }

        /// <summary>
        /// Gets EC2 service statistics;
        /// </summary>
        /// <returns>Service statistics</returns>
        public EC2ServiceStatistics GetStatistics()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            return Statistics.Clone();
        }

        /// <summary>
        /// Resets EC2 service statistics;
        /// </summary>
        public void ResetStatistics()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                Statistics = new EC2ServiceStatistics();
                _logger.LogInformation("EC2 service statistics reset");
            }
        }

        #endregion;

        #region Private Methods;

        private void InitializeDefaults()
        {
            Configuration = new EC2ManagerConfiguration;
            {
                MaxRetries = DEFAULT_MAX_RETRIES,
                TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
                EnableDetailedMonitoring = true,
                AutoMonitorNewInstances = true,
                DefaultInstanceType = "t2.micro",
                DefaultSecurityGroupName = "default",
                DefaultKeyPairName = null,
                EnableCostOptimization = true,
                EnableAutoScalingIntegration = false,
                InstancePollIntervalMs = INSTANCE_POLL_INTERVAL_MS;
            };

            Statistics = new EC2ServiceStatistics();
            InstanceTypeCache = new InstanceTypeCache();
        }

        private void ApplyConfiguration()
        {
            // Apply configuration to HTTP client;
            _httpClient.Timeout = TimeSpan.FromSeconds(Configuration.TimeoutSeconds);
        }

        private void InitializeSubManagers()
        {
            SecurityGroupManager = new SecurityGroupManager(_awsClient, _region, _logger);
            VolumeManager = new VolumeManager(_awsClient, _region, _logger);
            NetworkInterfaceManager = new NetworkInterfaceManager(_awsClient, _region, _logger);

            // Wire up events from sub-managers;
            SecurityGroupManager.SecurityGroupRulesModified += (sender, e) =>
            {
                OnSecurityGroupRulesModified(e.SecurityGroupId, e.RulesAdded, e.RulesRemoved);
            };
        }

        private void InitializeInstanceMonitor()
        {
            if (Configuration.InstancePollIntervalMs <= 0)
                return;

            _instanceMonitorTimer = new System.Timers.Timer(Configuration.InstancePollIntervalMs);
            _instanceMonitorTimer.Elapsed += async (sender, e) => await OnInstanceMonitorTimerElapsedAsync();
            _instanceMonitorTimer.AutoReset = true;
            _instanceMonitorTimer.Start();

            _logger.LogDebug($"Instance monitor initialized ({Configuration.InstancePollIntervalMs}ms interval)");
        }

        private async Task OnInstanceMonitorTimerElapsedAsync()
        {
            try
            {
                if (_disposed || !_isInitialized || _monitoredInstances.Count == 0)
                    return;

                var instanceIds = _monitoredInstances.Keys.ToList();

                foreach (var instanceId in instanceIds)
                {
                    await UpdateInstanceStateAsync(instanceId);
                    await UpdateInstanceMonitoringAsync(instanceId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in instance monitor timer");
            }
        }

        private async Task UpdateInstanceStateAsync(string instanceId)
        {
            try
            {
                var currentInstance = _monitoredInstances[instanceId];
                var newInstance = await GetInstanceAsync(instanceId);

                if (newInstance == null)
                {
                    // Instance may have been terminated;
                    StopMonitoringInstance(instanceId);
                    return;
                }

                // Check for state change;
                if (currentInstance.State.Name != newInstance.State.Name)
                {
                    var previousState = currentInstance.State;
                    _monitoredInstances[instanceId] = newInstance;

                    OnInstanceStateChanged(instanceId, previousState, newInstance.State);

                    _logger.LogInformation($"Instance state changed: {instanceId} ({previousState.Name} -> {newInstance.State.Name})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating instance state: {instanceId}");
            }
        }

        private async Task UpdateInstanceMonitoringAsync(string instanceId)
        {
            try
            {
                var monitoring = await GetInstanceMonitoringAsync(instanceId);
                if (monitoring != null)
                {
                    OnInstanceMonitoringUpdated(instanceId, monitoring);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating instance monitoring: {instanceId}");
            }
        }

        private async Task LoadInstanceTypeCacheAsync()
        {
            try
            {
                _logger.LogDebug("Loading instance type cache");

                // This would load from AWS EC2 API or local cache;
                // For now, populate with common instance types;

                var instanceTypes = new List<InstanceTypeInfo>
                {
                    CreateInstanceTypeInfo("t2.micro", 1, 1.0, "low"),
                    CreateInstanceTypeInfo("t2.small", 1, 2.0, "low"),
                    CreateInstanceTypeInfo("t2.medium", 2, 4.0, "low"),
                    CreateInstanceTypeInfo("t2.large", 2, 8.0, "low"),
                    CreateInstanceTypeInfo("m5.large", 2, 8.0, "moderate"),
                    CreateInstanceTypeInfo("m5.xlarge", 4, 16.0, "moderate"),
                    CreateInstanceTypeInfo("m5.2xlarge", 8, 32.0, "moderate"),
                    CreateInstanceTypeInfo("c5.large", 2, 4.0, "high"),
                    CreateInstanceTypeInfo("c5.xlarge", 4, 8.0, "high"),
                    CreateInstanceTypeInfo("c5.2xlarge", 8, 16.0, "high"),
                    CreateInstanceTypeInfo("r5.large", 2, 16.0, "high"),
                    CreateInstanceTypeInfo("r5.xlarge", 4, 32.0, "high"),
                    CreateInstanceTypeInfo("r5.2xlarge", 8, 64.0, "high")
                };

                InstanceTypeCache.InstanceTypes = instanceTypes;
                InstanceTypeCache.LastUpdated = DateTime.UtcNow;

                _logger.LogDebug($"Loaded {instanceTypes.Count} instance types into cache");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading instance type cache");
                throw new EC2InitializationException("Failed to load instance type cache", ex);
            }
        }

        private InstanceTypeInfo CreateInstanceTypeInfo(string name, int vcpus, double memoryGb, string performance)
        {
            return new InstanceTypeInfo;
            {
                InstanceTypeName = name,
                VCpus = vcpus,
                MemoryGb = memoryGb,
                Performance = performance,
                NetworkPerformance = GetNetworkPerformance(name),
                StorageType = GetStorageType(name),
                SupportedArchitectures = new List<string> { "x86_64" },
                SupportedVirtualizationTypes = new List<string> { "hvm" },
                Features = GetInstanceFeatures(name)
            };
        }

        private string GetNetworkPerformance(string instanceType)
        {
            if (instanceType.StartsWith("t2"))
                return "Low to Moderate";
            if (instanceType.StartsWith("c5") || instanceType.StartsWith("m5") || instanceType.StartsWith("r5"))
                return "Up to 10 Gbps";
            return "Moderate";
        }

        private string GetStorageType(string instanceType)
        {
            if (instanceType.StartsWith("c5") || instanceType.StartsWith("m5") || instanceType.StartsWith("r5"))
                return "EBS only";
            return "EBS only";
        }

        private List<string> GetInstanceFeatures(string instanceType)
        {
            var features = new List<string> { "EBS optimized" };

            if (instanceType.StartsWith("t2"))
                features.Add("Burstable performance");
            if (instanceType.StartsWith("c5"))
                features.Add("Compute optimized");
            if (instanceType.StartsWith("m5"))
                features.Add("General purpose");
            if (instanceType.StartsWith("r5"))
                features.Add("Memory optimized");

            return features;
        }

        private async Task<EC2ValidationResult> ValidatePermissionsAsync()
        {
            var result = new EC2ValidationResult();

            try
            {
                // Test basic EC2 permissions;
                await DescribeInstancesAsync(new DescribeInstancesRequest { MaxResults = 1 });
                result.HasRequiredPermissions = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Permission validation failed: {ex.Message}");
                result.MissingPermissions.Add("ec2:DescribeInstances");
                result.HasRequiredPermissions = false;
            }

            return result;
        }

        private async Task<LaunchRequestValidationResult> ValidateLaunchRequestAsync(LaunchInstanceRequest request)
        {
            var result = new LaunchRequestValidationResult();

            // Validate required fields;
            if (string.IsNullOrEmpty(request.ImageId))
                result.Errors.Add("ImageId is required");

            if (string.IsNullOrEmpty(request.InstanceType))
                result.Errors.Add("InstanceType is required");

            // Validate instance type;
            if (!InstanceTypeCache.InstanceTypes.Any(it => it.InstanceTypeName == request.InstanceType))
                result.Warnings.Add($"Instance type '{request.InstanceType}' may not be available in region {_region}");

            // Validate security groups;
            if (request.SecurityGroupIds != null)
            {
                foreach (var sgId in request.SecurityGroupIds)
                {
                    var sg = await SecurityGroupManager.GetSecurityGroupAsync(sgId);
                    if (sg == null)
                        result.Errors.Add($"Security group not found: {sgId}");
                }
            }

            // Validate key pair;
            if (!string.IsNullOrEmpty(request.KeyName))
            {
                // This would validate key pair exists;
                // For now, just log a warning;
                result.Warnings.Add($"Key pair '{request.KeyName}' existence not validated");
            }

            // Validate subnet;
            if (!string.IsNullOrEmpty(request.SubnetId))
            {
                // This would validate subnet exists;
                // For now, just log a warning;
                result.Warnings.Add($"Subnet '{request.SubnetId}' existence not validated");
            }

            result.IsValid = !result.Errors.Any();

            return result;
        }

        private Dictionary<string, string> BuildLaunchParameters(LaunchInstanceRequest request)
        {
            var parameters = new Dictionary<string, string>
            {
                { "Action", "RunInstances" },
                { "Version", AWS_EC2_API_VERSION },
                { "ImageId", request.ImageId },
                { "InstanceType", request.InstanceType },
                { "MinCount", "1" },
                { "MaxCount", "1" }
            };

            // Add optional parameters;
            if (!string.IsNullOrEmpty(request.KeyName))
                parameters.Add("KeyName", request.KeyName);

            if (!string.IsNullOrEmpty(request.SubnetId))
                parameters.Add("SubnetId", request.SubnetId);

            if (request.SecurityGroupIds != null && request.SecurityGroupIds.Any())
            {
                for (int i = 0; i < request.SecurityGroupIds.Count; i++)
                {
                    parameters.Add($"SecurityGroupId.{i + 1}", request.SecurityGroupIds[i]);
                }
            }
            else if (!string.IsNullOrEmpty(request.SecurityGroup))
            {
                parameters.Add("SecurityGroup.1", request.SecurityGroup);
            }

            if (request.UserData != null)
            {
                var userDataBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.UserData));
                parameters.Add("UserData", userDataBase64);
            }

            if (request.IamInstanceProfile != null)
            {
                if (!string.IsNullOrEmpty(request.IamInstanceProfile.Arn))
                    parameters.Add("IamInstanceProfile.Arn", request.IamInstanceProfile.Arn);
                else if (!string.IsNullOrEmpty(request.IamInstanceProfile.Name))
                    parameters.Add("IamInstanceProfile.Name", request.IamInstanceProfile.Name);
            }

            if (request.BlockDeviceMappings != null && request.BlockDeviceMappings.Any())
            {
                for (int i = 0; i < request.BlockDeviceMappings.Count; i++)
                {
                    var mapping = request.BlockDeviceMappings[i];
                    parameters.Add($"BlockDeviceMapping.{i + 1}.DeviceName", mapping.DeviceName);

                    if (mapping.Ebs != null)
                    {
                        if (mapping.Ebs.DeleteOnTermination.HasValue)
                            parameters.Add($"BlockDeviceMapping.{i + 1}.Ebs.DeleteOnTermination", mapping.Ebs.DeleteOnTermination.Value.ToString().ToLower());

                        if (mapping.Ebs.VolumeSize.HasValue)
                            parameters.Add($"BlockDeviceMapping.{i + 1}.Ebs.VolumeSize", mapping.Ebs.VolumeSize.Value.ToString());

                        if (!string.IsNullOrEmpty(mapping.Ebs.VolumeType))
                            parameters.Add($"BlockDeviceMapping.{i + 1}.Ebs.VolumeType", mapping.Ebs.VolumeType);

                        if (mapping.Ebs.Iops.HasValue)
                            parameters.Add($"BlockDeviceMapping.{i + 1}.Ebs.Iops", mapping.Ebs.Iops.Value.ToString());

                        if (mapping.Ebs.Encrypted.HasValue)
                            parameters.Add($"BlockDeviceMapping.{i + 1}.Ebs.Encrypted", mapping.Ebs.Encrypted.Value.ToString().ToLower());
                    }
                }
            }

            if (request.NetworkInterfaces != null && request.NetworkInterfaces.Any())
            {
                for (int i = 0; i < request.NetworkInterfaces.Count; i++)
                {
                    var ni = request.NetworkInterfaces[i];
                    parameters.Add($"NetworkInterface.{i + 1}.DeviceIndex", "0");

                    if (!string.IsNullOrEmpty(ni.NetworkInterfaceId))
                        parameters.Add($"NetworkInterface.{i + 1}.NetworkInterfaceId", ni.NetworkInterfaceId);

                    if (!string.IsNullOrEmpty(ni.SubnetId))
                        parameters.Add($"NetworkInterface.{i + 1}.SubnetId", ni.SubnetId);

                    if (ni.Groups != null && ni.Groups.Any())
                    {
                        for (int j = 0; j < ni.Groups.Count; j++)
                        {
                            parameters.Add($"NetworkInterface.{i + 1}.SecurityGroupId.{j + 1}", ni.Groups[j]);
                        }
                    }

                    if (ni.DeleteOnTermination.HasValue)
                        parameters.Add($"NetworkInterface.{i + 1}.DeleteOnTermination", ni.DeleteOnTermination.Value.ToString().ToLower());

                    if (!string.IsNullOrEmpty(ni.Description))
                        parameters.Add($"NetworkInterface.{i + 1}.Description", ni.Description);
                }
            }

            if (request.Placement != null)
            {
                if (!string.IsNullOrEmpty(request.Placement.AvailabilityZone))
                    parameters.Add("Placement.AvailabilityZone", request.Placement.AvailabilityZone);

                if (!string.IsNullOrEmpty(request.Placement.GroupName))
                    parameters.Add("Placement.GroupName", request.Placement.GroupName);

                if (!string.IsNullOrEmpty(request.Placement.Tenancy))
                    parameters.Add("Placement.Tenancy", request.Placement.Tenancy);
            }

            return parameters;
        }

        private Dictionary<string, string> BuildDescribeInstancesParameters(DescribeInstancesRequest request)
        {
            var parameters = new Dictionary<string, string>
            {
                { "Action", "DescribeInstances" },
                { "Version", AWS_EC2_API_VERSION }
            };

            // Add instance IDs;
            if (request.InstanceIds != null && request.InstanceIds.Any())
            {
                for (int i = 0; i < request.InstanceIds.Count; i++)
                {
                    parameters.Add($"InstanceId.{i + 1}", request.InstanceIds[i]);
                }
            }

            // Add filters;
            if (request.Filters != null && request.Filters.Any())
            {
                for (int i = 0; i < request.Filters.Count; i++)
                {
                    var filter = request.Filters[i];
                    parameters.Add($"Filter.{i + 1}.Name", filter.Name);

                    for (int j = 0; j < filter.Values.Count; j++)
                    {
                        parameters.Add($"Filter.{i + 1}.Value.{j + 1}", filter.Values[j]);
                    }
                }
            }

            // Add pagination;
            if (request.MaxResults.HasValue)
                parameters.Add("MaxResults", request.MaxResults.Value.ToString());

            if (!string.IsNullOrEmpty(request.NextToken))
                parameters.Add("NextToken", request.NextToken);

            return parameters;
        }

        private async Task<string> MakeEC2ApiRequestAsync(string action, Dictionary<string, string> parameters)
        {
            try
            {
                // Build endpoint URL;
                var endpoint = $"https://ec2.{_region}.amazonaws.com/";

                // Add required parameters;
                parameters["Action"] = action;
                parameters["Version"] = AWS_EC2_API_VERSION;

                // Sign the request using AWS Signature Version 4;
                var signedRequest = await _awsClient.SignRequestAsync(endpoint, parameters, AWS_EC2_SERVICE_NAME, _region);

                // Make the request;
                var response = await _httpClient.SendAsync(signedRequest);

                // Check for errors;
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new EC2ApiException($"EC2 API error ({response.StatusCode}): {errorContent}");
                }

                // Return response content;
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (!(ex is EC2ApiException))
            {
                _logger.LogError(ex, $"Error making EC2 API request: {action}");
                throw new EC2ApiException($"Failed to make EC2 API request: {action}", ex);
            }
        }

        private List<EC2Instance> ParseRunInstancesResponse(string response)
        {
            try
            {
                // Parse XML response;
                // This is a simplified implementation - actual implementation would use XML parsing;

                var instances = new List<EC2Instance>();

                // Parse instance ID, state, etc. from XML;
                // For now, return mock instances;

                var instance = new EC2Instance;
                {
                    InstanceId = "i-mockinstanceid",
                    InstanceType = "t2.micro",
                    State = new InstanceState;
                    {
                        Name = InstanceStateName.Pending,
                        Code = 0;
                    },
                    LaunchTime = DateTime.UtcNow,
                    ImageId = "ami-mockimageid",
                    KeyName = "mock-key",
                    SecurityGroups = new List<InstanceSecurityGroup>
                    {
                        new InstanceSecurityGroup { GroupId = "sg-mock", GroupName = "default" }
                    },
                    Tags = new List<Tag>(),
                    Monitoring = new InstanceMonitoring;
                    {
                        State = MonitoringState.Disabled;
                    }
                };

                instances.Add(instance);

                return instances;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing RunInstances response");
                throw new EC2ParseException("Failed to parse RunInstances response", ex);
            }
        }

        private List<EC2Instance> ParseDescribeInstancesResponse(string response)
        {
            try
            {
                // Parse XML response;
                // This is a simplified implementation - actual implementation would use XML parsing;

                var instances = new List<EC2Instance>();

                // Parse instances from XML response;
                // For now, return empty list or implement actual XML parsing;

                return instances;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing DescribeInstances response");
                throw new EC2ParseException("Failed to parse DescribeInstances response", ex);
            }
        }

        private List<InstanceTermination> ParseTerminateInstancesResponse(string response)
        {
            try
            {
                // Parse XML response;
                var terminations = new List<InstanceTermination>();

                // Parse terminations from XML;
                // For now, return mock terminations;

                return terminations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing TerminateInstances response");
                throw new EC2ParseException("Failed to parse TerminateInstances response", ex);
            }
        }

        private List<InstanceStart> ParseStartInstancesResponse(string response)
        {
            try
            {
                // Parse XML response;
                var starts = new List<InstanceStart>();

                // Parse starts from XML;
                // For now, return mock starts;

                return starts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing StartInstances response");
                throw new EC2ParseException("Failed to parse StartInstances response", ex);
            }
        }

        private List<InstanceStop> ParseStopInstancesResponse(string response)
        {
            try
            {
                // Parse XML response;
                var stops = new List<InstanceStop>();

                // Parse stops from XML;
                // For now, return mock stops;

                return stops;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing StopInstances response");
                throw new EC2ParseException("Failed to parse StopInstances response", ex);
            }
        }

        private InstanceMonitoring ParseInstanceMonitoringResponse(string response, string instanceId)
        {
            try
            {
                // Parse XML response;
                var monitoring = new InstanceMonitoring;
                {
                    InstanceId = instanceId,
                    State = MonitoringState.Disabled;
                };

                return monitoring;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing InstanceMonitoring response");
                throw new EC2ParseException("Failed to parse InstanceMonitoring response", ex);
            }
        }

        private MonitoringState ParseMonitoringStateResponse(string response, string instanceId)
        {
            try
            {
                // Parse XML response;
                return MonitoringState.Enabled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing MonitoringState response");
                throw new EC2ParseException("Failed to parse MonitoringState response", ex);
            }
        }

        private List<Tag> ParseDescribeTagsResponse(string response)
        {
            try
            {
                // Parse XML response;
                var tags = new List<Tag>();

                // Parse tags from XML;
                // For now, return empty list;

                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing DescribeTags response");
                throw new EC2ParseException("Failed to parse DescribeTags response", ex);
            }
        }

        private string ParseNextToken(string response)
        {
            try
            {
                // Parse nextToken from XML response;
                // For now, return null;
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing nextToken");
                return null;
            }
        }

        private List<Reservation> GroupInstancesByReservation(List<EC2Instance> instances)
        {
            var reservations = new Dictionary<string, Reservation>();

            foreach (var instance in instances)
            {
                if (!reservations.ContainsKey(instance.ReservationId))
                {
                    reservations[instance.ReservationId] = new Reservation;
                    {
                        ReservationId = instance.ReservationId,
                        Instances = new List<EC2Instance>()
                    };
                }

                reservations[instance.ReservationId].Instances.Add(instance);
            }

            return reservations.Values.ToList();
        }

        private bool MeetsRequirements(InstanceTypeInfo instanceType, InstanceRequirements requirements)
        {
            // Check vCPUs;
            if (requirements.MinVCpus.HasValue && instanceType.VCpus < requirements.MinVCpus.Value)
                return false;

            if (requirements.MaxVCpus.HasValue && instanceType.VCpus > requirements.MaxVCpus.Value)
                return false;

            // Check memory;
            if (requirements.MinMemoryGb.HasValue && instanceType.MemoryGb < requirements.MinMemoryGb.Value)
                return false;

            if (requirements.MaxMemoryGb.HasValue && instanceType.MemoryGb > requirements.MaxMemoryGb.Value)
                return false;

            // Check instance type family;
            if (!string.IsNullOrEmpty(requirements.InstanceFamily))
            {
                if (!instanceType.InstanceTypeName.StartsWith(requirements.InstanceFamily))
                    return false;
            }

            // Check performance;
            if (!string.IsNullOrEmpty(requirements.Performance))
            {
                if (!instanceType.Performance.Equals(requirements.Performance, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private double CalculateSuitabilityScore(InstanceTypeInfo instanceType, InstanceRequirements requirements)
        {
            double score = 100.0;

            // Penalize for over-provisioning;
            if (requirements.MinVCpus.HasValue)
            {
                var cpuRatio = (double)instanceType.VCpus / requirements.MinVCpus.Value;
                if (cpuRatio > 2.0) score -= 20;
                else if (cpuRatio > 1.5) score -= 10;
            }

            if (requirements.MinMemoryGb.HasValue)
            {
                var memoryRatio = instanceType.MemoryGb / requirements.MinMemoryGb.Value;
                if (memoryRatio > 2.0) score -= 20;
                else if (memoryRatio > 1.5) score -= 10;
            }

            // Bonus for exact match;
            if (requirements.PreferredInstanceType == instanceType.InstanceTypeName)
                score += 30;

            return Math.Max(0, score);
        }

        private List<string> GetRecommendationReasons(InstanceTypeInfo instanceType, InstanceRequirements requirements)
        {
            var reasons = new List<string>();

            if (requirements.MinVCpus.HasValue && instanceType.VCpus >= requirements.MinVCpus.Value)
                reasons.Add($"Meets vCPU requirement ({instanceType.VCpus} vCPUs)");

            if (requirements.MinMemoryGb.HasValue && instanceType.MemoryGb >= requirements.MinMemoryGb.Value)
                reasons.Add($"Meets memory requirement ({instanceType.MemoryGb} GB)");

            if (!string.IsNullOrEmpty(requirements.InstanceFamily) &&
                instanceType.InstanceTypeName.StartsWith(requirements.InstanceFamily))
                reasons.Add($"Matches instance family: {requirements.InstanceFamily}");

            if (requirements.PreferredInstanceType == instanceType.InstanceTypeName)
                reasons.Add("Matches preferred instance type");

            return reasons;
        }

        private async Task<int> GetRunningInstanceCountAsync()
        {
            try
            {
                var runningInstances = await GetInstancesByStateAsync(InstanceStateName.Running);
                return runningInstances.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private InstanceState GetInstanceState(string instanceId)
        {
            // Get from cache or make API call;
            if (_monitoredInstances.TryGetValue(instanceId, out var instance))
            {
                return instance.State;
            }

            return null;
        }

        private bool IsTerminalState(InstanceStateName stateName)
        {
            return stateName == InstanceStateName.Terminated ||
                   stateName == InstanceStateName.ShuttingDown ||
                   stateName == InstanceStateName.Stopped;
        }

        private string GenerateOperationId(string prefix)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            return $"{prefix}-{timestamp}-{random}";
        }

        private async Task TagResourcesAsync(IEnumerable<string> resourceIds, IEnumerable<Tag> tags)
        {
            await CreateTagsAsync(resourceIds, tags);
        }

        private void ValidateNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EC2Manager));
            }
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new EC2NotInitializedException("EC2Manager must be initialized before use");
            }
        }

        #endregion;

        #region Event Invokers;

        private void OnInstanceStateChanged(string instanceId, InstanceState previousState, InstanceState newState)
        {
            InstanceStateChanged?.Invoke(this, new InstanceStateChangedEventArgs(instanceId, previousState, newState));
        }

        private void OnInstanceLaunched(EC2Instance instance, LaunchInstanceRequest request)
        {
            InstanceLaunched?.Invoke(this, new InstanceLaunchedEventArgs(instance, request));
        }

        private void OnInstanceTerminated(string instanceId, bool force)
        {
            InstanceTerminated?.Invoke(this, new InstanceTerminatedEventArgs(instanceId, force));
        }

        private void OnInstanceStopped(string instanceId, bool force)
        {
            InstanceStopped?.Invoke(this, new InstanceStoppedEventArgs(instanceId, force));
        }

        private void OnInstanceStarted(string instanceId)
        {
            InstanceStarted?.Invoke(this, new InstanceStartedEventArgs(instanceId));
        }

        private void OnInstanceRebooted(string instanceId)
        {
            InstanceRebooted?.Invoke(this, new InstanceRebootedEventArgs(instanceId));
        }

        private void OnInstanceMonitoringUpdated(string instanceId, InstanceMonitoring monitoring)
        {
            InstanceMonitoringUpdated?.Invoke(this, new InstanceMonitoringUpdatedEventArgs(instanceId, monitoring));
        }

        private void OnOperationCompleted(EC2Operation operation)
        {
            OperationCompleted?.Invoke(this, new EC2OperationCompletedEventArgs(operation));
        }

        private void OnOperationFailed(EC2Operation operation, Exception exception)
        {
            OperationFailed?.Invoke(this, new EC2OperationFailedEventArgs(operation, exception));
        }

        private void OnInstanceTagsUpdated(string instanceId, List<Tag> tags)
        {
            InstanceTagsUpdated?.Invoke(this, new InstanceTagsUpdatedEventArgs(instanceId, tags));
        }

        private void OnSecurityGroupRulesModified(string securityGroupId, List<SecurityGroupRule> rulesAdded, List<SecurityGroupRule> rulesRemoved)
        {
            SecurityGroupRulesModified?.Invoke(this, new SecurityGroupRulesModifiedEventArgs(securityGroupId, rulesAdded, rulesRemoved));
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
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    _httpClient?.Dispose();

                    if (_instanceMonitorTimer != null)
                    {
                        _instanceMonitorTimer.Stop();
                        _instanceMonitorTimer.Dispose();
                        _instanceMonitorTimer = null;
                    }

                    SecurityGroupManager?.Dispose();
                    VolumeManager?.Dispose();
                    NetworkInterfaceManager?.Dispose();

                    // Clear collections;
                    _monitoredInstances.Clear();
                    _activeOperations.Clear();

                    // Unsubscribe from events;
                    InstanceStateChanged = null;
                    InstanceLaunched = null;
                    InstanceTerminated = null;
                    InstanceStopped = null;
                    InstanceStarted = null;
                    InstanceRebooted = null;
                    InstanceMonitoringUpdated = null;
                    OperationCompleted = null;
                    OperationFailed = null;
                    InstanceTagsUpdated = null;
                    SecurityGroupRulesModified = null;
                }

                _disposed = true;
                _logger.LogInformation("EC2Manager disposed");
            }
        }

        ~EC2Manager()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes and Enums;

        /// <summary>
        /// EC2 manager configuration;
        /// </summary>
        public class EC2ManagerConfiguration;
        {
            public int MaxRetries { get; set; } = DEFAULT_MAX_RETRIES;
            public int TimeoutSeconds { get; set; } = DEFAULT_TIMEOUT_SECONDS;
            public bool EnableDetailedMonitoring { get; set; } = true;
            public bool AutoMonitorNewInstances { get; set; } = true;
            public string DefaultInstanceType { get; set; } = "t2.micro";
            public string DefaultSecurityGroupName { get; set; } = "default";
            public string DefaultKeyPairName { get; set; }
            public bool EnableCostOptimization { get; set; } = true;
            public bool EnableAutoScalingIntegration { get; set; } = false;
            public int InstancePollIntervalMs { get; set; } = INSTANCE_POLL_INTERVAL_MS;
            public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// EC2 service statistics;
        /// </summary>
        public class EC2ServiceStatistics : ICloneable;
        {
            // Instance operations;
            public int InstancesLaunched { get; set; }
            public int TotalInstancesLaunched { get; set; }
            public int InstancesTerminated { get; set; }
            public int TotalInstancesTerminated { get; set; }
            public int InstancesStarted { get; set; }
            public int TotalInstancesStarted { get; set; }
            public int InstancesStopped { get; set; }
            public int TotalInstancesStopped { get; set; }
            public int InstancesRebooted { get; set; }
            public int TotalInstancesRebooted { get; set; }

            // API calls;
            public int DescribeInstancesCalls { get; set; }
            public int DescribeInstanceStatusCalls { get; set; }
            public int RunInstancesCalls { get; set; }
            public int TerminateInstancesCalls { get; set; }
            public int StartInstancesCalls { get; set; }
            public int StopInstancesCalls { get; set; }
            public int RebootInstancesCalls { get; set; }

            // Monitoring;
            public int DetailedMonitoringEnabled { get; set; }
            public int DetailedMonitoringDisabled { get; set; }
            public int InstanceStateChanges { get; set; }

            // Tags;
            public int TagsCreated { get; set; }
            public int TagsDeleted { get; set; }

            // Errors;
            public int ApiErrors { get; set; }
            public int TimeoutErrors { get; set; }
            public int ValidationErrors { get; set; }

            // Timing;
            public DateTime FirstOperationTime { get; set; } = DateTime.UtcNow;
            public DateTime LastOperationTime { get; set; } = DateTime.UtcNow;
            public DateTime LastDescribeInstancesCall { get; set; }
            public DateTime LastInstanceStateChange { get; set; }

            public object Clone()
            {
                return new EC2ServiceStatistics;
                {
                    InstancesLaunched = this.InstancesLaunched,
                    TotalInstancesLaunched = this.TotalInstancesLaunched,
                    InstancesTerminated = this.InstancesTerminated,
                    TotalInstancesTerminated = this.TotalInstancesTerminated,
                    InstancesStarted = this.InstancesStarted,
                    TotalInstancesStarted = this.TotalInstancesStarted,
                    InstancesStopped = this.InstancesStopped,
                    TotalInstancesStopped = this.TotalInstancesStopped,
                    InstancesRebooted = this.InstancesRebooted,
                    TotalInstancesRebooted = this.TotalInstancesRebooted,
                    DescribeInstancesCalls = this.DescribeInstancesCalls,
                    DescribeInstanceStatusCalls = this.DescribeInstanceStatusCalls,
                    RunInstancesCalls = this.RunInstancesCalls,
                    TerminateInstancesCalls = this.TerminateInstancesCalls,
                    StartInstancesCalls = this.StartInstancesCalls,
                    StopInstancesCalls = this.StopInstancesCalls,
                    RebootInstancesCalls = this.RebootInstancesCalls,
                    DetailedMonitoringEnabled = this.DetailedMonitoringEnabled,
                    DetailedMonitoringDisabled = this.DetailedMonitoringDisabled,
                    InstanceStateChanges = this.InstanceStateChanges,
                    TagsCreated = this.TagsCreated,
                    TagsDeleted = this.TagsDeleted,
                    ApiErrors = this.ApiErrors,
                    TimeoutErrors = this.TimeoutErrors,
                    ValidationErrors = this.ValidationErrors,
                    FirstOperationTime = this.FirstOperationTime,
                    LastOperationTime = this.LastOperationTime,
                    LastDescribeInstancesCall = this.LastDescribeInstancesCall,
                    LastInstanceStateChange = this.LastInstanceStateChange;
                };
            }
        }

        /// <summary>
        /// Instance type cache;
        /// </summary>
        public class InstanceTypeCache;
        {
            public List<InstanceTypeInfo> InstanceTypes { get; set; } = new List<InstanceTypeInfo>();
            public DateTime LastUpdated { get; set; }
            public int Count => InstanceTypes?.Count ?? 0;
        }

        /// <summary>
        /// EC2 validation result;
        /// </summary>
        public class EC2ValidationResult;
        {
            public bool IsValid { get; set; }
            public bool IsConnected { get; set; }
            public bool ConnectivityTestPassed { get; set; }
            public bool HasRequiredPermissions { get; set; }
            public bool SecurityGroupManagerValid { get; set; }
            public bool VolumeManagerValid { get; set; }
            public bool NetworkInterfaceManagerValid { get; set; }
            public bool InstanceTypeCacheValid { get; set; }
            public List<string> MissingPermissions { get; set; } = new List<string>();
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
        }

        /// <summary>
        /// Launch request validation result;
        /// </summary>
        public class LaunchRequestValidationResult;
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
        }

        /// <summary>
        /// Instance state names;
        /// </summary>
        public enum InstanceStateName;
        {
            Pending,
            Running,
            ShuttingDown,
            Terminated,
            Stopping,
            Stopped,
            Unknown;
        }

        /// <summary>
        /// Monitoring states;
        /// </summary>
        public enum MonitoringState;
        {
            Disabled,
            Disabling,
            Enabled,
            Pending;
        }

        /// <summary>
        /// Service status;
        /// </summary>
        public enum ServiceStatus;
        {
            Normal,
            Degraded,
            Disrupted,
            Down;
        }

        /// <summary>
        /// EC2 operation types;
        /// </summary>
        public enum EC2OperationType;
        {
            LaunchInstance,
            TerminateInstances,
            StartInstances,
            StopInstances,
            RebootInstances,
            DescribeInstances,
            CreateTags,
            DeleteTags,
            EnableMonitoring,
            DisableMonitoring,
            CreateSecurityGroup,
            DeleteSecurityGroup,
            AuthorizeSecurityGroup,
            RevokeSecurityGroup,
            CreateVolume,
            DeleteVolume,
            AttachVolume,
            DetachVolume,
            CreateNetworkInterface,
            DeleteNetworkInterface,
            AttachNetworkInterface,
            DetachNetworkInterface;
        }

        /// <summary>
        /// EC2 operation status;
        /// </summary>
        public enum EC2OperationStatus;
        {
            Pending,
            InProgress,
            Completed,
            Failed,
            Cancelled;
        }

        #endregion;

        #region Event Args Classes;

        public class InstanceStateChangedEventArgs : EventArgs;
        {
            public string InstanceId { get; }
            public InstanceState PreviousState { get; }
            public InstanceState NewState { get; }

            public InstanceStateChangedEventArgs(string instanceId, InstanceState previousState, InstanceState newState)
            {
                InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
                PreviousState = previousState ?? throw new ArgumentNullException(nameof(previousState));
                NewState = newState ?? throw new ArgumentNullException(nameof(newState));
            }
        }

        public class InstanceLaunchedEventArgs : EventArgs;
        {
            public EC2Instance Instance { get; }
            public LaunchInstanceRequest Request { get; }

            public InstanceLaunchedEventArgs(EC2Instance instance, LaunchInstanceRequest request)
            {
                Instance = instance ?? throw new ArgumentNullException(nameof(instance));
                Request = request ?? throw new ArgumentNullException(nameof(request));
            }
        }

        public class InstanceTerminatedEventArgs : EventArgs;
        {
            public string InstanceId { get; }
            public bool Force { get; }

            public InstanceTerminatedEventArgs(string instanceId, bool force)
            {
                InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
                Force = force;
            }
        }

        public class InstanceStoppedEventArgs : EventArgs;
        {
            public string InstanceId { get; }
            public bool Force { get; }

            public InstanceStoppedEventArgs(string instanceId, bool force)
            {
                InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
                Force = force;
            }
        }

        public class InstanceStartedEventArgs : EventArgs;
        {
            public string InstanceId { get; }

            public InstanceStartedEventArgs(string instanceId)
            {
                InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
            }
        }

        public class InstanceRebootedEventArgs : EventArgs;
        {
            public string InstanceId { get; }

            public InstanceRebootedEventArgs(string instanceId)
            {
                InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
            }
        }

        public class InstanceMonitoringUpdatedEventArgs : EventArgs;
        {
            public string InstanceId { get; }
            public InstanceMonitoring Monitoring { get; }

            public InstanceMonitoringUpdatedEventArgs(string instanceId, InstanceMonitoring monitoring)
            {
                InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
                Monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
            }
        }

        public class EC2OperationCompletedEventArgs : EventArgs;
        {
            public EC2Operation Operation { get; }

            public EC2OperationCompletedEventArgs(EC2Operation operation)
            {
                Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            }
        }

        public class EC2OperationFailedEventArgs : EventArgs;
        {
            public EC2Operation Operation { get; }
            public Exception Exception { get; }

            public EC2OperationFailedEventArgs(EC2Operation operation, Exception exception)
            {
                Operation = operation ?? throw new ArgumentNullException(nameof(operation));
                Exception = exception ?? throw new ArgumentNullException(nameof(exception));
            }
        }

        public class InstanceTagsUpdatedEventArgs : EventArgs;
        {
            public string InstanceId { get; }
            public List<Tag> Tags { get; }

            public InstanceTagsUpdatedEventArgs(string instanceId, List<Tag> tags)
            {
                InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
                Tags = tags ?? throw new ArgumentNullException(nameof(tags));
            }
        }

        public class SecurityGroupRulesModifiedEventArgs : EventArgs;
        {
            public string SecurityGroupId { get; }
            public List<SecurityGroupRule> RulesAdded { get; }
            public List<SecurityGroupRule> RulesRemoved { get; }

            public SecurityGroupRulesModifiedEventArgs(string securityGroupId, List<SecurityGroupRule> rulesAdded, List<SecurityGroupRule> rulesRemoved)
            {
                SecurityGroupId = securityGroupId ?? throw new ArgumentNullException(nameof(securityGroupId));
                RulesAdded = rulesAdded ?? throw new ArgumentNullException(nameof(rulesAdded));
                RulesRemoved = rulesRemoved ?? throw new ArgumentNullException(nameof(rulesRemoved));
            }
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// EC2 instance;
    /// </summary>
    public class EC2Instance;
    {
        public string InstanceId { get; set; }
        public string InstanceType { get; set; }
        public InstanceState State { get; set; }
        public string ImageId { get; set; }
        public DateTime LaunchTime { get; set; }
        public string KeyName { get; set; }
        public List<InstanceSecurityGroup> SecurityGroups { get; set; } = new List<InstanceSecurityGroup>();
        public List<Tag> Tags { get; set; } = new List<Tag>();
        public string ReservationId { get; set; }
        public string AvailabilityZone { get; set; }
        public InstanceMonitoring Monitoring { get; set; }
        public string PrivateIpAddress { get; set; }
        public string PublicIpAddress { get; set; }
        public string PrivateDnsName { get; set; }
        public string PublicDnsName { get; set; }
        public string VpcId { get; set; }
        public string SubnetId { get; set; }
        public List<InstanceBlockDeviceMapping> BlockDeviceMappings { get; set; } = new List<InstanceBlockDeviceMapping>();
        public IamInstanceProfile IamInstanceProfile { get; set; }
        public string Architecture { get; set; }
        public string RootDeviceType { get; set; }
        public string RootDeviceName { get; set; }
        public List<InstanceNetworkInterface> NetworkInterfaces { get; set; } = new List<InstanceNetworkInterface>();
        public string SourceDestCheck { get; set; }
        public string Hypervisor { get; set; }
        public List<InstanceProductCode> ProductCodes { get; set; } = new List<InstanceProductCode>();
    }

    /// <summary>
    /// Instance state;
    /// </summary>
    public class InstanceState;
    {
        public EC2Manager.InstanceStateName Name { get; set; }
        public int Code { get; set; }
    }

    /// <summary>
    /// Instance security group;
    /// </summary>
    public class InstanceSecurityGroup;
    {
        public string GroupId { get; set; }
        public string GroupName { get; set; }
    }

    /// <summary>
    /// Instance monitoring;
    /// </summary>
    public class InstanceMonitoring;
    {
        public string InstanceId { get; set; }
        public EC2Manager.MonitoringState State { get; set; }
    }

    /// <summary>
    /// Instance block device mapping;
    /// </summary>
    public class InstanceBlockDeviceMapping;
    {
        public string DeviceName { get; set; }
        public EbsInstanceBlockDevice Ebs { get; set; }
    }

    /// <summary>
    /// EBS instance block device;
    /// </summary>
    public class EbsInstanceBlockDevice;
    {
        public DateTime? AttachTime { get; set; }
        public bool? DeleteOnTermination { get; set; }
        public string Status { get; set; }
        public string VolumeId { get; set; }
    }

    /// <summary>
    /// IAM instance profile;
    /// </summary>
    public class IamInstanceProfile;
    {
        public string Arn { get; set; }
        public string Id { get; set; }
    }

    /// <summary>
    /// Instance network interface;
    /// </summary>
    public class InstanceNetworkInterface;
    {
        public string NetworkInterfaceId { get; set; }
        public string SubnetId { get; set; }
        public string VpcId { get; set; }
        public string Description { get; set; }
        public string OwnerId { get; set; }
        public string Status { get; set; }
        public string MacAddress { get; set; }
        public string PrivateIpAddress { get; set; }
        public string PrivateDnsName { get; set; }
        public bool? SourceDestCheck { get; set; }
        public List<InstanceNetworkInterfaceAssociation> Association { get; set; } = new List<InstanceNetworkInterfaceAssociation>();
        public List<InstanceNetworkInterfaceAttachment> Attachment { get; set; } = new List<InstanceNetworkInterfaceAttachment>();
        public List<InstanceNetworkInterfaceSecurityGroup> Groups { get; set; } = new List<InstanceNetworkInterfaceSecurityGroup>();
        public List<InstanceNetworkInterfacePrivateIpAddress> PrivateIpAddresses { get; set; } = new List<InstanceNetworkInterfacePrivateIpAddress>();
        public List<InstanceNetworkInterfaceIpv6Address> Ipv6Addresses { get; set; } = new List<InstanceNetworkInterfaceIpv6Address>();
    }

    /// <summary>
    /// Instance network interface association;
    /// </summary>
    public class InstanceNetworkInterfaceAssociation;
    {
        public string IpOwnerId { get; set; }
        public string PublicDnsName { get; set; }
        public string PublicIp { get; set; }
    }

    /// <summary>
    /// Instance network interface attachment;
    /// </summary>
    public class InstanceNetworkInterfaceAttachment;
    {
        public DateTime? AttachTime { get; set; }
        public string AttachmentId { get; set; }
        public bool? DeleteOnTermination { get; set; }
        public int? DeviceIndex { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Instance network interface security group;
    /// </summary>
    public class InstanceNetworkInterfaceSecurityGroup;
    {
        public string GroupId { get; set; }
        public string GroupName { get; set; }
    }

    /// <summary>
    /// Instance network interface private IP address;
    /// </summary>
    public class InstanceNetworkInterfacePrivateIpAddress;
    {
        public InstanceNetworkInterfaceAssociation Association { get; set; }
        public bool? Primary { get; set; }
        public string PrivateDnsName { get; set; }
        public string PrivateIpAddress { get; set; }
    }

    /// <summary>
    /// Instance network interface IPv6 address;
    /// </summary>
    public class InstanceNetworkInterfaceIpv6Address;
    {
        public string Ipv6Address { get; set; }
    }

    /// <summary>
    /// Instance product code;
    /// </summary>
    public class InstanceProductCode;
    {
        public string ProductCodeId { get; set; }
        public string ProductCodeType { get; set; }
    }

    /// <summary>
    /// Tag;
    /// </summary>
    public class Tag;
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// Filter;
    /// </summary>
    public class Filter;
    {
        public string Name { get; set; }
        public List<string> Values { get; set; } = new List<string>();
    }

    /// <summary>
    /// Reservation;
    /// </summary>
    public class Reservation;
    {
        public string ReservationId { get; set; }
        public List<EC2Instance> Instances { get; set; } = new List<EC2Instance>();
    }

    /// <summary>
    /// Launch instance request;
    /// </summary>
    public class LaunchInstanceRequest;
    {
        public string ImageId { get; set; }
        public string InstanceType { get; set; }
        public string InstanceName { get; set; }
        public string KeyName { get; set; }
        public List<string> SecurityGroupIds { get; set; }
        public string SecurityGroup { get; set; }
        public string SubnetId { get; set; }
        public string UserData { get; set; }
        public IamInstanceProfile IamInstanceProfile { get; set; }
        public List<BlockDeviceMapping> BlockDeviceMappings { get; set; }
        public List<InstanceNetworkInterfaceSpecification> NetworkInterfaces { get; set; }
        public InstancePlacement Placement { get; set; }
        public bool WaitForRunning { get; set; } = true;
        public int? WaitTimeoutSeconds { get; set; } = 300;
        public List<Tag> Tags { get; set; }
    }

    /// <summary>
    /// Block device mapping;
    /// </summary>
    public class BlockDeviceMapping;
    {
        public string DeviceName { get; set; }
        public EbsBlockDevice Ebs { get; set; }
    }

    /// <summary>
    /// EBS block device;
    /// </summary>
    public class EbsBlockDevice;
    {
        public bool? DeleteOnTermination { get; set; }
        public int? VolumeSize { get; set; }
        public string VolumeType { get; set; }
        public int? Iops { get; set; }
        public bool? Encrypted { get; set; }
        public string SnapshotId { get; set; }
    }

    /// <summary>
    /// Instance network interface specification;
    /// </summary>
    public class InstanceNetworkInterfaceSpecification;
    {
        public string NetworkInterfaceId { get; set; }
        public string SubnetId { get; set; }
        public List<string> Groups { get; set; }
        public bool? DeleteOnTermination { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Instance placement;
    /// </summary>
    public class InstancePlacement;
    {
        public string AvailabilityZone { get; set; }
        public string GroupName { get; set; }
        public string Tenancy { get; set; }
    }

    /// <summary>
    /// Launch instance result;
    /// </summary>
    public class LaunchInstanceResult;
    {
        public EC2Instance Instance { get; set; }
        public string OperationId { get; set; }
        public DateTime LaunchTime { get; set; }
        public LaunchInstanceRequest Request { get; set; }
    }

    /// <summary>
    /// Describe instances request;
    /// </summary>
    public class DescribeInstancesRequest;
    {
        public List<string> InstanceIds { get; set; }
        public List<Filter> Filters { get; set; }
        public int? MaxResults { get; set; }
        public string NextToken { get; set; }
    }

    /// <summary>
    /// Describe instances result;
    /// </summary>
    public class DescribeInstancesResult;
    {
        public List<Reservation> Reservations { get; set; } = new List<Reservation>();
        public int TotalInstances { get; set; }
        public string NextToken { get; set; }
        public DescribeInstancesRequest Request { get; set; }
    }

    /// <summary>
    /// Terminate instances result;
    /// </summary>
    public class TerminateInstancesResult;
    {
        public List<InstanceTermination> InstanceTerminations { get; set; } = new List<InstanceTermination>();
        public string OperationId { get; set; }
        public DateTime TerminationTime { get; set; }
        public bool ForceTermination { get; set; }
    }

    /// <summary>
    /// Instance termination;
    /// </summary>
    public class InstanceTermination;
    {
        public string InstanceId { get; set; }
        public EC2Manager.InstanceStateName PreviousState { get; set; }
        public EC2Manager.InstanceStateName CurrentState { get; set; }
        public DateTime TerminationTime { get; set; }
    }

    /// <summary>
    /// Start instances result;
    /// </summary>
    public class StartInstancesResult;
    {
        public List<InstanceStart> InstanceStarts { get; set; } = new List<InstanceStart>();
        public string OperationId { get; set; }
        public DateTime StartTime { get; set; }
    }

    /// <summary>
    /// Instance start;
    /// </summary>
    public class InstanceStart;
    {
        public string InstanceId { get; set; }
        public EC2Manager.InstanceStateName PreviousState { get; set; }
        public EC2Manager.InstanceStateName CurrentState { get; set; }
        public DateTime StartTime { get; set; }
    }

    /// <summary>
    /// Stop instances result;
    /// </summary>
    public class StopInstancesResult;
    {
        public List<InstanceStop> InstanceStops { get; set; } = new List<InstanceStop>();
        public string OperationId { get; set; }
        public DateTime StopTime { get; set; }
        public bool ForceStop { get; set; }
    }

    /// <summary>
    /// Instance stop;
    /// </summary>
    public class InstanceStop;
    {
        public string InstanceId { get; set; }
        public EC2Manager.InstanceStateName PreviousState { get; set; }
        public EC2Manager.InstanceStateName CurrentState { get; set; }
        public DateTime StopTime { get; set; }
    }

    /// <summary>
    /// Reboot instances result;
    /// </summary>
    public class RebootInstancesResult;
    {
        public List<InstanceReboot> InstanceReboots { get; set; } = new List<InstanceReboot>();
        public string OperationId { get; set; }
        public DateTime RebootTime { get; set; }
    }

    /// <summary>
    /// Instance reboot;
    /// </summary>
    public class InstanceReboot;
    {
        public string InstanceId { get; set; }
        public EC2Manager.InstanceStateName PreviousState { get; set; }
        public DateTime RebootTime { get; set; }
    }

    /// <summary>
    /// Monitoring enablement result;
    /// </summary>
    public class MonitoringEnablementResult;
    {
        public string InstanceId { get; set; }
        public EC2Manager.MonitoringState MonitoringState { get; set; }
        public string OperationId { get; set; }
        public DateTime EnablementTime { get; set; }
    }

    /// <summary>
    /// Monitoring disablement result;
    /// </summary>
    public class MonitoringDisablementResult;
    {
        public string InstanceId { get; set; }
        public EC2Manager.MonitoringState MonitoringState { get; set; }
        public string OperationId { get; set; }
        public DateTime DisablementTime { get; set; }
    }

    /// <summary>
    /// CloudWatch metrics;
    /// </summary>
    public class CloudWatchMetrics;
    {
        public string InstanceId { get; set; }
        public string MetricName { get; set; }
        public int Period { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<MetricDatapoint> Datapoints { get; set; } = new List<MetricDatapoint>();
    }

    /// <summary>
    /// Metric datapoint;
    /// </summary>
    public class MetricDatapoint;
    {
        public DateTime Timestamp { get; set; }
        public double Average { get; set; }
        public double Maximum { get; set; }
        public double Minimum { get; set; }
        public double Sum { get; set; }
        public int SampleCount { get; set; }
        public string Unit { get; set; }
    }

    /// <summary>
    /// Create tags result;
    /// </summary>
    public class CreateTagsResult;
    {
        public List<string> ResourceIds { get; set; }
        public List<Tag> Tags { get; set; }
        public string OperationId { get; set; }
        public DateTime CreationTime { get; set; }
    }

    /// <summary>
    /// Delete tags result;
    /// </summary>
    public class DeleteTagsResult;
    {
        public List<string> ResourceIds { get; set; }
        public List<Tag> Tags { get; set; }
        public string OperationId { get; set; }
        public DateTime DeletionTime { get; set; }
    }

    /// <summary>
    /// Instance type info;
    /// </summary>
    public class InstanceTypeInfo;
    {
        public string InstanceTypeName { get; set; }
        public int VCpus { get; set; }
        public double MemoryGb { get; set; }
        public string Performance { get; set; }
        public string NetworkPerformance { get; set; }
        public string StorageType { get; set; }
        public List<string> SupportedArchitectures { get; set; } = new List<string>();
        public List<string> SupportedVirtualizationTypes { get; set; } = new List<string>();
        public List<string> Features { get; set; } = new List<string>();
    }

    /// <summary>
    /// Instance requirements;
    /// </summary>
    public class InstanceRequirements;
    {
        public int? MinVCpus { get; set; }
        public int? MaxVCpus { get; set; }
        public double? MinMemoryGb { get; set; }
        public double? MaxMemoryGb { get; set; }
        public string InstanceFamily { get; set; }
        public string Performance { get; set; }
        public string PreferredInstanceType { get; set; }
        public bool EbsOptimized { get; set; }
        public bool BurstablePerformance { get; set; }
        public decimal? MaxPricePerHour { get; set; }
    }

    /// <summary>
    /// Instance type recommendation;
    /// </summary>
    public class InstanceTypeRecommendation;
    {
        public InstanceTypeInfo InstanceType { get; set; }
        public double SuitabilityScore { get; set; }
        public decimal EstimatedCostPerHour { get; set; }
        public List<string> Reasons { get; set; } = new List<string>();
    }

    /// <summary>
    /// EC2 service limits;
    /// </summary>
    public class EC2ServiceLimits;
    {
        public string Region { get; set; }
        public string AccountId { get; set; }
        public InstanceLimits InstanceLimits { get; set; }
        public VolumeLimits VolumeLimits { get; set; }
        public NetworkLimits NetworkLimits { get; set; }
    }

    /// <summary>
    /// Instance limits;
    /// </summary>
    public class InstanceLimits;
    {
        public int MaxRunningOnDemandInstances { get; set; }
        public int CurrentRunningOnDemandInstances { get; set; }
        public int MaxSpotInstances { get; set; }
        public int CurrentSpotInstances { get; set; }
    }

    /// <summary>
    /// Volume limits;
    /// </summary>
    public class VolumeLimits;
    {
        public int MaxVolumes { get; set; }
        public int CurrentVolumes { get; set; }
        public int MaxVolumeSizeGb { get; set; }
        public int MaxSnapshots { get; set; }
    }

    /// <summary>
    /// Network limits;
    /// </summary>
    public class NetworkLimits;
    {
        public int MaxVpcs { get; set; }
        public int MaxSecurityGroups { get; set; }
        public int MaxElasticIps { get; set; }
    }

    /// <summary>
    /// EC2 service health;
    /// </summary>
    public class EC2ServiceHealth;
    {
        public string Region { get; set; }
        public EC2Manager.ServiceStatus Status { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<ServiceEvent> Events { get; set; } = new List<ServiceEvent>();
        public ServiceMetrics Metrics { get; set; }
    }

    /// <summary>
    /// Service event;
    /// </summary>
    public class ServiceEvent;
    {
        public string EventId { get; set; }
        public string Description { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Severity { get; set; }
    }

    /// <summary>
    /// Service metrics;
    /// </summary>
    public class ServiceMetrics;
    {
        public double ApiSuccessRate { get; set; }
        public double AverageLatencyMs { get; set; }
        public double ErrorRate { get; set; }
    }

    /// <summary>
    /// EC2 operation;
    /// </summary>
    public class EC2Operation;
    {
        public string Id { get; set; }
        public EC2Manager.EC2OperationType Type { get; set; }
        public EC2Manager.EC2OperationStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Error { get; set; }
        public object Result { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    #endregion;

    #region Sub-Managers;

    /// <summary>
    /// Security group manager;
    /// </summary>
    public class SecurityGroupManager : IDisposable
    {
        private readonly AWSClient _awsClient;
        private readonly string _region;
        private readonly ILogger _logger;

        public event EventHandler<EC2Manager.SecurityGroupRulesModifiedEventArgs> SecurityGroupRulesModified;

        public SecurityGroupManager(AWSClient awsClient, string region, ILogger logger)
        {
            _awsClient = awsClient;
            _region = region;
            _logger = logger;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task<bool> ValidateAsync() => Task.FromResult(true);

        public Task<SecurityGroup> GetSecurityGroupAsync(string securityGroupId) => Task.FromResult<SecurityGroup>(null);

        public void Dispose() { }
    }

    /// <summary>
    /// Volume manager;
    /// </summary>
    public class VolumeManager : IDisposable
    {
        private readonly AWSClient _awsClient;
        private readonly string _region;
        private readonly ILogger _logger;

        public VolumeManager(AWSClient awsClient, string region, ILogger logger)
        {
            _awsClient = awsClient;
            _region = region;
            _logger = logger;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task<bool> ValidateAsync() => Task.FromResult(true);

        public void Dispose() { }
    }

    /// <summary>
    /// Network interface manager;
    /// </summary>
    public class NetworkInterfaceManager : IDisposable
    {
        private readonly AWSClient _awsClient;
        private readonly string _region;
        private readonly ILogger _logger;

        public NetworkInterfaceManager(AWSClient awsClient, string region, ILogger logger)
        {
            _awsClient = awsClient;
            _region = region;
            _logger = logger;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task<bool> ValidateAsync() => Task.FromResult(true);

        public void Dispose() { }
    }

    /// <summary>
    /// Security group;
    /// </summary>
    public class SecurityGroup;
    {
        public string GroupId { get; set; }
        public string GroupName { get; set; }
        public string Description { get; set; }
        public string VpcId { get; set; }
        public List<SecurityGroupRule> IngressRules { get; set; } = new List<SecurityGroupRule>();
        public List<SecurityGroupRule> EgressRules { get; set; } = new List<SecurityGroupRule>();
        public List<Tag> Tags { get; set; } = new List<Tag>();
    }

    /// <summary>
    /// Security group rule;
    /// </summary>
    public class SecurityGroupRule;
    {
        public string RuleId { get; set; }
        public string Protocol { get; set; }
        public int FromPort { get; set; }
        public int ToPort { get; set; }
        public string CidrIp { get; set; }
        public string SourceSecurityGroupId { get; set; }
        public string Description { get; set; }
    }

    #endregion;

    #region Custom Exceptions;

    [Serializable]
    public class EC2OperationException : Exception
    {
        public EC2OperationException() { }
        public EC2OperationException(string message) : base(message) { }
        public EC2OperationException(string message, Exception inner) : base(message, inner) { }
        protected EC2OperationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class EC2InitializationException : EC2OperationException;
    {
        public EC2InitializationException() { }
        public EC2InitializationException(string message) : base(message) { }
        public EC2InitializationException(string message, Exception inner) : base(message, inner) { }
        protected EC2InitializationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class EC2NotInitializedException : EC2OperationException;
    {
        public EC2NotInitializedException() { }
        public EC2NotInitializedException(string message) : base(message) { }
        public EC2NotInitializedException(string message, Exception inner) : base(message, inner) { }
        protected EC2NotInitializedException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class EC2ValidationException : EC2OperationException;
    {
        public EC2ValidationException() { }
        public EC2ValidationException(string message) : base(message) { }
        public EC2ValidationException(string message, Exception inner) : base(message, inner) { }
        protected EC2ValidationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class EC2ApiException : EC2OperationException;
    {
        public EC2ApiException() { }
        public EC2ApiException(string message) : base(message) { }
        public EC2ApiException(string message, Exception inner) : base(message, inner) { }
        protected EC2ApiException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class EC2ParseException : EC2OperationException;
    {
        public EC2ParseException() { }
        public EC2ParseException(string message) : base(message) { }
        public EC2ParseException(string message, Exception inner) : base(message, inner) { }
        protected EC2ParseException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class EC2TimeoutException : EC2OperationException;
    {
        public EC2TimeoutException() { }
        public EC2TimeoutException(string message) : base(message) { }
        public EC2TimeoutException(string message, Exception inner) : base(message, inner) { }
        protected EC2TimeoutException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    #endregion;
}
