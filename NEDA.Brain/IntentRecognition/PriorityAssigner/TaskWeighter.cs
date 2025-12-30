using Microsoft.Extensions.Logging;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.Messaging.EventBus;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.Brain.IntentRecognition.PriorityAssigner;
{
    /// <summary>
    /// AI sisteminin iş yükünü yönetmek, görev önceliklerini belirlemek; 
    /// ve kaynakları optimize etmek için gelişmiş ağırlıklandırma ve önceliklendirme sistemi.
    /// </summary>
    public interface ITaskWeighter : IDisposable
    {
        /// <summary>
        /// Görev için ağırlık ve öncelik hesaplar;
        /// </summary>
        Task<WeightedTask> CalculateTaskWeightAsync(TaskRequest request);

        /// <summary>
        /// Görev listesi için öncelik sıralaması yapar;
        /// </summary>
        Task<List<WeightedTask>> PrioritizeTasksAsync(List<TaskRequest> tasks);

        /// <summary>
        /// Sistem kaynaklarını analiz eder ve kapasite hesaplar;
        /// </summary>
        Task<SystemCapacity> AnalyzeSystemCapacityAsync();

        /// <summary>
        /// Kritik görevleri tespit eder;
        /// </summary>
        Task<List<CriticalTask>> IdentifyCriticalTasksAsync(List<WeightedTask> tasks);

        /// <summary>
        /// Görev yürütme planı oluşturur;
        /// </summary>
        Task<ExecutionPlan> CreateExecutionPlanAsync(List<WeightedTask> tasks, ExecutionConstraints constraints);

        /// <summary>
        /// Gerçek zamanlı öncelik ayarlaması yapar;
        /// </summary>
        Task<PriorityAdjustment> AdjustPriorityInRealTimeAsync(WeightedTask task,
            RealTimeContext context);

        /// <summary>
        /// Görev bağımlılıklarını analiz eder;
        /// </summary>
        Task<DependencyAnalysis> AnalyzeDependenciesAsync(WeightedTask task);

        /// <summary>
        /// Çakışan görevleri çözer;
        /// </summary>
        Task<ConflictResolution> ResolveTaskConflictsAsync(List<WeightedTask> tasks);

        /// <summary>
        /// Öncelik stratejisini günceller;
        /// </summary>
        Task UpdateWeightingStrategyAsync(WeightingStrategy newStrategy);

        /// <summary>
        /// Tarihsel verilere dayalı öncelik tahmini yapar;
        /// </summary>
        Task<PriorityPrediction> PredictFuturePrioritiesAsync(TimeSpan lookahead);

        /// <summary>
        /// Görev yük dengelemesi yapar;
        /// </summary>
        Task<LoadBalancingResult> BalanceTaskLoadAsync(List<WeightedTask> tasks);

        /// <summary>
        /// Acil durum önceliklendirmesi yapar;
        /// </summary>
        Task<EmergencyPrioritization> HandleEmergencySituationAsync(EmergencyContext context);

        /// <summary>
        /// Kullanıcı tercihlerine göre öncelik ayarlaması yapar;
        /// </summary>
        Task<UserPriorityAdjustment> AdjustForUserPreferencesAsync(WeightedTask task,
            UserPreferences preferences);

        /// <summary>
        /// Önceliklendirme istatistiklerini getirir;
        /// </summary>
        Task<WeightingStatistics> GetStatisticsAsync();

        /// <summary>
        /// Öncelik cache'ini temizler;
        /// </summary>
        Task ClearCacheAsync();
    }

    /// <summary>
    /// Görev isteği;
    /// </summary>
    public class TaskRequest;
    {
        [JsonProperty("taskId")]
        public string TaskId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("type")]
        public TaskType Type { get; set; }

        [JsonProperty("category")]
        public TaskCategory Category { get; set; }

        [JsonProperty("complexity")]
        public ComplexityLevel Complexity { get; set; } = ComplexityLevel.Medium;

        [JsonProperty("estimatedDuration")]
        public TimeSpan EstimatedDuration { get; set; }

        [JsonProperty("deadline")]
        public DateTime? Deadline { get; set; }

        [JsonProperty("priorityHint")]
        public PriorityLevel PriorityHint { get; set; } = PriorityLevel.Normal;

        [JsonProperty("userImportance")]
        public int UserImportance { get; set; } = 5; // 1-10;

        [JsonProperty("systemCriticality")]
        public int SystemCriticality { get; set; } = 5; // 1-10;

        [JsonProperty("resourceRequirements")]
        public ResourceRequirements Resources { get; set; } = new ResourceRequirements();

        [JsonProperty("dependencies")]
        public List<string> Dependencies { get; set; } = new List<string>();

        [JsonProperty("constraints")]
        public TaskConstraints Constraints { get; set; } = new TaskConstraints();

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("source")]
        public TaskSource Source { get; set; }
    }

    /// <summary>
    /// Ağırlıklandırılmış görev;
    /// </summary>
    public class WeightedTask;
    {
        [JsonProperty("taskRequest")]
        public TaskRequest Request { get; set; }

        [JsonProperty("calculatedWeight")]
        public double CalculatedWeight { get; set; }

        [JsonProperty("priorityScore")]
        public double PriorityScore { get; set; }

        [JsonProperty("priorityLevel")]
        public PriorityLevel PriorityLevel { get; set; }

        [JsonProperty("urgencyScore")]
        public double UrgencyScore { get; set; }

        [JsonProperty("importanceScore")]
        public double ImportanceScore { get; set; }

        [JsonProperty("complexityScore")]
        public double ComplexityScore { get; set; }

        [JsonProperty("resourceScore")]
        public double ResourceScore { get; set; }

        [JsonProperty("dependencyScore")]
        public double DependencyScore { get; set; }

        [JsonProperty("riskScore")]
        public double RiskScore { get; set; }

        [JsonProperty("userPreferenceScore")]
        public double UserPreferenceScore { get; set; }

        [JsonProperty("systemLoadScore")]
        public double SystemLoadScore { get; set; }

        [JsonProperty("finalPriority")]
        public double FinalPriority { get; set; }

        [JsonProperty("executionWindow")]
        public ExecutionWindow RecommendedWindow { get; set; }

        [JsonProperty("weightBreakdown")]
        public WeightBreakdown Breakdown { get; set; } = new WeightBreakdown();

        [JsonProperty("suggestedActions")]
        public List<PriorityAction> SuggestedActions { get; set; } = new List<PriorityAction>();

        [JsonProperty("calculatedAt")]
        public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("version")]
        public int Version { get; set; } = 1;
    }

    /// <summary>
    /// Ağırlık dağılımı;
    /// </summary>
    public class WeightBreakdown;
    {
        public Dictionary<string, double> Factors { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, string> Explanations { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, double> RawScores { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> NormalizedScores { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Öncelik seviyeleri;
    /// </summary>
    public enum PriorityLevel;
    {
        Emergency = 0,      // Hemen çalıştırılmalı;
        Critical = 1,       // Kritik - saatler içinde;
        High = 2,          // Yüksek - gün içinde;
        Normal = 3,        // Normal - 1-3 gün;
        Low = 4,           // Düşük - hafta içinde;
        Background = 5      // Arka plan - boş zamanda;
    }

    /// <summary>
    /// Görev türleri;
    /// </summary>
    public enum TaskType;
    {
        Processing,
        Analysis,
        Generation,
        Validation,
        Optimization,
        Learning,
        Monitoring,
        Maintenance,
        UserInteraction,
        SystemOperation,
        Backup,
        Cleanup,
        Report,
        Integration,
        Custom;
    }

    /// <summary>
    /// Görev kategorileri;
    /// </summary>
    public enum TaskCategory;
    {
        AI,
        System,
        Security,
        Data,
        Network,
        User,
        Administrative,
        Technical,
        Business,
        Development,
        Testing,
        Deployment,
        Support,
        Research;
    }

    /// <summary>
    /// Karmaşıklık seviyeleri;
    /// </summary>
    public enum ComplexityLevel;
    {
        Trivial,    // < 1 dakika;
        Simple,     // 1-5 dakika;
        Medium,     // 5-30 dakika;
        Complex,    // 30 dakika - 2 saat;
        VeryComplex, // 2-8 saat;
        Extreme     // > 8 saat;
    }

    /// <summary>
    /// Kaynak gereksinimleri;
    /// </summary>
    public class ResourceRequirements;
    {
        public double CpuUsage { get; set; } = 0.1; // 0-1;
        public long MemoryMb { get; set; } = 100;
        public long DiskMb { get; set; } = 10;
        public int ThreadCount { get; set; } = 1;
        public bool RequiresGpu { get; set; } = false;
        public double GpuMemoryGb { get; set; } = 0;
        public bool RequiresNetwork { get; set; } = false;
        public int NetworkBandwidthMbps { get; set; } = 1;
        public List<string> RequiredServices { get; set; } = new List<string>();
        public Dictionary<string, object> SpecialRequirements { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Görev kısıtlamaları;
    /// </summary>
    public class TaskConstraints;
    {
        public bool RequiresHumanApproval { get; set; } = false;
        public List<string> RequiredPermissions { get; set; } = new List<string>();
        public TimeSpan MaxExecutionTime { get; set; } = TimeSpan.FromHours(1);
        public int RetryCount { get; set; } = 3;
        public bool AllowParallelExecution { get; set; } = true;
        public bool AllowPartialSuccess { get; set; } = false;
        public List<TimeWindow> AllowedTimeWindows { get; set; } = new List<TimeWindow>();
        public Dictionary<string, object> CustomConstraints { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Zaman penceresi;
    /// </summary>
    public class TimeWindow;
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public List<DayOfWeek> Days { get; set; } = new List<DayOfWeek>();
    }

    /// <summary>
    /// Görev kaynağı;
    /// </summary>
    public enum TaskSource;
    {
        UserCommand,
        Automated,
        Scheduled,
        SystemEvent,
        ExternalAPI,
        Monitoring,
        Learning,
        Maintenance,
        Emergency;
    }

    /// <summary>
    /// Sistem kapasitesi;
    /// </summary>
    public class SystemCapacity;
    {
        public double AvailableCpu { get; set; }
        public long AvailableMemoryMb { get; set; }
        public long AvailableDiskMb { get; set; }
        public int AvailableThreads { get; set; }
        public bool GpuAvailable { get; set; }
        public double GpuMemoryGb { get; set; }
        public double NetworkBandwidthMbps { get; set; }
        public Dictionary<string, ServiceStatus> ServiceStatuses { get; set; } = new Dictionary<string, ServiceStatus>();
        public LoadMetrics CurrentLoad { get; set; } = new LoadMetrics();
        public CapacityTrend Trend { get; set; }
        public DateTime MeasuredAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Yük metrikleri;
    /// </summary>
    public class LoadMetrics;
    {
        public double CpuLoad { get; set; }
        public double MemoryLoad { get; set; }
        public double DiskLoad { get; set; }
        public int ActiveThreads { get; set; }
        public int QueuedTasks { get; set; }
        public double NetworkLoad { get; set; }
        public Dictionary<string, double> ServiceLoads { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Servis durumu;
    /// </summary>
    public class ServiceStatus;
    {
        public bool IsAvailable { get; set; }
        public double CurrentLoad { get; set; }
        public int QueuedRequests { get; set; }
        public TimeSpan AverageResponseTime { get; set; }
        public HealthStatus Health { get; set; }
    }

    public enum HealthStatus;
    {
        Healthy,
        Warning,
        Critical,
        Offline;
    }

    public enum CapacityTrend;
    {
        Improving,
        Stable,
        Declining,
        Critical;
    }

    /// <summary>
    /// Kritik görev;
    /// </summary>
    public class CriticalTask;
    {
        public WeightedTask Task { get; set; }
        public CriticalityLevel Criticality { get; set; }
        public List<string> CriticalFactors { get; set; } = new List<string>();
        public DateTime DetectionTime { get; set; } = DateTime.UtcNow;
        public List<MitigationAction> MitigationActions { get; set; } = new List<MitigationAction>();
    }

    public enum CriticalityLevel;
    {
        Normal,
        Warning,
        Critical,
        Emergency;
    }

    /// <summary>
    /// Hafifletme eylemi;
    /// </summary>
    public class MitigationAction;
    {
        public string Action { get; set; }
        public ActionType Type { get; set; }
        public int EstimatedEffectiveness { get; set; } // 0-100;
        public TimeSpan EstimatedDuration { get; set; }
        public List<string> RequiredResources { get; set; } = new List<string>();
    }

    public enum ActionType;
    {
        Optimize,
        Scale,
        Restart,
        Notify,
        Escalate,
        Cancel,
        Delay;
    }

    /// <summary>
    /// Yürütme planı;
    /// </summary>
    public class ExecutionPlan;
    {
        public string PlanId { get; set; } = Guid.NewGuid().ToString();
        public List<PlannedTask> Tasks { get; set; } = new List<PlannedTask>();
        public DateTime StartTime { get; set; }
        public DateTime EstimatedCompletion { get; set; }
        public double TotalWeight { get; set; }
        public ResourceAllocation ResourceAllocation { get; set; } = new ResourceAllocation();
        public RiskAssessment Risks { get; set; } = new RiskAssessment();
        public List<ContingencyPlan> Contingencies { get; set; } = new List<ContingencyPlan>();
        public PlanStatus Status { get; set; } = PlanStatus.Draft;
    }

    /// <summary>
    /// Planlanmış görev;
    /// </summary>
    public class PlannedTask;
    {
        public WeightedTask Task { get; set; }
        public DateTime ScheduledStart { get; set; }
        public DateTime ScheduledEnd { get; set; }
        public int Order { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public ResourceAllocation AllocatedResources { get; set; } = new ResourceAllocation();
        public double Progress { get; set; }
        public TaskStatus Status { get; set; }
    }

    public enum TaskStatus;
    {
        Pending,
        Scheduled,
        Running,
        Paused,
        Completed,
        Failed,
        Cancelled;
    }

    public enum PlanStatus;
    {
        Draft,
        Approved,
        Executing,
        Completed,
        Cancelled,
        Failed;
    }

    /// <summary>
    /// Kaynak tahsisi;
    /// </summary>
    public class ResourceAllocation;
    {
        public double CpuCores { get; set; }
        public long MemoryMb { get; set; }
        public long DiskMb { get; set; }
        public int Threads { get; set; }
        public bool GpuAllocated { get; set; }
        public double GpuMemoryGb { get; set; }
        public double NetworkBandwidthMbps { get; set; }
        public Dictionary<string, object> ServiceAllocations { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Risk değerlendirmesi;
    /// </summary>
    public class RiskAssessment;
    {
        public double OverallRisk { get; set; }
        public Dictionary<string, double> RiskFactors { get; set; } = new Dictionary<string, double>();
        public List<IdentifiedRisk> IdentifiedRisks { get; set; } = new List<IdentifiedRisk>();
        public List<RiskMitigation> Mitigations { get; set; } = new List<RiskMitigation>();
    }

    public class IdentifiedRisk;
    {
        public string RiskId { get; set; }
        public string Description { get; set; }
        public RiskLevel Level { get; set; }
        public double Probability { get; set; }
        public double Impact { get; set; }
        public List<string> AffectedTasks { get; set; } = new List<string>();
    }

    public enum RiskLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public class RiskMitigation;
    {
        public string RiskId { get; set; }
        public string Mitigation { get; set; }
        public double Effectiveness { get; set; }
        public TimeSpan ImplementationTime { get; set; }
    }

    /// <summary>
    /// Yedek plan;
    /// </summary>
    public class ContingencyPlan;
    {
        public string TriggerCondition { get; set; }
        public string Action { get; set; }
        public int Priority { get; set; }
        public List<string> RequiredResources { get; set; } = new List<string>();
        public TimeSpan EstimatedActivationTime { get; set; }
    }

    /// <summary>
    /// Yürütme kısıtlamaları;
    /// </summary>
    public class ExecutionConstraints;
    {
        public DateTime MustStartBy { get; set; }
        public DateTime MustCompleteBy { get; set; }
        public double MaxCpuUsage { get; set; } = 0.8;
        public long MaxMemoryMb { get; set; } = long.MaxValue;
        public int MaxConcurrentTasks { get; set; } = 10;
        public List<string> ExcludedTimeWindows { get; set; } = new List<string>();
        public Dictionary<string, object> CustomConstraints { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Gerçek zamanlı bağlam;
    /// </summary>
    public class RealTimeContext;
    {
        public SystemCapacity CurrentCapacity { get; set; }
        public List<WeightedTask> ActiveTasks { get; set; } = new List<WeightedTask>();
        public List<SystemEvent> RecentEvents { get; set; } = new List<SystemEvent>();
        public UserActivity UserActivity { get; set; }
        public DateTime ContextTime { get; set; } = DateTime.UtcNow;
    }

    public class SystemEvent;
    {
        public string EventId { get; set; }
        public string Type { get; set; }
        public DateTime OccurredAt { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    public class UserActivity;
    {
        public bool IsActive { get; set; }
        public string CurrentActivity { get; set; }
        public DateTime LastInteraction { get; set; }
        public int InteractionFrequency { get; set; }
    }

    /// <summary>
    /// Öncelik ayarlaması;
    /// </summary>
    public class PriorityAdjustment;
    {
        public WeightedTask OriginalTask { get; set; }
        public WeightedTask AdjustedTask { get; set; }
        public List<AdjustmentReason> Reasons { get; set; } = new List<AdjustmentReason>();
        public double AdjustmentMagnitude { get; set; }
        public DateTime AdjustedAt { get; set; } = DateTime.UtcNow;
    }

    public class AdjustmentReason;
    {
        public string Reason { get; set; }
        public string Category { get; set; }
        public double Impact { get; set; }
    }

    /// <summary>
    /// Bağımlılık analizi;
    /// </summary>
    public class DependencyAnalysis;
    {
        public WeightedTask Task { get; set; }
        public List<Dependency> DirectDependencies { get; set; } = new List<Dependency>();
        public List<Dependency> IndirectDependencies { get; set; } = new List<Dependency>();
        public DependencyGraph DependencyGraph { get; set; }
        public CriticalPathAnalysis CriticalPath { get; set; }
        public List<DependencyRisk> Risks { get; set; } = new List<DependencyRisk>();
    }

    public class Dependency;
    {
        public string TaskId { get; set; }
        public DependencyType Type { get; set; }
        public DependencyStrength Strength { get; set; }
        public TimeSpan RequiredLeadTime { get; set; }
    }

    public enum DependencyType;
    {
        Hard,       // Mutlak bağımlılık;
        Soft,       // Tercih edilen sıralama;
        Resource,   // Kaynak bağımlılığı;
        Temporal,   // Zaman bağımlılığı;
        Data        // Veri bağımlılığı;
    }

    public enum DependencyStrength;
    {
        Weak,
        Moderate,
        Strong,
        Critical;
    }

    /// <summary>
    /// Bağımlılık grafiği;
    /// </summary>
    public class DependencyGraph;
    {
        public Dictionary<string, List<string>> AdjacencyList { get; set; } = new Dictionary<string, List<string>>();
        public List<GraphNode> Nodes { get; set; } = new List<GraphNode>();
        public List<GraphEdge> Edges { get; set; } = new List<GraphEdge>();
    }

    public class GraphNode;
    {
        public string TaskId { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    public class GraphEdge;
    {
        public string FromTaskId { get; set; }
        public string ToTaskId { get; set; }
        public DependencyType Type { get; set; }
        public double Weight { get; set; }
    }

    /// <summary>
    /// Kritik yol analizi;
    /// </summary>
    public class CriticalPathAnalysis;
    {
        public List<string> CriticalPath { get; set; } = new List<string>();
        public TimeSpan CriticalPathDuration { get; set; }
        public double TotalFloat { get; set; }
        public List<string> BottleneckTasks { get; set; } = new List<string>();
    }

    /// <summary>
    /// Bağımlılık riski;
    /// </summary>
    public class DependencyRisk;
    {
        public string DependencyId { get; set; }
        public string RiskDescription { get; set; }
        public RiskLevel Level { get; set; }
        public List<string> AffectedTasks { get; set; } = new List<string>();
    }

    /// <summary>
    /// Çakışma çözümü;
    /// </summary>
    public class ConflictResolution;
    {
        public List<WeightedTask> ConflictingTasks { get; set; } = new List<WeightedTask>();
        public ConflictType Type { get; set; }
        public ResolutionStrategy Strategy { get; set; }
        public List<ResolutionAction> Actions { get; set; } = new List<ResolutionAction>();
        public WeightedTask SelectedTask { get; set; }
        public List<WeightedTask> DeferredTasks { get; set; } = new List<WeightedTask>();
        public List<WeightedTask> CancelledTasks { get; set; } = new List<WeightedTask>();
    }

    public enum ConflictType;
    {
        Resource,
        Dependency,
        Temporal,
        Priority,
        Logical;
    }

    public enum ResolutionStrategy;
    {
        HighestPriority,
        EarliestDeadline,
        LeastResources,
        RoundRobin,
        UserDecision,
        SystemOptimized;
    }

    public class ResolutionAction;
    {
        public string Action { get; set; }
        public string TaskId { get; set; }
        public ActionType Type { get; set; }
        public DateTime AppliedAt { get; set; }
    }

    /// <summary>
    /// Ağırlıklandırma stratejisi;
    /// </summary>
    public class WeightingStrategy;
    {
        public string StrategyId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public Dictionary<string, double> FactorWeights { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, Func<TaskRequest, double>> CustomFactors { get; set; } = new Dictionary<string, Func<TaskRequest, double>>();
        public List<WeightingRule> Rules { get; set; } = new List<WeightingRule>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class WeightingRule;
    {
        public string RuleId { get; set; }
        public string Condition { get; set; }
        public string Action { get; set; }
        public double WeightMultiplier { get; set; } = 1.0;
        public int Priority { get; set; }
    }

    /// <summary>
    /// Öncelik tahmini;
    /// </summary>
    public class PriorityPrediction;
    {
        public DateTime PredictionTime { get; set; } = DateTime.UtcNow;
        public TimeSpan Lookahead { get; set; }
        public List<PredictedPriority> Predictions { get; set; } = new List<PredictedPriority>();
        public double PredictionConfidence { get; set; }
        public Dictionary<string, double> InfluencingFactors { get; set; } = new Dictionary<string, double>();
    }

    public class PredictedPriority;
    {
        public string TaskType { get; set; }
        public PriorityLevel PredictedLevel { get; set; }
        public double Probability { get; set; }
        public DateTime ExpectedTime { get; set; }
    }

    /// <summary>
    /// Yük dengeleme sonucu;
    /// </summary>
    public class LoadBalancingResult;
    {
        public List<LoadDistribution> Distributions { get; set; } = new List<LoadDistribution>();
        public double BeforeImbalance { get; set; }
        public double AfterImbalance { get; set; }
        public double Improvement { get; set; }
        public List<BalancingAction> ActionsTaken { get; set; } = new List<BalancingAction>();
    }

    public class LoadDistribution;
    {
        public string ResourceType { get; set; }
        public Dictionary<string, double> NodeLoads { get; set; } = new Dictionary<string, double>();
        public double AverageLoad { get; set; }
        public double StandardDeviation { get; set; }
    }

    public class BalancingAction;
    {
        public string Action { get; set; }
        public string ResourceType { get; set; }
        public string FromNode { get; set; }
        public string ToNode { get; set; }
        public double Amount { get; set; }
    }

    /// <summary>
    /// Acil durum bağlamı;
    /// </summary>
    public class EmergencyContext;
    {
        public EmergencyType Type { get; set; }
        public EmergencySeverity Severity { get; set; }
        public List<string> AffectedSystems { get; set; } = new List<string>();
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> ContextData { get; set; } = new Dictionary<string, object>();
    }

    public enum EmergencyType;
    {
        SystemFailure,
        SecurityBreach,
        ResourceExhaustion,
        DataLoss,
        PerformanceDegradation,
        NetworkOutage,
        ExternalThreat,
        NaturalEvent;
    }

    public enum EmergencySeverity;
    {
        Minor,
        Moderate,
        Severe,
        Critical,
        Catastrophic;
    }

    /// <summary>
    /// Acil durum önceliklendirmesi;
    /// </summary>
    public class EmergencyPrioritization;
    {
        public EmergencyContext Context { get; set; }
        public List<EmergencyTask> EmergencyTasks { get; set; } = new List<EmergencyTask>();
        public List<WeightedTask> SuspendedTasks { get; set; } = new List<WeightedTask>();
        public EmergencyPlan ActivatedPlan { get; set; }
        public DateTime ActivatedAt { get; set; } = DateTime.UtcNow;
    }

    public class EmergencyTask;
    {
        public string TaskId { get; set; }
        public string Description { get; set; }
        public EmergencyPriority Priority { get; set; }
        public TimeSpan MaxCompletionTime { get; set; }
        public List<string> RequiredResources { get; set; } = new List<string>();
    }

    public enum EmergencyPriority;
    {
        Immediate,
        Critical,
        High,
        Medium,
        Low;
    }

    public class EmergencyPlan;
    {
        public string PlanId { get; set; }
        public string Name { get; set; }
        public List<EmergencyProcedure> Procedures { get; set; } = new List<EmergencyProcedure>();
    }

    public class EmergencyProcedure;
    {
        public string ProcedureId { get; set; }
        public string Description { get; set; }
        public int StepNumber { get; set; }
        public List<string> RequiredRoles { get; set; } = new List<string>();
        public TimeSpan EstimatedDuration { get; set; }
    }

    /// <summary>
    /// Kullanıcı tercihi ayarlaması;
    /// </summary>
    public class UserPriorityAdjustment;
    {
        public WeightedTask OriginalTask { get; set; }
        public WeightedTask AdjustedTask { get; set; }
        public UserPreferences Preferences { get; set; }
        public List<PreferenceMatch> Matches { get; set; } = new List<PreferenceMatch>();
        public double AdjustmentFactor { get; set; }
    }

    public class UserPreferences;
    {
        public Dictionary<string, int> CategoryPriorities { get; set; } = new Dictionary<string, int>();
        public List<string> PreferredTaskTypes { get; set; } = new List<string>();
        public List<TimePreference> TimePreferences { get; set; } = new List<TimePreference>();
        public Dictionary<string, object> CustomPreferences { get; set; } = new Dictionary<string, object>();
    }

    public class TimePreference;
    {
        public DayOfWeek Day { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public int PreferenceWeight { get; set; } // 1-10;
    }

    public class PreferenceMatch;
    {
        public string PreferenceType { get; set; }
        public string MatchDescription { get; set; }
        public double MatchScore { get; set; }
    }

    /// <summary>
    /// Öncelik eylemi;
    /// </summary>
    public class PriorityAction;
    {
        public string Action { get; set; }
        public ActionType Type { get; set; }
        public int Priority { get; set; }
        public string Reason { get; set; }
        public TimeSpan EstimatedDuration { get; set; }
        public List<string> RequiredResources { get; set; } = new List<string>();
    }

    /// <summary>
    /// Yürütme penceresi;
    /// </summary>
    public class ExecutionWindow;
    {
        public DateTime RecommendedStart { get; set; }
        public DateTime RecommendedEnd { get; set; }
        public TimeSpan Duration { get; set; }
        public double Confidence { get; set; }
        public List<string> Constraints { get; set; } = new List<string>();
    }

    /// <summary>
    /// Ağırlıklandırma istatistikleri;
    /// </summary>
    public class WeightingStatistics;
    {
        public int TotalTasksWeighted { get; set; }
        public Dictionary<PriorityLevel, int> PriorityDistribution { get; set; } = new Dictionary<PriorityLevel, int>();
        public Dictionary<string, int> TaskTypeDistribution { get; set; } = new Dictionary<string, int>();
        public double AverageCalculationTimeMs { get; set; }
        public Dictionary<string, double> FactorEffectiveness { get; set; } = new Dictionary<string, double>();
        public List<WeightingTrend> Trends { get; set; } = new List<WeightingTrend>();
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class WeightingTrend;
    {
        public DateTime Period { get; set; }
        public int TaskCount { get; set; }
        public double AverageWeight { get; set; }
        public Dictionary<PriorityLevel, int> PriorityChanges { get; set; } = new Dictionary<PriorityLevel, int>();
    }

    /// <summary>
    /// TaskWeighter implementasyonu;
    /// </summary>
    public class TaskWeighter : ITaskWeighter;
    {
        private readonly ILogger<TaskWeighter> _logger;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IEventBus _eventBus;
        private readonly WeightingEngine _weightingEngine;
        private readonly PriorityCache _priorityCache;
        private readonly StatisticsTracker _statisticsTracker;
        private bool _disposed = false;
        private readonly object _lock = new object();

        // Configurable parameters;
        private readonly double _emergencyThreshold = 0.9;
        private readonly double _criticalThreshold = 0.7;
        private readonly double _highThreshold = 0.5;
        private readonly double _normalThreshold = 0.3;
        private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(30);

        public TaskWeighter(
            ILogger<TaskWeighter> logger,
            IDiagnosticTool diagnosticTool,
            IPerformanceMonitor performanceMonitor,
            IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _weightingEngine = new WeightingEngine(logger);
            _priorityCache = new PriorityCache(_cacheTtl);
            _statisticsTracker = new StatisticsTracker();

            _logger.LogInformation("TaskWeighter initialized");
        }

        public async Task<WeightedTask> CalculateTaskWeightAsync(TaskRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var cacheKey = $"task_weight_{request.TaskId}_{request.GetHashCode()}";

            return await _priorityCache.GetOrCreateAsync(cacheKey, async () =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    _logger.LogDebug("Calculating weight for task: {TaskId} - {TaskName}",
                        request.TaskId, request.Name);

                    // 1. Temel faktörleri hesapla;
                    var baseFactors = CalculateBaseFactors(request);

                    // 2. Sistem durumunu analiz et;
                    var systemCapacity = await AnalyzeSystemCapacityAsync();
                    var systemFactors = CalculateSystemFactors(request, systemCapacity);

                    // 3. Bağımlılıkları analiz et;
                    var dependencyFactors = await CalculateDependencyFactors(request);

                    // 4. Kullanıcı ve bağlam faktörleri;
                    var contextFactors = await CalculateContextFactors(request);

                    // 5. Tüm faktörleri birleştir;
                    var combinedFactors = CombineFactors(
                        baseFactors,
                        systemFactors,
                        dependencyFactors,
                        contextFactors);

                    // 6. Nihai ağırlık ve öncelik hesapla;
                    var weightedTask = CalculateFinalWeight(request, combinedFactors);

                    // 7. Öncelik seviyesini belirle;
                    weightedTask.PriorityLevel = DeterminePriorityLevel(weightedTask.FinalPriority);

                    // 8. Önerilen yürütme penceresini hesapla;
                    weightedTask.RecommendedWindow = CalculateExecutionWindow(weightedTask, systemCapacity);

                    // 9. Önerilen eylemleri belirle;
                    weightedTask.SuggestedActions = GenerateSuggestedActions(weightedTask);

                    stopwatch.Stop();
                    weightedTask.CalculatedAt = DateTime.UtcNow;

                    // İstatistikleri güncelle;
                    _statisticsTracker.RecordCalculation(weightedTask, stopwatch.Elapsed);

                    // Olay yayınla;
                    await _eventBus.PublishAsync(new TaskWeightCalculatedEvent;
                    {
                        TaskId = request.TaskId,
                        CalculatedWeight = weightedTask.CalculatedWeight,
                        PriorityLevel = weightedTask.PriorityLevel,
                        CalculationTime = stopwatch.Elapsed;
                    });

                    _logger.LogInformation("Task weight calculated: {TaskId} - Weight: {Weight}, Priority: {Priority}",
                        request.TaskId, weightedTask.CalculatedWeight, weightedTask.PriorityLevel);

                    return weightedTask;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to calculate weight for task: {TaskId}", request.TaskId);
                    throw new TaskWeightingException(
                        $"Failed to calculate weight for task: {request.TaskId}",
                        ex,
                        ErrorCodes.TaskWeighter.CalculationFailed);
                }
            });
        }

        private Dictionary<string, double> CalculateBaseFactors(TaskRequest request)
        {
            var factors = new Dictionary<string, double>();

            // 1. Aciliyet faktörü;
            factors["urgency"] = CalculateUrgencyFactor(request);

            // 2. Önem faktörü;
            factors["importance"] = CalculateImportanceFactor(request);

            // 3. Karmaşıklık faktörü;
            factors["complexity"] = CalculateComplexityFactor(request);

            // 4. Kaynak faktörü;
            factors["resource"] = CalculateResourceFactor(request);

            // 5. Risk faktörü;
            factors["risk"] = CalculateRiskFactor(request);

            return factors;
        }

        private double CalculateUrgencyFactor(TaskRequest request)
        {
            double score = 0.0;

            // Deadline'a göre aciliyet;
            if (request.Deadline.HasValue)
            {
                var timeUntilDeadline = request.Deadline.Value - DateTime.UtcNow;

                if (timeUntilDeadline <= TimeSpan.Zero)
                    score = 1.0; // Deadline geçmiş;
                else if (timeUntilDeadline <= TimeSpan.FromHours(1))
                    score = 0.9;
                else if (timeUntilDeadline <= TimeSpan.FromHours(4))
                    score = 0.7;
                else if (timeUntilDeadline <= TimeSpan.FromHours(24))
                    score = 0.5;
                else if (timeUntilDeadline <= TimeSpan.FromDays(3))
                    score = 0.3;
                else;
                    score = 0.1;
            }

            // Görev türüne göre aciliyet;
            switch (request.Type)
            {
                case TaskType.Monitoring:
                case TaskType.SystemOperation:
                    score = Math.Max(score, 0.6);
                    break;
                case TaskType.Maintenance:
                case TaskType.Backup:
                    score = Math.Max(score, 0.4);
                    break;
            }

            // Kullanıcı ipucu;
            score += (request.PriorityHint switch;
            {
                PriorityLevel.Emergency => 0.3,
                PriorityLevel.Critical => 0.2,
                PriorityLevel.High => 0.1,
                _ => 0.0;
            });

            return Math.Min(1.0, score);
        }

        private double CalculateImportanceFactor(TaskRequest request)
        {
            double score = 0.0;

            // Kullanıcı önemi;
            score += (request.UserImportance / 10.0) * 0.3;

            // Sistem kritikliği;
            score += (request.SystemCriticality / 10.0) * 0.4;

            // Görev kategorisine göre önem;
            switch (request.Category)
            {
                case TaskCategory.Security:
                case TaskCategory.System:
                    score += 0.2;
                    break;
                case TaskCategory.AI:
                case TaskCategory.Data:
                    score += 0.15;
                    break;
                case TaskCategory.User:
                case TaskCategory.Business:
                    score += 0.1;
                    break;
            }

            // Kaynak gereksinimleri;
            if (request.Resources.RequiresGpu || request.Resources.MemoryMb > 1024)
                score += 0.1;

            return Math.Min(1.0, score);
        }

        private double CalculateComplexityFactor(TaskRequest request)
        {
            double score = request.Complexity switch;
            {
                ComplexityLevel.Trivial => 0.1,
                ComplexityLevel.Simple => 0.2,
                ComplexityLevel.Medium => 0.4,
                ComplexityLevel.Complex => 0.6,
                ComplexityLevel.VeryComplex => 0.8,
                ComplexityLevel.Extreme => 1.0,
                _ => 0.5;
            };

            // Tahmini süre;
            if (request.EstimatedDuration > TimeSpan.FromHours(8))
                score = Math.Max(score, 0.9);
            else if (request.EstimatedDuration > TimeSpan.FromHours(2))
                score = Math.Max(score, 0.7);

            // Bağımlılık sayısı;
            if (request.Dependencies.Count > 5)
                score += 0.2;
            else if (request.Dependencies.Count > 0)
                score += 0.1;

            return Math.Min(1.0, score);
        }

        private double CalculateResourceFactor(TaskRequest request)
        {
            double score = 0.0;

            // CPU kullanımı;
            score += request.Resources.CpuUsage * 0.2;

            // Bellek kullanımı (normalize)
            var memoryScore = Math.Min(1.0, request.Resources.MemoryMb / 8192.0); // 8GB max;
            score += memoryScore * 0.2;

            // GPU gereksinimi;
            if (request.Resources.RequiresGpu)
                score += 0.3;

            // Thread sayısı;
            var threadScore = Math.Min(1.0, request.Resources.ThreadCount / 8.0);
            score += threadScore * 0.1;

            // Network gereksinimi;
            if (request.Resources.RequiresNetwork)
                score += 0.1;

            // Servis bağımlılıkları;
            if (request.Resources.RequiredServices.Count > 0)
                score += 0.1;

            return Math.Min(1.0, score);
        }

        private double CalculateRiskFactor(TaskRequest request)
        {
            double score = 0.0;

            // İnsan onayı gerekiyorsa risk daha yüksek;
            if (request.Constraints.RequiresHumanApproval)
                score += 0.2;

            // Kısıtlı yürütme süresi;
            if (request.Constraints.MaxExecutionTime < TimeSpan.FromMinutes(5))
                score += 0.3;

            // Kısıtlı yeniden deneme;
            if (request.Constraints.RetryCount == 0)
                score += 0.2;

            // Paralel yürütmeye izin vermiyor;
            if (!request.Constraints.AllowParallelExecution)
                score += 0.1;

            // Kısmi başarıya izin vermiyor;
            if (!request.Constraints.AllowPartialSuccess)
                score += 0.2;

            return Math.Min(1.0, score);
        }

        private Dictionary<string, double> CalculateSystemFactors(TaskRequest request, SystemCapacity capacity)
        {
            var factors = new Dictionary<string, double>();

            // Mevcut sistem yükü;
            factors["systemLoad"] = CalculateSystemLoadFactor(capacity);

            // Kaynak uygunluğu;
            factors["resourceAvailability"] = CalculateResourceAvailability(request, capacity);

            // Servis durumu;
            factors["serviceStatus"] = CalculateServiceStatusFactor(request, capacity);

            // Kapasite trendi;
            factors["capacityTrend"] = CalculateCapacityTrendFactor(capacity);

            return factors;
        }

        private double CalculateSystemLoadFactor(SystemCapacity capacity)
        {
            var load = capacity.CurrentLoad;

            // Weighted average of all loads;
            double totalLoad =
                (load.CpuLoad * 0.4) +
                (load.MemoryLoad * 0.3) +
                (load.DiskLoad * 0.1) +
                (load.NetworkLoad * 0.1) +
                (Math.Min(1.0, load.QueuedTasks / 100.0) * 0.1);

            // Invert for factor (higher load = lower factor)
            return 1.0 - Math.Min(1.0, totalLoad);
        }

        private double CalculateResourceAvailability(TaskRequest request, SystemCapacity capacity)
        {
            double availability = 1.0;

            // CPU kullanılabilirliği;
            var cpuAvailable = capacity.AvailableCpu - request.Resources.CpuUsage;
            if (cpuAvailable < 0)
                availability *= 0.5;

            // Bellek kullanılabilirliği;
            var memoryAvailable = capacity.AvailableMemoryMb - request.Resources.MemoryMb;
            if (memoryAvailable < 0)
                availability *= 0.3;

            // GPU kullanılabilirliği;
            if (request.Resources.RequiresGpu && !capacity.GpuAvailable)
                availability *= 0.1;

            // Thread kullanılabilirliği;
            var threadsAvailable = capacity.AvailableThreads - request.Resources.ThreadCount;
            if (threadsAvailable < 0)
                availability *= 0.8;

            return Math.Max(0.0, availability);
        }

        private double CalculateServiceStatusFactor(TaskRequest request, SystemCapacity capacity)
        {
            if (!request.Resources.RequiredServices.Any())
                return 1.0;

            double factor = 1.0;

            foreach (var service in request.Resources.RequiredServices)
            {
                if (capacity.ServiceStatuses.TryGetValue(service, out var status))
                {
                    switch (status.Health)
                    {
                        case HealthStatus.Healthy:
                            factor *= 1.0;
                            break;
                        case HealthStatus.Warning:
                            factor *= 0.7;
                            break;
                        case HealthStatus.Critical:
                            factor *= 0.3;
                            break;
                        case HealthStatus.Offline:
                            factor *= 0.0;
                            break;
                    }
                }
                else;
                {
                    // Servis bilinmiyor, varsayılan olarak düşük;
                    factor *= 0.5;
                }
            }

            return factor;
        }

        private double CalculateCapacityTrendFactor(SystemCapacity capacity)
        {
            return capacity.Trend switch;
            {
                CapacityTrend.Improving => 0.8,
                CapacityTrend.Stable => 0.5,
                CapacityTrend.Declining => 0.3,
                CapacityTrend.Critical => 0.1,
                _ => 0.5;
            };
        }

        private async Task<Dictionary<string, double>> CalculateDependencyFactors(TaskRequest request)
        {
            var factors = new Dictionary<string, double>();

            if (!request.Dependencies.Any())
            {
                factors["dependency"] = 1.0; // No dependencies = best;
                return factors;
            }

            try
            {
                var dependencyAnalysis = await AnalyzeDependenciesAsync(new WeightedTask;
                {
                    Request = request,
                    CalculatedAt = DateTime.UtcNow;
                });

                // Kritik yol analizi;
                if (dependencyAnalysis.CriticalPath != null)
                {
                    var criticalPathLength = dependencyAnalysis.CriticalPath.CriticalPathDuration;
                    var totalFloat = dependencyAnalysis.CriticalPath.TotalFloat;

                    // Kritik yol uzunluğu faktörü;
                    factors["criticalPathLength"] = Math.Min(1.0, criticalPathLength.TotalHours / 24.0);

                    // Toplam float faktörü;
                    factors["totalFloat"] = Math.Min(1.0, totalFloat);
                }

                // Risk faktörü;
                if (dependencyAnalysis.Risks.Any())
                {
                    var maxRisk = dependencyAnalysis.Risks.Max(r => r.Level);
                    factors["dependencyRisk"] = maxRisk switch;
                    {
                        RiskLevel.Low => 0.8,
                        RiskLevel.Medium => 0.5,
                        RiskLevel.High => 0.3,
                        RiskLevel.Critical => 0.1,
                        _ => 1.0;
                    };
                }

                // Bağımlılık yoğunluğu faktörü;
                var totalDependencies = dependencyAnalysis.DirectDependencies.Count +
                                      dependencyAnalysis.IndirectDependencies.Count;
                factors["dependencyDensity"] = Math.Min(1.0, totalDependencies / 20.0);

                return factors;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate dependency factors for task: {TaskId}", request.TaskId);

                // Varsayılan değerler;
                factors["dependency"] = 0.5;
                factors["dependencyRisk"] = 0.5;
                return factors;
            }
        }

        private async Task<Dictionary<string, double>> CalculateContextFactors(TaskRequest request)
        {
            var factors = new Dictionary<string, double>();

            // 1. Zaman faktörü;
            factors["timeOfDay"] = CalculateTimeOfDayFactor();

            // 2. Gün faktörü;
            factors["dayOfWeek"] = CalculateDayOfWeekFactor();

            // 3. Mevsimsel faktörler;
            factors["seasonal"] = CalculateSeasonalFactor();

            // 4. Tarihsel performans;
            factors["historicalPerformance"] = await CalculateHistoricalPerformanceFactor(request);

            return factors;
        }

        private double CalculateTimeOfDayFactor()
        {
            var hour = DateTime.UtcNow.Hour;

            // İş saatleri: 09:00-17:00 UTC;
            if (hour >= 9 && hour <= 17)
                return 0.7; // Normal load;
            else if (hour >= 18 && hour <= 22)
                return 0.9; // Evening, lower load;
            else if (hour >= 23 || hour <= 5)
                return 1.0; // Night, lowest load;
            else;
                return 0.8; // Morning;
        }

        private double CalculateDayOfWeekFactor()
        {
            var day = DateTime.UtcNow.DayOfWeek;

            return day switch;
            {
                DayOfWeek.Monday => 0.6,    // Start of week;
                DayOfWeek.Tuesday => 0.7,
                DayOfWeek.Wednesday => 0.8,
                DayOfWeek.Thursday => 0.9,
                DayOfWeek.Friday => 0.7,    // End of week;
                DayOfWeek.Saturday => 1.0,  // Weekend;
                DayOfWeek.Sunday => 1.0,    // Weekend;
                _ => 0.8;
            };
        }

        private double CalculateSeasonalFactor()
        {
            var month = DateTime.UtcNow.Month;

            // Basit mevsimsel model;
            return month switch;
            {
                12 or 1 => 0.6,  // Holiday season;
                6 or 7 => 0.8,   // Summer;
                3 or 9 => 0.9,   // Spring/Fall;
                _ => 0.7;
            };
        }

        private async Task<double> CalculateHistoricalPerformanceFactor(TaskRequest request)
        {
            try
            {
                // Tarihsel verilere göre performans tahmini;
                // Burada veritabanı veya cache'den veri çekilebilir;

                // Varsayılan değer;
                return 0.8;
            }
            catch
            {
                return 0.5; // Hata durumunda orta değer;
            }
        }

        private Dictionary<string, double> CombineFactors(
            Dictionary<string, double> baseFactors,
            Dictionary<string, double> systemFactors,
            Dictionary<string, double> dependencyFactors,
            Dictionary<string, double> contextFactors)
        {
            var combined = new Dictionary<string, double>();

            // Tüm faktörleri birleştir;
            foreach (var factor in baseFactors)
                combined[factor.Key] = factor.Value;

            foreach (var factor in systemFactors)
                combined[factor.Key] = factor.Value;

            foreach (var factor in dependencyFactors)
                combined[factor.Key] = factor.Value;

            foreach (var factor in contextFactors)
                combined[factor.Key] = factor.Value;

            return combined;
        }

        private WeightedTask CalculateFinalWeight(TaskRequest request, Dictionary<string, double> factors)
        {
            var weightedTask = new WeightedTask;
            {
                Request = request,
                Breakdown = new WeightBreakdown;
                {
                    RawScores = factors,
                    NormalizedScores = NormalizeFactors(factors)
                }
            };

            // Ağırlıklandırma stratejisine göre nihai skor hesapla;
            var finalScore = _weightingEngine.CalculateWeightedScore(factors);

            weightedTask.CalculatedWeight = finalScore;
            weightedTask.PriorityScore = finalScore;
            weightedTask.FinalPriority = finalScore;

            // Bileşen skorlarını ata;
            weightedTask.UrgencyScore = factors.GetValueOrDefault("urgency", 0.5);
            weightedTask.ImportanceScore = factors.GetValueOrDefault("importance", 0.5);
            weightedTask.ComplexityScore = factors.GetValueOrDefault("complexity", 0.5);
            weightedTask.ResourceScore = factors.GetValueOrDefault("resource", 0.5);
            weightedTask.DependencyScore = factors.GetValueOrDefault("dependency", 1.0);
            weightedTask.RiskScore = factors.GetValueOrDefault("risk", 0.5);
            weightedTask.SystemLoadScore = factors.GetValueOrDefault("systemLoad", 0.5);

            // Açıklamalar ekle;
            foreach (var factor in factors)
            {
                weightedTask.Breakdown.Explanations[factor.Key] =
                    GetFactorExplanation(factor.Key, factor.Value);
            }

            return weightedTask;
        }

        private Dictionary<string, double> NormalizeFactors(Dictionary<string, double> factors)
        {
            var normalized = new Dictionary<string, double>();

            if (!factors.Any())
                return normalized;

            var min = factors.Values.Min();
            var max = factors.Values.Max();
            var range = max - min;

            if (range == 0)
            {
                // Tüm değerler aynı;
                foreach (var factor in factors)
                    normalized[factor.Key] = 0.5;
            }
            else;
            {
                foreach (var factor in factors)
                {
                    normalized[factor.Key] = (factor.Value - min) / range;
                }
            }

            return normalized;
        }

        private string GetFactorExplanation(string factor, double value)
        {
            return factor switch;
            {
                "urgency" => value switch;
                {
                    > 0.9 => "Extremely urgent - immediate action required",
                    > 0.7 => "Very urgent - complete within hours",
                    > 0.5 => "Moderately urgent - complete within day",
                    > 0.3 => "Low urgency - can wait",
                    _ => "No urgency - background task"
                },
                "importance" => value switch;
                {
                    > 0.8 => "Critical importance - system essential",
                    > 0.6 => "High importance - user critical",
                    > 0.4 => "Medium importance - beneficial",
                    > 0.2 => "Low importance - nice to have",
                    _ => "Minimal importance"
                },
                "complexity" => value switch;
                {
                    > 0.8 => "Extremely complex - requires specialized resources",
                    > 0.6 => "Very complex - significant effort required",
                    > 0.4 => "Moderately complex - standard effort",
                    > 0.2 => "Simple - minimal effort",
                    _ => "Trivial - automated execution"
                },
                "resource" => value switch;
                {
                    > 0.8 => "Very resource intensive",
                    > 0.6 => "Resource intensive",
                    > 0.4 => "Moderate resource usage",
                    > 0.2 => "Light resource usage",
                    _ => "Minimal resource usage"
                },
                _ => $"Factor value: {value:F2}"
            };
        }

        private PriorityLevel DeterminePriorityLevel(double finalPriority)
        {
            return finalPriority switch;
            {
                >= 0.9 => PriorityLevel.Emergency,
                >= 0.7 => PriorityLevel.Critical,
                >= 0.5 => PriorityLevel.High,
                >= 0.3 => PriorityLevel.Normal,
                >= 0.1 => PriorityLevel.Low,
                _ => PriorityLevel.Background;
            };
        }

        private ExecutionWindow CalculateExecutionWindow(WeightedTask task, SystemCapacity capacity)
        {
            var window = new ExecutionWindow();

            // Temel hesaplama;
            var baseDuration = task.Request.EstimatedDuration;
            var priorityMultiplier = task.PriorityLevel switch;
            {
                PriorityLevel.Emergency => 0.1,
                PriorityLevel.Critical => 0.3,
                PriorityLevel.High => 0.5,
                PriorityLevel.Normal => 0.8,
                PriorityLevel.Low => 1.2,
                PriorityLevel.Background => 2.0,
                _ => 1.0;
            };

            // Sistem yükü faktörü;
            var loadMultiplier = 1.0 + (1.0 - task.SystemLoadScore) * 0.5;

            // Nihai süre;
            var estimatedDuration = baseDuration * priorityMultiplier * loadMultiplier;

            // Başlangıç zamanı;
            var recommendedStart = DateTime.UtcNow;

            // Zaman kısıtlamalarını kontrol et;
            if (task.Request.Constraints.AllowedTimeWindows.Any())
            {
                recommendedStart = FindNextAllowedWindow(
                    recommendedStart,
                    task.Request.Constraints.AllowedTimeWindows);
            }

            window.RecommendedStart = recommendedStart;
            window.RecommendedEnd = recommendedStart + estimatedDuration;
            window.Duration = estimatedDuration;
            window.Confidence = CalculateWindowConfidence(task, capacity);

            return window;
        }

        private DateTime FindNextAllowedWindow(DateTime startTime, List<TimeWindow> allowedWindows)
        {
            if (!allowedWindows.Any())
                return startTime;

            // Basit implementasyon: ilk uygun pencereyi bul;
            foreach (var window in allowedWindows)
            {
                var windowStart = startTime.Date + window.Start;
                var windowEnd = startTime.Date + window.End;

                if (window.Days.Contains(startTime.DayOfWeek) &&
                    startTime >= windowStart && startTime <= windowEnd)
                {
                    return startTime;
                }
            }

            // Uygun pencere yoksa, bir sonraki günü dene;
            return FindNextAllowedWindow(startTime.AddDays(1), allowedWindows);
        }

        private double CalculateWindowConfidence(WeightedTask task, SystemCapacity capacity)
        {
            double confidence = 0.7; // Base confidence;

            // Sistem kararlılığı;
            if (capacity.Trend == CapacityTrend.Stable || capacity.Trend == CapacityTrend.Improving)
                confidence += 0.2;
            else if (capacity.Trend == CapacityTrend.Declining)
                confidence -= 0.1;
            else if (capacity.Trend == CapacityTrend.Critical)
                confidence -= 0.3;

            // Görev karmaşıklığı;
            confidence -= task.ComplexityScore * 0.1;

            // Bağımlılık riski;
            confidence -= task.DependencyScore * 0.1;

            return Math.Max(0.1, Math.Min(1.0, confidence));
        }

        private List<PriorityAction> GenerateSuggestedActions(WeightedTask task)
        {
            var actions = new List<PriorityAction>();

            // Acil durum eylemleri;
            if (task.PriorityLevel == PriorityLevel.Emergency)
            {
                actions.Add(new PriorityAction;
                {
                    Action = "Execute immediately with maximum resources",
                    Type = ActionType.Optimize,
                    Priority = 1,
                    Reason = "Emergency priority task",
                    EstimatedDuration = TimeSpan.FromMinutes(5),
                    RequiredResources = new List<string> { "Max CPU", "Max Memory", "High Priority Queue" }
                });
            }

            // Optimizasyon önerileri;
            if (task.ComplexityScore > 0.7)
            {
                actions.Add(new PriorityAction;
                {
                    Action = "Consider breaking down into smaller subtasks",
                    Type = ActionType.Optimize,
                    Priority = 2,
                    Reason = "High complexity detected",
                    EstimatedDuration = TimeSpan.FromMinutes(10),
                    RequiredResources = new List<string> { "Task Decomposition" }
                });
            }

            if (task.ResourceScore > 0.8)
            {
                actions.Add(new PriorityAction;
                {
                    Action = "Schedule during low system load hours",
                    Type = ActionType.Delay,
                    Priority = 3,
                    Reason = "High resource requirements",
                    EstimatedDuration = TimeSpan.Zero,
                    RequiredResources = new List<string> { "Resource Monitoring" }
                });
            }

            // Risk azaltma eylemleri;
            if (task.RiskScore > 0.6)
            {
                actions.Add(new PriorityAction;
                {
                    Action = "Implement additional error handling and retry logic",
                    Type = ActionType.Optimize,
                    Priority = 2,
                    Reason = "High risk task",
                    EstimatedDuration = TimeSpan.FromMinutes(15),
                    RequiredResources = new List<string> { "Error Handling Framework" }
                });
            }

            return actions.OrderBy(a => a.Priority).ToList();
        }

        public async Task<List<WeightedTask>> PrioritizeTasksAsync(List<TaskRequest> tasks)
        {
            if (tasks == null || !tasks.Any())
                return new List<WeightedTask>();

            try
            {
                _logger.LogDebug("Prioritizing {TaskCount} tasks", tasks.Count);

                // Tüm görevlerin ağırlıklarını hesapla;
                var weightedTasks = new List<WeightedTask>();

                foreach (var task in tasks)
                {
                    var weightedTask = await CalculateTaskWeightAsync(task);
                    weightedTasks.Add(weightedTask);
                }

                // Önceliğe göre sırala;
                var prioritizedTasks = weightedTasks;
                    .OrderByDescending(t => t.FinalPriority)
                    .ThenBy(t => t.Request.Deadline ?? DateTime.MaxValue)
                    .ThenBy(t => t.CalculatedWeight)
                    .ToList();

                // Sıralama bilgisini güncelle;
                for (int i = 0; i < prioritizedTasks.Count; i++)
                {
                    prioritizedTasks[i].Metadata["ranking"] = i + 1;
                    prioritizedTasks[i].Metadata["totalTasks"] = prioritizedTasks.Count;
                }

                // Çakışma çözümü;
                var conflicts = await IdentifyConflicts(prioritizedTasks);
                if (conflicts.Any())
                {
                    var resolution = await ResolveTaskConflictsAsync(prioritizedTasks);
                    if (resolution.SelectedTask != null)
                    {
                        // Çözümlenmiş listeyi yeniden sırala;
                        prioritizedTasks = ReorderAfterConflictResolution(
                            prioritizedTasks,
                            resolution);
                    }
                }

                _logger.LogInformation("Prioritized {TaskCount} tasks, top priority: {TopTask}",
                    tasks.Count, prioritizedTasks.FirstOrDefault()?.Request?.Name);

                return prioritizedTasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prioritize tasks");
                throw new TaskWeightingException(
                    "Failed to prioritize tasks",
                    ex,
                    ErrorCodes.TaskWeighter.PrioritizationFailed);
            }
        }

        private async Task<List<Conflict>> IdentifyConflicts(List<WeightedTask> tasks)
        {
            var conflicts = new List<Conflict>();

            // Kaynak çakışmalarını tespit et;
            var resourceConflicts = await IdentifyResourceConflicts(tasks);
            conflicts.AddRange(resourceConflicts);

            // Bağımlılık çakışmalarını tespit et;
            var dependencyConflicts = await IdentifyDependencyConflicts(tasks);
            conflicts.AddRange(dependencyConflicts);

            // Zaman çakışmalarını tespit et;
            var temporalConflicts = await IdentifyTemporalConflicts(tasks);
            conflicts.AddRange(temporalConflicts);

            return conflicts;
        }

        private async Task<List<Conflict>> IdentifyResourceConflicts(List<WeightedTask> tasks)
        {
            var conflicts = new List<Conflict>();
            var systemCapacity = await AnalyzeSystemCapacityAsync();

            // Grup görevleri aynı kaynaklar için;
            var resourceGroups = tasks;
                .Where(t => t.Request.Resources.CpuUsage > 0.5 ||
                           t.Request.Resources.MemoryMb > 512)
                .GroupBy(t => new;
                {
                    HighCpu = t.Request.Resources.CpuUsage > 0.7,
                    HighMemory = t.Request.Resources.MemoryMb > 1024,
                    RequiresGpu = t.Request.Resources.RequiresGpu;
                })
                .Where(g => g.Count() > 1);

            foreach (var group in resourceGroups)
            {
                if (group.Key.HighCpu && group.Sum(t => t.Request.Resources.CpuUsage) > systemCapacity.AvailableCpu)
                {
                    conflicts.Add(new Conflict;
                    {
                        Type = ConflictType.Resource,
                        Tasks = group.ToList(),
                        Description = "CPU resource conflict",
                        Severity = ConflictSeverity.High;
                    });
                }

                if (group.Key.HighMemory && group.Sum(t => t.Request.Resources.MemoryMb) > systemCapacity.AvailableMemoryMb)
                {
                    conflicts.Add(new Conflict;
                    {
                        Type = ConflictType.Resource,
                        Tasks = group.ToList(),
                        Description = "Memory resource conflict",
                        Severity = ConflictSeverity.High;
                    });
                }

                if (group.Key.RequiresGpu && !systemCapacity.GpuAvailable)
                {
                    conflicts.Add(new Conflict;
                    {
                        Type = ConflictType.Resource,
                        Tasks = group.ToList(),
                        Description = "GPU resource conflict",
                        Severity = ConflictSeverity.Critical;
                    });
                }
            }

            return conflicts;
        }

        private async Task<List<Conflict>> IdentifyDependencyConflicts(List<WeightedTask> tasks)
        {
            var conflicts = new List<Conflict>();

            // Döngüsel bağımlılıkları kontrol et;
            var dependencyGraph = BuildDependencyGraph(tasks);
            var cycles = FindCycles(dependencyGraph);

            foreach (var cycle in cycles)
            {
                var cycleTasks = tasks.Where(t => cycle.Contains(t.Request.TaskId)).ToList();
                conflicts.Add(new Conflict;
                {
                    Type = ConflictType.Dependency,
                    Tasks = cycleTasks,
                    Description = $"Circular dependency detected: {string.Join(" -> ", cycle)}",
                    Severity = ConflictSeverity.Critical;
                });
            }

            // Çakışan bağımlılıkları kontrol et;
            foreach (var task in tasks)
            {
                var missingDependencies = task.Request.Dependencies;
                    .Where(depId => !tasks.Any(t => t.Request.TaskId == depId))
                    .ToList();

                if (missingDependencies.Any())
                {
                    conflicts.Add(new Conflict;
                    {
                        Type = ConflictType.Dependency,
                        Tasks = new List<WeightedTask> { task },
                        Description = $"Missing dependencies: {string.Join(", ", missingDependencies)}",
                        Severity = ConflictSeverity.Medium;
                    });
                }
            }

            return conflicts;
        }

        private Dictionary<string, List<string>> BuildDependencyGraph(List<WeightedTask> tasks)
        {
            var graph = new Dictionary<string, List<string>>();

            foreach (var task in tasks)
            {
                if (!graph.ContainsKey(task.Request.TaskId))
                {
                    graph[task.Request.TaskId] = new List<string>();
                }

                foreach (var depId in task.Request.Dependencies)
                {
                    if (graph.ContainsKey(depId))
                    {
                        graph[depId].Add(task.Request.TaskId);
                    }
                    else;
                    {
                        graph[depId] = new List<string> { task.Request.TaskId };
                    }
                }
            }

            return graph;
        }

        private List<List<string>> FindCycles(Dictionary<string, List<string>> graph)
        {
            var cycles = new List<List<string>>();
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();
            var path = new Stack<string>();

            foreach (var node in graph.Keys)
            {
                if (!visited.Contains(node))
                {
                    FindCyclesDFS(node, graph, visited, recursionStack, path, cycles);
                }
            }

            return cycles;
        }

        private void FindCyclesDFS(string node, Dictionary<string, List<string>> graph,
            HashSet<string> visited, HashSet<string> recursionStack,
            Stack<string> path, List<List<string>> cycles)
        {
            visited.Add(node);
            recursionStack.Add(node);
            path.Push(node);

            if (graph.ContainsKey(node))
            {
                foreach (var neighbor in graph[node])
                {
                    if (!visited.Contains(neighbor))
                    {
                        FindCyclesDFS(neighbor, graph, visited, recursionStack, path, cycles);
                    }
                    else if (recursionStack.Contains(neighbor))
                    {
                        // Cycle found;
                        var cycle = new List<string>();
                        var currentPath = path.ToList();
                        var startIndex = currentPath.IndexOf(neighbor);

                        for (int i = startIndex; i < currentPath.Count; i++)
                        {
                            cycle.Add(currentPath[i]);
                        }

                        cycle.Reverse(); // Maintain order;
                        cycles.Add(cycle);
                    }
                }
            }

            recursionStack.Remove(node);
            path.Pop();
        }

        private async Task<List<Conflict>> IdentifyTemporalConflicts(List<WeightedTask> tasks)
        {
            var conflicts = new List<Conflict>();

            // Çakışan zaman pencerelerini kontrol et;
            var tasksWithWindows = tasks;
                .Where(t => t.RecommendedWindow != null)
                .OrderBy(t => t.RecommendedWindow.RecommendedStart)
                .ToList();

            for (int i = 0; i < tasksWithWindows.Count - 1; i++)
            {
                var current = tasksWithWindows[i];
                var next = tasksWithWindows[i + 1];

                if (current.RecommendedWindow.RecommendedEnd > next.RecommendedWindow.RecommendedStart)
                {
                    var overlap = current.RecommendedWindow.RecommendedEnd -
                                 next.RecommendedWindow.RecommendedStart;

                    if (overlap > TimeSpan.FromMinutes(5)) // Önemli örtüşme;
                    {
                        conflicts.Add(new Conflict;
                        {
                            Type = ConflictType.Temporal,
                            Tasks = new List<WeightedTask> { current, next },
                            Description = $"Temporal overlap: {overlap.TotalMinutes:F1} minutes",
                            Severity = overlap > TimeSpan.FromMinutes(30) ?
                                ConflictSeverity.High : ConflictSeverity.Medium;
                        });
                    }
                }
            }

            return conflicts;
        }

        private List<WeightedTask> ReorderAfterConflictResolution(
            List<WeightedTask> tasks,
            ConflictResolution resolution)
        {
            var reordered = new List<WeightedTask>(tasks);

            // Çözümlenmiş görevleri kaldır;
            foreach (var cancelled in resolution.CancelledTasks)
            {
                reordered.RemoveAll(t => t.Request.TaskId == cancelled.Request.TaskId);
            }

            // Ertelenmiş görevleri sona taşı;
            foreach (var deferred in resolution.DeferredTasks)
            {
                var task = reordered.FirstOrDefault(t => t.Request.TaskId == deferred.Request.TaskId);
                if (task != null)
                {
                    reordered.Remove(task);
                    reordered.Add(task);
                }
            }

            // Seçilen görevi öne al;
            if (resolution.SelectedTask != null)
            {
                var selected = reordered.FirstOrDefault(t => t.Request.TaskId == resolution.SelectedTask.Request.TaskId);
                if (selected != null)
                {
                    reordered.Remove(selected);
                    reordered.Insert(0, selected);
                }
            }

            return reordered.OrderByDescending(t => t.FinalPriority).ToList();
        }

        public async Task<SystemCapacity> AnalyzeSystemCapacityAsync()
        {
            var cacheKey = "system_capacity";

            return await _priorityCache.GetOrCreateAsync(cacheKey, async () =>
            {
                try
                {
                    _logger.LogDebug("Analyzing system capacity");

                    var capacity = new SystemCapacity;
                    {
                        MeasuredAt = DateTime.UtcNow;
                    };

                    // Performans metriklerini al;
                    var performanceData = await _performanceMonitor.GetCurrentMetricsAsync();

                    capacity.AvailableCpu = 1.0 - performanceData.CpuUsage;
                    capacity.AvailableMemoryMb = performanceData.AvailableMemoryMb;
                    capacity.AvailableDiskMb = performanceData.AvailableDiskMb;
                    capacity.AvailableThreads = performanceData.AvailableThreads;
                    capacity.GpuAvailable = performanceData.GpuAvailable;
                    capacity.GpuMemoryGb = performanceData.GpuMemoryGb;
                    capacity.NetworkBandwidthMbps = performanceData.NetworkBandwidthMbps;

                    // Mevcut yükü hesapla;
                    capacity.CurrentLoad = new LoadMetrics;
                    {
                        CpuLoad = performanceData.CpuUsage,
                        MemoryLoad = 1.0 - (performanceData.AvailableMemoryMb / (double)performanceData.TotalMemoryMb),
                        DiskLoad = 1.0 - (performanceData.AvailableDiskMb / (double)performanceData.TotalDiskMb),
                        ActiveThreads = performanceData.ActiveThreads,
                        QueuedTasks = performanceData.QueuedTasks,
                        NetworkLoad = performanceData.NetworkUsage;
                    };

                    // Servis durumlarını al;
                    capacity.ServiceStatuses = await GetServiceStatusesAsync();

                    // Trend analizi;
                    capacity.Trend = await AnalyzeCapacityTrendAsync(capacity);

                    _logger.LogInformation("System capacity analyzed: CPU: {CpuAvailable:P0}, Memory: {MemoryAvailable}MB",
                        capacity.AvailableCpu, capacity.AvailableMemoryMb);

                    return capacity;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to analyze system capacity");

                    // Varsayılan değerlerle dön;
                    return new SystemCapacity;
                    {
                        AvailableCpu = 0.5,
                        AvailableMemoryMb = 4096,
                        AvailableDiskMb = 102400,
                        AvailableThreads = 50,
                        GpuAvailable = false,
                        Trend = CapacityTrend.Stable,
                        MeasuredAt = DateTime.UtcNow;
                    };
                }
            }, TimeSpan.FromSeconds(30));
        }

        private async Task<Dictionary<string, ServiceStatus>> GetServiceStatusesAsync()
        {
            var statuses = new Dictionary<string, ServiceStatus>();

            try
            {
                // Critical services;
                var criticalServices = new[] { "Database", "Cache", "MessageQueue", "APIGateway" };

                foreach (var service in criticalServices)
                {
                    // Gerçek implementasyonda servis sağlık kontrolleri yapılır;
                    statuses[service] = new ServiceStatus;
                    {
                        IsAvailable = true,
                        CurrentLoad = 0.3 + new Random().NextDouble() * 0.4,
                        QueuedRequests = new Random().Next(0, 50),
                        AverageResponseTime = TimeSpan.FromMilliseconds(100 + new Random().NextDouble() * 400),
                        Health = HealthStatus.Healthy;
                    };
                }

                return statuses;
            }
            catch
            {
                // Hata durumunda varsayılan değerler;
                return statuses;
            }
        }

        private async Task<CapacityTrend> AnalyzeCapacityTrendAsync(SystemCapacity currentCapacity)
        {
            try
            {
                // Basit trend analizi;
                // Gerçek implementasyonda tarihsel veriler analiz edilir;

                var load = currentCapacity.CurrentLoad;

                if (load.CpuLoad > 0.9 || load.MemoryLoad > 0.9)
                    return CapacityTrend.Critical;
                else if (load.CpuLoad > 0.7 || load.MemoryLoad > 0.7)
                    return CapacityTrend.Declining;
                else if (load.CpuLoad < 0.3 && load.MemoryLoad < 0.3)
                    return CapacityTrend.Improving;
                else;
                    return CapacityTrend.Stable;
            }
            catch
            {
                return CapacityTrend.Stable;
            }
        }

        public async Task<List<CriticalTask>> IdentifyCriticalTasksAsync(List<WeightedTask> tasks)
        {
            var criticalTasks = new List<CriticalTask>();

            try
            {
                _logger.LogDebug("Identifying critical tasks from {TaskCount} tasks", tasks.Count);

                foreach (var task in tasks)
                {
                    var criticality = DetermineTaskCriticality(task);

                    if (criticality > CriticalityLevel.Normal)
                    {
                        var criticalTask = new CriticalTask;
                        {
                            Task = task,
                            Criticality = criticality,
                            CriticalFactors = GetCriticalFactors(task),
                            MitigationActions = GenerateMitigationActions(task, criticality)
                        };

                        criticalTasks.Add(criticalTask);
                    }
                }

                // Kritiklik seviyesine göre sırala;
                criticalTasks = criticalTasks;
                    .OrderByDescending(t => t.Criticality)
                    .ThenByDescending(t => t.Task.FinalPriority)
                    .ToList();

                _logger.LogInformation("Identified {CriticalTaskCount} critical tasks", criticalTasks.Count);

                return criticalTasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to identify critical tasks");
                throw new TaskWeightingException(
                    "Failed to identify critical tasks",
                    ex,
                    ErrorCodes.TaskWeighter.CriticalTaskIdentificationFailed);
            }
        }

        private CriticalityLevel DetermineTaskCriticality(WeightedTask task)
        {
            var score = 0;

            // Aciliyet faktörü;
            if (task.UrgencyScore > 0.9) score += 3;
            else if (task.UrgencyScore > 0.7) score += 2;
            else if (task.UrgencyScore > 0.5) score += 1;

            // Önem faktörü;
            if (task.ImportanceScore > 0.8) score += 3;
            else if (task.ImportanceScore > 0.6) score += 2;
            else if (task.ImportanceScore > 0.4) score += 1;

            // Risk faktörü;
            if (task.RiskScore > 0.8) score += 2;
            else if (task.RiskScore > 0.6) score += 1;

            // Bağımlılık faktörü;
            if (task.DependencyScore < 0.3) score += 2; // Yüksek bağımlılık = yüksek risk;
            else if (task.DependencyScore < 0.6) score += 1;

            // Öncelik seviyesi;
            score += task.PriorityLevel switch;
            {
                PriorityLevel.Emergency => 3,
                PriorityLevel.Critical => 2,
                PriorityLevel.High => 1,
                _ => 0;
            };

            return score switch;
            {
                >= 10 => CriticalityLevel.Emergency,
                >= 7 => CriticalityLevel.Critical,
                >= 5 => CriticalityLevel.Warning,
                _ => CriticalityLevel.Normal;
            };
        }

        private List<string> GetCriticalFactors(WeightedTask task)
        {
            var factors = new List<string>();

            if (task.UrgencyScore > 0.8)
                factors.Add("High urgency");

            if (task.ImportanceScore > 0.7)
                factors.Add("High importance");

            if (task.RiskScore > 0.7)
                factors.Add("High risk");

            if (task.DependencyScore < 0.4)
                factors.Add("Complex dependencies");

            if (task.Request.Deadline.HasValue &&
                (task.Request.Deadline.Value - DateTime.UtcNow) < TimeSpan.FromHours(1))
                factors.Add("Imminent deadline");

            if (task.Request.Resources.RequiresGpu || task.Request.Resources.MemoryMb > 2048)
                factors.Add("High resource requirements");

            return factors;
        }

        private List<MitigationAction> GenerateMitigationActions(WeightedTask task, CriticalityLevel criticality)
        {
            var actions = new List<MitigationAction>();

            if (criticality >= CriticalityLevel.Critical)
            {
                actions.Add(new MitigationAction;
                {
                    Action = "Execute immediately with priority override",
                    Type = ActionType.Escalate,
                    EstimatedEffectiveness = 100,
                    EstimatedDuration = TimeSpan.FromMinutes(1),
                    RequiredResources = new List<string> { "Priority Queue", "Emergency Resources" }
                });
            }

            if (task.RiskScore > 0.7)
            {
                actions.Add(new MitigationAction;
                {
                    Action = "Implement additional monitoring and alerting",
                    Type = ActionType.Notify,
                    EstimatedEffectiveness = 80,
                    EstimatedDuration = TimeSpan.FromMinutes(5),
                    RequiredResources = new List<string> { "Monitoring System", "Alerting Service" }
                });
            }

            if (task.Request.Resources.MemoryMb > 2048)
            {
                actions.Add(new MitigationAction;
                {
                    Action = "Optimize memory usage or allocate additional resources",
                    Type = ActionType.Optimize,
                    EstimatedEffectiveness = 70,
                    EstimatedDuration = TimeSpan.FromMinutes(15),
                    RequiredResources = new List<string> { "Memory Profiler", "Additional RAM" }
                });
            }

            if (task.Request.Dependencies.Count > 5)
            {
                actions.Add(new MitigationAction;
                {
                    Action = "Review and simplify task dependencies",
                    Type = ActionType.Optimize,
                    EstimatedEffectiveness = 60,
                    EstimatedDuration = TimeSpan.FromMinutes(20),
                    RequiredResources = new List<string> { "Dependency Analyzer" }
                });
            }

            return actions.OrderByDescending(a => a.EstimatedEffectiveness).ToList();
        }

        // Diğer metodların implementasyonları...
        // (CreateExecutionPlanAsync, AdjustPriorityInRealTimeAsync, AnalyzeDependenciesAsync,
        // ResolveTaskConflictsAsync, UpdateWeightingStrategyAsync, PredictFuturePrioritiesAsync,
        // BalanceTaskLoadAsync, HandleEmergencySituationAsync, AdjustForUserPreferencesAsync,
        // GetStatisticsAsync, ClearCacheAsync)

        #region Helper Classes;
        private class Conflict;
        {
            public ConflictType Type { get; set; }
            public List<WeightedTask> Tasks { get; set; }
            public string Description { get; set; }
            public ConflictSeverity Severity { get; set; }
        }

        private enum ConflictSeverity;
        {
            Low,
            Medium,
            High,
            Critical;
        }

        private class WeightingEngine;
        {
            private readonly ILogger _logger;
            private readonly Dictionary<string, double> _defaultWeights;

            public WeightingEngine(ILogger logger)
            {
                _logger = logger;

                _defaultWeights = new Dictionary<string, double>
                {
                    ["urgency"] = 0.25,
                    ["importance"] = 0.20,
                    ["complexity"] = 0.15,
                    ["resource"] = 0.10,
                    ["risk"] = 0.10,
                    ["systemLoad"] = 0.08,
                    ["dependency"] = 0.07,
                    ["timeOfDay"] = 0.03,
                    ["dayOfWeek"] = 0.02;
                };
            }

            public double CalculateWeightedScore(Dictionary<string, double> factors)
            {
                double totalScore = 0.0;
                double totalWeight = 0.0;

                foreach (var factor in factors)
                {
                    if (_defaultWeights.TryGetValue(factor.Key, out var weight))
                    {
                        totalScore += factor.Value * weight;
                        totalWeight += weight;
                    }
                    else;
                    {
                        // Varsayılan ağırlık;
                        totalScore += factor.Value * 0.05;
                        totalWeight += 0.05;
                    }
                }

                // Normalize;
                if (totalWeight > 0)
                {
                    return totalScore / totalWeight;
                }

                return 0.5; // Default;
            }
        }

        private class PriorityCache;
        {
            private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
            private readonly TimeSpan _defaultTtl;
            private readonly object _lock = new object();
            private readonly int _maxSize = 1000;

            public PriorityCache(TimeSpan defaultTtl)
            {
                _defaultTtl = defaultTtl;
            }

            public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null)
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
                    {
                        entry.LastAccessed = DateTime.UtcNow;
                        return (T)entry.Value;
                    }
                }

                var value = await factory();

                lock (_lock)
                {
                    if (_cache.Count >= _maxSize)
                    {
                        var oldest = _cache.OrderBy(e => e.Value.LastAccessed).First();
                        _cache.Remove(oldest.Key);
                    }

                    _cache[key] = new CacheEntry
                    {
                        Value = value,
                        ExpiresAt = DateTime.UtcNow.Add(ttl ?? _defaultTtl),
                        LastAccessed = DateTime.UtcNow;
                    };
                }

                return value;
            }

            private class CacheEntry
            {
                public object Value { get; set; }
                public DateTime ExpiresAt { get; set; }
                public DateTime LastAccessed { get; set; }
                public bool IsExpired => DateTime.UtcNow > ExpiresAt;
            }
        }

        private class StatisticsTracker;
        {
            private readonly object _lock = new object();
            private readonly List<WeightedTask> _recentTasks = new List<WeightedTask>();
            private readonly Dictionary<PriorityLevel, int> _priorityCounts = new Dictionary<PriorityLevel, int>();
            private readonly List<TimeSpan> _calculationTimes = new List<TimeSpan>();
            private readonly int _maxRecentTasks = 1000;

            public void RecordCalculation(WeightedTask task, TimeSpan calculationTime)
            {
                lock (_lock)
                {
                    _recentTasks.Add(task);

                    if (_recentTasks.Count > _maxRecentTasks)
                    {
                        _recentTasks.RemoveAt(0);
                    }

                    if (!_priorityCounts.ContainsKey(task.PriorityLevel))
                    {
                        _priorityCounts[task.PriorityLevel] = 0;
                    }
                    _priorityCounts[task.PriorityLevel]++;

                    _calculationTimes.Add(calculationTime);

                    if (_calculationTimes.Count > 1000)
                    {
                        _calculationTimes.RemoveAt(0);
                    }
                }
            }

            public WeightingStatistics GetStatistics()
            {
                lock (_lock)
                {
                    return new WeightingStatistics;
                    {
                        TotalTasksWeighted = _recentTasks.Count,
                        PriorityDistribution = new Dictionary<PriorityLevel, int>(_priorityCounts),
                        TaskTypeDistribution = _recentTasks;
                            .GroupBy(t => t.Request.Type.ToString())
                            .ToDictionary(g => g.Key, g => g.Count()),
                        AverageCalculationTimeMs = _calculationTimes.Any() ?
                            _calculationTimes.Average(t => t.TotalMilliseconds) : 0.0,
                        LastUpdated = DateTime.UtcNow;
                    };
                }
            }
        }

        public class TaskWeightCalculatedEvent : IEvent;
        {
            public string TaskId { get; set; }
            public double CalculatedWeight { get; set; }
            public PriorityLevel PriorityLevel { get; set; }
            public TimeSpan CalculationTime { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }
        #endregion;

        #region Error Codes;
        public static class ErrorCodes;
        {
            public const string CalculationFailed = "WEIGHT_001";
            public const string PrioritizationFailed = "WEIGHT_002";
            public const string CriticalTaskIdentificationFailed = "WEIGHT_003";
            public const string ExecutionPlanningFailed = "WEIGHT_004";
            public const string PriorityAdjustmentFailed = "WEIGHT_005";
            public const string DependencyAnalysisFailed = "WEIGHT_006";
            public const string ConflictResolutionFailed = "WEIGHT_007";
            public const string StrategyUpdateFailed = "WEIGHT_008";
        }
        #endregion;

        #region Exceptions;
        public class TaskWeightingException : Exception
        {
            public string ErrorCode { get; }

            public TaskWeightingException(string message, Exception innerException, string errorCode)
                : base(message, innerException)
            {
                ErrorCode = errorCode;
            }

            public TaskWeightingException(string message, string errorCode)
                : base(message)
            {
                ErrorCode = errorCode;
            }
        }
        #endregion;

        #region Kalan metodların stub implementasyonları;
        public async Task<ExecutionPlan> CreateExecutionPlanAsync(List<WeightedTask> tasks, ExecutionConstraints constraints)
        {
            // Tam implementasyon için yer;
            await Task.Delay(1);
            return new ExecutionPlan();
        }

        public async Task<PriorityAdjustment> AdjustPriorityInRealTimeAsync(WeightedTask task, RealTimeContext context)
        {
            await Task.Delay(1);
            return new PriorityAdjustment();
        }

        public async Task<DependencyAnalysis> AnalyzeDependenciesAsync(WeightedTask task)
        {
            await Task.Delay(1);
            return new DependencyAnalysis();
        }

        public async Task<ConflictResolution> ResolveTaskConflictsAsync(List<WeightedTask> tasks)
        {
            await Task.Delay(1);
            return new ConflictResolution();
        }

        public async Task UpdateWeightingStrategyAsync(WeightingStrategy newStrategy)
        {
            await Task.Delay(1);
        }

        public async Task<PriorityPrediction> PredictFuturePrioritiesAsync(TimeSpan lookahead)
        {
            await Task.Delay(1);
            return new PriorityPrediction();
        }

        public async Task<LoadBalancingResult> BalanceTaskLoadAsync(List<WeightedTask> tasks)
        {
            await Task.Delay(1);
            return new LoadBalancingResult();
        }

        public async Task<EmergencyPrioritization> HandleEmergencySituationAsync(EmergencyContext context)
        {
            await Task.Delay(1);
            return new EmergencyPrioritization();
        }

        public async Task<UserPriorityAdjustment> AdjustForUserPreferencesAsync(WeightedTask task, UserPreferences preferences)
        {
            await Task.Delay(1);
            return new UserPriorityAdjustment();
        }

        public async Task<WeightingStatistics> GetStatisticsAsync()
        {
            return await Task.FromResult(_statisticsTracker.GetStatistics());
        }

        public async Task ClearCacheAsync()
        {
            await Task.Delay(1);
        }
        #endregion;

        #region IDisposable;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    ClearCacheAsync().Wait(TimeSpan.FromSeconds(5));
                }
                _disposed = true;
            }
        }

        ~TaskWeighter()
        {
            Dispose(false);
        }
        #endregion;
    }
}
