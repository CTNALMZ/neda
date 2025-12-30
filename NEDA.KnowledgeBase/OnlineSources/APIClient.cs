using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.ErrorReporting;
using NEDA.Core.Logging;
using NEDA.Core.Security.Encryption;

namespace NEDA.KnowledgeBase.OnlineSources;
{
    /// <summary>
    /// HTTP API istemcisi - Çeşitli online kaynaklara bağlanmak için genel amaçlı API istemcisi;
    /// </summary>
    public class APIClient : IAPIClient, IDisposable;
    {
        #region Private Fields;
        private readonly HttpClient _httpClient;
        private readonly ILogger<APIClient> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly AppConfig _appConfig;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _disposed;
        #endregion;

        #region Properties;
        /// <summary>
        /// Varsayılan timeout süresi (milisaniye)
        /// </summary>
        public int DefaultTimeout { get; set; } = 30000;

        /// <summary>
        /// Maksimum yeniden deneme sayısı;
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Yeniden deneme bekleme süresi (milisaniye)
        /// </summary>
        public int RetryDelay { get; set; } = 1000;

        /// <summary>
        /// API anahtarı (şifreli olarak saklanır)
        /// </summary>
        public string ApiKey;
        {
            get => _cryptoEngine.Decrypt(_encryptedApiKey);
            set => _encryptedApiKey = _cryptoEngine.Encrypt(value);
        }
        private string _encryptedApiKey;

        /// <summary>
        /// Temel URL;
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// Kimlik doğrulama token'ı;
        /// </summary>
        public string AuthToken { get; set; }
        #endregion;

        #region Events;
        /// <summary>
        /// API isteği tamamlandığında tetiklenen olay;
        /// </summary>
        public event EventHandler<ApiRequestCompletedEventArgs> RequestCompleted;

        /// <summary>
        /// API hatası oluştuğunda tetiklenen olay;
        /// </summary>
        public event EventHandler<ApiErrorEventArgs> RequestFailed;
        #endregion;

        #region Constructors;
        /// <summary>
        /// APIClient sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="errorReporter">Hata raporlama servisi</param>
        /// <param name="cryptoEngine">Şifreleme motoru</param>
        /// <param name="appConfig">Uygulama konfigürasyonu</param>
        public APIClient(
            ILogger<APIClient> logger,
            IErrorReporter errorReporter,
            ICryptoEngine cryptoEngine,
            AppConfig appConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            // HttpClient oluştur;
            _httpClient = CreateHttpClient();

            // JSON serileştirme ayarları;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            };

            InitializeFromConfig();
        }

