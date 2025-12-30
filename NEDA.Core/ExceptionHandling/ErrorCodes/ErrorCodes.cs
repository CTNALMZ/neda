using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace NEDA.Core.ExceptionHandling.ErrorCodes
{
    /// <summary>
    /// NEDA Sistem Hata Kodları - Merkezi hata kodu yönetimi;
    /// HTTP status kodları ve özel hata kodlarını içerir;
    /// </summary>
    public static class ErrorCodes
    {
        #region Common Error Categories

        /// <summary>
        /// Hata kategori tipleri;
        /// </summary>
        public enum ErrorCategory
        {
            [Description("System Errors")]
            System = 1000,

            [Description("Security Errors")]
            Security = 2000,

            [Description("Application Errors")]
            Application = 3000,

            [Description("Data Errors")]
            Data = 4000,

            [Description("Network Errors")]
            Network = 5000,

            [Description("AI/ML Errors")]
            AI = 6000,

            [Description("Integration Errors")]
            Integration = 7000,

            [Description("Validation Errors")]
            Validation = 8000,

            [Description("Resource Errors")]
            Resource = 9000,

            [Description("Business Logic Errors")]
            Business = 10000
        }

        /// <summary>
        /// Hata şiddet seviyeleri;
        /// </summary>
        public enum ErrorSeverity
        {
            [Description("Informational")]
            Info = 0,

            [Description("Warning")]
            Warning = 1,

            [Description("Error")]
            Error = 2,

            [Description("Critical")]
            Critical = 3,

            [Description("Fatal")]
            Fatal = 4
        }

        #endregion

        #region System Errors (1000-1999)

        /// <summary>
        /// Sistem seviyesi hata kodları;
        /// </summary>
        public static class System
        {
            // Genel Sistem Hataları;
            public const int SYSTEM_INITIALIZATION_FAILED = 1001;
            public const int SYSTEM_SHUTDOWN_FAILED = 1002;
            public const int SYSTEM_NOT_INITIALIZED = 1003;
            public const int SYSTEM_CONFIGURATION_INVALID = 1004;
            public const int SYSTEM_RESOURCE_EXHAUSTED = 1005;
            public const int SYSTEM_MAINTENANCE_MODE = 1006;
            public const int SYSTEM_UPGRADE_IN_PROGRESS = 1007;

            // Runtime Hataları;
            public const int RUNTIME_EXCEPTION = 1010;
            public const int OUT_OF_MEMORY = 1011;
            public const int STACK_OVERFLOW = 1012;
            public const int NULL_REFERENCE = 1013;
            public const int INVALID_OPERATION = 1014;
            public const int NOT_IMPLEMENTED = 1015;
            public const int NOT_SUPPORTED = 1016;

            // Threading/Concurrency Hataları;
            public const int DEADLOCK_DETECTED = 1020;
            public const int THREAD_ABORTED = 1021;
            public const int TASK_CANCELLED = 1022;
            public const int CONCURRENCY_VIOLATION = 1023;
            public const int TIMEOUT_EXPIRED = 1024;

            // IO Hataları;
            public const int IO_EXCEPTION = 1030;
            public const int FILE_NOT_FOUND = 1031;
            public const int DIRECTORY_NOT_FOUND = 1032;
            public const int PATH_TOO_LONG = 1033;
            public const int ACCESS_DENIED = 1034;
            public const int DISK_FULL = 1035;
            public const int IO_TIMEOUT = 1036;

            // Memory Hataları;
            public const int MEMORY_ALLOCATION_FAILED = 1040;
            public const int MEMORY_CORRUPTION = 1041;
            public const int BUFFER_OVERFLOW = 1042;
            public const int MEMORY_LEAK_DETECTED = 1043;

            // Process Hataları;
            public const int PROCESS_START_FAILED = 1050;
            public const int PROCESS_TERMINATED = 1051;
            public const int PROCESS_HANG_DETECTED = 1052;

            // Service Hataları;
            public const int SERVICE_NOT_RUNNING = 1060;
            public const int SERVICE_START_FAILED = 1061;
            public const int SERVICE_STOP_FAILED = 1062;

            // Registry/Configuration Hataları;
            public const int REGISTRY_ACCESS_DENIED = 1070;
            public const int CONFIG_FILE_CORRUPT = 1071;
            public const int ENVIRONMENT_VARIABLE_MISSING = 1072;
        }

        #endregion

        #region Security Errors (2000-2999)

        /// <summary>
        /// Güvenlik ve yetkilendirme hata kodları;
        /// </summary>
        public static class Security
        {
            // Authentication Hataları;
            public const int AUTHENTICATION_FAILED = 2001;
            public const int INVALID_CREDENTIALS = 2002;
            public const int ACCOUNT_LOCKED = 2003;
            public const int ACCOUNT_EXPIRED = 2004;
            public const int PASSWORD_EXPIRED = 2005;
            public const int TOKEN_EXPIRED = 2006;
            public const int INVALID_TOKEN = 2007;
            public const int TOKEN_NOT_PROVIDED = 2008;
            public const int MULTI_FACTOR_REQUIRED = 2009;
            public const int BIOMETRIC_AUTH_FAILED = 2010;

            // Authorization Hataları;
            public const int AUTHORIZATION_DENIED = 2020;
            public const int INSUFFICIENT_PERMISSIONS = 2021;
            public const int ROLE_NOT_ASSIGNED = 2022;
            public const int PERMISSION_DENIED = 2023;
            public const int ACCESS_RESTRICTED = 2024;
            public const int IP_BLOCKED = 2025;
            public const int GEO_BLOCKED = 2026;
            public const int RATE_LIMIT_EXCEEDED = 2027;

            // Encryption Hataları;
            public const int ENCRYPTION_FAILED = 2030;
            public const int DECRYPTION_FAILED = 2031;
            public const int INVALID_ENCRYPTION_KEY = 2032;
            public const int KEY_NOT_FOUND = 2033;
            public const int CERTIFICATE_EXPIRED = 2034;
            public const int CERTIFICATE_INVALID = 2035;
            public const int SSL_HANDSHAKE_FAILED = 2036;
            public const int HASH_VALIDATION_FAILED = 2037;

            // Security Policy Hataları;
            public const int POLICY_VIOLATION = 2040;
            public const int PASSWORD_POLICY_VIOLATION = 2041;
            public const int SECURITY_PROTOCOL_VIOLATION = 2042;
            public const int COMPLIANCE_VIOLATION = 2043;

            // Threat Detection Hataları;
            public const int SUSPICIOUS_ACTIVITY = 2050;
            public const int INTRUSION_DETECTED = 2051;
            public const int MALWARE_DETECTED = 2052;
            public const int BRUTE_FORCE_ATTEMPT = 2053;
            public const int DDOS_ATTACK_DETECTED = 2054;

            // Audit Hataları;
            public const int AUDIT_LOG_FAILED = 2060;
            public const int AUDIT_TRAIL_CORRUPTED = 2061;
        }

        #endregion

        #region Application Errors (3000-3999)

        public static class Application
        {
            public const int UI_RENDERING_FAILED = 3001;
            public const int VIEW_NOT_FOUND = 3002;
            public const int VIEW_MODEL_ERROR = 3003;
            public const int BINDING_FAILED = 3004;
            public const int CONTROL_INITIALIZATION_FAILED = 3005;
            public const int THEME_LOAD_FAILED = 3006;

            public const int BUSINESS_RULE_VIOLATION = 3010;
            public const int INVALID_BUSINESS_STATE = 3011;
            public const int WORKFLOW_VIOLATION = 3012;
            public const int PROCESSING_FAILED = 3013;
            public const int VALIDATION_FAILED = 3014;

            public const int SESSION_EXPIRED = 3020;
            public const int SESSION_NOT_FOUND = 3021;
            public const int SESSION_CORRUPTED = 3022;
            public const int CONCURRENT_SESSION_LIMIT = 3023;

            public const int CACHE_READ_FAILED = 3030;
            public const int CACHE_WRITE_FAILED = 3031;
            public const int CACHE_INVALIDATION_FAILED = 3032;
            public const int CACHE_SIZE_EXCEEDED = 3033;

            public const int LOCALIZATION_FAILED = 3040;
            public const int CULTURE_NOT_SUPPORTED = 3041;
            public const int RESOURCE_NOT_FOUND = 3042;

            public const int PLUGIN_LOAD_FAILED = 3050;
            public const int PLUGIN_INITIALIZATION_FAILED = 3051;
            public const int PLUGIN_COMPATIBILITY_ERROR = 3052;
            public const int PLUGIN_DEPENDENCY_MISSING = 3053;
        }

        #endregion

        #region Data Errors (4000-4999)

        public static class Data
        {
            public const int DATABASE_CONNECTION_FAILED = 4001;
            public const int DATABASE_TIMEOUT = 4002;
            public const int CONNECTION_POOL_EXHAUSTED = 4003;
            public const int DATABASE_UNAVAILABLE = 4004;

            public const int SQL_EXCEPTION = 4010;
            public const int INVALID_QUERY = 4011;
            public const int QUERY_TIMEOUT = 4012;
            public const int DEADLOCK_DETECTED = 4013;
            public const int TRANSACTION_FAILED = 4014;
            public const int CONSTRAINT_VIOLATION = 4015;

            public const int DATA_CORRUPTION = 4020;
            public const int REFERENTIAL_INTEGRITY_VIOLATION = 4021;
            public const int DUPLICATE_KEY = 4022;
            public const int FOREIGN_KEY_VIOLATION = 4023;
            public const int CHECK_CONSTRAINT_VIOLATION = 4024;

            public const int RECORD_NOT_FOUND = 4030;
            public const int MULTIPLE_RECORDS_FOUND = 4031;
            public const int CONCURRENCY_CONFLICT = 4032;
            public const int OPTIMISTIC_CONCURRENCY_VIOLATION = 4033;

            public const int MIGRATION_FAILED = 4040;
            public const int SCHEMA_VERSION_MISMATCH = 4041;
            public const int MIGRATION_ROLLBACK_FAILED = 4042;

            public const int BACKUP_FAILED = 4050;
            public const int RESTORE_FAILED = 4051;
            public const int BACKUP_CORRUPTED = 4052;

            public const int FILE_FORMAT_INVALID = 4060;
            public const int DATA_PARSING_FAILED = 4061;
            public const int INVALID_DATA_FORMAT = 4062;
            public const int DATA_VALIDATION_FAILED = 4063;

            public const int SEARCH_INDEX_CORRUPTED = 4070;
            public const int INDEX_REBUILD_FAILED = 4071;
            public const int FULL_TEXT_SEARCH_FAILED = 4072;
        }

        #endregion

        #region Network Errors (5000-5999)

        public static class Network
        {
            public const int NETWORK_UNAVAILABLE = 5001;
            public const int CONNECTION_REFUSED = 5002;
            public const int CONNECTION_TIMEOUT = 5003;
            public const int HOST_NOT_FOUND = 5004;
            public const int HOST_UNREACHABLE = 5005;
            public const int NETWORK_RESET = 5006;

            public const int PROTOCOL_ERROR = 5010;
            public const int INVALID_PROTOCOL_VERSION = 5011;
            public const int PROTOCOL_VIOLATION = 5012;

            public const int HTTP_ERROR = 5020;
            public const int HTTP_TIMEOUT = 5021;
            public const int HTTP_REDIRECT_LIMIT = 5022;
            public const int HTTP_PROTOCOL_VIOLATION = 5023;

            public const int WEBSOCKET_CONNECTION_FAILED = 5030;
            public const int WEBSOCKET_HANDSHAKE_FAILED = 5031;
            public const int WEBSOCKET_PROTOCOL_ERROR = 5032;

            public const int DNS_RESOLUTION_FAILED = 5040;
            public const int DNS_SERVER_UNAVAILABLE = 5041;

            public const int FIREWALL_BLOCKED = 5050;
            public const int PROXY_AUTHENTICATION_FAILED = 5051;
            public const int PROXY_CONNECTION_FAILED = 5052;

            public const int BANDWIDTH_EXCEEDED = 5060;
            public const int NETWORK_CONGESTION = 5061;
        }

        #endregion

        #region AI/ML Errors (6000-6999)

        public static class AI
        {
            public const int AI_ENGINE_INITIALIZATION_FAILED = 6001;
            public const int AI_ENGINE_NOT_INITIALIZED = 6002;
            public const int AI_PROCESSING_FAILED = 6003;
            public const int AI_MODEL_LOAD_FAILED = 6004;
            public const int AI_ENGINE_OVERLOADED = 6005;

            public const int MODEL_NOT_FOUND = 6010;
            public const int MODEL_VERSION_MISMATCH = 6011;
            public const int MODEL_CORRUPTED = 6012;
            public const int MODEL_FORMAT_INVALID = 6013;
            public const int MODEL_DEPENDENCY_MISSING = 6014;

            public const int TRAINING_FAILED = 6020;
            public const int TRAINING_DATA_INSUFFICIENT = 6021;
            public const int TRAINING_DATA_CORRUPTED = 6022;
            public const int TRAINING_TIMEOUT = 6023;
            public const int HYPERPARAMETER_TUNING_FAILED = 6024;
            public const int OVERFITTING_DETECTED = 6025;
            public const int UNDERFITTING_DETECTED = 6026;

            public const int INFERENCE_FAILED = 6030;
            public const int INFERENCE_TIMEOUT = 6031;
            public const int INFERENCE_MEMORY_EXCEEDED = 6032;
            public const int INFERENCE_ACCURACY_TOO_LOW = 6033;

            public const int NLP_PROCESSING_FAILED = 6040;
            public const int LANGUAGE_NOT_SUPPORTED = 6041;
            public const int TEXT_PARSING_FAILED = 6042;
            public const int INTENT_RECOGNITION_FAILED = 6043;
            public const int ENTITY_EXTRACTION_FAILED = 6044;
            public const int SENTIMENT_ANALYSIS_FAILED = 6045;

            public const int IMAGE_PROCESSING_FAILED = 6050;
            public const int OBJECT_DETECTION_FAILED = 6051;
            public const int FACE_RECOGNITION_FAILED = 6052;
            public const int OPTICAL_CHARACTER_RECOGNITION_FAILED = 6053;

            public const int NEURAL_NETWORK_INITIALIZATION_FAILED = 6060;
            public const int NEURAL_NETWORK_TRAINING_FAILED = 6061;
            public const int GRADIENT_EXPLOSION = 6062;
            public const int GRADIENT_VANISHING = 6063;
            public const int ACTIVATION_FUNCTION_ERROR = 6064;

            public const int RL_AGENT_FAILED = 6070;
            public const int RL_ENVIRONMENT_ERROR = 6071;
            public const int RL_REWARD_FUNCTION_INVALID = 6072;

            public const int AI_ETHICS_VIOLATION = 6080;
            public const int BIAS_DETECTED = 6081;
            public const int FAIRNESS_VIOLATION = 6082;
            public const int EXPLAINABILITY_FAILED = 6083;
        }

        #endregion

        #region Integration Errors (7000-7999)

        public static class Integration
        {
            public const int API_CALL_FAILED = 7001;
            public const int API_RATE_LIMIT_EXCEEDED = 7002;
            public const int API_VERSION_NOT_SUPPORTED = 7003;
            public const int API_AUTHENTICATION_FAILED = 7004;
            public const int API_RESPONSE_INVALID = 7005;
            public const int API_TIMEOUT = 7006;

            public const int SERVICE_UNAVAILABLE = 7010;
            public const int SERVICE_BUSY = 7011;
            public const int SERVICE_DEPRECATED = 7012;

            public const int MESSAGE_QUEUE_CONNECTION_FAILED = 7020;
            public const int MESSAGE_PUBLISH_FAILED = 7021;
            public const int MESSAGE_CONSUME_FAILED = 7022;
            public const int QUEUE_FULL = 7023;
            public const int MESSAGE_FORMAT_INVALID = 7024;

            public const int EVENT_PUBLISH_FAILED = 7030;
            public const int EVENT_HANDLER_FAILED = 7031;
            public const int EVENT_SUBSCRIPTION_FAILED = 7032;

            public const int SOAP_FAULT = 7040;
            public const int WSDL_VALIDATION_FAILED = 7041;

            public const int FTP_CONNECTION_FAILED = 7050;
            public const int SFTP_AUTHENTICATION_FAILED = 7051;
            public const int FILE_UPLOAD_FAILED = 7052;
            public const int FILE_DOWNLOAD_FAILED = 7053;

            public const int DATABASE_REPLICATION_FAILED = 7060;
            public const int DATABASE_SYNC_FAILED = 7061;

            public const int THIRD_PARTY_SERVICE_FAILED = 7070;
            public const int THIRD_PARTY_API_CHANGED = 7071;
            public const int LICENSE_EXPIRED = 7072;
        }

        #endregion

        #region Validation Errors (8000-8999)

        public static class Validation
        {
            public const int INPUT_VALIDATION_FAILED = 8001;
            public const int REQUIRED_FIELD_MISSING = 8002;
            public const int INVALID_FORMAT = 8003;
            public const int OUT_OF_RANGE = 8004;
            public const int INVALID_TYPE = 8005;
            public const int PATTERN_MISMATCH = 8006;
            public const int LENGTH_EXCEEDED = 8007;

            public const int BUSINESS_VALIDATION_FAILED = 8010;
            public const int INVALID_BUSINESS_ID = 8011;
            public const int DUPLICATE_ENTRY = 8012;
            public const int CONFLICTING_DATA = 8013;
            public const int INVALID_TRANSITION = 8014;

            public const int CROSS_FIELD_VALIDATION_FAILED = 8020;
            public const int FIELD_MISMATCH = 8021;
            public const int CONSISTENCY_VIOLATION = 8022;

            public const int DATA_VALIDATION_FAILED = 8030;
            public const int INVALID_DATE_RANGE = 8031;
            public const int INVALID_CURRENCY = 8032;
            public const int INVALID_PHONE_NUMBER = 8033;
            public const int INVALID_EMAIL = 8034;
            public const int INVALID_URL = 8035;

            public const int SECURITY_VALIDATION_FAILED = 8040;
            public const int INVALID_SIGNATURE = 8041;
            public const int CERTIFICATE_VALIDATION_FAILED = 8042;
            public const int SANITIZATION_FAILED = 8043;
        }

        #endregion

        #region Resource Errors (9000-9999)

        public static class Resource
        {
            public const int MEMORY_EXHAUSTED = 9001;
            public const int MEMORY_ALLOCATION_FAILED = 9002;
            public const int GC_PRESSURE_HIGH = 9003;

            public const int CPU_OVERLOADED = 9010;
            public const int THREAD_POOL_EXHAUSTED = 9011;
            public const int PROCESS_CPU_LIMIT_EXCEEDED = 9012;

            public const int DISK_SPACE_INSUFFICIENT = 9020;
            public const int DISK_IO_OVERLOADED = 9021;
            public const int FILE_HANDLE_EXHAUSTED = 9022;

            public const int NETWORK_BUFFER_EXHAUSTED = 9030;
            public const int SOCKET_EXHAUSTED = 9031;
            public const int BANDWIDTH_EXCEEDED = 9032;

            public const int GPU_MEMORY_EXHAUSTED = 9040;
            public const int GPU_OVERHEATED = 9041;
            public const int GPU_DRIVER_FAILED = 9042;

            public const int DATABASE_CONNECTION_LIMIT = 9050;
            public const int DATABASE_LOCK_TIMEOUT = 9051;
            public const int DATABASE_TEMP_SPACE_EXHAUSTED = 9052;

            public const int EXTERNAL_RESOURCE_UNAVAILABLE = 9060;
            public const int LICENSE_LIMIT_EXCEEDED = 9061;
            public const int QUOTA_EXCEEDED = 9062;
        }

        #endregion

        #region Business Logic Errors (10000-10999)

        public static class Business
        {
            public const int USER_NOT_FOUND = 10001;
            public const int USER_ALREADY_EXISTS = 10002;
            public const int USER_PROFILE_INCOMPLETE = 10003;
            public const int USER_SUSPENDED = 10004;

            public const int PROJECT_NOT_FOUND = 10010;
            public const int PROJECT_ACCESS_DENIED = 10011;
            public const int PROJECT_LIMIT_EXCEEDED = 10012;
            public const int PROJECT_INVALID_STATE = 10013;

            public const int FILE_OPERATION_FAILED = 10020;
            public const int FILE_SIZE_EXCEEDED = 10021;
            public const int FILE_TYPE_NOT_SUPPORTED = 10022;
            public const int FILE_VERSION_CONFLICT = 10023;

            public const int WORKFLOW_EXECUTION_FAILED = 10030;
            public const int WORKFLOW_STATE_INVALID = 10031;
            public const int WORKFLOW_TRANSITION_INVALID = 10032;
            public const int WORKFLOW_TIMEOUT = 10033;

            public const int NOTIFICATION_FAILED = 10040;
            public const int NOTIFICATION_TEMPLATE_NOT_FOUND = 10041;
            public const int NOTIFICATION_CHANNEL_UNAVAILABLE = 10042;

            public const int PAYMENT_FAILED = 10050;
            public const int INSUFFICIENT_BALANCE = 10051;
            public const int PAYMENT_GATEWAY_ERROR = 10052;
            public const int INVOICE_GENERATION_FAILED = 10053;

            public const int SUBSCRIPTION_EXPIRED = 10060;
            public const int SUBSCRIPTION_LIMIT_EXCEEDED = 10061;
            public const int SUBSCRIPTION_NOT_FOUND = 10062;

            public const int CONTENT_NOT_FOUND = 10070;
            public const int CONTENT_VALIDATION_FAILED = 10071;
            public const int CONTENT_PROCESSING_FAILED = 10072;

            public const int ANALYTICS_PROCESSING_FAILED = 10080;
            public const int REPORT_GENERATION_FAILED = 10081;
            public const int DATA_AGGREGATION_FAILED = 10082;
        }

        #endregion

        #region HTTP Status Code Mappings

        public static class HttpStatusMappings
        {
            private static readonly Dictionary<int, int> _httpToErrorCode = new()
            {
                { 400, Validation.INPUT_VALIDATION_FAILED },
                { 401, Security.AUTHENTICATION_FAILED },
                { 403, Security.AUTHORIZATION_DENIED },
                { 404, Business.USER_NOT_FOUND },
                { 405, System.NOT_SUPPORTED },
                { 408, Network.CONNECTION_TIMEOUT },
                { 409, Validation.CONFLICTING_DATA },
                { 429, Security.RATE_LIMIT_EXCEEDED },

                { 500, System.RUNTIME_EXCEPTION },
                { 502, Network.CONNECTION_REFUSED },
                { 503, Integration.SERVICE_UNAVAILABLE },
                { 504, Network.CONNECTION_TIMEOUT },
                { 507, Resource.DISK_SPACE_INSUFFICIENT }
            };

            public static int GetErrorCodeFromHttpStatus(int httpStatusCode)
            {
                return _httpToErrorCode.TryGetValue(httpStatusCode, out var errorCode)
                    ? errorCode
                    : System.RUNTIME_EXCEPTION;
            }

            public static int GetHttpStatusFromErrorCode(int errorCode)
            {
                var category = GetErrorCategory(errorCode);

                return category switch
                {
                    ErrorCategory.Validation => 400,
                    ErrorCategory.Security => 401,
                    ErrorCategory.Application => 400,
                    ErrorCategory.Data => 400,
                    ErrorCategory.Network => 503,
                    ErrorCategory.AI => 500,
                    ErrorCategory.Integration => 502,
                    ErrorCategory.Resource => 503,
                    ErrorCategory.Business => 400,
                    _ => 500
                };
            }
        }

        #endregion

        #region Utility Methods

        public static ErrorCategory GetErrorCategory(int errorCode)
        {
            return errorCode switch
            {
                >= 1000 and < 2000 => ErrorCategory.System,
                >= 2000 and < 3000 => ErrorCategory.Security,
                >= 3000 and < 4000 => ErrorCategory.Application,
                >= 4000 and < 5000 => ErrorCategory.Data,
                >= 5000 and < 6000 => ErrorCategory.Network,
                >= 6000 and < 7000 => ErrorCategory.AI,
                >= 7000 and < 8000 => ErrorCategory.Integration,
                >= 8000 and < 9000 => ErrorCategory.Validation,
                >= 9000 and < 10000 => ErrorCategory.Resource,
                >= 10000 and < 11000 => ErrorCategory.Business,
                _ => ErrorCategory.System
            };
        }

        public static ErrorSeverity GetErrorSeverity(int errorCode)
        {
            var criticalErrors = new HashSet<int>
            {
                System.OUT_OF_MEMORY,
                System.STACK_OVERFLOW,
                System.DEADLOCK_DETECTED,
                Security.INTRUSION_DETECTED,
                Security.MALWARE_DETECTED,
                Resource.MEMORY_EXHAUSTED,
                Resource.CPU_OVERLOADED,
                AI.AI_ENGINE_INITIALIZATION_FAILED
            };

            if (criticalErrors.Contains(errorCode))
                return ErrorSeverity.Critical;

            var fatalErrors = new HashSet<int>
            {
                System.SYSTEM_INITIALIZATION_FAILED,
                System.MEMORY_CORRUPTION,
                Security.DDOS_ATTACK_DETECTED
            };

            if (fatalErrors.Contains(errorCode))
                return ErrorSeverity.Fatal;

            var category = GetErrorCategory(errorCode);

            return category switch
            {
                ErrorCategory.System => ErrorSeverity.Error,
                ErrorCategory.Security => ErrorSeverity.Error,
                ErrorCategory.Resource => ErrorSeverity.Critical,
                _ => ErrorSeverity.Error
            };
        }

        public static string GetErrorDescription(int errorCode)
        {
            return ErrorMessages.ResourceManager.GetString($"ERROR_{errorCode}")
                ?? "An unexpected error occurred";
        }

        public static string GetErrorTechnicalDetails(int errorCode)
        {
            var category = GetErrorCategory(errorCode);
            var severity = GetErrorSeverity(errorCode);

            return $"{category} Error {errorCode} - Severity: {severity}";
        }

        public static string GetErrorResolution(int errorCode)
        {
            return errorCode switch
            {
                System.SYSTEM_NOT_INITIALIZED => "Initialize the system before using",
                System.OUT_OF_MEMORY => "Free up memory or increase system memory",
                System.TIMEOUT_EXPIRED => "Increase timeout settings or optimize operation",

                Security.AUTHENTICATION_FAILED => "Check credentials and try again",
                Security.TOKEN_EXPIRED => "Refresh authentication token",
                Security.RATE_LIMIT_EXCEEDED => "Wait before making more requests",

                Resource.MEMORY_EXHAUSTED => "Close unused applications or add more memory",
                Resource.DISK_SPACE_INSUFFICIENT => "Free up disk space",

                Network.CONNECTION_TIMEOUT => "Check network connection and retry",
                Network.NETWORK_UNAVAILABLE => "Verify network connectivity",

                _ => "Contact system administrator for assistance"
            };
        }

        public static Microsoft.Extensions.Logging.LogLevel GetLogLevel(int errorCode)
        {
            var severity = GetErrorSeverity(errorCode);

            return severity switch
            {
                ErrorSeverity.Info => Microsoft.Extensions.Logging.LogLevel.Information,
                ErrorSeverity.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
                ErrorSeverity.Error => Microsoft.Extensions.Logging.LogLevel.Error,
                ErrorSeverity.Critical => Microsoft.Extensions.Logging.LogLevel.Critical,
                ErrorSeverity.Fatal => Microsoft.Extensions.Logging.LogLevel.Critical,
                _ => Microsoft.Extensions.Logging.LogLevel.Error
            };
        }

        public static IEnumerable<ErrorCodeInfo> GetAllErrorCodes()
        {
            var errorCodes = new List<ErrorCodeInfo>();

            AddErrorCodesFromClass(typeof(System), errorCodes);
            AddErrorCodesFromClass(typeof(Security), errorCodes);
            AddErrorCodesFromClass(typeof(Application), errorCodes);
            AddErrorCodesFromClass(typeof(Data), errorCodes);
            AddErrorCodesFromClass(typeof(Network), errorCodes);
            AddErrorCodesFromClass(typeof(AI), errorCodes);
            AddErrorCodesFromClass(typeof(Integration), errorCodes);
            AddErrorCodesFromClass(typeof(Validation), errorCodes);
            AddErrorCodesFromClass(typeof(Resource), errorCodes);
            AddErrorCodesFromClass(typeof(Business), errorCodes);

            return errorCodes;
        }

        private static void AddErrorCodesFromClass(Type errorClass, List<ErrorCodeInfo> errorCodes)
        {
            var fields = errorClass.GetFields(global::System.Reflection.BindingFlags.Public |
                                             global::System.Reflection.BindingFlags.Static);

            foreach (var field in fields)
            {
                if (field.FieldType == typeof(int))
                {
                    var value = (int)field.GetValue(null)!;
                    errorCodes.Add(new ErrorCodeInfo
                    {
                        Code = value,
                        Name = field.Name,
                        Category = GetErrorCategory(value),
                        Severity = GetErrorSeverity(value),
                        Description = GetErrorDescription(value),
                        TechnicalDetails = GetErrorTechnicalDetails(value),
                        Resolution = GetErrorResolution(value)
                    });
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Hata kodu bilgisi;
    /// </summary>
    public class ErrorCodeInfo
    {
        public int Code { get; set; }
        public string Name { get; set; } = string.Empty;
        public ErrorCodes.ErrorCategory Category { get; set; }
        public ErrorCodes.ErrorSeverity Severity { get; set; }
        public string Description { get; set; } = string.Empty;
        public string TechnicalDetails { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
    }

    /// <summary>
    /// Eğer .resx ile auto-generated ErrorMessages sınıfın yoksa,
    /// bu minimal stub compile hatasını çözer. (GetString null dönebilir.)
    /// </summary>
    internal static class ErrorMessages
    {
        public static global::System.Resources.ResourceManager ResourceManager { get; }
            = new global::System.Resources.ResourceManager(
                "NEDA.Core.ExceptionHandling.ErrorCodes.ErrorMessages",
                typeof(ErrorMessages).Assembly);
    }
}
