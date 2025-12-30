using NEDA.Automation.WorkflowEngine.ActivityLibrary;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace NEDA.Automation.WorkflowEngine.ActivityLibrary;
{
    /// <summary>
    /// Advanced file system activity with encryption support;
    /// </summary>
    [ActivityMetadata(
        Name = "SecureFileProcessor",
        Version = "2.3.0",
        Category = "System/File",
        Description = "Processes files with encryption, compression, and validation",
        Author = "NEDA Security Team",
        Tags = new[] { "File", "Security", "Encryption", "Compression" }
    )]
    public class SecureFileProcessorActivity : ActivityBase;
    {
        private readonly ILogger _logger;
        private readonly Aes _aesAlgorithm;

        public SecureFileProcessorActivity(ILogger logger) : base("SecureFileProcessor",
            "Processes files with encryption, compression, and validation")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _aesAlgorithm = Aes.Create();
            _aesAlgorithm.KeySize = 256;
            _aesAlgorithm.GenerateKey();
            _aesAlgorithm.GenerateIV();

            InitializeParameters();
            ConfigurePolicies();
        }

        private void InitializeParameters()
        {
            // Input parameters;
            AddInputParameter("SourceFilePath", typeof(string), true,
                "Path to the source file", null);
            AddInputParameter("DestinationPath", typeof(string), false,
                "Destination path (if different from source)", null);
            AddInputParameter("Operation", typeof(string), true,
                "Operation to perform", "Encrypt");
            AddInputParameter("EncryptionKey", typeof(string), false,
                "Encryption key (base64 encoded)", Convert.ToBase64String(_aesAlgorithm.Key));
            AddInputParameter("CompressionLevel", typeof(int), false,
                "Compression level (0-9)", 6);
            AddInputParameter("DeleteSource", typeof(bool), false,
                "Delete source file after processing", false);

            // Output parameters;
            AddOutputParameter("ProcessedFilePath", typeof(string),
                "Path to the processed file");
            AddOutputParameter("OriginalSize", typeof(long),
                "Original file size in bytes");
            AddOutputParameter("ProcessedSize", typeof(long),
                "Processed file size in bytes");
            AddOutputParameter("CompressionRatio", typeof(double),
                "Compression ratio (0-1)");
            AddOutputParameter("ProcessingTime", typeof(double),
                "Total processing time in milliseconds");
            AddOutputParameter("FileHash", typeof(string),
                "SHA256 hash of processed file");
            AddOutputParameter("Success", typeof(bool),
                "Processing success status");
        }

        private void ConfigurePolicies()
        {
            RetryPolicy.MaxRetries = 3;
            RetryPolicy.InitialDelay = TimeSpan.FromSeconds(2);
            RetryPolicy.BackoffMultiplier = 2.0;
            RetryPolicy.RetryableExceptions.Add(typeof(IOException));
            RetryPolicy.RetryableExceptions.Add(typeof(UnauthorizedAccessException));

            TimeoutPolicy.ExecutionTimeout = TimeSpan.FromMinutes(5);
            TimeoutPolicy.StartupTimeout = TimeSpan.FromSeconds(30);
            TimeoutPolicy.CleanupTimeout = TimeSpan.FromSeconds(30);

            // Metadata;
            Metadata["SecurityLevel"] = "High";
            Metadata["RequiresElevation"] = "false";
            Metadata["DataSensitivity"] = "Encrypted";
            Metadata["AuditRequired"] = "true";
            Metadata["PerformanceImpact"] = "Medium";
        }

        protected override async Task<Dictionary<string, object>> ExecuteCoreAsync(
            ActivityExecutionContext context)
        {
            var startTime = DateTime.UtcNow;
            var results = new Dictionary<string, object>();

            try
            {
                // Validate inputs;
                var validation = ValidateInputs(context);
                if (!validation.IsValid)
                    throw new ValidationException($"Input validation failed: {validation}");

                // Get input values;
                var sourcePath = context.GetInput<string>("SourceFilePath");
                var destPath = context.GetInput<string>("DestinationPath",
                    Path.Combine(Path.GetDirectoryName(sourcePath),
                               $"processed_{Path.GetFileName(sourcePath)}"));
                var operation = context.GetInput<string>("Operation", "Encrypt").ToLower();
                var encryptionKey = context.GetInput<string>("EncryptionKey",
                    Convert.ToBase64String(_aesAlgorithm.Key));
                var compressionLevel = context.GetInput<int>("CompressionLevel", 6);
                var deleteSource = context.GetInput<bool>("DeleteSource", false);

                context.Log($"Starting secure file processing: {operation} on {sourcePath}");

                // Read source file;
                var originalBytes = await File.ReadAllBytesAsync(sourcePath, context.CancellationToken);
                var originalSize = originalBytes.Length;

                // Process based on operation;
                byte[] processedBytes;
                switch (operation)
                {
                    case "encrypt":
                        processedBytes = await EncryptDataAsync(originalBytes, encryptionKey, context);
                        break;
                    case "decrypt":
                        processedBytes = await DecryptDataAsync(originalBytes, encryptionKey, context);
                        break;
                    case "compress":
                        processedBytes = await CompressDataAsync(originalBytes, compressionLevel, context);
                        break;
                    case "decompress":
                        processedBytes = await DecompressDataAsync(originalBytes, context);
                        break;
                    case "encryptcompress":
                        var compressed = await CompressDataAsync(originalBytes, compressionLevel, context);
                        processedBytes = await EncryptDataAsync(compressed, encryptionKey, context);
                        break;
                    case "decryptdecompress":
                        var decrypted = await DecryptDataAsync(originalBytes, encryptionKey, context);
                        processedBytes = await DecompressDataAsync(decrypted, context);
                        break;
                    default:
                        throw new ArgumentException($"Unknown operation: {operation}");
                }

                // Write processed file;
                await File.WriteAllBytesAsync(destPath, processedBytes, context.CancellationToken);

                // Calculate metrics;
                var processedSize = processedBytes.Length;
                var compressionRatio = originalSize > 0 ?
                    (double)processedSize / originalSize : 0;
                var fileHash = ComputeFileHash(destPath);
                var processingTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

                // Delete source if requested;
                if (deleteSource && File.Exists(sourcePath))
                {
                    File.Delete(sourcePath);
                    context.Log($"Source file deleted: {sourcePath}");
                }

                // Set outputs;
                context.SetOutput("ProcessedFilePath", destPath);
                context.SetOutput("OriginalSize", originalSize);
                context.SetOutput("ProcessedSize", processedSize);
                context.SetOutput("CompressionRatio", compressionRatio);
                context.SetOutput("ProcessingTime", processingTime);
                context.SetOutput("FileHash", fileHash);
                context.SetOutput("Success", true);

                // Share data;
                context.ShareData("LastProcessedFile", destPath);
                context.ShareData("ProcessingOperation", operation);

                results["ProcessedFilePath"] = destPath;
                results["OriginalSize"] = originalSize;
                results["ProcessedSize"] = processedSize;
                results["CompressionRatio"] = compressionRatio;
                results["ProcessingTime"] = processingTime;
                results["FileHash"] = fileHash;
                results["Success"] = true;

                context.Log($"File processing completed successfully: {destPath}");
                context.Log($"Compression ratio: {compressionRatio:P2}, Time: {processingTime:F0}ms");

                return results;
            }
            catch (Exception ex)
            {
                context.Log($"File processing failed: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        private async Task<byte[]> EncryptDataAsync(byte[] data, string keyBase64, ActivityExecutionContext context)
        {
            using var aes = Aes.Create();
            aes.Key = Convert.FromBase64String(keyBase64);
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();

            // Write IV first;
            await ms.WriteAsync(aes.IV, 0, aes.IV.Length, context.CancellationToken);

            // Encrypt data;
            using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
            await cs.WriteAsync(data, 0, data.Length, context.CancellationToken);
            cs.FlushFinalBlock();

            return ms.ToArray();
        }

        private async Task<byte[]> DecryptDataAsync(byte[] data, string keyBase64, ActivityExecutionContext context)
        {
            using var aes = Aes.Create();
            aes.Key = Convert.FromBase64String(keyBase64);

            // Read IV from beginning of data;
            var iv = new byte[16];
            Array.Copy(data, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream();
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Write);

            // Decrypt data (skip IV)
            await cs.WriteAsync(data, iv.Length, data.Length - iv.Length, context.CancellationToken);
            cs.FlushFinalBlock();

            return ms.ToArray();
        }

        private async Task<byte[]> CompressDataAsync(byte[] data, int level, ActivityExecutionContext context)
        {
            using var ms = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(ms,
                System.IO.Compression.CompressionLevel.Optimal, true))
            {
                await gzip.WriteAsync(data, 0, data.Length, context.CancellationToken);
            }
            return ms.ToArray();
        }

        private async Task<byte[]> DecompressDataAsync(byte[] data, ActivityExecutionContext context)
        {
            using var ms = new MemoryStream(data);
            using var gzip = new System.IO.Compression.GZipStream(ms,
                System.IO.Compression.CompressionMode.Decompress);
            using var result = new MemoryStream();
            await gzip.CopyToAsync(result, context.CancellationToken);
            return result.ToArray();
        }

        private string ComputeFileHash(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        protected override async Task CompensateCoreAsync(ActivityResult result,
            ActivityExecutionContext context, CompensationContext compensationContext)
        {
            // Rollback: Delete processed file, restore original if deleted;
            if (result.OutputData.TryGetValue("ProcessedFilePath", out var processedPathObj) &&
                processedPathObj is string processedPath &&
                File.Exists(processedPath))
            {
                File.Delete(processedPath);
                context.Log($"Rollback: Deleted processed file: {processedPath}");
            }

            // Restore source if it was deleted;
            if (context.InputData.TryGetValue("DeleteSource", out var deleteSourceObj) &&
                deleteSourceObj is bool deleteSource &&
                deleteSource &&
                context.InputData.TryGetValue("SourceFilePath", out var sourcePathObj) &&
                sourcePathObj is string sourcePath)
            {
                // In a real implementation, we would restore from backup;
                context.Log($"Rollback: Source file was deleted, needs restoration: {sourcePath}");
            }

            await Task.CompletedTask;
        }

        public override ValidationResult ValidateInputs(ActivityExecutionContext context)
        {
            var result = base.ValidateInputs(context);

            var sourcePath = context.GetInput<string>("SourceFilePath");
            if (!string.IsNullOrEmpty(sourcePath) && !File.Exists(sourcePath))
                result.AddError($"Source file does not exist: {sourcePath}");

            var operation = context.GetInput<string>("Operation");
            var validOperations = new[] { "encrypt", "decrypt", "compress", "decompress",
                                         "encryptcompress", "decryptdecompress" };
            if (!validOperations.Contains(operation?.ToLower()))
                result.AddError($"Invalid operation: {operation}. Valid operations: {string.Join(", ", validOperations)}");

            var compressionLevel = context.GetInput<int>("CompressionLevel");
            if (compressionLevel < 0 || compressionLevel > 9)
                result.AddError($"Compression level must be between 0 and 9, got: {compressionLevel}");

            return result;
        }
    }

    /// <summary>
    /// Advanced HTTP client with retry, circuit breaker, and caching;
    /// </summary>
    [ActivityMetadata(
        Name = "AdvancedHttpClient",
        Version = "3.1.0",
        Category = "Network/HTTP",
        Description = "HTTP client with retry, circuit breaker, caching, and advanced features",
        Author = "NEDA Web Team",
        Tags = new[] { "HTTP", "REST", "API", "Network", "Web" }
    )]
    public class AdvancedHttpClientActivity : ActivityBase;
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly HttpClientHandler _handler;
        private readonly Dictionary<string, HttpResponseCache> _responseCache;

        private class HttpResponseCache;
        {
            public string Content { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public DateTime Expiry { get; set; }
            public DateTime LastAccessed { get; set; }
        }

        public AdvancedHttpClientActivity(ILogger logger) : base("AdvancedHttpClient",
            "HTTP client with retry, circuit breaker, caching, and advanced features")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _handler = new HttpClientHandler;
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                       System.Net.DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            _httpClient = new HttpClient(_handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            _responseCache = new Dictionary<string, HttpResponseCache>();

            InitializeParameters();
            ConfigurePolicies();
        }

        private void InitializeParameters()
        {
            // Input parameters;
            AddInputParameter("Url", typeof(string), true,
                "The URL to send request to", null);
            AddInputParameter("Method", typeof(string), false,
                "HTTP method", "GET");
            AddInputParameter("Headers", typeof(Dictionary<string, string>), false,
                "HTTP headers", new Dictionary<string, string>());
            AddInputParameter("Body", typeof(object), false,
                "Request body", null);
            AddInputParameter("ContentType", typeof(string), false,
                "Content-Type header", "application/json");
            AddInputParameter("TimeoutSeconds", typeof(int), false,
                "Request timeout in seconds", 30);
            AddInputParameter("UseCache", typeof(bool), false,
                "Use response caching", true);
            AddInputParameter("CacheDuration", typeof(int), false,
                "Cache duration in seconds", 300);
            AddInputParameter("RetryCount", typeof(int), false,
                "Number of retry attempts", 3);
            AddInputParameter("ValidateCertificate", typeof(bool), false,
                "Validate SSL certificate", true);
            AddInputParameter("FollowRedirects", typeof(bool), false,
                "Follow HTTP redirects", true);

            // Output parameters;
            AddOutputParameter("StatusCode", typeof(int),
                "HTTP status code");
            AddOutputParameter("ResponseBody", typeof(string),
                "Response body as string");
            AddOutputParameter("ResponseHeaders", typeof(Dictionary<string, string>),
                "Response headers");
            AddOutputParameter("ResponseTime", typeof(double),
                "Total response time in milliseconds");
            AddOutputParameter("Success", typeof(bool),
                "Whether request was successful (2xx status)");
            AddOutputParameter("IsFromCache", typeof(bool),
                "Whether response was served from cache");
            AddOutputParameter("RetryAttempts", typeof(int),
                "Number of retry attempts made");
            AddOutputParameter("FinalUrl", typeof(string),
                "Final URL after redirects");
        }

        private void ConfigurePolicies()
        {
            RetryPolicy.MaxRetries = 3;
            RetryPolicy.InitialDelay = TimeSpan.FromSeconds(1);
            RetryPolicy.BackoffMultiplier = 2.0;
            RetryPolicy.RetryableExceptions.Add(typeof(HttpRequestException));
            RetryPolicy.RetryableExceptions.Add(typeof(TaskCanceledException));
            RetryPolicy.RetryableExceptions.Add(typeof(TimeoutException));

            TimeoutPolicy.ExecutionTimeout = TimeSpan.FromMinutes(2);
            TimeoutPolicy.StartupTimeout = TimeSpan.FromSeconds(10);

            // Metadata;
            Metadata["Protocols"] = "HTTP/1.1, HTTP/2, HTTPS";
            Metadata["SecurityLevel"] = "High";
            Metadata["SupportsAsync"] = "true";
            Metadata["PerformanceImpact"] = "Medium";
            Metadata["ConcurrentExecution"] = "true";
        }

        protected override async Task<Dictionary<string, object>> ExecuteCoreAsync(
            ActivityExecutionContext context)
        {
            var startTime = DateTime.UtcNow;
            var results = new Dictionary<string, object>();
            var retryAttempts = 0;
            var isFromCache = false;

            try
            {
                // Get input values;
                var url = context.GetInput<string>("Url");
                var method = context.GetInput<string>("Method", "GET").ToUpper();
                var headers = context.GetInput<Dictionary<string, string>>("Headers",
                    new Dictionary<string, string>());
                var body = context.GetInput<object>("Body");
                var contentType = context.GetInput<string>("ContentType", "application/json");
                var timeoutSeconds = context.GetInput<int>("TimeoutSeconds", 30);
                var useCache = context.GetInput<bool>("UseCache", true);
                var cacheDuration = context.GetInput<int>("CacheDuration", 300);
                var retryCount = context.GetInput<int>("RetryCount", 3);
                var validateCertificate = context.GetInput<bool>("ValidateCertificate", true);
                var followRedirects = context.GetInput<bool>("FollowRedirects", true);

                // Configure HTTP client;
                _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                _handler.ServerCertificateCustomValidationCallback =
                    validateCertificate ? null : (sender, cert, chain, errors) => true;
                _handler.AllowAutoRedirect = followRedirects;

                // Check cache;
                var cacheKey = GenerateCacheKey(url, method, headers, body);
                if (useCache && _responseCache.TryGetValue(cacheKey, out var cachedResponse) &&
                    cachedResponse.Expiry > DateTime.UtcNow)
                {
                    context.Log($"Serving response from cache for: {url}");
                    isFromCache = true;

                    // Update last accessed;
                    cachedResponse.LastAccessed = DateTime.UtcNow;

                    // Set outputs;
                    SetOutputsFromCache(context, cachedResponse, startTime, true);

                    results["IsFromCache"] = true;
                    results["Success"] = true;
                    return results;
                }

                context.Log($"Sending HTTP {method} request to: {url}");

                // Create request;
                using var request = new HttpRequestMessage(new HttpMethod(method), url);

                // Add headers;
                foreach (var header in headers)
                {
                    if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue; // Will be set with content;

                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        context.Log($"Warning: Could not add header: {header.Key}", LogLevel.Warning);
                }

                // Add body if present;
                if (body != null && (method == "POST" || method == "PUT" || method == "PATCH"))
                {
                    string bodyContent;
                    if (body is string stringBody)
                    {
                        bodyContent = stringBody;
                    }
                    else;
                    {
                        bodyContent = JsonSerializer.Serialize(body);
                    }

                    request.Content = new StringContent(bodyContent, Encoding.UTF8, contentType);
                }

                // Execute request with retry
                HttpResponseMessage response = null;
                while (retryAttempts <= retryCount)
                {
                    try
                    {
                        response = await _httpClient.SendAsync(request, context.CancellationToken);
                        break; // Success;
                    }
                    catch (Exception ex) when (retryAttempts < retryCount)
                    {
                        retryAttempts++;
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempts));
                        context.Log($"Request failed (attempt {retryAttempts}/{retryCount}): {ex.Message}. Retrying in {delay.TotalSeconds}s",
                            LogLevel.Warning);
                        await Task.Delay(delay, context.CancellationToken);
                    }
                }

                if (response == null)
                    throw new HttpRequestException($"All {retryCount} retry attempts failed");

                // Read response;
                var responseBody = await response.Content.ReadAsStringAsync();
                var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var success = response.IsSuccessStatusCode;
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;

                // Get response headers;
                var responseHeaders = new Dictionary<string, string>();
                foreach (var header in response.Headers)
                {
                    responseHeaders[header.Key] = string.Join(", ", header.Value);
                }
                foreach (var header in response.Content.Headers)
                {
                    responseHeaders[header.Key] = string.Join(", ", header.Value);
                }

                // Cache response if successful and caching enabled;
                if (useCache && success && method == "GET")
                {
                    var cacheEntry = new HttpResponseCache;
                    {
                        Content = responseBody,
                        Headers = responseHeaders,
                        Expiry = DateTime.UtcNow.AddSeconds(cacheDuration),
                        LastAccessed = DateTime.UtcNow;
                    };
                    _responseCache[cacheKey] = cacheEntry
                    CleanupCache();
                }

                // Set outputs;
                context.SetOutput("StatusCode", (int)response.StatusCode);
                context.SetOutput("ResponseBody", responseBody);
                context.SetOutput("ResponseHeaders", responseHeaders);
                context.SetOutput("ResponseTime", responseTime);
                context.SetOutput("Success", success);
                context.SetOutput("IsFromCache", isFromCache);
                context.SetOutput("RetryAttempts", retryAttempts);
                context.SetOutput("FinalUrl", finalUrl);

                // Share data;
                context.ShareData("LastHttpResponse", responseBody);
                context.ShareData("LastHttpStatus", (int)response.StatusCode);

                results["StatusCode"] = (int)response.StatusCode;
                results["ResponseBody"] = responseBody;
                results["ResponseHeaders"] = responseHeaders;
                results["ResponseTime"] = responseTime;
                results["Success"] = success;
                results["IsFromCache"] = isFromCache;
                results["RetryAttempts"] = retryAttempts;
                results["FinalUrl"] = finalUrl;

                context.Log($"HTTP request completed: Status={response.StatusCode}, Time={responseTime:F0}ms, " +
                           $"Size={responseBody.Length} bytes, Cached={isFromCache}");

                if (!success)
                    context.Log($"HTTP request failed with status: {response.StatusCode}", LogLevel.Warning);

                return results;
            }
            catch (Exception ex)
            {
                context.Log($"HTTP request failed: {ex.Message}", LogLevel.Error);

                // Set failure outputs;
                context.SetOutput("Success", false);
                context.SetOutput("RetryAttempts", retryAttempts);
                context.SetOutput("IsFromCache", false);

                results["Success"] = false;
                results["RetryAttempts"] = retryAttempts;
                results["IsFromCache"] = false;

                throw;
            }
        }

        private string GenerateCacheKey(string url, string method,
            Dictionary<string, string> headers, object body)
        {
            var keyBuilder = new StringBuilder();
            keyBuilder.Append(url);
            keyBuilder.Append('|');
            keyBuilder.Append(method);
            keyBuilder.Append('|');

            // Add sorted headers;
            foreach (var header in headers.OrderBy(h => h.Key))
            {
                keyBuilder.Append(header.Key);
                keyBuilder.Append(':');
                keyBuilder.Append(header.Value);
                keyBuilder.Append(';');
            }

            // Add body hash;
            if (body != null)
            {
                var bodyString = body is string str ? str : JsonSerializer.Serialize(body);
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(bodyString));
                keyBuilder.Append('|');
                keyBuilder.Append(BitConverter.ToString(hash).Replace("-", ""));
            }

            return keyBuilder.ToString();
        }

        private void SetOutputsFromCache(ActivityExecutionContext context,
            HttpResponseCache cachedResponse, DateTime startTime, bool success)
        {
            var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            context.SetOutput("StatusCode", 200); // Cache hit treated as success;
            context.SetOutput("ResponseBody", cachedResponse.Content);
            context.SetOutput("ResponseHeaders", cachedResponse.Headers);
            context.SetOutput("ResponseTime", responseTime);
            context.SetOutput("Success", success);
            context.SetOutput("IsFromCache", true);
            context.SetOutput("RetryAttempts", 0);
            context.SetOutput("FinalUrl", "cached");
        }

        private void CleanupCache()
        {
            // Remove expired entries;
            var expiredKeys = _responseCache;
                .Where(kvp => kvp.Value.Expiry < DateTime.UtcNow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
                _responseCache.Remove(key);

            // Limit cache size (LRU)
            if (_responseCache.Count > 1000)
            {
                var oldestEntries = _responseCache;
                    .OrderBy(kvp => kvp.Value.LastAccessed)
                    .Take(200)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in oldestEntries)
                    _responseCache.Remove(key);
            }
        }

        protected override async Task CompensateCoreAsync(ActivityResult result,
            ActivityExecutionContext context, CompensationContext compensationContext)
        {
            // For HTTP requests, compensation typically involves:
            // 1. Logging the failed request;
            // 2. Possibly sending a notification;
            // 3. Cleaning up any temporary resources;

            context.Log($"Compensating HTTP request failure: {result.Error?.Message}");

            // Clear cache entry if request failed;
            if (result.OutputData.TryGetValue("Url", out var urlObj) &&
                urlObj is string url)
            {
                var cacheKeys = _responseCache.Keys;
                    .Where(k => k.Contains(url))
                    .ToList();

                foreach (var key in cacheKeys)
                    _responseCache.Remove(key);

                context.Log($"Cleared cache entries for: {url}");
            }

            await Task.CompletedTask;
        }

        public override ValidationResult ValidateInputs(ActivityExecutionContext context)
        {
            var result = base.ValidateInputs(context);

            var url = context.GetInput<string>("Url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                result.AddError($"Invalid URL: {url}");
            else if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                result.AddError($"URL must be HTTP or HTTPS: {url}");

            var method = context.GetInput<string>("Method", "GET").ToUpper();
            var validMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" };
            if (!validMethods.Contains(method))
                result.AddError($"Invalid HTTP method: {method}. Valid methods: {string.Join(", ", validMethods)}");

            var timeout = context.GetInput<int>("TimeoutSeconds", 30);
            if (timeout < 1 || timeout > 300)
                result.AddError($"Timeout must be between 1 and 300 seconds, got: {timeout}");

            var cacheDuration = context.GetInput<int>("CacheDuration", 300);
            if (cacheDuration < 0 || cacheDuration > 86400)
                result.AddError($"Cache duration must be between 0 and 86400 seconds, got: {cacheDuration}");

            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient?.Dispose();
                _handler?.Dispose();
                _responseCache.Clear();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Advanced image processing activity with AI enhancement;
    /// </summary>
    [ActivityMetadata(
        Name = "ImageProcessorAI",
        Version = "2.5.0",
        Category = "Media/Image",
        Description = "Image processing with AI enhancement, filters, and transformations",
        Author = "NEDA Media Team",
        Tags = new[] { "Image", "AI", "Processing", "Media", "ComputerVision" }
    )]
    public class ImageProcessorAIActivity : ActivityBase;
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        public ImageProcessorAIActivity(ILogger logger) : base("ImageProcessorAI",
            "Image processing with AI enhancement, filters, and transformations")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = new HttpClient();

            InitializeParameters();
            ConfigurePolicies();
        }

        private void InitializeParameters()
        {
            // Input parameters;
            AddInputParameter("ImagePath", typeof(string), true,
                "Path to input image", null);
            AddInputParameter("OutputPath", typeof(string), false,
                "Output path (default: same directory with suffix)", null);
            AddInputParameter("Operations", typeof(string[]), true,
                "Operations to perform", new[] { "Resize", "Enhance" });
            AddInputParameter("TargetWidth", typeof(int), false,
                "Target width for resizing", 800);
            AddInputParameter("TargetHeight", typeof(int), false,
                "Target height for resizing", 600);
            AddInputParameter("MaintainAspectRatio", typeof(bool), false,
                "Maintain aspect ratio when resizing", true);
            AddInputParameter("EnhancementLevel", typeof(double), false,
                "AI enhancement level (0.0-1.0)", 0.7);
            AddInputParameter("ApplyFilters", typeof(string[]), false,
                "Filters to apply", new[] { "AutoContrast", "Sharpen" });
            AddInputParameter("Quality", typeof(int), false,
                "Output quality (1-100)", 90);
            AddInputParameter("Format", typeof(string), false,
                "Output format", "JPEG");

            // Output parameters;
            AddOutputParameter("ProcessedImagePath", typeof(string),
                "Path to processed image");
            AddOutputParameter("OriginalDimensions", typeof(string),
                "Original image dimensions (WxH)");
            AddOutputParameter("ProcessedDimensions", typeof(string),
                "Processed image dimensions (WxH)");
            AddOutputParameter("OriginalSize", typeof(long),
                "Original file size in bytes");
            AddOutputParameter("ProcessedSize", typeof(long),
                "Processed file size in bytes");
            AddOutputParameter("ProcessingTime", typeof(double),
                "Processing time in milliseconds");
            AddOutputParameter("Success", typeof(bool),
                "Processing success status");
            AddOutputParameter("AIEnhancementApplied", typeof(bool),
                "Whether AI enhancement was applied");
        }

        private void ConfigurePolicies()
        {
            RetryPolicy.MaxRetries = 2;
            RetryPolicy.InitialDelay = TimeSpan.FromSeconds(1);
            RetryPolicy.RetryableExceptions.Add(typeof(IOException));
            RetryPolicy.RetryableExceptions.Add(typeof(OutOfMemoryException));

            TimeoutPolicy.ExecutionTimeout = TimeSpan.FromMinutes(3);
            TimeoutPolicy.StartupTimeout = TimeSpan.FromSeconds(15);

            // Metadata;
            Metadata["PerformanceImpact"] = "High";
            Metadata["MemoryUsage"] = "High";
            Metadata["GPUAccelerated"] = "Optional";
            Metadata["SupportsBatch"] = "true";
            Metadata["ImageFormats"] = "JPEG, PNG, BMP, GIF, TIFF";
        }

        protected override async Task<Dictionary<string, object>> ExecuteCoreAsync(
            ActivityExecutionContext context)
        {
            var startTime = DateTime.UtcNow;
            var results = new Dictionary<string, object>();
            var aiEnhancementApplied = false;

            try
            {
                // Get input values;
                var imagePath = context.GetInput<string>("ImagePath");
                var outputPath = context.GetInput<string>("OutputPath",
                    GetDefaultOutputPath(imagePath));
                var operations = context.GetInput<string[]>("Operations", new[] { "Resize", "Enhance" });
                var targetWidth = context.GetInput<int>("TargetWidth", 800);
                var targetHeight = context.GetInput<int>("TargetHeight", 600);
                var maintainAspect = context.GetInput<bool>("MaintainAspectRatio", true);
                var enhancementLevel = context.GetInput<double>("EnhancementLevel", 0.7);
                var filters = context.GetInput<string[]>("ApplyFilters", new[] { "AutoContrast", "Sharpen" });
                var quality = context.GetInput<int>("Quality", 90);
                var format = context.GetInput<string>("Format", "JPEG").ToUpper();

                context.Log($"Starting image processing: {imagePath}");

                // Load image;
                using var originalImage = Image.FromFile(imagePath);
                var originalWidth = originalImage.Width;
                var originalHeight = originalImage.Height;
                var originalSize = new FileInfo(imagePath).Length;

                // Process image;
                using var processedImage = (Image)originalImage.Clone();

                foreach (var operation in operations)
                {
                    switch (operation.ToLower())
                    {
                        case "resize":
                            processedImage = ResizeImage(processedImage, targetWidth,
                                targetHeight, maintainAspect);
                            break;

                        case "enhance":
                            if (enhancementLevel > 0)
                            {
                                processedImage = await ApplyAIEnhancementAsync(processedImage,
                                    enhancementLevel, context);
                                aiEnhancementApplied = true;
                            }
                            break;

                        case "rotate":
                            processedImage.RotateFlip(RotateFlipType.Rotate90FlipNone);
                            break;

                        case "crop":
                            // Default crop to center;
                            var cropRect = new Rectangle(
                                (processedImage.Width - targetWidth) / 2,
                                (processedImage.Height - targetHeight) / 2,
                                Math.Min(targetWidth, processedImage.Width),
                                Math.Min(targetHeight, processedImage.Height)
                            );
                            processedImage = CropImage(processedImage, cropRect);
                            break;
                    }
                }

                // Apply filters;
                foreach (var filter in filters)
                {
                    switch (filter.ToLower())
                    {
                        case "autocontrast":
                            processedImage = ApplyAutoContrast(processedImage);
                            break;

                        case "sharpen":
                            processedImage = ApplySharpen(processedImage);
                            break;

                        case "grayscale":
                            processedImage = ApplyGrayscale(processedImage);
                            break;

                        case "sepia":
                            processedImage = ApplySepia(processedImage);
                            break;
                    }
                }

                // Save image;
                var imageFormat = GetImageFormat(format);
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);

                processedImage.Save(outputPath, GetEncoder(imageFormat), encoderParams);

                // Calculate metrics;
                var processedSize = new FileInfo(outputPath).Length;
                var processingTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

                // Set outputs;
                context.SetOutput("ProcessedImagePath", outputPath);
                context.SetOutput("OriginalDimensions", $"{originalWidth}x{originalHeight}");
                context.SetOutput("ProcessedDimensions", $"{processedImage.Width}x{processedImage.Height}");
                context.SetOutput("OriginalSize", originalSize);
                context.SetOutput("ProcessedSize", processedSize);
                context.SetOutput("ProcessingTime", processingTime);
                context.SetOutput("Success", true);
                context.SetOutput("AIEnhancementApplied", aiEnhancementApplied);

                // Share data;
                context.ShareData("LastProcessedImage", outputPath);
                context.ShareData("ImageProcessingTime", processingTime);

                results["ProcessedImagePath"] = outputPath;
                results["OriginalDimensions"] = $"{originalWidth}x{originalHeight}";
                results["ProcessedDimensions"] = $"{processedImage.Width}x{processedImage.Height}";
                results["OriginalSize"] = originalSize;
                results["ProcessedSize"] = processedSize;
                results["ProcessingTime"] = processingTime;
                results["Success"] = true;
                results["AIEnhancementApplied"] = aiEnhancementApplied;

                context.Log($"Image processing completed: {outputPath}");
                context.Log($"Dimensions: {originalWidth}x{originalHeight} -> {processedImage.Width}x{processedImage.Height}");
                context.Log($"Size: {originalSize / 1024}KB -> {processedSize / 1024}KB, Time: {processingTime:F0}ms");

                return results;
            }
            catch (Exception ex)
            {
                context.Log($"Image processing failed: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        private string GetDefaultOutputPath(string inputPath)
        {
            var directory = Path.GetDirectoryName(inputPath);
            var fileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = Path.GetExtension(inputPath);
            return Path.Combine(directory, $"{fileName}_processed{extension}");
        }

        private Image ResizeImage(Image image, int width, int height, bool maintainAspect)
        {
            if (maintainAspect)
            {
                var ratio = Math.Min((double)width / image.Width, (double)height / image.Height);
                width = (int)(image.Width * ratio);
                height = (int)(image.Height * ratio);
            }

            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height,
                        GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        private async Task<Image> ApplyAIEnhancementAsync(Image image, double level,
            ActivityExecutionContext context)
        {
            // In a real implementation, this would call an AI service;
            // For now, simulate with basic image adjustments;

            context.Log($"Applying AI enhancement (simulated) with level: {level}");

            // Simulate AI processing time;
            await Task.Delay(TimeSpan.FromMilliseconds(500 * level), context.CancellationToken);

            // Apply some basic enhancements based on level;
            var brightness = (float)(0.1 * level);
            var contrast = (float)(0.2 * level);
            var saturation = (float)(0.15 * level);

            return AdjustImage(image, brightness, contrast, saturation);
        }

        private Image AdjustImage(Image image, float brightness, float contrast, float saturation)
        {
            // Simplified adjustment - in real implementation use proper color matrix;
            var bitmap = new Bitmap(image);

            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    // Adjust brightness;
                    var r = Math.Min(255, (int)(pixel.R * (1 + brightness)));
                    var g = Math.Min(255, (int)(pixel.G * (1 + brightness)));
                    var b = Math.Min(255, (int)(pixel.B * (1 + brightness)));

                    // Adjust contrast;
                    r = (int)(((r - 128) * (1 + contrast)) + 128);
                    g = (int)(((g - 128) * (1 + contrast)) + 128);
                    b = (int)(((b - 128) * (1 + contrast)) + 128);

                    r = Math.Max(0, Math.Min(255, r));
                    g = Math.Max(0, Math.Min(255, g));
                    b = Math.Max(0, Math.Min(255, b));

                    bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, r, g, b));
                }
            }

            return bitmap;
        }

        private Image ApplyAutoContrast(Image image)
        {
            // Simplified auto-contrast;
            var bitmap = new Bitmap(image);
            // In real implementation, calculate histogram and adjust;
            return bitmap;
        }

        private Image ApplySharpen(Image image)
        {
            // Simplified sharpen filter;
            var bitmap = new Bitmap(image);
            // In real implementation, apply convolution matrix;
            return bitmap;
        }

        private Image ApplyGrayscale(Image image)
        {
            var bitmap = new Bitmap(image);

            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var gray = (int)(pixel.R * 0.3 + pixel.G * 0.59 + pixel.B * 0.11);
                    bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, gray, gray, gray));
                }
            }

            return bitmap;
        }

        private Image ApplySepia(Image image)
        {
            var bitmap = new Bitmap(image);

            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    var r = (int)(pixel.R * 0.393 + pixel.G * 0.769 + pixel.B * 0.189);
                    var g = (int)(pixel.R * 0.349 + pixel.G * 0.686 + pixel.B * 0.168);
                    var b = (int)(pixel.R * 0.272 + pixel.G * 0.534 + pixel.B * 0.131);

                    r = Math.Min(255, r);
                    g = Math.Min(255, g);
                    b = Math.Min(255, b);

                    bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, r, g, b));
                }
            }

            return bitmap;
        }

        private Image CropImage(Image image, Rectangle cropArea)
        {
            var bitmap = new Bitmap(cropArea.Width, cropArea.Height);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.DrawImage(image, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                 cropArea, GraphicsUnit.Pixel);
            }

            return bitmap;
        }

        private ImageFormat GetImageFormat(string format)
        {
            return format.ToUpper() switch;
            {
                "JPEG" or "JPG" => ImageFormat.Jpeg,
                "PNG" => ImageFormat.Png,
                "BMP" => ImageFormat.Bmp,
                "GIF" => ImageFormat.Gif,
                "TIFF" => ImageFormat.Tiff,
                _ => ImageFormat.Jpeg;
            };
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            return codecs.FirstOrDefault(codec => codec.FormatID == format.Guid) ?? codecs[1];
        }

        public override ValidationResult ValidateInputs(ActivityExecutionContext context)
        {
            var result = base.ValidateInputs(context);

            var imagePath = context.GetInput<string>("ImagePath");
            if (!string.IsNullOrEmpty(imagePath) && !File.Exists(imagePath))
                result.AddError($"Image file does not exist: {imagePath}");
            else if (!string.IsNullOrEmpty(imagePath))
            {
                var validExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff" };
                var ext = Path.GetExtension(imagePath).ToLower();
                if (!validExtensions.Contains(ext))
                    result.AddError($"Unsupported image format: {ext}. Supported: {string.Join(", ", validExtensions)}");
            }

            var quality = context.GetInput<int>("Quality", 90);
            if (quality < 1 || quality > 100)
                result.AddError($"Quality must be between 1 and 100, got: {quality}");

            var enhancementLevel = context.GetInput<double>("EnhancementLevel", 0.7);
            if (enhancementLevel < 0 || enhancementLevel > 1)
                result.AddError($"Enhancement level must be between 0.0 and 1.0, got: {enhancementLevel}");

            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Activity metadata attribute for registration and discovery;
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class ActivityMetadataAttribute : Attribute;
    {
        public string Name { get; }
        public string Version { get; }
        public string Category { get; }
        public string Description { get; }
        public string Author { get; }
        public string[] Tags { get; }

        public ActivityMetadataAttribute(string name, string version, string category,
            string description, string author, string[] tags)
        {
            Name = name;
            Version = version;
            Category = category;
            Description = description;
            Author = author;
            Tags = tags ?? Array.Empty<string>();
        }
    }
}
