using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.KnowledgeBase.Parsers;

namespace NEDA.KnowledgeBase.OnlineSources;
{
    /// <summary>
    /// Online veri kaynaklarını yöneten ve koordine eden merkezi yönetici;
    /// </summary>
    public class SourceManager : ISourceManager, IDisposable;
    {
        #region Fields;
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, IDataDownloader> _downloaders;
        private readonly Dictionary<string, ISourceConfiguration> _sourceConfigurations;
        private readonly Dictionary<string, SourceCache> _cache;
        private readonly object _syncLock = new object();
        private readonly Timer _healthCheckTimer;
        private readonly Timer _cacheCleanupTimer;
        private bool _isInitialized;
        private bool _isDisposed;
        #endregion;

        #region Properties;
        public string ManagerId { get; private set; }
        public SourceManagerStatus Status { get; private set; }
        public int ActiveDownloads => _downloaders.Values.Count(d => d.Status == DownloaderStatus.Downloading);
        public int CachedSources => _cache.Count;
        public long TotalBytesDownloaded { get; private set; }
        public long TotalRequestsProcessed { get; private set; }
        public event EventHandler<SourceManagerEventArgs> OnSourceUpdate;
        public event EventHandler<DownloadCompletedEventArgs> OnDownloadCompleted;
        #endregion;

        #region Constructor;
        public SourceManager(
            ILogger logger,
            IErrorReporter errorReporter,
            HttpClient httpClient = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _httpClient = httpClient ?? CreateHttpClient();

            ManagerId = Guid.NewGuid().ToString();
            Status = SourceManagerStatus.Stopped;
            _downloaders = new Dictionary<string, IDataDownloader>();
            _sourceConfigurations = new Dictionary<string, ISourceConfiguration>();
            _cache = new Dictionary<string, SourceCache>();

            // Sağlık kontrol timer'ını oluştur;
            _healthCheckTimer = new Timer(HealthCheckCallback, null, Timeout.Infinite, Timeout.Infinite);
            _cacheCleanupTimer = new Timer(CacheCleanupCallback, null, Timeout.Infinite, Timeout.Infinite);

            _logger.LogInformation($"Source Manager initialized with ID: {ManagerId}");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Source Manager'ı başlatır;
        /// </summary>
        public async Task<OperationResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("Source Manager is already initialized");
                    return OperationResult.Failure("Source Manager is already initialized");
                }

                _logger.LogInformation("Initializing Source Manager...");
                Status = SourceManagerStatus.Initializing;

                // Konfigürasyonları yükle;
                await LoadSourceConfigurationsAsync(cancellationToken);

                // Varsayılan downloader'ları oluştur;
                await CreateDefaultDownloadersAsync(cancellationToken);

                // Cache'i yükle;
                await LoadCacheAsync(cancellationToken);

                // Timer'ları başlat;
                StartTimers();

                _isInitialized = true;
                Status = SourceManagerStatus.Running;

