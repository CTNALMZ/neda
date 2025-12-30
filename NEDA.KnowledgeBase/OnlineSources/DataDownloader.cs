using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NEDA.Core.Common;
using NEDA.Core.Common.Constants;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.ExceptionHandling;
using NEDA.KnowledgeBase.DataManagement;

namespace NEDA.KnowledgeBase.OnlineSources;
{
    /// <summary>
    /// Advanced data downloader with support for multiple protocols, resume capability,
    /// bandwidth management, and comprehensive error handling.
    /// </summary>
    public class DataDownloader : IDataDownloader, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger<DataDownloader> _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IDataRepository _dataRepository;
        private readonly DownloadConfiguration _configuration;

        private bool _isDisposed;
        private readonly SemaphoreSlim _concurrentDownloadsSemaphore;
        private readonly Dictionary<string, DownloadOperation> _activeDownloads;
        private readonly object _downloadsLock = new object();

        private const int DEFAULT_CONCURRENT_DOWNLOADS = 5;
        private const int DEFAULT_BUFFER_SIZE = 81920; // 80KB;
        private const int DEFAULT_MAX_RETRIES = 3;
        private static readonly TimeSpan DEFAULT_TIMEOUT = TimeSpan.FromMinutes(5);

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when download progress changes;
        /// </summary>
        public event EventHandler<DownloadProgressEventArgs> DownloadProgress;

        /// <summary>
        /// Event raised when download completes;
        /// </summary>
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;

        /// <summary>
        /// Event raised when download fails;
        /// </summary>
        public event EventHandler<DownloadFailedEventArgs> DownloadFailed;

        /// <summary>
        /// Event raised when download starts;
        /// </summary>
        public event EventHandler<DownloadStartedEventArgs> DownloadStarted;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of DataDownloader;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="securityManager">Security manager instance</param>
        /// <param name="httpClientFactory">HTTP client factory</param>
        /// <param name="dataRepository">Data repository for storage</param>
        /// <param name="configuration">Download configuration</param>
        public DataDownloader(
            ILogger<DataDownloader> logger,
            ISecurityManager securityManager,
            IHttpClientFactory httpClientFactory,
            IDataRepository dataRepository,
            DownloadConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _dataRepository = dataRepository ?? throw new ArgumentNullException(nameof(dataRepository));

            _configuration = configuration ?? new DownloadConfiguration();
            _concurrentDownloadsSemaphore = new SemaphoreSlim(
                _configuration.MaxConcurrentDownloads ?? DEFAULT_CONCURRENT_DOWNLOADS,
                _configuration.MaxConcurrentDownloads ?? DEFAULT_CONCURRENT_DOWNLOADS);

            _activeDownloads = new Dictionary<string, DownloadOperation>(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(LogEvents.DataDownloaderCreated,
                "DataDownloader initialized with {MaxConcurrent} concurrent downloads",
                _configuration.MaxConcurrentDownloads ?? DEFAULT_CONCURRENT_DOWNLOADS);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Downloads data from a URI to the specified file path;
        /// </summary>
        /// <param name="uri">Source URI</param>
        /// <param name="filePath">Destination file path</param>
        /// <param name="options">Download options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Download result</returns>
        public async Task<DownloadResult> DownloadToFileAsync(
            Uri uri,
            string filePath,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateUri(uri);
            ValidateFilePath(filePath);

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataDownload);

            var operationId = Guid.NewGuid().ToString();
            var downloadOptions = options ?? new DownloadOptions();

            _logger.LogInformation(LogEvents.DownloadStarted,
                "Starting file download: {Uri} -> {FilePath}, Operation: {OperationId}",
                uri, filePath, operationId);

            try
            {
                await _concurrentDownloadsSemaphore.WaitAsync(cancellationToken);

                var downloadOperation = CreateDownloadOperation(operationId, uri, filePath, downloadOptions);
                RegisterActiveDownload(operationId, downloadOperation);

                OnDownloadStarted(new DownloadStartedEventArgs(
                    operationId,
                    uri,
                    filePath,
                    downloadOptions));

                var result = await ExecuteDownloadToFileAsync(
                    downloadOperation,
                    downloadOptions,
                    cancellationToken);

                UnregisterActiveDownload(operationId);
                OnDownloadCompleted(new DownloadCompletedEventArgs(
                    operationId,
                    uri,
                    filePath,
                    result));

                _logger.LogInformation(LogEvents.DownloadCompleted,
                    "File download completed: {Uri} -> {FilePath}, Size: {FileSize}, Duration: {Duration}",
                    uri, filePath, result.FileSize, result.Duration);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.DownloadCancelled,
                    "Download cancelled: {Uri}, Operation: {OperationId}",
                    uri, operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.DownloadFailed, ex,
                    "File download failed: {Uri} -> {FilePath}, Operation: {OperationId}",
                    uri, filePath, operationId);

                OnDownloadFailed(new DownloadFailedEventArgs(
                    operationId,
                    uri,
                    filePath,
                    ex,
                    downloadOptions));

                throw new DataDownloadException(
                    $"Failed to download from {uri} to {filePath}",
                    ex,
                    uri,
                    filePath,
                    operationId);
            }
            finally
            {
                _concurrentDownloadsSemaphore.Release();
            }
        }

