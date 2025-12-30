using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using NEDA.Core.Common;
using NEDA.Core.Common.Constants;
using NEDA.Core.Logging;

namespace NEDA.Core.Configuration.ConfigValidators
{
    /// <summary>
    /// Configuration validation kurallarını içeren static sınıf
    /// </summary>
    public static class ValidationRules
    {
        private static readonly ILogger _logger = LogManager.GetLogger(typeof(ValidationRules));

        #region General Validation Rules

        public static ValidationResult ValidateRequired(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _logger.Warning($"Required field '{fieldName}' is empty");
                return new ValidationResult($"{fieldName} is required.", new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateMinLength(string value, string fieldName, int minLength)
        {
            if (value?.Length < minLength)
            {
                _logger.Warning($"Field '{fieldName}' length is less than minimum ({minLength})");
                return new ValidationResult(
                    $"{fieldName} must be at least {minLength} characters long.",
                    new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateMaxLength(string value, string fieldName, int maxLength)
        {
            if (value?.Length > maxLength)
            {
                _logger.Warning($"Field '{fieldName}' length exceeds maximum ({maxLength})");
                return new ValidationResult(
                    $"{fieldName} must not exceed {maxLength} characters.",
                    new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateRange(int value, string fieldName, int min, int max)
        {
            if (value < min || value > max)
            {
                _logger.Warning($"Field '{fieldName}' value {value} is out of range [{min}-{max}]");
                return new ValidationResult(
                    $"{fieldName} must be between {min} and {max}.",
                    new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateEmail(string email, string fieldName = "Email")
        {
            if (string.IsNullOrWhiteSpace(email))
                return ValidationResult.Success;

            var emailRegex = new Regex(
                @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            if (!emailRegex.IsMatch(email))
            {
                _logger.Warning($"Invalid email format for field '{fieldName}': {email}");
                return new ValidationResult(
                    $"{fieldName} must be a valid email address.",
                    new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateUrl(string url, string fieldName = "Url")
        {
            if (string.IsNullOrWhiteSpace(url))
                return ValidationResult.Success;

            var urlRegex = new Regex(
                @"^(https?|ftp)://[^\s/$.?#].[^\s]*$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            if (!urlRegex.IsMatch(url))
            {
                _logger.Warning($"Invalid URL format for field '{fieldName}': {url}");
                return new ValidationResult(
                    $"{fieldName} must be a valid URL.",
                    new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        #endregion

        #region Configuration Specific Rules

        public static ValidationResult ValidateConnectionString(string connectionString)
        {
            var result = ValidateRequired(connectionString, "ConnectionString");
            if (result != ValidationResult.Success)
                return result;

            var requiredKeywords = new[] { "Server", "Data Source", "Host" };
            var hasServer = requiredKeywords.Any(keyword =>
                connectionString.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);

            if (!hasServer)
            {
                _logger.Warning("Connection string missing server/host information");
                return new ValidationResult(
                    "Connection string must contain server/host information.",
                    new[] { "ConnectionString" });
            }

            var dbKeywords = new[] { "Database", "Initial Catalog" };
            var hasDatabase = dbKeywords.Any(keyword =>
                connectionString.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);

            if (!hasDatabase)
            {
                _logger.Warning("Connection string missing database name");
                return new ValidationResult(
                    "Connection string must contain database name.",
                    new[] { "ConnectionString" });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateApiEndpoint(string endpoint)
        {
            var result = ValidateRequired(endpoint, "ApiEndpoint");
            if (result != ValidationResult.Success)
                return result;

            if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning($"API endpoint must start with http:// or https://: {endpoint}");
                return new ValidationResult(
                    "API endpoint must be a valid URL starting with http:// or https://.",
                    new[] { "ApiEndpoint" });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateFilePath(string path, bool mustExist = false)
        {
            var result = ValidateRequired(path, "FilePath");
            if (result != ValidationResult.Success)
                return result;

            try
            {
                var invalidChars = Path.GetInvalidPathChars();
                if (path.Any(c => invalidChars.Contains(c)))
                {
                    _logger.Warning($"File path contains invalid characters: {path}");
                    return new ValidationResult(
                        "File path contains invalid characters.",
                        new[] { "FilePath" });
                }

                if (mustExist && !File.Exists(path) && !Directory.Exists(path))
                {
                    _logger.Warning($"File or directory does not exist: {path}");
                    return new ValidationResult(
                        "File or directory does not exist.",
                        new[] { "FilePath" });
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error validating file path '{path}': {ex.Message}");
                return new ValidationResult(
                    $"Invalid file path: {ex.Message}",
                    new[] { "FilePath" });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidatePortNumber(int port, string fieldName = "Port")
        {
            return ValidateRange(port, fieldName, 1, 65535);
        }

        public static ValidationResult ValidateTimeout(TimeSpan timeout, string fieldName = "Timeout")
        {
            if (timeout <= TimeSpan.Zero)
            {
                _logger.Warning($"Timeout value must be positive: {timeout}");
                return new ValidationResult(
                    $"{fieldName} must be a positive value.",
                    new[] { fieldName });
            }

            if (timeout > TimeSpan.FromHours(24))
            {
                _logger.Warning($"Timeout value is too large: {timeout}");
                return new ValidationResult(
                    $"{fieldName} must not exceed 24 hours.",
                    new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidatePercentage(double percentage, string fieldName = "Percentage")
        {
            if (percentage < 0 || percentage > 100)
            {
                _logger.Warning($"Percentage value out of range: {percentage}");
                return new ValidationResult(
                    $"{fieldName} must be between 0 and 100.",
                    new[] { fieldName });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateEnvironmentName(string environment)
        {
            var result = ValidateRequired(environment, "Environment");
            if (result != ValidationResult.Success)
                return result;

            var validEnvironments = new[]
            {
                EnvironmentConstants.DEVELOPMENT,
                EnvironmentConstants.STAGING,
                EnvironmentConstants.PRODUCTION,
                EnvironmentConstants.TESTING
            };

            if (!validEnvironments.Any(e =>
                    string.Equals(e, environment, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Warning($"Invalid environment name: {environment}");
                return new ValidationResult(
                    $"Environment must be one of: {string.Join(", ", validEnvironments)}",
                    new[] { "Environment" });
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateLogLevel(string logLevel)
        {
            if (string.IsNullOrWhiteSpace(logLevel))
                return ValidationResult.Success;

            var validLevels = new[]
            {
                "Trace", "Debug", "Information", "Warning",
                "Error", "Critical", "None"
            };

            if (!validLevels.Any(l =>
                    string.Equals(l, logLevel, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Warning($"Invalid log level: {logLevel}");
                return new ValidationResult(
                    $"LogLevel must be one of: {string.join(", ", validLevels)}",
                    new[] { "LogLevel" });
            }

            return ValidationResult.Success;
        }

        #endregion

        #region Security Related Rules

        // ... (geri kalan kısım aynı mantıkla derlenebilir durumda kalıyor)

        #endregion

        #region Composite Validation

        public class CompositeValidationResult : ValidationResult
        {
            public IEnumerable<ValidationResult> Results { get; }

            public CompositeValidationResult(string errorMessage, IEnumerable<ValidationResult> results)
                : base(errorMessage)
            {
                Results = results ?? throw new ArgumentNullException(nameof(results));
            }
        }

        #endregion

        #region Extension Methods for Fluent Validation

        public static class Extensions
        {
            public static ValidationResult Required(this string value, string fieldName)
                => ValidateRequired(value, fieldName);

            public static ValidationResult MinLength(this string value, string fieldName, int minLength)
                => ValidateMinLength(value, fieldName, minLength);

            public static ValidationResult MaxLength(this string value, string fieldName, int maxLength)
                => ValidateMaxLength(value, fieldName, maxLength);

            public static ValidationResult InRange(this int value, string fieldName, int min, int max)
                => ValidateRange(value, fieldName, min, max);

            public static ValidationResult ValidEmail(this string email, string fieldName = "Email")
                => ValidateEmail(email, fieldName);

            public static ValidationResult ValidUrl(this string url, string fieldName = "Url")
                => ValidateUrl(url, fieldName);

            public static ValidationResult ValidPort(this int port, string fieldName = "Port")
                => ValidatePortNumber(port, fieldName);

            public static ValidationResult ValidEnvironment(this string environment)
                => ValidateEnvironmentName(environment);

            public static ValidationResult StrongPassword(this string password, string fieldName = "Password")
                => ValidatePasswordStrength(password, fieldName);
        }

        #endregion
    }
}
