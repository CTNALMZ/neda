using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Brain.DecisionMaking.LogicProcessor;
using NEDA.Cloud.Common;
using NEDA.Common.Exceptions;
using NEDA.Common.Utilities;
using NEDA.SecurityModules.Encryption;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.Cloud.Azure;
{
    /// <summary>
    /// Azure Blob Storage servisi için yönetim ve operasyon sınıfı;
    /// </summary>
    public class BlobService : IBlobService, IDisposable;
    {
        private readonly ILogger<BlobService> _logger;
        private readonly AzureCloudConfig _config;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IPerformanceMonitor _performanceMonitor;
        private BlobServiceClient _blobServiceClient;
        private readonly Dictionary<string, BlobContainerClient> _containerCache;
        private readonly SemaphoreSlim _clientLock = new SemaphoreSlim(1, 1);
        private bool _disposed;
        private DateTime _lastHealthCheck;

        /// <summary>
        /// BlobService constructor;
        /// </summary>
        public BlobService(
            ILogger<BlobService> logger,
            IOptions<AzureCloudConfig> configOptions,
            ICryptoEngine cryptoEngine,
            IPerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = configOptions?.Value ?? throw new ArgumentNullException(nameof(configOptions));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            _containerCache = new Dictionary<string, BlobContainerClient>(StringComparer.OrdinalIgnoreCase);

            InitializeClient();
            _logger.LogInformation("BlobService initialized with account: {AccountName}",
                _config.StorageAccountName);
        }

        /// <summary>
        /// Azure Blob Service Client başlatma;
        /// </summary>
        private void InitializeClient()
        {
            try
            {
                var connectionString = BuildConnectionString();
                _blobServiceClient = new BlobServiceClient(connectionString);

                _logger.LogDebug("BlobServiceClient initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize BlobServiceClient");
                throw new CloudServiceException(
                    "Azure Blob Service client initialization failed",
                    ex,
                    ErrorCodes.AzureConnectionFailed);
            }
        }

        /// <summary>
        /// Bağlantı string oluşturma;
        /// </summary>
        private string BuildConnectionString()
        {
            if (!string.IsNullOrEmpty(_config.ConnectionString))
            {
                return _config.ConnectionString;
            }

            if (string.IsNullOrEmpty(_config.StorageAccountName) ||
                string.IsNullOrEmpty(_config.StorageAccountKey))
            {
                throw new ConfigurationException(
                    "Storage account name and key must be provided",
                    ErrorCodes.InvalidCloudConfiguration);
            }

            return $"DefaultEndpointsProtocol=https;" +
                   $"AccountName={_config.StorageAccountName};" +
                   $"AccountKey={_config.StorageAccountKey};" +
                   $"EndpointSuffix=core.windows.net";
        }

        /// <summary>
        /// Container istemcisi alma (cache'li)
        /// </summary>
        private async Task<BlobContainerClient> GetContainerClientAsync(
            string containerName,
            bool createIfNotExists = false,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));

            if (_containerCache.TryGetValue(containerName, out var cachedClient))
            {
                return cachedClient;
            }

            await _clientLock.WaitAsync(cancellationToken);
            try
            {
                // Double-check locking;
                if (_containerCache.TryGetValue(containerName, out cachedClient))
                {
                    return cachedClient;
                }

                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);

                if (createIfNotExists)
                {
                    await containerClient.CreateIfNotExistsAsync(
                        PublicAccessType.None,
                        cancellationToken: cancellationToken);

                    _logger.LogDebug("Container created/verified: {ContainerName}", containerName);
                }

                _containerCache[containerName] = containerClient;
                return containerClient;
            }
            finally
            {
                _clientLock.Release();
            }
        }

        /// <summary>
        /// Dosya yükleme;
        /// </summary>
        public async Task<BlobUploadResult> UploadFileAsync(
            string containerName,
            string blobName,
            Stream content,
            string contentType = null,
            IDictionary<string, string> metadata = null,
            bool encrypt = false,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));
            Guard.AgainstNullOrEmpty(blobName, nameof(blobName));
            Guard.AgainstNull(content, nameof(content));

            var operationId = Guid.NewGuid();
            using var monitor = _performanceMonitor.StartOperation(
                OperationType.BlobUpload,
                operationId.ToString());

            try
            {
                _logger.LogInformation("Uploading blob: {BlobName} to container: {ContainerName}",
                    blobName, containerName);

                var containerClient = await GetContainerClientAsync(
                    containerName,
                    true,
                    cancellationToken);

                var blobClient = containerClient.GetBlobClient(blobName);

                // Metadata hazırlama;
                var blobMetadata = PrepareMetadata(metadata, encrypt);

                // İçerik şifreleme;
                Stream uploadStream = content;
                if (encrypt)
                {
                    uploadStream = await _cryptoEngine.EncryptStreamAsync(content, cancellationToken);
                    blobMetadata["Encrypted"] = "true";
                    blobMetadata["EncryptionAlgorithm"] = _cryptoEngine.AlgorithmName;
                }

                // Blob options;
                var options = new BlobUploadOptions;
                {
                    Metadata = blobMetadata,
                    Conditions = null;
                };

                if (!string.IsNullOrEmpty(contentType))
                {
                    options.HttpHeaders = new BlobHttpHeaders;
                    {
                        ContentType = contentType;
                    };
                }

                // Upload işlemi;
                var response = await blobClient.UploadAsync(
                    uploadStream,
                    options,
                    cancellationToken);

                var result = new BlobUploadResult;
                {
                    Success = true,
                    BlobUri = blobClient.Uri,
                    ETag = response.Value.ETag.ToString(),
                    VersionId = response.Value.VersionId,
                    ContentHash = response.Value.ContentHash != null ?
                        Convert.ToBase64String(response.Value.ContentHash) : null,
                    Size = content.Length,
                    OperationId = operationId,
                    Encrypted = encrypt;
                };

                _logger.LogInformation("Blob uploaded successfully: {BlobUri}, Size: {Size} bytes",
                    result.BlobUri, result.Size);

                monitor.Complete(result.Size);
                return result;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                _logger.LogWarning(ex, "Blob already exists: {BlobName}", blobName);
                throw new BlobAlreadyExistsException(
                    $"Blob '{blobName}' already exists in container '{containerName}'",
                    ex,
                    ErrorCodes.BlobAlreadyExists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload blob: {BlobName}", blobName);
                monitor.Fail(ex);
                throw new BlobOperationException(
                    $"Failed to upload blob '{blobName}'",
                    ex,
                    ErrorCodes.BlobUploadFailed);
            }
        }

        /// <summary>
        /// Dosya indirme;
        /// </summary>
        public async Task<Stream> DownloadFileAsync(
            string containerName,
            string blobName,
            bool decryptIfEncrypted = true,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));
            Guard.AgainstNullOrEmpty(blobName, nameof(blobName));

            var operationId = Guid.NewGuid();
            using var monitor = _performanceMonitor.StartOperation(
                OperationType.BlobDownload,
                operationId.ToString());

            try
            {
                _logger.LogDebug("Downloading blob: {BlobName} from container: {ContainerName}",
                    blobName, containerName);

                var containerClient = await GetContainerClientAsync(
                    containerName,
                    false,
                    cancellationToken);

                var blobClient = containerClient.GetBlobClient(blobName);

                // Blob özelliklerini al;
                var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);

                // Download işlemi;
                var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);

                var resultStream = new MemoryStream();
                await response.Value.Content.CopyToAsync(resultStream, cancellationToken);
                resultStream.Position = 0;

                // Şifre çözme;
                if (decryptIfEncrypted &&
                    properties.Value.Metadata.TryGetValue("Encrypted", out var encrypted) &&
                    encrypted == "true")
                {
                    if (properties.Value.Metadata.TryGetValue("EncryptionAlgorithm", out var algorithm))
                    {
                        resultStream = await _cryptoEngine.DecryptStreamAsync(
                            resultStream,
                            algorithm,
                            cancellationToken);
                    }
                    else;
                    {
                        resultStream = await _cryptoEngine.DecryptStreamAsync(
                            resultStream,
                            cancellationToken);
                    }
                }

                monitor.Complete(resultStream.Length);
                return resultStream;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning(ex, "Blob not found: {BlobName}", blobName);
                throw new BlobNotFoundException(
                    $"Blob '{blobName}' not found in container '{containerName}'",
                    ex,
                    ErrorCodes.BlobNotFound);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download blob: {BlobName}", blobName);
                monitor.Fail(ex);
                throw new BlobOperationException(
                    $"Failed to download blob '{blobName}'",
                    ex,
                    ErrorCodes.BlobDownloadFailed);
            }
        }

        /// <summary>
        /// Dosya silme;
        /// </summary>
        public async Task<bool> DeleteFileAsync(
            string containerName,
            string blobName,
            DeleteBlobOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));
            Guard.AgainstNullOrEmpty(blobName, nameof(blobName));

            var operationId = Guid.NewGuid();
            using var monitor = _performanceMonitor.StartOperation(
                OperationType.BlobDelete,
                operationId.ToString());

            try
            {
                _logger.LogInformation("Deleting blob: {BlobName} from container: {ContainerName}",
                    blobName, containerName);

                var containerClient = await GetContainerClientAsync(
                    containerName,
                    false,
                    cancellationToken);

                var blobClient = containerClient.GetBlobClient(blobName);

                var deleteOptions = options?.ToBlobDeleteOptions() ?? new BlobDeleteOptions();

                var response = await blobClient.DeleteAsync(
                    deleteOptions,
                    cancellationToken);

                _logger.LogInformation("Blob deleted successfully: {BlobName}", blobName);

                // Cache'den temizle;
                InvalidateBlobCache(containerName, blobName);

                monitor.Complete();
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning(ex, "Blob not found for deletion: {BlobName}", blobName);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete blob: {BlobName}", blobName);
                monitor.Fail(ex);
                throw new BlobOperationException(
                    $"Failed to delete blob '{blobName}'",
                    ex,
                    ErrorCodes.BlobDeleteFailed);
            }
        }

        /// <summary>
        /// SAS token oluşturma;
        /// </summary>
        public async Task<string> GenerateSasTokenAsync(
            string containerName,
            string blobName,
            BlobSasPermissions permissions,
            TimeSpan expiryDuration,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));
            Guard.AgainstNullOrEmpty(blobName, nameof(blobName));

            try
            {
                var containerClient = await GetContainerClientAsync(
                    containerName,
                    false,
                    cancellationToken);

                var blobClient = containerClient.GetBlobClient(blobName);

                // Blob'un var olduğunu doğrula;
                await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);

                // SAS builder oluştur;
                var sasBuilder = new BlobSasBuilder;
                {
                    BlobContainerName = containerName,
                    BlobName = blobName,
                    Resource = "b", // b = blob, c = container;
                    ExpiresOn = DateTimeOffset.UtcNow.Add(expiryDuration)
                };

                sasBuilder.SetPermissions(permissions);

                // SAS token oluştur;
                var sasToken = blobClient.GenerateSasUri(sasBuilder).Query;

                _logger.LogDebug("SAS token generated for blob: {BlobName}, Expires: {Expiry}",
                    blobName, sasBuilder.ExpiresOn);

                return sasToken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate SAS token for blob: {BlobName}", blobName);
                throw new BlobOperationException(
                    $"Failed to generate SAS token for blob '{blobName}'",
                    ex,
                    ErrorCodes.SasTokenGenerationFailed);
            }
        }

        /// <summary>
        /// Blob listeleme;
        /// </summary>
        public async Task<IEnumerable<BlobItemInfo>> ListBlobsAsync(
            string containerName,
            string prefix = null,
            bool includeMetadata = false,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));

            var operationId = Guid.NewGuid();
            using var monitor = _performanceMonitor.StartOperation(
                OperationType.BlobList,
                operationId.ToString());

            try
            {
                var containerClient = await GetContainerClientAsync(
                    containerName,
                    false,
                    cancellationToken);

                var results = new List<BlobItemInfo>();
                var pages = containerClient.GetBlobsAsync(
                    prefix: prefix,
                    cancellationToken: cancellationToken).AsPages();

                await foreach (var page in pages)
                {
                    foreach (var blobItem in page.Values)
                    {
                        var itemInfo = new BlobItemInfo;
                        {
                            Name = blobItem.Name,
                            ContainerName = containerName,
                            Size = blobItem.Properties.ContentLength ?? 0,
                            LastModified = blobItem.Properties.LastModified?.DateTime ?? DateTime.MinValue,
                            ContentType = blobItem.Properties.ContentType,
                            ETag = blobItem.Properties.ETag.ToString(),
                            BlobType = blobItem.Properties.BlobType.ToString(),
                            IsEncrypted = blobItem.Metadata?.ContainsKey("Encrypted") == true &&
                                         blobItem.Metadata["Encrypted"] == "true"
                        };

                        if (includeMetadata && blobItem.Metadata != null)
                        {
                            itemInfo.Metadata = new Dictionary<string, string>(blobItem.Metadata);
                        }

                        results.Add(itemInfo);
                    }
                }

                monitor.Complete(results.Count);
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list blobs in container: {ContainerName}", containerName);
                monitor.Fail(ex);
                throw new BlobOperationException(
                    $"Failed to list blobs in container '{containerName}'",
                    ex,
                    ErrorCodes.BlobListFailed);
            }
        }

        /// <summary>
        /// Container oluşturma;
        /// </summary>
        public async Task<BlobContainerInfo> CreateContainerAsync(
            string containerName,
            ContainerAccessLevel accessLevel = ContainerAccessLevel.Private,
            IDictionary<string, string> metadata = null,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));

            try
            {
                _logger.LogInformation("Creating container: {ContainerName}", containerName);

                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);

                var publicAccessType = accessLevel switch;
                {
                    ContainerAccessLevel.Private => PublicAccessType.None,
                    ContainerAccessLevel.Blob => PublicAccessType.Blob,
                    ContainerAccessLevel.Container => PublicAccessType.BlobContainer,
                    _ => PublicAccessType.None;
                };

                var response = await containerClient.CreateAsync(
                    publicAccessType,
                    metadata: metadata,
                    cancellationToken: cancellationToken);

                // Cache'e ekle;
                _containerCache[containerName] = containerClient;

                var result = new BlobContainerInfo;
                {
                    Name = containerName,
                    Uri = containerClient.Uri,
                    CreatedOn = response.Value.LastModified.DateTime,
                    AccessLevel = accessLevel,
                    ETag = response.Value.ETag.ToString()
                };

                _logger.LogInformation("Container created successfully: {ContainerName}", containerName);
                return result;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                _logger.LogWarning(ex, "Container already exists: {ContainerName}", containerName);
                throw new ContainerAlreadyExistsException(
                    $"Container '{containerName}' already exists",
                    ex,
                    ErrorCodes.ContainerAlreadyExists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create container: {ContainerName}", containerName);
                throw new BlobOperationException(
                    $"Failed to create container '{containerName}'",
                    ex,
                    ErrorCodes.ContainerCreationFailed);
            }
        }

        /// <summary>
        /// Container silme;
        /// </summary>
        public async Task<bool> DeleteContainerAsync(
            string containerName,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));

            try
            {
                _logger.LogInformation("Deleting container: {ContainerName}", containerName);

                var containerClient = await GetContainerClientAsync(
                    containerName,
                    false,
                    cancellationToken);

                var response = await containerClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

                // Cache'den temizle;
                _containerCache.Remove(containerName);

                _logger.LogInformation("Container deleted: {ContainerName}, Success: {Success}",
                    containerName, response.Value);

                return response.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete container: {ContainerName}", containerName);
                throw new BlobOperationException(
                    $"Failed to delete container '{containerName}'",
                    ex,
                    ErrorCodes.ContainerDeletionFailed);
            }
        }

        /// <summary>
        /// Blob özelliklerini alma;
        /// </summary>
        public async Task<BlobPropertiesInfo> GetBlobPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken = default)
        {
            Guard.AgainstNullOrEmpty(containerName, nameof(containerName));
            Guard.AgainstNullOrEmpty(blobName, nameof(blobName));

            try
            {
                var containerClient = await GetContainerClientAsync(
                    containerName,
                    false,
                    cancellationToken);

                var blobClient = containerClient.GetBlobClient(blobName);

                var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);

                return new BlobPropertiesInfo;
                {
                    Name = blobName,
                    ContainerName = containerName,
                    Size = properties.Value.ContentLength,
                    ContentType = properties.Value.ContentType,
                    LastModified = properties.Value.LastModified.DateTime,
                    CreatedOn = properties.Value.CreatedOn.DateTime,
                    ETag = properties.Value.ETag.ToString(),
                    ContentHash = properties.Value.ContentHash != null ?
                        Convert.ToBase64String(properties.Value.ContentHash) : null,
                    Metadata = properties.Value.Metadata != null ?
                        new Dictionary<string, string>(properties.Value.Metadata) : null,
                    BlobType = properties.Value.BlobType.ToString(),
                    AccessTier = properties.Value.AccessTier?.ToString(),
                    IsEncrypted = properties.Value.Metadata?.ContainsKey("Encrypted") == true &&
                                 properties.Value.Metadata["Encrypted"] == "true"
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning(ex, "Blob not found: {BlobName}", blobName);
                throw new BlobNotFoundException(
                    $"Blob '{blobName}' not found in container '{containerName}'",
                    ex,
                    ErrorCodes.BlobNotFound);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get blob properties: {BlobName}", blobName);
                throw new BlobOperationException(
                    $"Failed to get properties for blob '{blobName}'",
                    ex,
                    ErrorCodes.BlobPropertiesFailed);
            }
        }

        /// <summary>
        /// Sistem durum kontrolü;
        /// </summary>
        public async Task<ServiceHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            // Sağlık kontrolü throttling;
            if ((DateTime.UtcNow - _lastHealthCheck).TotalSeconds < 30)
            {
                return new ServiceHealthStatus;
                {
                    IsHealthy = true,
                    LastChecked = _lastHealthCheck,
                    Message = "Health check throttled"
                };
            }

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Service properties kontrolü;
                var properties = await _blobServiceClient.GetPropertiesAsync(cancellationToken);

                // Bir container listeleme testi;
                var containers = _blobServiceClient.GetBlobContainersAsync()
                    .AsPages(pageSizeHint: 1)
                    .GetAsyncEnumerator(cancellationToken);

                await containers.MoveNextAsync();

                stopwatch.Stop();

                _lastHealthCheck = DateTime.UtcNow;

                return new ServiceHealthStatus;
                {
                    IsHealthy = true,
                    LastChecked = _lastHealthCheck,
                    ResponseTime = stopwatch.ElapsedMilliseconds,
                    Message = $"Azure Blob Service is healthy. Account: {_config.StorageAccountName}",
                    Details = new Dictionary<string, object>
                    {
                        ["AccountName"] = _config.StorageAccountName,
                        ["ServiceProperties"] = properties.Value,
                        ["ResponseTimeMs"] = stopwatch.ElapsedMilliseconds;
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed for Azure Blob Service");

                return new ServiceHealthStatus;
                {
                    IsHealthy = false,
                    LastChecked = DateTime.UtcNow,
                    Message = $"Azure Blob Service health check failed: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Metadata hazırlama;
        /// </summary>
        private Dictionary<string, string> PrepareMetadata(
            IDictionary<string, string> customMetadata,
            bool encrypted)
        {
            var metadata = new Dictionary<string, string>
            {
                ["UploadedBy"] = "NEDA",
                ["UploadTimestamp"] = DateTime.UtcNow.ToString("O"),
                ["SystemVersion"] = AssemblyHelper.GetVersion()
            };

            if (encrypted)
            {
                metadata["Encrypted"] = "true";
                metadata["EncryptionAlgorithm"] = _cryptoEngine.AlgorithmName;
            }

            if (customMetadata != null)
            {
                foreach (var kvp in customMetadata)
                {
                    metadata[kvp.Key] = kvp.Value;
                }
            }

            return metadata;
        }

        /// <summary>
        /// Cache temizleme;
        /// </summary>
        private void InvalidateBlobCache(string containerName, string blobName)
        {
            // Implement blob-level cache invalidation if needed;
            // Currently only container-level caching;
        }

        /// <summary>
        /// Dispose pattern;
        /// </summary>
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
                    _clientLock?.Dispose();
                    _containerCache.Clear();
                }
                _disposed = true;
            }
        }

        ~BlobService()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// Blob işlemleri için arayüz;
    /// </summary>
    public interface IBlobService : IDisposable
    {
        Task<BlobUploadResult> UploadFileAsync(
            string containerName,
            string blobName,
            Stream content,
            string contentType = null,
            IDictionary<string, string> metadata = null,
            bool encrypt = false,
            CancellationToken cancellationToken = default);

        Task<Stream> DownloadFileAsync(
            string containerName,
            string blobName,
            bool decryptIfEncrypted = true,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteFileAsync(
            string containerName,
            string blobName,
            DeleteBlobOptions options = null,
            CancellationToken cancellationToken = default);

        Task<string> GenerateSasTokenAsync(
            string containerName,
            string blobName,
            BlobSasPermissions permissions,
            TimeSpan expiryDuration,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BlobItemInfo>> ListBlobsAsync(
            string containerName,
            string prefix = null,
            bool includeMetadata = false,
            CancellationToken cancellationToken = default);

        Task<BlobContainerInfo> CreateContainerAsync(
            string containerName,
            ContainerAccessLevel accessLevel = ContainerAccessLevel.Private,
            IDictionary<string, string> metadata = null,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteContainerAsync(
            string containerName,
            CancellationToken cancellationToken = default);

        Task<BlobPropertiesInfo> GetBlobPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken = default);

        Task<ServiceHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Blob yükleme sonucu;
    /// </summary>
    public class BlobUploadResult;
    {
        public bool Success { get; set; }
        public Uri BlobUri { get; set; }
        public string ETag { get; set; }
        public string VersionId { get; set; }
        public string ContentHash { get; set; }
        public long Size { get; set; }
        public Guid OperationId { get; set; }
        public bool Encrypted { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Blob öğe bilgisi;
    /// </summary>
    public class BlobItemInfo;
    {
        public string Name { get; set; }
        public string ContainerName { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string ContentType { get; set; }
        public string ETag { get; set; }
        public string BlobType { get; set; }
        public bool IsEncrypted { get; set; }
        public IDictionary<string, string> Metadata { get; set; }
    }

    /// <summary>
    /// Container bilgisi;
    /// </summary>
    public class BlobContainerInfo;
    {
        public string Name { get; set; }
        public Uri Uri { get; set; }
        public DateTime CreatedOn { get; set; }
        public ContainerAccessLevel AccessLevel { get; set; }
        public string ETag { get; set; }
    }

    /// <summary>
    /// Blob özellikleri;
    /// </summary>
    public class BlobPropertiesInfo;
    {
        public string Name { get; set; }
        public string ContainerName { get; set; }
        public long? Size { get; set; }
        public string ContentType { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime CreatedOn { get; set; }
        public string ETag { get; set; }
        public string ContentHash { get; set; }
        public IDictionary<string, string> Metadata { get; set; }
        public string BlobType { get; set; }
        public string AccessTier { get; set; }
        public bool IsEncrypted { get; set; }
    }

    /// <summary>
    /// Container erişim seviyesi;
    /// </summary>
    public enum ContainerAccessLevel;
    {
        Private,
        Blob,
        Container;
    }

    /// <summary>
    /// Silme seçenekleri;
    /// </summary>
    public class DeleteBlobOptions;
    {
        public bool DeleteSnapshots { get; set; } = true;
        public string LeaseId { get; set; }

        internal BlobDeleteOptions ToBlobDeleteOptions()
        {
            var options = new BlobDeleteOptions();

            if (DeleteSnapshots)
            {
                options.Conditions = new BlobRequestConditions();
            }

            if (!string.IsNullOrEmpty(LeaseId))
            {
                options.Conditions ??= new BlobRequestConditions();
                options.Conditions.LeaseId = LeaseId;
            }

            return options;
        }
    }

    /// <summary>
    /// Azure konfigürasyonu;
    /// </summary>
    public class AzureCloudConfig;
    {
        public string StorageAccountName { get; set; }
        public string StorageAccountKey { get; set; }
        public string ConnectionString { get; set; }
        public string DefaultContainer { get; set; } = "neda-default";
        public int RetryCount { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Servis durumu;
    /// </summary>
    public class ServiceHealthStatus;
    {
        public bool IsHealthy { get; set; }
        public DateTime LastChecked { get; set; }
        public long ResponseTime { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public IDictionary<string, object> Details { get; set; }
    }

    /// <summary>
    /// Özel exception'lar;
    /// </summary>
    public class BlobOperationException : CloudServiceException;
    {
        public BlobOperationException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode) { }
    }

    public class BlobNotFoundException : BlobOperationException;
    {
        public BlobNotFoundException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode) { }
    }

    public class BlobAlreadyExistsException : BlobOperationException;
    {
        public BlobAlreadyExistsException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode) { }
    }

    public class ContainerAlreadyExistsException : BlobOperationException;
    {
        public ContainerAlreadyExistsException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode) { }
    }

    /// <summary>
    /// Yardımcı sınıf;
    /// </summary>
    internal static class AssemblyHelper;
    {
        public static string GetVersion()
        {
            return typeof(BlobService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        }
    }
}
