using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.API.DTOs;
using NEDA.API.Common;

namespace NEDA.API.ClientSDK;
{
    /// <summary>
    /// Professional API wrapper for NEDA services with retry mechanisms, caching,
    /// request/response logging, and comprehensive error handling;
    /// </summary>
    public class APIWrapper : IAPIWrapper, IDisposable;
    {
        #region Private Fields;
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly APIWrapperSettings _settings;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly MemoryCache<CacheKey, object> _cache;
        private readonly RateLimiter _rateLimiter;
        private bool _disposed = false;
        private bool _isAuthenticated = false;
        private string _accessToken;
        private DateTime _tokenExpiry;
        #endregion;

        #region Public Properties;
        public string BaseUrl { get; private set; }
        public string ClientVersion { get; private set; }
        public APIStatus Status { get; private set; }
        public bool IsConnected => Status == APIStatus.Connected;
        public string SessionId { get; private set; }
        public DateTime LastRequestTime { get; private set; }
        public int TotalRequests { get; private set; }
        public int FailedRequests { get; private set; }
        public event EventHandler<APIStatusChangedEventArgs> StatusChanged;
        public event EventHandler<RequestCompletedEventArgs> RequestCompleted;
        #endregion;

        #region Constructors;
        public APIWrapper(HttpClient httpClient, ILogger logger, APIWrapperSettings settings = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? new APIWrapperSettings();

            InitializeWrapper();
        }

        public APIWrapper(string baseUrl, ILogger logger, APIWrapperSettings settings = null)
            : this(new HttpClient(), logger, settings)
        {
            BaseUrl = baseUrl?.TrimEnd('/');
            _httpClient.BaseAddress = new Uri(BaseUrl);
        }
        #endregion;

        #region Initialization;
        private void InitializeWrapper()
        {
            ClientVersion = "1.0.0";
            Status = APIStatus.Disconnected;

            // Configure JSON serialization;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                PropertyNameCaseInsensitive = true;
            };

            // Configure HTTP client;
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"NEDA-Client/{ClientVersion}");
            _httpClient.Timeout = _settings.DefaultTimeout;

            // Initialize cache;
            _cache = new MemoryCache<CacheKey, object>(
                TimeSpan.FromMinutes(_settings.CacheDurationMinutes));

            // Initialize rate limiter;
            _rateLimiter = new RateLimiter(
                _settings.MaxRequestsPerMinute,
                TimeSpan.FromMinutes(1));