                _logger.LogInformation("Source Manager initialized successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Source Manager");
                Status = SourceManagerStatus.Error;
                return OperationResult.Failure($"Initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kaynak yapılandırması ekler;
        /// </summary>
        public OperationResult AddSourceConfiguration(ISourceConfiguration configuration)
        {
            ValidateConfiguration(configuration);

            lock (_syncLock)
            {
                if (_sourceConfigurations.ContainsKey(configuration.SourceId))
                {
                    return OperationResult.Failure($"Source configuration with ID '{configuration.SourceId}' already exists");
                }

                _sourceConfigurations[configuration.SourceId] = configuration;
                _logger.LogInformation($"Added source configuration: {configuration.SourceName} ({configuration.SourceId})");

                return OperationResult.Success();
            }
        }

        /// <summary>
        /// Kaynaktan veri indirir;
        /// </summary>
        public async Task<SourceDataResult> DownloadDataAsync(
            string sourceId,
            DownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isInitialized)
                {
                    return SourceDataResult.Failure("Source Manager is not initialized");
                }

                if (!_sourceConfigurations.TryGetValue(sourceId, out var configuration))
                {
                    return SourceDataResult.Failure($"Source configuration not found: {sourceId}");
                }

                // Önbellekte kontrol et;
                if (ShouldUseCache(configuration, options))
                {
                    var cachedData = GetCachedData(sourceId, configuration.CacheKey);
                    if (cachedData != null)
                    {
                        _logger.LogDebug($"Using cached data for source: {sourceId}");
                        return SourceDataResult.Success(cachedData, true);
                    }
                }

                // İndirme işlemini başlat;
                var downloader = GetDownloaderForSource(configuration);
                if (downloader == null)
                {
                    return SourceDataResult.Failure($"No suitable downloader found for source: {sourceId}");
                }

                _logger.LogInformation($"Starting download from source: {configuration.SourceName}");

                var downloadResult = await downloader.DownloadAsync(configuration, options, cancellationToken);

                if (downloadResult.Success)
                {
                    // İndirilen veriyi işle;
                    var processedData = await ProcessDownloadedDataAsync(downloadResult.Data, configuration, cancellationToken);

                    // Önbelleğe al;
                    if (configuration.EnableCaching)
                    {
                        CacheData(sourceId, configuration.CacheKey, processedData, configuration.CacheDuration);
                    }

                    // İstatistikleri güncelle;
                    UpdateStatistics(downloadResult.BytesDownloaded);

                    // Event tetikle;
                    OnDownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs;
                    {
                        SourceId = sourceId,
                        SourceName = configuration.SourceName,
                        DataSize = downloadResult.BytesDownloaded,
                        DownloadTime = downloadResult.DownloadTime,
                        Success = true;
                    });

                    return SourceDataResult.Success(processedData, false);
                }
                else;
                {
                    _logger.LogError($"Download failed for source {sourceId}: {downloadResult.ErrorMessage}");

                    // Hata raporla;
                    await _errorReporter.ReportErrorAsync(
                        new ErrorReport;
                        {
                            ErrorCode = ErrorCodes.DownloadFailed,
                            Message = $"Download failed for source {configuration.SourceName}",
                            Details = downloadResult.ErrorMessage,
                            Severity = ErrorSeverity.Warning,
                            Source = "SourceManager"
                        },
                        cancellationToken);

                    return SourceDataResult.Failure(downloadResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error downloading data from source {sourceId}");
                return SourceDataResult.Failure($"Download error: {ex.Message}");
            }
        }