        /// <summary>
        /// Downloads data from a URI to a byte array;
        /// </summary>
        /// <param name="uri">Source URI</param>
        /// <param name="options">Download options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Downloaded data as byte array</returns>
        public async Task<byte[]> DownloadToBytesAsync(
            Uri uri,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateUri(uri);

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataDownload);

            var operationId = Guid.NewGuid().ToString();
            var downloadOptions = options ?? new DownloadOptions();

            _logger.LogDebug(LogEvents.DownloadStarted,
                "Starting byte array download: {Uri}, Operation: {OperationId}",
                uri, operationId);

            using (var memoryStream = new MemoryStream())
            {
                var result = await DownloadToStreamAsync(
                    uri,
                    memoryStream,
                    downloadOptions,
                    cancellationToken);

                if (result.Success)
                {
                    _logger.LogInformation(LogEvents.DownloadCompleted,
                        "Byte array download completed: {Uri}, Size: {Size} bytes",
                        uri, memoryStream.Length);

                    return memoryStream.ToArray();
                }
                else;
                {
                    throw new DataDownloadException(
                        $"Failed to download from {uri} to byte array",
                        null,
                        uri,
                        null,
                        operationId);
                }
            }
        }

        /// <summary>
        /// Downloads data from a URI to a stream;
        /// </summary>
        /// <param name="uri">Source URI</param>
        /// <param name="destinationStream">Destination stream</param>
        /// <param name="options">Download options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Download result</returns>
        public async Task<DownloadResult> DownloadToStreamAsync(
            Uri uri,
            Stream destinationStream,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateUri(uri);

            if (destinationStream == null)
                throw new ArgumentNullException(nameof(destinationStream));

            if (!destinationStream.CanWrite)
                throw new ArgumentException("Destination stream must be writable", nameof(destinationStream));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataDownload);

            var operationId = Guid.NewGuid().ToString();
            var downloadOptions = options ?? new DownloadOptions();

            _logger.LogDebug(LogEvents.DownloadStarted,
                "Starting stream download: {Uri}, Operation: {OperationId}",
                uri, operationId);

            try
            {
                await _concurrentDownloadsSemaphore.WaitAsync(cancellationToken);

                var downloadOperation = CreateDownloadOperation(operationId, uri, null, downloadOptions);
                RegisterActiveDownload(operationId, downloadOperation);

                OnDownloadStarted(new DownloadStartedEventArgs(
                    operationId,
                    uri,
                    "Stream",
                    downloadOptions));

                var result = await ExecuteDownloadToStreamAsync(
                    downloadOperation,
                    destinationStream,
                    downloadOptions,
                    cancellationToken);

                UnregisterActiveDownload(operationId);
                OnDownloadCompleted(new DownloadCompletedEventArgs(
                    operationId,
                    uri,
                    "Stream",
                    result));

                _logger.LogInformation(LogEvents.DownloadCompleted,
                    "Stream download completed: {Uri}, Size: {Size} bytes",
                    uri, result.FileSize);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.DownloadCancelled,
                    "Stream download cancelled: {Uri}, Operation: {OperationId}",
                    uri, operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.DownloadFailed, ex,
                    "Stream download failed: {Uri}, Operation: {OperationId}",
                    uri, operationId);

                OnDownloadFailed(new DownloadFailedEventArgs(
                    operationId,
                    uri,
                    "Stream",
                    ex,
                    downloadOptions));

                throw new DataDownloadException(
                    $"Failed to download from {uri} to stream",
                    ex,
                    uri,
                    null,
                    operationId);
            }
            finally
            {
                _concurrentDownloadsSemaphore.Release();
            }
        }

        /// <summary>
        /// Resumes an interrupted download;
        /// </summary>
        /// <param name="uri">Source URI</param>
        /// <param name="filePath">Destination file path</param>
        /// <param name="options">Download options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Download result</returns>
        public async Task<DownloadResult> ResumeDownloadAsync(
            Uri uri,
            string filePath,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateUri(uri);
            ValidateFilePath(filePath);

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataDownload);

            if (!File.Exists(filePath))
            {
                return await DownloadToFileAsync(uri, filePath, options, cancellationToken);
            }

            var fileInfo = new FileInfo(filePath);
            var operationId = Guid.NewGuid().ToString();
            var downloadOptions = options ?? new DownloadOptions();

            _logger.LogInformation(LogEvents.DownloadResumed,
                "Resuming download: {Uri} -> {FilePath}, Existing size: {ExistingSize}, Operation: {OperationId}",
                uri, filePath, fileInfo.Length, operationId);

            downloadOptions.ResumeFrom = fileInfo.Length;

            return await DownloadToFileAsync(uri, filePath, downloadOptions, cancellationToken);
        }

        /// <summary>
        /// Downloads multiple files in parallel;
        /// </summary>
        /// <param name="downloadRequests">Collection of download requests</param>
        /// <param name="parallelLimit">Maximum parallel downloads</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Collection of download results</returns>
        public async Task<IEnumerable<DownloadResult>> DownloadMultipleAsync(
            IEnumerable<DownloadRequest> downloadRequests,
            int? parallelLimit = null,
            CancellationToken cancellationToken = default)
        {
            if (downloadRequests == null)
                throw new ArgumentNullException(nameof(downloadRequests));

            var requests = downloadRequests.ToList();
            if (!requests.Any())
                return Enumerable.Empty<DownloadResult>();

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataDownload);

            var limit = parallelLimit ?? (_configuration.MaxConcurrentDownloads ?? DEFAULT_CONCURRENT_DOWNLOADS);
            var semaphore = new SemaphoreSlim(limit, limit);
            var tasks = new List<Task<DownloadResult>>();

            _logger.LogInformation(LogEvents.BatchDownloadStarted,
                "Starting batch download of {Count} files with parallelism {ParallelLimit}",
                requests.Count, limit);

            foreach (var request in requests)
            {
                tasks.Add(ProcessDownloadRequestAsync(request, semaphore, cancellationToken));
            }

            var results = await Task.WhenAll(tasks);

            _logger.LogInformation(LogEvents.BatchDownloadCompleted,
                "Batch download completed: {SuccessCount} successful, {FailedCount} failed",
                results.Count(r => r.Success), results.Count(r => !r.Success));

            return results;
        }

        /// <summary>
        /// Cancels an active download;
        /// </summary>
        /// <param name="operationId">Download operation ID</param>
        public void CancelDownload(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation ID cannot be null or empty", nameof(operationId));

            DownloadOperation downloadOperation;
            lock (_downloadsLock)
            {
                if (!_activeDownloads.TryGetValue(operationId, out downloadOperation))
                    return;
            }

            downloadOperation.CancellationTokenSource?.Cancel();

            _logger.LogInformation(LogEvents.DownloadCancelled,
                "Download cancellation requested: Operation {OperationId}",
                operationId);
        }

        /// <summary>
        /// Gets information about active downloads;
        /// </summary>
        /// <returns>Collection of active download information</returns>
        public IEnumerable<DownloadStatus> GetActiveDownloads()
        {
            lock (_downloadsLock)
            {
                return _activeDownloads.Values;
                    .Select(op => new DownloadStatus;
                    {
                        OperationId = op.OperationId,
                        Uri = op.Uri,
                        FilePath = op.FilePath,
                        Progress = op.Progress,
                        BytesDownloaded = op.BytesDownloaded,
                        TotalBytes = op.TotalBytes,
                        StartTime = op.StartTime,
                        Status = op.Status;
                    })
                    .ToList();
            }
        }

        #endregion;

        #region Private Methods;

        private async Task<DownloadResult> ExecuteDownloadToFileAsync(
            DownloadOperation downloadOperation,
            DownloadOptions options,
            CancellationToken cancellationToken)
        {
            var uri = downloadOperation.Uri;
            var filePath = downloadOperation.FilePath;
            var operationId = downloadOperation.OperationId;
            var startTime = DateTime.UtcNow;

            // Ensure directory exists;
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Handle resume;
            long startPosition = 0;
            if (options.ResumeFrom.HasValue && File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                startPosition = Math.Min(fileInfo.Length, options.ResumeFrom.Value);
                downloadOperation.BytesDownloaded = startPosition;

                _logger.LogDebug(LogEvents.DownloadResumed,
                    "Resuming download from position {StartPosition}: {Uri}",
                    startPosition, uri);
            }

            using (var fileStream = new FileStream(
                filePath,
                startPosition > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DEFAULT_BUFFER_SIZE,
                useAsync: true))
            {
                return await ExecuteDownloadCoreAsync(
                    downloadOperation,
                    fileStream,
                    options,
                    startPosition,
                    startTime,
                    cancellationToken);
            }
        }

        private async Task<DownloadResult> ExecuteDownloadToStreamAsync(
            DownloadOperation downloadOperation,
            Stream destinationStream,
            DownloadOptions options,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            return await ExecuteDownloadCoreAsync(
                downloadOperation,
                destinationStream,
                options,
                0,
                startTime,
                cancellationToken);
        }

        private async Task<DownloadResult> ExecuteDownloadCoreAsync(
            DownloadOperation downloadOperation,
            Stream destinationStream,
            DownloadOptions options,
            long startPosition,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            var uri = downloadOperation.Uri;
            var operationId = downloadOperation.OperationId;

            using (var httpClient = CreateHttpClient())
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                downloadOperation.CancellationTokenSource.Token))
            {
                // Configure request;
                var request = new HttpRequestMessage(HttpMethod.Get, uri);

                // Add range header for resume;
                if (startPosition > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(startPosition, null);
                }

                // Add custom headers;
                if (options.Headers != null)
                {
                    foreach (var header in options.Headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                // Add authentication if provided;
                if (options.Credentials != null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(
                            System.Text.Encoding.ASCII.GetBytes(
                                $"{options.Credentials.UserName}:{options.Credentials.Password}")));
                }

                // Set timeout;
                var timeout = options.Timeout ?? _configuration.DefaultTimeout ?? DEFAULT_TIMEOUT;
                httpClient.Timeout = timeout;

                // Execute request with retry logic;
                HttpResponseMessage response = null;
                for (int retryCount = 0; retryCount < (options.MaxRetries ?? DEFAULT_MAX_RETRIES); retryCount++)
                {
                    try
                    {
                        response = await httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            linkedCts.Token);

                        response.EnsureSuccessStatusCode();
                        break;
                    }
                    catch (HttpRequestException ex) when (retryCount < (options.MaxRetries ?? DEFAULT_MAX_RETRIES) - 1)
                    {
                        _logger.LogWarning(LogEvents.DownloadRetry,
                            "Download request failed, retry {RetryCount}/{MaxRetries}: {Uri}, Error: {Error}",
                            retryCount + 1, options.MaxRetries ?? DEFAULT_MAX_RETRIES, uri, ex.Message);

                        await Task.Delay(GetRetryDelay(retryCount), linkedCts.Token);
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Failed to download from {uri} after retries");
                }

                // Get content information;
                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                downloadOperation.TotalBytes = totalBytes;

                // Create progress reporter;
                var progress = new Progress<long>(bytesRead =>
                {
                    var totalRead = startPosition + bytesRead;
                    downloadOperation.BytesDownloaded = totalRead;
                    downloadOperation.Progress = totalBytes > 0 ? (double)totalRead / totalBytes : 0;

                    OnDownloadProgress(new DownloadProgressEventArgs(
                        operationId,
                        uri,
                        downloadOperation.FilePath,
                        downloadOperation.Progress,
                        totalRead,
                        totalBytes,
                        DateTime.UtcNow - startTime));
                });

                // Download content;
                using (var responseStream = await response.Content.ReadAsStreamAsync())
                {
                    await CopyToWithProgressAsync(
                        responseStream,
                        destinationStream,
                        DEFAULT_BUFFER_SIZE,
                        totalBytes,
                        progress,
                        linkedCts.Token);
                }

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;
                var fileSize = destinationStream.Length;

                // Update data repository;
                await _dataRepository.SaveDownloadRecordAsync(new DownloadRecord;
                {
                    OperationId = operationId,
                    Uri = uri.ToString(),
                    FilePath = downloadOperation.FilePath,
                    FileSize = fileSize,
                    DownloadTime = duration,
                    StartTime = startTime,
                    EndTime = endTime,
                    Success = true;
                });

                return new DownloadResult;
                {
                    Success = true,
                    OperationId = operationId,
                    Uri = uri,
                    FilePath = downloadOperation.FilePath,
                    FileSize = fileSize,
                    Duration = duration,
                    StartTime = startTime,
                    EndTime = endTime;
                };
            }
        }

        private async Task<DownloadResult> ProcessDownloadRequestAsync(
            DownloadRequest request,
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                if (request.DestinationType == DestinationType.File)
                {
                    return await DownloadToFileAsync(
                        request.Uri,
                        request.Destination,
                        request.Options,
                        cancellationToken);
                }
                else if (request.DestinationType == DestinationType.Stream)
                {
                    throw new NotSupportedException("Stream destination requires explicit stream instance");
                }
                else;
                {
                    var bytes = await DownloadToBytesAsync(
                        request.Uri,
                        request.Options,
                        cancellationToken);

                    return new DownloadResult;
                    {
                        Success = true,
                        Uri = request.Uri,
                        FileSize = bytes.Length,
                        Duration = TimeSpan.Zero // Would need timing;
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.DownloadFailed, ex,
                    "Failed to process download request: {Uri}",
                    request.Uri);

                return new DownloadResult;
                {
                    Success = false,
                    Uri = request.Uri,
                    ErrorMessage = ex.Message,
                    Exception = ex;
                };
            }
            finally
            {
                semaphore.Release();
            }
        }

        private HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient("DataDownloader");

            // Configure default headers;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                _configuration.UserAgent ?? "NEDA-DataDownloader/1.0");

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("*/*"));

            // Configure handler if needed;
            if (_configuration.Proxy != null)
            {
                var handler = new HttpClientHandler;
                {
                    Proxy = _configuration.Proxy,
                    UseProxy = true;
                };

                return new HttpClient(handler);
            }

            return client;
        }

        private async Task CopyToWithProgressAsync(
            Stream source,
            Stream destination,
            int bufferSize,
            long totalBytes,
            IProgress<long> progress,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[bufferSize];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) != 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                totalRead += bytesRead;
                progress?.Report(totalRead);

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private TimeSpan GetRetryDelay(int retryCount)
        {
            // Exponential backoff with jitter;
            var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
            var jitter = new Random().NextDouble() * 0.2; // 20% jitter;
            return TimeSpan.FromSeconds(baseDelay.TotalSeconds * (1 + jitter));
        }

        private DownloadOperation CreateDownloadOperation(
            string operationId,
            Uri uri,
            string filePath,
            DownloadOptions options)
        {
            return new DownloadOperation;
            {
                OperationId = operationId,
                Uri = uri,
                FilePath = filePath,
                Status = DownloadStatusType.Downloading,
                StartTime = DateTime.UtcNow,
                CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    CancellationToken.None),
                Options = options;
            };
        }

        private void RegisterActiveDownload(string operationId, DownloadOperation operation)
        {
            lock (_downloadsLock)
            {
                _activeDownloads[operationId] = operation;
            }
        }

        private void UnregisterActiveDownload(string operationId)
        {
            lock (_downloadsLock)
            {
                _activeDownloads.Remove(operationId);
            }
        }

        private void ValidateUri(Uri uri)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("URI must be absolute", nameof(uri));

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Only HTTP and HTTPS schemes are supported", nameof(uri));
        }

        private void ValidateFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            try
            {
                var fullPath = Path.GetFullPath(filePath);

                // Check if path is valid;
                if (fullPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    throw new ArgumentException("Invalid characters in file path", nameof(filePath));
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid file path", nameof(filePath), ex);
            }
        }

        #endregion;

        #region Event Methods;

        protected virtual void OnDownloadProgress(DownloadProgressEventArgs e)
        {
            DownloadProgress?.Invoke(this, e);
        }

        protected virtual void OnDownloadCompleted(DownloadCompletedEventArgs e)
        {
            DownloadCompleted?.Invoke(this, e);
        }

        protected virtual void OnDownloadFailed(DownloadFailedEventArgs e)
        {
            DownloadFailed?.Invoke(this, e);
        }

        protected virtual void OnDownloadStarted(DownloadStartedEventArgs e)
        {
            DownloadStarted?.Invoke(this, e);
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
                    // Cancel all active downloads;
                    lock (_downloadsLock)
                    {
                        foreach (var operation in _activeDownloads.Values)
                        {
                            operation.CancellationTokenSource?.Cancel();
                            operation.CancellationTokenSource?.Dispose();
                        }
                        _activeDownloads.Clear();
                    }

                    _concurrentDownloadsSemaphore?.Dispose();
                }

                _isDisposed = true;
            }
        }

        ~DataDownloader()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes;

        private class DownloadOperation;
        {
            public string OperationId { get; set; }
            public Uri Uri { get; set; }
            public string FilePath { get; set; }
            public DownloadStatusType Status { get; set; }
            public double Progress { get; set; }
            public long BytesDownloaded { get; set; }
            public long TotalBytes { get; set; }
            public DateTime StartTime { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; set; }
            public DownloadOptions Options { get; set; }
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Interface for data downloader functionality;
    /// </summary>
    public interface IDataDownloader : IDisposable
    {
        /// <summary>
        /// Downloads data from a URI to the specified file path;
        /// </summary>
        Task<DownloadResult> DownloadToFileAsync(
            Uri uri,
            string filePath,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads data from a URI to a byte array;
        /// </summary>
        Task<byte[]> DownloadToBytesAsync(
            Uri uri,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads data from a URI to a stream;
        /// </summary>
        Task<DownloadResult> DownloadToStreamAsync(
            Uri uri,
            Stream destinationStream,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resumes an interrupted download;
        /// </summary>
        Task<DownloadResult> ResumeDownloadAsync(
            Uri uri,
            string filePath,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads multiple files in parallel;
        /// </summary>
        Task<IEnumerable<DownloadResult>> DownloadMultipleAsync(
            IEnumerable<DownloadRequest> downloadRequests,
            int? parallelLimit = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels an active download;
        /// </summary>
        void CancelDownload(string operationId);

        /// <summary>
        /// Gets information about active downloads;
        /// </summary>
        IEnumerable<DownloadStatus> GetActiveDownloads();

        /// <summary>
        /// Event raised when download progress changes;
        /// </summary>
        event EventHandler<DownloadProgressEventArgs> DownloadProgress;

        /// <summary>
        /// Event raised when download completes;
        /// </summary>
        event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;

        /// <summary>
        /// Event raised when download fails;
        /// </summary>
        event EventHandler<DownloadFailedEventArgs> DownloadFailed;

        /// <summary>
        /// Event raised when download starts;
        /// </summary>
        event EventHandler<DownloadStartedEventArgs> DownloadStarted;
    }

    /// <summary>
    /// Download configuration;
    /// </summary>
    public class DownloadConfiguration;
    {
        public int? MaxConcurrentDownloads { get; set; }
        public TimeSpan? DefaultTimeout { get; set; }
        public string UserAgent { get; set; }
        public IWebProxy Proxy { get; set; }
        public bool EnableCompression { get; set; } = true;
        public bool UseCookies { get; set; } = false;
        public bool AllowAutoRedirect { get; set; } = true;
        public int MaxRedirects { get; set; } = 10;
    }

    /// <summary>
    /// Download options for a single operation;
    /// </summary>
    public class DownloadOptions;
    {
        public Dictionary<string, string> Headers { get; set; }
        public NetworkCredential Credentials { get; set; }
        public TimeSpan? Timeout { get; set; }
        public int? MaxRetries { get; set; }
        public long? ResumeFrom { get; set; }
        public bool VerifySSL { get; set; } = true;
        public bool UseCache { get; set; } = false;
        public Action<HttpRequestMessage> RequestConfiguration { get; set; }
    }

    /// <summary>
    /// Download request for batch operations;
    /// </summary>
    public class DownloadRequest;
    {
        public Uri Uri { get; set; }
        public string Destination { get; set; }
        public DestinationType DestinationType { get; set; }
        public DownloadOptions Options { get; set; }
    }

    /// <summary>
    /// Download result;
    /// </summary>
    public class DownloadResult;
    {
        public bool Success { get; set; }
        public string OperationId { get; set; }
        public Uri Uri { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Download status information;
    /// </summary>
    public class DownloadStatus;
    {
        public string OperationId { get; set; }
        public Uri Uri { get; set; }
        public string FilePath { get; set; }
        public double Progress { get; set; }
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public DateTime StartTime { get; set; }
        public DownloadStatusType Status { get; set; }
        public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;
        public double SpeedBytesPerSecond =>
            ElapsedTime.TotalSeconds > 0 ? BytesDownloaded / ElapsedTime.TotalSeconds : 0;
    }

    /// <summary>
    /// Download record for persistence;
    /// </summary>
    public class DownloadRecord;
    {
        public string OperationId { get; set; }
        public string Uri { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public TimeSpan DownloadTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Event arguments for download progress;
    /// </summary>
    public class DownloadProgressEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Uri Uri { get; }
        public string FilePath { get; }
        public double Progress { get; }
        public long BytesDownloaded { get; }
        public long TotalBytes { get; }
        public TimeSpan ElapsedTime { get; }
        public double SpeedBytesPerSecond { get; }

        public DownloadProgressEventArgs(
            string operationId,
            Uri uri,
            string filePath,
            double progress,
            long bytesDownloaded,
            long totalBytes,
            TimeSpan elapsedTime)
        {
            OperationId = operationId;
            Uri = uri;
            FilePath = filePath;
            Progress = progress;
            BytesDownloaded = bytesDownloaded;
            TotalBytes = totalBytes;
            ElapsedTime = elapsedTime;
            SpeedBytesPerSecond = elapsedTime.TotalSeconds > 0 ?
                bytesDownloaded / elapsedTime.TotalSeconds : 0;
        }
    }

    /// <summary>
    /// Event arguments for download completion;
    /// </summary>
    public class DownloadCompletedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Uri Uri { get; }
        public string FilePath { get; }
        public DownloadResult Result { get; }

        public DownloadCompletedEventArgs(
            string operationId,
            Uri uri,
            string filePath,
            DownloadResult result)
        {
            OperationId = operationId;
            Uri = uri;
            FilePath = filePath;
            Result = result;
        }
    }

    /// <summary>
    /// Event arguments for download failure;
    /// </summary>
    public class DownloadFailedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Uri Uri { get; }
        public string FilePath { get; }
        public Exception Exception { get; }
        public DownloadOptions Options { get; }

        public DownloadFailedEventArgs(
            string operationId,
            Uri uri,
            string filePath,
            Exception exception,
            DownloadOptions options)
        {
            OperationId = operationId;
            Uri = uri;
            FilePath = filePath;
            Exception = exception;
            Options = options;
        }
    }

    /// <summary>
    /// Event arguments for download start;
    /// </summary>
    public class DownloadStartedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Uri Uri { get; }
        public string FilePath { get; }
        public DownloadOptions Options { get; }

        public DownloadStartedEventArgs(
            string operationId,
            Uri uri,
            string filePath,
            DownloadOptions options)
        {
            OperationId = operationId;
            Uri = uri;
            FilePath = filePath;
            Options = options;
        }
    }

    /// <summary>
    /// Download status types;
    /// </summary>
    public enum DownloadStatusType;
    {
        Pending,
        Downloading,
        Paused,
        Completed,
        Failed,
        Cancelled;
    }

    /// <summary>
    /// Destination types for download;
    /// </summary>
    public enum DestinationType;
    {
        File,
        Stream,
        Memory;
    }

    /// <summary>
    /// Custom exception for data download errors;
    /// </summary>
    public class DataDownloadException : Exception
    {
        public Uri Uri { get; }
        public string FilePath { get; }
        public string OperationId { get; }

        public DataDownloadException(
            string message,
            Exception innerException,
            Uri uri,
            string filePath,
            string operationId)
            : base(message, innerException)
        {
            Uri = uri;
            FilePath = filePath;
            OperationId = operationId;
        }
    }

    #endregion;
}
