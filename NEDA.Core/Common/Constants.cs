using System;
using System.Collections.Generic;

namespace NEDA.Core.Common
{
    /// <summary>
    /// NEDA Core Constants - Contains all application-wide constants, enums, and configuration values;
    /// </summary>
    public static class Constants
    {
        #region Application Constants

        /// <summary>
        /// Application name and version constants;
        /// </summary>
        public static class Application
        {
            public const string Name = "NEDA - Neuro-Evolutionary Digital Assistant";
            public const string CodeName = "Project Aegis";
            public const string Version = "1.0.0";
            public const string BuildNumber = "2024.1.0.1";
            public const string Company = "NeuroTech Innovations";
            public const string Copyright = "© 2024 NeuroTech Innovations. All rights reserved.";

            public const int MajorVersion = 1;
            public const int MinorVersion = 0;
            public const int PatchVersion = 0;
            public const int BuildVersion = 1;

            public static readonly DateTime ReleaseDate = new DateTime(2024, 1, 15);
            public const string LicenseType = "Enterprise";
            public const string SupportEmail = "support@neda.ai";
            public const string DocumentationUrl = "https://docs.neda.ai";
            public const string ApiBaseUrl = "https://api.neda.ai/v1";
        }

        #endregion

        #region Engine Constants

        /// <summary>
        /// AI Engine related constants;
        /// </summary>
        public static class Engine
        {
            public const int DefaultBatchSize = 32;
            public const int MaxBatchSize = 256;
            public const int MinBatchSize = 1;

            public const double DefaultLearningRate = 0.001;
            public const double MinLearningRate = 0.00001;
            public const double MaxLearningRate = 0.1;

            public const int DefaultTrainingEpochs = 100;
            public const int MaxTrainingEpochs = 1000;
            public const int MinTrainingEpochs = 1;

            public const int DefaultInferenceTimeout = 30000; // 30 seconds;
            public const int MaxInferenceTimeout = 300000; // 5 minutes;
            public const int MinInferenceTimeout = 1000; // 1 second;

            public const int DefaultThreadPoolSize = 4;
            public const int MaxThreadPoolSize = 64;
            public const int MinThreadPoolSize = 1;

            public const string DefaultModelType = "NeuralNetwork";
            public const string FallbackModelType = "DecisionTree";

            public const int MemoryCacheSize = 1024 * 1024 * 100; // 100MB;
            public const int ModelCacheSize = 1024 * 1024 * 500; // 500MB;

            public const double ConfidenceThreshold = 0.8;
            public const double UncertaintyThreshold = 0.3;

            public const int MaxRetryAttempts = 3;
            public const int RetryDelayMs = 1000;
        }

        /// <summary>
        /// Processing engine constants;
        /// </summary>
        public static class Processing
        {
            public const int DefaultQueueSize = 1000;
            public const int MaxQueueSize = 10000;
            public const int MinQueueSize = 10;

            public const int MaxConcurrentOperations = 10;
            public const int MinConcurrentOperations = 1;

            public const int ChunkSize = 1024 * 4; // 4KB;
            public const int BufferSize = 81920; // 80KB;

            public const int DefaultTimeoutMs = 60000; // 1 minute;
            public const int QuickTimeoutMs = 5000; // 5 seconds;
            public const int ExtendedTimeoutMs = 300000; // 5 minutes;

            public const string DefaultEncoding = "UTF-8";
            public const string BinaryEncoding = "ISO-8859-1";

            public const int MaxFileSize = 1024 * 1024 * 100; // 100MB;
            public const int MaxMemoryAllocation = 1024 * 1024 * 1024; // 1GB;
        }

        #endregion

        #region Security Constants

        /// <summary>
        /// Security and authentication constants;
        /// </summary>
        public static class Security
        {
            public const int MinPasswordLength = 12;
            public const int MaxPasswordLength = 128;
            public const int PasswordHistoryCount = 5;