        /// <summary>
        /// Birden fazla kaynaktan veri indirir;
        /// </summary>
        public async Task<BatchDownloadResult> DownloadBatchAsync(
            List<string> sourceIds,
            BatchDownloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var result = new BatchDownloadResult;
            {
                TotalSources = sourceIds.Count,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                _logger.LogInformation($"Starting batch download for {sourceIds.Count} sources");

                var tasks = new List<Task<SourceDataResult>>();
                var downloadResults = new Dictionary<string, SourceDataResult>();

                // Paralel indirme için task'lar oluştur;
                foreach (var sourceId in sourceIds)
                {
                    var task = DownloadDataAsync(
                        sourceId,
                        options?.GetDownloadOptions(sourceId),
                        cancellationToken);

                    tasks.Add(task);
                }

                // Tüm task'ların tamamlanmasını bekle;
                await Task.WhenAll(tasks);

                // Sonuçları topla;
                for (int i = 0; i < sourceIds.Count; i++)
                {
                    var sourceId = sourceIds[i];
                    var downloadResult = tasks[i].Result;

                    downloadResults[sourceId] = downloadResult;

                    if (downloadResult.Success)
                    {
                        result.SuccessfulDownloads++;
                        result.TotalBytesDownloaded += downloadResult.BytesDownloaded;
                    }
                    else;
                    {
                        result.FailedDownloads++;
                        result.Errors.Add(new SourceError;
                        {
                            SourceId = sourceId,
                            ErrorMessage = downloadResult.ErrorMessage;
                        });
                    }
                }

                result.EndTime = DateTime.UtcNow;
                result.DownloadResults = downloadResults;
                result.Success = result.SuccessfulDownloads > 0;

                _logger.LogInformation($"Batch download completed: {result.SuccessfulDownloads} successful, {result.FailedDownloads} failed");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch download failed");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Kaynağı günceller;
        /// </summary>
        public async Task<OperationResult> UpdateSourceAsync(
            string sourceId,
            ISourceConfiguration newConfiguration,
            CancellationToken cancellationToken = default)
        {
            try
            {
                ValidateConfiguration(newConfiguration);

                lock (_syncLock)
                {
                    if (!_sourceConfigurations.ContainsKey(sourceId))
                    {
                        return OperationResult.Failure($"Source not found: {sourceId}");
                    }

                    _sourceConfigurations[sourceId] = newConfiguration;

                    // Önbelleği temizle;
                    ClearCacheForSource(sourceId);
                }

                _logger.LogInformation($"Updated source configuration: {newConfiguration.SourceName}");

                // Güncelleme sonrası veriyi yeniden indir;
                if (newConfiguration.AutoUpdate)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadDataAsync(sourceId, null, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Auto-update failed for source {sourceId}");
                        }
                    }, cancellationToken);
                }

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update source {sourceId}");
                return OperationResult.Failure($"Update failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kaynağı kaldırır;
        /// </summary>
        public OperationResult RemoveSource(string sourceId)
        {
            try
            {
                lock (_syncLock)
                {
                    if (!_sourceConfigurations.ContainsKey(sourceId))
                    {
                        return OperationResult.Failure($"Source not found: {sourceId}");
                    }

                    _sourceConfigurations.Remove(sourceId);
                    ClearCacheForSource(sourceId);

                    _logger.LogInformation($"Removed source: {sourceId}");

                    return OperationResult.Success();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove source {sourceId}");
                return OperationResult.Failure($"Remove failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Kaynak durumunu alır;
        /// </summary>
        public SourceStatus GetSourceStatus(string sourceId)
        {
            lock (_syncLock)
            {
                if (!_sourceConfigurations.TryGetValue(sourceId, out var config))
                {
                    return new SourceStatus;
                    {
                        SourceId = sourceId,
                        IsAvailable = false,
                        LastChecked = DateTime.UtcNow;
                    };
                }

                var downloader = GetDownloaderForSource(config);
                var cacheEntry = GetCacheEntry(sourceId, config.CacheKey);

                return new SourceStatus;
                {
                    SourceId = sourceId,
                    SourceName = config.SourceName,
                    IsAvailable = true,
                    LastChecked = cacheEntry?.LastAccessTime ?? DateTime.MinValue,
                    IsCached = cacheEntry != null,
                    CacheAge = cacheEntry?.Age ?? TimeSpan.Zero,
                    DownloaderStatus = downloader?.Status.ToString() ?? "Unknown",
                    Configuration = config;
                };
            }
        }

        /// <summary>
        /// Tüm kaynakların durumunu alır;
        /// </summary>
        public List<SourceStatus> GetAllSourceStatuses()
        {
            lock (_syncLock)
            {
                var statuses = new List<SourceStatus>();

                foreach (var config in _sourceConfigurations.Values)
                {
                    statuses.Add(GetSourceStatus(config.SourceId));
                }

                return statuses;
            }
        }

        /// <summary>
        /// Önbelleği temizler;
        /// </summary>
        public OperationResult ClearCache(string sourceId = null)
        {
            try
            {
                lock (_syncLock)
                {
                    if (string.IsNullOrEmpty(sourceId))
                    {
                        _cache.Clear();
                        _logger.LogInformation("Cleared all cache entries");
                    }
                    else;
                    {
                        ClearCacheForSource(sourceId);
                        _logger.LogInformation($"Cleared cache for source: {sourceId}");
                    }

                    return OperationResult.Success();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear cache");
                return OperationResult.Failure($"Cache clear failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Performans metriklerini alır;
        /// </summary>
        public SourceManagerMetrics GetMetrics()
        {
            return new SourceManagerMetrics;
            {
                ManagerId = ManagerId,
                Status = Status,
                TotalSources = _sourceConfigurations.Count,
                ActiveDownloads = ActiveDownloads,
                CachedSources = CachedSources,
                TotalBytesDownloaded = TotalBytesDownloaded,
                TotalRequestsProcessed = TotalRequestsProcessed,
                CacheHitRate = CalculateCacheHitRate(),
                AverageDownloadTime = CalculateAverageDownloadTime(),
                Uptime = DateTime.UtcNow - _startTime,
                Timestamp = DateTime.UtcNow;
            };
        }
        #endregion;

        #region Private Methods;
        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler;
            {
                UseCookies = true,
                CookieContainer = new System.Net.CookieContainer(),
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5;
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 10 * 1024 * 1024 // 10 MB;
            };

            // Varsayılan headers;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NEDA-SourceManager/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html, application/xhtml+xml, */*");
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            return client;
        }

        private async Task LoadSourceConfigurationsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Varsayılan kaynakları yükle;
                var defaultConfigs = GetDefaultSourceConfigurations();

                foreach (var config in defaultConfigs)
                {
                    _sourceConfigurations[config.SourceId] = config;
                }

                // Harici konfigürasyon dosyası varsa yükle;
                await LoadExternalConfigurationsAsync(cancellationToken);

                _logger.LogInformation($"Loaded {_sourceConfigurations.Count} source configurations");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load source configurations");
                throw;
            }
        }

        private List<ISourceConfiguration> GetDefaultSourceConfigurations()
        {
            return new List<ISourceConfiguration>
            {
                new SourceConfiguration;
                {
                    SourceId = "github_api",
                    SourceName = "GitHub API",
                    SourceType = SourceType.API,
                    Endpoint = "https://api.github.com",
                    RequestMethod = HttpMethod.Get,
                    RequiresAuthentication = true,
                    AuthenticationType = AuthenticationType.BearerToken,
                    CacheDuration = TimeSpan.FromMinutes(5),
                    EnableCaching = true,
                    AutoUpdate = true,
                    UpdateInterval = TimeSpan.FromHours(1)
                },
                new SourceConfiguration;
                {
                    SourceId = "stackoverflow_api",
                    SourceName = "StackOverflow API",
                    SourceType = SourceType.API,
                    Endpoint = "https://api.stackexchange.com/2.3",
                    RequestMethod = HttpMethod.Get,
                    RequiresAuthentication = false,
                    CacheDuration = TimeSpan.FromMinutes(10),
                    EnableCaching = true,
                    AutoUpdate = true,
                    UpdateInterval = TimeSpan.FromHours(2)
                },
                new SourceConfiguration;
                {
                    SourceId = "news_feed",
                    SourceName = "News Feed",
                    SourceType = SourceType.RSS,
                    Endpoint = "https://news.google.com/rss",
                    RequestMethod = HttpMethod.Get,
                    RequiresAuthentication = false,
                    CacheDuration = TimeSpan.FromMinutes(15),
                    EnableCaching = true,
                    AutoUpdate = true,
                    UpdateInterval = TimeSpan.FromMinutes(30)
                }
            };
        }

        private async Task LoadExternalConfigurationsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Harici konfigürasyon dosyalarını yükle;
                // Bu örnekte basit bir implementasyon;
                // Gerçek uygulamada dosya sistemi veya veritabanından yüklenir;
                var configPath = "config/sources.json";

                if (System.IO.File.Exists(configPath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(configPath, cancellationToken);
                    var externalConfigs = JsonSerializer.Deserialize<List<SourceConfiguration>>(json);

                    foreach (var config in externalConfigs)
                    {
                        _sourceConfigurations[config.SourceId] = config;
                    }

                    _logger.LogInformation($"Loaded {externalConfigs.Count} external source configurations");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load external configurations");
            }
        }

        private async Task CreateDefaultDownloadersAsync(CancellationToken cancellationToken)
        {
            try
            {
                // API downloader;
                var apiDownloader = new APIDownloader(_httpClient, _logger);
                await apiDownloader.InitializeAsync(cancellationToken);
                _downloaders["API"] = apiDownloader;

                // Web downloader;
                var webDownloader = new WebDownloader(_httpClient, _logger);
                await webDownloader.InitializeAsync(cancellationToken);
                _downloaders["Web"] = webDownloader;

                // RSS downloader;
                var rssDownloader = new RSSDownloader(_httpClient, _logger);
                await rssDownloader.InitializeAsync(cancellationToken);
                _downloaders["RSS"] = rssDownloader;

                // File downloader;
                var fileDownloader = new FileDownloader(_logger);
                await fileDownloader.InitializeAsync(cancellationToken);
                _downloaders["File"] = fileDownloader;

                _logger.LogInformation($"Created {_downloaders.Count} default downloaders");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create downloaders");
                throw;
            }
        }

        private async Task LoadCacheAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Cache dosyasını yükle;
                var cachePath = "cache/source_cache.json";

                if (System.IO.File.Exists(cachePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(cachePath, cancellationToken);
                    var cacheEntries = JsonSerializer.Deserialize<Dictionary<string, SourceCache>>(json);

                    lock (_syncLock)
                    {
                        foreach (var entry in cacheEntries)
                        {
                            _cache[entry.Key] = entry.Value;
                        }
                    }

                    _logger.LogInformation($"Loaded {cacheEntries.Count} cache entries");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cache");
            }
        }

        private void StartTimers()
        {
            // Sağlık kontrolü her 5 dakikada bir;
            _healthCheckTimer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(5));

            // Önbellek temizliği her saatte bir;
            _cacheCleanupTimer.Change(TimeSpan.Zero, TimeSpan.FromHours(1));

            _logger.LogInformation("Timers started");
        }

        private void HealthCheckCallback(object state)
        {
            try
            {
                PerformHealthCheck();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check callback error");
            }
        }

        private void CacheCleanupCallback(object state)
        {
            try
            {
                CleanupExpiredCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup callback error");
            }
        }

        private void PerformHealthCheck()
        {
            try
            {
                _logger.LogDebug("Performing health check...");

                int healthySources = 0;
                int unhealthySources = 0;

                foreach (var config in _sourceConfigurations.Values)
                {
                    try
                    {
                        // Kaynağa ping at;
                        var isAvailable = CheckSourceAvailability(config);

                        if (isAvailable)
                        {
                            healthySources++;
                        }
                        else;
                        {
                            unhealthySources++;
                            _logger.LogWarning($"Source {config.SourceName} is unavailable");
                        }

                        // Event tetikle;
                        OnSourceUpdate?.Invoke(this, new SourceManagerEventArgs;
                        {
                            SourceId = config.SourceId,
                            SourceName = config.SourceName,
                            EventType = isAvailable ? SourceEventType.HealthCheckPassed : SourceEventType.HealthCheckFailed,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Health check failed for source {config.SourceName}");
                        unhealthySources++;
                    }
                }

                _logger.LogInformation($"Health check completed: {healthySources} healthy, {unhealthySources} unhealthy");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
            }
        }

        private bool CheckSourceAvailability(ISourceConfiguration config)
        {
            // Basit bir ping kontrolü;
            using (var pingClient = new HttpClient())
            {
                pingClient.Timeout = TimeSpan.FromSeconds(5);

                var response = pingClient.SendAsync(
                    new HttpRequestMessage(HttpMethod.Head, config.Endpoint)
                ).Result;

                return response.IsSuccessStatusCode;
            }
        }

        private void CleanupExpiredCache()
        {
            try
            {
                lock (_syncLock)
                {
                    var expiredKeys = new List<string>();
                    var now = DateTime.UtcNow;

                    foreach (var entry in _cache)
                    {
                        if (entry.Value.ExpirationTime < now)
                        {
                            expiredKeys.Add(entry.Key);
                        }
                    }

                    foreach (var key in expiredKeys)
                    {
                        _cache.Remove(key);
                    }

                    if (expiredKeys.Count > 0)
                    {
                        _logger.LogInformation($"Cleaned up {expiredKeys.Count} expired cache entries");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup failed");
            }
        }

        private bool ShouldUseCache(ISourceConfiguration config, DownloadOptions options)
        {
            if (!config.EnableCaching)
                return false;

            if (options?.ForceDownload == true)
                return false;

            var cacheEntry = GetCacheEntry(config.SourceId, config.CacheKey);
            if (cacheEntry == null)
                return false;

            // Cache süresi dolmuş mu kontrol et;
            if (cacheEntry.ExpirationTime < DateTime.UtcNow)
                return false;

            // Minimum cache süresi kontrolü;
            var minCacheAge = options?.MinCacheAge ?? TimeSpan.Zero;
            if (cacheEntry.Age < minCacheAge)
                return false;

            return true;
        }

        private SourceCache GetCacheEntry(string sourceId, string cacheKey)
        {
            var key = $"{sourceId}_{cacheKey}";

            lock (_syncLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.LastAccessTime = DateTime.UtcNow;
                    return entry
                }
            }

            return null;
        }

        private SourceData GetCachedData(string sourceId, string cacheKey)
        {
            var entry = GetCacheEntry(sourceId, cacheKey);
            return entry?.Data;
        }

        private void CacheData(string sourceId, string cacheKey, SourceData data, TimeSpan duration)
        {
            try
            {
                var key = $"{sourceId}_{cacheKey}";
                var expirationTime = DateTime.UtcNow.Add(duration);

                var cacheEntry = new SourceCache;
                {
                    SourceId = sourceId,
                    CacheKey = cacheKey,
                    Data = data,
                    CreationTime = DateTime.UtcNow,
                    LastAccessTime = DateTime.UtcNow,
                    ExpirationTime = expirationTime,
                    Size = CalculateDataSize(data)
                };

                lock (_syncLock)
                {
                    _cache[key] = cacheEntry
                }

                _logger.LogDebug($"Cached data for source {sourceId}, key: {cacheKey}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cache data");
            }
        }

        private long CalculateDataSize(SourceData data)
        {
            // Basit bir boyut hesaplama;
            if (data.Content is string str)
                return str.Length * 2; // UTF-16 için;
            else if (data.Content is byte[] bytes)
                return bytes.Length;
            else;
                return 0;
        }

        private void ClearCacheForSource(string sourceId)
        {
            var keysToRemove = new List<string>();

            lock (_syncLock)
            {
                foreach (var key in _cache.Keys)
                {
                    if (key.StartsWith(sourceId + "_"))
                    {
                        keysToRemove.Add(key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }
            }
        }

        private IDataDownloader GetDownloaderForSource(ISourceConfiguration config)
        {
            switch (config.SourceType)
            {
                case SourceType.API:
                    return _downloaders.GetValueOrDefault("API");
                case SourceType.Web:
                    return _downloaders.GetValueOrDefault("Web");
                case SourceType.RSS:
                    return _downloaders.GetValueOrDefault("RSS");
                case SourceType.File:
                    return _downloaders.GetValueOrDefault("File");
                default:
                    return null;
            }
        }

        private async Task<SourceData> ProcessDownloadedDataAsync(
            RawData rawData,
            ISourceConfiguration config,
            CancellationToken cancellationToken)
        {
            try
            {
                var processedData = new SourceData;
                {
                    SourceId = config.SourceId,
                    SourceName = config.SourceName,
                    OriginalContent = rawData.Content,
                    DownloadTime = DateTime.UtcNow,
                    ContentType = rawData.ContentType;
                };

                // İçeriği türüne göre parse et;
                if (!string.IsNullOrEmpty(config.ParserType))
                {
                    var parser = CreateParser(config.ParserType);
                    if (parser != null)
                    {
                        processedData.ParsedContent = await parser.ParseAsync(
                            rawData.Content,
                            config.ParserOptions,
                            cancellationToken);
                    }
                }

                // Metadata ekle;
                processedData.Metadata = new Dictionary<string, object>
                {
                    ["ContentLength"] = rawData.ContentLength,
                    ["Encoding"] = rawData.Encoding,
                    ["ResponseCode"] = rawData.ResponseCode,
                    ["Url"] = rawData.Url;
                };

                return processedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process downloaded data");
                throw;
            }
        }

        private IDataParser CreateParser(string parserType)
        {
            return parserType switch;
            {
                "JSON" => new JsonParser(),
                "XML" => new XmlParser(),
                "HTML" => new HtmlParser(),
                "RSS" => new RssParser(),
                _ => null;
            };
        }

        private void UpdateStatistics(long bytesDownloaded)
        {
            lock (_syncLock)
            {
                TotalBytesDownloaded += bytesDownloaded;
                TotalRequestsProcessed++;
            }
        }

        private double CalculateCacheHitRate()
        {
            if (TotalRequestsProcessed == 0)
                return 0;

            // Basit bir cache hit rate hesaplama;
            // Gerçek uygulamada daha detaylı tracking gerekir;
            return (double)CachedSources / TotalRequestsProcessed;
        }

        private TimeSpan CalculateAverageDownloadTime()
        {
            // Basit bir ortalama hesaplama;
            // Gerçek uygulamada daha detaylı tracking gerekir;
            return TimeSpan.FromMilliseconds(100); // Örnek değer;
        }

        private void ValidateConfiguration(ISourceConfiguration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (string.IsNullOrWhiteSpace(config.SourceId))
                throw new ArgumentException("Source ID cannot be empty", nameof(config));

            if (string.IsNullOrWhiteSpace(config.SourceName))
                throw new ArgumentException("Source name cannot be empty", nameof(config));

            if (string.IsNullOrWhiteSpace(config.Endpoint))
                throw new ArgumentException("Endpoint cannot be empty", nameof(config));

            if (config.RequestMethod == null)
                throw new ArgumentException("Request method cannot be null", nameof(config));
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
                    // Timer'ları durdur;
                    _healthCheckTimer?.Dispose();
                    _cacheCleanupTimer?.Dispose();

                    // HttpClient'ı temizle;
                    _httpClient?.Dispose();

                    // Downloader'ları temizle;
                    foreach (var downloader in _downloaders.Values)
                    {
                        if (downloader is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    // Cache'i kaydet;
                    SaveCacheAsync().Wait(TimeSpan.FromSeconds(5));
                }

                _isDisposed = true;
                Status = SourceManagerStatus.Disposed;

                _logger.LogInformation("Source Manager disposed");
            }
        }

        private async Task SaveCacheAsync()
        {
            try
            {
                var cachePath = "cache/source_cache.json";
                var directory = System.IO.Path.GetDirectoryName(cachePath);

                if (!System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                Dictionary<string, SourceCache> cacheCopy;

                lock (_syncLock)
                {
                    cacheCopy = new Dictionary<string, SourceCache>(_cache);
                }

                var json = JsonSerializer.Serialize(cacheCopy, new JsonSerializerOptions
                {
                    WriteIndented = true;
                });

                await System.IO.File.WriteAllTextAsync(cachePath, json);

                _logger.LogInformation($"Saved {cacheCopy.Count} cache entries");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save cache");
            }
        }

        ~SourceManager()
        {
            Dispose(false);
        }
        #endregion;

        #region Supporting Classes and Enums;
        public enum SourceManagerStatus;
        {
            Stopped,
            Initializing,
            Running,
            Error,
            Disposed;
        }

        public enum SourceType;
        {
            API,
            Web,
            RSS,
            File,
            Database;
        }

        public enum AuthenticationType;
        {
            None,
            Basic,
            BearerToken,
            ApiKey,
            OAuth;
        }

        public class SourceConfiguration : ISourceConfiguration;
        {
            public string SourceId { get; set; }
            public string SourceName { get; set; }
            public SourceType SourceType { get; set; }
            public string Endpoint { get; set; }
            public HttpMethod RequestMethod { get; set; } = HttpMethod.Get;
            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
            public bool RequiresAuthentication { get; set; }
            public AuthenticationType AuthenticationType { get; set; }
            public string AuthenticationToken { get; set; }
            public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);
            public string CacheKey { get; set; }
            public bool EnableCaching { get; set; } = true;
            public bool AutoUpdate { get; set; }
            public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromHours(1);
            public string ParserType { get; set; }
            public Dictionary<string, object> ParserOptions { get; set; } = new Dictionary<string, object>();
            public int RetryCount { get; set; } = 3;
            public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
            public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
            public int MaxResponseSize { get; set; } = 10 * 1024 * 1024; // 10 MB;
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        public class SourceCache;
        {
            public string SourceId { get; set; }
            public string CacheKey { get; set; }
            public SourceData Data { get; set; }
            public DateTime CreationTime { get; set; }
            public DateTime LastAccessTime { get; set; }
            public DateTime ExpirationTime { get; set; }
            public long Size { get; set; }
            public TimeSpan Age => DateTime.UtcNow - CreationTime;
        }

        public class SourceData;
        {
            public string SourceId { get; set; }
            public string SourceName { get; set; }
            public object OriginalContent { get; set; }
            public object ParsedContent { get; set; }
            public DateTime DownloadTime { get; set; }
            public string ContentType { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        public class SourceManagerMetrics;
        {
            public string ManagerId { get; set; }
            public SourceManagerStatus Status { get; set; }
            public int TotalSources { get; set; }
            public int ActiveDownloads { get; set; }
            public int CachedSources { get; set; }
            public long TotalBytesDownloaded { get; set; }
            public long TotalRequestsProcessed { get; set; }
            public double CacheHitRate { get; set; }
            public TimeSpan AverageDownloadTime { get; set; }
            public TimeSpan Uptime { get; set; }
            public DateTime Timestamp { get; set; }
        }
        #endregion;
    }

    #region Interfaces;
    public interface ISourceManager : IDisposable
    {
        Task<OperationResult> InitializeAsync(CancellationToken cancellationToken = default);
        Task<SourceDataResult> DownloadDataAsync(string sourceId, DownloadOptions options = null, CancellationToken cancellationToken = default);
        Task<BatchDownloadResult> DownloadBatchAsync(List<string> sourceIds, BatchDownloadOptions options = null, CancellationToken cancellationToken = default);
        OperationResult AddSourceConfiguration(ISourceConfiguration configuration);
        Task<OperationResult> UpdateSourceAsync(string sourceId, ISourceConfiguration newConfiguration, CancellationToken cancellationToken = default);
        OperationResult RemoveSource(string sourceId);
        SourceStatus GetSourceStatus(string sourceId);
        List<SourceStatus> GetAllSourceStatuses();
        OperationResult ClearCache(string sourceId = null);
        SourceManagerMetrics GetMetrics();

        event EventHandler<SourceManagerEventArgs> OnSourceUpdate;
        event EventHandler<DownloadCompletedEventArgs> OnDownloadCompleted;
    }

    public interface ISourceConfiguration;
    {
        string SourceId { get; set; }
        string SourceName { get; set; }
        SourceType SourceType { get; set; }
        string Endpoint { get; set; }
        HttpMethod RequestMethod { get; set; }
        Dictionary<string, string> Headers { get; set; }
        Dictionary<string, string> Parameters { get; set; }
        bool RequiresAuthentication { get; set; }
        AuthenticationType AuthenticationType { get; set; }
        string AuthenticationToken { get; set; }
        TimeSpan CacheDuration { get; set; }
        string CacheKey { get; set; }
        bool EnableCaching { get; set; }
        bool AutoUpdate { get; set; }
        TimeSpan UpdateInterval { get; set; }
        string ParserType { get; set; }
        Dictionary<string, object> ParserOptions { get; set; }
        int RetryCount { get; set; }
        TimeSpan RetryDelay { get; set; }
        TimeSpan Timeout { get; set; }
        int MaxResponseSize { get; set; }
        Dictionary<string, object> Metadata { get; set; }
    }

    public interface IDataDownloader;
    {
        DownloaderStatus Status { get; }
        Task<DownloadResult> DownloadAsync(ISourceConfiguration configuration, DownloadOptions options = null, CancellationToken cancellationToken = default);
        Task<OperationResult> InitializeAsync(CancellationToken cancellationToken = default);
        Task<OperationResult> ShutdownAsync(CancellationToken cancellationToken = default);
    }
    #endregion;
}
