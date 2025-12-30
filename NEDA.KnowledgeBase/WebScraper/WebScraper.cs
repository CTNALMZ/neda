using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.KnowledgeBase.Parsers;
using NEDA.Core.Security;

namespace NEDA.KnowledgeBase.WebScraper;
{
    /// <summary>
    /// Web sayfalarını kazıyan ve veri çıkaran gelişmiş web scraper motoru;
    /// </summary>
    public class WebScraper : IWebScraper, IDisposable;
    {
        #region Fields;
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly ISecurityManager _securityManager;
        private readonly HttpClient _httpClient;
        private readonly HttpClientHandler _httpClientHandler;
        private readonly CookieContainer _cookieContainer;
        private readonly Random _random;
        private readonly Dictionary<string, SessionData> _sessions;
        private readonly List<ProxyServer> _proxyServers;
        private readonly Queue<ScrapeRequest> _requestQueue;
        private readonly object _queueLock = new object();
        private readonly object _sessionLock = new object();
        private CancellationTokenSource _scrapingCancellationTokenSource;
        private Task _scrapingTask;
        private bool _isDisposed;
        private bool _isRunning;
        private int _activeRequests;
        private DateTime _lastRequestTime;
        #endregion;

        #region Properties;
        public string ScraperId { get; private set; }
        public WebScraperStatus Status { get; private set; }
        public ScraperConfiguration Configuration { get; private set; }
        public int QueueLength => _requestQueue.Count;
        public int ActiveRequests => _activeRequests;
        public int TotalRequestsProcessed { get; private set; }
        public int TotalDataExtracted { get; private set; }
        public long TotalBytesDownloaded { get; private set; }
        public event EventHandler<ScrapingCompletedEventArgs> OnScrapingCompleted;
        public event EventHandler<DataExtractedEventArgs> OnDataExtracted;
        public event EventHandler<ScrapingErrorEventArgs> OnScrapingError;
        #endregion;

        #region Constructor;
        public WebScraper(
            ILogger logger,
            IErrorReporter errorReporter,
            ISecurityManager securityManager = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _securityManager = securityManager;

            ScraperId = Guid.NewGuid().ToString();
            Status = WebScraperStatus.Stopped;

            _cookieContainer = new CookieContainer();
            _random = new Random();
            _sessions = new Dictionary<string, SessionData>();
            _proxyServers = new List<ProxyServer>();
            _requestQueue = new Queue<ScrapeRequest>();
            _lastRequestTime = DateTime.MinValue;

            // HTTP client handler oluştur;
            _httpClientHandler = new HttpClientHandler;
            {
                UseCookies = true,
                CookieContainer = _cookieContainer,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseProxy = false,
                Proxy = null;
            };

            // HTTP client oluştur;
            _httpClient = new HttpClient(_httpClientHandler)
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 50 * 1024 * 1024 // 50 MB;
            };

            // Varsayılan headers;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");

