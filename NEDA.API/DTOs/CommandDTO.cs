using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NEDA.API.DTOs;
{
    #region Command Request DTOs;
    /// <summary>
    /// Request DTO for executing a single command;
    /// </summary>
    public class CommandRequest;
    {
        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; }

        [JsonPropertyName("priority")]
        public CommandPriority Priority { get; set; } = CommandPriority.Normal;

        [JsonPropertyName("timeout")]
        public TimeSpan? Timeout { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; }
    }

    /// <summary>
    /// Request DTO for executing multiple commands in batch;
    /// </summary>
    public class BatchCommandRequest;
    {
        [JsonPropertyName("commands")]
        public List<CommandRequest> Commands { get; set; } = new List<CommandRequest>();

        [JsonPropertyName("executeInParallel")]
        public bool ExecuteInParallel { get; set; } = true;

        [JsonPropertyName("stopOnFailure")]
        public bool StopOnFailure { get; set; } = false;

        [JsonPropertyName("batchId")]
        public string BatchId { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Request DTO for command execution with additional context;
    /// </summary>
    public class ContextualCommandRequest : CommandRequest;
    {
        [JsonPropertyName("executionContext")]
        public ExecutionContext ExecutionContext { get; set; } = new ExecutionContext();

        [JsonPropertyName("retryPolicy")]
        public RetryPolicy RetryPolicy { get; set; } = new RetryPolicy();

        [JsonPropertyName("securityContext")]
        public SecurityContext SecurityContext { get; set; } = new SecurityContext();
    }
    #endregion;

    #region Command Response DTOs;
    /// <summary>
    /// Response DTO for command execution result;
    /// </summary>
    public class CommandResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("commandId")]
        public string CommandId { get; set; }

        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("result")]
        public object Result { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("executionTime")]
        public TimeSpan ExecutionTime { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; }

        [JsonPropertyName("errorDetails")]
        public object ErrorDetails { get; set; }

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Response DTO for batch command execution;
    /// </summary>
    public class BatchCommandResponse;
    {
        [JsonPropertyName("batchId")]
        public string BatchId { get; set; }

        [JsonPropertyName("totalCommands")]
        public int TotalCommands { get; set; }

        [JsonPropertyName("successfulCommands")]
        public int SuccessfulCommands { get; set; }

        [JsonPropertyName("failedCommands")]
        public int FailedCommands { get; set; }

        [JsonPropertyName("results")]
        public List<CommandResponse> Results { get; set; } = new List<CommandResponse>();

        [JsonPropertyName("totalExecutionTime")]
        public TimeSpan TotalExecutionTime { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("batchStatus")]
        public BatchExecutionStatus BatchStatus { get; set; }

        [JsonPropertyName("errors")]
        public List<BatchCommandError> Errors { get; set; } = new List<BatchCommandError>();
    }

    /// <summary>
    /// Response DTO for command status check;
    /// </summary>
    public class CommandStatusResponse;
    {
        [JsonPropertyName("commandId")]
        public string CommandId { get; set; }

        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("status")]
        public CommandStatus Status { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("result")]
        public object Result { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("startTime")]
        public DateTime? StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime? EndTime { get; set; }

        [JsonPropertyName("executionTime")]
        public TimeSpan? ExecutionTime { get; set; }

        [JsonPropertyName("estimatedRemainingTime")]
        public TimeSpan? EstimatedRemainingTime { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; }

        [JsonPropertyName("error")]
        public CommandError Error { get; set; }

        [JsonPropertyName("checkpoint")]
        public string Checkpoint { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Response DTO for command history query;
    /// </summary>
    public class CommandHistoryResponse;
    {
        [JsonPropertyName("commands")]
        public List<CommandExecutionRecord> Commands { get; set; } = new List<CommandExecutionRecord>();

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("totalPages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("filter")]
        public CommandHistoryFilter Filter { get; set; }
    }

    /// <summary>
    /// Response DTO for available commands list;
    /// </summary>
    public class AvailableCommandsResponse;
    {
        [JsonPropertyName("commands")]
        public List<CommandInfo> Commands { get; set; } = new List<CommandInfo>();

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; } = new List<string>();
    }

    /// <summary>
    /// Response DTO for detailed command information;
    /// </summary>
    public class CommandInfoResponse;
    {
        [JsonPropertyName("command")]
        public CommandInfo Command { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("relatedCommands")]
        public List<CommandInfo> RelatedCommands { get; set; } = new List<CommandInfo>();

        [JsonPropertyName("usageStatistics")]
        public CommandUsageStatistics UsageStatistics { get; set; }
    }

    /// <summary>
    /// Response DTO for command validation;
    /// </summary>
    public class CommandValidationResponse;
    {
        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("validationErrors")]
        public List<ValidationError> ValidationErrors { get; set; } = new List<ValidationError>();

        [JsonPropertyName("warnings")]
        public List<ValidationWarning> Warnings { get; set; } = new List<ValidationWarning>();

        [JsonPropertyName("estimatedDuration")]
        public TimeSpan? EstimatedDuration { get; set; }

        [JsonPropertyName("requiredPermissions")]
        public List<string> RequiredPermissions { get; set; } = new List<string>();

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }
    #endregion;

    #region Command Information DTOs;
    /// <summary>
    /// DTO representing command information and metadata;
    /// </summary>
    public class CommandInfo;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; } = true;

        [JsonPropertyName("requiredPermissions")]
        public List<string> RequiredPermissions { get; set; } = new List<string>();

        [JsonPropertyName("parameters")]
        public List<CommandParameter> Parameters { get; set; } = new List<CommandParameter>();

        [JsonPropertyName("estimatedDuration")]
        public TimeSpan EstimatedDuration { get; set; }

        [JsonPropertyName("maxRetryCount")]
        public int MaxRetryCount { get; set; } = 3;

        [JsonPropertyName("timeout")]
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

        [JsonPropertyName("isDestructive")]
        public bool IsDestructive { get; set; }

        [JsonPropertyName("requiresConfirmation")]
        public bool RequiresConfirmation { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("author")]
        public string Author { get; set; }

        [JsonPropertyName("lastModified")]
        public DateTime LastModified { get; set; }

        [JsonPropertyName("examples")]
        public List<CommandExample> Examples { get; set; } = new List<CommandExample>();
    }

    /// <summary>
    /// DTO representing a command parameter definition;
    /// </summary>
    public class CommandParameter;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("defaultValue")]
        public object DefaultValue { get; set; }

        [JsonPropertyName("allowedValues")]
        public List<object> AllowedValues { get; set; } = new List<object>();

        [JsonPropertyName("validationRules")]
        public List<ValidationRule> ValidationRules { get; set; } = new List<ValidationRule>();

        [JsonPropertyName("minValue")]
        public object MinValue { get; set; }

        [JsonPropertyName("maxValue")]
        public object MaxValue { get; set; }

        [JsonPropertyName("isSensitive")]
        public bool IsSensitive { get; set; }
    }

    /// <summary>
    /// DTO representing a command execution record;
    /// </summary>
    public class CommandExecutionRecord;
    {
        [JsonPropertyName("commandId")]
        public string CommandId { get; set; }

        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("status")]
        public CommandStatus Status { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; }

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("result")]
        public object Result { get; set; }

        [JsonPropertyName("error")]
        public CommandError Error { get; set; }

        [JsonPropertyName("startTime")]
        public DateTime StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime? EndTime { get; set; }

        [JsonPropertyName("executionTime")]
        public TimeSpan? ExecutionTime { get; set; }

        [JsonPropertyName("priority")]
        public CommandPriority Priority { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("checkpoint")]
        public string Checkpoint { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; }
    }

    /// <summary>
    /// DTO representing command usage statistics;
    /// </summary>
    public class CommandUsageStatistics;
    {
        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("totalExecutions")]
        public int TotalExecutions { get; set; }

        [JsonPropertyName("successfulExecutions")]
        public int SuccessfulExecutions { get; set; }

        [JsonPropertyName("failedExecutions")]
        public int FailedExecutions { get; set; }

        [JsonPropertyName("averageExecutionTime")]
        public TimeSpan AverageExecutionTime { get; set; }

        [JsonPropertyName("lastExecution")]
        public DateTime? LastExecution { get; set; }

        [JsonPropertyName("mostCommonError")]
        public string MostCommonError { get; set; }

        [JsonPropertyName("successRate")]
        public double SuccessRate { get; set; }

        [JsonPropertyName("usageByUser")]
        public Dictionary<string, int> UsageByUser { get; set; } = new Dictionary<string, int>();

        [JsonPropertyName("usageOverTime")]
        public Dictionary<DateTime, int> UsageOverTime { get; set; } = new Dictionary<DateTime, int>();
    }
    #endregion;

    #region Supporting DTOs;
    /// <summary>
    /// DTO representing execution context for commands;
    /// </summary>
    public class ExecutionContext;
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; }

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("culture")]
        public string Culture { get; set; } = "en-US";

        [JsonPropertyName("timezone")]
        public string Timezone { get; set; }

        [JsonPropertyName("additionalContext")]
        public Dictionary<string, object> AdditionalContext { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// DTO representing security context for command execution;
    /// </summary>
    public class SecurityContext;
    {
        [JsonPropertyName("userRoles")]
        public List<string> UserRoles { get; set; } = new List<string>();

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new List<string>();

        [JsonPropertyName("securityLevel")]
        public SecurityLevel SecurityLevel { get; set; }

        [JsonPropertyName("authenticationMethod")]
        public string AuthenticationMethod { get; set; }

        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; }

        [JsonPropertyName("userAgent")]
        public string UserAgent { get; set; }
    }

    /// <summary>
    /// DTO representing retry policy for command execution;
    /// </summary>
    public class RetryPolicy;
    {
        [JsonPropertyName("maxRetryCount")]
        public int MaxRetryCount { get; set; } = 3;

        [JsonPropertyName("retryDelay")]
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

        [JsonPropertyName("backoffMultiplier")]
        public double BackoffMultiplier { get; set; } = 2.0;

        [JsonPropertyName("maxRetryDelay")]
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

        [JsonPropertyName("retryableErrorCodes")]
        public List<string> RetryableErrorCodes { get; set; } = new List<string>();
    }

    /// <summary>
    /// DTO representing command error information;
    /// </summary>
    public class CommandError;
    {
        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("details")]
        public object Details { get; set; }

        [JsonPropertyName("stackTrace")]
        public string StackTrace { get; set; }

        [JsonPropertyName("innerError")]
        public CommandError InnerError { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("recoverySuggestion")]
        public string RecoverySuggestion { get; set; }

        [JsonPropertyName("isTransient")]
        public bool IsTransient { get; set; }
    }

    /// <summary>
    /// DTO representing batch command error;
    /// </summary>
    public class BatchCommandError;
    {
        [JsonPropertyName("commandIndex")]
        public int CommandIndex { get; set; }

        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("error")]
        public CommandError Error { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// DTO representing command example;
    /// </summary>
    public class CommandExample;
    {
        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("expectedResult")]
        public object ExpectedResult { get; set; }

        [JsonPropertyName("notes")]
        public string Notes { get; set; }
    }

    /// <summary>
    /// DTO representing validation error;
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
    }

    /// <summary>
    /// DTO representing validation warning;
    /// </summary>
    public class ValidationWarning;
    {
        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("warningCode")]
        public string WarningCode { get; set; }
    }

    /// <summary>
    /// DTO representing validation rule;
    /// </summary>
    public class ValidationRule;
    {
        [JsonPropertyName("ruleType")]
        public string RuleType { get; set; }

        [JsonPropertyName("value")]
        public object Value { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }

    /// <summary>
    /// DTO for filtering command history;
    /// </summary>
    public class CommandHistoryFilter;
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("commandName")]
        public string CommandName { get; set; }

        [JsonPropertyName("status")]
        public CommandStatus? Status { get; set; }

        [JsonPropertyName("fromDate")]
        public DateTime? FromDate { get; set; }

        [JsonPropertyName("toDate")]
        public DateTime? ToDate { get; set; }

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; }

        [JsonPropertyName("minDuration")]
        public TimeSpan? MinDuration { get; set; }

        [JsonPropertyName("maxDuration")]
        public TimeSpan? MaxDuration { get; set; }

        [JsonPropertyName("hasErrors")]
        public bool? HasErrors { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; } = 1;

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; } = 50;

        [JsonPropertyName("sortBy")]
        public string SortBy { get; set; } = "StartTime";

        [JsonPropertyName("sortDirection")]
        public SortDirection SortDirection { get; set; } = SortDirection.Descending;
    }
    #endregion;

    #region Enums;
    /// <summary>
    /// Command execution status;
    /// </summary>
    public enum CommandStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled,
        TimedOut,
        WaitingForDependencies;
    }

    /// <summary>
    /// Command execution priority;
    /// </summary>
    public enum CommandPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    /// <summary>
    /// Batch execution status;
    /// </summary>
    public enum BatchExecutionStatus;
    {
        Pending,
        Running,
        Completed,
        PartiallyCompleted,
        Failed,
        Cancelled;
    }

    /// <summary>
    /// Security level for command execution;
    /// </summary>
    public enum SecurityLevel;
    {
        Public,
        User,
        Admin,
        System;
    }

    /// <summary>
    /// Validation severity level;
    /// </summary>
    public enum ValidationSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    /// <summary>
    /// Sort direction for queries;
    /// </summary>
    public enum SortDirection;
    {
        Ascending,
        Descending;
    }
    #endregion;
}