            public const int TokenExpiryHours = 24;
            public const int RefreshTokenExpiryDays = 30;
            public const int SessionTimeoutMinutes = 30;

            public const int MaxLoginAttempts = 5;
            public const int LockoutDurationMinutes = 15;

            public const int SaltSize = 32; // 256 bits;
            public const int KeySize = 256; // AES-256;
            public const int Iterations = 10000;

            public const string DefaultHashAlgorithm = "SHA512";
            public const string EncryptionAlgorithm = "AES-256-CBC";
            public const string HashingAlgorithm = "HMACSHA512";

            public const int ApiKeyLength = 64;
            public const int SessionTokenLength = 128;

            public const string JwtIssuer = "NEDA.Auth";
            public const string JwtAudience = "NEDA.API";

            public const int CertificateValidityYears = 2;
            public const int KeyRotationDays = 90;

            public const int MaxConcurrentSessions = 5;
            public const int GeolocationCheckRadiusKm = 100;
        }

        /// <summary>
        /// Permission and role constants;
        /// </summary>
        public static class Permissions
        {
            public const string SuperAdmin = "SuperAdministrator";
            public const string Admin = "Administrator";
            public const string Developer = "Developer";
            public const string User = "User";
            public const string Guest = "Guest";
            public const string System = "System";

            public const string Read = "Read";
            public const string Write = "Write";
            public const string Execute = "Execute";
            public const string Delete = "Delete";
            public const string Administer = "Administer";

            public const string AllPermissions = "*";
            public const string DenyAll = "None";

            public static readonly Dictionary<string, string[]> RolePermissions = new Dictionary<string, string[]>
            {
                [SuperAdmin] = new[] { AllPermissions },
                [Admin] = new[] { Read, Write, Execute, Delete },
                [Developer] = new[] { Read, Write, Execute },
                [User] = new[] { Read, Write },
                [Guest] = new[] { Read }
            };
        }

        #endregion

        #region File System Constants

        /// <summary>
        /// File system and storage constants;
        /// </summary>
        public static class FileSystem
        {
            public const int MaxPathLength = 260;
            public const int MaxFileNameLength = 255;

            public const string ConfigExtension = ".config";
            public const string LogExtension = ".log";
            public const string BackupExtension = ".bak";
            public const string TempExtension = ".tmp";
            public const string LockExtension = ".lock";

            public const string ConfigDirectory = "Config";
            public const string LogDirectory = "Logs";
            public const string DataDirectory = "Data";
            public const string CacheDirectory = "Cache";
            public const string TempDirectory = "Temp";
            public const string BackupDirectory = "Backup";
            public const string PluginDirectory = "Plugins";
            public const string ModelDirectory = "Models";

            public const long MaxLogFileSize = 1024 * 1024 * 10; // 10MB;
            public const int MaxLogFiles = 100;

            public const int FileBufferSize = 4096;
            public const int StreamCopyBufferSize = 81920;

            public static readonly string[] AllowedImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff" };
            public static readonly string[] AllowedDocumentExtensions = { ".txt", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };
            public static readonly string[] AllowedCodeExtensions = { ".cs", ".js", ".py", ".java", ".cpp", ".h", ".html", ".css", ".xml", ".json" };
            public static readonly string[] AllowedMediaExtensions = { ".mp3", ".mp4", ".wav", ".avi", ".mov", ".wmv" };

            public const string DefaultEncoding = "UTF-8";
            public const string BinaryFileSignature = "NEDA";
        }

        #endregion

        #region Network Constants

        /// <summary>
        /// Network and communication constants;
        /// </summary>
        public static class Network
        {
            public const int DefaultPort = 8080;
            public const int SecurePort = 8443;
            public const int ApiPort = 5000;
            public const int GrpcPort = 50051;

            public const int MaxConnections = 1000;
            public const int MaxConnectionsPerIp = 50;
            public const int ConnectionTimeout = 30000; // 30 seconds;
            public const int KeepAliveInterval = 30000; // 30 seconds;