            _logger.LogInformation("APIWrapper initialized successfully");
        }

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            try
            {
                UpdateStatus(APIStatus.Connecting);

                // Test connection;
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
                var response = await ExecuteRequestAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    UpdateStatus(APIStatus.Connected);
                    SessionId = Guid.NewGuid().ToString();

                    _logger.LogInformation($"API connection established. Session: {SessionId}");
                    return true;
                }

                UpdateStatus(APIStatus.ConnectionFailed);
                return false;
            }
            catch (Exception ex)
            {
                UpdateStatus(APIStatus.ConnectionFailed);
                _logger.LogError(ex, "Failed to connect to API");
                throw new APIWrapperException("Connection failed", ex);
            }
        }

        public async Task DisconnectAsync()
        {
            ValidateNotDisposed();

            try
            {
                if (IsConnected)
                {
                    // Notify server about disconnection;
                    var request = new HttpRequestMessage(HttpMethod.Post, "/api/session/disconnect");
                    await ExecuteRequestAsync(request);
                }

                UpdateStatus(APIStatus.Disconnected);
                _isAuthenticated = false;
                _accessToken = null;
                SessionId = null;

                _logger.LogInformation("API disconnected");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during API disconnection");
            }
        }
        #endregion;

        #region Authentication;
        public async Task<AuthResponse> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateCredentials(username, password);

            try
            {
                var authRequest = new AuthRequest;
                {
                    Username = username,
                    Password = password,
                    ClientVersion = ClientVersion;
                };

                var request = CreateRequest(HttpMethod.Post, "/api/auth/login", authRequest);
                var response = await ExecuteRequestAsync<AuthResponse>(request, cancellationToken);

                if (response.Success)
                {
                    _isAuthenticated = true;
                    _accessToken = response.AccessToken;
                    _tokenExpiry = DateTime.UtcNow.AddSeconds(response.ExpiresIn);

                    // Set authorization header for future requests;
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _accessToken);

                    _logger.LogInformation($"User authenticated: {username}");
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Authentication failed for user: {username}");
                throw new APIWrapperException("Authentication failed", ex);
            }
        }

        public async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (string.IsNullOrEmpty(_accessToken))
            {
                return false;
            }

            try
            {
                var request = CreateRequest(HttpMethod.Post, "/api/auth/refresh");
                var response = await ExecuteRequestAsync<AuthResponse>(request, cancellationToken);

                if (response.Success)
                {
                    _accessToken = response.AccessToken;
                    _tokenExpiry = DateTime.UtcNow.AddSeconds(response.ExpiresIn);

                    _httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _accessToken);

                    _logger.LogDebug("Access token refreshed");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token refresh failed");
                return false;
            }
        }

        public void Logout()
        {
            ValidateNotDisposed();

            _isAuthenticated = false;
            _accessToken = null;
            _httpClient.DefaultRequestHeaders.Authorization = null;

            _logger.LogInformation("User logged out");
        }
        #endregion;

        #region Project API Methods;
        public async Task<ProjectResponse> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateProjectId(projectId);

            var cacheKey = new CacheKey($"project_{projectId}", CacheType.Project);

            return await ExecuteCachedRequestAsync<ProjectResponse>(
                cacheKey,
                () => CreateRequest(HttpMethod.Get, $"/api/projects/{projectId}"),
                cancellationToken);
        }

        public async Task<ProjectListResponse> GetProjectsAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidatePagination(page, pageSize);

            var cacheKey = new CacheKey($"projects_{page}_{pageSize}", CacheType.ProjectList);

            return await ExecuteCachedRequestAsync<ProjectListResponse>(
                cacheKey,
                () => CreateRequest(HttpMethod.Get, $"/api/projects?page={page}&pageSize={pageSize}"),
                cancellationToken);
        }

        public async Task<ProjectResponse> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateProjectRequest(request);

            // Invalidate related caches;
            InvalidateCache(CacheType.ProjectList);

            var httpRequest = CreateRequest(HttpMethod.Post, "/api/projects", request);
            return await ExecuteRequestAsync<ProjectResponse>(httpRequest, cancellationToken);
        }

        public async Task<ProjectResponse> UpdateProjectAsync(string projectId, UpdateProjectRequest request, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateProjectId(projectId);
            ValidateProjectRequest(request);

            // Invalidate caches;
            InvalidateCache(CacheType.ProjectList);
            InvalidateCache(new CacheKey($"project_{projectId}", CacheType.Project));

            var httpRequest = CreateRequest(HttpMethod.Put, $"/api/projects/{projectId}", request);
            return await ExecuteRequestAsync<ProjectResponse>(httpRequest, cancellationToken);
        }

        public async Task<BaseResponse> DeleteProjectAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateProjectId(projectId);

            // Invalidate caches;
            InvalidateCache(CacheType.ProjectList);
            InvalidateCache(new CacheKey($"project_{projectId}", CacheType.Project));

            var request = CreateRequest(HttpMethod.Delete, $"/api/projects/{projectId}");
            return await ExecuteRequestAsync<BaseResponse>(request, cancellationToken);
        }
        #endregion;

        #region Command API Methods;
        public async Task<CommandResponse> ExecuteCommandAsync(CommandRequest command, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateCommandRequest(command);

            var request = CreateRequest(HttpMethod.Post, "/api/commands/execute", command);
            return await ExecuteRequestAsync<CommandResponse>(request, cancellationToken);
        }

        public async Task<CommandStatusResponse> GetCommandStatusAsync(string commandId, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateCommandId(commandId);

            var cacheKey = new CacheKey($"command_status_{commandId}", CacheType.CommandStatus);

            return await ExecuteCachedRequestAsync<CommandStatusResponse>(
                cacheKey,
                () => CreateRequest(HttpMethod.Get, $"/api/commands/{commandId}/status"),
                cancellationToken);
        }

        public async Task<CommandListResponse> GetCommandHistoryAsync(DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var url = "/api/commands/history";
            var queryParams = new List<string>();

            if (fromDate.HasValue)
                queryParams.Add($"from={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"to={toDate.Value:yyyy-MM-dd}");

            if (queryParams.Any())
                url += "?" + string.Join("&", queryParams);

            var cacheKey = new CacheKey($"command_history_{fromDate}_{toDate}", CacheType.CommandHistory);

            return await ExecuteCachedRequestAsync<CommandListResponse>(
                cacheKey,
                () => CreateRequest(HttpMethod.Get, url),
                cancellationToken);
        }
        #endregion;

        #region System API Methods;
        public async Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var cacheKey = new CacheKey("system_status", CacheType.SystemStatus);

            return await ExecuteCachedRequestAsync<SystemStatusResponse>(
                cacheKey,
                () => CreateRequest(HttpMethod.Get, "/api/system/status"),
                cancellationToken);
        }

        public async Task<PerformanceMetricsResponse> GetPerformanceMetricsAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var cacheKey = new CacheKey("performance_metrics", CacheType.PerformanceMetrics);

            return await ExecuteCachedRequestAsync<PerformanceMetricsResponse>(
                cacheKey,
                () => CreateRequest(HttpMethod.Get, "/api/system/metrics"),
                cancellationToken);
        }

        public async Task<BaseResponse> RestartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateServiceName(serviceName);

            var request = CreateRequest(HttpMethod.Post, $"/api/system/services/{serviceName}/restart");
            return await ExecuteRequestAsync<BaseResponse>(request, cancellationToken);
        }
        #endregion;

        #region Core Request Execution;
        private async Task<T> ExecuteRequestAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var response = await ExecuteRequestAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            try
            {
                var result = JsonSerializer.Deserialize<T>(content, _jsonOptions);
                return result;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, $"Failed to deserialize response for {request.RequestUri}");
                throw new APIWrapperException("Response deserialization failed", ex);
            }
        }

        private async Task<HttpResponseMessage> ExecuteRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            // Check rate limit;
            await _rateLimiter.WaitAsync(cancellationToken);

            // Ensure authentication for protected endpoints;
            if (IsProtectedEndpoint(request.RequestUri) && !_isAuthenticated)
            {
                throw new APIWrapperException("Authentication required for this endpoint");
            }

            // Refresh token if about to expire;
            if (_isAuthenticated && _tokenExpiry.Subtract(DateTime.UtcNow) < TimeSpan.FromMinutes(5))
            {
                await RefreshTokenAsync(cancellationToken);
            }

            var startTime = DateTime.UtcNow;
            HttpResponseMessage response = null;
            Exception lastException = null;

            // Retry logic;
            for (int attempt = 0; attempt <= _settings.MaxRetryAttempts; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                        _logger.LogWarning($"Retry attempt {attempt} for {request.RequestUri}");
                    }

                    response = await _httpClient.SendAsync(request, cancellationToken);
                    LastRequestTime = DateTime.UtcNow;
                    TotalRequests++;

                    if (response.IsSuccessStatusCode)
                    {
                        OnRequestCompleted(new RequestCompletedEventArgs;
                        {
                            RequestUri = request.RequestUri,
                            Method = request.Method,
                            StatusCode = response.StatusCode,
                            Duration = DateTime.UtcNow - startTime,
                            Attempts = attempt + 1;
                        });

                        return response;
                    }

                    // Handle specific status codes;
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        _isAuthenticated = false;
                        throw new APIWrapperException("Authentication expired");
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        throw new APIWrapperException("Rate limit exceeded");
                    }

                    // For server errors, retry for client errors, don't retry
                    if ((int)response.StatusCode < 500)
                    {
                        break;
                    }

                    lastException = new HttpRequestException($"HTTP {response.StatusCode}: {response.ReasonPhrase}");
                }
                catch (Exception ex) when (IsRetryableException(ex) && attempt < _settings.MaxRetryAttempts)
                {
                    lastException = ex;
                    continue;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    break;
                }
            }

            FailedRequests++;
            _logger.LogError(lastException, $"Request failed after {_settings.MaxRetryAttempts + 1} attempts: {request.RequestUri}");

            OnRequestCompleted(new RequestCompletedEventArgs;
            {
                RequestUri = request.RequestUri,
                Method = request.Method,
                StatusCode = response?.StatusCode ?? System.Net.HttpStatusCode.ServiceUnavailable,
                Duration = DateTime.UtcNow - startTime,
                Attempts = _settings.MaxRetryAttempts + 1,
                Error = lastException?.Message;
            });

            throw new APIWrapperException($"Request failed: {lastException?.Message}", lastException);
        }

        private async Task<T> ExecuteCachedRequestAsync<T>(CacheKey cacheKey, Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default)
            where T : class;
        {
            // Check cache first;
            if (_cache.TryGet(cacheKey, out var cachedResult) && cachedResult is T typedResult)
            {
                _logger.LogDebug($"Cache hit for {cacheKey}");
                return typedResult;
            }

            // Execute request;
            var request = requestFactory();
            var response = await ExecuteRequestAsync<T>(request, cancellationToken);

            // Cache successful responses;
            if (response != null)
            {
                _cache.Set(cacheKey, response);
            }

            return response;
        }
        #endregion;

        #region Utility Methods;
        private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, object content = null)
        {
            var request = new HttpRequestMessage(method, endpoint);

            if (content != null)
            {
                var jsonContent = JsonSerializer.Serialize(content, _jsonOptions);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            }

            return request;
        }

        private void UpdateStatus(APIStatus newStatus)
        {
            var oldStatus = Status;
            Status = newStatus;

            if (oldStatus != newStatus)
            {
                StatusChanged?.Invoke(this, new APIStatusChangedEventArgs;
                {
                    OldStatus = oldStatus,
                    NewStatus = newStatus,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private bool IsProtectedEndpoint(Uri uri)
        {
            var path = uri?.AbsolutePath ?? "";
            return !path.StartsWith("/api/auth/") &&
                   !path.StartsWith("/api/health") &&
                   !path.StartsWith("/api/public/");
        }

        private bool IsRetryableException(Exception ex)
        {
            return ex is HttpRequestException ||
                   ex is TaskCanceledException ||
                   ex is TimeoutException;
        }

        private TimeSpan GetRetryDelay(int attempt)
        {
            return TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff;
        }

        private void InvalidateCache(CacheType cacheType)
        {
            _cache.RemoveAll(key => key.Type == cacheType);
        }

        private void InvalidateCache(CacheKey specificKey)
        {
            _cache.Remove(specificKey);
        }

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(APIWrapper));
        }

        private void ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));
        }

        private void ValidateProjectId(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));
        }

        private void ValidateProjectRequest(object request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
        }

        private void ValidatePagination(int page, int pageSize)
        {
            if (page < 1)
                throw new ArgumentException("Page must be positive", nameof(page));
            if (pageSize < 1 || pageSize > 1000)
                throw new ArgumentException("Page size must be between 1 and 1000", nameof(pageSize));
        }

        private void ValidateCommandRequest(CommandRequest command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.CommandName))
                throw new ArgumentException("Command name cannot be empty", nameof(command));
        }

        private void ValidateCommandId(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                throw new ArgumentException("Command ID cannot be empty", nameof(commandId));
        }

        private void ValidateServiceName(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
                throw new ArgumentException("Service name cannot be empty", nameof(serviceName));
        }
        #endregion;

        #region Event Handling;
        protected virtual void OnStatusChanged(APIStatusChangedEventArgs e)
        {
            StatusChanged?.Invoke(this, e);
        }

        protected virtual void OnRequestCompleted(RequestCompletedEventArgs e)
        {
            RequestCompleted?.Invoke(this, e);
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
                    DisconnectAsync().Wait(5000);
                    _httpClient?.Dispose();
                    _cache?.Clear();
                    _logger.LogInformation("APIWrapper disposed");
                }

                _disposed = true;
            }
        }

        ~APIWrapper()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    public class APIWrapperSettings;
    {
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxRetryAttempts { get; set; } = 3;
        public int CacheDurationMinutes { get; set; } = 5;
        public int MaxRequestsPerMinute { get; set; } = 60;
        public bool EnableCompression { get; set; } = true;
    }

    public enum APIStatus;
    {
        Disconnected,
        Connecting,
        Connected,
        ConnectionFailed,
        Reconnecting;
    }

    public class CacheKey : IEquatable<CacheKey>
    {
        public string Key { get; }
        public CacheType Type { get; }

        public CacheKey(string key, CacheType type)
        {
            Key = key;
            Type = type;
        }

        public bool Equals(CacheKey other)
        {
            return Key == other?.Key && Type == other.Type;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as CacheKey);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Key, Type);
        }

        public override string ToString()
        {
            return $"{Type}:{Key}";
        }
    }

    public enum CacheType;
    {
        Project,
        ProjectList,
        CommandStatus,
        CommandHistory,
        SystemStatus,
        PerformanceMetrics;
    }

    // Simple memory cache implementation;
    public class MemoryCache<TKey, TValue>
    {
        private readonly Dictionary<TKey, CacheItem<TValue>> _cache;
        private readonly TimeSpan _defaultTtl;
        private readonly object _lockObject = new object();

        public MemoryCache(TimeSpan defaultTtl)
        {
            _cache = new Dictionary<TKey, CacheItem<TValue>>();
            _defaultTtl = defaultTtl;
        }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_lockObject)
            {
                if (_cache.TryGetValue(key, out var item) && !item.IsExpired)
                {
                    value = item.Value;
                    return true;
                }

                value = default;
                return false;
            }
        }

        public void Set(TKey key, TValue value, TimeSpan? ttl = null)
        {
            lock (_lockObject)
            {
                _cache[key] = new CacheItem<TValue>(value, ttl ?? _defaultTtl);
            }
        }

        public void Remove(TKey key)
        {
            lock (_lockObject)
            {
                _cache.Remove(key);
            }
        }

        public void RemoveAll(Func<TKey, bool> predicate)
        {
            lock (_lockObject)
            {
                var keysToRemove = _cache.Keys.Where(predicate).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }
            }
        }

        public void Clear()
        {
            lock (_lockObject)
            {
                _cache.Clear();
            }
        }
    }

    public class CacheItem<T>
    {
        public T Value { get; }
        public DateTime Expiry { get; }

        public bool IsExpired => DateTime.UtcNow > Expiry;

        public CacheItem(T value, TimeSpan ttl)
        {
            Value = value;
            Expiry = DateTime.UtcNow.Add(ttl);
        }
    }

    // Simple rate limiter implementation;
    public class RateLimiter;
    {
        private readonly int _maxRequests;
        private readonly TimeSpan _timeWindow;
        private readonly Queue<DateTime> _requestTimes;
        private readonly object _lockObject = new object();

        public RateLimiter(int maxRequests, TimeSpan timeWindow)
        {
            _maxRequests = maxRequests;
            _timeWindow = timeWindow;
            _requestTimes = new Queue<DateTime>();
        }

        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                lock (_lockObject)
                {
                    var now = DateTime.UtcNow;

                    // Remove old requests outside the time window;
                    while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > _timeWindow)
                    {
                        _requestTimes.Dequeue();
                    }

                    // Check if we can make a request;
                    if (_requestTimes.Count < _maxRequests)
                    {
                        _requestTimes.Enqueue(now);
                        return;
                    }
                }

                // Wait before retrying;
                await Task.Delay(100, cancellationToken);
            }
        }
    }
    #endregion;
}