        /// <summary>
        /// Belirli bir HttpClient ile APIClient sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public APIClient(
            HttpClient httpClient,
            ILogger<APIClient> logger,
            IErrorReporter errorReporter,
            ICryptoEngine cryptoEngine,
            AppConfig appConfig)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            };

            InitializeFromConfig();
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// GET isteği gönderir;
        /// </summary>
        /// <typeparam name="T">Dönüş tipi</typeparam>
        /// <param name="endpoint">Endpoint URL (BaseUrl'e eklenir)</param>
        /// <param name="headers">Ek HTTP başlıkları</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>API yanıtı</returns>
        public async Task<ApiResponse<T>> GetAsync<T>(
            string endpoint,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRetryAsync<T>(
                async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(endpoint));
                    AddHeaders(request, headers);

                    _logger.LogDebug("GET isteği gönderiliyor: {Url}", request.RequestUri);

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    return await ProcessResponseAsync<T>(response, cancellationToken);
                },
                $"GET {endpoint}",
                cancellationToken);
        }

        /// <summary>
        /// POST isteği gönderir;
        /// </summary>
        /// <typeparam name="T">Dönüş tipi</typeparam>
        /// <param name="endpoint">Endpoint URL</param>
        /// <param name="content">Gönderilecek içerik</param>
        /// <param name="headers">Ek HTTP başlıkları</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>API yanıtı</returns>
        public async Task<ApiResponse<T>> PostAsync<T>(
            string endpoint,
            object content = null,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRetryAsync<T>(
                async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(endpoint));
                    AddHeaders(request, headers);

                    if (content != null)
                    {
                        var jsonContent = JsonSerializer.Serialize(content, _jsonOptions);
                        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                        _logger.LogDebug("POST isteği gönderiliyor: {Url} - İçerik: {Content}",
                            request.RequestUri, jsonContent);
                    }
                    else;
                    {
                        _logger.LogDebug("POST isteği gönderiliyor: {Url}", request.RequestUri);
                    }

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    return await ProcessResponseAsync<T>(response, cancellationToken);
                },
                $"POST {endpoint}",
                cancellationToken);
        }

        /// <summary>
        /// PUT isteği gönderir;
        /// </summary>
        public async Task<ApiResponse<T>> PutAsync<T>(
            string endpoint,
            object content = null,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRetryAsync<T>(
                async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, BuildUrl(endpoint));
                    AddHeaders(request, headers);

                    if (content != null)
                    {
                        var jsonContent = JsonSerializer.Serialize(content, _jsonOptions);
                        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                    }

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    return await ProcessResponseAsync<T>(response, cancellationToken);
                },
                $"PUT {endpoint}",
                cancellationToken);
        }

        /// <summary>
        /// DELETE isteği gönderir;
        /// </summary>
        public async Task<ApiResponse<T>> DeleteAsync<T>(
            string endpoint,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRetryAsync<T>(
                async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUrl(endpoint));
                    AddHeaders(request, headers);

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    return await ProcessResponseAsync<T>(response, cancellationToken);
                },
                $"DELETE {endpoint}",
                cancellationToken);
        }

        /// <summary>
        /// Çok parçalı form verisi gönderir (file upload için)
        /// </summary>
        public async Task<ApiResponse<T>> PostMultipartAsync<T>(
            string endpoint,
            MultipartFormDataContent content,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRetryAsync<T>(
                async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(endpoint));
                    AddHeaders(request, headers);
                    request.Content = content;

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    return await ProcessResponseAsync<T>(response, cancellationToken);
                },
                $"POST Multipart {endpoint}",
                cancellationToken);
        }

        /// <summary>
        /// Temel URL'i değiştirir;
        /// </summary>
        public void SetBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Base URL boş olamaz", nameof(baseUrl));

            BaseUrl = baseUrl.TrimEnd('/');
            _httpClient.BaseAddress = new Uri(BaseUrl);

            _logger.LogInformation("Base URL güncellendi: {BaseUrl}", BaseUrl);
        }

        /// <summary>
        /// Kimlik doğrulama token'ını ayarlar;
        /// </summary>
        public void SetAuthToken(string token, string scheme = "Bearer")
        {
            AuthToken = token;
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(scheme, token);

            _logger.LogDebug("Kimlik doğrulama token'ı ayarlandı: {Scheme} token", scheme);
        }

        /// <summary>
        /// API anahtarını başlığa ekler;
        /// </summary>
        public void AddApiKeyHeader(string headerName = "X-API-Key")
        {
            if (!string.IsNullOrEmpty(ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Add(headerName, ApiKey);
                _logger.LogDebug("API anahtarı başlığa eklendi: {HeaderName}", headerName);
            }
        }

        /// <summary>
        /// Tüm özel başlıkları temizler;
        /// </summary>
        public void ClearCustomHeaders()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _logger.LogDebug("Özel HTTP başlıkları temizlendi");
        }
        #endregion;

        #region Private Methods;
        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler;
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5;
            };

            // Geliştirme ortamında SSL doğrulamasını atla (opsiyonel)
            if (_appConfig.Environment == "Development")
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(DefaultTimeout)
            };

            // Varsayılan başlıklar;
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"NEDA-Client/{typeof(APIClient).Assembly.GetName().Version}");

            return client;
        }

        private void InitializeFromConfig()
        {
            if (_appConfig.ApiSettings != null)
            {
                BaseUrl = _appConfig.ApiSettings.BaseUrl;
                DefaultTimeout = _appConfig.ApiSettings.Timeout;
                MaxRetries = _appConfig.ApiSettings.MaxRetries;
                RetryDelay = _appConfig.ApiSettings.RetryDelay;

                if (!string.IsNullOrEmpty(_appConfig.ApiSettings.ApiKey))
                {
                    ApiKey = _appConfig.ApiSettings.ApiKey;
                }

                if (!string.IsNullOrEmpty(BaseUrl))
                {
                    _httpClient.BaseAddress = new Uri(BaseUrl);
                }

                _logger.LogInformation("APIClient konfigürasyondan başlatıldı: BaseUrl={BaseUrl}, Timeout={Timeout}",
                    BaseUrl, DefaultTimeout);
            }
        }

        private string BuildUrl(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
                return BaseUrl;

            // Eğer endpoint tam URL ise, BaseUrl'i kullanma;
            if (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute))
                return endpoint;

            // Aksi halde BaseUrl'e ekle;
            return $"{BaseUrl}/{endpoint.TrimStart('/')}";
        }

        private void AddHeaders(HttpRequestMessage request, Dictionary<string, string> headers)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }
            }
        }

        private async Task<ApiResponse<T>> ExecuteWithRetryAsync<T>(
            Func<Task<ApiResponse<T>>> operation,
            string operationName,
            CancellationToken cancellationToken)
        {
            int attempt = 0;
            ApiResponse<T> lastResponse = null;
            Exception lastException = null;

            while (attempt <= MaxRetries)
            {
                attempt++;
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _logger.LogDebug("{Operation} - Deneme {Attempt}/{MaxRetries}",
                        operationName, attempt, MaxRetries);

                    lastResponse = await operation();

                    // Başarılı ise döndür;
                    if (lastResponse.IsSuccess)
                    {
                        OnRequestCompleted(new ApiRequestCompletedEventArgs;
                        {
                            Operation = operationName,
                            Attempts = attempt,
                            Success = true,
                            ResponseCode = lastResponse.StatusCode;
                        });

                        return lastResponse;
                    }

                    // HTTP 429 (Too Many Requests) veya 5xx hataları için yeniden dene;
                    if (ShouldRetry(lastResponse.StatusCode))
                    {
                        _logger.LogWarning("{Operation} - Yeniden deneniyor. Status: {StatusCode}",
                            operationName, lastResponse.StatusCode);

                        if (attempt < MaxRetries)
                        {
                            await Task.Delay(CalculateRetryDelay(attempt), cancellationToken);
                            continue;
                        }
                    }

                    // Yeniden deneme yapılmayacak hata;
                    OnRequestFailed(new ApiErrorEventArgs;
                    {
                        Operation = operationName,
                        StatusCode = lastResponse.StatusCode,
                        ErrorMessage = lastResponse.ErrorMessage,
                        Attempts = attempt;
                    });

                    return lastResponse;
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "{Operation} - HTTP istek hatası (Deneme {Attempt})",
                        operationName, attempt);

                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(CalculateRetryDelay(attempt), cancellationToken);
                        continue;
                    }
                }
                catch (TaskCanceledException ex)
                {
                    lastException = ex;
                    _logger.LogError(ex, "{Operation} - İstek zaman aşımına uğradı", operationName);
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogError(ex, "{Operation} - Beklenmeyen hata", operationName);

                    // Beklenmeyen hatalar için hata raporla;
                    await _errorReporter.ReportErrorAsync(ex,
                        $"API İşlemi Hatası: {operationName}");

                    if (attempt < MaxRetries && IsTransientError(ex))
                    {
                        await Task.Delay(CalculateRetryDelay(attempt), cancellationToken);
                        continue;
                    }
                    throw;
                }
            }

            // Tüm denemeler başarısız oldu;
            OnRequestFailed(new ApiErrorEventArgs;
            {
                Operation = operationName,
                StatusCode = lastResponse?.StatusCode ?? 0,
                ErrorMessage = lastResponse?.ErrorMessage ?? lastException?.Message,
                Exception = lastException,
                Attempts = attempt;
            });

            return lastResponse ?? new ApiResponse<T>
            {
                IsSuccess = false,
                StatusCode = 0,
                ErrorMessage = "Tüm yeniden denemeler başarısız oldu",
                Exception = lastException;
            };
        }

        private async Task<ApiResponse<T>> ProcessResponseAsync<T>(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            var apiResponse = new ApiResponse<T>
            {
                StatusCode = (int)response.StatusCode,
                IsSuccess = response.IsSuccessStatusCode,
                Headers = new Dictionary<string, string>()
            };

            try
            {
                // Yanıt başlıklarını kaydet;
                foreach (var header in response.Headers)
                {
                    apiResponse.Headers[header.Key] = string.Join(", ", header.Value);
                }

                var contentString = await response.Content.ReadAsStringAsync(cancellationToken);
                apiResponse.RawContent = contentString;

                if (response.IsSuccessStatusCode)
                {
                    if (!string.IsNullOrEmpty(contentString))
                    {
                        try
                        {
                            // T tipine deserialize et;
                            if (typeof(T) == typeof(string))
                            {
                                apiResponse.Data = (T)(object)contentString;
                            }
                            else;
                            {
                                apiResponse.Data = JsonSerializer.Deserialize<T>(
                                    contentString, _jsonOptions);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "JSON deserialize hatası: {Content}", contentString);
                            apiResponse.IsSuccess = false;
                            apiResponse.ErrorMessage = $"JSON deserialize hatası: {ex.Message}";
                            apiResponse.Exception = ex;
                        }
                    }
                }
                else;
                {
                    apiResponse.ErrorMessage = $"HTTP {(int)response.StatusCode} - {response.ReasonPhrase}";

                    // Hata yanıtını parse etmeye çalış;
                    if (!string.IsNullOrEmpty(contentString))
                    {
                        try
                        {
                            apiResponse.ErrorResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                contentString, _jsonOptions);
                        }
                        catch { /* Ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Yanıt işleme hatası");
                apiResponse.IsSuccess = false;
                apiResponse.ErrorMessage = $"Yanıt işleme hatası: {ex.Message}";
                apiResponse.Exception = ex;
            }

            return apiResponse;
        }

        private bool ShouldRetry(int statusCode)
        {
            return statusCode == 429 || // Too Many Requests;
                   statusCode >= 500;   // Server Errors;
        }

        private int CalculateRetryDelay(int attempt)
        {
            // Exponential backoff with jitter;
            var delay = RetryDelay * (int)Math.Pow(2, attempt - 1);
            var jitter = new Random().Next(0, 100);
            return delay + jitter;
        }

        private bool IsTransientError(Exception ex)
        {
            return ex is HttpRequestException ||
                   ex is TimeoutException ||
                   (ex.InnerException != null && IsTransientError(ex.InnerException));
        }

        private void OnRequestCompleted(ApiRequestCompletedEventArgs e)
        {
            RequestCompleted?.Invoke(this, e);
        }

        private void OnRequestFailed(ApiErrorEventArgs e)
        {
            RequestFailed?.Invoke(this, e);
        }
        #endregion;

        #region IDisposable Implementation;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion;
    }

    #region Supporting Types;
    /// <summary>
    /// API istemcisi arayüzü;
    /// </summary>
    public interface IAPIClient;
    {
        Task<ApiResponse<T>> GetAsync<T>(
            string endpoint,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default);

        Task<ApiResponse<T>> PostAsync<T>(
            string endpoint,
            object content = null,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default);

        Task<ApiResponse<T>> PutAsync<T>(
            string endpoint,
            object content = null,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default);

        Task<ApiResponse<T>> DeleteAsync<T>(
            string endpoint,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default);

        Task<ApiResponse<T>> PostMultipartAsync<T>(
            string endpoint,
            MultipartFormDataContent content,
            Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default);

        void SetBaseUrl(string baseUrl);
        void SetAuthToken(string token, string scheme = "Bearer");
        void AddApiKeyHeader(string headerName = "X-API-Key");
        void ClearCustomHeaders();
    }

    /// <summary>
    /// API yanıtı sınıfı;
    /// </summary>
    /// <typeparam name="T">Veri tipi</typeparam>
    public class ApiResponse<T>
    {
        public bool IsSuccess { get; set; }
        public int StatusCode { get; set; }
        public T Data { get; set; }
        public string RawContent { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> ErrorResponse { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public Exception Exception { get; set; }
    }

    /// <summary>
    /// API isteği tamamlandı olayı argümanları;
    /// </summary>
    public class ApiRequestCompletedEventArgs : EventArgs;
    {
        public string Operation { get; set; }
        public bool Success { get; set; }
        public int ResponseCode { get; set; }
        public int Attempts { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// API hatası olayı argümanları;
    /// </summary>
    public class ApiErrorEventArgs : EventArgs;
    {
        public string Operation { get; set; }
        public int StatusCode { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public int Attempts { get; set; }
    }
    #endregion;
}