            public const int MaxMessageSize = 1024 * 1024 * 10; // 10MB;
            public const int ChunkSize = 8192; // 8KB;

            public const int HttpTimeout = 30000; // 30 seconds;
            public const int RetryCount = 3;
            public const int RetryDelay = 1000; // 1 second;

            public const string UserAgent = "NEDA-Core/1.0";
            public const string ContentTypeJson = "application/json";
            public const string ContentTypeXml = "application/xml";
            public const string ContentTypeBinary = "application/octet-stream";
            public const string ContentTypeForm = "application/x-www-form-urlencoded";

            public const string DefaultApiVersion = "v1";
            public const string ApiKeyHeader = "X-API-Key";
            public const string AuthHeader = "Authorization";
            public const string CorrelationIdHeader = "X-Correlation-ID";

            public const int DnsCacheTimeout = 300000; // 5 minutes;
            public const int SocketBufferSize = 8192;
        }

        #endregion

        #region Database Constants

        /// <summary>
        /// Database and storage constants;
        /// </summary>
        public static class Database
        {
            public const int DefaultCommandTimeout = 30; // 30 seconds;
            public const int LongCommandTimeout = 300; // 5 minutes;

            public const int MaxPoolSize = 100;
            public const int MinPoolSize = 5;
            public const int ConnectionLifetime = 300; // 5 minutes;

            public const int BatchSize = 1000;
            public const int MaxParameters = 2100;

            public const string DefaultSchema = "dbo";
            public const string SystemSchema = "sys";
            public const string AuditSchema = "audit";

            public const int MaxStringLength = 4000;
            public const int MaxTextFieldLength = 8000;
            public const int MaxBinaryLength = 8000;

            public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
            public const string DateFormat = "yyyy-MM-dd";
            public const string TimeFormat = "HH:mm:ss";

            public const int TransactionTimeout = 300; // 5 minutes;
            public const int DeadlockRetryCount = 3;
            public const int DeadlockRetryDelay = 100; // 100ms;

            public const string IsolationLevel = "ReadCommitted";
            public const string ConnectionStringTemplate = "Server={0};Database={1};User Id={2};Password={3};";
        }

        #endregion

        #region Performance Constants

        /// <summary>
        /// Performance monitoring and optimization constants;
        /// </summary>
        public static class Performance
        {
            public const int SamplingInterval = 1000; // 1 second;
            public const int HistorySize = 3600; // 1 hour of samples;

            public const double CpuWarningThreshold = 80.0;
            public const double CpuCriticalThreshold = 95.0;

            public const double MemoryWarningThreshold = 85.0;
            public const double MemoryCriticalThreshold = 95.0;

            public const double DiskWarningThreshold = 90.0;
            public const double DiskCriticalThreshold = 95.0;

            public const int NetworkWarningThreshold = 80; // 80% utilization;
            public const int NetworkCriticalThreshold = 95; // 95% utilization;

            public const int ResponseTimeWarning = 1000; // 1 second;
            public const int ResponseTimeCritical = 5000; // 5 seconds;

            public const int GcCollectionThreshold = 85; // Trigger GC at 85% memory usage;
            public const int CacheEvictionPercentage = 20; // Evict 20% when full;

            public const int ThreadPoolMinThreads = 10;
            public const int ThreadPoolMaxThreads = 100;

            public const int ObjectPoolSize = 100;
            public const int ConnectionPoolSize = 100;
        }

        #endregion

        #region Validation Constants

        /// <summary>
        /// Data validation and sanitization constants;
        /// </summary>
        public static class Validation
        {
            public const int MinUsernameLength = 3;
            public const int MaxUsernameLength = 50;

            public const int MinEmailLength = 5;
            public const int MaxEmailLength = 254;

            public const string EmailRegex = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
            public const string PhoneRegex = @"^\+?[\d\s\-\(\)]{10,}$";
            public const string UrlRegex = @"^(https?:\/\/)?([\da-z\.-]+)\.([a-z\.]{2,6})([\/\w \.-]*)*\/?$";