            _logger.LogInformation($"Web Scraper initialized with ID: {ScraperId}");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Web Scraper'ı başlatır;
        /// </summary>
        public async Task<OperationResult> InitializeAsync(ScraperConfiguration configuration = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (Status == WebScraperStatus.Running)
                {
                    _logger.LogWarning("Web Scraper is already initialized");
                    return OperationResult.Failure("Web Scraper is already initialized");
                }

                _logger.LogInformation("Initializing Web Scraper...");
                Status = WebScraperStatus.Initializing;

                // Konfigürasyonu yükle;
                Configuration = configuration ?? GetDefaultConfiguration();

                // HTTP client ayarlarını güncelle;
                UpdateHttpClientSettings();

                // Proxy listesini yükle;
                await LoadProxyServersAsync(cancellationToken);

                // Robot.txt'leri yükle;
                await LoadRobotsTxtAsync(cancellationToken);

                // Session'ları temizle;
                ClearSessions();

                // Scraping task'ını başlat;
                StartScrapingTask();

                Status = WebScraperStatus.Running;
                _isRunning = true;

                _logger.LogInformation("Web Scraper initialized successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Web Scraper");
                Status = WebScraperStatus.Error;
                return OperationResult.Failure($"Initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Web Scraper'ı durdurur;
        /// </summary>
        public async Task<OperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (Status != WebScraperStatus.Running)
                {
                    return OperationResult.Failure("Web Scraper is not running");
                }

                _logger.LogInformation("Shutting down Web Scraper...");
                Status = WebScraperStatus.Stopping;

                // Scraping task'ını durdur;
                StopScrapingTask();

                // Bekleyen request'leri temizle;
                ClearQueue();

                // Session'ları kaydet;
                await SaveSessionsAsync(cancellationToken);

                // HTTP client'ı temizle;
                _httpClient?.CancelPendingRequests();

                Status = WebScraperStatus.Stopped;
                _isRunning = false;

                _logger.LogInformation("Web Scraper shut down successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to shutdown Web Scraper");
                return OperationResult.Failure($"Shutdown failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Web sayfasını kazır;
        /// </summary>
        public async Task<ScrapeResult> ScrapeAsync(string url, ScrapeOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isRunning)
                {
                    return ScrapeResult.Failure("Web Scraper is not running");
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return ScrapeResult.Failure("URL cannot be null or empty");
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return ScrapeResult.Failure("Invalid URL format");
                }

                var scrapeOptions = options ?? ScrapeOptions.Default;

                // Robot.txt kontrolü;
                if (scrapeOptions.RespectRobotsTxt && !IsAllowedByRobotsTxt(uri))
                {
                    _logger.LogWarning($"URL is disallowed by robots.txt: {url}");
                    return ScrapeResult.Failure("URL is disallowed by robots.txt");
                }

                // Request'i kuyruğa ekle veya doğrudan çalıştır;
                if (scrapeOptions.UseQueue)
                {
                    var request = new ScrapeRequest;
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Url = url,
                        Options = scrapeOptions,
                        CreatedAt = DateTime.UtcNow;
                    };

                    EnqueueRequest(request);

                    // Sonucu bekle;
                    return await WaitForResultAsync(request.RequestId, scrapeOptions.Timeout, cancellationToken);
                }
                else;
                {
                    // Doğrudan çalıştır;
                    return await ExecuteScrapeAsync(url, scrapeOptions, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scrape URL: {url}");
                return ScrapeResult.Failure($"Scraping failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Birden fazla web sayfasını kazır;
        /// </summary>
        public async Task<BatchScrapeResult> ScrapeBatchAsync(List<string> urls, BatchScrapeOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isRunning)
                {
                    return BatchScrapeResult.Failure("Web Scraper is not running");
                }

                if (urls == null || urls.Count == 0)
                {
                    return BatchScrapeResult.Failure("URL list cannot be null or empty");
                }

                var batchOptions = options ?? BatchScrapeOptions.Default;
                var results = new Dictionary<string, ScrapeResult>();
                var errors = new List<BatchError>();

                _logger.LogInformation($"Starting batch scrape of {urls.Count} URLs");

                if (batchOptions.ParallelProcessing)
                {
                    // Paralel işleme;
                    var semaphore = new SemaphoreSlim(batchOptions.MaxConcurrentRequests);
                    var tasks = new List<Task>();

                    foreach (var url in urls)
                    {
                        await semaphore.WaitAsync(cancellationToken);

                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var result = await ScrapeAsync(url, batchOptions.ScrapeOptions, cancellationToken);
                                lock (results)
                                {
                                    results[url] = result;
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (errors)
                                {
                                    errors.Add(new BatchError;
                                    {
                                        Url = url,
                                        ErrorMessage = ex.Message,
                                        Timestamp = DateTime.UtcNow;
                                    });
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }, cancellationToken));
                    }

                    await Task.WhenAll(tasks);
                }
                else;
                {
                    // Sıralı işleme;
                    foreach (var url in urls)
                    {
                        try
                        {
                            var result = await ScrapeAsync(url, batchOptions.ScrapeOptions, cancellationToken);
                            results[url] = result;

                            // Rate limiting;
                            if (batchOptions.DelayBetweenRequests > TimeSpan.Zero)
                            {
                                await Task.Delay(batchOptions.DelayBetweenRequests, cancellationToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new BatchError;
                            {
                                Url = url,
                                ErrorMessage = ex.Message,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                    }
                }

                var batchResult = new BatchScrapeResult;
                {
                    Success = errors.Count == 0,
                    Results = results,
                    Errors = errors,
                    TotalUrls = urls.Count,
                    SuccessfulScrapes = results.Count,
                    FailedScrapes = errors.Count,
                    StartTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(batchOptions.Timeout.TotalMilliseconds),
                    EndTime = DateTime.UtcNow;
                };

                _logger.LogInformation($"Batch scrape completed: {batchResult.SuccessfulScrapes} successful, {batchResult.FailedScrapes} failed");

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch scrape failed");
                return BatchScrapeResult.Failure($"Batch scraping failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Web sayfasından veri çıkarır;
        /// </summary>
        public async Task<ExtractionResult> ExtractDataAsync(string url, ExtractionRules rules, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isRunning)
                {
                    return ExtractionResult.Failure("Web Scraper is not running");
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return ExtractionResult.Failure("URL cannot be null or empty");
                }

                if (rules == null)
                {
                    return ExtractionResult.Failure("Extraction rules cannot be null");
                }

                _logger.LogInformation($"Extracting data from URL: {url}");

                // Sayfayı kazı;
                var scrapeResult = await ScrapeAsync(url, new ScrapeOptions;
                {
                    UseCache = true,
                    CacheDuration = TimeSpan.FromHours(1),
                    RespectRobotsTxt = true,
                    Timeout = TimeSpan.FromSeconds(60)
                }, cancellationToken);

                if (!scrapeResult.Success)
                {
                    return ExtractionResult.Failure($"Failed to scrape page: {scrapeResult.ErrorMessage}");
                }

                // Veriyi çıkar;
                var extractedData = ExtractDataFromContent(scrapeResult.Content, scrapeResult.ContentType, rules);

                // Event tetikle;
                OnDataExtracted?.Invoke(this, new DataExtractedEventArgs;
                {
                    Url = url,
                    Data = extractedData,
                    RuleSet = rules,
                    Timestamp = DateTime.UtcNow;
                });

                return ExtractionResult.Success(extractedData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to extract data from URL: {url}");
                return ExtractionResult.Failure($"Extraction failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Web sayfasındaki link'leri çıkarır;
        /// </summary>
        public async Task<LinkExtractionResult> ExtractLinksAsync(string url, LinkExtractionOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isRunning)
                {
                    return LinkExtractionResult.Failure("Web Scraper is not running");
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return LinkExtractionResult.Failure("URL cannot be null or empty");
                }

                _logger.LogDebug($"Extracting links from URL: {url}");

                var extractionOptions = options ?? LinkExtractionOptions.Default;

                // Sayfayı kazı;
                var scrapeResult = await ScrapeAsync(url, new ScrapeOptions;
                {
                    UseCache = true,
                    CacheDuration = TimeSpan.FromMinutes(30),
                    RespectRobotsTxt = true;
                }, cancellationToken);

                if (!scrapeResult.Success)
                {
                    return LinkExtractionResult.Failure($"Failed to scrape page: {scrapeResult.ErrorMessage}");
                }

                // Link'leri çıkar;
                var links = ExtractLinksFromContent(scrapeResult.Content, scrapeResult.ContentType, url, extractionOptions);

                // Filtrele;
                if (extractionOptions.Filters != null)
                {
                    links = FilterLinks(links, extractionOptions.Filters);
                }

                // Sırala;
                if (extractionOptions.SortBy != LinkSortOption.None)
                {
                    links = SortLinks(links, extractionOptions.SortBy);
                }

                return LinkExtractionResult.Success(links);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to extract links from URL: {url}");
                return LinkExtractionResult.Failure($"Link extraction failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Web sayfasındaki görselleri çıkarır;
        /// </summary>
        public async Task<ImageExtractionResult> ExtractImagesAsync(string url, ImageExtractionOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isRunning)
                {
                    return ImageExtractionResult.Failure("Web Scraper is not running");
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return ImageExtractionResult.Failure("URL cannot be null or empty");
                }

                _logger.LogDebug($"Extracting images from URL: {url}");

                var extractionOptions = options ?? ImageExtractionOptions.Default;

                // Sayfayı kazı;
                var scrapeResult = await ScrapeAsync(url, new ScrapeOptions;
                {
                    UseCache = true,
                    CacheDuration = TimeSpan.FromMinutes(30),
                    RespectRobotsTxt = true;
                }, cancellationToken);

                if (!scrapeResult.Success)
                {
                    return ImageExtractionResult.Failure($"Failed to scrape page: {scrapeResult.ErrorMessage}");
                }

                // Görselleri çıkar;
                var images = ExtractImagesFromContent(scrapeResult.Content, scrapeResult.ContentType, url, extractionOptions);

                // Filtrele;
                if (extractionOptions.Filters != null)
                {
                    images = FilterImages(images, extractionOptions.Filters);
                }

                // İndir (isteğe bağlı)
                if (extractionOptions.DownloadImages)
                {
                    await DownloadImagesAsync(images, extractionOptions.DownloadPath, cancellationToken);
                }

                return ImageExtractionResult.Success(images);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to extract images from URL: {url}");
                return ImageExtractionResult.Failure($"Image extraction failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Dinamik içeriği kazır (JavaScript render)
        /// </summary>
        public async Task<DynamicScrapeResult> ScrapeDynamicContentAsync(string url, DynamicScrapeOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isRunning)
                {
                    return DynamicScrapeResult.Failure("Web Scraper is not running");
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return DynamicScrapeResult.Failure("URL cannot be null or empty");
                }

                _logger.LogInformation($"Scraping dynamic content from URL: {url}");

                var dynamicOptions = options ?? DynamicScrapeOptions.Default;

                // Selenium/Puppeteer gibi bir tool kullanılması gerekir;
                // Bu örnekte simüle ediyoruz;

                var result = await ScrapeAsync(url, new ScrapeOptions;
                {
                    UseCache = false,
                    RespectRobotsTxt = true,
                    Timeout = TimeSpan.FromSeconds(dynamicOptions.RenderTimeout)
                }, cancellationToken);

                if (!result.Success)
                {
                    return DynamicScrapeResult.Failure($"Failed to scrape dynamic content: {result.ErrorMessage}");
                }

                // JavaScript'in render etmesi için bekle;
                if (dynamicOptions.WaitForRender)
                {
                    await Task.Delay(dynamicOptions.WaitTime, cancellationToken);
                }

                // Ek işlemler (click, scroll, vb.)
                if (dynamicOptions.Actions != null)
                {
                    foreach (var action in dynamicOptions.Actions)
                    {
                        await ExecuteDynamicActionAsync(action, cancellationToken);
                    }
                }

                // Tekrar içeriği al;
                var finalResult = await ScrapeAsync(url, new ScrapeOptions;
                {
                    UseCache = false,
                    RespectRobotsTxt = true;
                }, cancellationToken);

                return new DynamicScrapeResult;
                {
                    Success = finalResult.Success,
                    Content = finalResult.Content,
                    ContentType = finalResult.ContentType,
                    StatusCode = finalResult.StatusCode,
                    Headers = finalResult.Headers,
                    ExecutionTime = DateTime.UtcNow - result.Timestamp,
                    ActionsPerformed = dynamicOptions.Actions?.Count ?? 0;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scrape dynamic content from URL: {url}");
                return DynamicScrapeResult.Failure($"Dynamic scraping failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Session oluşturur veya yönetir;
        /// </summary>
        public OperationResult CreateSession(string sessionId, SessionConfiguration configuration = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return OperationResult.Failure("Session ID cannot be null or empty");
                }

                lock (_sessionLock)
                {
                    if (_sessions.ContainsKey(sessionId))
                    {
                        return OperationResult.Failure($"Session already exists: {sessionId}");
                    }

                    var sessionConfig = configuration ?? new SessionConfiguration();
                    var session = new SessionData;
                    {
                        SessionId = sessionId,
                        Configuration = sessionConfig,
                        CreatedAt = DateTime.UtcNow,
                        LastAccessed = DateTime.UtcNow,
                        Cookies = new CookieContainer(),
                        Headers = new Dictionary<string, string>(),
                        RequestHistory = new List<RequestHistory>()
                    };

                    _sessions[sessionId] = session;

                    _logger.LogInformation($"Created session: {sessionId}");

                    return OperationResult.Success();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create session: {sessionId}");
                return OperationResult.Failure($"Session creation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Session ile kazıma yapar;
        /// </summary>
        public async Task<ScrapeResult> ScrapeWithSessionAsync(string sessionId, string url, ScrapeOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_isRunning)
                {
                    return ScrapeResult.Failure("Web Scraper is not running");
                }

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return ScrapeResult.Failure("Session ID cannot be null or empty");
                }

                SessionData session;

                lock (_sessionLock)
                {
                    if (!_sessions.TryGetValue(sessionId, out session))
                    {
                        return ScrapeResult.Failure($"Session not found: {sessionId}");
                    }

                    session.LastAccessed = DateTime.UtcNow;
                }

                // Session cookie'lerini kullan;
                var sessionOptions = options ?? ScrapeOptions.Default;
                sessionOptions.Cookies = session.Cookies;
                sessionOptions.Headers = session.Headers;

                var result = await ScrapeAsync(url, sessionOptions, cancellationToken);

                // Session history'yi güncelle;
                lock (_sessionLock)
                {
                    session.RequestHistory.Add(new RequestHistory;
                    {
                        Url = url,
                        Timestamp = DateTime.UtcNow,
                        Success = result.Success,
                        StatusCode = result.StatusCode,
                        ResponseSize = result.Content?.Length ?? 0;
                    });

                    // History limit kontrolü;
                    if (session.RequestHistory.Count > session.Configuration.MaxHistorySize)
                    {
                        session.RequestHistory.RemoveAt(0);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scrape with session: {sessionId}");
                return ScrapeResult.Failure($"Session scraping failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Proxy sunucusu ekler;
        /// </summary>
        public OperationResult AddProxyServer(ProxyServer proxy)
        {
            try
            {
                if (proxy == null)
                {
                    return OperationResult.Failure("Proxy server cannot be null");
                }

                if (!proxy.IsValid())
                {
                    return OperationResult.Failure("Invalid proxy server configuration");
                }

                lock (_proxyServers)
                {
                    if (_proxyServers.Any(p => p.Id == proxy.Id))
                    {
                        return OperationResult.Failure($"Proxy server already exists: {proxy.Id}");
                    }

                    _proxyServers.Add(proxy);
                    _logger.LogInformation($"Added proxy server: {proxy.Host}:{proxy.Port}");

                    return OperationResult.Success();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add proxy server");
                return OperationResult.Failure($"Proxy addition failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Proxy rotasyonu yapar;
        /// </summary>
        public ProxyServer RotateProxy()
        {
            lock (_proxyServers)
            {
                if (_proxyServers.Count == 0)
                {
                    return null;
                }

                // Rastgele proxy seç;
                var index = _random.Next(_proxyServers.Count);
                var proxy = _proxyServers[index];

                // Kullanım sayısını artır;
                proxy.UsageCount++;
                proxy.LastUsed = DateTime.UtcNow;

                _logger.LogDebug($"Rotated to proxy: {proxy.Host}:{proxy.Port}");

                return proxy;
            }
        }

        /// <summary>
        /// Kazıma metriklerini alır;
        /// </summary>
        public ScraperMetrics GetMetrics()
        {
            return new ScraperMetrics;
            {
                ScraperId = ScraperId,
                Status = Status,
                QueueLength = QueueLength,
                ActiveRequests = ActiveRequests,
                TotalRequestsProcessed = TotalRequestsProcessed,
                TotalDataExtracted = TotalDataExtracted,
                TotalBytesDownloaded = TotalBytesDownloaded,
                ActiveSessions = _sessions.Count,
                ProxyCount = _proxyServers.Count,
                CacheHitRate = CalculateCacheHitRate(),
                AverageResponseTime = CalculateAverageResponseTime(),
                Uptime = DateTime.UtcNow - _startTime,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Request kuyruğunu temizler;
        /// </summary>
        public void ClearQueue()
        {
            lock (_queueLock)
            {
                _requestQueue.Clear();
                _logger.LogInformation("Request queue cleared");
            }
        }

        /// <summary>
        /// Session'ları temizler;
        /// </summary>
        public void ClearSessions()
        {
            lock (_sessionLock)
            {
                _sessions.Clear();
                _logger.LogInformation("Sessions cleared");
            }
        }

        /// <summary>
        /// Cache'i temizler;
        /// </summary>
        public void ClearCache()
        {
            // Cache temizleme işlemi;
            // Bu örnekte basit bir implementasyon;
            // Gerçek uygulamada önbellek yöneticisi kullanılmalı;
            _logger.LogInformation("Cache cleared");
        }
        #endregion;

        #region Private Methods;
        private ScraperConfiguration GetDefaultConfiguration()
        {
            return new ScraperConfiguration;
            {
                MaxConcurrentRequests = 5,
                RequestTimeout = TimeSpan.FromSeconds(30),
                RetryCount = 3,
                RetryDelay = TimeSpan.FromSeconds(2),
                RateLimit = TimeSpan.FromMilliseconds(500),
                RespectRobotsTxt = true,
                FollowRedirects = true,
                MaxRedirects = 10,
                EnableCaching = true,
                CacheDuration = TimeSpan.FromHours(1),
                MaxCacheSize = 100 * 1024 * 1024, // 100 MB;
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                DefaultHeaders = new Dictionary<string, string>
                {
                    ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
                    ["Accept-Language"] = "en-US,en;q=0.9",
                    ["Accept-Encoding"] = "gzip, deflate",
                    ["Connection"] = "keep-alive",
                    ["Upgrade-Insecure-Requests"] = "1"
                },
                EnableProxyRotation = false,
                ProxyRotationInterval = TimeSpan.FromMinutes(5),
                EnableSessionManagement = true,
                MaxSessions = 10,
                EnableAntiDetect = false,
                AntiDetectTechniques = AntiDetectTechnique.RandomUserAgent | AntiDetectTechnique.RandomDelay,
                LogLevel = LogLevel.Info;
            };
        }

        private void UpdateHttpClientSettings()
        {
            if (Configuration == null) return;

            _httpClient.Timeout = Configuration.RequestTimeout;
            _httpClientHandler.MaxAutomaticRedirections = Configuration.MaxRedirects;
            _httpClientHandler.AllowAutoRedirect = Configuration.FollowRedirects;

            // User agent güncelle;
            if (!string.IsNullOrWhiteSpace(Configuration.UserAgent))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(Configuration.UserAgent);
            }

            // Headers güncelle;
            if (Configuration.DefaultHeaders != null)
            {
                foreach (var header in Configuration.DefaultHeaders)
                {
                    if (!_httpClient.DefaultRequestHeaders.Contains(header.Key))
                    {
                        _httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                    }
                }
            }
        }

        private async Task LoadProxyServersAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Proxy listesini dosyadan veya API'den yükle;
                var proxyFile = "config/proxies.json";

                if (File.Exists(proxyFile))
                {
                    var json = await File.ReadAllTextAsync(proxyFile, cancellationToken);
                    var proxies = JsonConvert.DeserializeObject<List<ProxyServer>>(json);

                    lock (_proxyServers)
                    {
                        _proxyServers.Clear();
                        _proxyServers.AddRange(proxies);
                    }

                    _logger.LogInformation($"Loaded {proxies.Count} proxy servers");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load proxy servers");
            }
        }

        private async Task LoadRobotsTxtAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Domain'ler için robots.txt'leri yükle;
                // Bu örnekte basit bir implementasyon;
                // Gerçek uygulamada robots.txt parser kullanılmalı;

                _logger.LogDebug("Loading robots.txt files");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load robots.txt files");
            }
        }

        private void StartScrapingTask()
        {
            _scrapingCancellationTokenSource = new CancellationTokenSource();
            _scrapingTask = Task.Run(async () => await ProcessQueueAsync(_scrapingCancellationTokenSource.Token));
            _logger.LogInformation("Scraping task started");
        }

        private void StopScrapingTask()
        {
            _scrapingCancellationTokenSource?.Cancel();

            try
            {
                _scrapingTask?.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex, "Error stopping scraping task");
            }

            _scrapingCancellationTokenSource?.Dispose();
            _scrapingCancellationTokenSource = null;

            _logger.LogInformation("Scraping task stopped");
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ScrapeRequest request = null;

                    lock (_queueLock)
                    {
                        if (_requestQueue.Count > 0)
                        {
                            request = _requestQueue.Dequeue();
                        }
                    }

                    if (request != null)
                    {
                        await ProcessRequestAsync(request, cancellationToken);
                    }
                    else;
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing request queue");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private void EnqueueRequest(ScrapeRequest request)
        {
            lock (_queueLock)
            {
                _requestQueue.Enqueue(request);
                _logger.LogDebug($"Request enqueued: {request.RequestId}");
            }
        }

        private async Task<ScrapeResult> WaitForResultAsync(string requestId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            // Bu örnekte basit bir implementasyon;
            // Gerçek uygulamada result tracking mekanizması gerekir;

            while (DateTime.UtcNow - startTime < timeout)
            {
                await Task.Delay(100, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new TaskCanceledException();
                }
            }

            return ScrapeResult.Failure("Request timeout");
        }

        private async Task ProcessRequestAsync(ScrapeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var result = await ExecuteScrapeAsync(request.Url, request.Options, cancellationToken);

                // Event tetikle;
                OnScrapingCompleted?.Invoke(this, new ScrapingCompletedEventArgs;
                {
                    RequestId = request.RequestId,
                    Url = request.Url,
                    Result = result,
                    QueueTime = DateTime.UtcNow - request.CreatedAt,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing request: {request.RequestId}");

                OnScrapingError?.Invoke(this, new ScrapingErrorEventArgs;
                {
                    RequestId = request.RequestId,
                    Url = request.Url,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private async Task<ScrapeResult> ExecuteScrapeAsync(string url, ScrapeOptions options, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            HttpResponseMessage response = null;

            try
            {
                Interlocked.Increment(ref _activeRequests);

                // Rate limiting;
                await ApplyRateLimiting(options);

                // Proxy rotasyonu;
                if (options.UseProxy && Configuration.EnableProxyRotation)
                {
                    var proxy = RotateProxy();
                    if (proxy != null)
                    {
                        ApplyProxy(proxy);
                    }
                }

                // Anti-detect teknikleri;
                if (options.UseAntiDetect)
                {
                    ApplyAntiDetectTechniques();
                }

                // HTTP request oluştur;
                using (var request = new HttpRequestMessage(options.Method, url))
                {
                    // Headers ekle;
                    if (options.Headers != null)
                    {
                        foreach (var header in options.Headers)
                        {
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }

                    // Cookies ekle;
                    if (options.Cookies != null)
                    {
                        // Cookie'leri request'e ekle;
                    }

                    // Body ekle (POST için)
                    if (options.Method == HttpMethod.Post && options.Body != null)
                    {
                        request.Content = new StringContent(options.Body, Encoding.UTF8, options.ContentType);
                    }

                    // Retry mekanizması;
                    for (int attempt = 1; attempt <= options.RetryCount; attempt++)
                    {
                        try
                        {
                            _logger.LogDebug($"Sending request to {url} (Attempt {attempt}/{options.RetryCount})");

                            response = await _httpClient.SendAsync(request, cancellationToken);
                            break;
                        }
                        catch (Exception ex) when (attempt < options.RetryCount)
                        {
                            _logger.LogWarning($"Request failed (Attempt {attempt}): {ex.Message}");
                            await Task.Delay(options.RetryDelay, cancellationToken);

                            // Exponential backoff;
                            options.RetryDelay = TimeSpan.FromMilliseconds(options.RetryDelay.TotalMilliseconds * 2);
                        }
                    }

                    if (response == null)
                    {
                        return ScrapeResult.Failure("All retry attempts failed");
                    }

                    // Response'u işle;
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
                    var statusCode = (int)response.StatusCode;

                    // İstatistikleri güncelle;
                    UpdateStatistics(response.Content.Headers.ContentLength ?? content.Length);

                    _logger.LogDebug($"Request completed: {url} - Status: {statusCode}, Size: {content.Length} bytes");

                    return new ScrapeResult;
                    {
                        Success = response.IsSuccessStatusCode,
                        Url = url,
                        Content = content,
                        ContentType = contentType,
                        StatusCode = statusCode,
                        Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
                        Cookies = GetCookies(url),
                        ResponseTime = DateTime.UtcNow - startTime,
                        Timestamp = DateTime.UtcNow;
                    };
                }
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, $"HTTP request failed: {url}");
                return ScrapeResult.Failure($"HTTP request failed: {httpEx.Message}", httpEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error scraping URL: {url}");
                return ScrapeResult.Failure($"Unexpected error: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
                response?.Dispose();
                _lastRequestTime = DateTime.UtcNow;
            }
        }

        private async Task ApplyRateLimiting(ScrapeOptions options)
        {
            if (options.RateLimit > TimeSpan.Zero)
            {
                var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
                if (timeSinceLastRequest < options.RateLimit)
                {
                    var delay = options.RateLimit - timeSinceLastRequest;
                    await Task.Delay(delay);
                }
            }
            else if (Configuration.RateLimit > TimeSpan.Zero)
            {
                var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
                if (timeSinceLastRequest < Configuration.RateLimit)
                {
                    var delay = Configuration.RateLimit - timeSinceLastRequest;
                    await Task.Delay(delay);
                }
            }
        }

        private void ApplyProxy(ProxyServer proxy)
        {
            if (proxy == null) return;

            _httpClientHandler.UseProxy = true;
            _httpClientHandler.Proxy = new System.Net.WebProxy($"{proxy.Host}:{proxy.Port}");

            if (!string.IsNullOrEmpty(proxy.Username) && !string.IsNullOrEmpty(proxy.Password))
            {
                _httpClientHandler.Proxy.Credentials = new System.Net.NetworkCredential(proxy.Username, proxy.Password);
            }
        }

        private void ApplyAntiDetectTechniques()
        {
            // Random user agent;
            if (Configuration.AntiDetectTechniques.HasFlag(AntiDetectTechnique.RandomUserAgent))
            {
                var userAgents = new[]
                {
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15"
                };

                var randomAgent = userAgents[_random.Next(userAgents.Length)];
                _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(randomAgent);
            }

            // Random delay;
            if (Configuration.AntiDetectTechniques.HasFlag(AntiDetectTechnique.RandomDelay))
            {
                var delay = _random.Next(100, 3000);
                Task.Delay(delay).Wait();
            }

            // Random headers;
            if (Configuration.AntiDetectTechniques.HasFlag(AntiDetectTechnique.RandomHeaders))
            {
                // Rastgele headers ekle;
            }
        }

        private Dictionary<string, string> GetCookies(string url)
        {
            var cookies = _cookieContainer.GetCookies(new Uri(url));
            return cookies.Cast<System.Net.Cookie>().ToDictionary(c => c.Name, c => c.Value);
        }

        private bool IsAllowedByRobotsTxt(Uri uri)
        {
            // robots.txt kontrolü;
            // Bu örnekte basit implementasyon;
            // Gerçek uygulamada robots.txt parser kullanılmalı;

            if (!Configuration.RespectRobotsTxt)
            {
                return true;
            }

            // Domain bazlı kontrol;
            var domain = uri.Host;

            // Örnek: bazı domain'leri engelle;
            var blockedDomains = new[] { "example.com", "test.com" };
            if (blockedDomains.Any(d => domain.Contains(d)))
            {
                return false;
            }

            return true;
        }

        private ExtractedData ExtractDataFromContent(string content, string contentType, ExtractionRules rules)
        {
            var extractedData = new ExtractedData;
            {
                SourceUrl = rules.SourceUrl,
                ExtractionTime = DateTime.UtcNow,
                Data = new Dictionary<string, object>()
            };

            try
            {
                if (contentType.Contains("html"))
                {
                    // HTML parsing;
                    var htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(content);

                    foreach (var rule in rules.Rules)
                    {
                        var value = ExtractFromHtml(htmlDoc, rule);
                        extractedData.Data[rule.FieldName] = value;
                    }
                }
                else if (contentType.Contains("json"))
                {
                    // JSON parsing;
                    var json = JToken.Parse(content);

                    foreach (var rule in rules.Rules)
                    {
                        var value = ExtractFromJson(json, rule);
                        extractedData.Data[rule.FieldName] = value;
                    }
                }
                else if (contentType.Contains("xml"))
                {
                    // XML parsing;
                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(content);

                    foreach (var rule in rules.Rules)
                    {
                        var value = ExtractFromXml(xmlDoc, rule);
                        extractedData.Data[rule.FieldName] = value;
                    }
                }

                extractedData.Success = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract data from content");
                extractedData.Success = false;
                extractedData.ErrorMessage = ex.Message;
            }

            return extractedData;
        }

        private object ExtractFromHtml(HtmlDocument htmlDoc, ExtractionRule rule)
        {
            try
            {
                if (rule.SelectorType == SelectorType.CssSelector)
                {
                    var nodes = htmlDoc.DocumentNode.SelectNodes(rule.Selector);
                    if (nodes == null || nodes.Count == 0)
                    {
                        return rule.DefaultValue;
                    }

                    if (rule.ExtractionType == ExtractionType.Text)
                    {
                        return nodes.First().InnerText.Trim();
                    }
                    else if (rule.ExtractionType == ExtractionType.Html)
                    {
                        return nodes.First().InnerHtml;
                    }
                    else if (rule.ExtractionType == ExtractionType.Attribute)
                    {
                        return nodes.First().GetAttributeValue(rule.AttributeName, rule.DefaultValue?.ToString());
                    }
                    else if (rule.ExtractionType == ExtractionType.Multiple)
                    {
                        return nodes.Select(n => n.InnerText.Trim()).ToList();
                    }
                }
                else if (rule.SelectorType == SelectorType.XPath)
                {
                    var nodes = htmlDoc.DocumentNode.SelectNodes(rule.Selector);
                    if (nodes == null || nodes.Count == 0)
                    {
                        return rule.DefaultValue;
                    }

                    return nodes.First().InnerText.Trim();
                }

                return rule.DefaultValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to extract data with selector: {rule.Selector}");
                return rule.DefaultValue;
            }
        }

        private object ExtractFromJson(JToken json, ExtractionRule rule)
        {
            try
            {
                var token = json.SelectToken(rule.Selector);
                if (token == null)
                {
                    return rule.DefaultValue;
                }

                return token.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to extract data from JSON with selector: {rule.Selector}");
                return rule.DefaultValue;
            }
        }

        private object ExtractFromXml(XmlDocument xmlDoc, ExtractionRule rule)
        {
            try
            {
                var nodes = xmlDoc.SelectNodes(rule.Selector);
                if (nodes == null || nodes.Count == 0)
                {
                    return rule.DefaultValue;
                }

                if (rule.ExtractionType == ExtractionType.Text)
                {
                    return nodes[0].InnerText.Trim();
                }
                else if (rule.ExtractionType == ExtractionType.Multiple)
                {
                    var values = new List<string>();
                    foreach (XmlNode node in nodes)
                    {
                        values.Add(node.InnerText.Trim());
                    }
                    return values;
                }

                return rule.DefaultValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to extract data from XML with selector: {rule.Selector}");
                return rule.DefaultValue;
            }
        }

        private List<ExtractedLink> ExtractLinksFromContent(string content, string contentType, string baseUrl, LinkExtractionOptions options)
        {
            var links = new List<ExtractedLink>();

            try
            {
                if (contentType.Contains("html"))
                {
                    var htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(content);

                    var linkNodes = htmlDoc.DocumentNode.SelectNodes("//a[@href]");
                    if (linkNodes != null)
                    {
                        foreach (var node in linkNodes)
                        {
                            var href = node.GetAttributeValue("href", "");
                            var text = node.InnerText.Trim();

                            if (!string.IsNullOrEmpty(href))
                            {
                                // Relative URL'leri absolute yap;
                                var absoluteUrl = MakeAbsoluteUrl(href, baseUrl);

                                links.Add(new ExtractedLink;
                                {
                                    Url = absoluteUrl,
                                    Text = text,
                                    Title = node.GetAttributeValue("title", ""),
                                    Rel = node.GetAttributeValue("rel", ""),
                                    Target = node.GetAttributeValue("target", "")
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract links from content");
            }

            return links;
        }

        private List<ExtractedImage> ExtractImagesFromContent(string content, string contentType, string baseUrl, ImageExtractionOptions options)
        {
            var images = new List<ExtractedImage>();

            try
            {
                if (contentType.Contains("html"))
                {
                    var htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(content);

                    var imgNodes = htmlDoc.DocumentNode.SelectNodes("//img[@src]");
                    if (imgNodes != null)
                    {
                        foreach (var node in imgNodes)
                        {
                            var src = node.GetAttributeValue("src", "");
                            var alt = node.GetAttributeValue("alt", "");
                            var width = node.GetAttributeValue("width", "");
                            var height = node.GetAttributeValue("height", "");

                            if (!string.IsNullOrEmpty(src))
                            {
                                // Relative URL'leri absolute yap;
                                var absoluteUrl = MakeAbsoluteUrl(src, baseUrl);

                                images.Add(new ExtractedImage;
                                {
                                    Url = absoluteUrl,
                                    AltText = alt,
                                    Width = width,
                                    Height = height,
                                    Title = node.GetAttributeValue("title", ""),
                                    SourceUrl = baseUrl;
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract images from content");
            }

            return images;
        }

        private string MakeAbsoluteUrl(string url, string baseUrl)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }

            if (Uri.TryCreate(new Uri(baseUrl), url, out var combinedUri))
            {
                return combinedUri.ToString();
            }

            return url;
        }

        private List<ExtractedLink> FilterLinks(List<ExtractedLink> links, LinkFilters filters)
        {
            var filteredLinks = links.AsEnumerable();

            if (!string.IsNullOrEmpty(filters.Domain))
            {
                filteredLinks = filteredLinks.Where(l => l.Url.Contains(filters.Domain));
            }

            if (!string.IsNullOrEmpty(filters.ContainsText))
            {
                filteredLinks = filteredLinks.Where(l =>
                    l.Text.Contains(filters.ContainsText, StringComparison.OrdinalIgnoreCase) ||
                    l.Url.Contains(filters.ContainsText, StringComparison.OrdinalIgnoreCase));
            }

            if (filters.FileTypes != null && filters.FileTypes.Count > 0)
            {
                filteredLinks = filteredLinks.Where(l =>
                    filters.FileTypes.Any(ft => l.Url.EndsWith(ft, StringComparison.OrdinalIgnoreCase)));
            }

            if (filters.ExcludePatterns != null && filters.ExcludePatterns.Count > 0)
            {
                foreach (var pattern in filters.ExcludePatterns)
                {
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                    filteredLinks = filteredLinks.Where(l => !regex.IsMatch(l.Url));
                }
            }

            return filteredLinks.ToList();
        }

        private List<ExtractedImage> FilterImages(List<ExtractedImage> images, ImageFilters filters)
        {
            var filteredImages = images.AsEnumerable();

            if (filters.MinWidth > 0)
            {
                filteredImages = filteredImages.Where(i =>
                    int.TryParse(i.Width, out var width) && width >= filters.MinWidth);
            }

            if (filters.MinHeight > 0)
            {
                filteredImages = filteredImages.Where(i =>
                    int.TryParse(i.Height, out var height) && height >= filters.MinHeight);
            }

            if (!string.IsNullOrEmpty(filters.ContainsText))
            {
                filteredImages = filteredImages.Where(i =>
                    i.AltText.Contains(filters.ContainsText, StringComparison.OrdinalIgnoreCase) ||
                    i.Title.Contains(filters.ContainsText, StringComparison.OrdinalIgnoreCase));
            }

            if (filters.FileTypes != null && filters.FileTypes.Count > 0)
            {
                filteredImages = filteredImages.Where(i =>
                    filters.FileTypes.Any(ft => i.Url.EndsWith(ft, StringComparison.OrdinalIgnoreCase)));
            }

            return filteredImages.ToList();
        }

        private List<ExtractedLink> SortLinks(List<ExtractedLink> links, LinkSortOption sortBy)
        {
            return sortBy switch;
            {
                LinkSortOption.Url => links.OrderBy(l => l.Url).ToList(),
                LinkSortOption.Text => links.OrderBy(l => l.Text).ToList(),
                LinkSortOption.Domain => links.OrderBy(l => new Uri(l.Url).Host).ToList(),
                LinkSortOption.Length => links.OrderBy(l => l.Url.Length).ToList(),
                _ => links;
            };
        }

        private async Task DownloadImagesAsync(List<ExtractedImage> images, string downloadPath, CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(downloadPath))
                {
                    Directory.CreateDirectory(downloadPath);
                }

                foreach (var image in images)
                {
                    try
                    {
                        var imageBytes = await _httpClient.GetByteArrayAsync(image.Url, cancellationToken);

                        var fileName = Path.GetFileName(new Uri(image.Url).LocalPath);
                        if (string.IsNullOrEmpty(fileName))
                        {
                            fileName = $"image_{Guid.NewGuid()}.jpg";
                        }

                        var filePath = Path.Combine(downloadPath, fileName);
                        await File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken);

                        image.LocalPath = filePath;
                        image.Downloaded = true;

                        _logger.LogDebug($"Downloaded image: {image.Url} -> {filePath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to download image: {image.Url}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download images");
            }
        }

        private async Task ExecuteDynamicActionAsync(DynamicAction action, CancellationToken cancellationToken)
        {
            // Dinamik action'ları çalıştır;
            // Bu örnekte simüle ediyoruz;
            // Gerçek uygulamada Selenium/Puppeteer gibi araçlar kullanılmalı;

            _logger.LogDebug($"Executing dynamic action: {action.ActionType}");

            switch (action.ActionType)
            {
                case ActionType.Click:
                    await Task.Delay(1000, cancellationToken);
                    break;

                case ActionType.Scroll:
                    await Task.Delay(500, cancellationToken);
                    break;

                case ActionType.Wait:
                    await Task.Delay(action.WaitTime, cancellationToken);
                    break;

                case ActionType.Type:
                    await Task.Delay(500, cancellationToken);
                    break;
            }
        }

        private async Task SaveSessionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var sessionsFile = "sessions/sessions.json";
                var directory = Path.GetDirectoryName(sessionsFile);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Dictionary<string, SessionData> sessionsCopy;

                lock (_sessionLock)
                {
                    sessionsCopy = new Dictionary<string, SessionData>(_sessions);
                }

                var json = JsonConvert.SerializeObject(sessionsCopy, Formatting.Indented);
                await File.WriteAllTextAsync(sessionsFile, json, cancellationToken);

                _logger.LogInformation($"Saved {sessionsCopy.Count} sessions");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save sessions");
            }
        }

        private void UpdateStatistics(long bytesDownloaded)
        {
            TotalBytesDownloaded += bytesDownloaded;
            TotalRequestsProcessed++;
        }

        private double CalculateCacheHitRate()
        {
            // Cache hit rate hesaplama;
            // Gerçek uygulamada cache tracking gerekir;
            return 0.75; // Örnek değer;
        }

        private TimeSpan CalculateAverageResponseTime()
        {
            // Ortalama response time hesaplama;
            // Gerçek uygulamada timing verisi toplanmalı;
            return TimeSpan.FromMilliseconds(1500); // Örnek değer;
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
                    // Kaynakları serbest bırak;
                    _scrapingCancellationTokenSource?.Cancel();
                    _scrapingCancellationTokenSource?.Dispose();
                    _httpClient?.Dispose();
                    _httpClientHandler?.Dispose();
                    ClearQueue();
                    ClearSessions();

                    Status = WebScraperStatus.Disposed;

                    _logger.LogInformation("Web Scraper disposed");
                }

                _isDisposed = true;
            }
        }

        ~WebScraper()
        {
            Dispose(false);
        }
        #endregion;

        #region Supporting Classes and Enums;
        public enum WebScraperStatus;
        {
            Stopped,
            Initializing,
            Running,
            Stopping,
            Error,
            Disposed;
        }

        public enum SelectorType;
        {
            CssSelector,
            XPath,
            Regex,
            JsonPath;
        }

        public enum ExtractionType;
        {
            Text,
            Html,
            Attribute,
            Multiple;
        }

        public enum AntiDetectTechnique;
        {
            None = 0,
            RandomUserAgent = 1,
            RandomDelay = 2,
            RandomHeaders = 4,
            ProxyRotation = 8,
            All = RandomUserAgent | RandomDelay | RandomHeaders | ProxyRotation;
        }

        public enum LinkSortOption;
        {
            None,
            Url,
            Text,
            Domain,
            Length;
        }

        public enum ActionType;
        {
            Click,
            Scroll,
            Wait,
            Type,
            Navigate;
        }

        public enum LogLevel;
        {
            Debug,
            Info,
            Warning,
            Error;
        }

        public class ScraperConfiguration;
        {
            public int MaxConcurrentRequests { get; set; }
            public TimeSpan RequestTimeout { get; set; }
            public int RetryCount { get; set; }
            public TimeSpan RetryDelay { get; set; }
            public TimeSpan RateLimit { get; set; }
            public bool RespectRobotsTxt { get; set; }
            public bool FollowRedirects { get; set; }
            public int MaxRedirects { get; set; }
            public bool EnableCaching { get; set; }
            public TimeSpan CacheDuration { get; set; }
            public long MaxCacheSize { get; set; }
            public string UserAgent { get; set; }
            public Dictionary<string, string> DefaultHeaders { get; set; }
            public bool EnableProxyRotation { get; set; }
            public TimeSpan ProxyRotationInterval { get; set; }
            public bool EnableSessionManagement { get; set; }
            public int MaxSessions { get; set; }
            public bool EnableAntiDetect { get; set; }
            public AntiDetectTechnique AntiDetectTechniques { get; set; }
            public LogLevel LogLevel { get; set; }
        }

        public class ScrapeOptions;
        {
            public static ScrapeOptions Default => new ScrapeOptions();

            public HttpMethod Method { get; set; } = HttpMethod.Get;
            public Dictionary<string, string> Headers { get; set; }
            public CookieContainer Cookies { get; set; }
            public string Body { get; set; }
            public string ContentType { get; set; } = "application/x-www-form-urlencoded";
            public bool UseCache { get; set; } = true;
            public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
            public bool RespectRobotsTxt { get; set; } = true;
            public int RetryCount { get; set; } = 3;
            public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
            public TimeSpan RateLimit { get; set; } = TimeSpan.Zero;
            public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
            public bool UseProxy { get; set; } = false;
            public bool UseAntiDetect { get; set; } = false;
            public bool UseQueue { get; set; } = false;
            public string SessionId { get; set; }
        }

        public class BatchScrapeOptions;
        {
            public static BatchScrapeOptions Default => new BatchScrapeOptions();

            public bool ParallelProcessing { get; set; } = true;
            public int MaxConcurrentRequests { get; set; } = 5;
            public TimeSpan DelayBetweenRequests { get; set; } = TimeSpan.FromMilliseconds(500);
            public ScrapeOptions ScrapeOptions { get; set; } = ScrapeOptions.Default;
            public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
        }

        public class ExtractionRule;
        {
            public string FieldName { get; set; }
            public SelectorType SelectorType { get; set; }
            public string Selector { get; set; }
            public ExtractionType ExtractionType { get; set; }
            public string AttributeName { get; set; }
            public object DefaultValue { get; set; }
            public bool Required { get; set; }
            public string ValidationPattern { get; set; }
        }

        public class ExtractionRules;
        {
            public string SourceUrl { get; set; }
            public string RuleSetName { get; set; }
            public List<ExtractionRule> Rules { get; set; } = new List<ExtractionRule>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        public class ExtractedData;
        {
            public string SourceUrl { get; set; }
            public DateTime ExtractionTime { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public Dictionary<string, object> Data { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        public class ProxyServer;
        {
            public string Id { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public ProxyType Type { get; set; }
            public int UsageCount { get; set; }
            public DateTime LastUsed { get; set; }
            public bool IsActive { get; set; } = true;

            public bool IsValid()
            {
                return !string.IsNullOrWhiteSpace(Host) && Port > 0 && Port <= 65535;
            }
        }

        public enum ProxyType;
        {
            HTTP,
            HTTPS,
            SOCKS4,
            SOCKS5;
        }

        public class SessionData;
        {
            public string SessionId { get; set; }
            public SessionConfiguration Configuration { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastAccessed { get; set; }
            public CookieContainer Cookies { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public List<RequestHistory> RequestHistory { get; set; }
        }

        public class SessionConfiguration;
        {
            public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(1);
            public int MaxHistorySize { get; set; } = 1000;
            public bool PersistCookies { get; set; } = true;
            public bool AutoRenew { get; set; } = false;
        }

        public class RequestHistory;
        {
            public string Url { get; set; }
            public DateTime Timestamp { get; set; }
            public bool Success { get; set; }
            public int StatusCode { get; set; }
            public long ResponseSize { get; set; }
        }

        public class ScraperMetrics;
        {
            public string ScraperId { get; set; }
            public WebScraperStatus Status { get; set; }
            public int QueueLength { get; set; }
            public int ActiveRequests { get; set; }
            public int TotalRequestsProcessed { get; set; }
            public int TotalDataExtracted { get; set; }
            public long TotalBytesDownloaded { get; set; }
            public int ActiveSessions { get; set; }
            public int ProxyCount { get; set; }
            public double CacheHitRate { get; set; }
            public TimeSpan AverageResponseTime { get; set; }
            public TimeSpan Uptime { get; set; }
            public DateTime Timestamp { get; set; }
        }
        #endregion;
    }

    #region Interfaces;
    public interface IWebScraper : IDisposable
    {
        Task<OperationResult> InitializeAsync(ScraperConfiguration configuration = null, CancellationToken cancellationToken = default);
        Task<OperationResult> ShutdownAsync(CancellationToken cancellationToken = default);
        Task<ScrapeResult> ScrapeAsync(string url, ScrapeOptions options = null, CancellationToken cancellationToken = default);
        Task<BatchScrapeResult> ScrapeBatchAsync(List<string> urls, BatchScrapeOptions options = null, CancellationToken cancellationToken = default);
        Task<ExtractionResult> ExtractDataAsync(string url, ExtractionRules rules, CancellationToken cancellationToken = default);
        Task<LinkExtractionResult> ExtractLinksAsync(string url, LinkExtractionOptions options = null, CancellationToken cancellationToken = default);
        Task<ImageExtractionResult> ExtractImagesAsync(string url, ImageExtractionOptions options = null, CancellationToken cancellationToken = default);
        Task<DynamicScrapeResult> ScrapeDynamicContentAsync(string url, DynamicScrapeOptions options = null, CancellationToken cancellationToken = default);
        OperationResult CreateSession(string sessionId, SessionConfiguration configuration = null);
        Task<ScrapeResult> ScrapeWithSessionAsync(string sessionId, string url, ScrapeOptions options = null, CancellationToken cancellationToken = default);
        OperationResult AddProxyServer(ProxyServer proxy);
        ProxyServer RotateProxy();
        ScraperMetrics GetMetrics();
        void ClearQueue();
        void ClearSessions();
        void ClearCache();

        event EventHandler<ScrapingCompletedEventArgs> OnScrapingCompleted;
        event EventHandler<DataExtractedEventArgs> OnDataExtracted;
        event EventHandler<ScrapingErrorEventArgs> OnScrapingError;
    }
    #endregion;
}
