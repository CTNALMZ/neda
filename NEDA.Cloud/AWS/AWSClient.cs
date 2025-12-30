using Amazon;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.RDS;
using Amazon.RDS.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Amazon.SNS;
using Amazon.SNS.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.ClientSDK;
using NEDA.API.DTOs;
using NEDA.Brain.KnowledgeBase.CreativePatterns;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Common.Constants;
using NEDA.Common.Utilities;
using NEDA.Services.EventBus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Cloud.AWS;
{
    /// <summary>
    /// AWS service types supported by the client;
    /// </summary>
    public enum AWSServiceType;
    {
        S3,
        EC2,
        Lambda,
        RDS,
        CloudWatch,
        SQS,
        SNS,
        DynamoDB,
        CloudFront,
        SES,
        All;
    }

    /// <summary>
    /// AWS region configuration;
    /// </summary>
    public class AWSRegion;
    {
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public RegionEndpoint RegionEndpoint { get; set; }

        public AWSRegion(string name, RegionEndpoint regionEndpoint)
        {
            Name = name;
            RegionEndpoint = regionEndpoint;
            Endpoint = regionEndpoint.SystemName;
        }
    }

    /// <summary>
    /// AWS credentials configuration;
    /// </summary>
    public class AWSCredentialsConfig;
    {
        public string AccessKeyId { get; set; }
        public string SecretAccessKey { get; set; }
        public string SessionToken { get; set; }
        public string ProfileName { get; set; }
        public bool UseInstanceProfile { get; set; }

        public AWSCredentialsConfig() { }

        public AWSCredentialsConfig(string accessKeyId, string secretAccessKey)
        {
            AccessKeyId = accessKeyId;
            SecretAccessKey = secretAccessKey;
        }

        public AWSCredentials GetCredentials()
        {
            if (UseInstanceProfile)
                return FallbackCredentialsFactory.GetCredentials();

            if (!string.IsNullOrEmpty(SessionToken))
                return new SessionAWSCredentials(AccessKeyId, SecretAccessKey, SessionToken);

            if (!string.IsNullOrEmpty(ProfileName))
                return new StoredProfileAWSCredentials(ProfileName);

            return new BasicAWSCredentials(AccessKeyId, SecretAccessKey);
        }
    }

    /// <summary>
    /// AWS client configuration;
    /// </summary>
    public class AWSClientConfig;
    {
        public AWSCredentialsConfig Credentials { get; set; }
        public AWSRegion Region { get; set; }
        public string DefaultBucket { get; set; }
        public string DefaultQueueUrl { get; set; }
        public string DefaultTopicArn { get; set; }
        public string DefaultTableName { get; set; }
        public string DefaultFunctionName { get; set; }
        public string DefaultInstanceId { get; set; }
        public string DefaultDBInstanceId { get; set; }
        public string DefaultDistributionId { get; set; }

        public int MaxRetryAttempts { get; set; } = 3;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool UseHttpClientFactory { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
        public bool EnableMetrics { get; set; } = true;
        public bool EnableCaching { get; set; } = true;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> ServiceConfigurations { get; set; } = new Dictionary<string, object>();

        public AWSClientConfig()
        {
            Credentials = new AWSCredentialsConfig();
            Region = new AWSRegion("us-east-1", RegionEndpoint.USEast1);
        }
    }

    /// <summary>
    /// AWS operation result;
    /// </summary>
    public class AWSOperationResult<T>
    {
        public bool Success { get; set; }
        public T Data { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }

        public AWSOperationResult()
        {
            Metadata = new Dictionary<string, object>();
            Timestamp = DateTime.UtcNow;
        }

        public static AWSOperationResult<T> SuccessResult(T data, Dictionary<string, object> metadata = null)
        {
            return new AWSOperationResult<T>
            {
                Success = true,
                Data = data,
                Metadata = metadata ?? new Dictionary<string, object>()
            };
        }

        public static AWSOperationResult<T> ErrorResult(string errorCode, string errorMessage, Exception ex = null)
        {
            return new AWSOperationResult<T>
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                Exception = ex;
            };
        }
    }

    /// <summary>
    /// S3 object information;
    /// </summary>
    public class S3ObjectInfo;
    {
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string ETag { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string StorageClass { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
        public string ContentType { get; set; }

        public S3ObjectInfo()
        {
            Metadata = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// EC2 instance information;
    /// </summary>
    public class EC2InstanceInfo;
    {
        public string InstanceId { get; set; }
        public string InstanceType { get; set; }
        public string State { get; set; }
        public string PublicIpAddress { get; set; }
        public string PrivateIpAddress { get; set; }
        public DateTime LaunchTime { get; set; }
        public Dictionary<string, string> Tags { get; set; }
        public List<string> SecurityGroups { get; set; }

        public EC2InstanceInfo()
        {
            Tags = new Dictionary<string, string>();
            SecurityGroups = new List<string>();
        }
    }

    /// <summary>
    /// Lambda function information;
    /// </summary>
    public class LambdaFunctionInfo;
    {
        public string FunctionName { get; set; }
        public string FunctionArn { get; set; }
        public string Runtime { get; set; }
        public string Handler { get; set; }
        public int Timeout { get; set; }
        public int MemorySize { get; set; }
        public DateTime LastModified { get; set; }
        public string Description { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; }

        public LambdaFunctionInfo()
        {
            EnvironmentVariables = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// CloudWatch metric data;
    /// </summary>
    public class CloudWatchMetricData;
    {
        public string Namespace { get; set; }
        public string MetricName { get; set; }
        public List<Dimension> Dimensions { get; set; }
        public List<Datapoint> Datapoints { get; set; }
        public string Unit { get; set; }

        public CloudWatchMetricData()
        {
            Dimensions = new List<Dimension>();
            Datapoints = new List<Datapoint>();
        }
    }

    /// <summary>
    /// AWS operation metrics;
    /// </summary>
    public class AWSOperationMetrics;
    {
        public string Service { get; set; }
        public string Operation { get; set; }
        public int RequestCount { get; set; }
        public int ErrorCount { get; set; }
        public TimeSpan AverageLatency { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public Dictionary<string, int> StatusCodes { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }

        public AWSOperationMetrics()
        {
            StatusCodes = new Dictionary<string, int>();
        }
    }

    /// <summary>
    /// AWS client interface;
    /// </summary>
    public interface IAWSClient : IDisposable
    {
        // Configuration;
        AWSClientConfig Config { get; }
        bool IsInitialized { get; }

        // Initialization;
        Task InitializeAsync(AWSClientConfig config = null);
        Task ReinitializeAsync(AWSClientConfig config);

        // Service clients;
        IAmazonS3 S3Client { get; }
        IAmazonEC2 EC2Client { get; }
        IAmazonLambda LambdaClient { get; }
        IAmazonCloudWatch CloudWatchClient { get; }
        IAmazonRDS RDSClient { get; }
        IAmazonSQS SQSClient { get; }
        IAmazonSNS SNSClient { get; }
        IAmazonDynamoDB DynamoDBClient { get; }
        IAmazonCloudFront CloudFrontClient { get; }
        IAmazonSimpleEmailService SESClient { get; }

        // S3 Operations;
        Task<AWSOperationResult<S3ObjectInfo>> UploadFileAsync(
            string bucketName,
            string key,
            Stream data,
            string contentType = null,
            Dictionary<string, string> metadata = null,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<Stream>> DownloadFileAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<bool>> DeleteFileAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<List<S3ObjectInfo>>> ListObjectsAsync(
            string bucketName,
            string prefix = null,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<string>> GeneratePresignedUrlAsync(
            string bucketName,
            string key,
            TimeSpan expiration,
            CancellationToken cancellationToken = default);

        // EC2 Operations;
        Task<AWSOperationResult<List<EC2InstanceInfo>>> ListInstancesAsync(
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<bool>> StartInstanceAsync(
            string instanceId,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<bool>> StopInstanceAsync(
            string instanceId,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<EC2InstanceInfo>> GetInstanceInfoAsync(
            string instanceId,
            CancellationToken cancellationToken = default);

        // Lambda Operations;
        Task<AWSOperationResult<LambdaFunctionInfo>> GetFunctionInfoAsync(
            string functionName,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<string>> InvokeFunctionAsync(
            string functionName,
            string payload,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<bool>> UpdateFunctionCodeAsync(
            string functionName,
            byte[] zipFile,
            CancellationToken cancellationToken = default);

        // CloudWatch Operations;
        Task<AWSOperationResult<List<CloudWatchMetricData>>> GetMetricsAsync(
            string namespaceName,
            string metricName,
            TimeSpan period,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<bool>> PutMetricAsync(
            string namespaceName,
            string metricName,
            double value,
            List<Dimension> dimensions = null,
            CancellationToken cancellationToken = default);

        // SQS Operations;
        Task<AWSOperationResult<string>> SendMessageAsync(
            string queueUrl,
            string messageBody,
            Dictionary<string, string> messageAttributes = null,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<List<Message>>> ReceiveMessagesAsync(
            string queueUrl,
            int maxMessages = 10,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<bool>> DeleteMessageAsync(
            string queueUrl,
            string receiptHandle,
            CancellationToken cancellationToken = default);

        // SNS Operations;
        Task<AWSOperationResult<string>> PublishMessageAsync(
            string topicArn,
            string message,
            Dictionary<string, string> messageAttributes = null,
            CancellationToken cancellationToken = default);

        // DynamoDB Operations;
        Task<AWSOperationResult<Dictionary<string, AttributeValue>>> GetItemAsync(
            string tableName,
            Dictionary<string, AttributeValue> key,
            CancellationToken cancellationToken = default);

        Task<AWSOperationResult<bool>> PutItemAsync(
            string tableName,
            Dictionary<string, AttributeValue> item,
            CancellationToken cancellationToken = default);

        // Utility Operations;
        Task<AWSOperationResult<bool>> TestConnectionAsync(AWSServiceType serviceType = AWSServiceType.S3);
        Task<AWSOperationResult<AWSOperationMetrics>> GetOperationMetricsAsync(TimeSpan timeWindow);

        // Event Handlers;
        event EventHandler<AWSClientEventArgs> ClientInitialized;
        event EventHandler<AWSOperationEventArgs> OperationCompleted;
        event EventHandler<AWSErrorEventArgs> OperationFailed;
    }

    /// <summary>
    /// Implementation of AWS Client;
    /// </summary>
    public class AWSClient : IAWSClient;
    {
        private readonly ILogger<AWSClient> _logger;
        private readonly IMemoryCache _cache;
        private readonly IEventBus _eventBus;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);

        private AWSClientConfig _config;
        private bool _disposed;

        // AWS Service Clients;
        private IAmazonS3 _s3Client;
        private IAmazonEC2 _ec2Client;
        private IAmazonLambda _lambdaClient;
        private IAmazonCloudWatch _cloudWatchClient;
        private IAmazonRDS _rdsClient;
        private IAmazonSQS _sqsClient;
        private IAmazonSNS _snsClient;
        private IAmazonDynamoDB _dynamoDbClient;
        private IAmazonCloudFront _cloudFrontClient;
        private IAmazonSimpleEmailService _sesClient;

        // Metrics tracking;
        private readonly Dictionary<string, AWSOperationMetrics> _operationMetrics;
        private readonly DateTime _metricsStartTime;

        public AWSClientConfig Config => _config;
        public bool IsInitialized { get; private set; }

        // Service client properties;
        public IAmazonS3 S3Client => _s3Client;
        public IAmazonEC2 EC2Client => _ec2Client;
        public IAmazonLambda LambdaClient => _lambdaClient;
        public IAmazonCloudWatch CloudWatchClient => _cloudWatchClient;
        public IAmazonRDS RDSClient => _rdsClient;
        public IAmazonSQS SQSClient => _sqsClient;
        public IAmazonSNS SNSClient => _snsClient;
        public IAmazonDynamoDB DynamoDBClient => _dynamoDbClient;
        public IAmazonCloudFront CloudFrontClient => _cloudFrontClient;
        public IAmazonSimpleEmailService SESClient => _sesClient;

        // Events;
        public event EventHandler<AWSClientEventArgs> ClientInitialized;
        public event EventHandler<AWSOperationEventArgs> OperationCompleted;
        public event EventHandler<AWSErrorEventArgs> OperationFailed;

        public AWSClient(
            ILogger<AWSClient> logger,
            IMemoryCache cache,
            IEventBus eventBus,
            IHttpClientFactory httpClientFactory = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _operationMetrics = new Dictionary<string, AWSOperationMetrics>();
            _metricsStartTime = DateTime.UtcNow;

            // Create HTTP client;
            _httpClient = httpClientFactory?.CreateClient("AWSClient") ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            _logger.LogInformation("AWSClient initialized (not yet connected to AWS)");
        }

        public async Task InitializeAsync(AWSClientConfig config = null)
        {
            await _initializationLock.WaitAsync();

            try
            {
                if (IsInitialized)
                {
                    _logger.LogWarning("AWSClient is already initialized");
                    return;
                }

                _config = config ?? new AWSClientConfig();

                _logger.LogInformation("Initializing AWSClient for region {Region}", _config.Region.Name);

                // Validate configuration;
                ValidateConfig();

                // Get credentials;
                var credentials = _config.Credentials.GetCredentials();

                // Initialize service clients;
                await InitializeServiceClientsAsync(credentials);

                IsInitialized = true;

                _logger.LogInformation("AWSClient initialized successfully for region {Region}", _config.Region.Name);

                // Raise event;
                OnClientInitialized(new AWSClientEventArgs;
                {
                    Region = _config.Region.Name,
                    ServicesInitialized = GetInitializedServices(),
                    Timestamp = DateTime.UtcNow;
                });

                // Test connection if enabled;
                if (_config.EnableMetrics)
                {
                    await TestConnectionAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AWSClient");
                throw new AWSClientException("Failed to initialize AWSClient", ex);
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        private void ValidateConfig()
        {
            if (_config.Credentials == null)
                throw new ArgumentException("AWS credentials configuration is required");

            if (_config.Region == null)
                throw new ArgumentException("AWS region configuration is required");

            if (_config.MaxRetryAttempts < 0)
                throw new ArgumentException("MaxRetryAttempts must be non-negative");

            if (_config.Timeout <= TimeSpan.Zero)
                throw new ArgumentException("Timeout must be positive");
        }

        private async Task InitializeServiceClientsAsync(AWSCredentials credentials)
        {
            var clientConfig = new Amazon.Extensions.NETCore.Setup.AWSOptions;
            {
                Credentials = credentials,
                Region = _config.Region.RegionEndpoint,
                DefaultClientConfig =
                {
                    MaxErrorRetry = _config.MaxRetryAttempts,
                    Timeout = _config.Timeout;
                }
            };

            // Initialize S3 client;
            _s3Client = new AmazonS3Client(credentials, _config.Region.RegionEndpoint);
            _logger.LogDebug("S3 client initialized");

            // Initialize EC2 client;
            _ec2Client = new AmazonEC2Client(credentials, _config.Region.RegionEndpoint);
            _logger.LogDebug("EC2 client initialized");

            // Initialize Lambda client;
            _lambdaClient = new AmazonLambdaClient(credentials, _config.Region.RegionEndpoint);
            _logger.LogDebug("Lambda client initialized");

            // Initialize CloudWatch client;
            _cloudWatchClient = new AmazonCloudWatchClient(credentials, _config.Region.RegionEndpoint);
            _logger.LogDebug("CloudWatch client initialized");

            // Initialize other clients on-demand or as needed;
            // This reduces startup time and resource usage;

            await Task.CompletedTask;
        }

        private List<string> GetInitializedServices()
        {
            var services = new List<string>();

            if (_s3Client != null) services.Add("S3");
            if (_ec2Client != null) services.Add("EC2");
            if (_lambdaClient != null) services.Add("Lambda");
            if (_cloudWatchClient != null) services.Add("CloudWatch");
            if (_rdsClient != null) services.Add("RDS");
            if (_sqsClient != null) services.Add("SQS");
            if (_snsClient != null) services.Add("SNS");
            if (_dynamoDbClient != null) services.Add("DynamoDB");
            if (_cloudFrontClient != null) services.Add("CloudFront");
            if (_sesClient != null) services.Add("SES");

            return services;
        }

        public async Task ReinitializeAsync(AWSClientConfig config)
        {
            await _initializationLock.WaitAsync();

            try
            {
                DisposeServiceClients();
                IsInitialized = false;

                await InitializeAsync(config);
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        #region S3 Operations;

        public async Task<AWSOperationResult<S3ObjectInfo>> UploadFileAsync(
            string bucketName,
            string key,
            Stream data,
            string contentType = null,
            Dictionary<string, string> metadata = null,
            CancellationToken cancellationToken = default)
        {
            var operationName = "S3.UploadFile";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                bucketName = bucketName ?? _config.DefaultBucket;
                if (string.IsNullOrEmpty(bucketName))
                    throw new ArgumentException("Bucket name is required");

                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Key is required");

                if (data == null || data.Length == 0)
                    throw new ArgumentException("Data cannot be null or empty");

                _logger.LogInformation("Uploading file to S3: {Bucket}/{Key}", bucketName, key);

                var request = new PutObjectRequest;
                {
                    BucketName = bucketName,
                    Key = key,
                    InputStream = data,
                    ContentType = contentType ?? "application/octet-stream",
                    AutoCloseStream = false;
                };

                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        request.Metadata.Add(kvp.Key, kvp.Value);
                    }
                }

                // Add tags if configured;
                if (_config.Tags.Count > 0)
                {
                    request.TagSet = _config.Tags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList();
                }

                var response = await _s3Client.PutObjectAsync(request, cancellationToken);

                var result = new S3ObjectInfo;
                {
                    BucketName = bucketName,
                    Key = key,
                    ETag = response.ETag,
                    Size = data.Length,
                    LastModified = DateTime.UtcNow,
                    ContentType = contentType,
                    Metadata = metadata ?? new Dictionary<string, string>()
                };

                _logger.LogDebug("File uploaded successfully: {Bucket}/{Key}, ETag: {ETag}",
                    bucketName, key, response.ETag);

                // Cache the upload info if caching is enabled;
                if (_config.EnableCaching)
                {
                    var cacheKey = $"s3_object_{bucketName}_{key}";
                    _cache.Set(cacheKey, result, _config.CacheDuration);
                }

                var operationResult = AWSOperationResult<S3ObjectInfo>.SuccessResult(result);
                operationResult.Duration = DateTime.UtcNow - startTime;

                // Record metrics;
                RecordOperationMetrics(operationName, true, operationResult.Duration);

                // Raise event;
                OnOperationCompleted(new AWSOperationEventArgs;
                {
                    Service = "S3",
                    Operation = "UploadFile",
                    Resource = $"{bucketName}/{key}",
                    Duration = operationResult.Duration,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                });

                // Publish event;
                await _eventBus.PublishAsync(new S3ObjectUploadedEvent;
                {
                    BucketName = bucketName,
                    Key = key,
                    Size = data.Length,
                    ETag = response.ETag,
                    Timestamp = DateTime.UtcNow;
                });

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to upload file to S3: {Bucket}/{Key}", bucketName, key);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "S3",
                    Operation = "UploadFile",
                    Resource = $"{bucketName}/{key}",
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<S3ObjectInfo>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to upload file: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<Stream>> DownloadFileAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken = default)
        {
            var operationName = "S3.DownloadFile";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                bucketName = bucketName ?? _config.DefaultBucket;
                if (string.IsNullOrEmpty(bucketName))
                    throw new ArgumentException("Bucket name is required");

                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Key is required");

                // Check cache first;
                if (_config.EnableCaching)
                {
                    var cacheKey = $"s3_download_{bucketName}_{key}";
                    if (_cache.TryGetValue<Stream>(cacheKey, out var cachedStream))
                    {
                        _logger.LogDebug("Returning cached S3 object: {Bucket}/{Key}", bucketName, key);

                        var cachedResult = AWSOperationResult<Stream>.SuccessResult(cachedStream);
                        cachedResult.Duration = DateTime.UtcNow - startTime;
                        cachedResult.Metadata["Cached"] = true;

                        return cachedResult;
                    }
                }

                _logger.LogInformation("Downloading file from S3: {Bucket}/{Key}", bucketName, key);

                var request = new GetObjectRequest;
                {
                    BucketName = bucketName,
                    Key = key;
                };

                var response = await _s3Client.GetObjectAsync(request, cancellationToken);

                // Read the stream into memory for caching;
                var memoryStream = new MemoryStream();
                await response.ResponseStream.CopyToAsync(memoryStream, cancellationToken);
                memoryStream.Position = 0;

                // Cache the result if enabled;
                if (_config.EnableCaching)
                {
                    var cacheKey = $"s3_download_{bucketName}_{key}";
                    _cache.Set(cacheKey, memoryStream, _config.CacheDuration);
                }

                var operationResult = AWSOperationResult<Stream>.SuccessResult(memoryStream);
                operationResult.Duration = DateTime.UtcNow - startTime;
                operationResult.Metadata["ContentType"] = response.Headers.ContentType;
                operationResult.Metadata["ContentLength"] = response.Headers.ContentLength;
                operationResult.Metadata["ETag"] = response.ETag;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                OnOperationCompleted(new AWSOperationEventArgs;
                {
                    Service = "S3",
                    Operation = "DownloadFile",
                    Resource = $"{bucketName}/{key}",
                    Duration = operationResult.Duration,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                });

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to download file from S3: {Bucket}/{Key}", bucketName, key);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "S3",
                    Operation = "DownloadFile",
                    Resource = $"{bucketName}/{key}",
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<Stream>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to download file: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<bool>> DeleteFileAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken = default)
        {
            var operationName = "S3.DeleteFile";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                bucketName = bucketName ?? _config.DefaultBucket;
                if (string.IsNullOrEmpty(bucketName))
                    throw new ArgumentException("Bucket name is required");

                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Key is required");

                _logger.LogInformation("Deleting file from S3: {Bucket}/{Key}", bucketName, key);

                var request = new DeleteObjectRequest;
                {
                    BucketName = bucketName,
                    Key = key;
                };

                await _s3Client.DeleteObjectAsync(request, cancellationToken);

                // Remove from cache if exists;
                if (_config.EnableCaching)
                {
                    var cacheKey = $"s3_object_{bucketName}_{key}";
                    _cache.Remove(cacheKey);

                    var downloadCacheKey = $"s3_download_{bucketName}_{key}";
                    _cache.Remove(downloadCacheKey);
                }

                _logger.LogDebug("File deleted successfully: {Bucket}/{Key}", bucketName, key);

                var operationResult = AWSOperationResult<bool>.SuccessResult(true);
                operationResult.Duration = DateTime.UtcNow - startTime;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                OnOperationCompleted(new AWSOperationEventArgs;
                {
                    Service = "S3",
                    Operation = "DeleteFile",
                    Resource = $"{bucketName}/{key}",
                    Duration = operationResult.Duration,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                });

                // Publish event;
                await _eventBus.PublishAsync(new S3ObjectDeletedEvent;
                {
                    BucketName = bucketName,
                    Key = key,
                    Timestamp = DateTime.UtcNow;
                });

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to delete file from S3: {Bucket}/{Key}", bucketName, key);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "S3",
                    Operation = "DeleteFile",
                    Resource = $"{bucketName}/{key}",
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<bool>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to delete file: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<List<S3ObjectInfo>>> ListObjectsAsync(
            string bucketName,
            string prefix = null,
            CancellationToken cancellationToken = default)
        {
            var operationName = "S3.ListObjects";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                bucketName = bucketName ?? _config.DefaultBucket;
                if (string.IsNullOrEmpty(bucketName))
                    throw new ArgumentException("Bucket name is required");

                _logger.LogDebug("Listing objects in S3 bucket: {Bucket}, Prefix: {Prefix}",
                    bucketName, prefix ?? "(none)");

                var request = new ListObjectsV2Request;
                {
                    BucketName = bucketName,
                    Prefix = prefix;
                };

                var objects = new List<S3ObjectInfo>();
                ListObjectsV2Response response;

                do;
                {
                    response = await _s3Client.ListObjectsV2Async(request, cancellationToken);

                    foreach (var s3Object in response.S3Objects)
                    {
                        objects.Add(new S3ObjectInfo;
                        {
                            BucketName = bucketName,
                            Key = s3Object.Key,
                            ETag = s3Object.ETag,
                            Size = s3Object.Size,
                            LastModified = s3Object.LastModified,
                            StorageClass = s3Object.StorageClass.Value;
                        });
                    }

                    request.ContinuationToken = response.NextContinuationToken;

                } while (response.IsTruncated);

                var operationResult = AWSOperationResult<List<S3ObjectInfo>>.SuccessResult(objects);
                operationResult.Duration = DateTime.UtcNow - startTime;
                operationResult.Metadata["Count"] = objects.Count;
                operationResult.Metadata["Bucket"] = bucketName;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Listed {Count} objects from bucket {Bucket}", objects.Count, bucketName);

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to list objects in S3 bucket: {Bucket}", bucketName);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "S3",
                    Operation = "ListObjects",
                    Resource = bucketName,
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<List<S3ObjectInfo>>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to list objects: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<string>> GeneratePresignedUrlAsync(
            string bucketName,
            string key,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
        {
            var operationName = "S3.GeneratePresignedUrl";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                bucketName = bucketName ?? _config.DefaultBucket;
                if (string.IsNullOrEmpty(bucketName))
                    throw new ArgumentException("Bucket name is required");

                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Key is required");

                if (expiration <= TimeSpan.Zero || expiration > TimeSpan.FromDays(7))
                    throw new ArgumentException("Expiration must be between 1 second and 7 days");

                _logger.LogDebug("Generating presigned URL for: {Bucket}/{Key}, Expires in: {Expiration}",
                    bucketName, key, expiration);

                var request = new GetPreSignedUrlRequest;
                {
                    BucketName = bucketName,
                    Key = key,
                    Expires = DateTime.UtcNow.Add(expiration),
                    Verb = HttpVerb.GET;
                };

                var url = _s3Client.GetPreSignedURL(request);

                var operationResult = AWSOperationResult<string>.SuccessResult(url);
                operationResult.Duration = DateTime.UtcNow - startTime;
                operationResult.Metadata["Expires"] = DateTime.UtcNow.Add(expiration);

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Generated presigned URL: {Url}", url);

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to generate presigned URL for: {Bucket}/{Key}", bucketName, key);

                RecordOperationMetrics(operationName, false, duration);

                return AWSOperationResult<string>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to generate presigned URL: {ex.Message}",
                    ex);
            }
        }

        #endregion;

        #region EC2 Operations;

        public async Task<AWSOperationResult<List<EC2InstanceInfo>>> ListInstancesAsync(
            CancellationToken cancellationToken = default)
        {
            var operationName = "EC2.ListInstances";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_ec2Client == null)
                    await InitializeEC2ClientAsync();

                _logger.LogDebug("Listing EC2 instances");

                var request = new DescribeInstancesRequest();
                var response = await _ec2Client.DescribeInstancesAsync(request, cancellationToken);

                var instances = new List<EC2InstanceInfo>();

                foreach (var reservation in response.Reservations)
                {
                    foreach (var instance in reservation.Instances)
                    {
                        var instanceInfo = new EC2InstanceInfo;
                        {
                            InstanceId = instance.InstanceId,
                            InstanceType = instance.InstanceType.Value,
                            State = instance.State.Name.Value,
                            PublicIpAddress = instance.PublicIpAddress,
                            PrivateIpAddress = instance.PrivateIpAddress,
                            LaunchTime = instance.LaunchTime;
                        };

                        // Add tags;
                        if (instance.Tags != null)
                        {
                            foreach (var tag in instance.Tags)
                            {
                                instanceInfo.Tags[tag.Key] = tag.Value;
                            }
                        }

                        // Add security groups;
                        if (instance.SecurityGroups != null)
                        {
                            instanceInfo.SecurityGroups.AddRange(
                                instance.SecurityGroups.Select(sg => sg.GroupName));
                        }

                        instances.Add(instanceInfo);
                    }
                }

                var operationResult = AWSOperationResult<List<EC2InstanceInfo>>.SuccessResult(instances);
                operationResult.Duration = DateTime.UtcNow - startTime;
                operationResult.Metadata["Count"] = instances.Count;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Listed {Count} EC2 instances", instances.Count);

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to list EC2 instances");

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "EC2",
                    Operation = "ListInstances",
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<List<EC2InstanceInfo>>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to list instances: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<bool>> StartInstanceAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            var operationName = "EC2.StartInstance";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_ec2Client == null)
                    await InitializeEC2ClientAsync();

                instanceId = instanceId ?? _config.DefaultInstanceId;
                if (string.IsNullOrEmpty(instanceId))
                    throw new ArgumentException("Instance ID is required");

                _logger.LogInformation("Starting EC2 instance: {InstanceId}", instanceId);

                var request = new StartInstancesRequest;
                {
                    InstanceIds = new List<string> { instanceId }
                };

                var response = await _ec2Client.StartInstancesAsync(request, cancellationToken);

                var success = response.StartingInstances.Count > 0 &&
                             response.StartingInstances[0].CurrentState.Name.Value == "pending";

                var operationResult = AWSOperationResult<bool>.SuccessResult(success);
                operationResult.Duration = DateTime.UtcNow - startTime;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogInformation("EC2 instance {InstanceId} start initiated: {Success}",
                    instanceId, success);

                OnOperationCompleted(new AWSOperationEventArgs;
                {
                    Service = "EC2",
                    Operation = "StartInstance",
                    Resource = instanceId,
                    Duration = operationResult.Duration,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                });

                // Publish event;
                await _eventBus.PublishAsync(new EC2InstanceStartedEvent;
                {
                    InstanceId = instanceId,
                    Timestamp = DateTime.UtcNow;
                });

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to start EC2 instance: {InstanceId}", instanceId);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "EC2",
                    Operation = "StartInstance",
                    Resource = instanceId,
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<bool>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to start instance: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<bool>> StopInstanceAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            var operationName = "EC2.StopInstance";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_ec2Client == null)
                    await InitializeEC2ClientAsync();

                instanceId = instanceId ?? _config.DefaultInstanceId;
                if (string.IsNullOrEmpty(instanceId))
                    throw new ArgumentException("Instance ID is required");

                _logger.LogInformation("Stopping EC2 instance: {InstanceId}", instanceId);

                var request = new StopInstancesRequest;
                {
                    InstanceIds = new List<string> { instanceId }
                };

                var response = await _ec2Client.StopInstancesAsync(request, cancellationToken);

                var success = response.StoppingInstances.Count > 0 &&
                             response.StoppingInstances[0].CurrentState.Name.Value == "stopping";

                var operationResult = AWSOperationResult<bool>.SuccessResult(success);
                operationResult.Duration = DateTime.UtcNow - startTime;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogInformation("EC2 instance {InstanceId} stop initiated: {Success}",
                    instanceId, success);

                OnOperationCompleted(new AWSOperationEventArgs;
                {
                    Service = "EC2",
                    Operation = "StopInstance",
                    Resource = instanceId,
                    Duration = operationResult.Duration,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                });

                // Publish event;
                await _eventBus.PublishAsync(new EC2InstanceStoppedEvent;
                {
                    InstanceId = instanceId,
                    Timestamp = DateTime.UtcNow;
                });

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to stop EC2 instance: {InstanceId}", instanceId);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "EC2",
                    Operation = "StopInstance",
                    Resource = instanceId,
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<bool>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to stop instance: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<EC2InstanceInfo>> GetInstanceInfoAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            var operationName = "EC2.GetInstanceInfo";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_ec2Client == null)
                    await InitializeEC2ClientAsync();

                instanceId = instanceId ?? _config.DefaultInstanceId;
                if (string.IsNullOrEmpty(instanceId))
                    throw new ArgumentException("Instance ID is required");

                _logger.LogDebug("Getting EC2 instance info: {InstanceId}", instanceId);

                var request = new DescribeInstancesRequest;
                {
                    InstanceIds = new List<string> { instanceId }
                };

                var response = await _ec2Client.DescribeInstancesAsync(request, cancellationToken);

                if (response.Reservations.Count == 0 || response.Reservations[0].Instances.Count == 0)
                {
                    throw new AWSClientException($"Instance not found: {instanceId}");
                }

                var instance = response.Reservations[0].Instances[0];
                var instanceInfo = new EC2InstanceInfo;
                {
                    InstanceId = instance.InstanceId,
                    InstanceType = instance.InstanceType.Value,
                    State = instance.State.Name.Value,
                    PublicIpAddress = instance.PublicIpAddress,
                    PrivateIpAddress = instance.PrivateIpAddress,
                    LaunchTime = instance.LaunchTime;
                };

                // Add tags;
                if (instance.Tags != null)
                {
                    foreach (var tag in instance.Tags)
                    {
                        instanceInfo.Tags[tag.Key] = tag.Value;
                    }
                }

                // Add security groups;
                if (instance.SecurityGroups != null)
                {
                    instanceInfo.SecurityGroups.AddRange(
                        instance.SecurityGroups.Select(sg => sg.GroupName));
                }

                var operationResult = AWSOperationResult<EC2InstanceInfo>.SuccessResult(instanceInfo);
                operationResult.Duration = DateTime.UtcNow - startTime;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Retrieved EC2 instance info: {InstanceId}", instanceId);

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to get EC2 instance info: {InstanceId}", instanceId);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "EC2",
                    Operation = "GetInstanceInfo",
                    Resource = instanceId,
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<EC2InstanceInfo>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to get instance info: {ex.Message}",
                    ex);
            }
        }

        #endregion;

        #region Lambda Operations;

        public async Task<AWSOperationResult<LambdaFunctionInfo>> GetFunctionInfoAsync(
            string functionName,
            CancellationToken cancellationToken = default)
        {
            var operationName = "Lambda.GetFunctionInfo";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_lambdaClient == null)
                    await InitializeLambdaClientAsync();

                functionName = functionName ?? _config.DefaultFunctionName;
                if (string.IsNullOrEmpty(functionName))
                    throw new ArgumentException("Function name is required");

                _logger.LogDebug("Getting Lambda function info: {FunctionName}", functionName);

                var request = new GetFunctionRequest;
                {
                    FunctionName = functionName;
                };

                var response = await _lambdaClient.GetFunctionAsync(request, cancellationToken);

                var functionInfo = new LambdaFunctionInfo;
                {
                    FunctionName = response.Configuration.FunctionName,
                    FunctionArn = response.Configuration.FunctionArn,
                    Runtime = response.Configuration.Runtime,
                    Handler = response.Configuration.Handler,
                    Timeout = response.Configuration.Timeout,
                    MemorySize = response.Configuration.MemorySize,
                    LastModified = response.Configuration.LastModified,
                    Description = response.Configuration.Description;
                };

                // Add environment variables;
                if (response.Configuration.Environment?.Variables != null)
                {
                    foreach (var variable in response.Configuration.Environment.Variables)
                    {
                        functionInfo.EnvironmentVariables[variable.Key] = variable.Value;
                    }
                }

                var operationResult = AWSOperationResult<LambdaFunctionInfo>.SuccessResult(functionInfo);
                operationResult.Duration = DateTime.UtcNow - startTime;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Retrieved Lambda function info: {FunctionName}", functionName);

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to get Lambda function info: {FunctionName}", functionName);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "Lambda",
                    Operation = "GetFunctionInfo",
                    Resource = functionName,
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<LambdaFunctionInfo>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to get function info: {ex.Message}",
                    ex);
            }
        }

        public async Task<AWSOperationResult<string>> InvokeFunctionAsync(
            string functionName,
            string payload,
            CancellationToken cancellationToken = default)
        {
            var operationName = "Lambda.InvokeFunction";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_lambdaClient == null)
                    await InitializeLambdaClientAsync();

                functionName = functionName ?? _config.DefaultFunctionName;
                if (string.IsNullOrEmpty(functionName))
                    throw new ArgumentException("Function name is required");

                _logger.LogInformation("Invoking Lambda function: {FunctionName}", functionName);

                var request = new InvokeRequest;
                {
                    FunctionName = functionName,
                    Payload = payload,
                    InvocationType = InvocationType.RequestResponse;
                };

                var response = await _lambdaClient.InvokeAsync(request, cancellationToken);

                string result = null;
                if (response.Payload != null)
                {
                    using (var reader = new StreamReader(response.Payload))
                    {
                        result = await reader.ReadToEndAsync();
                    }
                }

                var operationResult = AWSOperationResult<string>.SuccessResult(result);
                operationResult.Duration = DateTime.UtcNow - startTime;
                operationResult.Metadata["StatusCode"] = response.StatusCode;
                operationResult.Metadata["LogResult"] = response.LogResult;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Lambda function invoked: {FunctionName}, Status: {StatusCode}",
                    functionName, response.StatusCode);

                OnOperationCompleted(new AWSOperationEventArgs;
                {
                    Service = "Lambda",
                    Operation = "InvokeFunction",
                    Resource = functionName,
                    Duration = operationResult.Duration,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                });

                // Publish event;
                await _eventBus.PublishAsync(new LambdaFunctionInvokedEvent;
                {
                    FunctionName = functionName,
                    StatusCode = response.StatusCode,
                    Timestamp = DateTime.UtcNow;
                });

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to invoke Lambda function: {FunctionName}", functionName);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "Lambda",
                    Operation = "InvokeFunction",
                    Resource = functionName,
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<string>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to invoke function: {ex.Message}",
                    ex);
            }
        }

        #endregion;

        #region CloudWatch Operations;

        public async Task<AWSOperationResult<List<CloudWatchMetricData>>> GetMetricsAsync(
            string namespaceName,
            string metricName,
            TimeSpan period,
            CancellationToken cancellationToken = default)
        {
            var operationName = "CloudWatch.GetMetrics";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_cloudWatchClient == null)
                    await InitializeCloudWatchClientAsync();

                if (string.IsNullOrEmpty(namespaceName))
                    throw new ArgumentException("Namespace name is required");

                if (string.IsNullOrEmpty(metricName))
                    throw new ArgumentException("Metric name is required");

                if (period <= TimeSpan.Zero)
                    throw new ArgumentException("Period must be positive");

                _logger.LogDebug("Getting CloudWatch metrics: {Namespace}/{MetricName}",
                    namespaceName, metricName);

                var request = new GetMetricStatisticsRequest;
                {
                    Namespace = namespaceName,
                    MetricName = metricName,
                    StartTime = DateTime.UtcNow.Subtract(period),
                    EndTime = DateTime.UtcNow,
                    Period = (int)period.TotalSeconds,
                    Statistics = new List<string> { "Average", "Sum", "SampleCount", "Maximum", "Minimum" }
                };

                var response = await _cloudWatchClient.GetMetricStatisticsAsync(request, cancellationToken);

                var metricData = new CloudWatchMetricData;
                {
                    Namespace = namespaceName,
                    MetricName = metricName,
                    Dimensions = response.Datapoints.Count > 0 ? response.Datapoints[0].Dimensions : new List<Dimension>(),
                    Datapoints = response.Datapoints,
                    Unit = response.Datapoints.Count > 0 ? response.Datapoints[0].Unit : null;
                };

                var operationResult = AWSOperationResult<List<CloudWatchMetricData>>.SuccessResult(
                    new List<CloudWatchMetricData> { metricData });
                operationResult.Duration = DateTime.UtcNow - startTime;
                operationResult.Metadata["DatapointCount"] = response.Datapoints.Count;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Retrieved {Count} CloudWatch datapoints", response.Datapoints.Count);

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to get CloudWatch metrics: {Namespace}/{MetricName}",
                    namespaceName, metricName);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "CloudWatch",
                    Operation = "GetMetrics",
                    Resource = $"{namespaceName}/{metricName}",
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<List<CloudWatchMetricData>>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to get metrics: {ex.Message}",
                    ex);
            }
        }

        #endregion;

        #region SQS Operations;

        public async Task<AWSOperationResult<string>> SendMessageAsync(
            string queueUrl,
            string messageBody,
            Dictionary<string, string> messageAttributes = null,
            CancellationToken cancellationToken = default)
        {
            var operationName = "SQS.SendMessage";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                if (_sqsClient == null)
                    await InitializeSQSClientAsync();

                queueUrl = queueUrl ?? _config.DefaultQueueUrl;
                if (string.IsNullOrEmpty(queueUrl))
                    throw new ArgumentException("Queue URL is required");

                if (string.IsNullOrEmpty(messageBody))
                    throw new ArgumentException("Message body is required");

                _logger.LogDebug("Sending message to SQS queue: {QueueUrl}", queueUrl);

                var request = new SendMessageRequest;
                {
                    QueueUrl = queueUrl,
                    MessageBody = messageBody;
                };

                if (messageAttributes != null)
                {
                    foreach (var attr in messageAttributes)
                    {
                        request.MessageAttributes.Add(attr.Key,
                            new MessageAttributeValue;
                            {
                                DataType = "String",
                                StringValue = attr.Value;
                            });
                    }
                }

                var response = await _sqsClient.SendMessageAsync(request, cancellationToken);

                var operationResult = AWSOperationResult<string>.SuccessResult(response.MessageId);
                operationResult.Duration = DateTime.UtcNow - startTime;

                RecordOperationMetrics(operationName, true, operationResult.Duration);

                _logger.LogDebug("Message sent to SQS: {MessageId}", response.MessageId);

                OnOperationCompleted(new AWSOperationEventArgs;
                {
                    Service = "SQS",
                    Operation = "SendMessage",
                    Resource = queueUrl,
                    Duration = operationResult.Duration,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                });

                // Publish event;
                await _eventBus.PublishAsync(new SQSMessageSentEvent;
                {
                    QueueUrl = queueUrl,
                    MessageId = response.MessageId,
                    Timestamp = DateTime.UtcNow;
                });

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "Failed to send message to SQS queue: {QueueUrl}", queueUrl);

                RecordOperationMetrics(operationName, false, duration);

                OnOperationFailed(new AWSErrorEventArgs;
                {
                    Service = "SQS",
                    Operation = "SendMessage",
                    Resource = queueUrl,
                    ErrorCode = GetErrorCode(ex),
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                return AWSOperationResult<string>.ErrorResult(
                    GetErrorCode(ex),
                    $"Failed to send message: {ex.Message}",
                    ex);
            }
        }

        #endregion;

        #region Utility Operations;

        public async Task<AWSOperationResult<bool>> TestConnectionAsync(AWSServiceType serviceType = AWSServiceType.S3)
        {
            var operationName = "TestConnection";
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateInitialized();

                _logger.LogInformation("Testing AWS connection for service: {ServiceType}", serviceType);

                bool success = false;

                switch (serviceType)
                {
                    case AWSServiceType.S3:
                        success = await TestS3ConnectionAsync();
                        break;
                    case AWSServiceType.EC2:
                        success = await TestEC2ConnectionAsync();
                        break;
                    case AWSServiceType.Lambda:
                        success = await TestLambdaConnectionAsync();
                        break;
                    case AWSServiceType.All:
                        success = await TestS3ConnectionAsync() &&
                                 await TestEC2ConnectionAsync() &&
                                 await TestLambdaConnectionAsync();
                        break;
                    default:
                        success = await TestS3ConnectionAsync();
                        break;
                }

                var operationResult = AWSOperationResult<bool>.SuccessResult(success);
                operationResult.Duration = DateTime.UtcNow - startTime;

                RecordOperationMetrics(operationName, success, operationResult.Duration);

                _logger.LogInformation("AWS connection test {Result} for service: {ServiceType}",
                    success ? "succeeded" : "failed", serviceType);

                return operationResult;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "AWS connection test failed for service: {ServiceType}", serviceType);

                RecordOperationMetrics(operationName, false, duration);

                return AWSOperationResult<bool>.ErrorResult(
                    GetErrorCode(ex),
                    $"Connection test failed: {ex.Message}",
                    ex);
            }
        }

        private async Task<bool> TestS3ConnectionAsync()
        {
            try
            {
                // Try to list buckets (low-cost operation)
                var response = await _s3Client.ListBucketsAsync();
                return response.Buckets != null;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TestEC2ConnectionAsync()
        {
            try
            {
                if (_ec2Client == null)
                    await InitializeEC2ClientAsync();

                // Try to describe regions (low-cost operation)
                var response = await _ec2Client.DescribeRegionsAsync();
                return response.Regions != null;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TestLambdaConnectionAsync()
        {
            try
            {
                if (_lambdaClient == null)
                    await InitializeLambdaClientAsync();

                // Try to list functions (limited to small number)
                var request = new ListFunctionsRequest { MaxItems = 1 };
                var response = await _lambdaClient.ListFunctionsAsync(request);
                return response.Functions != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<AWSOperationResult<AWSOperationMetrics>> GetOperationMetricsAsync(TimeSpan timeWindow)
        {
            try
            {
                var endTime = DateTime.UtcNow;
                var startTime = endTime.Subtract(timeWindow);

                var metrics = new AWSOperationMetrics;
                {
                    WindowStart = startTime,
                    WindowEnd = endTime;
                };

                // Aggregate metrics from all operations;
                foreach (var kvp in _operationMetrics)
                {
                    // Filter by time window;
                    if (kvp.Value.WindowEnd >= startTime && kvp.Value.WindowStart <= endTime)
                    {
                        metrics.RequestCount += kvp.Value.RequestCount;
                        metrics.ErrorCount += kvp.Value.ErrorCount;
                        metrics.TotalDuration += kvp.Value.TotalDuration;

                        // Merge status codes;
                        foreach (var status in kvp.Value.StatusCodes)
                        {
                            if (metrics.StatusCodes.ContainsKey(status.Key))
                                metrics.StatusCodes[status.Key] += status.Value;
                            else;
                                metrics.StatusCodes[status.Key] = status.Value;
                        }
                    }
                }

                // Calculate averages;
                if (metrics.RequestCount > 0)
                {
                    metrics.AverageLatency = TimeSpan.FromTicks(
                        metrics.TotalDuration.Ticks / metrics.RequestCount);
                }

                var operationResult = AWSOperationResult<AWSOperationMetrics>.SuccessResult(metrics);
                operationResult.Metadata["TimeWindow"] = timeWindow;

                return operationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get operation metrics");
                return AWSOperationResult<AWSOperationMetrics>.ErrorResult(
                    "METRICS_ERROR",
                    $"Failed to get operation metrics: {ex.Message}",
                    ex);
            }
        }

        #endregion;

        #region Helper Methods;

        private void ValidateInitialized()
        {
            if (!IsInitialized)
                throw new AWSClientException("AWSClient is not initialized. Call InitializeAsync first.");
        }

        private async Task InitializeEC2ClientAsync()
        {
            if (_ec2Client == null)
            {
                var credentials = _config.Credentials.GetCredentials();
                _ec2Client = new AmazonEC2Client(credentials, _config.Region.RegionEndpoint);
                _logger.LogDebug("EC2 client initialized on-demand");
            }

            await Task.CompletedTask;
        }

        private async Task InitializeLambdaClientAsync()
        {
            if (_lambdaClient == null)
            {
                var credentials = _config.Credentials.GetCredentials();
                _lambdaClient = new AmazonLambdaClient(credentials, _config.Region.RegionEndpoint);
                _logger.LogDebug("Lambda client initialized on-demand");
            }

            await Task.CompletedTask;
        }

        private async Task InitializeCloudWatchClientAsync()
        {
            if (_cloudWatchClient == null)
            {
                var credentials = _config.Credentials.GetCredentials();
                _cloudWatchClient = new AmazonCloudWatchClient(credentials, _config.Region.RegionEndpoint);
                _logger.LogDebug("CloudWatch client initialized on-demand");
            }

            await Task.CompletedTask;
        }

        private async Task InitializeSQSClientAsync()
        {
            if (_sqsClient == null)
            {
                var credentials = _config.Credentials.GetCredentials();
                _sqsClient = new AmazonSQSClient(credentials, _config.Region.RegionEndpoint);
                _logger.LogDebug("SQS client initialized on-demand");
            }

            await Task.CompletedTask;
        }

        private string GetErrorCode(Exception ex)
        {
            return ex switch;
            {
                AmazonServiceException awsEx => awsEx.ErrorCode,
                AmazonClientException clientEx => "CLIENT_ERROR",
                TimeoutException => "TIMEOUT",
                OperationCanceledException => "CANCELLED",
                _ => "UNKNOWN_ERROR"
            };
        }

        private void RecordOperationMetrics(string operationName, bool success, TimeSpan duration)
        {
            if (!_config.EnableMetrics)
                return;

            var now = DateTime.UtcNow;

            if (!_operationMetrics.ContainsKey(operationName))
            {
                _operationMetrics[operationName] = new AWSOperationMetrics;
                {
                    Service = operationName.Split('.')[0],
                    Operation = operationName,
                    WindowStart = now,
                    WindowEnd = now;
                };
            }

            var metrics = _operationMetrics[operationName];
            metrics.RequestCount++;

            if (!success)
                metrics.ErrorCount++;

            metrics.TotalDuration += duration;
            metrics.WindowEnd = now;

            // Update status code counts;
            var statusCode = success ? "Success" : "Error";
            if (metrics.StatusCodes.ContainsKey(statusCode))
                metrics.StatusCodes[statusCode]++;
            else;
                metrics.StatusCodes[statusCode] = 1;
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnClientInitialized(AWSClientEventArgs e)
        {
            ClientInitialized?.Invoke(this, e);
        }

        protected virtual void OnOperationCompleted(AWSOperationEventArgs e)
        {
            OperationCompleted?.Invoke(this, e);
        }

        protected virtual void OnOperationFailed(AWSErrorEventArgs e)
        {
            OperationFailed?.Invoke(this, e);
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
            if (_disposed)
                return;

            if (disposing)
            {
                DisposeServiceClients();
                _httpClient?.Dispose();
                _initializationLock?.Dispose();
            }

            _disposed = true;
        }

        private void DisposeServiceClients()
        {
            _s3Client?.Dispose();
            _ec2Client?.Dispose();
            _lambdaClient?.Dispose();
            _cloudWatchClient?.Dispose();
            _rdsClient?.Dispose();
            _sqsClient?.Dispose();
            _snsClient?.Dispose();
            _dynamoDbClient?.Dispose();
            _cloudFrontClient?.Dispose();
            _sesClient?.Dispose();
        }

        #endregion;
    }

    /// <summary>
    /// Event arguments for AWS client events;
    /// </summary>
    public class AWSClientEventArgs : EventArgs;
    {
        public string Region { get; set; }
        public List<string> ServicesInitialized { get; set; }
        public DateTime Timestamp { get; set; }

        public AWSClientEventArgs()
        {
            ServicesInitialized = new List<string>();
        }
    }

    /// <summary>
    /// Event arguments for AWS operation completion;
    /// </summary>
    public class AWSOperationEventArgs : EventArgs;
    {
        public string Service { get; set; }
        public string Operation { get; set; }
        public string Resource { get; set; }
        public bool Success { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for AWS operation failure;
    /// </summary>
    public class AWSErrorEventArgs : EventArgs;
    {
        public string Service { get; set; }
        public string Operation { get; set; }
        public string Resource { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Custom exception for AWS client errors;
    /// </summary>
    public class AWSClientException : Exception
    {
        public string Service { get; }
        public string Operation { get; }

        public AWSClientException(string message) : base(message) { }

        public AWSClientException(string message, Exception innerException)
            : base(message, innerException) { }

        public AWSClientException(string message, string service, string operation, Exception innerException = null)
            : base(message, innerException)
        {
            Service = service;
            Operation = operation;
        }
    }

    /// <summary>
    /// Factory for creating AWS clients;
    /// </summary>
    public class AWSClientFactory;
    {
        private readonly ILogger<AWSClientFactory> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AWSClientFactory(
            ILogger<AWSClientFactory> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public async Task<IAWSClient> CreateClientAsync(AWSClientConfig config = null)
        {
            try
            {
                _logger.LogInformation("Creating AWS client");

                var client = new AWSClient(
                    _serviceProvider.GetRequiredService<ILogger<AWSClient>>(),
                    _serviceProvider.GetRequiredService<IMemoryCache>(),
                    _serviceProvider.GetRequiredService<IEventBus>());

                await client.InitializeAsync(config);

                _logger.LogDebug("AWS client created successfully");

                return client;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create AWS client");
                throw new AWSClientException("Failed to create AWS client", ex);
            }
        }

        public async Task<IAWSClient> CreateClientWithProfileAsync(string profileName, string region = "us-east-1")
        {
            var config = new AWSClientConfig;
            {
                Credentials = new AWSCredentialsConfig;
                {
                    ProfileName = profileName;
                },
                Region = new AWSRegion(region, RegionEndpoint.GetBySystemName(region))
            };

            return await CreateClientAsync(config);
        }

        public async Task<IAWSClient> CreateClientWithCredentialsAsync(
            string accessKeyId,
            string secretAccessKey,
            string region = "us-east-1")
        {
            var config = new AWSClientConfig;
            {
                Credentials = new AWSCredentialsConfig(accessKeyId, secretAccessKey),
                Region = new AWSRegion(region, RegionEndpoint.GetBySystemName(region))
            };

            return await CreateClientAsync(config);
        }
    }

    /// <summary>
    /// Service registration extension;
    /// </summary>
    public static class AWSClientExtensions;
    {
        public static IServiceCollection AddAWSClient(this IServiceCollection services, Action<AWSClientConfig> configure = null)
        {
            services.AddScoped<IAWSClient, AWSClient>();
            services.AddSingleton<AWSClientFactory>();

            // Configure default AWS client if configuration provided;
            if (configure != null)
            {
                services.Configure(configure);
            }

            // Add HTTP client factory for AWS operations;
            services.AddHttpClient("AWSClient")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler;
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                });

            // Register event subscribers;
            services.AddTransient<IEventBusSubscriber, AWSEventSubscriber>();

            return services;
        }
    }

    /// <summary>
    /// AWS event subscriber;
    /// </summary>
    public class AWSEventSubscriber : IEventBusSubscriber;
    {
        private readonly ILogger<AWSEventSubscriber> _logger;

        public AWSEventSubscriber(ILogger<AWSEventSubscriber> logger)
        {
            _logger = logger;
        }

        public void Subscribe(IEventBus eventBus)
        {
            eventBus.Subscribe<S3ObjectUploadedEvent>(HandleS3Uploaded);
            eventBus.Subscribe<EC2InstanceStartedEvent>(HandleEC2Started);
            eventBus.Subscribe<LambdaFunctionInvokedEvent>(HandleLambdaInvoked);
        }

        private async Task HandleS3Uploaded(S3ObjectUploadedEvent @event)
        {
            _logger.LogInformation("S3 object uploaded: {@Event}", @event);

            // Example: Trigger additional processing, update database, etc.
            await Task.CompletedTask;
        }

        private async Task HandleEC2Started(EC2InstanceStartedEvent @event)
        {
            _logger.LogInformation("EC2 instance started: {@Event}", @event);

            // Example: Send notification, update monitoring, etc.
            await Task.CompletedTask;
        }

        private async Task HandleLambdaInvoked(LambdaFunctionInvokedEvent @event)
        {
            _logger.LogInformation("Lambda function invoked: {@Event}", @event);

            // Example: Log invocation, update metrics, etc.
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Event for S3 object uploaded;
    /// </summary>
    public class S3ObjectUploadedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string BucketName { get; set; }
        public string Key { get; set; }
        public long Size { get; set; }
        public string ETag { get; set; }
    }

    /// <summary>
    /// Event for S3 object deleted;
    /// </summary>
    public class S3ObjectDeletedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string BucketName { get; set; }
        public string Key { get; set; }
    }

    /// <summary>
    /// Event for EC2 instance started;
    /// </summary>
    public class EC2InstanceStartedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string InstanceId { get; set; }
    }

    /// <summary>
    /// Event for EC2 instance stopped;
    /// </summary>
    public class EC2InstanceStoppedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string InstanceId { get; set; }
    }

    /// <summary>
    /// Event for Lambda function invoked;
    /// </summary>
    public class LambdaFunctionInvokedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string FunctionName { get; set; }
        public int StatusCode { get; set; }
    }

    /// <summary>
    /// Event for SQS message sent;
    /// </summary>
    public class SQSMessageSentEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string QueueUrl { get; set; }
        public string MessageId { get; set; }
    }
}