            public const int MinNameLength = 1;
            public const int MaxNameLength = 100;

            public const double MinLatitude = -90.0;
            public const double MaxLatitude = 90.0;
            public const double MinLongitude = -180.0;
            public const double MaxLongitude = 180.0;

            public const decimal MinMoneyAmount = 0.0m;
            public const decimal MaxMoneyAmount = 1000000000.0m; // 1 billion;

            public const int MaxListSize = 10000;
            public const int MaxStringArrayLength = 1000;

            public const string AllowedCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.@";
            public const string SafeHtmlRegex = @"<[^>]+>|&[^;]+;";

            public const int MaxJsonDepth = 64;
            public const int MaxXmlDepth = 32;
        }

        #endregion

        #region AI Model Constants

        /// <summary>
        /// AI model specific constants;
        /// </summary>
        public static class AIModels
        {
            public const string DefaultModel = "NEDA-Core-Model-v1";
            public const string VisionModel = "NEDA-Vision-Model-v1";
            public const string LanguageModel = "NEDA-Language-Model-v1";
            public const string AudioModel = "NEDA-Audio-Model-v1";
            public const string DecisionModel = "NEDA-Decision-Model-v1";

            public const int InputVectorSize = 256;
            public const int HiddenLayerSize = 512;
            public const int OutputVectorSize = 128;

            public const float DropoutRate = 0.2f;
            public const float LearningRateDecay = 0.95f;
            public const float GradientClipNorm = 1.0f;

            public const int AttentionHeads = 8;
            public const int TransformerLayers = 6;
            public const int EmbeddingSize = 768;

            public const int MaxSequenceLength = 512;
            public const int VocabularySize = 50000;

            public const string DefaultTokenizer = "WordPiece";
            public const string DefaultOptimizer = "Adam";
            public const string DefaultLossFunction = "CrossEntropy";

            public const float BatchNormMomentum = 0.99f;
            public const float LabelSmoothing = 0.1f;
            public const float WeightDecay = 0.01f;
        }

        #endregion

        #region Error Code Constants

        /// <summary>
        /// Application error codes;
        /// </summary>
        public static class ErrorCodes
        {
            // System Errors (1000-1999)
            public const int SystemError = 1000;
            public const int ConfigurationError = 1001;
            public const int InitializationError = 1002;
            public const int ResourceExhausted = 1003;
            public const int ServiceUnavailable = 1004;
            public const int TimeoutError = 1005;
            public const int ConnectionError = 1006;
            public const int FileSystemError = 1007;
            public const int MemoryAllocationError = 1008;
            public const int ThreadPoolExhausted = 1009;

            // Security Errors (2000-2999)
            public const int AuthenticationFailed = 2000;
            public const int AuthorizationFailed = 2001;
            public const int InvalidToken = 2002;
            public const int TokenExpired = 2003;
            public const int AccessDenied = 2004;
            public const int RateLimitExceeded = 2005;
            public const int InvalidCredentials = 2006;
            public const int AccountLocked = 2007;
            public const int SessionExpired = 2008;
            public const int SecurityViolation = 2009;

            // Validation Errors (3000-3999)
            public const int ValidationError = 3000;
            public const int InvalidInput = 3001;
            public const int MissingRequiredField = 3002;
            public const int TypeMismatch = 3003;
            public const int OutOfRange = 3004;
            public const int FormatError = 3005;
            public const int DuplicateEntry = 3006;
            public const int ConstraintViolation = 3007;
            public const int BusinessRuleViolation = 3008;
            public const int InvalidState = 3009;

            // Database Errors (4000-4999)
            public const int DatabaseError = 4000;
            public const int ConnectionFailed = 4001;
            public const int QueryFailed = 4002;
            public const int TransactionFailed = 4003;
            public const int DeadlockDetected = 4004;
            public const int ConstraintFailed = 4005;
            public const int RecordNotFound = 4006;
            public const int ConcurrentModification = 4007;
            public const int DatabaseTimeout = 4008;
            public const int MigrationError = 4009;

