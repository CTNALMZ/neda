using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.S3.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Cloud.AWS;
{
    /// <summary>
    /// Amazon S3 servisini yöneten ana interface;
    /// </summary>
    public interface IS3Service;
    {
        /// <summary>
        /// Dosyayı S3'e yükler;
        /// </summary>
        Task<S3UploadResult> UploadFileAsync(S3UploadRequest request);

        /// <summary>
        /// Dosyayı S3'ten indirir;
        /// </summary>
        Task<S3DownloadResult> DownloadFileAsync(S3DownloadRequest request);

        /// <summary>
        /// Dosyayı S3'ten siler;
        /// </summary>
        Task<S3DeleteResult> DeleteFileAsync(S3DeleteRequest request);

        /// <summary>
        /// Dosya bilgilerini getirir;
        /// </summary>
        Task<S3FileInfo> GetFileInfoAsync(string bucketName, string key);

        /// <summary>
        /// Bucket'taki dosyaları listeler;
        /// </summary>
        Task<List<S3ObjectInfo>> ListFilesAsync(string bucketName, string prefix = null, int maxKeys = 1000);

        /// <summary>
        /// Presigned URL oluşturur;
        /// </summary>
        Task<string> GeneratePresignedUrlAsync(S3PresignedUrlRequest request);

        /// <summary>
        /// Dosya kopyalar;
        /// </summary>
        Task<S3CopyResult> CopyFileAsync(S3CopyRequest request);

        /// <summary>
        /// Dosya taşır;
        /// </summary>
        Task<S3MoveResult> MoveFileAsync(S3MoveRequest request);

        /// <summary>
        /// Multipart upload başlatır;
        /// </summary>
        Task<S3MultipartUpload> StartMultipartUploadAsync(S3MultipartUploadRequest request);

        /// <summary>
        /// Multipart upload'ı tamamlar;
        /// </summary>
        Task<S3UploadResult> CompleteMultipartUploadAsync(S3MultipartUploadCompleteRequest request);

        /// <summary>
        /// Bucket oluşturur;
        /// </summary>
        Task<S3BucketResult> CreateBucketAsync(string bucketName, S3Region region = S3Region.USEast1);

        /// <summary>
        /// Bucket'ı siler;
        /// </summary>
        Task<S3BucketResult> DeleteBucketAsync(string bucketName);

        /// <summary>
        /// Bucket policy ayarlar;
        /// </summary>
        Task SetBucketPolicyAsync(string bucketName, string policy);

        /// <summary>
        /// CORS ayarlarını yapılandırır;
        /// </summary>
        Task ConfigureCorsAsync(string bucketName, List<CorsRule> rules);

        /// <summary>
        /// Lifecycle policy ayarlar;
        /// </summary>
        Task ConfigureLifecycleAsync(string bucketName, List<LifecycleRule> rules);

        /// <summary>
        /// Versiyonlamayı etkinleştirir;
        /// </summary>
        Task EnableVersioningAsync(string bucketName);

        /// <summary>
        /// Dosya boyutunu getirir;
        /// </summary>
        Task<long> GetFileSizeAsync(string bucketName, string key);

        /// <summary>
        /// Dosya MIME type'ını getirir;
        /// </summary>
        Task<string> GetFileContentTypeAsync(string bucketName, string key);

        /// <summary>
        /// Batch dosya işlemleri;
        /// </summary>
        Task<S3BatchResult> ProcessBatchAsync(S3BatchRequest request);
    }

    /// <summary>
    /// S3 Service implementasyonu;
    /// </summary>
    public class S3Service : IS3Service, IDisposable;
    {
        private readonly ILogger<S3Service> _logger;
        private readonly IAppConfig _appConfig;
        private readonly IAmazonS3 _s3Client;
        private readonly ITransferUtility _transferUtility;
        private readonly ConcurrentDictionary<string, S3BucketInfo> _bucketCache;
        private readonly ConcurrentDictionary<string, S3MultipartUpload> _multipartUploads;
        private readonly SemaphoreSlim _uploadSemaphore;

        private bool _disposed = false;
        private const int MAX_UPLOAD_RETRIES = 3;
        private const int UPLOAD_TIMEOUT_SECONDS = 300;
        private const long MAX_SINGLE_UPLOAD_SIZE = 100 * 1024 * 1024; // 100MB;
        private const int MAX_CONCURRENT_UPLOADS = 10;
        private const int DEFAULT_PART_SIZE = 5 * 1024 * 1024; // 5MB;

        public S3Service(
            ILogger<S3Service> logger,
            IAppConfig appConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            // AWS credentials ve region'ı config'den al;
            var awsConfig = new AmazonS3Config;
            {
                RegionEndpoint = GetRegionEndpoint(appConfig.AwsRegion),
                UseHttp = appConfig.AwsUseHttp,
                Timeout = TimeSpan.FromSeconds(appConfig.AwsTimeoutSeconds),
                MaxErrorRetry = appConfig.AwsMaxRetries,
                BufferSize = 81920,
                ProgressUpdateInterval = TimeSpan.FromMilliseconds(100)
            };

            _s3Client = new AmazonS3Client(
                appConfig.AwsAccessKey,
                appConfig.AwsSecretKey,
                awsConfig);

            _transferUtility = new TransferUtility(_s3Client);
            _bucketCache = new ConcurrentDictionary<string, S3BucketInfo>();
            _multipartUploads = new ConcurrentDictionary<string, S3MultipartUpload>();
            _uploadSemaphore = new SemaphoreSlim(MAX_CONCURRENT_UPLOADS, MAX_CONCURRENT_UPLOADS);

            _logger.LogInformation("S3 Service initialized for region: {Region}", appConfig.AwsRegion);
        }

        /// <summary>
        /// AWS RegionEndpoint'ini döndürür;
        /// </summary>
        private RegionEndpoint GetRegionEndpoint(string region)
        {
            return RegionEndpoint.GetBySystemName(region);
        }

        public async Task<S3UploadResult> UploadFileAsync(S3UploadRequest request)
        {
            await _uploadSemaphore.WaitAsync();

            try
            {
                _logger.LogInformation("Uploading file to S3. Bucket: {Bucket}, Key: {Key}, Size: {Size} bytes",
                    request.BucketName, request.Key, request.FileStream?.Length ?? 0);

                ValidateUploadRequest(request);

                // Dosya boyutuna göre upload method seç;
                if (request.FileStream.Length > MAX_SINGLE_UPLOAD_SIZE)
                {
                    return await UploadLargeFileMultipartAsync(request);
                }
                else;
                {
                    return await UploadSmallFileAsync(request);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload file to S3. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);
                throw new S3ServiceException($"Failed to upload file to S3: {ex.Message}", ex);
            }
            finally
            {
                _uploadSemaphore.Release();
            }
        }

        /// <summary>
        Küçük dosya upload'ı;
        /// </summary>
        private async Task<S3UploadResult> UploadSmallFileAsync(S3UploadRequest request)
        {
            var retryCount = 0;

            while (retryCount < MAX_UPLOAD_RETRIES)
            {
                try
                {
                    var putRequest = new PutObjectRequest;
                    {
                        BucketName = request.BucketName,
                        Key = request.Key,
                        InputStream = request.FileStream,
                        ContentType = request.ContentType ?? "application/octet-stream",
                        StorageClass = GetStorageClass(request.StorageClass),
                        ServerSideEncryptionMethod = GetEncryptionMethod(request.Encryption),
                        AutoCloseStream = request.AutoCloseStream,
                        CannedACL = GetCannedACL(request.AccessControl)
                    };

                    // Metadata ekle;
                    if (request.Metadata != null && request.Metadata.Any())
                    {
                        foreach (var metadata in request.Metadata)
                        {
                            putRequest.Metadata.Add(metadata.Key, metadata.Value);
                        }
                    }

                    // Tags ekle;
                    if (request.Tags != null && request.Tags.Any())
                    {
                        putRequest.TagSet = request.Tags.Select(t => new Tag;
                        {
                            Key = t.Key,
                            Value = t.Value;
                        }).ToList();
                    }

                    // Progress event;
                    putRequest.StreamTransferProgress += (sender, args) =>
                    {
                        _logger.LogDebug("Upload progress: {Percent}% for {Key}",
                            (int)((double)args.TransferredBytes / args.TotalBytes * 100), request.Key);

                        request.ProgressCallback?.Invoke(args.TransferredBytes, args.TotalBytes);
                    };

                    var response = await _s3Client.PutObjectAsync(putRequest);

                    var result = new S3UploadResult;
                    {
                        Success = response.HttpStatusCode == HttpStatusCode.OK,
                        BucketName = request.BucketName,
                        Key = request.Key,
                        ETag = response.ETag,
                        VersionId = response.VersionId,
                        FileSize = request.FileStream.Length,
                        UploadTime = DateTime.UtcNow,
                        StorageClass = request.StorageClass.ToString(),
                        ServerSideEncryption = response.ServerSideEncryptionMethod?.ToString()
                    };

                    _logger.LogInformation("File uploaded successfully to S3. Bucket: {Bucket}, Key: {Key}, ETag: {ETag}",
                        request.BucketName, request.Key, response.ETag);

                    // Event publish;
                    await PublishUploadEventAsync(request, result);

                    return result;
                }
                catch (AmazonS3Exception s3Ex)
                {
                    retryCount++;

                    if (retryCount >= MAX_UPLOAD_RETRIES)
                    {
                        _logger.LogError(s3Ex, "S3 upload failed after {Retries} retries", MAX_UPLOAD_RETRIES);
                        throw;
                    }

                    _logger.LogWarning(s3Ex, "S3 upload failed, retrying ({Retry}/{MaxRetries})",
                        retryCount, MAX_UPLOAD_RETRIES);

                    await Task.Delay(1000 * retryCount); // Exponential backoff;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during S3 upload");
                    throw;
                }
            }

            throw new S3ServiceException("Upload failed after maximum retries");
        }

        /// <summary>
        Büyük dosya multipart upload'ı;
        /// </summary>
        private async Task<S3UploadResult> UploadLargeFileMultipartAsync(S3UploadRequest request)
        {
            try
            {
                _logger.LogInformation("Starting multipart upload for large file. Bucket: {Bucket}, Key: {Key}, Size: {Size}",
                    request.BucketName, request.Key, request.FileStream.Length);

                // Multipart upload başlat;
                var initiateRequest = new InitiateMultipartUploadRequest;
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    ContentType = request.ContentType ?? "application/octet-stream",
                    StorageClass = GetStorageClass(request.StorageClass),
                    ServerSideEncryptionMethod = GetEncryptionMethod(request.Encryption)
                };

                var initiateResponse = await _s3Client.InitiateMultipartUploadAsync(initiateRequest);

                var uploadId = initiateResponse.UploadId;
                var partETags = new List<PartETag>();
                var partNumber = 1;
                var buffer = new byte[DEFAULT_PART_SIZE];

                // Dosyayı part'lara böl ve upload et;
                while (true)
                {
                    var bytesRead = await request.FileStream.ReadAsync(buffer, 0, DEFAULT_PART_SIZE);

                    if (bytesRead == 0)
                        break;

                    using var partStream = new MemoryStream(buffer, 0, bytesRead);

                    var uploadPartRequest = new UploadPartRequest;
                    {
                        BucketName = request.BucketName,
                        Key = request.Key,
                        UploadId = uploadId,
                        PartNumber = partNumber,
                        InputStream = partStream,
                        PartSize = bytesRead;
                    };

                    var uploadPartResponse = await _s3Client.UploadPartAsync(uploadPartRequest);
                    partETags.Add(new PartETag(partNumber, uploadPartResponse.ETag));

                    _logger.LogDebug("Uploaded part {PartNumber} for {Key}, ETag: {ETag}",
                        partNumber, request.Key, uploadPartResponse.ETag);

                    partNumber++;
                }

                // Multipart upload'ı tamamla;
                var completeRequest = new CompleteMultipartUploadRequest;
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    UploadId = uploadId,
                    PartETags = partETags;
                };

                var completeResponse = await _s3Client.CompleteMultipartUploadAsync(completeRequest);

                var result = new S3UploadResult;
                {
                    Success = completeResponse.HttpStatusCode == HttpStatusCode.OK,
                    BucketName = request.BucketName,
                    Key = request.Key,
                    ETag = completeResponse.ETag,
                    VersionId = completeResponse.VersionId,
                    FileSize = request.FileStream.Length,
                    UploadTime = DateTime.UtcNow,
                    StorageClass = request.StorageClass.ToString(),
                    ServerSideEncryption = completeResponse.ServerSideEncryptionMethod?.ToString(),
                    IsMultipartUpload = true,
                    PartsCount = partETags.Count;
                };

                _logger.LogInformation("Multipart upload completed successfully. Bucket: {Bucket}, Key: {Key}, Parts: {Parts}",
                    request.BucketName, request.Key, partETags.Count);

                // Event publish;
                await PublishUploadEventAsync(request, result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Multipart upload failed for {Key}", request.Key);
                throw;
            }
        }

        public async Task<S3DownloadResult> DownloadFileAsync(S3DownloadRequest request)
        {
            try
            {
                _logger.LogInformation("Downloading file from S3. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);

                ValidateDownloadRequest(request);

                var getRequest = new GetObjectRequest;
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    VersionId = request.VersionId;
                };

                // Byte range belirtilmişse;
                if (request.ByteRangeStart.HasValue || request.ByteRangeEnd.HasValue)
                {
                    getRequest.ByteRange = new ByteRange(
                        request.ByteRangeStart ?? 0,
                        request.ByteRangeEnd ?? long.MaxValue);
                }

                using var response = await _s3Client.GetObjectAsync(getRequest);

                if (response.HttpStatusCode != HttpStatusCode.OK)
                {
                    throw new S3ServiceException($"Download failed with status: {response.HttpStatusCode}");
                }

                // Stream'e yaz;
                var memoryStream = new MemoryStream();
                await response.ResponseStream.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var result = new S3DownloadResult;
                {
                    Success = true,
                    BucketName = request.BucketName,
                    Key = request.Key,
                    FileStream = memoryStream,
                    ContentType = response.Headers.ContentType,
                    ContentLength = response.Headers.ContentLength,
                    ETag = response.ETag,
                    VersionId = response.VersionId,
                    LastModified = response.LastModified,
                    Metadata = response.Metadata.ToDictionary(m => m.Key, m => m.Value),
                    DownloadTime = DateTime.UtcNow;
                };

                _logger.LogInformation("File downloaded successfully from S3. Bucket: {Bucket}, Key: {Key}, Size: {Size} bytes",
                    request.BucketName, request.Key, memoryStream.Length);

                return result;
            }
            catch (AmazonS3Exception s3Ex) when (s3Ex.ErrorCode == "NoSuchKey")
            {
                _logger.LogWarning("File not found in S3. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);
                throw new S3FileNotFoundException($"File not found: {request.Key}", s3Ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download file from S3. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);
                throw new S3ServiceException($"Failed to download file from S3: {ex.Message}", ex);
            }
        }

        public async Task<S3DeleteResult> DeleteFileAsync(S3DeleteRequest request)
        {
            try
            {
                _logger.LogInformation("Deleting file from S3. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);

                var deleteRequest = new DeleteObjectRequest;
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    VersionId = request.VersionId;
                };

                var response = await _s3Client.DeleteObjectAsync(deleteRequest);

                var result = new S3DeleteResult;
                {
                    Success = response.HttpStatusCode == HttpStatusCode.NoContent,
                    BucketName = request.BucketName,
                    Key = request.Key,
                    VersionId = request.VersionId,
                    DeleteMarker = response.DeleteMarker,
                    DeleteMarkerVersionId = response.DeleteMarkerVersionId,
                    DeleteTime = DateTime.UtcNow;
                };

                _logger.LogInformation("File deleted successfully from S3. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file from S3. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);
                throw new S3ServiceException($"Failed to delete file from S3: {ex.Message}", ex);
            }
        }

        public async Task<S3FileInfo> GetFileInfoAsync(string bucketName, string key)
        {
            try
            {
                _logger.LogDebug("Getting file info from S3. Bucket: {Bucket}, Key: {Key}", bucketName, key);

                var request = new GetObjectMetadataRequest;
                {
                    BucketName = bucketName,
                    Key = key;
                };

                var response = await _s3Client.GetObjectMetadataAsync(request);

                var fileInfo = new S3FileInfo;
                {
                    BucketName = bucketName,
                    Key = key,
                    ContentType = response.Headers.ContentType,
                    ContentLength = response.Headers.ContentLength,
                    ETag = response.ETag,
                    LastModified = response.LastModified,
                    StorageClass = response.StorageClass,
                    VersionId = response.VersionId,
                    ServerSideEncryption = response.ServerSideEncryptionMethod?.ToString(),
                    Metadata = response.Metadata.ToDictionary(m => m.Key, m => m.Value),
                    Expiration = response.Expiration?.ExpiryDate,
                    IsRestoreInProgress = response.RestoreInProgress,
                    RestoreExpiration = response.RestoreExpiryDate;
                };

                return fileInfo;
            }
            catch (AmazonS3Exception s3Ex) when (s3Ex.ErrorCode == "NotFound")
            {
                _logger.LogWarning("File not found in S3. Bucket: {Bucket}, Key: {Key}", bucketName, key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get file info from S3. Bucket: {Bucket}, Key: {Key}", bucketName, key);
                throw new S3ServiceException($"Failed to get file info from S3: {ex.Message}", ex);
            }
        }

        public async Task<List<S3ObjectInfo>> ListFilesAsync(string bucketName, string prefix = null, int maxKeys = 1000)
        {
            try
            {
                _logger.LogDebug("Listing files from S3. Bucket: {Bucket}, Prefix: {Prefix}, MaxKeys: {MaxKeys}",
                    bucketName, prefix ?? "null", maxKeys);

                var objects = new List<S3ObjectInfo>();
                var request = new ListObjectsV2Request;
                {
                    BucketName = bucketName,
                    Prefix = prefix,
                    MaxKeys = maxKeys;
                };

                ListObjectsV2Response response;

                do;
                {
                    response = await _s3Client.ListObjectsV2Async(request);

                    foreach (var s3Object in response.S3Objects)
                    {
                        objects.Add(new S3ObjectInfo;
                        {
                            Key = s3Object.Key,
                            BucketName = bucketName,
                            Size = s3Object.Size,
                            LastModified = s3Object.LastModified,
                            ETag = s3Object.ETag,
                            StorageClass = s3Object.StorageClass,
                            Owner = s3Object.Owner?.DisplayName;
                        });
                    }

                    request.ContinuationToken = response.NextContinuationToken;

                } while (response.IsTruncated);

                _logger.LogDebug("Listed {Count} files from S3 bucket {Bucket}", objects.Count, bucketName);

                return objects;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list files from S3 bucket {Bucket}", bucketName);
                throw new S3ServiceException($"Failed to list files from S3: {ex.Message}", ex);
            }
        }

        public async Task<string> GeneratePresignedUrlAsync(S3PresignedUrlRequest request)
        {
            try
            {
                _logger.LogDebug("Generating presigned URL. Bucket: {Bucket}, Key: {Key}, Expiry: {ExpiryHours} hours",
                    request.BucketName, request.Key, request.ExpiryHours);

                var urlRequest = new GetPreSignedUrlRequest;
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    Expires = DateTime.UtcNow.AddHours(request.ExpiryHours),
                    Verb = GetHttpVerb(request.Verb),
                    Protocol = request.UseHttps ? Protocol.HTTPS : Protocol.HTTP;
                };

                // Response headers;
                if (request.ResponseHeaders != null)
                {
                    foreach (var header in request.ResponseHeaders)
                    {
                        urlRequest.ResponseHeaderOverrides[header.Key] = header.Value;
                    }
                }

                var url = _s3Client.GetPreSignedURL(urlRequest);

                _logger.LogDebug("Generated presigned URL for {Key}: {Url}", request.Key, url);

                return url;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate presigned URL for {Key}", request.Key);
                throw new S3ServiceException($"Failed to generate presigned URL: {ex.Message}", ex);
            }
        }

        public async Task<S3CopyResult> CopyFileAsync(S3CopyRequest request)
        {
            try
            {
                _logger.LogInformation("Copying file in S3. Source: {SourceBucket}/{SourceKey} -> Destination: {DestBucket}/{DestKey}",
                    request.SourceBucket, request.SourceKey, request.DestinationBucket, request.DestinationKey);

                var copyRequest = new CopyObjectRequest;
                {
                    SourceBucket = request.SourceBucket,
                    SourceKey = request.SourceKey,
                    DestinationBucket = request.DestinationBucket,
                    DestinationKey = request.DestinationKey,
                    SourceVersionId = request.SourceVersionId,
                    StorageClass = GetStorageClass(request.StorageClass),
                    ServerSideEncryptionMethod = GetEncryptionMethod(request.Encryption),
                    CannedACL = GetCannedACL(request.AccessControl)
                };

                // Metadata copy;
                if (request.CopyMetadata)
                {
                    copyRequest.MetadataDirective = S3MetadataDirective.COPY;
                }
                else;
                {
                    copyRequest.MetadataDirective = S3MetadataDirective.REPLACE;
                    if (request.NewMetadata != null)
                    {
                        foreach (var metadata in request.NewMetadata)
                        {
                            copyRequest.Metadata.Add(metadata.Key, metadata.Value);
                        }
                    }
                }

                var response = await _s3Client.CopyObjectAsync(copyRequest);

                var result = new S3CopyResult;
                {
                    Success = response.HttpStatusCode == HttpStatusCode.OK,
                    SourceBucket = request.SourceBucket,
                    SourceKey = request.SourceKey,
                    DestinationBucket = request.DestinationBucket,
                    DestinationKey = request.DestinationKey,
                    ETag = response.ETag,
                    VersionId = response.VersionId,
                    CopyTime = DateTime.UtcNow,
                    CopySourceVersionId = response.CopySourceVersionId;
                };

                _logger.LogInformation("File copied successfully in S3. Source: {SourceKey} -> Destination: {DestKey}",
                    request.SourceKey, request.DestinationKey);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy file in S3. Source: {SourceKey}, Destination: {DestKey}",
                    request.SourceKey, request.DestinationKey);
                throw new S3ServiceException($"Failed to copy file in S3: {ex.Message}", ex);
            }
        }

        public async Task<S3MoveResult> MoveFileAsync(S3MoveRequest request)
        {
            try
            {
                _logger.LogInformation("Moving file in S3. Source: {SourceKey} -> Destination: {DestKey}",
                    request.SourceKey, request.DestinationKey);

                // Önce kopyala;
                var copyRequest = new S3CopyRequest;
                {
                    SourceBucket = request.SourceBucket,
                    SourceKey = request.SourceKey,
                    DestinationBucket = request.DestinationBucket,
                    DestinationKey = request.DestinationKey,
                    StorageClass = request.StorageClass,
                    Encryption = request.Encryption,
                    AccessControl = request.AccessControl,
                    CopyMetadata = true;
                };

                var copyResult = await CopyFileAsync(copyRequest);

                if (!copyResult.Success)
                {
                    throw new S3ServiceException("Copy operation failed during move");
                }

                // Sonra source'u sil;
                var deleteRequest = new S3DeleteRequest;
                {
                    BucketName = request.SourceBucket,
                    Key = request.SourceKey;
                };

                var deleteResult = await DeleteFileAsync(deleteRequest);

                var result = new S3MoveResult;
                {
                    Success = copyResult.Success && deleteResult.Success,
                    SourceBucket = request.SourceBucket,
                    SourceKey = request.SourceKey,
                    DestinationBucket = request.DestinationBucket,
                    DestinationKey = request.DestinationKey,
                    CopyResult = copyResult,
                    DeleteResult = deleteResult,
                    MoveTime = DateTime.UtcNow;
                };

                _logger.LogInformation("File moved successfully in S3. Source: {SourceKey} -> Destination: {DestKey}",
                    request.SourceKey, request.DestinationKey);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to move file in S3. Source: {SourceKey}, Destination: {DestKey}",
                    request.SourceKey, request.DestinationKey);
                throw new S3ServiceException($"Failed to move file in S3: {ex.Message}", ex);
            }
        }

        public async Task<S3MultipartUpload> StartMultipartUploadAsync(S3MultipartUploadRequest request)
        {
            try
            {
                _logger.LogInformation("Starting multipart upload. Bucket: {Bucket}, Key: {Key}",
                    request.BucketName, request.Key);

                var initiateRequest = new InitiateMultipartUploadRequest;
                {
                    BucketName = request.BucketName,
                    Key = request.Key,
                    ContentType = request.ContentType,
                    StorageClass = GetStorageClass(request.StorageClass),
                    ServerSideEncryptionMethod = GetEncryptionMethod(request.Encryption),
                    CannedACL = GetCannedACL(request.AccessControl)
                };

                var response = await _s3Client.InitiateMultipartUploadAsync(initiateRequest);

                var multipartUpload = new S3MultipartUpload;
                {
                    UploadId = response.UploadId,
                    BucketName = request.BucketName,
                    Key = request.Key,
                    Initiated = DateTime.UtcNow,
                    Parts = new List<S3MultipartUploadPart>(),
                    IsCompleted = false;
                };

                // Cache'e ekle;
                _multipartUploads[response.UploadId] = multipartUpload;

                _logger.LogInformation("Multipart upload started. UploadId: {UploadId}, Bucket: {Bucket}, Key: {Key}",
                    response.UploadId, request.BucketName, request.Key);

                return multipartUpload;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start multipart upload for {Key}", request.Key);
                throw new S3ServiceException($"Failed to start multipart upload: {ex.Message}", ex);
            }
        }

        public async Task<S3UploadResult> CompleteMultipartUploadAsync(S3MultipartUploadCompleteRequest request)
        {
            try
            {
                _logger.LogInformation("Completing multipart upload. UploadId: {UploadId}, Parts: {PartsCount}",
                    request.UploadId, request.Parts.Count);

                if (!_multipartUploads.TryGetValue(request.UploadId, out var multipartUpload))
                {
                    throw new S3ServiceException($"Multipart upload not found: {request.UploadId}");
                }

                var partETags = request.Parts.Select(p => new PartETag(p.PartNumber, p.ETag)).ToList();

                var completeRequest = new CompleteMultipartUploadRequest;
                {
                    BucketName = multipartUpload.BucketName,
                    Key = multipartUpload.Key,
                    UploadId = request.UploadId,
                    PartETags = partETags;
                };

                var response = await _s3Client.CompleteMultipartUploadAsync(completeRequest);

                // Cache'ten kaldır;
                _multipartUploads.TryRemove(request.UploadId, out _);

                var result = new S3UploadResult;
                {
                    Success = response.HttpStatusCode == HttpStatusCode.OK,
                    BucketName = multipartUpload.BucketName,
                    Key = multipartUpload.Key,
                    ETag = response.ETag,
                    VersionId = response.VersionId,
                    UploadTime = DateTime.UtcNow,
                    IsMultipartUpload = true,
                    PartsCount = partETags.Count,
                    UploadId = request.UploadId;
                };

                _logger.LogInformation("Multipart upload completed. UploadId: {UploadId}, ETag: {ETag}",
                    request.UploadId, response.ETag);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete multipart upload {UploadId}", request.UploadId);
                throw new S3ServiceException($"Failed to complete multipart upload: {ex.Message}", ex);
            }
        }

        public async Task<S3BucketResult> CreateBucketAsync(string bucketName, S3Region region = S3Region.USEast1)
        {
            try
            {
                _logger.LogInformation("Creating S3 bucket: {BucketName} in region: {Region}", bucketName, region);

                // Bucket adı validation;
                if (!AmazonS3Util.ValidateV2BucketName(bucketName))
                {
                    throw new ArgumentException($"Invalid bucket name: {bucketName}");
                }

                var regionEndpoint = RegionEndpoint.GetBySystemName(region.ToString());
                var request = new PutBucketRequest;
                {
                    BucketName = bucketName,
                    BucketRegion = region == S3Region.USEast1 ? S3Region.US : region,
                    UseClientRegion = region == S3Region.USEast1;
                };

                var response = await _s3Client.PutBucketAsync(request);

                // Bucket info'yu cache'e ekle;
                var bucketInfo = new S3BucketInfo;
                {
                    Name = bucketName,
                    Region = region.ToString(),
                    CreationDate = DateTime.UtcNow,
                    IsVersioningEnabled = false,
                    HasLifecycleRules = false;
                };

                _bucketCache[bucketName] = bucketInfo;

                var result = new S3BucketResult;
                {
                    Success = response.HttpStatusCode == HttpStatusCode.OK,
                    BucketName = bucketName,
                    Region = region.ToString(),
                    CreationDate = DateTime.UtcNow;
                };

                _logger.LogInformation("S3 bucket created successfully: {BucketName}", bucketName);

                return result;
            }
            catch (AmazonS3Exception s3Ex) when (s3Ex.ErrorCode == "BucketAlreadyExists")
            {
                _logger.LogWarning("S3 bucket already exists: {BucketName}", bucketName);
                return new S3BucketResult;
                {
                    Success = false,
                    BucketName = bucketName,
                    ErrorMessage = "Bucket already exists"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create S3 bucket: {BucketName}", bucketName);
                throw new S3ServiceException($"Failed to create S3 bucket: {ex.Message}", ex);
            }
        }

        public async Task<S3BucketResult> DeleteBucketAsync(string bucketName)
        {
            try
            {
                _logger.LogInformation("Deleting S3 bucket: {BucketName}", bucketName);

                // Bucket'ın boş olup olmadığını kontrol et;
                var objects = await ListFilesAsync(bucketName, maxKeys: 1);
                if (objects.Any())
                {
                    throw new S3ServiceException($"Bucket {bucketName} is not empty");
                }

                var request = new DeleteBucketRequest;
                {
                    BucketName = bucketName;
                };

                var response = await _s3Client.DeleteBucketAsync(request);

                // Cache'ten kaldır;
                _bucketCache.TryRemove(bucketName, out _);

                var result = new S3BucketResult;
                {
                    Success = response.HttpStatusCode == HttpStatusCode.NoContent,
                    BucketName = bucketName,
                    DeletionDate = DateTime.UtcNow;
                };

                _logger.LogInformation("S3 bucket deleted successfully: {BucketName}", bucketName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete S3 bucket: {BucketName}", bucketName);
                throw new S3ServiceException($"Failed to delete S3 bucket: {ex.Message}", ex);
            }
        }

        public async Task SetBucketPolicyAsync(string bucketName, string policy)
        {
            try
            {
                _logger.LogInformation("Setting bucket policy for: {BucketName}", bucketName);

                var request = new PutBucketPolicyRequest;
                {
                    BucketName = bucketName,
                    Policy = policy;
                };

                await _s3Client.PutBucketPolicyAsync(request);

                _logger.LogInformation("Bucket policy set successfully for: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set bucket policy for: {BucketName}", bucketName);
                throw new S3ServiceException($"Failed to set bucket policy: {ex.Message}", ex);
            }
        }

        public async Task ConfigureCorsAsync(string bucketName, List<CorsRule> rules)
        {
            try
            {
                _logger.LogInformation("Configuring CORS for bucket: {BucketName}", bucketName);

                var corsConfiguration = new CORSConfiguration;
                {
                    Rules = rules.Select(r => new CORSRule;
                    {
                        AllowedHeaders = r.AllowedHeaders,
                        AllowedMethods = r.AllowedMethods,
                        AllowedOrigins = r.AllowedOrigins,
                        ExposeHeaders = r.ExposeHeaders,
                        MaxAgeSeconds = r.MaxAgeSeconds;
                    }).ToList()
                };

                var request = new PutCORSConfigurationRequest;
                {
                    BucketName = bucketName,
                    Configuration = corsConfiguration;
                };

                await _s3Client.PutCORSConfigurationAsync(request);

                _logger.LogInformation("CORS configured successfully for bucket: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure CORS for bucket: {BucketName}", bucketName);
                throw new S3ServiceException($"Failed to configure CORS: {ex.Message}", ex);
            }
        }

        public async Task ConfigureLifecycleAsync(string bucketName, List<LifecycleRule> rules)
        {
            try
            {
                _logger.LogInformation("Configuring lifecycle rules for bucket: {BucketName}", bucketName);

                var lifecycleConfiguration = new LifecycleConfiguration;
                {
                    Rules = rules.Select(r => new LifecycleRule;
                    {
                        Id = r.Id,
                        Prefix = r.Prefix,
                        Status = r.Status ? LifecycleRuleStatus.Enabled : LifecycleRuleStatus.Disabled,
                        Expiration = r.Days.HasValue ? new LifecycleRuleExpiration { Days = r.Days.Value } : null,
                        Transitions = r.Transitions?.Select(t => new LifecycleTransition;
                        {
                            Days = t.Days,
                            StorageClass = GetStorageClass(t.StorageClass)
                        }).ToList(),
                        NoncurrentVersionExpiration = r.NoncurrentVersionDays.HasValue ?
                            new LifecycleRuleNoncurrentVersionExpiration { NoncurrentDays = r.NoncurrentVersionDays.Value } : null;
                    }).ToList()
                };

                var request = new PutLifecycleConfigurationRequest;
                {
                    BucketName = bucketName,
                    Configuration = lifecycleConfiguration;
                };

                await _s3Client.PutLifecycleConfigurationAsync(request);

                _logger.LogInformation("Lifecycle rules configured for bucket: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure lifecycle rules for bucket: {BucketName}", bucketName);
                throw new S3ServiceException($"Failed to configure lifecycle rules: {ex.Message}", ex);
            }
        }

        public async Task EnableVersioningAsync(string bucketName)
        {
            try
            {
                _logger.LogInformation("Enabling versioning for bucket: {BucketName}", bucketName);

                var request = new PutBucketVersioningRequest;
                {
                    BucketName = bucketName,
                    VersioningConfig = new S3BucketVersioningConfig;
                    {
                        Status = VersionStatus.Enabled;
                    }
                };

                await _s3Client.PutBucketVersioningAsync(request);

                // Cache'i güncelle;
                if (_bucketCache.TryGetValue(bucketName, out var bucketInfo))
                {
                    bucketInfo.IsVersioningEnabled = true;
                }

                _logger.LogInformation("Versioning enabled for bucket: {BucketName}", bucketName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enable versioning for bucket: {BucketName}", bucketName);
                throw new S3ServiceException($"Failed to enable versioning: {ex.Message}", ex);
            }
        }

        public async Task<long> GetFileSizeAsync(string bucketName, string key)
        {
            try
            {
                var fileInfo = await GetFileInfoAsync(bucketName, key);
                return fileInfo?.ContentLength ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get file size for {Bucket}/{Key}", bucketName, key);
                throw;
            }
        }

        public async Task<string> GetFileContentTypeAsync(string bucketName, string key)
        {
            try
            {
                var fileInfo = await GetFileInfoAsync(bucketName, key);
                return fileInfo?.ContentType;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get content type for {Bucket}/{Key}", bucketName, key);
                throw;
            }
        }

        public async Task<S3BatchResult> ProcessBatchAsync(S3BatchRequest request)
        {
            try
            {
                _logger.LogInformation("Processing batch operation with {Count} items", request.Operations.Count);

                var results = new List<S3BatchOperationResult>();
                var tasks = new List<Task>();

                foreach (var operation in request.Operations)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            S3BatchOperationResult result = null;

                            switch (operation.Type)
                            {
                                case S3BatchOperationType.Upload:
                                    var uploadResult = await UploadFileAsync(operation.UploadRequest);
                                    result = new S3BatchOperationResult;
                                    {
                                        OperationId = operation.Id,
                                        Type = operation.Type,
                                        Success = uploadResult.Success,
                                        Result = uploadResult,
                                        Error = null;
                                    };
                                    break;

                                case S3BatchOperationType.Download:
                                    var downloadResult = await DownloadFileAsync(operation.DownloadRequest);
                                    result = new S3BatchOperationResult;
                                    {
                                        OperationId = operation.Id,
                                        Type = operation.Type,
                                        Success = downloadResult.Success,
                                        Result = downloadResult,
                                        Error = null;
                                    };
                                    break;

                                case S3BatchOperationType.Delete:
                                    var deleteResult = await DeleteFileAsync(operation.DeleteRequest);
                                    result = new S3BatchOperationResult;
                                    {
                                        OperationId = operation.Id,
                                        Type = operation.Type,
                                        Success = deleteResult.Success,
                                        Result = deleteResult,
                                        Error = null;
                                    };
                                    break;

                                case S3BatchOperationType.Copy:
                                    var copyResult = await CopyFileAsync(operation.CopyRequest);
                                    result = new S3BatchOperationResult;
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
                            var errorResult = new S3BatchOperationResult;
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

                var batchResult = new S3BatchResult;
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
                throw new S3ServiceException($"Failed to process batch operation: {ex.Message}", ex);
            }
        }

        #region Helper Methods;

        private void ValidateUploadRequest(S3UploadRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.BucketName))
                throw new ArgumentException("Bucket name is required", nameof(request.BucketName));

            if (string.IsNullOrEmpty(request.Key))
                throw new ArgumentException("Key is required", nameof(request.Key));

            if (request.FileStream == null || request.FileStream.Length == 0)
                throw new ArgumentException("File stream is empty", nameof(request.FileStream));

            if (!request.FileStream.CanRead)
                throw new ArgumentException("File stream is not readable", nameof(request.FileStream));
        }

        private void ValidateDownloadRequest(S3DownloadRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.BucketName))
                throw new ArgumentException("Bucket name is required", nameof(request.BucketName));

            if (string.IsNullOrEmpty(request.Key))
                throw new ArgumentException("Key is required", nameof(request.Key));
        }

        private S3StorageClass GetStorageClass(S3StorageClassType storageClass)
        {
            return storageClass switch;
            {
                S3StorageClassType.Standard => S3StorageClass.Standard,
                S3StorageClassType.StandardIA => S3StorageClass.StandardInfrequentAccess,
                S3StorageClassType.OneZoneIA => S3StorageClass.OneZoneInfrequentAccess,
                S3StorageClassType.Glacier => S3StorageClass.Glacier,
                S3StorageClassType.GlacierIR => S3StorageClass.GlacierInstantRetrieval,
                S3StorageClassType.DeepArchive => S3StorageClass.DeepArchive,
                S3StorageClassType.IntelligentTiering => S3StorageClass.IntelligentTiering,
                _ => S3StorageClass.Standard;
            };
        }

        private ServerSideEncryptionMethod GetEncryptionMethod(S3EncryptionType encryption)
        {
            return encryption switch;
            {
                S3EncryptionType.AES256 => ServerSideEncryptionMethod.AES256,
                S3EncryptionType.AWSKMS => ServerSideEncryptionMethod.AWSKMS,
                _ => ServerSideEncryptionMethod.None;
            };
        }

        private S3CannedACL GetCannedACL(S3AccessControlType accessControl)
        {
            return accessControl switch;
            {
                S3AccessControlType.Private => S3CannedACL.Private,
                S3AccessControlType.PublicRead => S3CannedACL.PublicRead,
                S3AccessControlType.PublicReadWrite => S3CannedACL.PublicReadWrite,
                S3AccessControlType.AuthenticatedRead => S3CannedACL.AuthenticatedRead,
                S3AccessControlType.BucketOwnerRead => S3CannedACL.BucketOwnerRead,
                S3AccessControlType.BucketOwnerFullControl => S3CannedACL.BucketOwnerFullControl,
                _ => S3CannedACL.Private;
            };
        }

        private HttpVerb GetHttpVerb(S3HttpVerb verb)
        {
            return verb switch;
            {
                S3HttpVerb.GET => HttpVerb.GET,
                S3HttpVerb.PUT => HttpVerb.PUT,
                S3HttpVerb.DELETE => HttpVerb.DELETE,
                S3HttpVerb.HEAD => HttpVerb.HEAD,
                S3HttpVerb.POST => HttpVerb.POST,
                _ => HttpVerb.GET;
            };
        }

        private async Task PublishUploadEventAsync(S3UploadRequest request, S3UploadResult result)
        {
            try
            {
                // Gerçek implementasyonda EventBus kullanılır;
                await Task.CompletedTask;

                _logger.LogDebug("Upload event published for {Key}", request.Key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish upload event for {Key}", request.Key);
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
                    _s3Client?.Dispose();
                    _transferUtility?.Dispose();
                    _uploadSemaphore?.Dispose();
                }

                _disposed = true;
            }
        }

        ~S3Service()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Data Models;

    public class S3UploadRequest;
    {
        public string BucketName { get; set; }
        public string Key { get; set; }
        public Stream FileStream { get; set; }
        public string ContentType { get; set; }
        public S3StorageClassType StorageClass { get; set; } = S3StorageClassType.Standard;
        public S3EncryptionType Encryption { get; set; } = S3EncryptionType.None;
        public S3AccessControlType AccessControl { get; set; } = S3AccessControlType.Private;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public bool AutoCloseStream { get; set; } = true;
        public Action<long, long> ProgressCallback { get; set; }
    }

    public class S3UploadResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string ETag { get; set; }
        public string VersionId { get; set; }
        public long FileSize { get; set; }
        public DateTime UploadTime { get; set; }
        public string StorageClass { get; set; }
        public string ServerSideEncryption { get; set; }
        public bool IsMultipartUpload { get; set; }
        public int PartsCount { get; set; }
        public string UploadId { get; set; }
    }

    public class S3DownloadRequest;
    {
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string VersionId { get; set; }
        public long? ByteRangeStart { get; set; }
        public long? ByteRangeEnd { get; set; }
    }

    public class S3DownloadResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string Key { get; set; }
        public Stream FileStream { get; set; }
        public string ContentType { get; set; }
        public long ContentLength { get; set; }
        public string ETag { get; set; }
        public string VersionId { get; set; }
        public DateTime LastModified { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public DateTime DownloadTime { get; set; }
    }

    public class S3DeleteRequest;
    {
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string VersionId { get; set; }
    }

    public class S3DeleteResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string VersionId { get; set; }
        public bool DeleteMarker { get; set; }
        public string DeleteMarkerVersionId { get; set; }
        public DateTime DeleteTime { get; set; }
    }

    public class S3FileInfo;
    {
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string ContentType { get; set; }
        public long ContentLength { get; set; }
        public string ETag { get; set; }
        public DateTime LastModified { get; set; }
        public string StorageClass { get; set; }
        public string VersionId { get; set; }
        public string ServerSideEncryption { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public DateTime? Expiration { get; set; }
        public bool IsRestoreInProgress { get; set; }
        public DateTime? RestoreExpiration { get; set; }
    }

    public class S3ObjectInfo;
    {
        public string Key { get; set; }
        public string BucketName { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string ETag { get; set; }
        public string StorageClass { get; set; }
        public string Owner { get; set; }
    }

    public class S3PresignedUrlRequest;
    {
        public string BucketName { get; set; }
        public string Key { get; set; }
        public double ExpiryHours { get; set; } = 1.0;
        public S3HttpVerb Verb { get; set; } = S3HttpVerb.GET;
        public bool UseHttps { get; set; } = true;
        public Dictionary<string, string> ResponseHeaders { get; set; } = new Dictionary<string, string>();
    }

    public class S3CopyRequest;
    {
        public string SourceBucket { get; set; }
        public string SourceKey { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationKey { get; set; }
        public string SourceVersionId { get; set; }
        public S3StorageClassType StorageClass { get; set; } = S3StorageClassType.Standard;
        public S3EncryptionType Encryption { get; set; } = S3EncryptionType.None;
        public S3AccessControlType AccessControl { get; set; } = S3AccessControlType.Private;
        public bool CopyMetadata { get; set; } = true;
        public Dictionary<string, string> NewMetadata { get; set; } = new Dictionary<string, string>();
    }

    public class S3CopyResult;
    {
        public bool Success { get; set; }
        public string SourceBucket { get; set; }
        public string SourceKey { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationKey { get; set; }
        public string ETag { get; set; }
        public string VersionId { get; set; }
        public DateTime CopyTime { get; set; }
        public string CopySourceVersionId { get; set; }
    }

    public class S3MoveRequest;
    {
        public string SourceBucket { get; set; }
        public string SourceKey { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationKey { get; set; }
        public S3StorageClassType StorageClass { get; set; } = S3StorageClassType.Standard;
        public S3EncryptionType Encryption { get; set; } = S3EncryptionType.None;
        public S3AccessControlType AccessControl { get; set; } = S3AccessControlType.Private;
    }

    public class S3MoveResult;
    {
        public bool Success { get; set; }
        public string SourceBucket { get; set; }
        public string SourceKey { get; set; }
        public string DestinationBucket { get; set; }
        public string DestinationKey { get; set; }
        public S3CopyResult CopyResult { get; set; }
        public S3DeleteResult DeleteResult { get; set; }
        public DateTime MoveTime { get; set; }
    }

    public class S3MultipartUploadRequest;
    {
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string ContentType { get; set; } = "application/octet-stream";
        public S3StorageClassType StorageClass { get; set; } = S3StorageClassType.Standard;
        public S3EncryptionType Encryption { get; set; } = S3EncryptionType.None;
        public S3AccessControlType AccessControl { get; set; } = S3AccessControlType.Private;
    }

    public class S3MultipartUpload;
    {
        public string UploadId { get; set; }
        public string BucketName { get; set; }
        public string Key { get; set; }
        public DateTime Initiated { get; set; }
        public List<S3MultipartUploadPart> Parts { get; set; } = new List<S3MultipartUploadPart>();
        public bool IsCompleted { get; set; }
    }

    public class S3MultipartUploadPart;
    {
        public int PartNumber { get; set; }
        public string ETag { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
    }

    public class S3MultipartUploadCompleteRequest;
    {
        public string UploadId { get; set; }
        public List<S3MultipartUploadPart> Parts { get; set; } = new List<S3MultipartUploadPart>();
    }

    public class S3BucketResult;
    {
        public bool Success { get; set; }
        public string BucketName { get; set; }
        public string Region { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime? DeletionDate { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class S3BucketInfo;
    {
        public string Name { get; set; }
        public string Region { get; set; }
        public DateTime CreationDate { get; set; }
        public bool IsVersioningEnabled { get; set; }
        public bool HasLifecycleRules { get; set; }
    }

    public class CorsRule;
    {
        public List<string> AllowedHeaders { get; set; } = new List<string>();
        public List<string> AllowedMethods { get; set; } = new List<string>();
        public List<string> AllowedOrigins { get; set; } = new List<string>();
        public List<string> ExposeHeaders { get; set; } = new List<string>();
        public int MaxAgeSeconds { get; set; } = 3000;
    }

    public class LifecycleRule;
    {
        public string Id { get; set; }
        public string Prefix { get; set; }
        public bool Status { get; set; } = true;
        public int? Days { get; set; }
        public List<LifecycleTransition> Transitions { get; set; } = new List<LifecycleTransition>();
        public int? NoncurrentVersionDays { get; set; }
    }

    public class LifecycleTransition;
    {
        public int Days { get; set; }
        public S3StorageClassType StorageClass { get; set; }
    }

    public class S3BatchRequest;
    {
        public List<S3BatchOperation> Operations { get; set; } = new List<S3BatchOperation>();
        public int MaxConcurrency { get; set; } = 10;
    }

    public class S3BatchOperation;
    {
        public string Id { get; set; }
        public S3BatchOperationType Type { get; set; }
        public S3UploadRequest UploadRequest { get; set; }
        public S3DownloadRequest DownloadRequest { get; set; }
        public S3DeleteRequest DeleteRequest { get; set; }
        public S3CopyRequest CopyRequest { get; set; }
    }

    public class S3BatchResult;
    {
        public int TotalOperations { get; set; }
        public int SuccessfulOperations { get; set; }
        public int FailedOperations { get; set; }
        public List<S3BatchOperationResult> OperationResults { get; set; } = new List<S3BatchOperationResult>();
        public DateTime CompletionTime { get; set; }
    }

    public class S3BatchOperationResult;
    {
        public string OperationId { get; set; }
        public S3BatchOperationType Type { get; set; }
        public bool Success { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
    }

    #endregion;

    #region Enums;

    public enum S3Region;
    {
        USEast1,
        USEast2,
        USWest1,
        USWest2,
        EUWest1,
        EUWest2,
        EUCentral1,
        APSoutheast1,
        APSoutheast2,
        APNortheast1,
        APNortheast2,
        SAEast1,
        CACentral1;
    }

    public enum S3StorageClassType;
    {
        Standard,
        StandardIA,
        OneZoneIA,
        Glacier,
        GlacierIR,
        DeepArchive,
        IntelligentTiering;
    }

    public enum S3EncryptionType;
    {
        None,
        AES256,
        AWSKMS;
    }

    public enum S3AccessControlType;
    {
        Private,
        PublicRead,
        PublicReadWrite,
        AuthenticatedRead,
        BucketOwnerRead,
        BucketOwnerFullControl;
    }

    public enum S3HttpVerb;
    {
        GET,
        PUT,
        DELETE,
        HEAD,
        POST;
    }

    public enum S3BatchOperationType;
    {
        Upload,
        Download,
        Delete,
        Copy,
        Move;
    }

    #endregion;

    #region Exceptions;

    public class S3ServiceException : Exception
    {
        public S3ServiceException(string message) : base(message) { }
        public S3ServiceException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class S3FileNotFoundException : S3ServiceException;
    {
        public S3FileNotFoundException(string message) : base(message) { }
        public S3FileNotFoundException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
