using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NEDA.API.DTOs;
{
    #region Project Request DTOs;
    /// <summary>
    /// Request DTO for creating a new project;
    /// </summary>
    public class CreateProjectRequest;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("type")]
        public ProjectType Type { get; set; } = ProjectType.General;

        [JsonPropertyName("settings")]
        public ProjectSettings Settings { get; set; } = new ProjectSettings();

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonPropertyName("templateId")]
        public string TemplateId { get; set; }

        [JsonPropertyName("parentProjectId")]
        public string ParentProjectId { get; set; }

        [JsonPropertyName("initialMembers")]
        public List<ProjectMemberRequest> InitialMembers { get; set; } = new List<ProjectMemberRequest>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Request DTO for updating an existing project;
    /// </summary>
    public class UpdateProjectRequest;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("settings")]
        public ProjectSettings Settings { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; }

        [JsonPropertyName("status")]
        public ProjectStatus? Status { get; set; }

        [JsonPropertyName("version")]
        public int? Version { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Request DTO for changing project status;
    /// </summary>
    public class ChangeStatusRequest;
    {
        [JsonPropertyName("newStatus")]
        public ProjectStatus NewStatus { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        [JsonPropertyName("comment")]
        public string Comment { get; set; }
    }

    /// <summary>
    /// Request DTO for copying a project;
    /// </summary>
    public class CopyProjectRequest;
    {
        [JsonPropertyName("newName")]
        public string NewName { get; set; }

        [JsonPropertyName("copySettings")]
        public bool CopySettings { get; set; } = true;

        [JsonPropertyName("copyFiles")]
        public bool CopyFiles { get; set; } = true;

        [JsonPropertyName("copyMembers")]
        public bool CopyMembers { get; set; } = false;

        [JsonPropertyName("copyHistory")]
        public bool CopyHistory { get; set; } = false;

        [JsonPropertyName("targetWorkspace")]
        public string TargetWorkspace { get; set; }
    }

    /// <summary>
    /// Request DTO for adding a project member;
    /// </summary>
    public class AddMemberRequest;
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("role")]
        public ProjectRole Role { get; set; } = ProjectRole.Member;

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new List<string>();

        [JsonPropertyName("notes")]
        public string Notes { get; set; }
    }

    /// <summary>
    /// Request DTO for updating project member;
    /// </summary>
    public class UpdateMemberRequest;
    {
        [JsonPropertyName("role")]
        public ProjectRole? Role { get; set; }

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; }

        [JsonPropertyName("isActive")]
        public bool? IsActive { get; set; }

        [JsonPropertyName("notes")]
        public string Notes { get; set; }
    }

    /// <summary>
    /// Request DTO for project search and filtering;
    /// </summary>
    public class ProjectSearchRequest;
    {
        [JsonPropertyName("searchTerm")]
        public string SearchTerm { get; set; }

        [JsonPropertyName("types")]
        public List<ProjectType> Types { get; set; } = new List<ProjectType>();

        [JsonPropertyName("statuses")]
        public List<ProjectStatus> Statuses { get; set; } = new List<ProjectStatus>();

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonPropertyName("createdAfter")]
        public DateTime? CreatedAfter { get; set; }

        [JsonPropertyName("createdBefore")]
        public DateTime? CreatedBefore { get; set; }

        [JsonPropertyName("modifiedAfter")]
        public DateTime? ModifiedAfter { get; set; }

        [JsonPropertyName("modifiedBefore")]
        public DateTime? ModifiedBefore { get; set; }

        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; }

        [JsonPropertyName("hasFiles")]
        public bool? HasFiles { get; set; }

        [JsonPropertyName("hasMembers")]
        public bool? HasMembers { get; set; }

        [JsonPropertyName("minSize")]
        public long? MinSize { get; set; }

        [JsonPropertyName("maxSize")]
        public long? MaxSize { get; set; }

        [JsonPropertyName("sortBy")]
        public string SortBy { get; set; } = "ModifiedAt";

        [JsonPropertyName("sortDirection")]
        public SortDirection SortDirection { get; set; } = SortDirection.Descending;

        [JsonPropertyName("page")]
        public int Page { get; set; } = 1;

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; } = 50;
    }
    #endregion;

    #region Project Response DTOs;
    /// <summary>
    /// Response DTO for project operations;
    /// </summary>
    public class ProjectResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("project")]
        public Project Project { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Response DTO for project list operations;
    /// </summary>
    public class ProjectListResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("projects")]
        public List<Project> Projects { get; set; } = new List<Project>();

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("totalPages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("filters")]
        public ProjectFilter AppliedFilters { get; set; }
    }

    /// <summary>
    /// Response DTO for project analytics;
    /// </summary>
    public class ProjectAnalyticsResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("analytics")]
        public ProjectAnalytics Analytics { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Response DTO for project members;
    /// </summary>
    public class ProjectMembersResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("members")]
        public List<ProjectMember> Members { get; set; } = new List<ProjectMember>();

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Response DTO for project permissions;
    /// </summary>
    public class ProjectPermissionsResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new List<string>();

        [JsonPropertyName("userRole")]
        public ProjectRole UserRole { get; set; }

        [JsonPropertyName("canEdit")]
        public bool CanEdit { get; set; }

        [JsonPropertyName("canDelete")]
        public bool CanDelete { get; set; }

        [JsonPropertyName("canManageMembers")]
        public bool CanManageMembers { get; set; }

        [JsonPropertyName("canManageSettings")]
        public bool CanManageSettings { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Response DTO for project export;
    /// </summary>
    public class ProjectExportResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("exportId")]
        public string ExportId { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("format")]
        public string Format { get; set; }

        [JsonPropertyName("includes")]
        public List<string> Includes { get; set; } = new List<string>();

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Response DTO for project validation;
    /// </summary>
    public class ProjectValidationResponse;
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("validationErrors")]
        public List<ValidationError> ValidationErrors { get; set; } = new List<ValidationError>();

        [JsonPropertyName("warnings")]
        public List<ValidationWarning> Warnings { get; set; } = new List<ValidationWarning>();

        [JsonPropertyName("suggestions")]
        public List<string> Suggestions { get; set; } = new List<string>();

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }
    #endregion;

    #region Project Model DTOs;
    /// <summary>
    /// Main project DTO;
    /// </summary>
    public class Project;
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("type")]
        public ProjectType Type { get; set; }

        [JsonPropertyName("status")]
        public ProjectStatus Status { get; set; }

        [JsonPropertyName("settings")]
        public ProjectSettings Settings { get; set; } = new ProjectSettings();

        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("modifiedBy")]
        public string ModifiedBy { get; set; }

        [JsonPropertyName("modifiedAt")]
        public DateTime ModifiedAt { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("statistics")]
        public ProjectStatistics Statistics { get; set; } = new ProjectStatistics();

        [JsonPropertyName("permissions")]
        public ProjectPermissions Permissions { get; set; } = new ProjectPermissions();

        [JsonPropertyName("workspaceId")]
        public string WorkspaceId { get; set; }

        [JsonPropertyName("parentProjectId")]
        public string ParentProjectId { get; set; }

        [JsonPropertyName("templateId")]
        public string TemplateId { get; set; }

        [JsonPropertyName("isPublic")]
        public bool IsPublic { get; set; }

        [JsonPropertyName("isArchived")]
        public bool IsArchived { get; set; }

        [JsonPropertyName("lastActivity")]
        public DateTime? LastActivity { get; set; }
    }

    /// <summary>
    /// Project settings DTO;
    /// </summary>
    public class ProjectSettings;
    {
        [JsonPropertyName("versionControl")]
        public VersionControlSettings VersionControl { get; set; } = new VersionControlSettings();

        [JsonPropertyName("backup")]
        public BackupSettings Backup { get; set; } = new BackupSettings();

        [JsonPropertyName("collaboration")]
        public CollaborationSettings Collaboration { get; set; } = new CollaborationSettings();

        [JsonPropertyName("security")]
        public ProjectSecuritySettings Security { get; set; } = new ProjectSecuritySettings();

        [JsonPropertyName("notifications")]
        public NotificationSettings Notifications { get; set; } = new NotificationSettings();

        [JsonPropertyName("workflow")]
        public WorkflowSettings Workflow { get; set; } = new WorkflowSettings();

        [JsonPropertyName("customSettings")]
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Project statistics DTO;
    /// </summary>
    public class ProjectStatistics;
    {
        [JsonPropertyName("totalFiles")]
        public int TotalFiles { get; set; }

        [JsonPropertyName("totalSize")]
        public long TotalSize { get; set; }

        [JsonPropertyName("totalMembers")]
        public int TotalMembers { get; set; }

        [JsonPropertyName("totalActivities")]
        public int TotalActivities { get; set; }

        [JsonPropertyName("fileTypes")]
        public Dictionary<string, int> FileTypes { get; set; } = new Dictionary<string, int>();

        [JsonPropertyName("storageUsage")]
        public StorageUsage StorageUsage { get; set; } = new StorageUsage();

        [JsonPropertyName("activityTrend")]
        public List<ActivityDataPoint> ActivityTrend { get; set; } = new List<ActivityDataPoint>();

        [JsonPropertyName("lastBackup")]
        public DateTime? LastBackup { get; set; }

        [JsonPropertyName("completionPercentage")]
        public double CompletionPercentage { get; set; }
    }

    /// <summary>
    /// Project analytics DTO;
    /// </summary>
    public class ProjectAnalytics;
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; }

        [JsonPropertyName("timePeriod")]
        public TimePeriod TimePeriod { get; set; }

        [JsonPropertyName("userEngagement")]
        public UserEngagementMetrics UserEngagement { get; set; } = new UserEngagementMetrics();

        [JsonPropertyName("performanceMetrics")]
        public PerformanceMetrics PerformanceMetrics { get; set; } = new PerformanceMetrics();

        [JsonPropertyName("collaborationMetrics")]
        public CollaborationMetrics CollaborationMetrics { get; set; } = new CollaborationMetrics();

        [JsonPropertyName("storageMetrics")]
        public StorageMetrics StorageMetrics { get; set; } = new StorageMetrics();

        [JsonPropertyName("activityMetrics")]
        public ActivityMetrics ActivityMetrics { get; set; } = new ActivityMetrics();

        [JsonPropertyName("trends")]
        public ProjectTrends Trends { get; set; } = new ProjectTrends();

        [JsonPropertyName("recommendations")]
        public List<ProjectRecommendation> Recommendations { get; set; } = new List<ProjectRecommendation>();
    }

    /// <summary>
    /// Project member DTO;
    /// </summary>
    public class ProjectMember;
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("role")]
        public ProjectRole Role { get; set; }

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new List<string>();

        [JsonPropertyName("joinedAt")]
        public DateTime JoinedAt { get; set; }

        [JsonPropertyName("lastActive")]
        public DateTime? LastActive { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; } = true;

        [JsonPropertyName("contributionStats")]
        public ContributionStats ContributionStats { get; set; } = new ContributionStats();

        [JsonPropertyName("notes")]
        public string Notes { get; set; }
    }

    /// <summary>
    /// Project permissions DTO;
    /// </summary>
    public class ProjectPermissions;
    {
        [JsonPropertyName("canView")]
        public bool CanView { get; set; }

        [JsonPropertyName("canEdit")]
        public bool CanEdit { get; set; }

        [JsonPropertyName("canDelete")]
        public bool CanDelete { get; set; }

        [JsonPropertyName("canManageMembers")]
        public bool CanManageMembers { get; set; }

        [JsonPropertyName("canManageSettings")]
        public bool CanManageSettings { get; set; }

        [JsonPropertyName("canExport")]
        public bool CanExport { get; set; }

        [JsonPropertyName("canInvite")]
        public bool CanInvite { get; set; }

        [JsonPropertyName("customPermissions")]
        public List<string> CustomPermissions { get; set; } = new List<string>();
    }
    #endregion;

    #region Supporting DTOs;
    /// <summary>
    /// Project filter DTO;
    /// </summary>
    public class ProjectFilter;
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("searchTerm")]
        public string SearchTerm { get; set; }

        [JsonPropertyName("status")]
        public ProjectStatus? Status { get; set; }

        [JsonPropertyName("type")]
        public ProjectType? Type { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonPropertyName("createdAfter")]
        public DateTime? CreatedAfter { get; set; }

        [JsonPropertyName("createdBefore")]
        public DateTime? CreatedBefore { get; set; }

        [JsonPropertyName("modifiedAfter")]
        public DateTime? ModifiedAfter { get; set; }

        [JsonPropertyName("modifiedBefore")]
        public DateTime? ModifiedBefore { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; } = 1;

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; } = 50;

        [JsonPropertyName("sortBy")]
        public string SortBy { get; set; } = "ModifiedAt";

        [JsonPropertyName("sortDirection")]
        public SortDirection SortDirection { get; set; } = SortDirection.Descending;
    }

    /// <summary>
    /// Version control settings DTO;
    /// </summary>
    public class VersionControlSettings;
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("autoCommit")]
        public bool AutoCommit { get; set; } = false;

        [JsonPropertyName("commitMessageTemplate")]
        public string CommitMessageTemplate { get; set; }

        [JsonPropertyName("maxRevisionHistory")]
        public int MaxRevisionHistory { get; set; } = 100;

        [JsonPropertyName("branchingStrategy")]
        public string BranchingStrategy { get; set; } = "simple";
    }

    /// <summary>
    /// Backup settings DTO;
    /// </summary>
    public class BackupSettings;
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("autoBackup")]
        public bool AutoBackup { get; set; } = true;

        [JsonPropertyName("backupInterval")]
        public TimeSpan BackupInterval { get; set; } = TimeSpan.FromHours(24);

        [JsonPropertyName("retentionPeriod")]
        public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(30);

        [JsonPropertyName("backupLocation")]
        public string BackupLocation { get; set; }

        [JsonPropertyName("encryptBackups")]
        public bool EncryptBackups { get; set; } = true;
    }

    /// <summary>
    /// Collaboration settings DTO;
    /// </summary>
    public class CollaborationSettings;
    {
        [JsonPropertyName("allowComments")]
        public bool AllowComments { get; set; } = true;

        [JsonPropertyName("allowAnnotations")]
        public bool AllowAnnotations { get; set; } = true;

        [JsonPropertyName("requireReview")]
        public bool RequireReview { get; set; } = false;

        [JsonPropertyName("autoNotifyMembers")]
        public bool AutoNotifyMembers { get; set; } = true;

        [JsonPropertyName("sharedWorkspace")]
        public bool SharedWorkspace { get; set; } = true;

        [JsonPropertyName("concurrentEditing")]
        public bool ConcurrentEditing { get; set; } = true;
    }

    /// <summary>
    /// Project security settings DTO;
    /// </summary>
    public class ProjectSecuritySettings;
    {
        [JsonPropertyName("isPublic")]
        public bool IsPublic { get; set; } = false;

        [JsonPropertyName("requireAuthentication")]
        public bool RequireAuthentication { get; set; } = true;

        [JsonPropertyName("allowedDomains")]
        public List<string> AllowedDomains { get; set; } = new List<string>();

        [JsonPropertyName("ipRestrictions")]
        public List<string> IpRestrictions { get; set; } = new List<string>();

        [JsonPropertyName("encryptionLevel")]
        public EncryptionLevel EncryptionLevel { get; set; } = EncryptionLevel.Standard;

        [JsonPropertyName("auditLogging")]
        public bool AuditLogging { get; set; } = true;

        [JsonPropertyName("twoFactorRequired")]
        public bool TwoFactorRequired { get; set; } = false;
    }

    /// <summary>
    /// Notification settings DTO;
    /// </summary>
    public class NotificationSettings;
    {
        [JsonPropertyName("emailNotifications")]
        public bool EmailNotifications { get; set; } = true;

        [JsonPropertyName("pushNotifications")]
        public bool PushNotifications { get; set; } = true;

        [JsonPropertyName("activityDigest")]
        public bool ActivityDigest { get; set; } = true;

        [JsonPropertyName("digestFrequency")]
        public DigestFrequency DigestFrequency { get; set; } = DigestFrequency.Daily;

        [JsonPropertyName("notifyOnChanges")]
        public bool NotifyOnChanges { get; set; } = true;

        [JsonPropertyName("notifyOnComments")]
        public bool NotifyOnComments { get; set; } = true;

        [JsonPropertyName("quietHours")]
        public QuietHours QuietHours { get; set; } = new QuietHours();
    }

    /// <summary>
    /// Workflow settings DTO;
    /// </summary>
    public class WorkflowSettings;
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("approvalRequired")]
        public bool ApprovalRequired { get; set; } = false;

        [JsonPropertyName("autoAssign")]
        public bool AutoAssign { get; set; } = false;

        [JsonPropertyName("stages")]
        public List<WorkflowStage> Stages { get; set; } = new List<WorkflowStage>();

        [JsonPropertyName("autoAdvance")]
        public bool AutoAdvance { get; set; } = false;
    }

    /// <summary>
    /// Storage usage DTO;
    /// </summary>
    public class StorageUsage;
    {
        [JsonPropertyName("totalBytes")]
        public long TotalBytes { get; set; }

        [JsonPropertyName("usedBytes")]
        public long UsedBytes { get; set; }

        [JsonPropertyName("availableBytes")]
        public long AvailableBytes { get; set; }

        [JsonPropertyName("usagePercentage")]
        public double UsagePercentage { get; set; }

        [JsonPropertyName("quota")]
        public long Quota { get; set; }

        [JsonPropertyName("quotaEnabled")]
        public bool QuotaEnabled { get; set; }
    }

    /// <summary>
    /// User engagement metrics DTO;
    /// </summary>
    public class UserEngagementMetrics;
    {
        [JsonPropertyName("activeUsers")]
        public int ActiveUsers { get; set; }

        [JsonPropertyName("totalSessions")]
        public int TotalSessions { get; set; }

        [JsonPropertyName("averageSessionDuration")]
        public TimeSpan AverageSessionDuration { get; set; }

        [JsonPropertyName("userRetentionRate")]
        public double UserRetentionRate { get; set; }

        [JsonPropertyName("featureUsage")]
        public Dictionary<string, int> FeatureUsage { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Performance metrics DTO;
    /// </summary>
    public class PerformanceMetrics;
    {
        [JsonPropertyName("responseTime")]
        public TimeSpan ResponseTime { get; set; }

        [JsonPropertyName("throughput")]
        public double Throughput { get; set; }

        [JsonPropertyName("errorRate")]
        public double ErrorRate { get; set; }

        [JsonPropertyName("uptime")]
        public double Uptime { get; set; }

        [JsonPropertyName("resourceUsage")]
        public ResourceUsage ResourceUsage { get; set; } = new ResourceUsage();
    }

    /// <summary>
    /// Collaboration metrics DTO;
    /// </summary>
    public class CollaborationMetrics;
    {
        [JsonPropertyName("totalCollaborators")]
        public int TotalCollaborators { get; set; }

        [JsonPropertyName("activeCollaborators")]
        public int ActiveCollaborators { get; set; }

        [JsonPropertyName("commentsCount")]
        public int CommentsCount { get; set; }

        [JsonPropertyName("sharesCount")]
        public int SharesCount { get; set; }

        [JsonPropertyName("collaborationScore")]
        public double CollaborationScore { get; set; }
    }

    /// <summary>
    /// Project trends DTO;
    /// </summary>
    public class ProjectTrends;
    {
        [JsonPropertyName("growthRate")]
        public double GrowthRate { get; set; }

        [JsonPropertyName("adoptionRate")]
        public double AdoptionRate { get; set; }

        [JsonPropertyName("engagementTrend")]
        public TrendDirection EngagementTrend { get; set; }

        [JsonPropertyName("performanceTrend")]
        public TrendDirection PerformanceTrend { get; set; }

        [JsonPropertyName("storageTrend")]
        public TrendDirection StorageTrend { get; set; }
    }

    /// <summary>
    /// Project recommendation DTO;
    /// </summary>
    public class ProjectRecommendation;
    {
        [JsonPropertyName("type")]
        public RecommendationType Type { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("priority")]
        public RecommendationPriority Priority { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; }

        [JsonPropertyName("impact")]
        public string Impact { get; set; }
    }
    #endregion;

    #region Additional Supporting DTOs;
    public class ActivityDataPoint;
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }
    }

    public class ContributionStats;
    {
        [JsonPropertyName("filesCreated")]
        public int FilesCreated { get; set; }

        [JsonPropertyName("filesModified")]
        public int FilesModified { get; set; }

        [JsonPropertyName("commentsPosted")]
        public int CommentsPosted { get; set; }

        [JsonPropertyName("tasksCompleted")]
        public int TasksCompleted { get; set; }

        [JsonPropertyName("totalContributions")]
        public int TotalContributions { get; set; }
    }

    public class StorageMetrics;
    {
        [JsonPropertyName("totalProjects")]
        public int TotalProjects { get; set; }

        [JsonPropertyName("totalStorageUsed")]
        public long TotalStorageUsed { get; set; }

        [JsonPropertyName("averageProjectSize")]
        public long AverageProjectSize { get; set; }

        [JsonPropertyName("storageTrend")]
        public List<StorageDataPoint> StorageTrend { get; set; } = new List<StorageDataPoint>();
    }

    public class ActivityMetrics;
    {
        [JsonPropertyName("totalActivities")]
        public int TotalActivities { get; set; }

        [JsonPropertyName("activitiesPerDay")]
        public double ActivitiesPerDay { get; set; }

        [JsonPropertyName("peakActivityHours")]
        public List<int> PeakActivityHours { get; set; } = new List<int>();

        [JsonPropertyName("userActivityDistribution")]
        public Dictionary<string, int> UserActivityDistribution { get; set; } = new Dictionary<string, int>();
    }

    public class ResourceUsage;
    {
        [JsonPropertyName("cpuUsage")]
        public double CpuUsage { get; set; }

        [JsonPropertyName("memoryUsage")]
        public double MemoryUsage { get; set; }

        [JsonPropertyName("diskUsage")]
        public double DiskUsage { get; set; }

        [JsonPropertyName("networkUsage")]
        public double NetworkUsage { get; set; }
    }

    public class StorageDataPoint;
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("bytesUsed")]
        public long BytesUsed { get; set; }
    }

    public class WorkflowStage;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("requiredApprovals")]
        public int RequiredApprovals { get; set; }

        [JsonPropertyName("approvers")]
        public List<string> Approvers { get; set; } = new List<string>();
    }

    public class QuietHours;
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("startTime")]
        public TimeSpan StartTime { get; set; } = new TimeSpan(22, 0, 0); // 10 PM;

        [JsonPropertyName("endTime")]
        public TimeSpan EndTime { get; set; } = new TimeSpan(6, 0, 0); // 6 AM;

        [JsonPropertyName("timezone")]
        public string Timezone { get; set; } = "UTC";
    }
    #endregion;

    #region Enums;
    public enum ProjectType;
    {
        General,
        Animation,
        GameDevelopment,
        Architecture,
        Engineering,
        Research,
        Education,
        Marketing,
        Design,
        SoftwareDevelopment,
        DataAnalysis,
        ArtificialIntelligence,
        VirtualReality,
        FilmProduction,
        MusicProduction;
    }

    public enum ProjectStatus;
    {
        Draft,
        Active,
        Paused,
        Completed,
        Archived,
        Cancelled,
        OnHold;
    }

    public enum ProjectRole;
    {
        Owner,
        Admin,
        Editor,
        Contributor,
        Viewer,
        Guest;
    }

    public enum EncryptionLevel;
    {
        None,
        Standard,
        High,
        Maximum;
    }

    public enum DigestFrequency;
    {
        Never,
        RealTime,
        Hourly,
        Daily,
        Weekly;
    }

    public enum TimePeriod;
    {
        Last24Hours,
        Last7Days,
        Last30Days,
        Last90Days,
        LastYear,
        Custom;
    }

    public enum TrendDirection;
    {
        Improving,
        Stable,
        Declining,
        Volatile;
    }

    public enum RecommendationType;
    {
        Performance,
        Security,
        Collaboration,
        Storage,
        Backup,
        CostOptimization;
    }

    public enum RecommendationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }
    #endregion;
}