            // AI Engine Errors (5000-5999)
            public const int AIEngineError = 5000;
            public const int ModelNotFound = 5001;
            public const int InferenceFailed = 5002;
            public const int TrainingFailed = 5003;
            public const int ModelValidationFailed = 5004;
            public const int InsufficientData = 5005;
            public const int PredictionError = 5006;
            public const int FeatureExtractionError = 5007;
            public const int ModelSerializationError = 5008;
            public const int GPUError = 5009;

            // Network Errors (6000-6999)
            public const int NetworkError = 6000;
            public const int HttpError = 6001;
            public const int SocketError = 6002;
            public const int DnsError = 6003;
            public const int ProxyError = 6004;
            public const int SslError = 6005;
            public const int ProtocolError = 6006;
            public const int ConnectionReset = 6007;
            public const int HostNotFound = 6008;
            public const int ServiceNotFound = 6009;

            // External Service Errors (7000-7999)
            public const int ExternalServiceError = 7000;
            public const int ApiError = 7001;
            public const int ThirdPartyError = 7002;
            public const int IntegrationError = 7003;
            public const int WebhookError = 7004;
            public const int PaymentError = 7005;
            public const int NotificationError = 7006;
            public const int MessagingError = 7007;
            public const int StorageError = 7008;
            public const int CacheError = 7009;
        }

        #endregion

        #region Enum Definitions

        /// <summary>
        /// Log level enumeration;
        /// </summary>
        public enum LogLevel
        {
            Trace = 0,
            Debug = 1,
            Information = 2,
            Warning = 3,
            Error = 4,
            Critical = 5,
            None = 6
        }

        /// <summary>
        /// Operation status enumeration;
        /// </summary>
        public enum OperationStatus
        {
            Pending = 0,
            Running = 1,
            Completed = 2,
            Failed = 3,
            Cancelled = 4,
            TimedOut = 5,
            Aborted = 6
        }

        /// <summary>
        /// Priority levels for tasks and operations;
        /// </summary>
        public enum Priority
        {
            Low = 0,
            Normal = 1,
            High = 2,
            Critical = 3,
            Emergency = 4
        }

        /// <summary>
        /// Security levels for data and operations;
        /// </summary>
        public enum SecurityLevel
        {
            Public = 0,
            Internal = 1,
            Confidential = 2,
            Secret = 3,
            TopSecret = 4
        }

        /// <summary>
        /// Data type enumerations for processing;
        /// </summary>
        public enum DataType
        {
            Unknown = 0,
            Text = 1,
            Number = 2,
            Boolean = 3,
            DateTime = 4,
            Binary = 5,
            Json = 6,
            Xml = 7,
            Image = 8,
            Audio = 9,
            Video = 10
        }

        /// <summary>
        /// AI model types;
        /// </summary>
        public enum ModelType
        {
            NeuralNetwork = 0,
            DecisionTree = 1,
            SupportVectorMachine = 2,
            RandomForest = 3,
            GradientBoosting = 4,
            KMeans = 5,
            PrincipalComponentAnalysis = 6,
            Autoencoder = 7,
            Transformer = 8,
            Convolutional = 9,
            Recurrent = 10
        }

        /// <summary>
        /// Environment types;
        /// </summary>
        public enum EnvironmentType
        {
            Development = 0,
            Testing = 1,
            Staging = 2,
            Production = 3,
            DisasterRecovery = 4
        }

        /// <summary>
        /// Platform types;
        /// </summary>
        public enum Platform
        {
            Windows = 0,
            Linux = 1,
            macOS = 2,
            Android = 3,
            iOS = 4,
            Web = 5,
            Cloud = 6
        }

