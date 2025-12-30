using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;

namespace NEDA.Core.Configuration.ConfigValidators
{
    /// <summary>
    /// Comprehensive configuration validator for NEDA application settings;
    /// Performs validation at multiple levels including file integrity, data validation, and dependency checking;
    /// </summary>
    public class ConfigValidator : IConfigValidator
    {
        private readonly ILogger<ConfigValidator> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly List<ValidationResult> _validationResults;

        /// <summary>
        /// Gets the validation mode for the validator;
        /// </summary>
        public ValidationMode Mode { get; private set; }

        /// <summary>
        /// Gets a value indicating whether validation was successful;
        /// </summary>
        public bool IsValid => !_validationResults.Any(r => r.Severity == ValidationSeverity.Error);

        /// <summary>
        /// Gets all validation results;
        /// </summary>
        public IReadOnlyList<ValidationResult> ValidationResults => _validationResults.AsReadOnly();

        /// <summary>
        /// Gets the validation summary;
        /// </summary>
        public ValidationSummary Summary { get; private set; }

        /// <summary>
        /// Initializes a new instance of ConfigValidator;
        /// </summary>
        public ConfigValidator(ILogger<ConfigValidator> logger, IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _validationResults = new List<ValidationResult>();
            Summary = new ValidationSummary();
            Mode = ValidationMode.Strict;
        }

        /// <summary>
        /// Validates the entire application configuration;
        /// </summary>
        public ValidationResult Validate(AppConfig config, ValidationMode mode = ValidationMode.Strict)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _logger.LogInformation("Starting configuration validation with mode: {Mode}", mode);

            Mode = mode;
            _validationResults.Clear();
            Summary = new ValidationSummary
            {
                StartTime = DateTime.UtcNow,
                TotalChecksPerformed = 0
            };

            try
            {
                // Perform comprehensive validation;
                ValidateStructure(config);
                ValidateApplicationSettings(config.ApplicationSettings);
                ValidateAIModels(config.AIModels);
                ValidateDatabaseSettings(config.DatabaseSettings);
                ValidateSecuritySettings(config.SecuritySettings);
                ValidateLoggingSettings(config.LoggingSettings);
                ValidateNetworkSettings(config.NetworkSettings);
                ValidatePerformanceSettings(config.PerformanceSettings);
                ValidateExternalServices(config.ExternalServices);
                ValidateCacheSettings(config.CacheSettings);
                ValidateMonitoringSettings(config.MonitoringSettings);
                ValidateDependencies(config);
                ValidateFileSystemAccess(config);
                ValidateEnvironmentSpecificRules(config);

                // Update summary;
                Summary.EndTime = DateTime.UtcNow;
                Summary.Duration = Summary.EndTime - Summary.StartTime;
                Summary.TotalErrors = _validationResults.Count(r => r.Severity == ValidationSeverity.Error);
                Summary.TotalWarnings = _validationResults.Count(r => r.Severity == ValidationSeverity.Warning);
                Summary.TotalInfo = _validationResults.Count(r => r.Severity == ValidationSeverity.Info);
                Summary.IsValid = IsValid;

                _logger.LogInformation("Configuration validation completed. Valid: {IsValid}, Errors: {ErrorCount}, Warnings: {WarningCount}",
                    IsValid, Summary.TotalErrors, Summary.TotalWarnings);

                if (!IsValid && mode == ValidationMode.Strict)
                {
                    throw new ConfigurationValidationException("Configuration validation failed", _validationResults);
                }

                return new ValidationResult
                {
                    IsValid = IsValid,
                    Message = IsValid ? "Configuration is valid" : "Configuration has validation issues",
                    Severity = IsValid ? ValidationSeverity.Info : ValidationSeverity.Error,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Configuration validation failed with exception");
                _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = "ConfigValidator.Validate",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.High
                }).GetAwaiter().GetResult();

