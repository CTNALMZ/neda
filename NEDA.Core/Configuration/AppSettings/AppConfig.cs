using Microsoft.Extensions.Configuration;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.Core.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.Extensions.Logging;
using static NEDA.Core.Common.Constants;

namespace NEDA.Core.Configuration.AppSettings
{
    /// <summary>
    /// Main application configuration class that holds all configuration settings for NEDA;
    /// Implements IValidatableObject for configuration validation;
    /// </summary>
    public class AppConfig : IValidatableObject
    {
        #region Constructor

        /// <summary>
        /// Initializes a new instance of AppConfig with default values;
        /// </summary>
        public AppConfig()
        {
            // Initialize collections to prevent null reference exceptions;
            AIModels = new List<AIModelConfig>();
            DatabaseSettings = new DatabaseConfig();
            SecuritySettings = new SecurityConfig();
            LoggingSettings = new LoggingConfig();
            NetworkSettings = new NetworkConfig();
            PerformanceSettings = new PerformanceConfig();
            ExternalServices = new ExternalServicesConfig();
            ApplicationSettings = new ApplicationConfig();
            CacheSettings = new CacheConfig();
            MonitoringSettings = new MonitoringConfig();
        }

        #endregion

        #region Application Configuration

        /// <summary>
        /// Application specific settings;
        /// </summary>
        public ApplicationConfig ApplicationSettings { get; set; }

        /// <summary>
        /// Application configuration class;
        /// </summary>
        public class ApplicationConfig
        {
            [Required(ErrorMessage = "Application name is required")]
            [StringLength(100, MinimumLength = 1, ErrorMessage = "Application name must be between 1 and 100 characters")]
            public string Name { get; set; } = Constants.Application.Name;

            [Required(ErrorMessage = "Application version is required")]
            [RegularExpression(@"^\d+\.\d+\.\d+$", ErrorMessage = "Version must be in format X.Y.Z")]
            public string Version { get; set; } = Constants.Application.Version;

            [Required(ErrorMessage = "Environment is required")]
            public EnvironmentType Environment { get; set; } = EnvironmentType.Development;

            [Required(ErrorMessage = "Application mode is required")]
            public ApplicationMode Mode { get; set; } = ApplicationMode.Normal;

            public bool DebugMode { get; set; } = false;

            [Range(1, 65535, ErrorMessage = "API port must be between 1 and 65535")]
            public int ApiPort { get; set; } = Constants.Network.ApiPort;

            [Range(1, 65535, ErrorMessage = "gRPC port must be between 1 and 65535")]
            public int GrpcPort { get; set; } = Constants.Network.GrpcPort;

            [Url(ErrorMessage = "Base URL must be a valid URL")]
            public string BaseUrl { get; set; } = "https://localhost:5000";

            public string[] AllowedOrigins { get; set; } = { "http://localhost:3000", "https://localhost:3000" };

            [Range(1, 100, ErrorMessage = "Max request size must be between 1 and 100 MB")]
            public int MaxRequestSizeMB { get; set; } = 10;

            public bool EnableSwagger { get; set; } = true;

            public bool EnableHealthChecks { get; set; } = true;

            [Required(ErrorMessage = "Culture is required")]
            public string Culture { get; set; } = "en-US";

            [Required(ErrorMessage = "Time zone is required")]
            public string TimeZone { get; set; } = "UTC";

            public string DataDirectory { get; set; } = "Data";

            public string TempDirectory { get; set; } = "Temp";

            public string LogDirectory { get; set; } = "Logs";

            [Range(1, 365, ErrorMessage = "Data retention days must be between 1 and 365")]
            public int DataRetentionDays { get; set; } = 90;

            public bool EnableTelemetry { get; set; } = true;

            public bool EnableBackgroundServices { get; set; } = true;

            [Range(1, int.MaxValue, ErrorMessage = "Session timeout must be positive")]
            public int SessionTimeoutMinutes { get; set; } = Constants.Security.SessionTimeoutMinutes;

            public bool EnableCompression { get; set; } = true;

            public bool EnableCaching { get; set; } = true;

            public Dictionary<string, string> FeatureFlags { get; set; } = new Dictionary<string, string>();
        }

        #endregion

        #region AI Configuration

        /// <summary>
        /// List of AI model configurations;
        /// </summary>
        [Required(ErrorMessage = "At least one AI model configuration is required")]
        [MinLength(1, ErrorMessage = "At least one AI model must be configured")]
        public List<AIModelConfig> AIModels { get; set; }

        /// <summary>
        /// AI model configuration class;
        /// </summary>
        public class AIModelConfig
        {
            [Required(ErrorMessage = "Model name is required")]
            [StringLength(100, MinimumLength = 1, ErrorMessage = "Model name must be between 1 and 100 characters")]
            public string Name { get; set; }