        /// <summary>
        /// Application modes;
        /// </summary>
        public enum ApplicationMode
        {
            Normal = 0,
            SafeMode = 1,
            Maintenance = 2,
            Recovery = 3,
            Debug = 4,
            Performance = 5
        }

        #endregion

        #region Configuration Keys

        /// <summary>
        /// Configuration key constants;
        /// </summary>
        public static class ConfigKeys
        {
            // Application Settings;
            public const string ApplicationName = "Application:Name";
            public const string ApplicationVersion = "Application:Version";
            public const string Environment = "Application:Environment";
            public const string DebugMode = "Application:DebugMode";

            // Database Settings;
            public const string ConnectionString = "Database:ConnectionString";
            public const string Provider = "Database:Provider";
            public const string CommandTimeout = "Database:CommandTimeout";

            // AI Settings;
            public const string ModelPath = "AI:ModelPath";
            public const string TrainingEnabled = "AI:TrainingEnabled";
            public const string InferenceBatchSize = "AI:InferenceBatchSize";

            // Security Settings;
            public const string JwtSecret = "Security:JwtSecret";
            public const string EncryptionKey = "Security:EncryptionKey";
            public const string RequireHttps = "Security:RequireHttps";

            // Logging Settings;
            public const string LogLevel = "Logging:LogLevel";
            public const string LogPath = "Logging:LogPath";
            public const string EnableFileLogging = "Logging:EnableFileLogging";

            // Performance Settings;
            public const string CacheSize = "Performance:CacheSize";
            public const string ThreadPoolSize = "Performance:ThreadPoolSize";
            public const string GcMode = "Performance:GcMode";

            // Network Settings;
            public const string ApiUrl = "Network:ApiUrl";
            public const string Timeout = "Network:Timeout";
            public const string RetryCount = "Network:RetryCount";

            // External Services;
            public const string ExternalApiKey = "ExternalServices:ApiKey";
            public const string WebhookUrl = "ExternalServices:WebhookUrl";
            public const string StorageConnection = "ExternalServices:StorageConnection";
        }

        #endregion

        #region Date/Time Constants

        /// <summary>
        /// Date and time related constants;
        /// </summary>
        public static class DateTimeConstants
        {
            public const string Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffZ";
            public const string ShortDateFormat = "yyyy-MM-dd";
            public const string LongDateFormat = "dddd, MMMM dd, yyyy";
            public const string TimeFormat = "HH:mm:ss";
            public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

            public const int MillisecondsPerSecond = 1000;
            public const int SecondsPerMinute = 60;
            public const int MinutesPerHour = 60;
            public const int HoursPerDay = 24;
            public const int DaysPerWeek = 7;
            public const int DaysPerYear = 365;

            public const int DefaultCacheDurationMinutes = 15;
            public const int SessionTimeoutMinutes = 30;
            public const int TokenExpiryHours = 24;

            public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
            public static readonly TimeSpan ExtendedTimeout = TimeSpan.FromMinutes(5);
            public static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5);

            public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            public static readonly DateTime MinSupportedDate = new DateTime(1900, 1, 1);
            public static readonly DateTime MaxSupportedDate = new DateTime(9999, 12, 31);
        }

        #endregion

        #region Mathematical Constants

        /// <summary>
        /// Mathematical and scientific constants;
        /// </summary>
        public static class MathConstants
        {
            public const double Pi = 3.14159265358979323846;
            public const double E = 2.71828182845904523536;
            public const double GoldenRatio = 1.61803398874989484820;
            public const double EulerMascheroni = 0.57721566490153286060;

            public const double PlanckConstant = 6.62607015e-34;
            public const double SpeedOfLight = 299792458.0;
            public const double GravitationalConstant = 6.67430e-11;
            public const double AvogadroNumber = 6.02214076e23;

            public const double ZeroThreshold = 1e-10;
            public const double FloatingPointTolerance = 1e-6;
            public const double StatisticalSignificance = 0.05;

