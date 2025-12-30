#region Supporting Classes and Enums;

namespace NEDA.SecurityModules.Firewall;
{
    /// <summary>
    /// Rule actions that can be taken;
    /// </summary>
    public enum RuleAction;
    {
        Allow,
        Deny,
        Block,
        Quarantine,
        LogOnly,
        Redirect;
    }

    /// <summary>
    /// Rule priority levels;
    /// </summary>
    public enum RulePriority;
    {
        Emergency = 0,
        High = 1,
        Normal = 2,
        Low = 3;
    }

    /// <summary>
    /// Types of firewall rules;
    /// </summary>
    public enum RuleType;
    {
        IP,
        Port,
        Protocol,
        URL,
        Geo,
        RateLimit,
        Custom,
        Signature,
        Behavioral,
        MachineLearning;
    }

    /// <summary>
    /// Rule evaluation result;
    /// </summary>
    public class RuleEvaluationResult;
    {
        public RuleAction Action { get; set; }
        public bool IsMatch { get; set; }
        public string MatchedRuleId { get; set; }
        public double Score { get; set; }
        public string Error { get; set; }
        public DateTime EvaluationTime { get; set; }
        public TimeSpan? EvaluationDuration { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Network request representation;
    /// </summary>
    public class NetworkRequest;
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
        public IPAddress SourceIP { get; set; }
        public IPAddress DestinationIP { get; set; }
        public int SourcePort { get; set; }
        public int DestinationPort { get; set; }
        public string Protocol { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public byte[] Payload { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string UserAgent { get; set; }
        public string CountryCode { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Evaluation context;
    /// </summary>
    public class EvaluationContext;
    {
        public NetworkRequest Request { get; set; }
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> AdditionalContext { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Rule filter for querying rules;
    /// </summary>
    public class RuleFilter;
    {
        public string RuleType { get; set; }
        public bool? IsEnabled { get; set; }
        public RulePriority? Priority { get; set; }
        public string SearchTerm { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? CreatedBefore { get; set; }
    }

    /// <summary>
    /// Rule operation result;
    /// </summary>
    public class RuleOperationResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string RuleId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static RuleOperationResult Success(string message, string ruleId = null)
        {
            return new RuleOperationResult;
            {
                Success = true,
                Message = message,
                RuleId = ruleId;
            };
        }

        public static RuleOperationResult Failure(string message, string ruleId = null)
        {
            return new RuleOperationResult;
            {
                Success = false,
                Message = message,
                RuleId = ruleId;
            };
        }
    }

    /// <summary>
    /// Rule validation result;
    /// </summary>
    public class RuleValidationResult;
    {
        public bool IsValid { get; set; }
        public int TotalRules { get; set; }
        public int ValidRules { get; set; }
        public List<InvalidRuleInfo> InvalidRules { get; set; } = new List<InvalidRuleInfo>();
        public List<RuleConflict> Conflicts { get; set; } = new List<RuleConflict>();
        public List<string> Errors { get; set; } = new List<string>();
        public DateTime ValidationTime { get; set; }
    }

    /// <summary>
    /// Invalid rule information;
    /// </summary>
    public class InvalidRuleInfo;
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Rule conflict definition;
    /// </summary>
    public class RuleConflict;
    {
        public List<string> RuleIds { get; set; } = new List<string>();
        public List<string> RuleNames { get; set; } = new List<string>();
        public ConflictType ConflictType { get; set; }
        public string Description { get; set; }
        public ConflictSeverity Severity { get; set; }
    }

    /// <summary>
    /// Conflict types;
    /// </summary>
    public enum ConflictType;
    {
        ActionConflict,
        PriorityConflict,
        OverlappingConditions,
        CircularDependency,
        RedundantRule;
    }

    /// <summary>
    /// Conflict severity levels;
    /// </summary>
    public enum ConflictSeverity;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Rule engine statistics;
    /// </summary>
    public class RuleEngineStats;
    {
        public TimeSpan Uptime { get; set; }
        public int TotalEvaluations { get; set; }
        public int AllowedRequests { get; set; }
        public int BlockedRequests { get; set; }
        public double CacheHitRate { get; set; }
        public TimeSpan AverageEvaluationTime { get; set; }
        public DateTime LastRuleUpdate { get; set; }
        public Dictionary<RuleType, int> RuleCountsByType { get; set; } = new Dictionary<RuleType, int>();
        public Dictionary<string, double> PerformanceMetrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Rule export data structure;
    /// </summary>
    public class RuleExportData;
    {
        public DateTime ExportTime { get; set; }
        public int TotalRules { get; set; }
        public string EngineVersion { get; set; }
        public List<RuleExportItem> Rules { get; set; } = new List<RuleExportItem>();
    }

    /// <summary>
    /// Individual rule export item;
    /// </summary>
    public class RuleExportItem;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public RuleType RuleType { get; set; }
        public RulePriority Priority { get; set; }
        public bool IsEnabled { get; set; }
        public RuleAction Action { get; set; }
        public Dictionary<string, string> Conditions { get; set; } = new Dictionary<string, string>();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Engine health check result;
    /// </summary>
    public class EngineHealthCheckResult;
    {
        public DateTime Timestamp { get; set; }
        public EngineStatus EngineStatus { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, double> PerformanceMetrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Engine status;
    /// </summary>
    public enum EngineStatus;
    {
        Unknown,
        NotInitialized,
        Healthy,
        Degraded,
        Unhealthy,
        Error;
    }

    /// <summary>
    /// Configuration change types;
    /// </summary>
    public enum ConfigChangeType;
    {
        Initialization,
        RuleAdded,
        RuleUpdated,
        RuleRemoved,
        RuleStatusChanged,
        RulesReloaded,
        AllRulesCleared;
    }

    /// <summary>
    /// Rule match event arguments;
    /// </summary>
    public class RuleMatchEventArgs : EventArgs;
    {
        public IFirewallRule Rule { get; set; }
        public NetworkRequest Request { get; set; }
        public RuleEvaluationResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Engine configuration changed event arguments;
    /// </summary>
    public class EngineConfigChangedEventArgs : EventArgs;
    {
        public ConfigChangeType ChangeType { get; set; }
        public string RuleId { get; set; }
        public bool? Enabled { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Rule evaluation log entry
    /// </summary>
    public class RuleEvaluationLog;
    {
        public string RequestId { get; set; }
        public DateTime Timestamp { get; set; }
        public string SourceIP { get; set; }
        public string DestinationIP { get; set; }
        public RuleAction Action { get; set; }
        public string MatchedRuleId { get; set; }
        public double Score { get; set; }
        public bool IsMatch { get; set; }
        public double EvaluationTimeMs { get; set; }
    }

    /// <summary>
    /// Custom exception for rule engine errors;
    /// </summary>
    public class RuleEngineException : Exception
    {
        public string RuleId { get; set; }
        public RuleErrorCode ErrorCode { get; set; }

        public RuleEngineException(string message) : base(message) { }
        public RuleEngineException(string message, Exception inner) : base(message, inner) { }
        public RuleEngineException(string message, string ruleId, RuleErrorCode errorCode)
            : base(message)
        {
            RuleId = ruleId;
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Rule engine initialization exception;
    /// </summary>
    public class RuleEngineInitializationException : RuleEngineException;
    {
        public RuleEngineInitializationException(string message) : base(message) { }
        public RuleEngineInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Rule error codes;
    /// </summary>
    public enum RuleErrorCode;
    {
        Unknown = 0,
        InvalidRuleDefinition = 1,
        RuleCompilationError = 2,
        RuleEvaluationError = 3,
        CacheError = 4,
        ConfigurationError = 5,
        PermissionDenied = 6,
        ResourceExhausted = 7;
    }
}

#endregion;