            [Required(ErrorMessage = "Model type is required")]
            public ModelType Type { get; set; }

            [Required(ErrorMessage = "Model path is required")]
            [StringLength(500, MinimumLength = 1, ErrorMessage = "Model path must be between 1 and 500 characters")]
            public string Path { get; set; }

            [Range(0.00001, 0.1, ErrorMessage = "Learning rate must be between 0.00001 and 0.1")]
            public double LearningRate { get; set; } = Constants.Engine.DefaultLearningRate;

            [Range(1, 1000, ErrorMessage = "Training epochs must be between 1 and 1000")]
            public int TrainingEpochs { get; set; } = Constants.Engine.DefaultTrainingEpochs;

            [Range(1, 256, ErrorMessage = "Batch size must be between 1 and 256")]
            public int BatchSize { get; set; } = Constants.Engine.DefaultBatchSize;

            [Range(0.0, 1.0, ErrorMessage = "Confidence threshold must be between 0.0 and 1.0")]
            public double ConfidenceThreshold { get; set; } = Constants.Engine.ConfidenceThreshold;

            public bool IsEnabled { get; set; } = true;

            public bool IsRequired { get; set; } = true;

            public bool UseGPU { get; set; } = true;

            [Range(1, 300000, ErrorMessage = "Inference timeout must be between 1 and 300000 ms")]
            public int InferenceTimeoutMs { get; set; } = Constants.Engine.DefaultInferenceTimeout;

            public bool EnableCaching { get; set; } = true;

            [Range(0, 100, ErrorMessage = "Cache TTL must be between 0 and 100 minutes")]
            public int CacheTTLMinutes { get; set; } = 15;

            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

            public string[] SupportedLanguages { get; set; } = { "en", "es", "fr", "de", "it" };

            public bool AllowFineTuning { get; set; } = true;

            [Range(1, 100, ErrorMessage = "Max concurrent requests must be between 1 and 100")]
            public int MaxConcurrentRequests { get; set; } = 10;

            public string Version { get; set; } = "1.0.0";
        }