            public const int MaxIterations = 10000;
            public const double ConvergenceThreshold = 1e-8;
        }

        #endregion

        #region Cache Keys

        /// <summary>
        /// Cache key templates and patterns;
        /// </summary>
        public static class CacheKeys
        {
            public const string UserProfile = "user:{0}:profile";
            public const string UserSession = "user:{0}:session:{1}";
            public const string UserPermissions = "user:{0}:permissions";

            public const string ModelCache = "model:{0}:{1}";
            public const string TrainingResult = "training:{0}:result";
            public const string InferenceResult = "inference:{0}:result";

            public const string Configuration = "config:{0}";
            public const string ApplicationSettings = "app:settings";
            public const string SystemStatus = "system:status";

            public const string ApiResponse = "api:{0}:{1}";
            public const string ExternalData = "external:{0}:{1}";

            public const string LockKey = "lock:{0}";
            public const string RateLimit = "ratelimit:{0}:{1}";

            public static string Format(string template, params object[] args)
            {
                return string.Format(template, args);
            }
        }

        #endregion

        #region Regular Expressions

        /// <summary>
        /// Commonly used regular expressions;
        /// </summary>
        public static class RegexPatterns
        {
            public const string Email = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
            public const string Phone = @"^\+?[\d\s\-\(\)]{10,}$";
            public const string Url = @"^(https?:\/\/)?([\da-z\.-]+)\.([a-z\.]{2,6})([\/\w \.-]*)*\/?$";
            public const string IpAddress = @"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$";
            public const string Guid = @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
            public const string HexColor = @"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$";
            public const string Base64 = @"^[A-Za-z0-9+/]*={0,2}$";
            public const string Json = @"^\s*(\{.*\}|\[.*\])\s*$";
            public const string DateIso8601 = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$";
            public const string CreditCard = @"^\d{4}[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{4}$";
            public const string Password = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]{12,}$";
        }

        #endregion
    }

    #region Extension Methods

    /// <summary>
    /// Extension methods for constants;
    /// </summary>
    public static class Extensions
    {
        /// <summary>
        /// Checks if error code is in security range;
        /// </summary>
        public static bool IsSecurityError(this int errorCode)
        {
            return errorCode >= 2000 && errorCode < 3000;
        }

        /// <summary>
        /// Gets default timeout for priority level;
        /// </summary>
        public static TimeSpan GetDefaultTimeout(this Constants.Priority priority)
        {
            return priority switch
            {
                Constants.Priority.Emergency => TimeSpan.FromSeconds(5),
                Constants.Priority.Critical => TimeSpan.FromSeconds(10),
                Constants.Priority.High => TimeSpan.FromSeconds(30),
                Constants.Priority.Normal => TimeSpan.FromMinutes(1),
                Constants.Priority.Low => TimeSpan.FromMinutes(5),
                _ => TimeSpan.FromMinutes(1)
            };
        }

        /// <summary>
        /// Gets security level display name;
        /// </summary>
        public static string GetDisplayName(this Constants.SecurityLevel securityLevel)
        {
            return securityLevel switch
            {
                Constants.SecurityLevel.Public => "Public",
                Constants.SecurityLevel.Internal => "Internal Use Only",
                Constants.SecurityLevel.Confidential => "Confidential",
                Constants.SecurityLevel.Secret => "Secret",
                Constants.SecurityLevel.TopSecret => "Top Secret",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Converts DataType to MIME type;
        /// </summary>
        public static string ToMimeType(this Constants.DataType dataType)
        {
            return dataType switch
            {
                Constants.DataType.Json => "application/json",
                Constants.DataType.Xml => "application/xml",
                Constants.DataType.Text => "text/plain",
                Constants.DataType.Image => "image/*",
                Constants.DataType.Audio => "audio/*",
                Constants.DataType.Video => "video/*",
                Constants.DataType.Binary => "application/octet-stream",
                _ => "application/octet-stream"
            };
        }
    }

    #endregion
}
