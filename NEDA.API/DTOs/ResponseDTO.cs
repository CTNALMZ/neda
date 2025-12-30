using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NEDA.API.DTOs;
{
    #region Base Response DTOs;
    /// <summary>
    /// Base response DTO for all API responses;
    /// </summary>
    public class BaseResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";
    }

    /// <summary>
    /// Generic response DTO for typed responses;
    /// </summary>
    public class Response<T> : BaseResponse;
    {
        [JsonPropertyName("data")]
        public T Data { get; set; }

        [JsonPropertyName("metadata")]
        public ResponseMetadata Metadata { get; set; } = new ResponseMetadata();
    }

    /// <summary>
    /// Paginated response DTO for list operations;
    /// </summary>
    public class PaginatedResponse<T> : BaseResponse;
    {
        [JsonPropertyName("data")]
        public List<T> Data { get; set; } = new List<T>();

        [JsonPropertyName("pagination")]
        public PaginationInfo Pagination { get; set; } = new PaginationInfo();

        [JsonPropertyName("filters")]
        public Dictionary<string, object> AppliedFilters { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("sorting")]
        public SortingInfo Sorting { get; set; } = new SortingInfo();
    }

    /// <summary>
    /// Operation response DTO for simple operations;
    /// </summary>
    public class OperationResponse : BaseResponse;
    {
        [JsonPropertyName("operationId")]
        public string OperationId { get; set; }

        [JsonPropertyName("affectedRecords")]
        public int AffectedRecords { get; set; }

        [JsonPropertyName("duration")]
        public TimeSpan Duration { get; set; }

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }
    #endregion;

    #region Specialized Response DTOs;
    /// <summary>
    /// Error response DTO for API errors;
    /// </summary>
    public class ErrorResponse : BaseResponse;
    {
        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; }

        [JsonPropertyName("errorType")]
        public ErrorType ErrorType { get; set; }

        [JsonPropertyName("details")]
        public object Details { get; set; }

        [JsonPropertyName("stackTrace")]
        public string StackTrace { get; set; }

        [JsonPropertyName("innerError")]
        public ErrorResponse InnerError { get; set; }

        [JsonPropertyName("recoverySuggestion")]
        public string RecoverySuggestion { get; set; }

        [JsonPropertyName("documentationUrl")]
        public string DocumentationUrl { get; set; }

        [JsonPropertyName("validationErrors")]
        public List<ValidationError> ValidationErrors { get; set; } = new List<ValidationError>();

        public ErrorResponse()
        {
            Success = false;
        }
    }

    /// <summary>
    /// Validation response DTO for input validation;
    /// </summary>
    public class ValidationResponse : BaseResponse;
    {
        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("validationErrors")]
        public List<ValidationError> ValidationErrors { get; set; } = new List<ValidationError>();

        [JsonPropertyName("warnings")]
        public List<ValidationWarning> Warnings { get; set; } = new List<ValidationWarning>();

        [JsonPropertyName("suggestions")]
        public List<string> Suggestions { get; set; } = new List<string>();

        public ValidationResponse()
        {
            Success = true;
        }
    }

    /// <summary>
    /// Health check response DTO;
    /// </summary>
    public class HealthCheckResponse;
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("uptime")]
        public TimeSpan Uptime { get; set; }

        [JsonPropertyName("checks")]
        public List<HealthCheck> Checks { get; set; } = new List<HealthCheck>();

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("dependencies")]
        public List<DependencyHealth> Dependencies { get; set; } = new List<DependencyHealth>();
    }

    /// <summary>
    /// System status response DTO;
    /// </summary>
    public class SystemStatusResponse : BaseResponse;
    {
        [JsonPropertyName("systemStatus")]
        public SystemStatus SystemStatus { get; set; } = new SystemStatus();

        [JsonPropertyName("healthStatus")]
        public HealthStatus HealthStatus { get; set; } = new HealthStatus();

        [JsonPropertyName("performanceMetrics")]
        public PerformanceMetrics PerformanceMetrics { get; set; } = new PerformanceMetrics();

        [JsonPropertyName("activeConnections")]
        public int ActiveConnections { get; set; }

        [JsonPropertyName("memoryUsage")]
        public MemoryUsage MemoryUsage { get; set; } = new MemoryUsage();

        [JsonPropertyName("cpuUsage")]
        public CpuUsage CpuUsage { get; set; } = new CpuUsage();
    }

    /// <summary>
    /// Authentication response DTO;
    /// </summary>
    public class AuthResponse : BaseResponse;
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; }

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; }

        [JsonPropertyName("tokenType")]
        public string TokenType { get; set; } = "Bearer";

        [JsonPropertyName("expiresIn")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new List<string>();

        [JsonPropertyName("roles")]
        public List<string> Roles { get; set; } = new List<string>();
    }

    /// <summary>
    /// File upload response DTO;
    /// </summary>
    public class FileUploadResponse : BaseResponse;
    {
        [JsonPropertyName("fileId")]
        public string FileId { get; set; }

        [JsonPropertyName("fileName")]
        public string FileName { get; set; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("contentType")]
        public string ContentType { get; set; }

        [JsonPropertyName("uploadUrl")]
        public string UploadUrl { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; }

        [JsonPropertyName("uploadedAt")]
        public DateTime UploadedAt { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Export response DTO;
    /// </summary>
    public class ExportResponse : BaseResponse;
    {
        [JsonPropertyName("exportId")]
        public string ExportId { get; set; }

        [JsonPropertyName("format")]
        public string Format { get; set; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTime ExpiresAt { get; set; }

        [JsonPropertyName("recordCount")]
        public int RecordCount { get; set; }

        [JsonPropertyName("includedFields")]
        public List<string> IncludedFields { get; set; } = new List<string>();
    }

    /// <summary>
    /// Import response DTO;
    /// </summary>
    public class ImportResponse : BaseResponse;
    {
        [JsonPropertyName("importId")]
        public string ImportId { get; set; }

        [JsonPropertyName("totalRecords")]
        public int TotalRecords { get; set; }

        [JsonPropertyName("processedRecords")]
        public int ProcessedRecords { get; set; }

        [JsonPropertyName("successfulRecords")]
        public int SuccessfulRecords { get; set; }

        [JsonPropertyName("failedRecords")]
        public int FailedRecords { get; set; }

        [JsonPropertyName("errors")]
        public List<ImportError> Errors { get; set; } = new List<ImportError>();

        [JsonPropertyName("warnings")]
        public List<ImportWarning> Warnings { get; set; } = new List<ImportWarning>();

        [JsonPropertyName("duration")]
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Search response DTO;
    /// </summary>
    public class SearchResponse<T> : PaginatedResponse<T>
    {
        [JsonPropertyName("searchQuery")]
        public string SearchQuery { get; set; }

        [JsonPropertyName("searchTime")]
        public TimeSpan SearchTime { get; set; }

        [JsonPropertyName("totalMatches")]
        public int TotalMatches { get; set; }

        [JsonPropertyName("facets")]
        public Dictionary<string, List<Facet>> Facets { get; set; } = new Dictionary<string, List<Facet>>();

        [JsonPropertyName("suggestions")]
        public List<string> Suggestions { get; set; } = new List<string>();

        [JsonPropertyName("highlightedFields")]
        public Dictionary<string, List<string>> HighlightedFields { get; set; } = new Dictionary<string, List<string>>();
    }

    /// <summary>
    /// Analytics response DTO;
    /// </summary>
    public class AnalyticsResponse : BaseResponse;
    {
        [JsonPropertyName("analyticsId")]
        public string AnalyticsId { get; set; }

        [JsonPropertyName("timeRange")]
        public TimeRange TimeRange { get; set; } = new TimeRange();

        [JsonPropertyName("metrics")]
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("dimensions")]
        public Dictionary<string, object> Dimensions { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("trends")]
        public List<Trend> Trends { get; set; } = new List<Trend>();

        [JsonPropertyName("comparisons")]
        public Dictionary<string, object> Comparisons { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("insights")]
        public List<Insight> Insights { get; set; } = new List<Insight>();
    }

    /// <summary>
    /// Notification response DTO;
    /// </summary>
    public class NotificationResponse : BaseResponse;
    {
        [JsonPropertyName("notificationId")]
        public string NotificationId { get; set; }

        [JsonPropertyName("type")]
        public NotificationType Type { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("priority")]
        public NotificationPriority Priority { get; set; }

        [JsonPropertyName("sentAt")]
        public DateTime SentAt { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTime? ExpiresAt { get; set; }

        [JsonPropertyName("actions")]
        public List<NotificationAction> Actions { get; set; } = new List<NotificationAction>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }
    #endregion;

    #region Supporting DTOs;
    /// <summary>
    /// Response metadata DTO;
    /// </summary>
    public class ResponseMetadata;
    {
        [JsonPropertyName("requestId")]
        public string RequestId { get; set; }

        [JsonPropertyName("serverId")]
        public string ServerId { get; set; }

        [JsonPropertyName("processingTime")]
        public TimeSpan ProcessingTime { get; set; }

        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; }

        [JsonPropertyName("rateLimit")]
        public RateLimitInfo RateLimit { get; set; } = new RateLimitInfo();

        [JsonPropertyName("cache")]
        public CacheInfo Cache { get; set; } = new CacheInfo();

        [JsonPropertyName("pagination")]
        public PaginationInfo Pagination { get; set; } = new PaginationInfo();
    }

    /// <summary>
    /// Pagination information DTO;
    /// </summary>
    public class PaginationInfo;
    {
        [JsonPropertyName("page")]
        public int Page { get; set; } = 1;

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; } = 50;

        [JsonPropertyName("totalPages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("totalRecords")]
        public int TotalRecords { get; set; }

        [JsonPropertyName("hasNextPage")]
        public bool HasNextPage { get; set; }

        [JsonPropertyName("hasPreviousPage")]
        public bool HasPreviousPage { get; set; }

        [JsonPropertyName("nextPageToken")]
        public string NextPageToken { get; set; }

        [JsonPropertyName("previousPageToken")]
        public string PreviousPageToken { get; set; }
    }

    /// <summary>
    /// Sorting information DTO;
    /// </summary>
    public class SortingInfo;
    {
        [JsonPropertyName("sortBy")]
        public string SortBy { get; set; }

        [JsonPropertyName("sortDirection")]
        public SortDirection SortDirection { get; set; } = SortDirection.Ascending;

        [JsonPropertyName("secondarySort")]
        public List<SortField> SecondarySort { get; set; } = new List<SortField>();
    }

    /// <summary>
    /// Rate limit information DTO;
    /// </summary>
    public class RateLimitInfo;
    {
        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("remaining")]
        public int Remaining { get; set; }

        [JsonPropertyName("resetTime")]
        public DateTime ResetTime { get; set; }

        [JsonPropertyName("window")]
        public TimeSpan Window { get; set; }
    }

    /// <summary>
    /// Cache information DTO;
    /// </summary>
    public class CacheInfo;
    {
        [JsonPropertyName("cached")]
        public bool Cached { get; set; }

        [JsonPropertyName("cacheKey")]
        public string CacheKey { get; set; }

        [JsonPropertyName("cacheDuration")]
        public TimeSpan CacheDuration { get; set; }

        [JsonPropertyName("cachedAt")]
        public DateTime CachedAt { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTime ExpiresAt { get; set; }
    }

    /// <summary>
    /// Validation error DTO;
    /// </summary>
    public class ValidationError;
    {
        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; }

        [JsonPropertyName("severity")]
        public ValidationSeverity Severity { get; set; }

        [JsonPropertyName("attemptedValue")]
        public object AttemptedValue { get; set; }

        [JsonPropertyName("customState")]
        public object CustomState { get; set; }
    }

    /// <summary>
    /// Validation warning DTO;
    /// </summary>
    public class ValidationWarning;
    {
        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("warningCode")]
        public string WarningCode { get; set; }

        [JsonPropertyName("suggestion")]
        public string Suggestion { get; set; }
    }

    /// <summary>
    /// Health check DTO;
    /// </summary>
    public class HealthCheck;
    {
        [JsonPropertyName("component")]
        public string Component { get; set; }

        [JsonPropertyName("status")]
        public HealthStatus Status { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("responseTime")]
        public TimeSpan ResponseTime { get; set; }

        [JsonPropertyName("lastChecked")]
        public DateTime LastChecked { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Dependency health DTO;
    /// </summary>
    public class DependencyHealth;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public DependencyType Type { get; set; }

        [JsonPropertyName("status")]
        public HealthStatus Status { get; set; }

        [JsonPropertyName("responseTime")]
        public TimeSpan ResponseTime { get; set; }

        [JsonPropertyName("lastSuccessfulCheck")]
        public DateTime LastSuccessfulCheck { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }

        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; }
    }

    /// <summary>
    /// System status DTO;
    /// </summary>
    public class SystemStatus;
    {
        [JsonPropertyName("overallStatus")]
        public SystemHealthStatus OverallStatus { get; set; }

        [JsonPropertyName("components")]
        public List<ComponentStatus> Components { get; set; } = new List<ComponentStatus>();

        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; }

        [JsonPropertyName("uptime")]
        public TimeSpan Uptime { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }
    }

    /// <summary>
    /// Health status DTO;
    /// </summary>
    public class HealthStatus;
    {
        [JsonPropertyName("overallStatus")]
        public HealthStatusLevel OverallStatus { get; set; }

        [JsonPropertyName("components")]
        public List<ComponentHealth> Components { get; set; } = new List<ComponentHealth>();

        [JsonPropertyName("lastChecked")]
        public DateTime LastChecked { get; set; }
    }

    /// <summary>
    /// Import error DTO;
    /// </summary>
    public class ImportError;
    {
        [JsonPropertyName("recordIndex")]
        public int RecordIndex { get; set; }

        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("value")]
        public object Value { get; set; }

        [JsonPropertyName("suggestion")]
        public string Suggestion { get; set; }
    }

    /// <summary>
    /// Import warning DTO;
    /// </summary>
    public class ImportWarning;
    {
        [JsonPropertyName("recordIndex")]
        public int RecordIndex { get; set; }

        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("warningCode")]
        public string WarningCode { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("value")]
        public object Value { get; set; }
    }

    /// <summary>
    /// Facet DTO for search results;
    /// </summary>
    public class Facet;
    {
        [JsonPropertyName("value")]
        public string Value { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("selected")]
        public bool Selected { get; set; }
    }

    /// <summary>
    /// Trend DTO for analytics;
    /// </summary>
    public class Trend;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("direction")]
        public TrendDirection Direction { get; set; }

        [JsonPropertyName("percentageChange")]
        public double PercentageChange { get; set; }

        [JsonPropertyName("currentValue")]
        public double CurrentValue { get; set; }

        [JsonPropertyName("previousValue")]
        public double PreviousValue { get; set; }

        [JsonPropertyName("timePeriod")]
        public TimePeriod TimePeriod { get; set; }
    }

    /// <summary>
    /// Insight DTO for analytics;
    /// </summary>
    public class Insight;
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("type")]
        public InsightType Type { get; set; }

        [JsonPropertyName("impact")]
        public ImpactLevel Impact { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("dataPoints")]
        public List<DataPoint> DataPoints { get; set; } = new List<DataPoint>();

        [JsonPropertyName("recommendations")]
        public List<string> Recommendations { get; set; } = new List<string>();
    }

    /// <summary>
    /// Notification action DTO;
    /// </summary>
    public class NotificationAction;
    {
        [JsonPropertyName("label")]
        public string Label { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("confirmationRequired")]
        public bool ConfirmationRequired { get; set; }
    }

    /// <summary>
    /// Sort field DTO;
    /// </summary>
    public class SortField;
    {
        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("direction")]
        public SortDirection Direction { get; set; }
    }

    /// <summary>
    /// Time range DTO;
    /// </summary>
    public class TimeRange;
    {
        [JsonPropertyName("start")]
        public DateTime Start { get; set; }

        [JsonPropertyName("end")]
        public DateTime End { get; set; }

        [JsonPropertyName("timezone")]
        public string Timezone { get; set; } = "UTC";
    }

    /// <summary>
    /// Data point DTO;
    /// </summary>
    public class DataPoint;
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("value")]
        public double Value { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; }
    }

    /// <summary>
    /// Component status DTO;
    /// </summary>
    public class ComponentStatus;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("status")]
        public ComponentHealthStatus Status { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Component health DTO;
    /// </summary>
    public class ComponentHealth;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("status")]
        public HealthStatusLevel Status { get; set; }

        [JsonPropertyName("responseTime")]
        public TimeSpan ResponseTime { get; set; }

        [JsonPropertyName("lastChecked")]
        public DateTime LastChecked { get; set; }
    }

    /// <summary>
    /// Memory usage DTO;
    /// </summary>
    public class MemoryUsage;
    {
        [JsonPropertyName("totalBytes")]
        public long TotalBytes { get; set; }

        [JsonPropertyName("usedBytes")]
        public long UsedBytes { get; set; }

        [JsonPropertyName("availableBytes")]
        public long AvailableBytes { get; set; }

        [JsonPropertyName("percentage")]
        public double Percentage { get; set; }
    }

    /// <summary>
    /// CPU usage DTO;
    /// </summary>
    public class CpuUsage;
    {
        [JsonPropertyName("percentage")]
        public double Percentage { get; set; }

        [JsonPropertyName("processCount")]
        public int ProcessCount { get; set; }

        [JsonPropertyName("loadAverage")]
        public double LoadAverage { get; set; }
    }
    #endregion;

    #region Enums;
    public enum ErrorType;
    {
        Validation,
        Authentication,
        Authorization,
        NotFound,
        Conflict,
        RateLimit,
        System,
        Network,
        Timeout,
        Unknown;
    }

    public enum ValidationSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    public enum HealthStatusLevel;
    {
        Healthy,
        Degraded,
        Unhealthy,
        Unknown;
    }

    public enum DependencyType;
    {
        Database,
        Api,
        FileSystem,
        Network,
        ExternalService,
        Cache,
        MessageQueue;
    }

    public enum SystemHealthStatus;
    {
        Operational,
        Degraded,
        PartialOutage,
        MajorOutage,
        Maintenance;
    }

    public enum ComponentHealthStatus;
    {
        Operational,
        Degraded,
        Outage,
        Maintenance,
        Unknown;
    }

    public enum NotificationType;
    {
        Info,
        Success,
        Warning,
        Error,
        System,
        Security;
    }

    public enum NotificationPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    public enum SortDirection;
    {
        Ascending,
        Descending;
    }

    public enum TrendDirection;
    {
        Up,
        Down,
        Stable,
        Volatile;
    }

    public enum TimePeriod;
    {
        Hourly,
        Daily,
        Weekly,
        Monthly,
        Quarterly,
        Yearly;
    }

    public enum InsightType;
    {
        Performance,
        Usage,
        Security,
        Cost,
        Opportunity,
        Risk;
    }

    public enum ImpactLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }
    #endregion;
}