        /// <summary>
        /// Gets a specific AI model configuration by name;
        /// </summary>
        public AIModelConfig GetAIModel(string name)
        {
            return AIModels.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets all enabled AI models;
        /// </summary>
        public IEnumerable<AIModelConfig> GetEnabledAIModels()
        {
            return AIModels.Where(m => m.IsEnabled);
        }

        #endregion

        #region Database Configuration

        [Required(ErrorMessage = "Database configuration is required")]
        public DatabaseConfig DatabaseSettings { get; set; }

        public class DatabaseConfig
        {
            [Required(ErrorMessage = "Connection string is required")]
            [StringLength(1000, MinimumLength = 1, ErrorMessage = "Connection string must be between 1 and 1000 characters")]
            public string ConnectionString { get; set; }

            [Required(ErrorMessage = "Database provider is required")]
            public string Provider { get; set; } = "SQLServer";

            [Range(1, 300, ErrorMessage = "Command timeout must be between 1 and 300 seconds")]
            public int CommandTimeout { get; set; } = Constants.Database.DefaultCommandTimeout;

            [Range(1, 1000, ErrorMessage = "Max pool size must be between 1 and 1000")]
            public int MaxPoolSize { get; set; } = Constants.Database.MaxPoolSize;

            [Range(1, 100, ErrorMessage = "Min pool size must be between 1 and 100")]
            public int MinPoolSize { get; set; } = Constants.Database.MinPoolSize;

            public bool EnableRetryOnFailure { get; set; } = true;

            [Range(1, 10, ErrorMessage = "Max retry count must be between 1 and 10")]
            public int MaxRetryCount { get; set; } = 3;

            public bool EnableSensitiveDataLogging { get; set; } = false;

            public bool EnableDetailedErrors { get; set; } = false;

            public bool UseLazyLoading { get; set; } = false;

            [Range(1, 10000, ErrorMessage = "Batch size must be between 1 and 10000")]
            public int BatchSize { get; set; } = Constants.Database.BatchSize;

            public string Schema { get; set; } = Constants.Database.DefaultSchema;

            public bool EnableMigration { get; set; } = true;

            public bool EnableSeedData { get; set; } = true;

            public bool EnableAuditing { get; set; } = true;

            [Range(1, 365, ErrorMessage = "Audit retention days must be between 1 and 365")]
            public int AuditRetentionDays { get; set; } = 90;

            public bool EnableEncryption { get; set; } = true;

            public string EncryptionKey { get; set; }

            public bool EnableQueryTracking { get; set; } = true;

            [Range(0, 100, ErrorMessage = "Query tracking threshold must be between 0 and 100")]
            public int QueryTrackingThreshold { get; set; } = 50;

            public bool EnablePerformanceMonitoring { get; set; } = true;

            public Dictionary<string, string> ConnectionParameters { get; set; } = new Dictionary<string, string>();
        }

        #endregion

        #region Security Configuration

        [Required(ErrorMessage = "Security configuration is required")]
        public SecurityConfig SecuritySettings { get; set; }

        public class SecurityConfig
        {
            [Required(ErrorMessage = "JWT secret key is required")]
            [MinLength(32, ErrorMessage = "JWT secret must be at least 32 characters")]
            public string JwtSecret { get; set; }

            [Required(ErrorMessage = "Encryption key is required")]
            [MinLength(32, ErrorMessage = "Encryption key must be at least 32 characters")]
            public string EncryptionKey { get; set; }

            public bool RequireHttps { get; set; } = true;

            [Range(1, 24, ErrorMessage = "Token expiry must be between 1 and 24 hours")]
            public int TokenExpiryHours { get; set; } = Constants.Security.TokenExpiryHours;

            [Range(1, 90, ErrorMessage = "Refresh token expiry must be between 1 and 90 days")]
            public int RefreshTokenExpiryDays { get; set; } = Constants.Security.RefreshTokenExpiryDays;

            [Range(1, 100, ErrorMessage = "Max login attempts must be between 1 and 100")]
            public int MaxLoginAttempts { get; set; } = Constants.Security.MaxLoginAttempts;

            [Range(1, 1440, ErrorMessage = "Lockout duration must be between 1 and 1440 minutes")]
            public int LockoutDurationMinutes { get; set; } = Constants.Security.LockoutDurationMinutes;

            [Range(3, 128, ErrorMessage = "Minimum password length must be between 3 and 128 characters")]
            public int MinPasswordLength { get; set; } = Constants.Security.MinPasswordLength;

            public bool RequireDigit { get; set; } = true;

            public bool RequireLowercase { get; set; } = true;

            public bool RequireUppercase { get; set; } = true;

            public bool RequireNonAlphanumeric { get; set; } = true;

            [Range(0, 100, ErrorMessage = "Password history count must be between 0 and 100")]
            public int PasswordHistoryCount { get; set; } = Constants.Security.PasswordHistoryCount;

            [Range(0, 365, ErrorMessage = "Password expiration days must be between 0 and 365")]
            public int PasswordExpirationDays { get; set; } = 90;

            public bool EnableTwoFactor { get; set; } = true;

            public bool EnableBiometricAuth { get; set; } = false;

            [Range(1, 100, ErrorMessage = "Max concurrent sessions must be between 1 and 100")]
            public int MaxConcurrentSessions { get; set; } = Constants.Security.MaxConcurrentSessions;

            public bool EnableIpWhitelist { get; set; } = false;

            public string[] IpWhitelist { get; set; } = Array.Empty<string>();

            public bool EnableGeolocationCheck { get; set; } = false;

            [Range(1, 10000, ErrorMessage = "Geolocation radius must be between 1 and 10000 km")]
            public int GeolocationRadiusKm { get; set; } = Constants.Security.GeolocationCheckRadiusKm;

            public bool EnableRateLimiting { get; set; } = true;

            [Range(1, 10000, ErrorMessage = "Rate limit must be between 1 and 10000 requests")]
            public int RateLimitRequests { get; set; } = 100;

            [Range(1, 3600, ErrorMessage = "Rate limit window must be between 1 and 3600 seconds")]
            public int RateLimitWindowSeconds { get; set; } = 60;

            public bool EnableAuditLogging { get; set; } = true;

            public bool EnableDataMasking { get; set; } = true;

            public bool EnableInputValidation { get; set; } = true;

            public bool EnableOutputEncoding { get; set; } = true;

            public string[] AllowedFileExtensions { get; set; } =
            {
                ".txt", ".pdf", ".doc", ".docx", ".xls", ".xlsx",
                ".jpg", ".jpeg", ".png", ".gif", ".mp3", ".mp4"
            };

            [Range(1, 104857600, ErrorMessage = "Max file size must be between 1 and 104857600 bytes (100MB)")]
            public long MaxFileSize { get; set; } = Constants.FileSystem.MaxLogFileSize;

            public Dictionary<string, string> SecurityHeaders { get; set; } = new Dictionary<string, string>
            {
                ["X-Frame-Options"] = "DENY",
                ["X-Content-Type-Options"] = "nosniff",
                ["X-XSS-Protection"] = "1; mode=block",
                ["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains",
                ["Content-Security-Policy"] = "default-src 'self'"
            };
        }

        #endregion

        #region Logging Configuration

        [Required(ErrorMessage = "Logging configuration is required")]
        public LoggingConfig LoggingSettings { get; set; }

        public class LoggingConfig
        {
            [Required(ErrorMessage = "Log level is required")]
            public LogLevel LogLevel { get; set; } = LogLevel.Information;

            [Required(ErrorMessage = "Log path is required")]
            public string LogPath { get; set; } = "Logs";

            public bool EnableFileLogging { get; set; } = true;

            public bool EnableConsoleLogging { get; set; } = true;

            public bool EnableDatabaseLogging { get; set; } = false;

            public bool EnableEventLogging { get; set; } = false;

            [Range(1, 100, ErrorMessage = "Max log file size must be between 1 and 100 MB")]
            public int MaxLogFileSizeMB { get; set; } = 10;

            [Range(1, 1000, ErrorMessage = "Max log files must be between 1 and 1000")]
            public int MaxLogFiles { get; set; } = Constants.FileSystem.MaxLogFiles;

            public string LogFileTemplate { get; set; } = "{Date:yyyy-MM-dd}.log";

            public bool EnableStructuredLogging { get; set; } = true;

            public bool EnableJsonFormat { get; set; } = true;

            public bool EnableLogRotation { get; set; } = true;

            [Range(1, 365, ErrorMessage = "Log retention days must be between 1 and 365")]
            public int LogRetentionDays { get; set; } = 30;

            public bool EnablePerformanceLogging { get; set; } = true;

            public bool EnableAuditLogging { get; set; } = true;

            public bool EnableSecurityLogging { get; set; } = true;

            public bool EnableErrorReporting { get; set; } = true;

            public string[] ExcludedLogSources { get; set; } = Array.Empty<string>();

            public Dictionary<string, LogLevel> LogLevelOverrides { get; set; } = new Dictionary<string, LogLevel>();

            public bool EnableCorrelationId { get; set; } = true;

            public bool EnableRequestLogging { get; set; } = true;

            public bool EnableResponseLogging { get; set; } = true;
        }

        #endregion

        #region Network Configuration

        [Required(ErrorMessage = "Network configuration is required")]
        public NetworkConfig NetworkSettings { get; set; }

        public class NetworkConfig
        {
            [Url(ErrorMessage = "API URL must be a valid URL")]
            public string ApiUrl { get; set; } = "https://api.neda.ai";

            [Range(1, 300000, ErrorMessage = "Timeout must be between 1 and 300000 ms")]
            public int TimeoutMs { get; set; } = Constants.Network.HttpTimeout;

            [Range(0, 10, ErrorMessage = "Retry count must be between 0 and 10")]
            public int RetryCount { get; set; } = Constants.Network.RetryCount;

            [Range(0, 10000, ErrorMessage = "Retry delay must be between 0 and 10000 ms")]
            public int RetryDelayMs { get; set; } = Constants.Network.RetryDelay;

            [Range(1, 1000, ErrorMessage = "Max connections must be between 1 and 1000")]
            public int MaxConnections { get; set; } = Constants.Network.MaxConnections;

            [Range(1, 100, ErrorMessage = "Max connections per IP must be between 1 and 100")]
            public int MaxConnectionsPerIp { get; set; } = Constants.Network.MaxConnectionsPerIp;

            [Range(1000, 300000, ErrorMessage = "Connection timeout must be between 1000 and 300000 ms")]
            public int ConnectionTimeoutMs { get; set; } = Constants.Network.ConnectionTimeout;

            [Range(1000, 60000, ErrorMessage = "Keep alive interval must be between 1000 and 60000 ms")]
            public int KeepAliveIntervalMs { get; set; } = Constants.Network.KeepAliveInterval;

            [Range(1024, 104857600, ErrorMessage = "Max message size must be between 1024 and 104857600 bytes")]
            public int MaxMessageSize { get; set; } = Constants.Network.MaxMessageSize;

            public bool EnableCompression { get; set; } = true;

            public bool EnableCaching { get; set; } = true;

            [Range(0, 3600, ErrorMessage = "Cache duration must be between 0 and 3600 seconds")]
            public int CacheDurationSeconds { get; set; } = 300;

            public bool EnableProxy { get; set; } = false;

            public string ProxyUrl { get; set; }

            public string ProxyUsername { get; set; }

            public string ProxyPassword { get; set; }

            public bool EnableDnsCache { get; set; } = true;

            [Range(0, 3600000, ErrorMessage = "DNS cache timeout must be between 0 and 3600000 ms")]
            public int DnsCacheTimeoutMs { get; set; } = Constants.Network.DnsCacheTimeout;

            public bool EnableLoadBalancing { get; set; } = false;

            public string[] LoadBalancerUrls { get; set; } = Array.Empty<string>();

            public bool EnableCircuitBreaker { get; set; } = true;

            [Range(1, 100, ErrorMessage = "Circuit breaker threshold must be between 1 and 100")]
            public int CircuitBreakerThreshold { get; set; } = 5;

            [Range(1000, 60000, ErrorMessage = "Circuit breaker timeout must be between 1000 and 60000 ms")]
            public int CircuitBreakerTimeoutMs { get; set; } = 30000;

            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>
            {
                ["User-Agent"] = Constants.Network.UserAgent,
                ["Accept"] = "application/json",
                ["Accept-Encoding"] = "gzip, deflate"
            };

            public bool EnableWebSockets { get; set; } = true;

            [Range(1024, 65536, ErrorMessage = "WebSocket buffer size must be between 1024 and 65536 bytes")]
            public int WebSocketBufferSize { get; set; } = 4096;
        }

        #endregion

        #region Performance Configuration

        [Required(ErrorMessage = "Performance configuration is required")]
        public PerformanceConfig PerformanceSettings { get; set; }

        public class PerformanceConfig
        {
            [Range(1, 10000, ErrorMessage = "Cache size must be between 1 and 10000 MB")]
            public int CacheSizeMB { get; set; } = 100;

            [Range(1, 100, ErrorMessage = "Thread pool size must be between 1 and 100")]
            public int ThreadPoolSize { get; set; } = Constants.Engine.DefaultThreadPoolSize;

            public GCMode GcMode { get; set; } = GCMode.Server;

            [Range(1, 100, ErrorMessage = "Object pool size must be between 1 and 100")]
            public int ObjectPoolSize { get; set; } = Constants.Performance.ObjectPoolSize;

            [Range(1, 1000, ErrorMessage = "Connection pool size must be between 1 and 1000")]
            public int ConnectionPoolSize { get; set; } = Constants.Performance.ConnectionPoolSize;

            [Range(50, 100, ErrorMessage = "GC collection threshold must be between 50 and 100")]
            public int GcCollectionThreshold { get; set; } = Constants.Performance.GcCollectionThreshold;

            [Range(1, 50, ErrorMessage = "Cache eviction percentage must be between 1 and 50")]
            public int CacheEvictionPercentage { get; set; } = Constants.Performance.CacheEvictionPercentage;

            [Range(10, 1000, ErrorMessage = "Sampling interval must be between 10 and 1000 ms")]
            public int SamplingIntervalMs { get; set; } = Constants.Performance.SamplingInterval;

            [Range(60, 86400, ErrorMessage = "History size must be between 60 and 86400 seconds")]
            public int HistorySizeSeconds { get; set; } = Constants.Performance.HistorySize;

            [Range(50, 100, ErrorMessage = "CPU warning threshold must be between 50 and 100")]
            public double CpuWarningThreshold { get; set; } = Constants.Performance.CpuWarningThreshold;

            [Range(60, 100, ErrorMessage = "CPU critical threshold must be between 60 and 100")]
            public double CpuCriticalThreshold { get; set; } = Constants.Performance.CpuCriticalThreshold;

            [Range(50, 100, ErrorMessage = "Memory warning threshold must be between 50 and 100")]
            public double MemoryWarningThreshold { get; set; } = Constants.Performance.MemoryWarningThreshold;

            [Range(60, 100, ErrorMessage = "Memory critical threshold must be between 60 and 100")]
            public double MemoryCriticalThreshold { get; set; } = Constants.Performance.MemoryCriticalThreshold;

            [Range(50, 100, ErrorMessage = "Disk warning threshold must be between 50 and 100")]
            public double DiskWarningThreshold { get; set; } = Constants.Performance.DiskWarningThreshold;

            [Range(60, 100, ErrorMessage = "Disk critical threshold must be between 60 and 100")]
            public double DiskCriticalThreshold { get; set; } = Constants.Performance.DiskCriticalThreshold;

            [Range(500, 10000, ErrorMessage = "Response time warning must be between 500 and 10000 ms")]
            public int ResponseTimeWarningMs { get; set; } = Constants.Performance.ResponseTimeWarning;

            [Range(1000, 30000, ErrorMessage = "Response time critical must be between 1000 and 30000 ms")]
            public int ResponseTimeCriticalMs { get; set; } = Constants.Performance.ResponseTimeCritical;

            public bool EnablePerformanceCounters { get; set; } = true;

            public bool EnableResourceMonitoring { get; set; } = true;

            public bool EnableGarbageCollectionMonitoring { get; set; } = true;

            public bool EnableThreadPoolMonitoring { get; set; } = true;

            public bool EnableMemoryProfiling { get; set; } = false;

            public bool EnableCpuProfiling { get; set; } = false;

            public int ProfilingSamplingRate { get; set; } = 1000;
        }

        /// <summary>
        /// Garbage Collection mode enumeration;
        /// </summary>
        public enum GCMode
        {
            Workstation = 0,
            Server = 1,
            SustainedLowLatency = 2
        }

        #endregion

        #region External Services Configuration

        [Required(ErrorMessage = "External services configuration is required")]
        public ExternalServicesConfig ExternalServices { get; set; }

        public class ExternalServicesConfig
        {
            public string ApiKey { get; set; }

            [Url(ErrorMessage = "Webhook URL must be a valid URL")]
            public string WebhookUrl { get; set; }

            public string StorageConnectionString { get; set; }

            [Url(ErrorMessage = "Email service URL must be a valid URL")]
            public string EmailServiceUrl { get; set; }

            public string EmailServiceApiKey { get; set; }

            [Url(ErrorMessage = "SMS service URL must be a valid URL")]
            public string SmsServiceUrl { get; set; }

            public string SmsServiceApiKey { get; set; }

            [Url(ErrorMessage = "Notification service URL must be a valid URL")]
            public string NotificationServiceUrl { get; set; }

            public string NotificationServiceApiKey { get; set; }

            [Url(ErrorMessage = "Payment service URL must be a valid URL")]
            public string PaymentServiceUrl { get; set; }

            public string PaymentServiceApiKey { get; set; }

            [Url(ErrorMessage = "Analytics service URL must be a valid URL")]
            public string AnalyticsServiceUrl { get; set; }

            public string AnalyticsServiceApiKey { get; set; }

            public Dictionary<string, ExternalServiceConfig> Services { get; set; } = new Dictionary<string, ExternalServiceConfig>();
        }

        public class ExternalServiceConfig
        {
            [Required(ErrorMessage = "Service URL is required")]
            [Url(ErrorMessage = "Service URL must be a valid URL")]
            public string Url { get; set; }

            public string ApiKey { get; set; }

            [Range(1, 300000, ErrorMessage = "Timeout must be between 1 and 300000 ms")]
            public int TimeoutMs { get; set; } = 30000;

            [Range(0, 10, ErrorMessage = "Retry count must be between 0 and 10")]
            public int RetryCount { get; set; } = 3;

            public bool IsEnabled { get; set; } = true;

            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        }

        #endregion

        #region Cache Configuration

        [Required(ErrorMessage = "Cache configuration is required")]
        public CacheConfig CacheSettings { get; set; }

        public class CacheConfig
        {
            [Range(1, 10000, ErrorMessage = "Memory cache size must be between 1 and 10000 MB")]
            public int MemoryCacheSizeMB { get; set; } = 100;

            [Range(0, 1440, ErrorMessage = "Default cache duration must be between 0 and 1440 minutes")]
            public int DefaultCacheDurationMinutes { get; set; } = 15;

            public bool EnableDistributedCache { get; set; } = false;

            public string DistributedCacheConnectionString { get; set; }

            public string DistributedCacheInstanceName { get; set; } = "NEDA";

            [Range(0, 10080, ErrorMessage = "Distributed cache timeout must be between 0 and 10080 minutes")]
            public int DistributedCacheTimeoutMinutes { get; set; } = 60;

            public bool EnableResponseCaching { get; set; } = true;

            [Range(0, 1440, ErrorMessage = "Response cache duration must be between 0 and 1440 minutes")]
            public int ResponseCacheDurationMinutes { get; set; } = 5;

            public bool EnableQueryCaching { get; set; } = true;

            [Range(0, 1440, ErrorMessage = "Query cache duration must be between 0 and 1440 minutes")]
            public int QueryCacheDurationMinutes { get; set; } = 10;

            public Dictionary<string, CachePolicy> CachePolicies { get; set; } = new Dictionary<string, CachePolicy>();

            public bool EnableCacheCompression { get; set; } = true;

            public bool EnableCacheStatistics { get; set; } = true;
        }

        public class CachePolicy
        {
            [Range(0, 10080, ErrorMessage = "Cache duration must be between 0 and 10080 minutes")]
            public int DurationMinutes { get; set; }

            public bool SlidingExpiration { get; set; } = false;

            [Range(0, 100, ErrorMessage = "Priority must be between 0 and 100")]
            public int Priority { get; set; } = 50;

            public string[] DependencyKeys { get; set; } = Array.Empty<string>();
        }

        #endregion

        #region Monitoring Configuration

        [Required(ErrorMessage = "Monitoring configuration is required")]
        public MonitoringConfig MonitoringSettings { get; set; }

        public class MonitoringConfig
        {
            public bool EnableApplicationInsights { get; set; } = true;

            public string ApplicationInsightsKey { get; set; }

            public bool EnableHealthChecks { get; set; } = true;

            [Range(1, 300, ErrorMessage = "Health check interval must be between 1 and 300 seconds")]
            public int HealthCheckIntervalSeconds { get; set; } = 30;

            [Range(1, 300, ErrorMessage = "Health check timeout must be between 1 and 300 seconds")]
            public int HealthCheckTimeoutSeconds { get; set; } = 10;

            public bool EnableMetrics { get; set; } = true;

            [Range(1, 300, ErrorMessage = "Metrics collection interval must be between 1 and 300 seconds")]
            public int MetricsCollectionIntervalSeconds { get; set; } = 60;

            public bool EnableTracing { get; set; } = true;

            public string TracingEndpoint { get; set; } = "http://localhost:9411/api/v2/spans";

            public bool EnableAlerting { get; set; } = true;

            public Dictionary<string, AlertThreshold> AlertThresholds { get; set; } = new Dictionary<string, AlertThreshold>();

            public bool EnableDashboard { get; set; } = true;

            public string DashboardUrl { get; set; } = "http://localhost:3000";

            public bool EnableEventLogging { get; set; } = true;

            public bool EnablePerformanceLogging { get; set; } = true;
        }

        public class AlertThreshold
        {
            [Range(0, 100, ErrorMessage = "Warning threshold must be between 0 and 100")]
            public double Warning { get; set; }

            [Range(0, 100, ErrorMessage = "Critical threshold must be between 0 and 100")]
            public double Critical { get; set; }

            [Range(1, 300, ErrorMessage = "Check interval must be between 1 and 300 seconds")]
            public int CheckIntervalSeconds { get; set; } = 60;
        }

        #endregion

        #region IValidatableObject Implementation

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            var results = new List<ValidationResult>();

            if (ApplicationSettings == null)
            {
                results.Add(new ValidationResult("Application settings are required", new[] { nameof(ApplicationSettings) }));
            }

            if (AIModels == null || AIModels.Count == 0)
            {
                results.Add(new ValidationResult("At least one AI model must be configured", new[] { nameof(AIModels) }));
            }
            else
            {
                for (int i = 0; i < AIModels.Count; i++)
                {
                    var model = AIModels[i];
                    if (model != null && !model.IsEnabled && model.IsRequired)
                    {
                        results.Add(new ValidationResult(
                            $"Required AI model '{model.Name}' is disabled",
                            new[] { $"{nameof(AIModels)}[{i}].IsEnabled" }));
                    }
                }
            }

            if (DatabaseSettings == null)
            {
                results.Add(new ValidationResult("Database settings are required", new[] { nameof(DatabaseSettings) }));
            }
            else if (string.IsNullOrWhiteSpace(DatabaseSettings.ConnectionString))
            {
                results.Add(new ValidationResult("Database connection string is required",
                    new[] { $"{nameof(DatabaseSettings)}.{nameof(DatabaseSettings.ConnectionString)}" }));
            }

            if (SecuritySettings == null)
            {
                results.Add(new ValidationResult("Security settings are required", new[] { nameof(SecuritySettings) }));
            }
            else if (string.IsNullOrWhiteSpace(SecuritySettings.JwtSecret))
            {
                results.Add(new ValidationResult("JWT secret is required for security",
                    new[] { $"{nameof(SecuritySettings)}.{nameof(SecuritySettings.JwtSecret)}" }));
            }

            if (NetworkSettings != null && NetworkSettings.TimeoutMs < 1000)
            {
                results.Add(new ValidationResult("Network timeout should be at least 1000ms for reliable operations",
                    new[] { $"{nameof(NetworkSettings)}.{nameof(NetworkSettings.TimeoutMs)}" }));
            }

            if (ApplicationSettings != null)
            {
                if (ApplicationSettings.Environment == EnvironmentType.Production)
                {
                    if (!SecuritySettings?.RequireHttps ?? false)
                    {
                        results.Add(new ValidationResult("HTTPS is required in production environment",
                            new[] { $"{nameof(SecuritySettings)}.{nameof(SecuritySettings.RequireHttps)}" }));
                    }

                    if (string.IsNullOrWhiteSpace(SecuritySettings?.JwtSecret) || SecuritySettings.JwtSecret.Length < 64)
                    {
                        results.Add(new ValidationResult("Production environment requires a JWT secret of at least 64 characters",
                            new[] { $"{nameof(SecuritySettings)}.{nameof(SecuritySettings.JwtSecret)}" }));
                    }
                }
            }

            return results;
        }

        #endregion

        #region Helper Methods

        public static AppConfig CreateDefaultDevelopmentConfig()
        {
            return new AppConfig
            {
                ApplicationSettings = new ApplicationConfig
                {
                    Name = Constants.Application.Name,
                    Version = Constants.Application.Version,
                    Environment = EnvironmentType.Development,
                    Mode = ApplicationMode.Normal,
                    DebugMode = true,
                    ApiPort = 5000,
                    GrpcPort = 50051,
                    BaseUrl = "https://localhost:5000",
                    AllowedOrigins = new[] { "*" },
                    EnableSwagger = true,
                    EnableHealthChecks = true
                },
                AIModels = new List<AIModelConfig>
                {
                    new AIModelConfig
                    {
                        Name = "Default-Model",
                        Type = ModelType.NeuralNetwork,
                        Path = "Models/default.model",
                        LearningRate = 0.001,
                        TrainingEpochs = 100,
                        BatchSize = 32,
                        IsEnabled = true,
                        IsRequired = true
                    }
                },
                DatabaseSettings = new DatabaseConfig
                {
                    ConnectionString = "Server=(localdb)\\mssqllocaldb;Database=NEDA;Trusted_Connection=True;",
                    Provider = "SQLServer",
                    CommandTimeout = 30,
                    MaxPoolSize = 100
                },
                SecuritySettings = new SecurityConfig
                {
                    JwtSecret = "dev-jwt-secret-key-minimum-32-characters-long-for-development",
                    EncryptionKey = "dev-encryption-key-32-characters-minimum",
                    RequireHttps = false,
                    TokenExpiryHours = 24,
                    MinPasswordLength = 8
                },
                LoggingSettings = new LoggingConfig
                {
                    LogLevel = LogLevel.Debug,
                    LogPath = "Logs",
                    EnableFileLogging = true,
                    EnableConsoleLogging = true
                }
            };
        }

        public static AppConfig FromConfiguration(IConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var config = new AppConfig();
            configuration.Bind(config);
            return config;
        }

        public bool IsDebugMode => ApplicationSettings?.DebugMode ?? false;

        public bool IsProduction => ApplicationSettings?.Environment == EnvironmentType.Production;

        public bool IsDevelopment => ApplicationSettings?.Environment == EnvironmentType.Development;

        public string BaseUrl => ApplicationSettings?.BaseUrl ?? "https://localhost:5000";

        public string ApiUrl => NetworkSettings?.ApiUrl ?? BaseUrl;

        #endregion

        #region Serialization Methods

        public string ToJson()
        {
            return System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        }

        public static AppConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        public AppConfig Clone()
        {
            var json = ToJson();
            return FromJson(json);
        }

        public void Merge(AppConfig other)
        {
            if (other == null)
                return;

            if (other.ApplicationSettings != null)
            {
                ApplicationSettings = other.ApplicationSettings;
            }

            if (other.AIModels != null && other.AIModels.Count > 0)
            {
                AIModels = other.AIModels;
            }

            if (other.DatabaseSettings != null)
            {
                DatabaseSettings = other.DatabaseSettings;
            }

            if (other.SecuritySettings != null)
            {
                SecuritySettings = other.SecuritySettings;
            }

            if (other.LoggingSettings != null)
            {
                LoggingSettings = other.LoggingSettings;
            }

            if (other.NetworkSettings != null)
            {
                NetworkSettings = other.NetworkSettings;
            }

            if (other.PerformanceSettings != null)
            {
                PerformanceSettings = other.PerformanceSettings;
            }

            if (other.ExternalServices != null)
            {
                ExternalServices = other.ExternalServices;
            }

            if (other.CacheSettings != null)
            {
                CacheSettings = other.CacheSettings;
            }

            if (other.MonitoringSettings != null)
            {
                MonitoringSettings = other.MonitoringSettings;
            }
        }

        #endregion

        #region Validation Methods

        public void ValidateAndThrow()
        {
            var context = new ValidationContext(this);
            var results = new List<ValidationResult>();

            if (!Validator.TryValidateObject(this, context, results, true))
            {
                var errorMessages = results.Select(r => r.ErrorMessage);
                throw new ConfigurationValidationException("Configuration validation failed: " +
                    string.Join("; ", errorMessages));
            }
        }

        public IEnumerable<string> GetValidationErrors()
        {
            var context = new ValidationContext(this);
            var results = new List<ValidationResult>();

            Validator.TryValidateObject(this, context, results, true);

            return results.Select(r => r.ErrorMessage);
        }

        public bool IsValid()
        {
            var context = new ValidationContext(this);
            return Validator.TryValidateObject(this, context, new List<ValidationResult>(), true);
        }

        #endregion
    }

    #region Custom Exceptions

    public class ConfigurationValidationException : Exception
    {
        public ConfigurationValidationException() { }
        public ConfigurationValidationException(string message) : base(message) { }
        public ConfigurationValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationMissingException : Exception
    {
        public ConfigurationMissingException() { }
        public ConfigurationMissingException(string message) : base(message) { }
        public ConfigurationMissingException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion
}