                throw;
            }
        }

        /// <summary>
        /// Validates configuration files on disk;
        /// </summary>
        public ValidationResult ValidateConfigurationFiles(string configPath, IEnumerable<string> requiredFiles)
        {
            _logger.LogInformation("Validating configuration files at path: {ConfigPath}", configPath);

            var fileResults = new List<ValidationResult>();

            try
            {
                // Check if configuration directory exists;
                if (!Directory.Exists(configPath))
                {
                    AddValidationResult(ValidationSeverity.Error, "Configuration directory does not exist",
                        $"Path: {configPath}", "FileSystem");
                    return new ValidationResult
                    {
                        IsValid = false,
                        Message = "Configuration directory not found",
                        Severity = ValidationSeverity.Error
                    };
                }

                // Validate each required file;
                foreach (var file in requiredFiles ?? Enumerable.Empty<string>())
                {
                    var filePath = Path.Combine(configPath, file);
                    ValidateConfigurationFile(filePath);
                }

                // Validate file permissions;
                ValidateFilePermissions(configPath);

                // Validate file integrity;
                ValidateFileIntegrity(configPath);

                return new ValidationResult
                {
                    IsValid = IsValid,
                    Message = "Configuration files validation completed",
                    Severity = IsValid ? ValidationSeverity.Info : ValidationSeverity.Warning
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File validation failed");
                AddValidationResult(ValidationSeverity.Error, "File validation failed", ex.Message, "FileSystem");
                throw;
            }
        }

        /// <summary>
        /// Validates configuration against environment-specific rules;
        /// </summary>
        public ValidationResult ValidateForEnvironment(AppConfig config, EnvironmentType environment)
        {
            _logger.LogInformation("Validating configuration for environment: {Environment}", environment);

            var environmentResults = new List<ValidationResult>();

            try
            {
                switch (environment)
                {
                    case EnvironmentType.Production:
                        ValidateProductionRules(config);
                        break;
                    case EnvironmentType.Staging:
                        ValidateStagingRules(config);
                        break;
                    case EnvironmentType.Development:
                        ValidateDevelopmentRules(config);
                        break;
                    case EnvironmentType.Testing:
                        ValidateTestingRules(config);
                        break;
                }

                return new ValidationResult
                {
                    IsValid = IsValid,
                    Message = $"Environment validation for {environment} completed",
                    Severity = IsValid ? ValidationSeverity.Info : ValidationSeverity.Warning
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Environment validation failed");
                AddValidationResult(ValidationSeverity.Error, "Environment validation failed", ex.Message, "Environment");
                throw;
            }
        }

        /// <summary>
        /// Gets detailed validation report;
        /// </summary>
        public ValidationReport GetValidationReport()
        {
            return new ValidationReport
            {
                Summary = Summary,
                Results = _validationResults,
                GeneratedAt = DateTime.UtcNow,
                ValidatorVersion = "1.0.0"
            };
        }

        /// <summary>
        /// Clears all validation results;
        /// </summary>
        public void ClearResults()
        {
            _validationResults.Clear();
            Summary = new ValidationSummary();
        }

        #region Private Validation Methods

        private void ValidateStructure(AppConfig config)
        {
            AddCheck("Structure", "Config structure validation");

            if (config == null)
            {
                AddValidationResult(ValidationSeverity.Error, "Configuration object is null",
                    "AppConfig instance cannot be null", "Structure");
                return;
            }

            // Validate required sections;
            ValidateRequiredSection(config.ApplicationSettings, "ApplicationSettings");
            ValidateRequiredSection(config.DatabaseSettings, "DatabaseSettings");
            ValidateRequiredSection(config.SecuritySettings, "SecuritySettings");
            ValidateRequiredSection(config.LoggingSettings, "LoggingSettings");

            // Validate collections;
            if (config.AIModels == null || config.AIModels.Count == 0)
            {
                AddValidationResult(ValidationSeverity.Warning, "No AI models configured",
                    "At least one AI model should be configured", "Structure");
            }
        }

        private void ValidateApplicationSettings(ApplicationConfig settings)
        {
            if (settings == null) return;

            AddCheck("Application", "Application settings validation");

            // Basic validation;
            ValidateRequiredString(settings.Name, "Application Name", "Application");
            ValidateRequiredString(settings.Version, "Application Version", "Application");

            // Port validation;
            if (settings.ApiPort < 1 || settings.ApiPort > 65535)
            {
                AddValidationResult(ValidationSeverity.Error, "Invalid API port",
                    $"API port must be between 1 and 65535. Current: {settings.ApiPort}", "Application");
            }

            // URL validation;
            if (!string.IsNullOrWhiteSpace(settings.BaseUrl) && !Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _))
            {
                AddValidationResult(ValidationSeverity.Error, "Invalid base URL",
                    $"Base URL '{settings.BaseUrl}' is not a valid URL", "Application");
            }

            // Directory validation;
            ValidateDirectory(settings.DataDirectory, "Data Directory", "Application");
            ValidateDirectory(settings.LogDirectory, "Log Directory", "Application");
            ValidateDirectory(settings.TempDirectory, "Temp Directory", "Application");

            // Data retention validation;
            if (settings.DataRetentionDays < 1 || settings.DataRetentionDays > 3650)
            {
                AddValidationResult(ValidationSeverity.Warning, "Data retention days out of range",
                    $"Data retention days should be between 1 and 3650. Current: {settings.DataRetentionDays}", "Application");
            }

            // Session timeout validation;
            if (settings.SessionTimeoutMinutes < 1 || settings.SessionTimeoutMinutes > 1440)
            {
                AddValidationResult(ValidationSeverity.Warning, "Session timeout out of range",
                    $"Session timeout should be between 1 and 1440 minutes. Current: {settings.SessionTimeoutMinutes}", "Application");
            }
        }

        private void ValidateAIModels(List<AIModelConfig> models)
        {
            if (models == null || models.Count == 0) return;

            AddCheck("AI Models", $"Validating {models.Count} AI models");

            var enabledModels = models.Where(m => m.IsEnabled).ToList();
            var requiredModels = models.Where(m => m.IsRequired).ToList();

            // Check if required models are enabled;
            foreach (var model in requiredModels.Where(m => !m.IsEnabled))
            {
                AddValidationResult(ValidationSeverity.Error, "Required AI model is disabled",
                    $"Model '{model.Name}' is required but disabled", "AI Models");
            }

            // Validate each model;
            for (int i = 0; i < models.Count; i++)
            {
                ValidateAIModel(models[i], i);
            }
        }

        private void ValidateAIModel(AIModelConfig model, int index)
        {
            if (model == null) return;

            var context = $"AI Models[{index}] ({model.Name})";

            ValidateRequiredString(model.Name, "Model Name", context);
            ValidateRequiredString(model.Path, "Model Path", context);

            // Learning rate validation;
            if (model.LearningRate < Constants.Engine.MinLearningRate || model.LearningRate > Constants.Engine.MaxLearningRate)
            {
                AddValidationResult(ValidationSeverity.Warning, "Learning rate out of recommended range",
                    $"Learning rate should be between {Constants.Engine.MinLearningRate} and {Constants.Engine.MaxLearningRate}. Current: {model.LearningRate}", context);
            }

            // Batch size validation;
            if (model.BatchSize < Constants.Engine.MinBatchSize || model.BatchSize > Constants.Engine.MaxBatchSize)
            {
                AddValidationResult(ValidationSeverity.Warning, "Batch size out of recommended range",
                    $"Batch size should be between {Constants.Engine.MinBatchSize} and {Constants.Engine.MaxBatchSize}. Current: {model.BatchSize}", context);
            }

            // Confidence threshold validation;
            if (model.ConfidenceThreshold < 0.0 || model.ConfidenceThreshold > 1.0)
            {
                AddValidationResult(ValidationSeverity.Error, "Invalid confidence threshold",
                    $"Confidence threshold must be between 0.0 and 1.0. Current: {model.ConfidenceThreshold}", context);
            }

            // Inference timeout validation;
            if (model.InferenceTimeoutMs < Constants.Engine.MinInferenceTimeout || model.InferenceTimeoutMs > Constants.Engine.MaxInferenceTimeout)
            {
                AddValidationResult(ValidationSeverity.Warning, "Inference timeout out of range",
                    $"Inference timeout should be between {Constants.Engine.MinInferenceTimeout} and {Constants.Engine.MaxInferenceTimeout} ms. Current: {model.InferenceTimeoutMs}", context);
            }

            // Cache TTL validation;
            if (model.CacheTTLMinutes < 0 || model.CacheTTLMinutes > 10080)
            {
                AddValidationResult(ValidationSeverity.Warning, "Cache TTL out of range",
                    $"Cache TTL should be between 0 and 10080 minutes (1 week). Current: {model.CacheTTLMinutes}", context);
            }

            // Validate model file existence if path is local;
            if (!model.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(model.Path))
            {
                AddValidationResult(ValidationSeverity.Warning, "Model file not found",
                    $"Model file not found at path: {model.Path}", context);
            }
        }

        private void ValidateDatabaseSettings(DatabaseConfig settings)
        {
            if (settings == null) return;

            AddCheck("Database", "Database settings validation");

            ValidateRequiredString(settings.ConnectionString, "Connection String", "Database");

            // Connection string basic validation;
            if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
            {
                if (settings.ConnectionString.Length > 1000)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Connection string is very long",
                        "Connection string exceeds 1000 characters, consider using integrated security", "Database");
                }

                // Check for sensitive data in connection string;
                if (settings.ConnectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase) &&
                    !settings.ConnectionString.Contains("Integrated Security", StringComparison.OrdinalIgnoreCase))
                {
                    AddValidationResult(ValidationSeverity.Warning, "Password in connection string",
                        "Consider using integrated security or secure credential storage", "Database");
                }
            }

            // Pool size validation;
            if (settings.MinPoolSize > settings.MaxPoolSize)
            {
                AddValidationResult(ValidationSeverity.Error, "Invalid pool size configuration",
                    $"Min pool size ({settings.MinPoolSize}) cannot be greater than max pool size ({settings.MaxPoolSize})", "Database");
            }

            // Command timeout validation;
            if (settings.CommandTimeout < 1 || settings.CommandTimeout > 600)
            {
                AddValidationResult(ValidationSeverity.Warning, "Command timeout out of range",
                    $"Command timeout should be between 1 and 600 seconds. Current: {settings.CommandTimeout}", "Database");
            }

            // Retry count validation;
            if (settings.MaxRetryCount < 0 || settings.MaxRetryCount > 10)
            {
                AddValidationResult(ValidationSeverity.Warning, "Max retry count out of range",
                    $"Max retry count should be between 0 and 10. Current: {settings.MaxRetryCount}", "Database");
            }

            // Batch size validation;
            if (settings.BatchSize < 1 || settings.BatchSize > 10000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Batch size out of range",
                    $"Batch size should be between 1 and 10000. Current: {settings.BatchSize}", "Database");
            }

            // Audit retention validation;
            if (settings.AuditRetentionDays < 1 || settings.AuditRetentionDays > 3650)
            {
                AddValidationResult(ValidationSeverity.Warning, "Audit retention out of range",
                    $"Audit retention should be between 1 and 3650 days. Current: {settings.AuditRetentionDays}", "Database");
            }
        }

        private void ValidateSecuritySettings(SecurityConfig settings)
        {
            if (settings == null) return;

            AddCheck("Security", "Security settings validation");

            // JWT secret validation;
            if (string.IsNullOrWhiteSpace(settings.JwtSecret))
            {
                AddValidationResult(ValidationSeverity.Error, "JWT secret is required",
                    "JWT secret cannot be empty", "Security");
            }
            else if (settings.JwtSecret.Length < 32)
            {
                AddValidationResult(ValidationSeverity.Error, "JWT secret is too short",
                    $"JWT secret must be at least 32 characters. Current length: {settings.JwtSecret.Length}", "Security");
            }
            else if (settings.JwtSecret == "dev-jwt-secret" || settings.JwtSecret.Contains("secret"))
            {
                AddValidationResult(ValidationSeverity.Warning, "Weak JWT secret detected",
                    "Consider using a stronger, randomly generated JWT secret", "Security");
            }

            // Encryption key validation;
            if (string.IsNullOrWhiteSpace(settings.EncryptionKey))
            {
                AddValidationResult(ValidationSeverity.Error, "Encryption key is required",
                    "Encryption key cannot be empty", "Security");
            }
            else if (settings.EncryptionKey.Length < 32)
            {
                AddValidationResult(ValidationSeverity.Error, "Encryption key is too short",
                    $"Encryption key must be at least 32 characters. Current length: {settings.EncryptionKey.Length}", "Security");
            }

            // Password policy validation;
            if (settings.MinPasswordLength < Constants.Security.MinPasswordLength)
            {
                AddValidationResult(ValidationSeverity.Warning, "Minimum password length is too low",
                    $"Minimum password length should be at least {Constants.Security.MinPasswordLength} characters. Current: {settings.MinPasswordLength}", "Security");
            }

            if (settings.MaxLoginAttempts < 1 || settings.MaxLoginAttempts > 100)
            {
                AddValidationResult(ValidationSeverity.Warning, "Max login attempts out of range",
                    $"Max login attempts should be between 1 and 100. Current: {settings.MaxLoginAttempts}", "Security");
            }

            // Token expiry validation;
            if (settings.TokenExpiryHours < 1 || settings.TokenExpiryHours > 720)
            {
                AddValidationResult(ValidationSeverity.Warning, "Token expiry out of range",
                    $"Token expiry should be between 1 and 720 hours (30 days). Current: {settings.TokenExpiryHours}", "Security");
            }

            // Rate limiting validation;
            if (settings.EnableRateLimiting)
            {
                if (settings.RateLimitRequests < 1 || settings.RateLimitRequests > 10000)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Rate limit out of range",
                        $"Rate limit should be between 1 and 10000 requests. Current: {settings.RateLimitRequests}", "Security");
                }

                if (settings.RateLimitWindowSeconds < 1 || settings.RateLimitWindowSeconds > 3600)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Rate limit window out of range",
                        $"Rate limit window should be between 1 and 3600 seconds. Current: {settings.RateLimitWindowSeconds}", "Security");
                }
            }

            // File upload validation;
            if (settings.MaxFileSize <= 0 || settings.MaxFileSize > 1073741824) // 1GB;
            {
                AddValidationResult(ValidationSeverity.Warning, "Max file size out of range",
                    $"Max file size should be between 1 and 1073741824 bytes (1GB). Current: {settings.MaxFileSize}", "Security");
            }

            // IP whitelist validation;
            if (settings.EnableIpWhitelist && (settings.IpWhitelist == null || settings.IpWhitelist.Length == 0))
            {
                AddValidationResult(ValidationSeverity.Warning, "IP whitelist enabled but empty",
                    "IP whitelist is enabled but no IP addresses are configured", "Security");
            }

            // Geolocation validation;
            if (settings.EnableGeolocationCheck && settings.GeolocationRadiusKm <= 0)
            {
                AddValidationResult(ValidationSeverity.Error, "Invalid geolocation radius",
                    "Geolocation radius must be greater than 0 when geolocation check is enabled", "Security");
            }
        }

        private void ValidateLoggingSettings(LoggingConfig settings)
        {
            if (settings == null) return;

            AddCheck("Logging", "Logging settings validation");

            // Log path validation;
            if (!string.IsNullOrWhiteSpace(settings.LogPath))
            {
                ValidateDirectory(settings.LogPath, "Log Path", "Logging");
            }

            // Log file size validation;
            if (settings.MaxLogFileSizeMB < 1 || settings.MaxLogFileSizeMB > 1000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Max log file size out of range",
                    $"Max log file size should be between 1 and 1000 MB. Current: {settings.MaxLogFileSizeMB}", "Logging");
            }

            // Max log files validation;
            if (settings.MaxLogFiles < 1 || settings.MaxLogFiles > 10000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Max log files out of range",
                    $"Max log files should be between 1 and 10000. Current: {settings.MaxLogFiles}", "Logging");
            }

            // Log retention validation;
            if (settings.LogRetentionDays < 1 || settings.LogRetentionDays > 3650)
            {
                AddValidationResult(ValidationSeverity.Warning, "Log retention out of range",
                    $"Log retention should be between 1 and 3650 days (10 years). Current: {settings.LogRetentionDays}", "Logging");
            }

            // Check if any logging is enabled;
            if (!settings.EnableFileLogging && !settings.EnableConsoleLogging &&
                !settings.EnableDatabaseLogging && !settings.EnableEventLogging)
            {
                AddValidationResult(ValidationSeverity.Warning, "No logging output enabled",
                    "All logging outputs are disabled. Consider enabling at least one logging method", "Logging");
            }
        }

        private void ValidateNetworkSettings(NetworkConfig settings)
        {
            if (settings == null) return;

            AddCheck("Network", "Network settings validation");

            // Timeout validation;
            if (settings.TimeoutMs < 1000 || settings.TimeoutMs > 300000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Network timeout out of range",
                    $"Network timeout should be between 1000 and 300000 ms. Current: {settings.TimeoutMs}", "Network");
            }

            // Retry validation;
            if (settings.RetryCount < 0 || settings.RetryCount > 10)
            {
                AddValidationResult(ValidationSeverity.Warning, "Retry count out of range",
                    $"Retry count should be between 0 and 10. Current: {settings.RetryCount}", "Network");
            }

            if (settings.RetryDelayMs < 0 || settings.RetryDelayMs > 30000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Retry delay out of range",
                    $"Retry delay should be between 0 and 30000 ms. Current: {settings.RetryDelayMs}", "Network");
            }

            // Connection validation;
            if (settings.MaxConnections < 1 || settings.MaxConnections > 10000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Max connections out of range",
                    $"Max connections should be between 1 and 10000. Current: {settings.MaxConnections}", "Network");
            }

            if (settings.MaxConnectionsPerIp < 1 || settings.MaxConnectionsPerIp > 1000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Max connections per IP out of range",
                    $"Max connections per IP should be between 1 and 1000. Current: {settings.MaxConnectionsPerIp}", "Network");
            }

            // Cache duration validation;
            if (settings.CacheDurationSeconds < 0 || settings.CacheDurationSeconds > 86400)
            {
                AddValidationResult(ValidationSeverity.Warning, "Cache duration out of range",
                    $"Cache duration should be between 0 and 86400 seconds (24 hours). Current: {settings.CacheDurationSeconds}", "Network");
            }

            // Circuit breaker validation;
            if (settings.EnableCircuitBreaker)
            {
                if (settings.CircuitBreakerThreshold < 1 || settings.CircuitBreakerThreshold > 100)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Circuit breaker threshold out of range",
                        $"Circuit breaker threshold should be between 1 and 100. Current: {settings.CircuitBreakerThreshold}", "Network");
                }

                if (settings.CircuitBreakerTimeoutMs < 1000 || settings.CircuitBreakerTimeoutMs > 300000)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Circuit breaker timeout out of range",
                        $"Circuit breaker timeout should be between 1000 and 300000 ms. Current: {settings.CircuitBreakerTimeoutMs}", "Network");
                }
            }

            // Proxy validation;
            if (settings.EnableProxy)
            {
                if (string.IsNullOrWhiteSpace(settings.ProxyUrl))
                {
                    AddValidationResult(ValidationSeverity.Error, "Proxy URL required",
                        "Proxy URL is required when proxy is enabled", "Network");
                }
                else if (!Uri.TryCreate(settings.ProxyUrl, UriKind.Absolute, out _))
                {
                    AddValidationResult(ValidationSeverity.Error, "Invalid proxy URL",
                        $"Proxy URL '{settings.ProxyUrl}' is not a valid URL", "Network");
                }
            }
        }

        private void ValidatePerformanceSettings(PerformanceConfig settings)
        {
            if (settings == null) return;

            AddCheck("Performance", "Performance settings validation");

            // Cache size validation;
            if (settings.CacheSizeMB < 1 || settings.CacheSizeMB > 100000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Cache size out of range",
                    $"Cache size should be between 1 and 100000 MB. Current: {settings.CacheSizeMB}", "Performance");
            }

            // Thread pool validation;
            if (settings.ThreadPoolSize < 1 || settings.ThreadPoolSize > 1000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Thread pool size out of range",
                    $"Thread pool size should be between 1 and 1000. Current: {settings.ThreadPoolSize}", "Performance");
            }

            // Threshold validation;
            ValidateThreshold(settings.CpuWarningThreshold, settings.CpuCriticalThreshold, "CPU", "Performance");
            ValidateThreshold(settings.MemoryWarningThreshold, settings.MemoryCriticalThreshold, "Memory", "Performance");
            ValidateThreshold(settings.DiskWarningThreshold, settings.DiskCriticalThreshold, "Disk", "Performance");

            // Response time validation;
            if (settings.ResponseTimeWarningMs >= settings.ResponseTimeCriticalMs)
            {
                AddValidationResult(ValidationSeverity.Error, "Invalid response time thresholds",
                    $"Response time warning ({settings.ResponseTimeWarningMs}ms) must be less than critical ({settings.ResponseTimeCriticalMs}ms)", "Performance");
            }

            // GC collection threshold validation;
            if (settings.GcCollectionThreshold < 50 || settings.GcCollectionThreshold > 100)
            {
                AddValidationResult(ValidationSeverity.Warning, "GC collection threshold out of range",
                    $"GC collection threshold should be between 50 and 100. Current: {settings.GcCollectionThreshold}", "Performance");
            }

            // Cache eviction validation;
            if (settings.CacheEvictionPercentage < 1 || settings.CacheEvictionPercentage > 100)
            {
                AddValidationResult(ValidationSeverity.Warning, "Cache eviction percentage out of range",
                    $"Cache eviction percentage should be between 1 and 100. Current: {settings.CacheEvictionPercentage}", "Performance");
            }
        }

        private void ValidateExternalServices(ExternalServicesConfig settings)
        {
            if (settings == null) return;

            AddCheck("External Services", "External services validation");

            // Validate individual services;
            if (settings.Services != null)
            {
                foreach (var service in settings.Services)
                {
                    ValidateExternalService(service.Value, service.Key);
                }
            }

            // Validate API keys if services are configured;
            if (settings.Services?.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(settings.ApiKey) && settings.Services.Any(s =>
                    !string.IsNullOrWhiteSpace(s.Value.Url) && string.IsNullOrWhiteSpace(s.Value.ApiKey)))
                {
                    AddValidationResult(ValidationSeverity.Warning, "Missing API keys for external services",
                        "Some external services have URLs but no API keys configured", "External Services");
                }
            }
        }

        private void ValidateExternalService(ExternalServiceConfig service, string serviceName)
        {
            if (service == null || !service.IsEnabled) return;

            var context = $"External Services.{serviceName}";

            // URL validation;
            if (string.IsNullOrWhiteSpace(service.Url))
            {
                AddValidationResult(ValidationSeverity.Error, "Service URL is required",
                    $"URL is required for service '{serviceName}'", context);
            }
            else if (!Uri.TryCreate(service.Url, UriKind.Absolute, out var uri))
            {
                AddValidationResult(ValidationSeverity.Error, "Invalid service URL",
                    $"URL '{service.Url}' for service '{serviceName}' is not valid", context);
            }
            else if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                AddValidationResult(ValidationSeverity.Warning, "Unsupported URL scheme",
                    $"URL scheme '{uri.Scheme}' for service '{serviceName}' may not be supported", context);
            }

            // Timeout validation;
            if (service.TimeoutMs < 1000 || service.TimeoutMs > 300000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Service timeout out of range",
                    $"Timeout for service '{serviceName}' should be between 1000 and 300000 ms. Current: {service.TimeoutMs}", context);
            }

            // Retry validation;
            if (service.RetryCount < 0 || service.RetryCount > 10)
            {
                AddValidationResult(ValidationSeverity.Warning, "Service retry count out of range",
                    $"Retry count for service '{serviceName}' should be between 0 and 10. Current: {service.RetryCount}", context);
            }
        }

        private void ValidateCacheSettings(CacheConfig settings)
        {
            if (settings == null) return;

            AddCheck("Cache", "Cache settings validation");

            // Memory cache validation;
            if (settings.MemoryCacheSizeMB < 1 || settings.MemoryCacheSizeMB > 100000)
            {
                AddValidationResult(ValidationSeverity.Warning, "Memory cache size out of range",
                    $"Memory cache size should be between 1 and 100000 MB. Current: {settings.MemoryCacheSizeMB}", "Cache");
            }

            // Cache duration validation;
            if (settings.DefaultCacheDurationMinutes < 0 || settings.DefaultCacheDurationMinutes > 10080)
            {
                AddValidationResult(ValidationSeverity.Warning, "Default cache duration out of range",
                    $"Default cache duration should be between 0 and 10080 minutes (1 week). Current: {settings.DefaultCacheDurationMinutes}", "Cache");
            }

            // Distributed cache validation;
            if (settings.EnableDistributedCache)
            {
                if (string.IsNullOrWhiteSpace(settings.DistributedCacheConnectionString))
                {
                    AddValidationResult(ValidationSeverity.Error, "Distributed cache connection string required",
                        "Connection string is required when distributed cache is enabled", "Cache");
                }

                if (settings.DistributedCacheTimeoutMinutes < 0 || settings.DistributedCacheTimeoutMinutes > 10080)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Distributed cache timeout out of range",
                        $"Distributed cache timeout should be between 0 and 10080 minutes (1 week). Current: {settings.DistributedCacheTimeoutMinutes}", "Cache");
                }
            }

            // Validate cache policies;
            if (settings.CachePolicies != null)
            {
                foreach (var policy in settings.CachePolicies)
                {
                    ValidateCachePolicy(policy.Value, policy.Key);
                }
            }
        }

        private void ValidateCachePolicy(CachePolicy policy, string policyName)
        {
            if (policy == null) return;

            var context = $"Cache.Policies.{policyName}";

            if (policy.DurationMinutes < 0 || policy.DurationMinutes > 10080)
            {
                AddValidationResult(ValidationSeverity.Warning, "Cache policy duration out of range",
                    $"Cache policy duration for '{policyName}' should be between 0 and 10080 minutes. Current: {policy.DurationMinutes}", context);
            }

            if (policy.Priority < 0 || policy.Priority > 100)
            {
                AddValidationResult(ValidationSeverity.Warning, "Cache policy priority out of range",
                    $"Cache policy priority for '{policyName}' should be between 0 and 100. Current: {policy.Priority}", context);
            }
        }

        private void ValidateMonitoringSettings(MonitoringConfig settings)
        {
            if (settings == null) return;

            AddCheck("Monitoring", "Monitoring settings validation");

            // Health check validation;
            if (settings.EnableHealthChecks)
            {
                if (settings.HealthCheckIntervalSeconds < 1 || settings.HealthCheckIntervalSeconds > 300)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Health check interval out of range",
                        $"Health check interval should be between 1 and 300 seconds. Current: {settings.HealthCheckIntervalSeconds}", "Monitoring");
                }

                if (settings.HealthCheckTimeoutSeconds < 1 || settings.HealthCheckTimeoutSeconds > 300)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Health check timeout out of range",
                        $"Health check timeout should be between 1 and 300 seconds. Current: {settings.HealthCheckTimeoutSeconds}", "Monitoring");
                }
            }

            // Metrics collection validation;
            if (settings.EnableMetrics)
            {
                if (settings.MetricsCollectionIntervalSeconds < 1 || settings.MetricsCollectionIntervalSeconds > 300)
                {
                    AddValidationResult(ValidationSeverity.Warning, "Metrics collection interval out of range",
                        $"Metrics collection interval should be between 1 and 300 seconds. Current: {settings.MetricsCollectionIntervalSeconds}", "Monitoring");
                }
            }

            // Alert thresholds validation;
            if (settings.AlertThresholds != null)
            {
                foreach (var threshold in settings.AlertThresholds)
                {
                    ValidateAlertThreshold(threshold.Value, threshold.Key);
                }
            }

            // Application Insights validation;
            if (settings.EnableApplicationInsights && string.IsNullOrWhiteSpace(settings.ApplicationInsightsKey))
            {
                AddValidationResult(ValidationSeverity.Warning, "Application Insights key missing",
                    "Application Insights is enabled but no instrumentation key is configured", "Monitoring");
            }
        }

        private void ValidateAlertThreshold(AlertThreshold threshold, string thresholdName)
        {
            if (threshold == null) return;

            var context = $"Monitoring.AlertThresholds.{thresholdName}";

            ValidateThreshold(threshold.Warning, threshold.Critical, thresholdName, context);

            if (threshold.CheckIntervalSeconds < 1 || threshold.CheckIntervalSeconds > 300)
            {
                AddValidationResult(ValidationSeverity.Warning, "Alert check interval out of range",
                    $"Alert check interval for '{thresholdName}' should be between 1 and 300 seconds. Current: {threshold.CheckIntervalSeconds}", context);
            }
        }

        private void ValidateDependencies(AppConfig config)
        {
            AddCheck("Dependencies", "Configuration dependencies validation");

            // Check if AI models require GPU but GPU is not available;
            var gpuModels = config.AIModels?.Where(m => m.UseGPU && m.IsEnabled).ToList();
            if (gpuModels?.Count > 0)
            {
                // This would normally check for GPU availability;
                AddValidationResult(ValidationSeverity.Info, "GPU models configured",
                    $"{gpuModels.Count} AI models are configured to use GPU", "Dependencies");
            }

            // Check if external services are configured but API keys are missing;
            var servicesWithoutKeys = config.ExternalServices?.Services?
                .Where(s => s.Value.IsEnabled && !string.IsNullOrWhiteSpace(s.Value.Url) && string.IsNullOrWhiteSpace(s.Value.ApiKey))
                .ToList();

            if (servicesWithoutKeys?.Count > 0)
            {
                AddValidationResult(ValidationSeverity.Warning, "External services missing API keys",
                    $"{servicesWithoutKeys.Count} external services are missing API keys", "Dependencies");
            }

            // Check cache dependencies;
            if (config.CacheSettings?.CachePolicies != null)
            {
                foreach (var policy in config.CacheSettings.CachePolicies)
                {
                    if (policy.Value?.DependencyKeys != null)
                    {
                        foreach (var dependency in policy.Value.DependencyKeys)
                        {
                            if (!config.CacheSettings.CachePolicies.ContainsKey(dependency))
                            {
                                AddValidationResult(ValidationSeverity.Warning, "Missing cache dependency",
                                    $"Cache policy '{policy.Key}' depends on non-existent policy '{dependency}'", "Dependencies");
                            }
                        }
                    }
                }
            }
        }

        private void ValidateFileSystemAccess(AppConfig config)
        {
            AddCheck("FileSystem", "File system access validation");

            // Check writable directories;
            var directoriesToCheck = new List<string>();

            if (config.ApplicationSettings != null)
            {
                if (!string.IsNullOrWhiteSpace(config.ApplicationSettings.DataDirectory))
                    directoriesToCheck.Add(config.ApplicationSettings.DataDirectory);

                if (!string.IsNullOrWhiteSpace(config.ApplicationSettings.LogDirectory))
                    directoriesToCheck.Add(config.ApplicationSettings.LogDirectory);

                if (!string.IsNullOrWhiteSpace(config.ApplicationSettings.TempDirectory))
                    directoriesToCheck.Add(config.ApplicationSettings.TempDirectory);
            }

            if (config.LoggingSettings != null && !string.IsNullOrWhiteSpace(config.LoggingSettings.LogPath))
            {
                directoriesToCheck.Add(config.LoggingSettings.LogPath);
            }

            // Check each directory;
            foreach (var directory in directoriesToCheck.Distinct())
            {
                try
                {
                    // Check if directory exists or can be created;
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        AddValidationResult(ValidationSeverity.Info, "Directory created",
                            $"Directory created: {directory}", "FileSystem");
                    }

                    // Check write permission;
                    var testFile = Path.Combine(directory, $"test_{Guid.NewGuid():N}.tmp");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);

                    AddValidationResult(ValidationSeverity.Info, "Directory is writable",
                        $"Directory is writable: {directory}", "FileSystem");
                }
                catch (Exception ex)
                {
                    AddValidationResult(ValidationSeverity.Error, "Directory access error",
                        $"Cannot access directory '{directory}': {ex.Message}", "FileSystem");
                }
            }

            // Check AI model files;
            if (config.AIModels != null)
            {
                foreach (var model in config.AIModels.Where(m => m.IsEnabled))
                {
                    if (!model.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var modelDirectory = Path.GetDirectoryName(model.Path);
                            if (!string.IsNullOrWhiteSpace(modelDirectory) && !Directory.Exists(modelDirectory))
                            {
                                AddValidationResult(ValidationSeverity.Warning, "Model directory not found",
                                    $"Model directory does not exist: {modelDirectory}", "FileSystem");
                            }
                        }
                        catch (Exception ex)
                        {
                            AddValidationResult(ValidationSeverity.Warning, "Model path validation error",
                                $"Error validating model path '{model.Path}': {ex.Message}", "FileSystem");
                        }
                    }
                }
            }
        }

        private void ValidateEnvironmentSpecificRules(AppConfig config)
        {
            AddCheck("Environment", "Environment-specific rules validation");

            if (config.ApplicationSettings == null) return;

            var environment = config.ApplicationSettings.Environment;

            switch (environment)
            {
                case EnvironmentType.Production:
                    // Production-specific validations;
                    if (!config.SecuritySettings.RequireHttps)
                    {
                        AddValidationResult(ValidationSeverity.Error, "HTTPS not required in production",
                            "HTTPS should be required in production environment", "Environment");
                    }

                    if (config.ApplicationSettings.DebugMode)
                    {
                        AddValidationResult(ValidationSeverity.Error, "Debug mode enabled in production",
                            "Debug mode should be disabled in production environment", "Environment");
                    }

                    if (config.LoggingSettings.LogLevel <= LogLevel.Debug)
                    {
                        AddValidationResult(ValidationSeverity.Warning, "Debug logging in production",
                            "Consider using Information or higher log level in production", "Environment");
                    }

                    // Check for default/weak secrets;
                    if (config.SecuritySettings.JwtSecret?.Contains("secret") == true ||
                        config.SecuritySettings.JwtSecret?.Length < 64)
                    {
                        AddValidationResult(ValidationSeverity.Error, "Weak JWT secret in production",
                            "Production environment requires strong JWT secret (at least 64 random characters)", "Environment");
                    }

                    break;

                case EnvironmentType.Development:
                    // Development-specific validations;
                    if (config.SecuritySettings.RequireHttps && config.ApplicationSettings.BaseUrl?.Contains("localhost") == true)
                    {
                        AddValidationResult(ValidationSeverity.Warning, "HTPS with localhost",
                            "HTTPS with localhost may require certificate setup", "Environment");
                    }

                    if (!config.ApplicationSettings.DebugMode)
                    {
                        AddValidationResult(ValidationSeverity.Info, "Debug mode disabled in development",
                            "Debug mode is typically enabled in development environment", "Environment");
                    }

                    break;

                case EnvironmentType.Staging:
                    // Staging-specific validations;
                    if (!config.SecuritySettings.RequireHttps)
                    {
                        AddValidationResult(ValidationSeverity.Warning, "HTTPS not required in staging",
                            "Staging environment should mirror production, consider enabling HTTPS", "Environment");
                    }

                    break;
            }
        }

        private void ValidateProductionRules(AppConfig config)
        {
            // Additional production-only validations;
            if (config.ExternalServices?.Services != null)
            {
                var prodServices = config.ExternalServices.Services
                    .Where(s => s.Value.IsEnabled && s.Value.Url?.Contains("localhost") == true)
                    .ToList();

                if (prodServices.Count > 0)
                {
                    AddValidationResult(ValidationSeverity.Error, "Localhost services in production",
                        $"Production environment should not use localhost services: {string.Join(", ", prodServices.Select(s => s.Key))}", "Environment");
                }
            }

            // Check for development database connections;
            if (config.DatabaseSettings?.ConnectionString?.Contains("localhost") == true ||
                config.DatabaseSettings?.ConnectionString?.Contains("(localdb)") == true)
            {
                AddValidationResult(ValidationSeverity.Error, "Development database in production",
                    "Production environment should use production database servers", "Environment");
            }
        }

        private void ValidateStagingRules(AppConfig config)
        {
            // Staging should closely mirror production;
            if (config.ApplicationSettings?.BaseUrl?.Contains("staging") != true)
            {
                AddValidationResult(ValidationSeverity.Info, "Staging URL check",
                    "Consider using 'staging' in the base URL for staging environment", "Environment");
            }
        }

        private void ValidateDevelopmentRules(AppConfig config)
        {
            // Development environment warnings;
            if (config.SecuritySettings?.JwtSecret?.Length > 64)
            {
                AddValidationResult(ValidationSeverity.Info, "Strong secret in development",
                    "Consider using shorter secrets for development to improve startup time", "Environment");
            }
        }

        private void ValidateTestingRules(AppConfig config)
        {
            // Testing environment validations;
            if (!config.DatabaseSettings?.EnableSeedData ?? false)
            {
                AddValidationResult(ValidationSeverity.Warning, "Seed data disabled in testing",
                    "Testing environment typically uses seed data for test scenarios", "Environment");
            }
        }

        private void ValidateConfigurationFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    AddValidationResult(ValidationSeverity.Error, "Configuration file not found",
                        $"File not found: {filePath}", "FileSystem");
                    return;
                }

                var fileInfo = new FileInfo(filePath);

                // Check file size;
                if (fileInfo.Length == 0)
                {
                    AddValidationResult(ValidationSeverity.Error, "Configuration file is empty",
                        $"File is empty: {filePath}", "FileSystem");
                }
                else if (fileInfo.Length > 10485760) // 10MB;
                {
                    AddValidationResult(ValidationSeverity.Warning, "Configuration file is large",
                        $"Configuration file exceeds 10MB: {filePath}", "FileSystem");
                }

                // Check file extension;
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                var validExtensions = new[] { ".json", ".config", ".xml", ".yaml", ".yml" };
                if (!validExtensions.Contains(extension))
                {
                    AddValidationResult(ValidationSeverity.Warning, "Unusual configuration file extension",
                        $"File has unusual extension: {filePath}", "FileSystem");
                }

                // Try to read the file;
                var content = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(content))
                {
                    AddValidationResult(ValidationSeverity.Error, "Configuration file content is empty",
                        $"File contains only whitespace: {filePath}", "FileSystem");
                }

                AddValidationResult(ValidationSeverity.Info, "Configuration file is valid",
                    $"File validated: {filePath}", "FileSystem");
            }
            catch (UnauthorizedAccessException ex)
            {
                AddValidationResult(ValidationSeverity.Error, "File access denied",
                    $"Access denied to file: {filePath}. Error: {ex.Message}", "FileSystem");
            }
            catch (IOException ex)
            {
                AddValidationResult(ValidationSeverity.Error, "File IO error",
                    $"IO error reading file: {filePath}. Error: {ex.Message}", "FileSystem");
            }
            catch (Exception ex)
            {
                AddValidationResult(ValidationSeverity.Error, "File validation error",
                    $"Error validating file: {filePath}. Error: {ex.Message}", "FileSystem");
            }
        }

        private void ValidateFilePermissions(string directoryPath)
        {
            try
            {
                // Check read permission;
                Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);

                // Check write permission;
                var testFile = Path.Combine(directoryPath, $"permission_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                AddValidationResult(ValidationSeverity.Info, "Directory permissions are valid",
                    $"Directory has read/write permissions: {directoryPath}", "FileSystem");
            }
            catch (UnauthorizedAccessException ex)
            {
                AddValidationResult(ValidationSeverity.Error, "Directory permission error",
                    $"Insufficient permissions for directory: {directoryPath}. Error: {ex.Message}", "FileSystem");
            }
            catch (Exception ex)
            {
                AddValidationResult(ValidationSeverity.Error, "Directory permission check failed",
                    $"Failed to check directory permissions: {directoryPath}. Error: {ex.Message}", "FileSystem");
            }
        }

        private void ValidateFileIntegrity(string directoryPath)
        {
            try
            {
                var configFiles = Directory.GetFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(directoryPath, "*.config", SearchOption.TopDirectoryOnly))
                    .ToList();

                foreach (var file in configFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(file);

                        // Simple JSON validation;
                        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                System.Text.Json.JsonDocument.Parse(content);
                                AddValidationResult(ValidationSeverity.Info, "JSON file is valid",
                                    $"JSON file is valid: {Path.GetFileName(file)}", "FileSystem");
                            }
                            catch (System.Text.Json.JsonException ex)
                            {
                                AddValidationResult(ValidationSeverity.Error, "Invalid JSON file",
                                    $"JSON file is invalid: {Path.GetFileName(file)}. Error: {ex.Message}", "FileSystem");
                            }
                        }

                        // Check for sensitive data;
                        if (content.Contains("Password=") || content.Contains("password=") ||
                            content.Contains("Secret=") || content.Contains("secret="))
                        {
                            AddValidationResult(ValidationSeverity.Warning, "Potential sensitive data in config file",
                                $"Configuration file may contain sensitive data: {Path.GetFileName(file)}", "FileSystem");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddValidationResult(ValidationSeverity.Error, "File integrity check failed",
                            $"Failed to check file integrity: {Path.GetFileName(file)}. Error: {ex.Message}", "FileSystem");
                    }
                }
            }
            catch (Exception ex)
            {
                AddValidationResult(ValidationSeverity.Error, "Directory integrity check failed",
                    $"Failed to check directory integrity: {directoryPath}. Error: {ex.Message}", "FileSystem");
            }
        }

        #endregion

        #region Helper Validation Methods

        private void ValidateRequiredSection(object section, string sectionName)
        {
            if (section == null)
            {
                AddValidationResult(ValidationSeverity.Error, $"Missing required section: {sectionName}",
                    $"{sectionName} configuration section is required", "Structure");
            }
        }

        private void ValidateRequiredString(string value, string fieldName, string context)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddValidationResult(ValidationSeverity.Error, $"Missing required field: {fieldName}",
                    $"{fieldName} is required in {context}", context);
            }
        }

        private void ValidateDirectory(string path, string directoryName, string context)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                // Check if path contains invalid characters;
                if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    AddValidationResult(ValidationSeverity.Error, $"Invalid characters in {directoryName}",
                        $"{directoryName} contains invalid characters: {path}", context);
                    return;
                }

                // Check path length;
                if (path.Length > Constants.FileSystem.MaxPathLength)
                {
                    AddValidationResult(ValidationSeverity.Warning, $"{directoryName} path is too long",
                        $"{directoryName} path exceeds maximum length: {path}", context);
                }
            }
            catch (Exception ex)
            {
                AddValidationResult(ValidationSeverity.Error, $"Error validating {directoryName}",
                    $"Error validating {directoryName} '{path}': {ex.Message}", context);
            }
        }

        private void ValidateThreshold(double warning, double critical, string metricName, string context)
        {
            if (warning >= critical)
            {
                AddValidationResult(ValidationSeverity.Error, $"Invalid {metricName} thresholds",
                    $"{metricName} warning threshold ({warning}) must be less than critical threshold ({critical})", context);
            }

            if (warning < 0 || warning > 100)
            {
                AddValidationResult(ValidationSeverity.Error, $"{metricName} warning threshold out of range",
                    $"{metricName} warning threshold must be between 0 and 100. Current: {warning}", context);
            }

            if (critical < 0 || critical > 100)
            {
                AddValidationResult(ValidationSeverity.Error, $"{metricName} critical threshold out of range",
                    $"{metricName} critical threshold must be between 0 and 100. Current: {critical}", context);
            }
        }

        private void AddCheck(string category, string description)
        {
            Summary.TotalChecksPerformed++;
            _logger.LogDebug("Validation check: {Category} - {Description}", category, description);
        }

        private void AddValidationResult(ValidationSeverity severity, string title, string message, string category)
        {
            var result = new ValidationResult
            {
                Severity = severity,
                Title = title,
                Message = message,
                Category = category,
                Timestamp = DateTime.UtcNow
            };

            _validationResults.Add(result);

            // Log based on severity;
            switch (severity)
            {
                case ValidationSeverity.Error:
                    _logger.LogError("Validation Error [{Category}]: {Title} - {Message}", category, title, message);
                    break;
                case ValidationSeverity.Warning:
                    _logger.LogWarning("Validation Warning [{Category}]: {Title} - {Message}", category, title, message);
                    break;
                case ValidationSeverity.Info:
                    _logger.LogInformation("Validation Info [{Category}]: {Title} - {Message}", category, title, message);
                    break;
            }
        }

        #endregion
    }

    #region Supporting Classes and Interfaces

    /// <summary>
    /// Interface for configuration validator;
    /// </summary>
    public interface IConfigValidator
    {
        /// <summary>
        /// Validates the entire application configuration;
        /// </summary>
        ValidationResult Validate(AppConfig config, ValidationMode mode = ValidationMode.Strict);

        /// <summary>
        /// Validates configuration files on disk;
        /// </summary>
        ValidationResult ValidateConfigurationFiles(string configPath, IEnumerable<string> requiredFiles);

        /// <summary>
        /// Validates configuration against environment-specific rules;
        /// </summary>
        ValidationResult ValidateForEnvironment(AppConfig config, EnvironmentType environment);

        /// <summary>
        /// Gets detailed validation report;
        /// </summary>
        ValidationReport GetValidationReport();

        /// <summary>
        /// Clears all validation results;
        /// </summary>
        void ClearResults();

        /// <summary>
        /// Gets a value indicating whether validation was successful;
        /// </summary>
        bool IsValid { get; }

        /// <summary>
        /// Gets all validation results;
        /// </summary>
        IReadOnlyList<ValidationResult> ValidationResults { get; }

        /// <summary>
        /// Gets the validation summary;
        /// </summary>
        ValidationSummary Summary { get; }
    }

    /// <summary>
    /// Validation mode enumeration;
    /// </summary>
    public enum ValidationMode
    {
        /// <summary>
        /// Strict validation - errors will throw exceptions;
        /// </summary>
        Strict = 0,

        /// <summary>
        /// Lenient validation - errors will be logged but not thrown;
        /// </summary>
        Lenient = 1,

        /// <summary>
        /// Warning only mode - only warnings are reported;
        /// </summary>
        WarningOnly = 2,

        /// <summary>
        /// Quick validation - only critical validations are performed;
        /// </summary>
        Quick = 3
    }

    /// <summary>
    /// Validation severity enumeration;
    /// </summary>
    public enum ValidationSeverity
    {
        /// <summary>
        /// Informational message;
        /// </summary>
        Info = 0,

        /// <summary>
        /// Warning message;
        /// </summary>
        Warning = 1,

        /// <summary>
        /// Error message;
        /// </summary>
        Error = 2
    }

    /// <summary>
    /// Validation result for individual checks;
    /// </summary>
    public class ValidationResult
    {
        /// <summary>
        /// Gets or sets the validation severity;
        /// </summary>
        public ValidationSeverity Severity { get; set; }

        /// <summary>
        /// Gets or sets the validation title;
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Gets or sets the detailed validation message;
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Gets or sets the validation category;
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Gets or sets the timestamp of the validation;
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the validation passed;
        /// </summary>
        public bool IsValid { get; set; } = true;
    }

    /// <summary>
    /// Validation summary for the entire validation process;
    /// </summary>
    public class ValidationSummary
    {
        /// <summary>
        /// Gets or sets the start time of validation;
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// Gets or sets the end time of validation;
        /// </summary>
        public DateTime EndTime { get; set; }

        /// <summary>
        /// Gets or sets the duration of validation;
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets the total number of checks performed;
        /// </summary>
        public int TotalChecksPerformed { get; set; }

        /// <summary>
        /// Gets or sets the total number of errors found;
        /// </summary>
        public int TotalErrors { get; set; }

        /// <summary>
        /// Gets or sets the total number of warnings found;
        /// </summary>
        public int TotalWarnings { get; set; }

        /// <summary>
        /// Gets or sets the total number of informational messages;
        /// </summary>
        public int TotalInfo { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the configuration is valid;
        /// </summary>
        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Comprehensive validation report;
    /// </summary>
    public class ValidationReport
    {
        /// <summary>
        /// Gets or sets the validation summary;
        /// </summary>
        public ValidationSummary Summary { get; set; }

        /// <summary>
        /// Gets or sets all validation results;
        /// </summary>
        public List<ValidationResult> Results { get; set; }

        /// <summary>
        /// Gets or sets the report generation timestamp;
        /// </summary>
        public DateTime GeneratedAt { get; set; }

        /// <summary>
        /// Gets or sets the validator version;
        /// </summary>
        public string ValidatorVersion { get; set; }

        /// <summary>
        /// Gets the errors from the validation results;
        /// </summary>
        public List<ValidationResult> Errors =>
            Results?.Where(r => r.Severity == ValidationSeverity.Error).ToList() ?? new List<ValidationResult>();

        /// <summary>
        /// Gets the warnings from the validation results;
        /// </summary>
        public List<ValidationResult> Warnings =>
            Results?.Where(r => r.Severity == ValidationSeverity.Warning).ToList() ?? new List<ValidationResult>();

        /// <summary>
        /// Gets the informational messages from the validation results;
        /// </summary>
        public List<ValidationResult> Informational =>
            Results?.Where(r => r.Severity == ValidationSeverity.Info).ToList() ?? new List<ValidationResult>();

        /// <summary>
        /// Creates a formatted string representation of the report;
        /// </summary>
        public override string ToString()
        {
            return $"Validation Report - Generated: {GeneratedAt:yyyy-MM-dd HH:mm:ss}, Valid: {Summary?.IsValid}, " +
                   $"Errors: {Errors.Count}, Warnings: {Warnings.Count}, Duration: {Summary?.Duration:hh\\:mm\\:ss}";
        }
    }

    /// <summary>
    /// Custom exception for configuration validation failures;
    /// </summary>
    public class ConfigurationValidationException : Exception
    {
        /// <summary>
        /// Gets the validation results that caused the exception;
        /// </summary>
        public IReadOnlyList<ValidationResult> ValidationResults { get; }

        public ConfigurationValidationException() { }

        public ConfigurationValidationException(string message) : base(message) { }

        public ConfigurationValidationException(string message, Exception inner) : base(message, inner) { }

        public ConfigurationValidationException(string message, IEnumerable<ValidationResult> validationResults)
            : base(message)
        {
            ValidationResults = validationResults?.ToList() ?? new List<ValidationResult>();
        }

        /// <summary>
        /// Gets formatted error messages from validation results;
        /// </summary>
        public string GetFormattedErrors()
        {
            if (ValidationResults == null || !ValidationResults.Any())
                return Message;

            var errors = ValidationResults
                .Where(r => r.Severity == ValidationSeverity.Error)
                .Select(r => $"[{r.Category}] {r.Title}: {r.Message}");

            return $"{Message}\n\nErrors:\n{string.Join("\n", errors)}";
        }
    }

    #endregion
}
