using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Cloud.Storage.V1;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Services.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Cloud.GoogleCloud;
{
    /// <summary>
    /// Google Cloud Storage servisini yöneten ana interface;
    /// </summary>
    public interface IStorageService;
    {
        /// <summary>
        /// Dosyayı Cloud Storage'a yükler;
        /// </summary>
        Task<StorageUploadResult> UploadFileAsync(StorageUploadRequest request);

        /// <summary>
        /// Dosyayı Cloud Storage'dan indirir;
        /// </summary>
        Task<StorageDownloadResult> DownloadFileAsync(StorageDownloadRequest request);

        /// <summary>
        /// Dosyayı Cloud Storage'dan siler;
        /// </summary>
        Task<StorageDeleteResult> DeleteFileAsync(StorageDeleteRequest request);

        /// <summary>
        /// Dosya bilgilerini getirir;
        /// </summary>
        Task<StorageFileInfo> GetFileInfoAsync(string bucketName, string objectName);

        /// <summary>
        /// Bucket'taki dosyaları listeler;
        /// </summary>
        Task<List<StorageObjectInfo>> ListFilesAsync(string bucketName, string prefix = null, int maxResults = 1000);

        /// <summary>
        /// Signed URL oluşturur;
        /// </summary>
        Task<string> GenerateSignedUrlAsync(StorageSignedUrlRequest request);

        /// <summary>
        /// Dosya kopyalar;
        /// </summary>
        Task<StorageCopyResult> CopyFileAsync(StorageCopyRequest request);

        /// <summary>
        /// Dosya taşır;
        /// </summary>
        Task<StorageMoveResult> MoveFileAsync(StorageMoveRequest request);

        /// <summary>
        /// Resumable upload başlatır;
        /// </summary>
        Task<StorageResumableUpload> StartResumableUploadAsync(StorageResumableUploadRequest request);

        /// <summary>
        /// Resumable upload'ı tamamlar;
        /// </summary>
        Task<StorageUploadResult> CompleteResumableUploadAsync(StorageResumableUploadCompleteRequest request);

        /// <summary>
        /// Bucket oluşturur;
        /// </summary>
        Task<StorageBucketResult> CreateBucketAsync(StorageBucketRequest request);

        /// <summary>
        /// Bucket'ı siler;
        /// </summary>
        Task<StorageBucketResult> DeleteBucketAsync(string bucketName);

        /// <summary>
        /// Bucket IAM policy ayarlar;
        /// </summary>
        Task SetBucketIamPolicyAsync(string bucketName, StorageIamPolicy policy);

        /// <summary>
        /// CORS ayarlarını yapılandırır;
        /// </summary>
        Task ConfigureCorsAsync(string bucketName, List<StorageCorsRule> rules);

        /// <summary>
        /// Lifecycle policy ayarlar;
        /// </summary>
        Task ConfigureLifecycleAsync(string bucketName, List<StorageLifecycleRule> rules);

        /// <summary>
        /// Versioning'i etkinleştirir;
        /// </summary>
        Task EnableVersioningAsync(string bucketName);

        /// <summary>
        /// Object ACL ayarlar;
        /// </summary>
        Task SetObjectAclAsync(string bucketName, string objectName, StorageAcl acl);

        /// <summary>
        /// Bucket ACL ayarlar;
        /// </summary>
        Task SetBucketAclAsync(string bucketName, StorageAcl acl);

        /// <summary>
        /// Dosya boyutunu getirir;
        /// </summary>
        Task<long> GetFileSizeAsync(string bucketName, string objectName);

        /// <summary>
        /// Dosya MIME type'ını getirir;
        /// </summary>
        Task<string> GetFileContentTypeAsync(string bucketName, string objectName);

        /// <summary>
        /// Object metadata günceller;
        /// </summary>
        Task<StorageMetadataResult> UpdateObjectMetadataAsync(StorageMetadataUpdateRequest request);

        /// <summary>
        /// Bucket metadata günceller;
        /// </summary>
        Task<StorageMetadataResult> UpdateBucketMetadataAsync(string bucketName, Dictionary<string, string> metadata);

        /// <summary>
        /// Batch dosya işlemleri;
        /// </summary>
        Task<StorageBatchResult> ProcessBatchAsync(StorageBatchRequest request);

        /// <summary>
        /// Object composition (concatenation) yapar;
        /// </summary>
        Task<StorageComposeResult> ComposeObjectsAsync(StorageComposeRequest request);

        /// <summary>
        /// Object'i farklı storage class'a taşır;
        /// </summary>
        Task<StorageRewriteResult> RewriteObjectAsync(StorageRewriteRequest request);

        /// <summary>
        /// Object retention ayarlar;
        /// </summary>
        Task SetObjectRetentionAsync(string bucketName, string objectName, DateTimeOffset retentionUntil);

        /// <summary>
        /// Object hold ayarlar;
        /// </summary>
        Task SetObjectHoldAsync(string bucketName, string objectName, bool temporaryHold, bool eventBasedHold);

        /// <summary>
        /// HMAC key oluşturur;
        /// </summary>
        Task<StorageHmacKey> CreateHmacKeyAsync(string serviceAccountEmail);

        /// <summary>
        /// Notification channel oluşturur;
        /// </summary>
        Task<StorageNotificationChannel> CreateNotificationChannelAsync(StorageNotificationRequest request);
    }

    /// <summary>
    /// Google Cloud Storage Service implementasyonu;
    /// </summary>
    public class StorageService : IStorageService, IDisposable;
    {
        private readonly ILogger<StorageService> _logger;
        private readonly IAppConfig _appConfig;
        private readonly IEventBus _eventBus;
        private readonly IMetricsCollector _metricsCollector;
        private readonly StorageClient _storageClient;
        private readonly ConcurrentDictionary<string, Google.Apis.Storage.v1.Data.Bucket> _bucketCache;
        private readonly ConcurrentDictionary<string, StorageResumableUpload> _resumableUploads;
        private readonly SemaphoreSlim _operationSemaphore;

        private bool _disposed = false;
        private const int MAX_CONCURRENT_OPERATIONS = 10;
        private const int OPERATION_TIMEOUT_SECONDS = 300;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 1000;
        private const long MAX_SINGLE_UPLOAD_SIZE = 100 * 1024 * 1024; // 100MB;
        private const int DEFAULT_CHUNK_SIZE = 256 * 1024; // 256KB;

        public StorageService(
            ILogger<StorageService> logger,
            IAppConfig appConfig,
            IEventBus eventBus,
            IMetricsCollector metricsCollector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));

            // Google Cloud Authentication;
            var credential = GetGoogleCredential(appConfig);
            _storageClient = StorageClient.Create(credential);

            _bucketCache = new ConcurrentDictionary<string, Google.Apis.Storage.v1.Data.Bucket>();
            _resumableUploads = new ConcurrentDictionary<string, StorageResumableUpload>();
            _operationSemaphore = new SemaphoreSlim(MAX_CONCURRENT_OPERATIONS, MAX_CONCURRENT_OPERATIONS);

            SubscribeToEvents();

            _logger.LogInformation("Google Cloud Storage Service initialized for project: {ProjectId}",
                appConfig.GcpProjectId);
        }

        /// <summary>
        /// Google credential oluşturur;
        /// </summary>
        private GoogleCredential GetGoogleCredential(IAppConfig config)
        {
            if (!string.IsNullOrEmpty(config.GcpCredentialsJson))
            {
                // JSON credentials dosyasından;
                return GoogleCredential.FromJson(config.GcpCredentialsJson)
                    .CreateScoped(new[] { "https://www.googleapis.com/auth/cloud-platform" });
            }
            else if (!string.IsNullOrEmpty(config.GcpCredentialsPath))
            {
                // JSON credentials dosya yolundan;
                return GoogleCredential.FromFile(config.GcpCredentialsPath)
                    .CreateScoped(new[] { "https://www.googleapis.com/auth/cloud-platform" });
            }
            else;
            {
                // Application Default Credentials (ADC) kullan;
                // 1. GOOGLE_APPLICATION_CREDENTIALS environment variable;
                // 2. Google Cloud SDK;
                // 3. GCE/GKE/Cloud Run metadata server;
                return GoogleCredential.GetApplicationDefault()
                    .CreateScoped(new[] { "https://www.googleapis.com/auth/cloud-platform" });
            }
        }

        /// <summary>
        /// Event'lara subscribe olur;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<StorageUploadRequestedEvent>(OnStorageUploadRequested);
            _eventBus.Subscribe<StorageDeleteRequestedEvent>(OnStorageDeleteRequested);
            _eventBus.Subscribe<StorageCopyRequestedEvent>(OnStorageCopyRequested);
        }

        public async Task<StorageUploadResult> UploadFileAsync(StorageUploadRequest request)
        {
            await _operationSemaphore.WaitAsync();

            try
            {
                _logger.LogInformation("Uploading file to Google Cloud Storage. Bucket: {Bucket}, Object: {Object}, Size: {Size} bytes",
                    request.BucketName, request.ObjectName, request.FileStream?.Length ?? 0);

                ValidateUploadRequest(request);

                // Dosya boyutuna göre upload method seç;
                if (request.FileStream.Length > MAX_SINGLE_UPLOAD_SIZE)
                {
                    return await UploadLargeFileResumableAsync(request);
                }
                else;
                {
                    return await UploadSmallFileAsync(request);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload file to Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);

                await _eventBus.PublishAsync(new StorageUploadFailedEvent;
                {
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });

                throw new StorageServiceException($"Failed to upload file: {ex.Message}", ex);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        Küçük dosya upload'ı;
        /// </summary>
        private async Task<StorageUploadResult> UploadSmallFileAsync(StorageUploadRequest request)
        {
            var retryCount = 0;

            while (retryCount < MAX_RETRY_ATTEMPTS)
            {
                try
                {
                    var uploadObject = new Google.Apis.Storage.v1.Data.Object;
                    {
                        Bucket = request.BucketName,
                        Name = request.ObjectName,
                        ContentType = request.ContentType ?? "application/octet-stream",
                        StorageClass = GetStorageClass(request.StorageClass),
                        Metadata = request.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                        CacheControl = request.CacheControl,
                        ContentDisposition = request.ContentDisposition,
                        ContentEncoding = request.ContentEncoding,
                        ContentLanguage = request.ContentLanguage,
                        CustomTime = request.CustomTime;
                    };

                    // KMS encryption key;
                    if (!string.IsNullOrEmpty(request.KmsKeyName))
                    {
                        uploadObject.KmsKeyName = request.KmsKeyName;
                    }

                    var uploadOptions = new UploadObjectOptions;
                    {
                        ChunkSize = DEFAULT_CHUNK_SIZE,
                        PredefinedAcl = GetPredefinedAcl(request.PredefinedAcl),
                        IfGenerationMatch = request.IfGenerationMatch,
                        IfGenerationNotMatch = request.IfGenerationNotMatch,
                        IfMetagenerationMatch = request.IfMetagenerationMatch,
                        IfMetagenerationNotMatch = request.IfMetagenerationNotMatch;
                    };

                    // Progress reporter;
                    IProgress<Google.Apis.Upload.IUploadProgress> progress = new Progress<Google.Apis.Upload.IUploadProgress>(p =>
                    {
                        _logger.LogDebug("Upload progress: {Status}, {BytesSent}/{TotalBytes}",
                            p.Status, p.BytesSent, request.FileStream.Length);

                        request.ProgressCallback?.Invoke((long)p.BytesSent, request.FileStream.Length);
                    });

                    var uploadedObject = await _storageClient.UploadObjectAsync(
                        uploadObject,
                        request.FileStream,
                        uploadOptions,
                        cancellationToken: CancellationToken.None);

                    var result = new StorageUploadResult;
                    {
                        Success = uploadedObject != null,
                        BucketName = request.BucketName,
                        ObjectName = request.ObjectName,
                        Generation = uploadedObject.Generation,
                        Size = uploadedObject.Size ?? 0,
                        ETag = uploadedObject.ETag,
                        Md5Hash = uploadedObject.Md5Hash,
                        Crc32c = uploadedObject.Crc32c,
                        StorageClass = uploadedObject.StorageClass,
                        TimeCreated = uploadedObject.TimeCreated?.DateTime ?? DateTime.UtcNow,
                        Updated = uploadedObject.Updated?.DateTime ?? DateTime.UtcNow,
                        KmsKeyName = uploadedObject.KmsKeyName,
                        Metadata = uploadedObject.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                    };

                    _logger.LogInformation("File uploaded successfully to Google Cloud Storage. Bucket: {Bucket}, Object: {Object}, Generation: {Generation}",
                        request.BucketName, request.ObjectName, uploadedObject.Generation);

                    // Event publish;
                    await _eventBus.PublishAsync(new StorageUploadedEvent;
                    {
                        BucketName = request.BucketName,
                        ObjectName = request.ObjectName,
                        Size = result.Size,
                        Generation = result.Generation,
                        StorageClass = result.StorageClass,
                        UploadTime = DateTime.UtcNow;
                    });

                    // Metrics topla;
                    await CollectUploadMetricsAsync(result);

                    return result;
                }
                catch (Google.GoogleApiException gex)
                {
                    retryCount++;

                    if (retryCount >= MAX_RETRY_ATTEMPTS || !IsRetryableError(gex))
                    {
                        _logger.LogError(gex, "Google Cloud Storage upload failed after {Retries} retries", MAX_RETRY_ATTEMPTS);
                        throw;
                    }

                    _logger.LogWarning(gex, "Google Cloud Storage upload failed, retrying ({Retry}/{MaxRetries})",
                        retryCount, MAX_RETRY_ATTEMPTS);

                    await Task.Delay(RETRY_DELAY_MS * retryCount); // Exponential backoff;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during Google Cloud Storage upload");
                    throw;
                }
            }

            throw new StorageServiceException("Upload failed after maximum retries");
        }

        /// <summary>
        Büyük dosya resumable upload'ı;
        /// </summary>
        private async Task<StorageUploadResult> UploadLargeFileResumableAsync(StorageUploadRequest request)
        {
            try
            {
                _logger.LogInformation("Starting resumable upload for large file. Bucket: {Bucket}, Object: {Object}, Size: {Size}",
                    request.BucketName, request.ObjectName, request.FileStream.Length);

                var uploadObject = new Google.Apis.Storage.v1.Data.Object;
                {
                    Bucket = request.BucketName,
                    Name = request.ObjectName,
                    ContentType = request.ContentType ?? "application/octet-stream",
                    StorageClass = GetStorageClass(request.StorageClass),
                    Metadata = request.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                var uploadOptions = new UploadObjectOptions;
                {
                    UploadValidationMode = UploadValidationMode.Always,
                    PredefinedAcl = GetPredefinedAcl(request.PredefinedAcl),
                    ChunkSize = DEFAULT_CHUNK_SIZE * 4, // Büyük dosyalar için daha büyük chunk;
                    UploadType = Google.Apis.Upload.ResumableUpload.ResumableUploadType.Resumable;
                };

                // Resumable upload session başlat;
                using var sessionStream = new MemoryStream();
                var session = await _storageClient.CreateResumableUploadSessionAsync(
                    uploadObject,
                    sessionStream,
                    uploadOptions,
                    CancellationToken.None);

                var sessionUri = session;
                var uploadedBytes = 0L;
                var buffer = new byte[uploadOptions.ChunkSize.Value];

                // Dosyayı chunk'lara böl ve upload et;
                while (uploadedBytes < request.FileStream.Length)
                {
                    var bytesRead = await request.FileStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    using var chunkStream = new MemoryStream(buffer, 0, bytesRead);
                    var chunkResponse = await _storageClient.UploadResumableAsync(
                        session,
                        chunkStream,
                        new UploadResumableOptions;
                        {
                            Position = uploadedBytes;
                        });

                    uploadedBytes += bytesRead;

                    _logger.LogDebug("Uploaded chunk: {UploadedBytes}/{TotalBytes} bytes",
                        uploadedBytes, request.FileStream.Length);

                    request.ProgressCallback?.Invoke(uploadedBytes, request.FileStream.Length);
                }

                // Upload'ı tamamla;
                var uploadedObject = await _storageClient.UploadResumableAsync(
                    session,
                    request.FileStream,
                    new UploadResumableOptions;
                    {
                        Position = 0,
                        Complete = true;
                    });

                var result = new StorageUploadResult;
                {
                    Success = uploadedObject != null,
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    Generation = uploadedObject.Generation,
                    Size = uploadedObject.Size ?? 0,
                    ETag = uploadedObject.ETag,
                    Md5Hash = uploadedObject.Md5Hash,
                    Crc32c = uploadedObject.Crc32c,
                    StorageClass = uploadedObject.StorageClass,
                    TimeCreated = uploadedObject.TimeCreated?.DateTime ?? DateTime.UtcNow,
                    Updated = uploadedObject.Updated?.DateTime ?? DateTime.UtcNow,
                    IsResumableUpload = true,
                    SessionId = session.ToString()
                };

                _logger.LogInformation("Resumable upload completed successfully. Bucket: {Bucket}, Object: {Object}, Size: {Size}",
                    request.BucketName, request.ObjectName, result.Size);

                // Event publish;
                await _eventBus.PublishAsync(new StorageUploadedEvent;
                {
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    Size = result.Size,
                    Generation = result.Generation,
                    StorageClass = result.StorageClass,
                    UploadTime = DateTime.UtcNow,
                    IsResumableUpload = true;
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resumable upload failed for {Object}", request.ObjectName);
                throw;
            }
        }

        public async Task<StorageDownloadResult> DownloadFileAsync(StorageDownloadRequest request)
        {
            try
            {
                _logger.LogInformation("Downloading file from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);

                ValidateDownloadRequest(request);

                var downloadOptions = new DownloadObjectOptions;
                {
                    Generation = request.Generation,
                    IfGenerationMatch = request.IfGenerationMatch,
                    IfGenerationNotMatch = request.IfGenerationNotMatch,
                    IfMetagenerationMatch = request.IfMetagenerationMatch,
                    IfMetagenerationNotMatch = request.IfMetagenerationNotMatch,
                    ChunkSize = DEFAULT_CHUNK_SIZE;
                };

                // Byte range belirtilmişse;
                if (request.RangeStart.HasValue || request.RangeEnd.HasValue)
                {
                    downloadOptions.Range = new RangeHeaderValue(
                        request.RangeStart ?? 0,
                        request.RangeEnd);
                }

                var memoryStream = new MemoryStream();

                // Progress reporter;
                IProgress<Google.Apis.Download.IDownloadProgress> progress = null;
                if (request.ProgressCallback != null)
                {
                    progress = new Progress<Google.Apis.Download.IDownloadProgress>(p =>
                    {
                        _logger.LogDebug("Download progress: {Status}, {BytesDownloaded}",
                            p.Status, p.BytesDownloaded);

                        request.ProgressCallback?.Invoke(p.BytesDownloaded, p.Status == Google.Apis.Download.DownloadStatus.Completed ? p.BytesDownloaded : 0);
                    });
                }

                var downloadObject = await _storageClient.DownloadObjectAsync(
                    request.BucketName,
                    request.ObjectName,
                    memoryStream,
                    downloadOptions,
                    cancellationToken: CancellationToken.None);

                memoryStream.Position = 0;

                var result = new StorageDownloadResult;
                {
                    Success = downloadObject != null,
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    FileStream = memoryStream,
                    ContentType = downloadObject.ContentType,
                    Size = downloadObject.Size ?? 0,
                    Generation = downloadObject.Generation,
                    ETag = downloadObject.ETag,
                    Md5Hash = downloadObject.Md5Hash,
                    Crc32c = downloadObject.Crc32c,
                    StorageClass = downloadObject.StorageClass,
                    TimeCreated = downloadObject.TimeCreated?.DateTime ?? DateTime.MinValue,
                    Updated = downloadObject.Updated?.DateTime ?? DateTime.MinValue,
                    Metadata = downloadObject.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    CacheControl = downloadObject.CacheControl,
                    ContentDisposition = downloadObject.ContentDisposition,
                    ContentEncoding = downloadObject.ContentEncoding,
                    ContentLanguage = downloadObject.ContentLanguage,
                    DownloadTime = DateTime.UtcNow;
                };

                _logger.LogInformation("File downloaded successfully from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}, Size: {Size} bytes",
                    request.BucketName, request.ObjectName, memoryStream.Length);

                return result;
            }
            catch (Google.GoogleApiException gex) when (gex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("File not found in Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);
                throw new StorageFileNotFoundException($"File not found: {request.ObjectName}", gex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download file from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);
                throw new StorageServiceException($"Failed to download file: {ex.Message}", ex);
            }
        }

        public async Task<StorageDeleteResult> DeleteFileAsync(StorageDeleteRequest request)
        {
            try
            {
                _logger.LogInformation("Deleting file from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);

                var deleteOptions = new DeleteObjectOptions;
                {
                    Generation = request.Generation,
                    IfGenerationMatch = request.IfGenerationMatch,
                    IfGenerationNotMatch = request.IfGenerationNotMatch,
                    IfMetagenerationMatch = request.IfMetagenerationMatch,
                    IfMetagenerationNotMatch = request.IfMetagenerationNotMatch;
                };

                await _storageClient.DeleteObjectAsync(
                    request.BucketName,
                    request.ObjectName,
                    deleteOptions,
                    CancellationToken.None);

                var result = new StorageDeleteResult;
                {
                    Success = true,
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    Generation = request.Generation,
                    DeleteTime = DateTime.UtcNow;
                };

                _logger.LogInformation("File deleted successfully from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);

                // Event publish;
                await _eventBus.PublishAsync(new StorageDeletedEvent;
                {
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    DeleteTime = DateTime.UtcNow;
                });

                return result;
            }
            catch (Google.GoogleApiException gex) when (gex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("File not found for deletion. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);
                return new StorageDeleteResult;
                {
                    Success = false,
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    ErrorMessage = "File not found",
                    DeleteTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);
                throw new StorageServiceException($"Failed to delete file: {ex.Message}", ex);
            }
        }

        public async Task<StorageFileInfo> GetFileInfoAsync(string bucketName, string objectName)
        {
            try
            {
                _logger.LogDebug("Getting file info from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    bucketName, objectName);

                var storageObject = await _storageClient.GetObjectAsync(
                    bucketName,
                    objectName,
                    new GetObjectOptions;
                    {
                        IfGenerationMatch = null,
                        IfGenerationNotMatch = null,
                        IfMetagenerationMatch = null,
                        IfMetagenerationNotMatch = null;
                    },
                    CancellationToken.None);

                if (storageObject == null)
                {
                    return null;
                }

                var fileInfo = new StorageFileInfo;
                {
                    BucketName = bucketName,
                    ObjectName = objectName,
                    ContentType = storageObject.ContentType,
                    Size = storageObject.Size ?? 0,
                    Generation = storageObject.Generation,
                    ETag = storageObject.ETag,
                    Md5Hash = storageObject.Md5Hash,
                    Crc32c = storageObject.Crc32c,
                    StorageClass = storageObject.StorageClass,
                    TimeCreated = storageObject.TimeCreated?.DateTime ?? DateTime.MinValue,
                    Updated = storageObject.Updated?.DateTime ?? DateTime.MinValue,
                    TimeStorageClassUpdated = storageObject.TimeStorageClassUpdated?.DateTime,
                    RetentionExpirationTime = storageObject.RetentionExpirationTime?.DateTime,
                    Metadata = storageObject.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    CacheControl = storageObject.CacheControl,
                    ContentDisposition = storageObject.ContentDisposition,
                    ContentEncoding = storageObject.ContentEncoding,
                    ContentLanguage = storageObject.ContentLanguage,
                    KmsKeyName = storageObject.KmsKeyName,
                    TemporaryHold = storageObject.TemporaryHold ?? false,
                    EventBasedHold = storageObject.EventBasedHold ?? false,
                    Retention = storageObject.Retention,
                    CustomTime = storageObject.CustomTime?.DateTime;
                };

                return fileInfo;
            }
            catch (Google.GoogleApiException gex) when (gex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("File not found in Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    bucketName, objectName);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get file info from Google Cloud Storage. Bucket: {Bucket}, Object: {Object}",
                    bucketName, objectName);
                throw new StorageServiceException($"Failed to get file info: {ex.Message}", ex);
            }
        }

        public async Task<List<StorageObjectInfo>> ListFilesAsync(string bucketName, string prefix = null, int maxResults = 1000)
        {
            try
            {
                _logger.LogDebug("Listing files from Google Cloud Storage. Bucket: {Bucket}, Prefix: {Prefix}, MaxResults: {MaxResults}",
                    bucketName, prefix ?? "null", maxResults);

                var objects = new List<StorageObjectInfo>();
                var listOptions = new ListObjectsOptions;
                {
                    Prefix = prefix,
                    PageSize = Math.Min(maxResults, 1000) // Max page size is 1000;
                };

                await foreach (var storageObject in _storageClient.ListObjectsAsync(
                    bucketName,
                    prefix,
                    pageSize: Math.Min(maxResults, 1000)))
                {
                    if (objects.Count >= maxResults)
                        break;

                    objects.Add(new StorageObjectInfo;
                    {
                        Name = storageObject.Name,
                        BucketName = bucketName,
                        Size = storageObject.Size ?? 0,
                        TimeCreated = storageObject.TimeCreated?.DateTime ?? DateTime.MinValue,
                        Updated = storageObject.Updated?.DateTime ?? DateTime.MinValue,
                        Generation = storageObject.Generation,
                        StorageClass = storageObject.StorageClass,
                        ETag = storageObject.ETag,
                        Md5Hash = storageObject.Md5Hash,
                        Crc32c = storageObject.Crc32c;
                    });
                }

                _logger.LogDebug("Listed {Count} files from Google Cloud Storage bucket {Bucket}", objects.Count, bucketName);

                return objects;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list files from Google Cloud Storage bucket {Bucket}", bucketName);
                throw new StorageServiceException($"Failed to list files: {ex.Message}", ex);
            }
        }

        public async Task<string> GenerateSignedUrlAsync(StorageSignedUrlRequest request)
        {
            try
            {
                _logger.LogDebug("Generating signed URL. Bucket: {Bucket}, Object: {Object}, Expiry: {ExpiryHours} hours",
                    request.BucketName, request.ObjectName, request.ExpiryHours);

                // HMAC key credentials gerekiyor;
                // Gerçek implementasyonda UrlSigner kullanılır;

                var urlSigner = UrlSigner.FromServiceAccountCredential(
                    GoogleCredential.GetApplicationDefault()
                        .UnderlyingCredential as ServiceAccountCredential);

                var options = new UrlSigner.Options;
                {
                    Scheme = request.UseHttps ? UrlSigner.Scheme.Https : UrlSigner.Scheme.Http,
                    SigningVersion = UrlSigner.SigningVersion.V4;
                };

                // Response headers;
                if (request.ResponseHeaders != null && request.ResponseHeaders.Any())
                {
                    options.Headers = request.ResponseHeaders;
                }

                var signedUrl = await urlSigner.SignAsync(
                    request.BucketName,
                    request.ObjectName,
                    TimeSpan.FromHours(request.ExpiryHours),
                    request.HttpMethod.ToString().ToUpperInvariant(),
                    options);

                _logger.LogDebug("Generated signed URL for {Object}: {Url}", request.ObjectName, signedUrl);

                return signedUrl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate signed URL for {Object}", request.ObjectName);
                throw new StorageServiceException($"Failed to generate signed URL: {ex.Message}", ex);
            }
        }

        public async Task<StorageCopyResult> CopyFileAsync(StorageCopyRequest request)
        {
            try
            {
                _logger.LogInformation("Copying file in Google Cloud Storage. Source: {SourceBucket}/{SourceObject} -> Destination: {DestBucket}/{DestObject}",
                    request.SourceBucket, request.SourceObject, request.DestinationBucket, request.DestinationObject);

                var copyOptions = new CopyObjectOptions;
                {
                    SourceGeneration = request.SourceGeneration,
                    IfGenerationMatch = request.IfGenerationMatch,
                    IfGenerationNotMatch = request.IfGenerationNotMatch,
                    IfMetagenerationMatch = request.IfMetagenerationMatch,
                    IfMetagenerationNotMatch = request.IfMetagenerationNotMatch,
                    IfSourceGenerationMatch = request.IfSourceGenerationMatch,
                    IfSourceGenerationNotMatch = request.IfSourceGenerationNotMatch,
                    IfSourceMetagenerationMatch = request.IfSourceMetagenerationMatch,
                    IfSourceMetagenerationNotMatch = request.IfSourceMetagenerationNotMatch,
                    PredefinedAcl = GetPredefinedAcl(request.PredefinedAcl),
                    Projection = Google.Apis.Storage.v1.Data.ObjectsResource.CopyRequest.ProjectionEnum.Full;
                };

                // Storage class override;
                if (!string.IsNullOrEmpty(request.StorageClass))
                {
                    copyOptions.StorageClass = GetStorageClassFromString(request.StorageClass);
                }

                // KMS key;
                if (!string.IsNullOrEmpty(request.KmsKeyName))
                {
                    copyOptions.KmsKeyName = request.KmsKeyName;
                }

                var copiedObject = await _storageClient.CopyObjectAsync(
                    request.SourceBucket,
                    request.SourceObject,
                    request.DestinationBucket,
                    request.DestinationObject,
                    copyOptions,
                    CancellationToken.None);

                var result = new StorageCopyResult;
                {
                    Success = copiedObject != null,
                    SourceBucket = request.SourceBucket,
                    SourceObject = request.SourceObject,
                    DestinationBucket = request.DestinationBucket,
                    DestinationObject = request.DestinationObject,
                    Generation = copiedObject.Generation,
                    Size = copiedObject.Size ?? 0,
                    StorageClass = copiedObject.StorageClass,
                    CopyTime = DateTime.UtcNow,
                    SourceGeneration = request.SourceGeneration;
                };

                _logger.LogInformation("File copied successfully in Google Cloud Storage. Source: {SourceObject} -> Destination: {DestObject}",
                    request.SourceObject, request.DestinationObject);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy file in Google Cloud Storage. Source: {SourceObject}, Destination: {DestObject}",
                    request.SourceObject, request.DestinationObject);
                throw new StorageServiceException($"Failed to copy file: {ex.Message}", ex);
            }
        }

        public async Task<StorageMoveResult> MoveFileAsync(StorageMoveRequest request)
        {
            try
            {
                _logger.LogInformation("Moving file in Google Cloud Storage. Source: {SourceObject} -> Destination: {DestObject}",
                    request.SourceObject, request.DestinationObject);

                // Önce kopyala;
                var copyRequest = new StorageCopyRequest;
                {
                    SourceBucket = request.SourceBucket,
                    SourceObject = request.SourceObject,
                    DestinationBucket = request.DestinationBucket,
                    DestinationObject = request.DestinationObject,
                    StorageClass = request.StorageClass,
                    PredefinedAcl = request.PredefinedAcl,
                    KmsKeyName = request.KmsKeyName;
                };

                var copyResult = await CopyFileAsync(copyRequest);

                if (!copyResult.Success)
                {
                    throw new StorageServiceException("Copy operation failed during move");
                }

                // Sonra source'u sil;
                var deleteRequest = new StorageDeleteRequest;
                {
                    BucketName = request.SourceBucket,
                    ObjectName = request.SourceObject;
                };

                var deleteResult = await DeleteFileAsync(deleteRequest);

                var result = new StorageMoveResult;
                {
                    Success = copyResult.Success && deleteResult.Success,
                    SourceBucket = request.SourceBucket,
                    SourceObject = request.SourceObject,
                    DestinationBucket = request.DestinationBucket,
                    DestinationObject = request.DestinationObject,
                    CopyResult = copyResult,
                    DeleteResult = deleteResult,
                    MoveTime = DateTime.UtcNow;
                };

                _logger.LogInformation("File moved successfully in Google Cloud Storage. Source: {SourceObject} -> Destination: {DestObject}",
                    request.SourceObject, request.DestinationObject);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to move file in Google Cloud Storage. Source: {SourceObject}, Destination: {DestObject}",
                    request.SourceObject, request.DestinationObject);
                throw new StorageServiceException($"Failed to move file: {ex.Message}", ex);
            }
        }

        public async Task<StorageResumableUpload> StartResumableUploadAsync(StorageResumableUploadRequest request)
        {
            try
            {
                _logger.LogInformation("Starting resumable upload. Bucket: {Bucket}, Object: {Object}",
                    request.BucketName, request.ObjectName);

                var uploadObject = new Google.Apis.Storage.v1.Data.Object;
                {
                    Bucket = request.BucketName,
                    Name = request.ObjectName,
                    ContentType = request.ContentType,
                    Metadata = request.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                var uploadOptions = new UploadObjectOptions;
                {
                    UploadValidationMode = UploadValidationMode.Always,
                    PredefinedAcl = GetPredefinedAcl(request.PredefinedAcl),
                    ChunkSize = request.ChunkSize ?? DEFAULT_CHUNK_SIZE,
                    UploadType = Google.Apis.Upload.ResumableUpload.ResumableUploadType.Resumable;
                };

                using var sessionStream = new MemoryStream();
                var session = await _storageClient.CreateResumableUploadSessionAsync(
                    uploadObject,
                    sessionStream,
                    uploadOptions,
                    CancellationToken.None);

                var resumableUpload = new StorageResumableUpload;
                {
                    SessionId = session.ToString(),
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    Initiated = DateTime.UtcNow,
                    Chunks = new List<StorageUploadChunk>(),
                    IsCompleted = false;
                };

                // Cache'e ekle;
                _resumableUploads[session.ToString()] = resumableUpload;

                _logger.LogInformation("Resumable upload started. SessionId: {SessionId}, Bucket: {Bucket}, Object: {Object}",
                    session, request.BucketName, request.ObjectName);

                return resumableUpload;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start resumable upload for {Object}", request.ObjectName);
                throw new StorageServiceException($"Failed to start resumable upload: {ex.Message}", ex);
            }
        }

        public async Task<StorageUploadResult> CompleteResumableUploadAsync(StorageResumableUploadCompleteRequest request)
        {
            try
            {
                _logger.LogInformation("Completing resumable upload. SessionId: {SessionId}, Chunks: {ChunksCount}",
                    request.SessionId, request.Chunks.Count);

                if (!_resumableUploads.TryGetValue(request.SessionId, out var resumableUpload))
                {
                    throw new StorageServiceException($"Resumable upload not found: {request.SessionId}");
                }

                // Session'ı al;
                var sessionUri = new Uri(request.SessionId);

                // Tüm chunk'ları upload et;
                foreach (var chunk in request.Chunks.OrderBy(c => c.ChunkIndex))
                {
                    using var chunkStream = new MemoryStream(chunk.Data);
                    var chunkResponse = await _storageClient.UploadResumableAsync(
                        sessionUri,
                        chunkStream,
                        new UploadResumableOptions;
                        {
                            Position = chunk.Offset,
                            Length = chunk.Data.Length;
                        });

                    _logger.LogDebug("Uploaded chunk {ChunkIndex} for session {SessionId}",
                        chunk.ChunkIndex, request.SessionId);
                }

                // Upload'ı tamamla;
                var uploadedObject = await _storageClient.UploadResumableAsync(
                    sessionUri,
                    Stream.Null, // Empty stream for completion;
                    new UploadResumableOptions;
                    {
                        Complete = true;
                    });

                // Cache'ten kaldır;
                _resumableUploads.TryRemove(request.SessionId, out _);

                var result = new StorageUploadResult;
                {
                    Success = uploadedObject != null,
                    BucketName = resumableUpload.BucketName,
                    ObjectName = resumableUpload.ObjectName,
                    Generation = uploadedObject.Generation,
                    Size = uploadedObject.Size ?? 0,
                    ETag = uploadedObject.ETag,
                    Md5Hash = uploadedObject.Md5Hash,
                    Crc32c = uploadedObject.Crc32c,
                    StorageClass = uploadedObject.StorageClass,
                    TimeCreated = uploadedObject.TimeCreated?.DateTime ?? DateTime.UtcNow,
                    Updated = uploadedObject.Updated?.DateTime ?? DateTime.UtcNow,
                    IsResumableUpload = true,
                    SessionId = request.SessionId,
                    ChunksCount = request.Chunks.Count;
                };

                _logger.LogInformation("Resumable upload completed. SessionId: {SessionId}, Generation: {Generation}",
                    request.SessionId, uploadedObject.Generation);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete resumable upload {SessionId}", request.SessionId);
                throw new StorageServiceException($"Failed to complete resumable upload: {ex.Message}", ex);
            }
        }

        public async Task<StorageBucketResult> CreateBucketAsync(StorageBucketRequest request)
        {
            try
            {
                _logger.LogInformation("Creating Google Cloud Storage bucket: {BucketName} in location: {Location}",
                    request.BucketName, request.Location);

                // Bucket adı validation;
                if (!IsValidBucketName(request.BucketName))
                {
                    throw new ArgumentException($"Invalid bucket name: {request.BucketName}");
                }

                var bucket = new Google.Apis.Storage.v1.Data.Bucket;
                {
                    Name = request.BucketName,
                    Location = request.Location,
                    StorageClass = GetStorageClass(request.StorageClass),
                    Versioning = new Google.Apis.Storage.v1.Data.Bucket.VersioningData;
                    {
                        Enabled = request.EnableVersioning;
                    },
                    Labels = request.Labels?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    IamConfiguration = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData;
                    {
                        UniformBucketLevelAccess = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData.UniformBucketLevelAccessData;
                        {
                            Enabled = request.UniformBucketLevelAccess;
                        }
                    },
                    TimeCreated = DateTime.UtcNow;
                };

                // Lifecycle rules;
                if (request.LifecycleRules != null && request.LifecycleRules.Any())
                {
                    bucket.Lifecycle = new Google.Apis.Storage.v1.Data.Bucket.LifecycleData;
                    {
                        Rule = request.LifecycleRules.Select(r => new Google.Apis.Storage.v1.Data.Bucket.LifecycleData.RuleData;
                        {
                            Action = new Google.Apis.Storage.v1.Data.Bucket.LifecycleData.RuleData.ActionData;
                            {
                                Type = r.ActionType,
                                StorageClass = r.StorageClass;
                            },
                            Condition = new Google.Apis.Storage.v1.Data.Bucket.LifecycleData.RuleData.ConditionData;
                            {
                                Age = r.AgeDays,
                                NumberOfNewerVersions = r.NumberOfNewerVersions,
                                IsLive = r.IsLive,
                                MatchesStorageClass = r.MatchesStorageClasses,
                                DaysSinceCustomTime = r.DaysSinceCustomTime,
                                CustomTimeBefore = r.CustomTimeBefore?.ToString("yyyy-MM-dd"),
                                DaysSinceNoncurrentTime = r.DaysSinceNoncurrentTime,
                                NoncurrentTimeBefore = r.NoncurrentTimeBefore?.ToString("yyyy-MM-dd")
                            }
                        }).ToList()
                    };
                }

                // CORS configuration;
                if (request.CorsRules != null && request.CorsRules.Any())
                {
                    bucket.Cors = request.CorsRules.Select(r => new Google.Apis.Storage.v1.Data.Bucket.CorsData;
                    {
                        Origin = r.Origins,
                        Method = r.Methods,
                        ResponseHeader = r.ResponseHeaders,
                        MaxAgeSeconds = r.MaxAgeSeconds;
                    }).ToList();
                }

                var createdBucket = await _storageClient.CreateBucketAsync(
                    _appConfig.GcpProjectId,
                    bucket,
                    new CreateBucketOptions;
                    {
                        PredefinedAcl = GetPredefinedBucketAcl(request.PredefinedAcl),
                        PredefinedDefaultObjectAcl = GetPredefinedObjectAcl(request.PredefinedDefaultObjectAcl)
                    },
                    CancellationToken.None);

                // Bucket info'yu cache'e ekle;
                _bucketCache[request.BucketName] = createdBucket;

                var result = new StorageBucketResult;
                {
                    Success = createdBucket != null,
                    BucketName = request.BucketName,
                    Location = request.Location,
                    StorageClass = createdBucket.StorageClass,
                    TimeCreated = createdBucket.TimeCreated?.DateTime ?? DateTime.UtcNow,
                    ProjectNumber = createdBucket.ProjectNumber,
                    Id = createdBucket.Id,
                    SelfLink = createdBucket.SelfLink;
                };

                _logger.LogInformation("Google Cloud Storage bucket created successfully: {BucketName}", request.BucketName);

                return result;
            }
            catch (Google.GoogleApiException gex) when (gex.HttpStatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogWarning("Google Cloud Storage bucket already exists: {BucketName}", request.BucketName);
                return new StorageBucketResult;
                {
                    Success = false,
                    BucketName = request.BucketName,
                    ErrorMessage = "Bucket already exists"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Google Cloud Storage bucket: {BucketName}", request.BucketName);
                throw new StorageServiceException($"Failed to create bucket: {ex.Message}", ex);
            }
        }

        public async Task<StorageBucketResult> DeleteBucketAsync(string bucketName)
        {
            try
            {
                _logger.LogInformation("Deleting Google Cloud Storage bucket: {BucketName}", bucketName);

                // Bucket'ın boş olup olmadığını kontrol et;
                var objects = await ListFilesAsync(bucketName, maxResults: 1);
                if (objects.Any())
                {
                    throw new StorageServiceException($"Bucket {bucketName} is not empty");
                }

                await _storageClient.DeleteBucketAsync(
                    bucketName,
                    new DeleteBucketOptions;
                    {
                        IfMetagenerationMatch = null;
                    },
                    CancellationToken.None);

                // Cache'ten kaldır;
                _bucketCache.TryRemove(bucketName, out _);

                var result = new StorageBucketResult;
                {
                    Success = true,
                    BucketName = bucketName,
                    DeletionTime = DateTime.UtcNow;
                };

                _logger.LogInformation("Google Cloud Storage bucket deleted successfully: {BucketName}", bucketName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete Google Cloud Storage bucket: {BucketName}", bucketName);
                throw new StorageServiceException($"Failed to delete bucket: {ex.Message}", ex);
            }
        }

        public async Task SetBucketIamPolicyAsync(string bucketName, StorageIamPolicy policy)
        {
            try
            {
                _logger.LogInformation("Setting bucket IAM policy for: {BucketName}", bucketName);

                var iamPolicy = await _storageClient.GetBucketIamPolicyAsync(
                    bucketName,
                    null,
                    CancellationToken.None);

                // Mevcut policy'yi güncelle veya yeni oluştur;
                if (iamPolicy == null)
                {
                    iamPolicy = new Google.Apis.Storage.v1.Data.Policy;
                    {
                        Bindings = new List<Google.Apis.Storage.v1.Data.Binding>()
                    };
                }

                // Bindings ekle/güncelle;
                foreach (var binding in policy.Bindings)
                {
                    var existingBinding = iamPolicy.Bindings.FirstOrDefault(b => b.Role == binding.Role);
                    if (existingBinding != null)
                    {
                        existingBinding.Members = binding.Members;
                    }
                    else;
                    {
                        iamPolicy.Bindings.Add(new Google.Apis.Storage.v1.Data.Binding;
                        {
                            Role = binding.Role,
                            Members = binding.Members,
                            Condition = binding.Condition != null ? new Google.Apis.Storage.v1.Data.Expr;
                            {
                                Title = binding.Condition.Title,
                                Description = binding.Condition.Description,
                                Expression = binding.Condition.Expression;
                            } : null;
                        });
                    }
                }

                await _storageClient.SetBucketIamPolicyAsync(
                    bucketName,
                    iamPolicy,
                    null,
                    CancellationToken.None);

                _logger.LogInformation("Bucket IAM policy set successfully for: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set bucket IAM policy for: {BucketName}", bucketName);
                throw new StorageServiceException($"Failed to set bucket IAM policy: {ex.Message}", ex);
            }
        }

        public async Task ConfigureCorsAsync(string bucketName, List<StorageCorsRule> rules)
        {
            try
            {
                _logger.LogInformation("Configuring CORS for bucket: {BucketName}", bucketName);

                var bucket = await GetBucketAsync(bucketName);
                if (bucket == null)
                {
                    throw new StorageServiceException($"Bucket not found: {bucketName}");
                }

                bucket.Cors = rules.Select(r => new Google.Apis.Storage.v1.Data.Bucket.CorsData;
                {
                    Origin = r.Origins,
                    Method = r.Methods,
                    ResponseHeader = r.ResponseHeaders,
                    MaxAgeSeconds = r.MaxAgeSeconds;
                }).ToList();

                await _storageClient.UpdateBucketAsync(
                    bucket,
                    new UpdateBucketOptions;
                    {
                        IfMetagenerationMatch = bucket.Metageneration;
                    },
                    CancellationToken.None);

                _logger.LogInformation("CORS configured successfully for bucket: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure CORS for bucket: {BucketName}", bucketName);
                throw new StorageServiceException($"Failed to configure CORS: {ex.Message}", ex);
            }
        }

        public async Task ConfigureLifecycleAsync(string bucketName, List<StorageLifecycleRule> rules)
        {
            try
            {
                _logger.LogInformation("Configuring lifecycle rules for bucket: {BucketName}", bucketName);

                var bucket = await GetBucketAsync(bucketName);
                if (bucket == null)
                {
                    throw new StorageServiceException($"Bucket not found: {bucketName}");
                }

                bucket.Lifecycle = new Google.Apis.Storage.v1.Data.Bucket.LifecycleData;
                {
                    Rule = rules.Select(r => new Google.Apis.Storage.v1.Data.Bucket.LifecycleData.RuleData;
                    {
                        Action = new Google.Apis.Storage.v1.Data.Bucket.LifecycleData.RuleData.ActionData;
                        {
                            Type = r.ActionType,
                            StorageClass = r.StorageClass;
                        },
                        Condition = new Google.Apis.Storage.v1.Data.Bucket.LifecycleData.RuleData.ConditionData;
                        {
                            Age = r.AgeDays,
                            NumberOfNewerVersions = r.NumberOfNewerVersions,
                            IsLive = r.IsLive,
                            MatchesStorageClass = r.MatchesStorageClasses,
                            DaysSinceCustomTime = r.DaysSinceCustomTime,
                            CustomTimeBefore = r.CustomTimeBefore?.ToString("yyyy-MM-dd"),
                            DaysSinceNoncurrentTime = r.DaysSinceNoncurrentTime,
                            NoncurrentTimeBefore = r.NoncurrentTimeBefore?.ToString("yyyy-MM-dd")
                        }
                    }).ToList()
                };

                await _storageClient.UpdateBucketAsync(
                    bucket,
                    new UpdateBucketOptions;
                    {
                        IfMetagenerationMatch = bucket.Metageneration;
                    },
                    CancellationToken.None);

                _logger.LogInformation("Lifecycle rules configured for bucket: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure lifecycle rules for bucket: {BucketName}", bucketName);
                throw new StorageServiceException($"Failed to configure lifecycle rules: {ex.Message}", ex);
            }
        }

        public async Task EnableVersioningAsync(string bucketName)
        {
            try
            {
                _logger.LogInformation("Enabling versioning for bucket: {BucketName}", bucketName);

                var bucket = await GetBucketAsync(bucketName);
                if (bucket == null)
                {
                    throw new StorageServiceException($"Bucket not found: {bucketName}");
                }

                bucket.Versioning = new Google.Apis.Storage.v1.Data.Bucket.VersioningData;
                {
                    Enabled = true;
                };

                await _storageClient.UpdateBucketAsync(
                    bucket,
                    new UpdateBucketOptions;
                    {
                        IfMetagenerationMatch = bucket.Metageneration;
                    },
                    CancellationToken.None);

                _logger.LogInformation("Versioning enabled for bucket: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enable versioning for bucket: {BucketName}", bucketName);
                throw new StorageServiceException($"Failed to enable versioning: {ex.Message}", ex);
            }
        }

        public async Task SetObjectAclAsync(string bucketName, string objectName, StorageAcl acl)
        {
            try
            {
                _logger.LogInformation("Setting object ACL for: {Bucket}/{Object}", bucketName, objectName);

                var storageAcl = acl.Entries.Select(e => new Google.Apis.Storage.v1.Data.ObjectAccessControl;
                {
                    Entity = e.Entity,
                    Role = e.Role,
                    Email = e.Email,
                    Domain = e.Domain,
                    ProjectTeam = e.ProjectTeam != null ? new Google.Apis.Storage.v1.Data.ObjectAccessControl.ProjectTeamData;
                    {
                        ProjectNumber = e.ProjectTeam.ProjectNumber,
                        Team = e.ProjectTeam.Team;
                    } : null;
                }).ToList();

                await _storageClient.UpdateObjectAclAsync(
                    bucketName,
                    objectName,
                    storageAcl,
                    null,
                    CancellationToken.None);

                _logger.LogInformation("Object ACL set for: {Bucket}/{Object}", bucketName, objectName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set object ACL for: {Bucket}/{Object}", bucketName, objectName);
                throw new StorageServiceException($"Failed to set object ACL: {ex.Message}", ex);
            }
        }

        public async Task SetBucketAclAsync(string bucketName, StorageAcl acl)
        {
            try
            {
                _logger.LogInformation("Setting bucket ACL for: {Bucket}", bucketName);

                var storageAcl = acl.Entries.Select(e => new Google.Apis.Storage.v1.Data.BucketAccessControl;
                {
                    Entity = e.Entity,
                    Role = e.Role,
                    Email = e.Email,
                    Domain = e.Domain,
                    ProjectTeam = e.ProjectTeam != null ? new Google.Apis.Storage.v1.Data.BucketAccessControl.ProjectTeamData;
                    {
                        ProjectNumber = e.ProjectTeam.ProjectNumber,
                        Team = e.ProjectTeam.Team;
                    } : null;
                }).ToList();

                await _storageClient.UpdateBucketAclAsync(
                    bucketName,
                    storageAcl,
                    null,
                    CancellationToken.None);

                _logger.LogInformation("Bucket ACL set for: {Bucket}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set bucket ACL for: {Bucket}", bucketName);
                throw new StorageServiceException($"Failed to set bucket ACL: {ex.Message}", ex);
            }
        }

        public async Task<long> GetFileSizeAsync(string bucketName, string objectName)
        {
            try
            {
                var fileInfo = await GetFileInfoAsync(bucketName, objectName);
                return fileInfo?.Size ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get file size for {Bucket}/{Object}", bucketName, objectName);
                throw;
            }
        }

        public async Task<string> GetFileContentTypeAsync(string bucketName, string objectName)
        {
            try
            {
                var fileInfo = await GetFileInfoAsync(bucketName, objectName);
                return fileInfo?.ContentType;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get content type for {Bucket}/{Object}", bucketName, objectName);
                throw;
            }
        }

        public async Task<StorageMetadataResult> UpdateObjectMetadataAsync(StorageMetadataUpdateRequest request)
        {
            try
            {
                _logger.LogInformation("Updating object metadata for: {Bucket}/{Object}",
                    request.BucketName, request.ObjectName);

                var storageObject = await _storageClient.GetObjectAsync(
                    request.BucketName,
                    request.ObjectName,
                    null,
                    CancellationToken.None);

                if (storageObject == null)
                {
                    throw new StorageServiceException($"Object not found: {request.ObjectName}");
                }

                // Metadata'yi güncelle;
                if (request.Metadata != null)
                {
                    storageObject.Metadata = request.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }

                // Diğer fields'ı güncelle;
                if (!string.IsNullOrEmpty(request.CacheControl))
                    storageObject.CacheControl = request.CacheControl;

                if (!string.IsNullOrEmpty(request.ContentDisposition))
                    storageObject.ContentDisposition = request.ContentDisposition;

                if (!string.IsNullOrEmpty(request.ContentEncoding))
                    storageObject.ContentEncoding = request.ContentEncoding;

                if (!string.IsNullOrEmpty(request.ContentLanguage))
                    storageObject.ContentLanguage = request.ContentLanguage;

                if (request.ContentType != null)
                    storageObject.ContentType = request.ContentType;

                if (request.CustomTime.HasValue)
                    storageObject.CustomTime = request.CustomTime.Value;

                if (!string.IsNullOrEmpty(request.StorageClass))
                    storageObject.StorageClass = GetStorageClassFromString(request.StorageClass);

                var updatedObject = await _storageClient.UpdateObjectAsync(
                    storageObject,
                    new UpdateObjectOptions;
                    {
                        IfMetagenerationMatch = request.IfMetagenerationMatch,
                        IfMetagenerationNotMatch = request.IfMetagenerationNotMatch,
                        PredefinedAcl = GetPredefinedAcl(request.PredefinedAcl)
                    },
                    CancellationToken.None);

                var result = new StorageMetadataResult;
                {
                    Success = updatedObject != null,
                    BucketName = request.BucketName,
                    ObjectName = request.ObjectName,
                    Generation = updatedObject.Generation,
                    Metageneration = updatedObject.Metageneration ?? 0,
                    UpdateTime = DateTime.UtcNow,
                    Metadata = updatedObject.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                _logger.LogInformation("Object metadata updated for: {Bucket}/{Object}",
                    request.BucketName, request.ObjectName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update object metadata for {Bucket}/{Object}",
                    request.BucketName, request.ObjectName);
                throw new StorageServiceException($"Failed to update object metadata: {ex.Message}", ex);
            }
        }

        public async Task<StorageMetadataResult> UpdateBucketMetadataAsync(string bucketName, Dictionary<string, string> metadata)
        {
            try
            {
                _logger.LogInformation("Updating bucket metadata for: {Bucket}", bucketName);

                var bucket = await GetBucketAsync(bucketName);
                if (bucket == null)
                {
                    throw new StorageServiceException($"Bucket not found: {bucketName}");
                }

                // Labels'i güncelle (GCS'de metadata yerine labels kullanılır)
                if (metadata != null)
                {
                    bucket.Labels = metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }

                var updatedBucket = await _storageClient.UpdateBucketAsync(
                    bucket,
                    new UpdateBucketOptions;
                    {
                        IfMetagenerationMatch = bucket.Metageneration;
                    },
                    CancellationToken.None);

                var result = new StorageMetadataResult;
                {
                    Success = updatedBucket != null,
                    BucketName = bucketName,
                    Metageneration = updatedBucket.Metageneration ?? 0,
                    UpdateTime = DateTime.UtcNow,
                    Metadata = updatedBucket.Labels?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                _logger.LogInformation("Bucket metadata updated for: {Bucket}", bucketName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update bucket metadata for {Bucket}", bucketName);
                throw new StorageServiceException($"Failed to update bucket metadata: {ex.Message}", ex);
            }
        }

        public async Task<StorageBatchResult> ProcessBatchAsync(StorageBatchRequest request)
        {
            try
            {
                _logger.LogInformation("Processing batch operation with {Count} items", request.Operations.Count);

                var results = new List<StorageBatchOperationResult>();
                var tasks = new List<Task>();

                foreach (var operation in request.Operations)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            StorageBatchOperationResult result = null;

                            switch (operation.Type)
                            {
                                case StorageBatchOperationType.Upload:
                                    var uploadResult = await UploadFileAsync(operation.UploadRequest);
                                    result = new StorageBatchOperationResult;
                                    {
                                        OperationId = operation.Id,
                                        Type = operation.Type,
                                        Success = uploadResult.Success,
                                        Result = uploadResult,
                                        Error = null;
                                    };
                                    break;

                                case StorageBatchOperationType.Download:
                                    var downloadResult = await DownloadFileAsync(operation.DownloadRequest);
                                    result = new StorageBatchOperationResult;
                                    {
                                        OperationId = operation.Id,
                                        Type = operation.Type,
                                        Success = downloadResult.Success,
                                        Result = downloadResult,
                                        Error = null;
                                    };
                                    break;

                                case StorageBatchOperationType.Delete:
                                    var deleteResult = await DeleteFileAsync(operation.DeleteRequest);
                                    result = new StorageBatchOperationResult;
                                    {
                                        OperationId = operation.Id,
                                        Type = operation.Type,
                                        Success = deleteResult.Success,
                                        Result = deleteResult,
                                        Error = null;
                                    };
                                    break;

                                case StorageBatchOperationType.Copy:
                                    var copyResult = await CopyFileAsync(operation.CopyRequest);
                                    result = new StorageBatchOperationResult;
                                    {
                                        OperationId = operation.Id,
                                        Type = operation.Type,
                                        Success = copyResult.Success,
                                        Result = copyResult,
                                        Error = null;
                                    };
                                    break;
                            }

                            lock (results)
                            {
                                results.Add(result);
                            }
                        }
                        catch (Exception ex)
                        {
                            var errorResult = new StorageBatchOperationResult;
                            {
                                OperationId = operation.Id,
                                Type = operation.Type,
                                Success = false,
                                Result = null,
                                Error = ex.Message;
                            };

                            lock (results)
                            {
                                results.Add(errorResult);
                            }
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                var batchResult = new StorageBatchResult;
                {
                    TotalOperations = request.Operations.Count,
                    SuccessfulOperations = results.Count(r => r.Success),
                    FailedOperations = results.Count(r => !r.Success),
                    OperationResults = results,
                    CompletionTime = DateTime.UtcNow;
                };

                _logger.LogInformation("Batch operation completed: {Success}/{Total} successful",
                    batchResult.SuccessfulOperations, batchResult.TotalOperations);

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process batch operation");
                throw new StorageServiceException($"Failed to process batch operation: {ex.Message}", ex);
            }
        }

        public async Task<StorageComposeResult> ComposeObjectsAsync(StorageComposeRequest request)
        {
            try
            {
                _logger.LogInformation("Composing objects in bucket: {Bucket}, Source objects: {Count}",
                    request.DestinationBucket, request.SourceObjects.Count);

                var sourceObjects = request.SourceObjects.Select(so =>
                    new Google.Apis.Storage.v1.Data.ComposeRequest.SourceObjectsData;
                    {
                        Name = so.ObjectName,
                        Generation = so.Generation,
                        ObjectPreconditions = so.IfGenerationMatch.HasValue ?
                            new Google.Apis.Storage.v1.Data.ComposeRequest.SourceObjectsData.ObjectPreconditionsData;
                            {
                                IfGenerationMatch = so.IfGenerationMatch.Value;
                            } : null;
                    }).ToList();

                var destinationObject = new Google.Apis.Storage.v1.Data.Object;
                {
                    Bucket = request.DestinationBucket,
                    Name = request.DestinationObject,
                    ContentType = request.ContentType,
                    Metadata = request.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                var composedObject = await _storageClient.ComposeObjectAsync(
                    sourceObjects,
                    destinationObject,
                    new ComposeObjectOptions;
                    {
                        IfGenerationMatch = request.IfGenerationMatch,
                        IfMetagenerationMatch = request.IfMetagenerationMatch,
                        KmsKeyName = request.KmsKeyName,
                        PredefinedAcl = GetPredefinedAcl(request.PredefinedAcl)
                    },
                    CancellationToken.None);

                var result = new StorageComposeResult;
                {
                    Success = composedObject != null,
                    DestinationBucket = request.DestinationBucket,
                    DestinationObject = request.DestinationObject,
                    SourceObjects = request.SourceObjects,
                    Generation = composedObject.Generation,
                    Size = composedObject.Size ?? 0,
                    ComposeTime = DateTime.UtcNow;
                };

                _logger.LogInformation("Objects composed successfully. Destination: {DestinationObject}, Size: {Size}",
                    request.DestinationObject, result.Size);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compose objects to {DestinationObject}", request.DestinationObject);
                throw new StorageServiceException($"Failed to compose objects: {ex.Message}", ex);
            }
        }

        public async Task<StorageRewriteResult> RewriteObjectAsync(StorageRewriteRequest request)
        {
            try
            {
                _logger.LogInformation("Rewriting object: {SourceObject} -> {DestinationObject}",
                    request.SourceObject, request.DestinationObject);

                var rewriteOperation = _storageClient.RewriteObject(
                    request.SourceBucket,
                    request.SourceObject,
                    request.DestinationBucket,
                    request.DestinationObject,
                    sourceGeneration: request.SourceGeneration,
                    destinationPredefinedAcl: GetPredefinedAcl(request.DestinationPredefinedAcl),
                    destinationKmsKeyName: request.DestinationKmsKeyName);

                // Rewrite progress'ı takip et;
                long totalBytesRewritten = 0;
                long totalBytes = 0;

                while (!rewriteOperation.Complete)
                {
                    await rewriteOperation.RewriteAsync();

                    totalBytesRewritten = rewriteOperation.TotalBytesRewritten;
                    totalBytes = rewriteOperation.TotalBytes;

                    _logger.LogDebug("Rewrite progress: {RewrittenBytes}/{TotalBytes} bytes",
                        totalBytesRewritten, totalBytes);

                    request.ProgressCallback?.Invoke(totalBytesRewritten, totalBytes);
                }

                var resultObject = rewriteOperation.Result;

                var result = new StorageRewriteResult;
                {
                    Success = resultObject != null,
                    SourceBucket = request.SourceBucket,
                    SourceObject = request.SourceObject,
                    DestinationBucket = request.DestinationBucket,
                    DestinationObject = request.DestinationObject,
                    Generation = resultObject.Generation,
                    Size = resultObject.Size ?? 0,
                    StorageClass = resultObject.StorageClass,
                    RewriteTime = DateTime.UtcNow,
                    TotalBytesRewritten = totalBytesRewritten,
                    TotalBytes = totalBytes;
                };

                _logger.LogInformation("Object rewritten successfully: {SourceObject} -> {DestinationObject}",
                    request.SourceObject, request.DestinationObject);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rewrite object {SourceObject}", request.SourceObject);
                throw new StorageServiceException($"Failed to rewrite object: {ex.Message}", ex);
            }
        }

        public async Task SetObjectRetentionAsync(string bucketName, string objectName, DateTimeOffset retentionUntil)
        {
            try
            {
                _logger.LogInformation("Setting object retention for: {Bucket}/{Object} until {RetentionUntil}",
                    bucketName, objectName, retentionUntil);

                var storageObject = await _storageClient.GetObjectAsync(
                    bucketName,
                    objectName,
                    null,
                    CancellationToken.None);

                if (storageObject == null)
                {
                    throw new StorageServiceException($"Object not found: {objectName}");
                }

                storageObject.Retention = new Google.Apis.Storage.v1.Data.Object.RetentionData;
                {
                    RetentionUntil = retentionUntil.DateTime;
                };

                await _storageClient.UpdateObjectAsync(
                    storageObject,
                    new UpdateObjectOptions;
                    {
                        IfMetagenerationMatch = storageObject.Metageneration;
                    },
                    CancellationToken.None);

                _logger.LogInformation("Object retention set for: {Bucket}/{Object}", bucketName, objectName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set object retention for {Bucket}/{Object}", bucketName, objectName);
                throw new StorageServiceException($"Failed to set object retention: {ex.Message}", ex);
            }
        }

        public async Task SetObjectHoldAsync(string bucketName, string objectName, bool temporaryHold, bool eventBasedHold)
        {
            try
            {
                _logger.LogInformation("Setting object hold for: {Bucket}/{Object}, Temporary: {Temporary}, EventBased: {EventBased}",
                    bucketName, objectName, temporaryHold, eventBasedHold);

                var storageObject = await _storageClient.GetObjectAsync(
                    bucketName,
                    objectName,
                    null,
                    CancellationToken.None);

                if (storageObject == null)
                {
                    throw new StorageServiceException($"Object not found: {objectName}");
                }

                storageObject.TemporaryHold = temporaryHold;
                storageObject.EventBasedHold = eventBasedHold;

                await _storageClient.UpdateObjectAsync(
                    storageObject,
                    new UpdateObjectOptions;
                    {
                        IfMetagenerationMatch = storageObject.Metageneration;
                    },
                    CancellationToken.None);

                _logger.LogInformation("Object hold set for: {Bucket}/{Object}", bucketName, objectName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set object hold for {Bucket}/{Object}", bucketName, objectName);
                throw new StorageServiceException($"Failed to set object hold: {ex.Message}", ex);
            }
        }

        public async Task<StorageHmacKey> CreateHmacKeyAsync(string serviceAccountEmail)
        {
            try
            {
                _logger.LogInformation("Creating HMAC key for service account: {ServiceAccount}", serviceAccountEmail);

                var hmacKey = await _storageClient.CreateHmacKeyAsync(
                    _appConfig.GcpProjectId,
                    serviceAccountEmail,
                    null,
                    CancellationToken.None);

                var result = new StorageHmacKey;
                {
                    AccessId = hmacKey.Metadata.AccessId,
                    Secret = hmacKey.Secret,
                    ServiceAccountEmail = serviceAccountEmail,
                    State = hmacKey.Metadata.State,
                    TimeCreated = hmacKey.Metadata.TimeCreated?.DateTime ?? DateTime.UtcNow,
                    Updated = hmacKey.Metadata.Updated?.DateTime ?? DateTime.UtcNow;
                };

                _logger.LogInformation("HMAC key created for service account: {ServiceAccount}", serviceAccountEmail);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create HMAC key for service account: {ServiceAccount}", serviceAccountEmail);
                throw new StorageServiceException($"Failed to create HMAC key: {ex.Message}", ex);
            }
        }

        public async Task<StorageNotificationChannel> CreateNotificationChannelAsync(StorageNotificationRequest request)
        {
            try
            {
                _logger.LogInformation("Creating notification channel for bucket: {Bucket}", request.BucketName);

                var notificationChannel = new Google.Apis.Storage.v1.Data.Channel;
                {
                    Address = request.Address,
                    Type = request.Type,
                    Id = Guid.NewGuid().ToString(),
                    Token = request.Token,
                    Expiration = request.ExpirationTime?.ToUnixTimeMilliseconds().ToString(),
                    Params = request.Params?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                var createdChannel = await _storageClient.CreateNotificationChannelAsync(
                    request.BucketName,
                    notificationChannel,
                    null,
                    CancellationToken.None);

                var result = new StorageNotificationChannel;
                {
                    Id = createdChannel.Id,
                    ResourceId = createdChannel.ResourceId,
                    ResourceUri = createdChannel.ResourceUri,
                    Token = createdChannel.Token,
                    Expiration = createdChannel.Expiration != null ?
                        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(createdChannel.Expiration)) : null,
                    Address = createdChannel.Address,
                    Type = createdChannel.Type,
                    Params = createdChannel.Params?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                _logger.LogInformation("Notification channel created for bucket: {Bucket}, ChannelId: {ChannelId}",
                    request.BucketName, result.Id);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create notification channel for bucket: {Bucket}", request.BucketName);
                throw new StorageServiceException($"Failed to create notification channel: {ex.Message}", ex);
            }
        }

        #region Helper Methods;

        private void ValidateUploadRequest(StorageUploadRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.BucketName))
                throw new ArgumentException("Bucket name is required", nameof(request.BucketName));

            if (string.IsNullOrEmpty(request.ObjectName))
                throw new ArgumentException("Object name is required", nameof(request.ObjectName));

            if (request.FileStream == null || request.FileStream.Length == 0)
                throw new ArgumentException("File stream is empty", nameof(request.FileStream));

            if (!request.FileStream.CanRead)
                throw new ArgumentException("File stream is not readable", nameof(request.FileStream));
        }

        private void ValidateDownloadRequest(StorageDownloadRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.BucketName))
                throw new ArgumentException("Bucket name is required", nameof(request.BucketName));

            if (string.IsNullOrEmpty(request.ObjectName))
                throw new ArgumentException("Object name is required", nameof(request.ObjectName));
        }

        private string GetStorageClass(StorageClassType storageClass)
        {
            return storageClass switch;
            {
                StorageClassType.Standard => "STANDARD",
                StorageClassType.Nearline => "NEARLINE",
                StorageClassType.Coldline => "COLDLINE",
                StorageClassType.Archive => "ARCHIVE",
                StorageClassType.MultiRegional => "MULTI_REGIONAL",
                StorageClassType.Regional => "REGIONAL",
                StorageClassType.DurableReducedAvailability => "DURABLE_REDUCED_AVAILABILITY",
                _ => "STANDARD"
            };
        }

        private Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum? GetStorageClassFromString(string storageClass)
        {
            if (string.IsNullOrEmpty(storageClass))
                return null;

            return storageClass.ToUpperInvariant() switch;
            {
                "STANDARD" => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.Standard,
                "NEARLINE" => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.Nearline,
                "COLDLINE" => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.Coldline,
                "ARCHIVE" => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.Archive,
                "MULTI_REGIONAL" => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.MultiRegional,
                "REGIONAL" => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.Regional,
                "DURABLE_REDUCED_AVAILABILITY" => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.DurableReducedAvailability,
                _ => Google.Apis.Storage.v1.Data.Bucket.StorageClassEnum.Standard;
            };
        }

        private PredefinedObjectAcl? GetPredefinedAcl(PredefinedAclType? aclType)
        {
            if (!aclType.HasValue)
                return null;

            return aclType.Value switch;
            {
                PredefinedAclType.AuthenticatedRead => PredefinedObjectAcl.AuthenticatedRead,
                PredefinedAclType.BucketOwnerFullControl => PredefinedObjectAcl.BucketOwnerFullControl,
                PredefinedAclType.BucketOwnerRead => PredefinedObjectAcl.BucketOwnerRead,
                PredefinedAclType.Private => PredefinedObjectAcl.Private,
                PredefinedAclType.ProjectPrivate => PredefinedObjectAcl.ProjectPrivate,
                PredefinedAclType.PublicRead => PredefinedObjectAcl.PublicRead,
                _ => null;
            };
        }

        private PredefinedBucketAcl? GetPredefinedBucketAcl(PredefinedAclType? aclType)
        {
            if (!aclType.HasValue)
                return null;

            return aclType.Value switch;
            {
                PredefinedAclType.AuthenticatedRead => PredefinedBucketAcl.AuthenticatedRead,
                PredefinedAclType.Private => PredefinedBucketAcl.Private,
                PredefinedAclType.ProjectPrivate => PredefinedBucketAcl.ProjectPrivate,
                PredefinedAclType.PublicRead => PredefinedBucketAcl.PublicRead,
                PredefinedAclType.PublicReadWrite => PredefinedBucketAcl.PublicReadWrite,
                _ => null;
            };
        }

        private PredefinedObjectAcl? GetPredefinedObjectAcl(PredefinedAclType? aclType)
        {
            return GetPredefinedAcl(aclType);
        }

        private bool IsRetryableError(Google.GoogleApiException exception)
        {
            return exception.HttpStatusCode switch;
            {
                HttpStatusCode.RequestTimeout => true,
                HttpStatusCode.InternalServerError => true,
                HttpStatusCode.BadGateway => true,
                HttpStatusCode.ServiceUnavailable => true,
                HttpStatusCode.GatewayTimeout => true,
                _ => false;
            };
        }

        private bool IsValidBucketName(string bucketName)
        {
            // Google Cloud Storage bucket name rules:
            // 1. 3-63 characters long;
            // 2. Contains only lowercase letters, numbers, dots (.), dashes (-)
            // 3. Starts and ends with a letter or number;
            // 4. Not an IP address;

            if (string.IsNullOrEmpty(bucketName))
                return false;

            if (bucketName.Length < 3 || bucketName.Length > 63)
                return false;

            if (!char.IsLetterOrDigit(bucketName[0]) || !char.IsLetterOrDigit(bucketName[^1]))
                return false;

            if (bucketName.Contains("..") || bucketName.Contains(".-") || bucketName.Contains("-."))
                return false;

            // Check if it's an IP address;
            if (System.Net.IPAddress.TryParse(bucketName, out _))
                return false;

            // Check valid characters;
            foreach (char c in bucketName)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-'))
                    return false;
            }

            return true;
        }

        private async Task<Google.Apis.Storage.v1.Data.Bucket> GetBucketAsync(string bucketName)
        {
            if (_bucketCache.TryGetValue(bucketName, out var cachedBucket))
            {
                return cachedBucket;
            }

            try
            {
                var bucket = await _storageClient.GetBucketAsync(
                    bucketName,
                    null,
                    CancellationToken.None);

                _bucketCache[bucketName] = bucket;
                return bucket;
            }
            catch (Google.GoogleApiException gex) when (gex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task CollectUploadMetricsAsync(StorageUploadResult result)
        {
            try
            {
                await _metricsCollector.RecordMetricAsync("gcs_upload_size_bytes", result.Size, new Dictionary<string, string>
                {
                    ["storage_class"] = result.StorageClass ?? "STANDARD",
                    ["bucket"] = result.BucketName;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect upload metrics");
            }
        }

        #endregion;

        #region Event Handlers;

        private async Task OnStorageUploadRequested(StorageUploadRequestedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing storage upload request: {Bucket}/{Object}",
                    @event.BucketName, @event.ObjectName);

                var request = new StorageUploadRequest;
                {
                    BucketName = @event.BucketName,
                    ObjectName = @event.ObjectName,
                    FileStream = @event.FileStream,
                    ContentType = @event.ContentType,
                    StorageClass = @event.StorageClass,
                    Metadata = @event.Metadata;
                };

                await UploadFileAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling storage upload request");
            }
        }

        private async Task OnStorageDeleteRequested(StorageDeleteRequestedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing storage delete request: {Bucket}/{Object}",
                    @event.BucketName, @event.ObjectName);

                var request = new StorageDeleteRequest;
                {
                    BucketName = @event.BucketName,
                    ObjectName = @event.ObjectName;
                };

                await DeleteFileAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling storage delete request");
            }
        }

        private async Task OnStorageCopyRequested(StorageCopyRequestedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing storage copy request: {Source} -> {Destination}",
                    @event.SourceObject, @event.DestinationObject);

                var request = new StorageCopyRequest;
                {
                    SourceBucket = @event.SourceBucket,
                    SourceObject = @event.SourceObject,
                    DestinationBucket = @event.DestinationBucket,
                    DestinationObject = @event.DestinationObject;
                };

                await CopyFileAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling storage copy request");
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
            if (!_disposed)
            {
                if (disposing)
                {
                    _storageClient?.Dispose();
                    _operationSemaphore?.Dispose();
                }

                _disposed = true;
            }
        }

        ~StorageService()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Data Models;

    public class StorageUploadRequest;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public Stream FileStream { get; set; }
        public string ContentType { get; set; }
        public StorageClassType StorageClass { get; set; } = StorageClassType.Standard;
        public PredefinedAclType? PredefinedAcl { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public string CacheControl { get; set; }
        public string ContentDisposition { get; set; }
        public string ContentEncoding { get; set; }
        public string ContentLanguage { get; set; }
        public string KmsKeyName { get; set; }
        public DateTime? CustomTime { get; set; }
        public long? IfGenerationMatch { get; set; }
        public long? IfGenerationNotMatch { get; set; }
        public long? IfMetagenerationMatch { get; set; }
        public long? IfMetagenerationNotMatch { get; set; }
        public Action<long, long> ProgressCallback { get; set; }
    }

    public class StorageUploadResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public long Generation { get; set; }
        public long Size { get; set; }
        public string ETag { get; set; }
        public string Md5Hash { get; set; }
        public string Crc32c { get; set; }
        public string StorageClass { get; set; }
        public DateTime TimeCreated { get; set; }
        public DateTime Updated { get; set; }
        public string KmsKeyName { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public bool IsResumableUpload { get; set; }
        public string SessionId { get; set; }
        public int ChunksCount { get; set; }
    }

    public class StorageDownloadRequest;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public long? Generation { get; set; }
        public long? RangeStart { get; set; }
        public long? RangeEnd { get; set; }
        public long? IfGenerationMatch { get; set; }
        public long? IfGenerationNotMatch { get; set; }
        public long? IfMetagenerationMatch { get; set; }
        public long? IfMetagenerationNotMatch { get; set; }
        public Action<long, long?> ProgressCallback { get; set; }
    }

    public class StorageDownloadResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public Stream FileStream { get; set; }
        public string ContentType { get; set; }
        public long Size { get; set; }
        public long Generation { get; set; }
        public string ETag { get; set; }
        public string Md5Hash { get; set; }
        public string Crc32c { get; set; }
        public string StorageClass { get; set; }
        public DateTime TimeCreated { get; set; }
        public DateTime Updated { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public string CacheControl { get; set; }
        public string ContentDisposition { get; set; }
        public string ContentEncoding { get; set; }
        public string ContentLanguage { get; set; }
        public DateTime DownloadTime { get; set; }
    }

    public class StorageDeleteRequest;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public long? Generation { get; set; }
        public long? IfGenerationMatch { get; set; }
        public long? IfGenerationNotMatch { get; set; }
        public long? IfMetagenerationMatch { get; set; }
        public long? IfMetagenerationNotMatch { get; set; }
    }

    public class StorageDeleteResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public long? Generation { get; set; }
        public DateTime DeleteTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class StorageFileInfo;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public string ContentType { get; set; }
        public long Size { get; set; }
        public long Generation { get; set; }
        public string ETag { get; set; }
        public string Md5Hash { get; set; }
        public string Crc32c { get; set; }
        public string StorageClass { get; set; }
        public DateTime TimeCreated { get; set; }
        public DateTime Updated { get; set; }
        public DateTime? TimeStorageClassUpdated { get; set; }
        public DateTime? RetentionExpirationTime { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public string CacheControl { get; set; }
        public string ContentDisposition { get; set; }
        public string ContentEncoding { get; set; }
        public string ContentLanguage { get; set; }
        public string KmsKeyName { get; set; }
        public bool TemporaryHold { get; set; }
        public bool EventBasedHold { get; set; }
        public object Retention { get; set; }
        public DateTime? CustomTime { get; set; }
    }

    public class StorageObjectInfo;
    {
        public string Name { get; set; }
        public string BucketName { get; set; }
        public long Size { get; set; }
        public DateTime TimeCreated { get; set; }
        public DateTime Updated { get; set; }
        public long Generation { get; set; }
        public string StorageClass { get; set; }
        public string ETag { get; set; }
        public string Md5Hash { get; set; }
        public string Crc32c { get; set; }
    }

    public class StorageSignedUrlRequest;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public double ExpiryHours { get; set; } = 1.0;
        public HttpMethod HttpMethod { get; set; } = HttpMethod.GET;
        public bool UseHttps { get; set; } = true;
        public Dictionary<string, string> ResponseHeaders { get; set; } = new Dictionary<string, string>();
    }

    public class StorageCopyRequest;
    {
        public string SourceBucket { get; set; }
        public string SourceObject { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public long? SourceGeneration { get; set; }
        public string StorageClass { get; set; }
        public PredefinedAclType? PredefinedAcl { get; set; }
        public string KmsKeyName { get; set; }
        public long? IfGenerationMatch { get; set; }
        public long? IfGenerationNotMatch { get; set; }
        public long? IfMetagenerationMatch { get; set; }
        public long? IfMetagenerationNotMatch { get; set; }
        public long? IfSourceGenerationMatch { get; set; }
        public long? IfSourceGenerationNotMatch { get; set; }
        public long? IfSourceMetagenerationMatch { get; set; }
        public long? IfSourceMetagenerationNotMatch { get; set; }
    }

    public class StorageCopyResult;
    {
        public bool Success { get; set; }
        public string SourceBucket { get; set; }
        public string SourceObject { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public long Generation { get; set; }
        public long Size { get; set; }
        public string StorageClass { get; set; }
        public DateTime CopyTime { get; set; }
        public long? SourceGeneration { get; set; }
    }

    public class StorageMoveRequest;
    {
        public string SourceBucket { get; set; }
        public string SourceObject { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public string StorageClass { get; set; }
        public PredefinedAclType? PredefinedAcl { get; set; }
        public string KmsKeyName { get; set; }
    }

    public class StorageMoveResult;
    {
        public bool Success { get; set; }
        public string SourceBucket { get; set; }
        public string SourceObject { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public StorageCopyResult CopyResult { get; set; }
        public StorageDeleteResult DeleteResult { get; set; }
        public DateTime MoveTime { get; set; }
    }

    public class StorageResumableUploadRequest;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public string ContentType { get; set; } = "application/octet-stream";
        public PredefinedAclType? PredefinedAcl { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public int? ChunkSize { get; set; }
    }

    public class StorageResumableUpload;
    {
        public string SessionId { get; set; }
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public DateTime Initiated { get; set; }
        public List<StorageUploadChunk> Chunks { get; set; } = new List<StorageUploadChunk>();
        public bool IsCompleted { get; set; }
    }

    public class StorageUploadChunk;
    {
        public int ChunkIndex { get; set; }
        public long Offset { get; set; }
        public byte[] Data { get; set; }
        public DateTime Uploaded { get; set; }
    }

    public class StorageResumableUploadCompleteRequest;
    {
        public string SessionId { get; set; }
        public List<StorageUploadChunk> Chunks { get; set; } = new List<StorageUploadChunk>();
    }

    public class StorageBucketRequest;
    {
        public string BucketName { get; set; }
        public string Location { get; set; }
        public StorageClassType StorageClass { get; set; } = StorageClassType.Standard;
        public bool EnableVersioning { get; set; }
        public bool UniformBucketLevelAccess { get; set; }
        public PredefinedAclType? PredefinedAcl { get; set; }
        public PredefinedAclType? PredefinedDefaultObjectAcl { get; set; }
        public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
        public List<StorageLifecycleRule> LifecycleRules { get; set; } = new List<StorageLifecycleRule>();
        public List<StorageCorsRule> CorsRules { get; set; } = new List<StorageCorsRule>();
    }

    public class StorageBucketResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string Location { get; set; }
        public string StorageClass { get; set; }
        public DateTime TimeCreated { get; set; }
        public DateTime? DeletionTime { get; set; }
        public string ProjectNumber { get; set; }
        public string Id { get; set; }
        public string SelfLink { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class StorageIamPolicy;
    {
        public List<StorageIamBinding> Bindings { get; set; } = new List<StorageIamBinding>();
        public string Etag { get; set; }
        public int Version { get; set; } = 3;
    }

    public class StorageIamBinding;
    {
        public string Role { get; set; }
        public List<string> Members { get; set; } = new List<string>();
        public StorageIamCondition Condition { get; set; }
    }

    public class StorageIamCondition;
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string Expression { get; set; }
    }

    public class StorageCorsRule;
    {
        public List<string> Origins { get; set; } = new List<string>();
        public List<string> Methods { get; set; } = new List<string>();
        public List<string> ResponseHeaders { get; set; } = new List<string>();
        public int MaxAgeSeconds { get; set; } = 3600;
    }

    public class StorageLifecycleRule;
    {
        public string ActionType { get; set; } // "Delete", "SetStorageClass"
        public string StorageClass { get; set; }
        public int? AgeDays { get; set; }
        public int? NumberOfNewerVersions { get; set; }
        public bool? IsLive { get; set; }
        public List<string> MatchesStorageClasses { get; set; } = new List<string>();
        public int? DaysSinceCustomTime { get; set; }
        public DateTime? CustomTimeBefore { get; set; }
        public int? DaysSinceNoncurrentTime { get; set; }
        public DateTime? NoncurrentTimeBefore { get; set; }
    }

    public class StorageAcl;
    {
        public List<StorageAclEntry> Entries { get; set; } = new List<StorageAclEntry>();
    }

    public class StorageAclEntry
    {
        public string Entity { get; set; }
        public string Role { get; set; }
        public string Email { get; set; }
        public string Domain { get; set; }
        public StorageProjectTeam ProjectTeam { get; set; }
    }

    public class StorageProjectTeam;
    {
        public string ProjectNumber { get; set; }
        public string Team { get; set; } // "owners", "editors", "viewers"
    }

    public class StorageMetadataUpdateRequest;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
        public string CacheControl { get; set; }
        public string ContentDisposition { get; set; }
        public string ContentEncoding { get; set; }
        public string ContentLanguage { get; set; }
        public string ContentType { get; set; }
        public DateTime? CustomTime { get; set; }
        public string StorageClass { get; set; }
        public PredefinedAclType? PredefinedAcl { get; set; }
        public long? IfMetagenerationMatch { get; set; }
        public long? IfMetagenerationNotMatch { get; set; }
    }

    public class StorageMetadataResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public long Generation { get; set; }
        public long Metageneration { get; set; }
        public DateTime UpdateTime { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public class StorageBatchRequest;
    {
        public List<StorageBatchOperation> Operations { get; set; } = new List<StorageBatchOperation>();
        public int MaxConcurrency { get; set; } = 10;
    }

    public class StorageBatchOperation;
    {
        public string Id { get; set; }
        public StorageBatchOperationType Type { get; set; }
        public StorageUploadRequest UploadRequest { get; set; }
        public StorageDownloadRequest DownloadRequest { get; set; }
        public StorageDeleteRequest DeleteRequest { get; set; }
        public StorageCopyRequest CopyRequest { get; set; }
    }

    public class StorageBatchResult;
    {
        public int TotalOperations { get; set; }
        public int SuccessfulOperations { get; set; }
        public int FailedOperations { get; set; }
        public List<StorageBatchOperationResult> OperationResults { get; set; } = new List<StorageBatchOperationResult>();
        public DateTime CompletionTime { get; set; }
    }

    public class StorageBatchOperationResult;
    {
        public string OperationId { get; set; }
        public StorageBatchOperationType Type { get; set; }
        public bool Success { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
    }

    public class StorageComposeRequest;
    {
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public List<StorageSourceObject> SourceObjects { get; set; } = new List<StorageSourceObject>();
        public string ContentType { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public string KmsKeyName { get; set; }
        public PredefinedAclType? PredefinedAcl { get; set; }
        public long? IfGenerationMatch { get; set; }
        public long? IfMetagenerationMatch { get; set; }
    }

    public class StorageSourceObject;
    {
        public string ObjectName { get; set; }
        public long? Generation { get; set; }
        public long? IfGenerationMatch { get; set; }
    }

    public class StorageComposeResult;
    {
        public bool Success { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public List<StorageSourceObject> SourceObjects { get; set; }
        public long Generation { get; set; }
        public long Size { get; set; }
        public DateTime ComposeTime { get; set; }
    }

    public class StorageRewriteRequest;
    {
        public string SourceBucket { get; set; }
        public string SourceObject { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public long? SourceGeneration { get; set; }
        public string DestinationKmsKeyName { get; set; }
        public PredefinedAclType? DestinationPredefinedAcl { get; set; }
        public Action<long, long> ProgressCallback { get; set; }
    }

    public class StorageRewriteResult;
    {
        public bool Success { get; set; }
        public string SourceBucket { get; set; }
        public string SourceObject { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
        public long Generation { get; set; }
        public long Size { get; set; }
        public string StorageClass { get; set; }
        public DateTime RewriteTime { get; set; }
        public long TotalBytesRewritten { get; set; }
        public long TotalBytes { get; set; }
    }

    public class StorageHmacKey;
    {
        public string AccessId { get; set; }
        public string Secret { get; set; }
        public string ServiceAccountEmail { get; set; }
        public string State { get; set; }
        public DateTime TimeCreated { get; set; }
        public DateTime Updated { get; set; }
    }

    public class StorageNotificationRequest;
    {
        public string BucketName { get; set; }
        public string Address { get; set; }
        public string Type { get; set; } = "web_hook";
        public string Token { get; set; }
        public DateTimeOffset? ExpirationTime { get; set; }
        public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>();
    }

    public class StorageNotificationChannel;
    {
        public string Id { get; set; }
        public string ResourceId { get; set; }
        public string ResourceUri { get; set; }
        public string Token { get; set; }
        public DateTimeOffset? Expiration { get; set; }
        public string Address { get; set; }
        public string Type { get; set; }
        public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>();
    }

    #endregion;

    #region Enums;

    public enum StorageClassType;
    {
        Standard,
        Nearline,
        Coldline,
        Archive,
        MultiRegional,
        Regional,
        DurableReducedAvailability;
    }

    public enum PredefinedAclType;
    {
        AuthenticatedRead,
        BucketOwnerFullControl,
        BucketOwnerRead,
        Private,
        ProjectPrivate,
        PublicRead,
        PublicReadWrite;
    }

    public enum StorageBatchOperationType;
    {
        Upload,
        Download,
        Delete,
        Copy,
        Move;
    }

    public enum HttpMethod;
    {
        GET,
        PUT,
        DELETE,
        HEAD,
        POST;
    }

    #endregion;

    #region Events;

    public class StorageUploadedEvent : IEvent;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public long Size { get; set; }
        public long Generation { get; set; }
        public string StorageClass { get; set; }
        public DateTime UploadTime { get; set; }
        public bool IsResumableUpload { get; set; }
    }

    public class StorageUploadFailedEvent : IEvent;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StorageDeletedEvent : IEvent;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public DateTime DeleteTime { get; set; }
    }

    // Request events;
    public class StorageUploadRequestedEvent : IEvent;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
        public Stream FileStream { get; set; }
        public string ContentType { get; set; }
        public StorageClassType StorageClass { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public class StorageDeleteRequestedEvent : IEvent;
    {
        public string BucketName { get; set; }
        public string ObjectName { get; set; }
    }

    public class StorageCopyRequestedEvent : IEvent;
    {
        public string SourceBucket { get; set; }
        public string SourceObject { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationObject { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class StorageServiceException : Exception
    {
        public StorageServiceException(string message) : base(message) { }
        public StorageServiceException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class StorageFileNotFoundException : StorageServiceException;
    {
        public StorageFileNotFoundException(string message) : base(message) { }
        public StorageFileNotFoundException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;

    #region GCS SDK Models;

    // Note: These are simplified versions for clarity;
    // In real implementation, use the actual Google.Cloud.Storage.V1 namespace;

    public enum UploadValidationMode;
    {
        None,
        Always;
    }

    public enum PredefinedObjectAcl;
    {
        AuthenticatedRead,
        BucketOwnerFullControl,
        BucketOwnerRead,
        Private,
        ProjectPrivate,
        PublicRead;
    }

    public enum PredefinedBucketAcl;
    {
        AuthenticatedRead,
        Private,
        ProjectPrivate,
        PublicRead,
        PublicReadWrite;
    }

    #endregion;
}
