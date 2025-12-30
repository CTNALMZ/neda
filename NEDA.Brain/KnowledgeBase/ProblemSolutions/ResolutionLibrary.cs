using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.Monitoring.Diagnostics;
using NEDA.Services.Messaging.EventBus;
using NEDA.AI.MachineLearning;
using NEDA.Brain.KnowledgeBase.FactDatabase;

namespace NEDA.Brain.KnowledgeBase.ProblemSolutions;
{
    /// <summary>
    /// Problem çözme, çözüm pattern'ları, çözüm arama ve optimizasyon; 
    /// için gelişmiş kütüphane.
    /// </summary>
    public interface IResolutionLibrary : IDisposable
    {
        /// <summary>
        /// Problemi analiz eder ve çözüm pattern'ları önerir;
        /// </summary>
        Task<ProblemAnalysis> AnalyzeProblemAsync(ProblemStatement problem);

        /// <summary>
        /// Problemi çözmek için en uygun çözüm pattern'larını bulur;
        /// </summary>
        Task<List<SolutionPattern>> FindSolutionPatternsAsync(ProblemAnalysis analysis,
            SearchCriteria criteria = null);

        /// <summary>
        /// Çözüm pattern'ını probleme uygular;
        /// </summary>
        Task<AppliedSolution> ApplySolutionPatternAsync(SolutionPattern pattern,
            ProblemContext context, ApplicationParameters parameters = null);

        /// <summary>
        /// Mevcut çözümleri iyileştirir;
        /// </summary>
        Task<SolutionImprovement> ImproveSolutionAsync(ExistingSolution solution,
            ImprovementRequest request);

        /// <summary>
        /// Çözümleri karşılaştırır ve en iyisini seçer;
        /// </summary>
        Task<SolutionComparison> CompareSolutionsAsync(List<AppliedSolution> solutions,
            ComparisonCriteria criteria);

        /// <summary>
        /// Yeni çözüm pattern'ı öğrenir;
        /// </summary>
        Task<LearnedPattern> LearnNewPatternAsync(SolutionLearningRequest request);

        /// <summary>
        /// Çözümleri case-based reasoning ile bulur;
        /// </summary>
        Task<List<CaseBasedSolution>> FindSimilarCasesAsync(ProblemStatement problem,
            SimilarityThreshold threshold);

        /// <summary>
        /// Çözümleri optimize eder;
        /// </summary>
        Task<OptimizedSolution> OptimizeSolutionAsync(AppliedSolution solution,
            OptimizationRequest request);

        /// <summary>
        /// Çözümü doğrular ve test eder;
        /// </summary>
        Task<ValidationResult> ValidateSolutionAsync(AppliedSolution solution,
            ValidationParameters parameters);

        /// <summary>
        /// Çözümün risklerini analiz eder;
        /// </summary>
        Task<RiskAssessment> AssessSolutionRisksAsync(AppliedSolution solution,
            RiskAnalysisContext context);

        /// <summary>
        /// Çözümü geri alır (undo)
        /// </summary>
        Task<RollbackResult> RollbackSolutionAsync(AppliedSolution solution,
            RollbackStrategy strategy);

        /// <summary>
        /// Çözümleri kategorize eder;
        /// </summary>
        Task<SolutionCategorization> CategorizeSolutionsAsync(List<AppliedSolution> solutions,
            CategorizationStrategy strategy);

        /// <summary>
        /// Çözüm pattern'larını birleştirir;
        /// </summary>
        Task<CombinedPattern> CombinePatternsAsync(List<SolutionPattern> patterns,
            CombinationStrategy strategy);

        /// <summary>
        /// Çözüm bağımlılıklarını analiz eder;
        /// </summary>
        Task<DependencyAnalysis> AnalyzeSolutionDependenciesAsync(AppliedSolution solution);

        /// <summary>
        /// Çözümleri ölçeklendirir;
        /// </summary>
        Task<ScaledSolution> ScaleSolutionAsync(AppliedSolution solution, ScalingRequest request);

        /// <summary>
        /// Çözümü genelleştirir;
        /// </summary>
        Task<GeneralizedSolution> GeneralizeSolutionAsync(AppliedSolution solution,
            GeneralizationRequest request);

        /// <summary>
        /// Çözüm meta-analizi yapar;
        /// </summary>
        Task<MetaAnalysis> AnalyzeSolutionMetaAsync(List<AppliedSolution> solutions,
            AnalysisParameters parameters);

        /// <summary>
        /// Çözüm kütüphanesini günceller;
        /// </summary>
        Task UpdateLibraryAsync(List<SolutionPattern> newPatterns, UpdateStrategy strategy);

        /// <summary>
        /// Çözüm istatistiklerini getirir;
        /// </summary>
        Task<ResolutionStatistics> GetStatisticsAsync();

        /// <summary>
        /// Çözüm cache'ini temizler;
        /// </summary>
        Task ClearCacheAsync();

        /// <summary>
        /// Çözümleri dışa aktarır;
        /// </summary>
        Task<SolutionExport> ExportSolutionsAsync(ExportFormat format);

        /// <summary>
        /// Çözümleri içe aktarır;
        /// </summary>
        Task<bool> ImportSolutionsAsync(SolutionImport importData);
    }

    /// <summary>
    /// Problem ifadesi;
    /// </summary>
    public class ProblemStatement;
    {
        [JsonProperty("problemId")]
        public string ProblemId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("domain")]
        public string Domain { get; set; }

        [JsonProperty("constraints")]
        public List<Constraint> Constraints { get; set; } = new List<Constraint>();

        [JsonProperty("objectives")]
        public List<Objective> Objectives { get; set; } = new List<Objective>();

        [JsonProperty("context")]
        public ProblemContext Context { get; set; }

        [JsonProperty("urgency")]
        public UrgencyLevel Urgency { get; set; } = UrgencyLevel.Normal;

        [JsonProperty("complexity")]
        public ComplexityLevel Complexity { get; set; } = ComplexityLevel.Medium;

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Problem kısıtı;
    /// </summary>
    public class Constraint;
    {
        [JsonProperty("constraintId")]
        public string ConstraintId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("type")]
        public ConstraintType Type { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("condition")]
        public string Condition { get; set; }

        [JsonProperty("priority")]
        public int Priority { get; set; } = 1;

        [JsonProperty("isHard")]
        public bool IsHard { get; set; } = true;

        [JsonProperty("penalty")]
        public double Penalty { get; set; } = 0.0;
    }

    public enum ConstraintType;
    {
        Resource,
        Time,
        Cost,
        Technical,
        Legal,
        Ethical,
        Environmental,
        Custom;
    }

    /// <summary>
    /// Hedef;
    /// </summary>
    public class Objective;
    {
        [JsonProperty("objectiveId")]
        public string ObjectiveId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("type")]
        public ObjectiveType Type { get; set; }

        [JsonProperty("target")]
        public object Target { get; set; }

        [JsonProperty("priority")]
        public int Priority { get; set; } = 1;

        [JsonProperty("weight")]
        public double Weight { get; set; } = 1.0;

        [JsonProperty("isEssential")]
        public bool IsEssential { get; set; } = false;
    }

    public enum ObjectiveType;
    {
        Maximize,
        Minimize,
        Achieve,
        Maintain,
        Avoid,
        Custom;
    }

    /// <summary>
    /// Problem bağlamı;
    /// </summary>
    public class ProblemContext;
    {
        [JsonProperty("environment")]
        public Dictionary<string, object> Environment { get; set; } = new Dictionary<string, object>();

        [JsonProperty("stakeholders")]
        public List<Stakeholder> Stakeholders { get; set; } = new List<Stakeholder>();

        [JsonProperty("availableResources")]
        public ResourcePool Resources { get; set; } = new ResourcePool();

        [JsonProperty("historicalContext")]
        public HistoricalContext History { get; set; }

        [JsonProperty("constraints")]
        public List<ContextConstraint> ContextConstraints { get; set; } = new List<ContextConstraint>();

        [JsonProperty("assumptions")]
        public List<Assumption> Assumptions { get; set; } = new List<Assumption>();
    }

    /// <summary>
    /// Paydaş;
    /// </summary>
    public class Stakeholder;
    {
        public string Name { get; set; }
        public string Role { get; set; }
        public List<string> Interests { get; set; } = new List<string>();
        public InfluenceLevel Influence { get; set; }
    }

    public enum InfluenceLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Kaynak havuzu;
    /// </summary>
    public class ResourcePool;
    {
        public Dictionary<string, double> Resources { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, List<string>> Capabilities { get; set; } = new Dictionary<string, List<string>>();
        public TimeSpan AvailableTime { get; set; }
        public double Budget { get; set; }
    }

    /// <summary>
    /// Tarihsel bağlam;
    /// </summary>
    public class HistoricalContext;
    {
        public List<PastProblem> SimilarProblems { get; set; } = new List<PastProblem>();
        public List<PreviousSolution> PreviousSolutions { get; set; } = new List<PreviousSolution>();
        public List<HistoricalPattern> Patterns { get; set; } = new List<HistoricalPattern>();
        public DateTime AnalysisPeriodStart { get; set; }
        public DateTime AnalysisPeriodEnd { get; set; }
    }

    /// <summary>
    /// Bağlam kısıtı;
    /// </summary>
    public class ContextConstraint;
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public object Value { get; set; }
        public string Impact { get; set; }
    }

    /// <summary>
    /// Varsayım;
    /// </summary>
    public class Assumption;
    {
        public string Description { get; set; }
        public double Confidence { get; set; }
        public string Justification { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
    }

    /// <summary>
    /// Aciliyet seviyesi;
    /// </summary>
    public enum UrgencyLevel;
    {
        Low,
        Normal,
        High,
        Critical,
        Emergency;
    }

    /// <summary>
    /// Karmaşıklık seviyesi;
    /// </summary>
    public enum ComplexityLevel;
    {
        Simple,
        Moderate,
        Complex,
        VeryComplex,
        ExtremelyComplex;
    }

    /// <summary>
    /// Problem analizi;
    /// </summary>
    public class ProblemAnalysis;
    {
        [JsonProperty("analysisId")]
        public string AnalysisId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("problem")]
        public ProblemStatement Problem { get; set; }

        [JsonProperty("keyFactors")]
        public List<KeyFactor> KeyFactors { get; set; } = new List<KeyFactor>();

        [JsonProperty("rootCauses")]
        public List<RootCause> RootCauses { get; set; } = new List<RootCause>();

        [JsonProperty("problemType")]
        public ProblemType Type { get; set; }

        [JsonProperty("difficultyScore")]
        public double DifficultyScore { get; set; }

        [JsonProperty("constraintAnalysis")]
        public ConstraintAnalysis ConstraintAnalysis { get; set; }

        [JsonProperty("objectiveAnalysis")]
        public ObjectiveAnalysis ObjectiveAnalysis { get; set; }

        [JsonProperty("similarityMatches")]
        public List<SimilarProblem> SimilarProblems { get; set; } = new List<SimilarProblem>();

        [JsonProperty("recommendedApproach")]
        public RecommendedApproach Approach { get; set; }

        [JsonProperty("analyzedAt")]
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Anahtar faktör;
    /// </summary>
    public class KeyFactor;
    {
        public string Factor { get; set; }
        public string Description { get; set; }
        public double Impact { get; set; }
        public double Controllability { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
    }

    /// <summary>
    /// Kök neden;
    /// </summary>
    public class RootCause;
    {
        public string Cause { get; set; }
        public string Description { get; set; }
        public double Probability { get; set; }
        public double Impact { get; set; }
        public List<string> Evidence { get; set; } = new List<string>();
        public List<string> MitigationStrategies { get; set; } = new List<string>();
    }

    /// <summary>
    /// Problem türü;
    /// </summary>
    public enum ProblemType;
    {
        DecisionMaking,
        Optimization,
        Diagnosis,
        Design,
        Planning,
        Prediction,
        Classification,
        ResourceAllocation,
        Scheduling,
        Routing,
        Custom;
    }

    /// <summary>
    /// Kısıt analizi;
    /// </summary>
    public class ConstraintAnalysis;
    {
        public List<ConstraintSeverity> Severities { get; set; } = new List<ConstraintSeverity>();
        public List<ConstraintConflict> Conflicts { get; set; } = new List<ConstraintConflict>();
        public double OverallRestrictiveness { get; set; }
        public List<string> FlexibilityAreas { get; set; } = new List<string>();
    }

    public class ConstraintSeverity;
    {
        public string ConstraintId { get; set; }
        public SeverityLevel Level { get; set; }
        public string Reason { get; set; }
    }

    public enum SeverityLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public class ConstraintConflict;
    {
        public string Constraint1 { get; set; }
        public string Constraint2 { get; set; }
        public string ConflictType { get; set; }
        public double Severity { get; set; }
        public List<string> ResolutionSuggestions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Hedef analizi;
    /// </summary>
    public class ObjectiveAnalysis;
    {
        public List<ObjectivePriority> Priorities { get; set; } = new List<ObjectivePriority>();
        public List<ObjectiveConflict> Conflicts { get; set; } = new List<ObjectiveConflict>();
        public List<string> AchievableObjectives { get; set; } = new List<string>();
        public List<string> ChallengingObjectives { get; set; } = new List<string>();
    }

    public class ObjectivePriority;
    {
        public string ObjectiveId { get; set; }
        public double CalculatedPriority { get; set; }
        public List<string> SupportingFactors { get; set; } = new List<string>();
    }

    public class ObjectiveConflict;
    {
        public string Objective1 { get; set; }
        public string Objective2 { get; set; }
        public string ConflictType { get; set; }
        public double TradeoffRequired { get; set; }
    }

    /// <summary>
    /// Benzer problem;
    /// </summary>
    public class SimilarProblem;
    {
        public string ProblemId { get; set; }
        public string Title { get; set; }
        public double SimilarityScore { get; set; }
        public List<string> SimilarityFactors { get; set; } = new List<string>();
        public List<string> AppliedSolutions { get; set; } = new List<string>();
        public double SuccessRate { get; set; }
    }

    /// <summary>
    /// Önerilen yaklaşım;
    /// </summary>
    public class RecommendedApproach;
    {
        public string ApproachType { get; set; }
        public string Description { get; set; }
        public double Confidence { get; set; }
        public List<string> Steps { get; set; } = new List<string>();
        public List<string> RequiredResources { get; set; } = new List<string>();
        public TimeSpan EstimatedTime { get; set; }
    }

    /// <summary>
    /// Arama kriterleri;
    /// </summary>
    public class SearchCriteria;
    {
        [JsonProperty("maxPatterns")]
        public int MaxPatterns { get; set; } = 10;

        [JsonProperty("minConfidence")]
        public double MinConfidence { get; set; } = 0.7;

        [JsonProperty("patternTypes")]
        public List<string> PatternTypes { get; set; } = new List<string>();

        [JsonProperty("excludedTypes")]
        public List<string> ExcludedTypes { get; set; } = new List<string>();

        [JsonProperty("complexityRange")]
        public ComplexityRange ComplexityRange { get; set; }

        [JsonProperty("successRateThreshold")]
        public double SuccessRateThreshold { get; set; } = 0.6;

        [JsonProperty("resourceConstraints")]
        public Dictionary<string, object> ResourceConstraints { get; set; } = new Dictionary<string, object>();

        [JsonProperty("timeConstraints")]
        public TimeSpan? MaxSolutionTime { get; set; }

        [JsonProperty("customFilters")]
        public Dictionary<string, object> CustomFilters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Karmaşıklık aralığı;
    /// </summary>
    public class ComplexityRange;
    {
        public ComplexityLevel Min { get; set; } = ComplexityLevel.Simple;
        public ComplexityLevel Max { get; set; } = ComplexityLevel.VeryComplex;
    }

    /// <summary>
    /// Çözüm pattern'ı;
    /// </summary>
    public class SolutionPattern;
    {
        [JsonProperty("patternId")]
        public string PatternId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("type")]
        public PatternType Type { get; set; }

        [JsonProperty("applicableProblemTypes")]
        public List<ProblemType> ApplicableProblemTypes { get; set; } = new List<ProblemType>();

        [JsonProperty("algorithm")]
        public SolutionAlgorithm Algorithm { get; set; }

        [JsonProperty("steps")]
        public List<PatternStep> Steps { get; set; } = new List<PatternStep>();

        [JsonProperty("parameters")]
        public Dictionary<string, ParameterDefinition> Parameters { get; set; } = new Dictionary<string, ParameterDefinition>();

        [JsonProperty("constraints")]
        public List<PatternConstraint> Constraints { get; set; } = new List<PatternConstraint>();

        [JsonProperty("successCriteria")]
        public List<SuccessCriterion> SuccessCriteria { get; set; } = new List<SuccessCriterion>();

        [JsonProperty("performanceMetrics")]
        public Dictionary<string, PerformanceMetric> Metrics { get; set; } = new Dictionary<string, PerformanceMetric>();

        [JsonProperty("historicalPerformance")]
        public HistoricalPerformance History { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; } = 0.8;

        [JsonProperty("complexity")]
        public ComplexityLevel Complexity { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("lastUsed")]
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        [JsonProperty("usageCount")]
        public int UsageCount { get; set; }

        [JsonProperty("successCount")]
        public int SuccessCount { get; set; }
    }

    /// <summary>
    /// Pattern türü;
    /// </summary>
    public enum PatternType;
    {
        Algorithmic,
        Heuristic,
        RuleBased,
        ModelBased,
        Hybrid,
        Metaheuristic,
        Exact,
        Approximation,
        Randomized,
        Custom;
    }

    /// <summary>
    /// Çözüm algoritması;
    /// </summary>
    public class SolutionAlgorithm;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public AlgorithmType Type { get; set; }
        public List<string> ImplementationSteps { get; set; } = new List<string>();
        public TimeSpan AverageExecutionTime { get; set; }
        public double AverageSuccessRate { get; set; }
    }

    public enum AlgorithmType;
    {
        DivideAndConquer,
        DynamicProgramming,
        Greedy,
        Backtracking,
        BranchAndBound,
        LinearProgramming,
        IntegerProgramming,
        GeneticAlgorithm,
        SimulatedAnnealing,
        TabuSearch,
        NeuralNetwork,
        ReinforcementLearning,
        Custom;
    }

    /// <summary>
    /// Pattern adımı;
    /// </summary>
    public class PatternStep;
    {
        public int StepNumber { get; set; }
        public string Description { get; set; }
        public string Action { get; set; }
        public List<string> Inputs { get; set; } = new List<string>();
        public List<string> Outputs { get; set; } = new List<string>();
        public List<string> Dependencies { get; set; } = new List<string>();
        public TimeSpan EstimatedDuration { get; set; }
    }

    /// <summary>
    /// Parametre tanımı;
    /// </summary>
    public class ParameterDefinition;
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public object DefaultValue { get; set; }
        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public bool IsRequired { get; set; } = true;
    }

    /// <summary>
    /// Pattern kısıtı;
    /// </summary>
    public class PatternConstraint;
    {
        public string Type { get; set; }
        public string Condition { get; set; }
        public string Message { get; set; }
        public SeverityLevel Severity { get; set; }
    }

    /// <summary>
    /// Başarı kriteri;
    /// </summary>
    public class SuccessCriterion;
    {
        public string Criterion { get; set; }
        public string Description { get; set; }
        public string Measurement { get; set; }
        public double Threshold { get; set; }
        public double Weight { get; set; } = 1.0;
    }

    /// <summary>
    /// Performans metriği;
    /// </summary>
    public class PerformanceMetric;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Unit { get; set; }
        public bool HigherIsBetter { get; set; } = true;
        public double Baseline { get; set; }
        public double Target { get; set; }
    }

    /// <summary>
    /// Tarihsel performans;
    /// </summary>
    public class HistoricalPerformance;
    {
        public int TotalApplications { get; set; }
        public int Successes { get; set; }
        public int PartialSuccesses { get; set; }
        public int Failures { get; set; }
        public double AverageSuccessRate { get; set; }
        public Dictionary<string, double> PerformanceByProblemType { get; set; } = new Dictionary<string, double>();
        public List<PerformanceTrend> Trends { get; set; } = new List<PerformanceTrend>();
    }

    public class PerformanceTrend;
    {
        public DateTime Period { get; set; }
        public double SuccessRate { get; set; }
        public double Efficiency { get; set; }
        public int UsageCount { get; set; }
    }

    /// <summary>
    /// Uygulama parametreleri;
    /// </summary>
    public class ApplicationParameters;
    {
        [JsonProperty("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        [JsonProperty("adaptationRules")]
        public List<AdaptationRule> AdaptationRules { get; set; } = new List<AdaptationRule>();

        [JsonProperty("constraintRelaxation")]
        public List<ConstraintRelaxation> ConstraintRelaxations { get; set; } = new List<ConstraintRelaxation>();

        [JsonProperty("optimizationGoals")]
        public List<OptimizationGoal> OptimizationGoals { get; set; } = new List<OptimizationGoal>();

        [JsonProperty("timeLimit")]
        public TimeSpan TimeLimit { get; set; } = TimeSpan.FromMinutes(30);

        [JsonProperty("resourceLimit")]
        public Dictionary<string, double> ResourceLimits { get; set; } = new Dictionary<string, double>();

        [JsonProperty("customSettings")]
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    public class AdaptationRule;
    {
        public string Condition { get; set; }
        public string Action { get; set; }
        public string Parameter { get; set; }
        public object Value { get; set; }
    }

    public class ConstraintRelaxation;
    {
        public string ConstraintId { get; set; }
        public double RelaxationFactor { get; set; }
        public string Justification { get; set; }
    }

    public class OptimizationGoal;
    {
        public string Goal { get; set; }
        public double Weight { get; set; }
        public string Metric { get; set; }
    }

    /// <summary>
    /// Uygulanmış çözüm;
    /// </summary>
    public class AppliedSolution;
    {
        [JsonProperty("solutionId")]
        public string SolutionId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("problem")]
        public ProblemStatement Problem { get; set; }

        [JsonProperty("pattern")]
        public SolutionPattern Pattern { get; set; }

        [JsonProperty("parameters")]
        public ApplicationParameters Parameters { get; set; }

        [JsonProperty("executionSteps")]
        public List<ExecutionStep> Steps { get; set; } = new List<ExecutionStep>();

        [JsonProperty("results")]
        public Dictionary<string, object> Results { get; set; } = new Dictionary<string, object>();

        [JsonProperty("performanceMetrics")]
        public Dictionary<string, double> PerformanceMetrics { get; set; } = new Dictionary<string, double>();

        [JsonProperty("successCriteria")]
        public Dictionary<string, bool> SuccessCriteria { get; set; } = new Dictionary<string, bool>();

        [JsonProperty("adaptationsApplied")]
        public List<AdaptationApplied> Adaptations { get; set; } = new List<AdaptationApplied>();

        [JsonProperty("constraintsViolated")]
        public List<ConstraintViolation> ConstraintViolations { get; set; } = new List<ConstraintViolation>();

        [JsonProperty("overallScore")]
        public double OverallScore { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("status")]
        public SolutionStatus Status { get; set; }

        [JsonProperty("executionTime")]
        public TimeSpan ExecutionTime { get; set; }

        [JsonProperty("resourceUsage")]
        public Dictionary<string, double> ResourceUsage { get; set; } = new Dictionary<string, double>();

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("executedAt")]
        public DateTime? ExecutedAt { get; set; }

        [JsonProperty("completedAt")]
        public DateTime? CompletedAt { get; set; }
    }

    /// <summary>
    /// Yürütme adımı;
    /// </summary>
    public class ExecutionStep;
    {
        public int StepNumber { get; set; }
        public string Description { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> Inputs { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Outputs { get; set; } = new Dictionary<string, object>();
        public StepStatus Status { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public enum StepStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        Skipped;
    }

    /// <summary>
    /// Uygulanan adaptasyon;
    /// </summary>
    public class AdaptationApplied;
    {
        public string RuleId { get; set; }
        public string Description { get; set; }
        public string Parameter { get; set; }
        public object OriginalValue { get; set; }
        public object AppliedValue { get; set; }
        public string Reason { get; set; }
        public double Impact { get; set; }
    }

    /// <summary>
    /// Kısıt ihlali;
    /// </summary>
    public class ConstraintViolation;
    {
        public string ConstraintId { get; set; }
        public string Description { get; set; }
        public double ViolationDegree { get; set; }
        public SeverityLevel Severity { get; set; }
        public string Impact { get; set; }
        public List<string> MitigationActions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Çözüm durumu;
    /// </summary>
    public enum SolutionStatus;
    {
        Draft,
        Ready,
        Executing,
        Completed,
        Failed,
        Cancelled,
        PartiallySuccessful;
    }

    /// <summary>
    /// İyileştirme isteği;
    /// </summary>
    public class ImprovementRequest;
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("targetAreas")]
        public List<ImprovementArea> TargetAreas { get; set; } = new List<ImprovementArea>();

        [JsonProperty("improvementGoals")]
        public Dictionary<string, double> ImprovementGoals { get; set; } = new Dictionary<string, double>();

        [JsonProperty("constraints")]
        public List<ImprovementConstraint> Constraints { get; set; } = new List<ImprovementConstraint>();

        [JsonProperty("allowedChanges")]
        public List<ChangeType> AllowedChanges { get; set; } = new List<ChangeType>();

        [JsonProperty("riskTolerance")]
        public double RiskTolerance { get; set; } = 0.5;

        [JsonProperty("timeLimit")]
        public TimeSpan TimeLimit { get; set; } = TimeSpan.FromHours(1);

        [JsonProperty("resourceBudget")]
        public Dictionary<string, double> ResourceBudget { get; set; } = new Dictionary<string, double>();
    }

    public class ImprovementArea;
    {
        public string Area { get; set; }
        public string Description { get; set; }
        public double CurrentPerformance { get; set; }
        public double TargetPerformance { get; set; }
        public double Priority { get; set; }
    }

    public class ImprovementConstraint;
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public string Condition { get; set; }
        public bool IsHard { get; set; } = true;
    }

    public enum ChangeType;
    {
        ParameterAdjustment,
        AlgorithmModification,
        ResourceReallocation,
        ConstraintRelaxation,
        StepReordering,
        Parallelization,
        Caching,
        Precomputation,
        Custom;
    }

    /// <summary>
    /// Çözüm iyileştirmesi;
    /// </summary>
    public class SolutionImprovement;
    {
        [JsonProperty("improvementId")]
        public string ImprovementId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("originalSolution")]
        public AppliedSolution OriginalSolution { get; set; }

        [JsonProperty("improvedSolution")]
        public AppliedSolution ImprovedSolution { get; set; }

        [JsonProperty("improvementAreas")]
        public List<ImprovedArea> Improvements { get; set; } = new List<ImprovedArea>();

        [JsonPropertyperformanceGains]
        public Dictionary<string, double> PerformanceGains { get; set; } = new Dictionary<string, double>();

        [JsonProperty("changesMade")]
        public List<SolutionChange> Changes { get; set; } = new List<SolutionChange>();

        [JsonProperty("validationResults")]
        public ValidationResult Validation { get; set; }

        [JsonProperty("overallImprovement")]
        public double OverallImprovement { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("improvedAt")]
        public DateTime ImprovedAt { get; set; } = DateTime.UtcNow;
    }

    public class ImprovedArea;
    {
        public string Area { get; set; }
        public double Before { get; set; }
        public double After { get; set; }
        public double Improvement { get; set; }
        public string Explanation { get; set; }
    }

    public class SolutionChange;
    {
        public string ChangeType { get; set; }
        public string Description { get; set; }
        public object Before { get; set; }
        public object After { get; set; }
        public double Impact { get; set; }
        public string Rationale { get; set; }
    }

    /// <summary>
    /// Karşılaştırma kriterleri;
    /// </summary>
    public class ComparisonCriteria;
    {
        [JsonProperty("criteria")]
        public List<ComparisonCriterion> Criteria { get; set; } = new List<ComparisonCriterion>();

        [JsonProperty("weights")]
        public Dictionary<string, double> Weights { get; set; } = new Dictionary<string, double>();

        [JsonProperty("normalizationMethod")]
        public NormalizationMethod Normalization { get; set; } = NormalizationMethod.MinMax;

        [JsonProperty("thresholds")]
        public Dictionary<string, Threshold> Thresholds { get; set; } = new Dictionary<string, Threshold>();

        [JsonProperty("aggregationMethod")]
        public AggregationMethod Aggregation { get; set; } = AggregationMethod.WeightedSum;
    }

    public class ComparisonCriterion;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public CriterionType Type { get; set; }
        public bool HigherIsBetter { get; set; } = true;
        public string Measurement { get; set; }
        public double Weight { get; set; } = 1.0;
    }

    public enum CriterionType;
    {
        Performance,
        Efficiency,
        Reliability,
        Cost,
        Time,
        ResourceUsage,
        Quality,
        Risk,
        Scalability,
        Custom;
    }

    public enum NormalizationMethod;
    {
        MinMax,
        ZScore,
        DecimalScaling,
        Sigmoid,
        Custom;
    }

    public class Threshold;
    {
        public double Min { get; set; }
        public double Max { get; set; }
        public bool Required { get; set; } = false;
    }

    /// <summary>
    /// Çözüm karşılaştırması;
    /// </summary>
    public class SolutionComparison;
    {
        [JsonProperty("comparisonId")]
        public string ComparisonId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("solutions")]
        public List<AppliedSolution> Solutions { get; set; } = new List<AppliedSolution>();

        [JsonProperty("criteria")]
        public ComparisonCriteria Criteria { get; set; }

        [JsonProperty("scores")]
        public Dictionary<string, Dictionary<string, double>> Scores { get; set; }
            = new Dictionary<string, Dictionary<string, double>>();

        [JsonProperty("normalizedScores")]
        public Dictionary<string, Dictionary<string, double>> NormalizedScores { get; set; }
            = new Dictionary<string, Dictionary<string, double>>();

        [JsonProperty("aggregatedScores")]
        public Dictionary<string, double> AggregatedScores { get; set; }
            = new Dictionary<string, double>();

        [JsonProperty("rankings")]
        public List<SolutionRanking> Rankings { get; set; } = new List<SolutionRanking>();

        [JsonProperty("tradeoffAnalysis")]
        public TradeoffAnalysis Tradeoffs { get; set; }

        [JsonProperty("recommendation")]
        public Recommendation Recommendation { get; set; }

        [JsonProperty("comparisonDate")]
        public DateTime ComparisonDate { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Çözüm sıralaması;
    /// </summary>
    public class SolutionRanking;
    {
        public string SolutionId { get; set; }
        public int Rank { get; set; }
        public double Score { get; set; }
        public List<string> Strengths { get; set; } = new List<string>();
        public List<string> Weaknesses { get; set; } = new List<string>();
        public string BestFor { get; set; }
    }

    /// <summary>
    /// Trade-off analizi;
    /// </summary>
    public class TradeoffAnalysis;
    {
        public List<TradeoffPair> Tradeoffs { get; set; } = new List<TradeoffPair>();
        public List<ParetoFrontSolution> ParetoFront { get; set; } = new List<ParetoFrontSolution>();
        public Dictionary<string, double> DominanceRelations { get; set; }
            = new Dictionary<string, double>();
    }

    public class TradeoffPair;
    {
        public string Criterion1 { get; set; }
        public string Criterion2 { get; set; }
        public double TradeoffDegree { get; set; }
        public string Description { get; set; }
        public List<string> AffectedSolutions { get; set; } = new List<string>();
    }

    public class ParetoFrontSolution;
    {
        public string SolutionId { get; set; }
        public Dictionary<string, double> Values { get; set; } = new Dictionary<string, double>();
        public bool IsParetoOptimal { get; set; }
        public int DominatesCount { get; set; }
        public int DominatedByCount { get; set; }
    }

    /// <summary>
    /// Tavsiye;
    /// </summary>
    public class Recommendation;
    {
        public string RecommendedSolutionId { get; set; }
        public double Confidence { get; set; }
        public string Rationale { get; set; }
        public List<AlternativeRecommendation> Alternatives { get; set; }
            = new List<AlternativeRecommendation>();
        public List<string> ImplementationConsiderations { get; set; }
            = new List<string>();
    }

    public class AlternativeRecommendation;
    {
        public string SolutionId { get; set; }
        public string Scenario { get; set; }
        public string Reason { get; set; }
        public double SuitabilityScore { get; set; }
    }

    /// <summary>
    /// Çözüm öğrenme isteği;
    /// </summary>
    public class SolutionLearningRequest;
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("solutions")]
        public List<AppliedSolution> Solutions { get; set; } = new List<AppliedSolution>();

        [JsonProperty("patternType")]
        public PatternType TargetPatternType { get; set; }

        [JsonProperty("learningMethod")]
        public LearningMethod Method { get; set; } = LearningMethod.Inductive;

        [JsonProperty("parameters")]
        public LearningParameters Parameters { get; set; } = new LearningParameters();

        [JsonProperty("validationSplit")]
        public double ValidationSplit { get; set; } = 0.2;

        [JsonProperty("minConfidence")]
        public double MinConfidence { get; set; } = 0.8;

        [JsonProperty("complexityLimit")]
        public ComplexityLevel ComplexityLimit { get; set; } = ComplexityLevel.Complex;

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public enum LearningMethod;
    {
        Inductive,
        Deductive,
        Analogical,
        CaseBased,
        Statistical,
        MachineLearning,
        Hybrid;
    }

    public class LearningParameters;
    {
        public int MinSamples { get; set; } = 10;
        public double GeneralizationFactor { get; set; } = 0.7;
        public bool AllowAbstraction { get; set; } = true;
        public int MaxPatternSteps { get; set; } = 20;
        public Dictionary<string, object> AlgorithmSpecific { get; set; }
            = new Dictionary<string, object>();
    }

    /// <summary>
    /// Öğrenilmiş pattern;
    /// </summary>
    public class LearnedPattern;
    {
        [JsonProperty("patternId")]
        public string PatternId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("pattern")]
        public SolutionPattern Pattern { get; set; }

        [JsonProperty("learningMethod")]
        public LearningMethod Method { get; set; }

        [JsonProperty("sourceSolutions")]
        public List<string> SourceSolutionIds { get; set; } = new List<string>();

        [JsonProperty("learningMetrics")]
        public LearningMetrics Metrics { get; set; }

        [JsonProperty("validationResults")]
        public LearningValidation Validation { get; set; }

        [JsonProperty("abstractions")]
        public List<Abstraction> Abstractions { get; set; } = new List<Abstraction>();

        [JsonProperty("discoveredRules")]
        public List<DiscoveredRule> DiscoveredRules { get; set; } = new List<DiscoveredRule>();

        [JsonProperty("learnedAt")]
        public DateTime LearnedAt { get; set; } = DateTime.UtcNow;
    }

    public class LearningMetrics;
    {
        public double Accuracy { get; set; }
        public double Coverage { get; set; }
        public double GeneralizationScore { get; set; }
        public double ComplexityScore { get; set; }
        public double NoveltyScore { get; set; }
        public TimeSpan LearningTime { get; set; }
    }

    public class LearningValidation;
    {
        public double CrossValidationScore { get; set; }
        public Dictionary<string, double> PerformanceByCategory { get; set; }
            = new Dictionary<string, double>();
        public List<ValidationCase> TestCases { get; set; } = new List<ValidationCase>();
        public bool IsValid { get; set; }
    }

    public class ValidationCase;
    {
        public string CaseId { get; set; }
        public double ExpectedPerformance { get; set; }
        public double ActualPerformance { get; set; }
        public double Error { get; set; }
        public bool Passed { get; set; }
    }

    public class Abstraction;
    {
        public string OriginalElement { get; set; }
        public string AbstractedElement { get; set; }
        public string AbstractionRule { get; set; }
        public double Confidence { get; set; }
    }

    public class DiscoveredRule;
    {
        public string RuleId { get; set; }
        public string Condition { get; set; }
        public string Action { get; set; }
        public double Support { get; set; }
        public double Confidence { get; set; }
        public double Lift { get; set; }
    }

    /// <summary>
    /// Benzerlik eşiği;
    /// </summary>
    public class SimilarityThreshold;
    {
        public double MinSimilarity { get; set; } = 0.7;
        public Dictionary<string, double> FeatureWeights { get; set; }
            = new Dictionary<string, double>();
        public SimilarityMethod Method { get; set; } = SimilarityMethod.Cosine;
        public int MaxResults { get; set; } = 10;
    }

    public enum SimilarityMethod;
    {
        Cosine,
        Euclidean,
        Jaccard,
        Levenshtein,
        Semantic,
        Hybrid;
    }

    /// <summary>
    /// Case-based çözüm;
    /// </summary>
    public class CaseBasedSolution;
    {
        [JsonProperty("caseId")]
        public string CaseId { get; set; }

        [JsonProperty("originalProblem")]
        public ProblemStatement OriginalProblem { get; set; }

        [JsonProperty("originalSolution")]
        public AppliedSolution OriginalSolution { get; set; }

        [JsonProperty("similarityScore")]
        public double SimilarityScore { get; set; }

        [JsonProperty("similarityFactors")]
        public Dictionary<string, double> SimilarityFactors { get; set; }
            = new Dictionary<string, double>();

        [JsonProperty("adaptationsRequired")]
        public List<RequiredAdaptation> AdaptationsRequired { get; set; }
            = new List<RequiredAdaptation>();

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("applicability")]
        public double Applicability { get; set; }
    }

    public class RequiredAdaptation;
    {
        public string Element { get; set; }
        public string AdaptationType { get; set; }
        public string Description { get; set; }
        public double Difficulty { get; set; }
        public string AdaptationRule { get; set; }
    }

    /// <summary>
    /// Optimizasyon isteği;
    /// </summary>
    public class OptimizationRequest;
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("objectives")]
        public List<OptimizationObjective> Objectives { get; set; }
            = new List<OptimizationObjective>();

        [JsonProperty("constraints")]
        public List<OptimizationConstraint> Constraints { get; set; }
            = new List<OptimizationConstraint>();

        [JsonProperty("parameters")]
        public OptimizationParameters Parameters { get; set; }
            = new OptimizationParameters();

        [JsonProperty("method")]
        public OptimizationMethod Method { get; set; } = OptimizationMethod.MultiObjective;

        [JsonProperty("terminationConditions")]
        public TerminationConditions Termination { get; set; }
            = new TerminationConditions();

        [JsonProperty("resourceLimits")]
        public Dictionary<string, double> ResourceLimits { get; set; }
            = new Dictionary<string, double>();
    }

    public class OptimizationObjective;
    {
        public string Metric { get; set; }
        public OptimizationDirection Direction { get; set; }
        public double Weight { get; set; } = 1.0;
        public double Target { get; set; }
        public bool IsHard { get; set; } = false;
    }

    public enum OptimizationDirection;
    {
        Minimize,
        Maximize,
        TargetValue,
        MinimizeDeviation;
    }

    public class OptimizationConstraint;
    {
        public string ConstraintId { get; set; }
        public string Type { get; set; }
        public string Condition { get; set; }
        public double LowerBound { get; set; } = double.MinValue;
        public double UpperBound { get; set; } = double.MaxValue;
        public bool IsHard { get; set; } = true;
    }

    public class OptimizationParameters;
    {
        public int MaxIterations { get; set; } = 1000;
        public int PopulationSize { get; set; } = 50;
        public double CrossoverRate { get; set; } = 0.8;
        public double MutationRate { get; set; } = 0.1;
        public double ConvergenceThreshold { get; set; } = 0.001;
        public Dictionary<string, object> AlgorithmSpecific { get; set; }
            = new Dictionary<string, object>();
    }

    public enum OptimizationMethod;
    {
        GradientDescent,
        GeneticAlgorithm,
        ParticleSwarm,
        SimulatedAnnealing,
        TabuSearch,
        BayesianOptimization,
        MultiObjective,
        Hybrid;
    }

    public class TerminationConditions;
    {
        public int MaxIterations { get; set; } = 1000;
        public TimeSpan MaxTime { get; set; } = TimeSpan.FromMinutes(30);
        public double ConvergenceThreshold { get; set; } = 0.001;
        public int StagnationLimit { get; set; } = 50;
        public bool StopOnConstraintViolation { get; set; } = false;
    }

    /// <summary>
    /// Optimize edilmiş çözüm;
    /// </summary>
    public class OptimizedSolution;
    {
        [JsonProperty("optimizationId")]
        public string OptimizationId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("originalSolution")]
        public AppliedSolution OriginalSolution { get; set; }

        [JsonProperty("optimizedSolution")]
        public AppliedSolution OptimizedSolution { get; set; }

        [JsonProperty("optimizationMethod")]
        public OptimizationMethod Method { get; set; }

        [JsonProperty("improvements")]
        public Dictionary<string, double> Improvements { get; set; }
            = new Dictionary<string, double>();

        [JsonProperty("optimizationPath")]
        public List<OptimizationStep> OptimizationPath { get; set; }
            = new List<OptimizationStep>();

        [JsonProperty("convergenceMetrics")]
        public ConvergenceMetrics Convergence { get; set; }

        [JsonProperty("paretoFront")]
        public List<ParetoSolution> ParetoFront { get; set; }
            = new List<ParetoSolution>();

        [JsonProperty("executionStatistics")]
        public OptimizationStatistics Statistics { get; set; }

        [JsonProperty("optimizedAt")]
        public DateTime OptimizedAt { get; set; } = DateTime.UtcNow;
    }

    public class OptimizationStep;
    {
        public int Iteration { get; set; }
        public Dictionary<string, double> ObjectiveValues { get; set; }
            = new Dictionary<string, double>();
        public Dictionary<string, double> ParameterValues { get; set; }
            = new Dictionary<string, double>();
        public double OverallFitness { get; set; }
        public bool IsFeasible { get; set; }
        public List<string> ViolatedConstraints { get; set; }
            = new List<string>();
    }

    public class ConvergenceMetrics;
    {
        public bool Converged { get; set; }
        public ConvergenceReason Reason { get; set; }
        public int Iterations { get; set; }
        public TimeSpan Duration { get; set; }
        public double FinalFitness { get; set; }
        public double ImprovementRate { get; set; }
        public double Diversity { get; set; }
    }

    public enum ConvergenceReason;
    {
        MaxIterations,
        Convergence,
        TimeLimit,
        Stagnation,
        ConstraintViolation,
        ManualStop;
    }

    public class ParetoSolution;
    {
        public int Rank { get; set; }
        public Dictionary<string, double> Objectives { get; set; }
            = new Dictionary<string, double>();
        public Dictionary<string, object> Parameters { get; set; }
            = new Dictionary<string, object>();
        public double CrowdingDistance { get; set; }
        public bool IsOptimal { get; set; }
    }

    public class OptimizationStatistics;
    {
        public int FunctionEvaluations { get; set; }
        public int FeasibleSolutions { get; set; }
        public int InfeasibleSolutions { get; set; }
        public TimeSpan TotalTime { get; set; }
        public Dictionary<string, TimeSpan> TimeByPhase { get; set; }
            = new Dictionary<string, TimeSpan>();
        public double AverageImprovement { get; set; }
        public double BestImprovement { get; set; }
    }

    /// <summary>
    /// Doğrulama parametreleri;
    /// </summary>
    public class ValidationParameters;
    {
        [JsonProperty("validationMethod")]
        public ValidationMethod Method { get; set; } = ValidationMethod.Comprehensive;

        [JsonProperty("testCases")]
        public List<TestCase> TestCases { get; set; } = new List<TestCase>();

        [JsonProperty("acceptanceCriteria")]
        public List<AcceptanceCriterion> AcceptanceCriteria { get; set; }
            = new List<AcceptanceCriterion>();

        [JsonProperty("stressTests")]
        public List<StressTest> StressTests { get; set; } = new List<StressTest>();

        [JsonProperty("randomization")]
        public RandomizationSettings Randomization { get; set; }
            = new RandomizationSettings();

        [JsonProperty("performanceThresholds")]
        public Dictionary<string, Threshold> PerformanceThresholds { get; set; }
            = new Dictionary<string, Threshold>();

        [JsonProperty("timeLimit")]
        public TimeSpan TimeLimit { get; set; } = TimeSpan.FromHours(1);
    }

    public enum ValidationMethod;
    {
        UnitTesting,
        IntegrationTesting,
        StressTesting,
        RandomizedTesting,
        FormalVerification,
        ModelChecking,
        Comprehensive;
    }

    public class TestCase;
    {
        public string CaseId { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Inputs { get; set; }
            = new Dictionary<string, object>();
        public Dictionary<string, object> ExpectedOutputs { get; set; }
            = new Dictionary<string, object>();
        public Dictionary<string, object> Constraints { get; set; }
            = new Dictionary<string, object>();
        public double Weight { get; set; } = 1.0;
    }

    public class AcceptanceCriterion;
    {
        public string Criterion { get; set; }
        public string Description { get; set; }
        public double Threshold { get; set; }
        public bool MustPass { get; set; } = true;
        public double Weight { get; set; } = 1.0;
    }

    public class StressTest;
    {
        public string TestId { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> ExtremeConditions { get; set; }
            = new Dictionary<string, object>();
        public string FailureMode { get; set; }
        public double RecoveryRequirement { get; set; } = 0.8;
    }

    public class RandomizationSettings;
    {
        public int SampleSize { get; set; } = 1000;
        public string Distribution { get; set; } = "Uniform";
        public Dictionary<string, object> DistributionParameters { get; set; }
            = new Dictionary<string, object>();
        public int Seed { get; set; } = 42;
    }

    /// <summary>
    /// Doğrulama sonucu;
    /// </summary>
    public class ValidationResult;
    {
        [JsonProperty("validationId")]
        public string ValidationId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("solution")]
        public AppliedSolution Solution { get; set; }

        [JsonProperty("validationMethod")]
        public ValidationMethod Method { get; set; }

        [JsonProperty("testResults")]
        public List<TestResult> TestResults { get; set; } = new List<TestResult>();

        [JsonProperty("acceptanceResults")]
        public Dictionary<string, AcceptanceResult> AcceptanceResults { get; set; }
            = new Dictionary<string, AcceptanceResult>();

        [JsonProperty("stressTestResults")]
        public List<StressTestResult> StressTestResults { get; set; }
            = new List<StressTestResult>();

        [JsonProperty("performanceMetrics")]
        public Dictionary<string, PerformanceResult> PerformanceMetrics { get; set; }
            = new Dictionary<string, PerformanceResult>();

        [JsonProperty("overallScore")]
        public double OverallScore { get; set; }

        [JsonProperty("isValid")]
        public bool IsValid { get; set; }

        [JsonProperty("validationIssues")]
        public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();

        [JsonProperty("recommendations")]
        public List<ValidationRecommendation> Recommendations { get; set; }
            = new List<ValidationRecommendation>();

        [JsonProperty("validatedAt")]
        public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TestResult;
    {
        public string CaseId { get; set; }
        public bool Passed { get; set; }
        public Dictionary<string, object> ActualOutputs { get; set; }
            = new Dictionary<string, object>();
        public Dictionary<string, double> Deviations { get; set; }
            = new Dictionary<string, double>();
        public TimeSpan ExecutionTime { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public double Score { get; set; }
    }

    public class AcceptanceResult;
    {
        public string Criterion { get; set; }
        public bool Passed { get; set; }
        public double ActualValue { get; set; }
        public double Threshold { get; set; }
        public double Margin { get; set; }
        public string Assessment { get; set; }
    }

    public class StressTestResult;
    {
        public string TestId { get; set; }
        public bool Survived { get; set; }
        public string FailurePoint { get; set; }
        public double RecoveryRate { get; set; }
        public List<string> Degradations { get; set; } = new List<string>();
        public string ResilienceAssessment { get; set; }
    }

    public class PerformanceResult;
    {
        public string Metric { get; set; }
        public double Value { get; set; }
        public double Threshold { get; set; }
        public bool MeetsThreshold { get; set; }
        public string Unit { get; set; }
        public string Interpretation { get; set; }
    }

    public class ValidationIssue;
    {
        public string IssueId { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public SeverityLevel Severity { get; set; }
        public string Impact { get; set; }
        public List<string> RemediationSteps { get; set; } = new List<string>();
    }

    public class ValidationRecommendation;
    {
        public string Area { get; set; }
        public string Recommendation { get; set; }
        public PriorityLevel Priority { get; set; }
        public double ExpectedImprovement { get; set; }
        public string ImplementationGuidance { get; set; }
    }

    public enum PriorityLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Risk analizi bağlamı;
    /// </summary>
    public class RiskAnalysisContext;
    {
        [JsonProperty("stakeholders")]
        public List<Stakeholder> Stakeholders { get; set; } = new List<Stakeholder>();

        [JsonProperty("riskCategories")]
        public List<RiskCategory> RiskCategories { get; set; } = new List<RiskCategory>();

        [JsonProperty("riskTolerance")]
        public Dictionary<string, double> RiskTolerance { get; set; }
            = new Dictionary<string, double>();

        [JsonProperty("impactMetrics")]
        public List<ImpactMetric> ImpactMetrics { get; set; } = new List<ImpactMetric>();

        [JsonProperty("mitigationStrategies")]
        public List<MitigationStrategy> AvailableMitigations { get; set; }
            = new List<MitigationStrategy>();

        [JsonProperty("historicalRisks")]
        public List<HistoricalRisk> HistoricalRisks { get; set; }
            = new List<HistoricalRisk>();

        [JsonProperty("regulatoryConstraints")]
        public List<RegulatoryConstraint> RegulatoryConstraints { get; set; }
            = new List<RegulatoryConstraint>();
    }

    public class RiskCategory;
    {
        public string Category { get; set; }
        public string Description { get; set; }
        public List<string> Subcategories { get; set; } = new List<string>();
        public double Weight { get; set; } = 1.0;
    }

    public class ImpactMetric;
    {
        public string Metric { get; set; }
        public string Description { get; set; }
        public string Measurement { get; set; }
        public Dictionary<string, double> ImpactLevels { get; set; }
            = new Dictionary<string, double>();
    }

    public class MitigationStrategy;
    {
        public string StrategyId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> ApplicableRisks { get; set; } = new List<string>();
        public double Effectiveness { get; set; }
        public double Cost { get; set; }
        public TimeSpan ImplementationTime { get; set; }
    }

    public class HistoricalRisk;
    {
        public string RiskId { get; set; }
        public string Description { get; set; }
        public double Probability { get; set; }
        public double Impact { get; set; }
        public string MitigationUsed { get; set; }
        public bool Materialized { get; set; }
        public double ActualImpact { get; set; }
    }

    public class RegulatoryConstraint;
    {
        public string Regulation { get; set; }
        public string Requirement { get; set; }
        public string ComplianceLevel { get; set; }
        public double Penalty { get; set; }
        public string VerificationMethod { get; set; }
    }

    /// <summary>
    /// Risk değerlendirmesi;
    /// </summary>
    public class RiskAssessment;
    {
        [JsonProperty("assessmentId")]
        public string AssessmentId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("solution")]
        public AppliedSolution Solution { get; set; }

        [JsonProperty("identifiedRisks")]
        public List<IdentifiedRisk> IdentifiedRisks { get; set; }
            = new List<IdentifiedRisk>();

        [JsonProperty("riskMatrix")]
        public RiskMatrix Matrix { get; set; }

        [JsonProperty("riskScore")]
        public double RiskScore { get; set; }

        [JsonProperty("riskLevel")]
        public RiskLevel Level { get; set; }

        [JsonProperty("mitigationPlan")]
        public MitigationPlan Plan { get; set; }

        [JsonProperty("residualRisks")]
        public List<ResidualRisk> ResidualRisks { get; set; }
            = new List<ResidualRisk>();

        [JsonProperty("recommendations")]
        public List<RiskRecommendation> Recommendations { get; set; }
            = new List<RiskRecommendation>();

        [JsonProperty("assessmentDate")]
        public DateTime AssessmentDate { get; set; } = DateTime.UtcNow;
    }

    public class IdentifiedRisk;
    {
        public string RiskId { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public double Probability { get; set; }
        public double Impact { get; set; }
        public double RiskExposure { get; set; }
        public List<string> Triggers { get; set; } = new List<string>();
        public List<string> Dependencies { get; set; } = new List<string>();
        public string DetectionMethod { get; set; }
    }

    public class RiskMatrix;
    {
        public Dictionary<string, Dictionary<string, List<RiskCell>>> Matrix { get; set; }
            = new Dictionary<string, Dictionary<string, List<RiskCell>>>();
        public List<RiskLevel> Levels { get; set; } = new List<RiskLevel>();
        public string MatrixType { get; set; }
    }

    public class RiskCell;
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string RiskLevel { get; set; }
        public List<string> Actions { get; set; } = new List<string>();
    }

    public enum RiskLevel;
    {
        Negligible,
        Low,
        Moderate,
        High,
        Extreme;
    }

    public class MitigationPlan;
    {
        public List<MitigationAction> Actions { get; set; } = new List<MitigationAction>();
        public double ExpectedRiskReduction { get; set; }
        public double ImplementationCost { get; set; }
        public TimeSpan ImplementationTime { get; set; }
        public Dictionary<string, double> EffectivenessByRisk { get; set; }
            = new Dictionary<string, double>();
    }

    public class MitigationAction;
    {
        public string ActionId { get; set; }
        public string Description { get; set; }
        public string TargetRisk { get; set; }
        public double ExpectedEffectiveness { get; set; }
        public double Cost { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public string Owner { get; set; }
    }

    public class ResidualRisk;
    {
        public string RiskId { get; set; }
        public double ResidualProbability { get; set; }
        public double ResidualImpact { get; set; }
        public string AcceptanceRationale { get; set; }
        public string MonitoringPlan { get; set; }
    }

    public class RiskRecommendation;
    {
        public string Recommendation { get; set; }
        public RiskLevel Priority { get; set; }
        public double ExpectedBenefit { get; set; }
        public string ImplementationGuidance { get; set; }
    }

    /// <summary>
    /// Geri alma stratejisi;
    /// </summary>
    public class RollbackStrategy;
    {
        [JsonProperty("strategyType")]
        public RollbackType Type { get; set; } = RollbackType.Incremental;

        [JsonProperty("checkpoints")]
        public List<Checkpoint> Checkpoints { get; set; } = new List<Checkpoint>();

        [JsonProperty("rollbackSteps")]
        public List<RollbackStep> Steps { get; set; } = new List<RollbackStep>();

        [JsonProperty("timeLimit")]
        public TimeSpan TimeLimit { get; set; } = TimeSpan.FromMinutes(30);

        [JsonProperty("resourceAllocation")]
        public Dictionary<string, double> ResourceAllocation { get; set; }
            = new Dictionary<string, double>();

        [JsonProperty("validationSteps")]
        public List<ValidationStep> ValidationSteps { get; set; }
            = new List<ValidationStep>();

        [JsonProperty("riskAssessment")]
        public RollbackRiskAssessment RiskAssessment { get; set; }
    }

    public enum RollbackType;
    {
        Full,
        Partial,
        Incremental,
        Compensating,
        Hybrid;
    }

    public class Checkpoint;
    {
        public string CheckpointId { get; set; }
        public string Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public Dictionary<string, object> State { get; set; }
            = new Dictionary<string, object>();
        public List<string> Dependencies { get; set; } = new List<string>();
        public double IntegrityScore { get; set; }
    }

    public class RollbackStep;
    {
        public int StepNumber { get; set; }
        public string Description { get; set; }
        public string Action { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public TimeSpan EstimatedDuration { get; set; }
        public List<string> Preconditions { get; set; } = new List<string>();
        public List<string> Postconditions { get; set; } = new List<string>();
    }

    public class ValidationStep;
    {
        public string ValidationId { get; set; }
        public string Description { get; set; }
        public string Method { get; set; }
        public Dictionary<string, object> Criteria { get; set; }
            = new Dictionary<string, object>();
        public bool IsCritical { get; set; } = true;
    }

    public class RollbackRiskAssessment;
    {
        public List<RollbackRisk> Risks { get; set; } = new List<RollbackRisk>();
        public double OverallRisk { get; set; }
        public List<ContingencyPlan> ContingencyPlans { get; set; }
            = new List<ContingencyPlan>();
    }

    public class RollbackRisk;
    {
        public string RiskId { get; set; }
        public string Description { get; set; }
        public double Probability { get; set; }
        public double Impact { get; set; }
        public List<string> MitigationActions { get; set; } = new List<string>();
    }

    public class ContingencyPlan;
    {
        public string Scenario { get; set; }
        public string Trigger { get; set; }
        public List<string> Actions { get; set; } = new List<string>();
        public string Owner { get; set; }
        public TimeSpan ResponseTime { get; set; }
    }

    /// <summary>
    /// Geri alma sonucu;
    /// </summary>
    public class RollbackResult;
    {
        [JsonProperty("rollbackId")]
        public string RollbackId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("originalSolution")]
        public AppliedSolution OriginalSolution { get; set; }

        [JsonProperty("rollbackStrategy")]
        public RollbackStrategy Strategy { get; set; }

        [JsonProperty("executionSteps")]
        public List<RollbackExecutionStep> Steps { get; set; }
            = new List<RollbackExecutionStep>();

        [JsonProperty("finalState")]
        public SystemState FinalState { get; set; }

        [JsonProperty("successMetrics")]
        public RollbackMetrics Metrics { get; set; }

        [JsonProperty("issuesEncountered")]
        public List<RollbackIssue> Issues { get; set; } = new List<RollbackIssue>();

        [JsonProperty("isSuccessful")]
        public bool IsSuccessful { get; set; }

        [JsonProperty("rollbackDuration")]
        public TimeSpan Duration { get; set; }

        [JsonProperty("rolledBackAt")]
        public DateTime RolledBackAt { get; set; } = DateTime.UtcNow;
    }

    public class RollbackExecutionStep;
    {
        public int StepNumber { get; set; }
        public string Description { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public List<string> ActionsPerformed { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public string StateBefore { get; set; }
        public string StateAfter { get; set; }
    }

    public class SystemState;
    {
        public Dictionary<string, object> State { get; set; }
            = new Dictionary<string, object>();
        public string IntegrityHash { get; set; }
        public DateTime CapturedAt { get; set; }
        public List<string> Validations { get; set; } = new List<string>();
    }

    public class RollbackMetrics;
    {
        public double Completeness { get; set; }
        public double Integrity { get; set; }
        public double TimeEfficiency { get; set; }
        public double ResourceEfficiency { get; set; }
        public double SafetyScore { get; set; }
    }

    public class RollbackIssue;
    {
        public string IssueId { get; set; }
        public string Step { get; set; }
        public string Description { get; set; }
        public SeverityLevel Severity { get; set; }
        public string Resolution { get; set; }
        public bool Resolved { get; set; }
    }

    /// <summary>
    /// Kategorizasyon stratejisi;
    /// </summary>
    public class CategorizationStrategy;
    {
        [JsonProperty("strategyType")]
        public CategorizationType Type { get; set; } = CategorizationType.Hierarchical;

        [JsonProperty("categories")]
        public List<SolutionCategory> Categories { get; set; }
            = new List<SolutionCategory>();

        [JsonProperty("classificationRules")]
        public List<ClassificationRule> Rules { get; set; }
            = new List<ClassificationRule>();

        [JsonProperty("similarityThreshold")]
        public double SimilarityThreshold { get; set; } = 0.7;

        [JsonProperty("clusteringParameters")]
        public ClusteringParameters ClusteringParameters { get; set; }
            = new ClusteringParameters();

        [JsonProperty("validationRules")]
        public List<ValidationRule> ValidationRules { get; set; }
            = new List<ValidationRule>();
    }

    public enum CategorizationType;
    {
        Hierarchical,
        Flat,
        Clustering,
        RuleBased,
        Hybrid;
    }

    public class SolutionCategory;
    {
        public string CategoryId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Characteristics { get; set; } = new List<string>();
        public List<string> Subcategories { get; set; } = new List<string>();
        public Dictionary<string, double> Prototype { get; set; }
            = new Dictionary<string, double>();
    }

    public class ClassificationRule;
    {
        public string RuleId { get; set; }
        public string Condition { get; set; }
        public string Category { get; set; }
        public double Confidence { get; set; }
        public int Priority { get; set; } = 1;
    }

    public class ClusteringParameters;
    {
        public int NumberOfClusters { get; set; } = 5;
        public string Algorithm { get; set; } = "KMeans";
        public Dictionary<string, object> Parameters { get; set; }
            = new Dictionary<string, object>();
        public int MaxIterations { get; set; } = 100;
        public double ConvergenceThreshold { get; set; } = 0.001;
    }

    public class ValidationRule;
    {
        public string RuleId { get; set; }
        public string Condition { get; set; }
        public string Message { get; set; }
        public SeverityLevel Severity { get; set; }
    }

    /// <summary>
    /// Çözüm kategorizasyonu;
    /// </summary>
    public class SolutionCategorization;
    {
        [JsonProperty("categorizationId")]
        public string CategorizationId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("solutions")]
        public List<AppliedSolution> Solutions { get; set; }
            = new List<AppliedSolution>();

        [JsonProperty("strategy")]
        public CategorizationStrategy Strategy { get; set; }

        [JsonProperty("categories")]
        public Dictionary<string, List<CategorizedSolution>> Categories { get; set; }
            = new Dictionary<string, List<CategorizedSolution>>();

        [JsonProperty("clusters")]
        public List<SolutionCluster> Clusters { get; set; }
            = new List<SolutionCluster>();

        [JsonProperty("classificationMetrics")]
        public ClassificationMetrics Metrics { get; set; }

        [JsonProperty("validationResults")]
        public List<ValidationResult> ValidationResults { get; set; }
            = new List<ValidationResult>();

        [JsonProperty("recommendations")]
        public List<CategorizationRecommendation> Recommendations { get; set; }
            = new List<CategorizationRecommendation>();

        [JsonProperty("categorizedAt")]
        public DateTime CategorizedAt { get; set; } = DateTime.UtcNow;
    }

    public class CategorizedSolution;
    {
        public string SolutionId { get; set; }
        public string Category { get; set; }
        public double Confidence { get; set; }
        public List<string> MatchingCharacteristics { get; set; }
            = new List<string>();
        public Dictionary<string, double> SimilarityScores { get; set; }
            = new Dictionary<string, double>();
    }

    public class SolutionCluster;
    {
        public int ClusterId { get; set; }
        public List<string> SolutionIds { get; set; } = new List<string>();
        public Dictionary<string, double> Center { get; set; }
            = new Dictionary<string, double>();
        public double Cohesion { get; set; }
        public double Separation { get; set; }
        public string Label { get; set; }
        public List<string> Characteristics { get; set; } = new List<string>();
    }

    public class ClassificationMetrics;
    {
        public double Accuracy { get; set; }
        public double Precision { get; set; }
        public double Recall { get; set; }
        public double F1Score { get; set; }
        public double SilhouetteScore { get; set; }
        public Dictionary<string, double> CategoryMetrics { get; set; }
            = new Dictionary<string, double>();
    }

    public class CategorizationRecommendation;
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public double Priority { get; set; }
        public List<string> AffectedCategories { get; set; }
            = new List<string>();
        public string Implementation { get; set; }
    }

    /// <summary>
    /// Birleştirme stratejisi;
    /// </summary>
    public class CombinationStrategy;
    {
        [JsonProperty("strategyType")]
        public CombinationType Type { get; set; } = CombinationType.Sequential;

        [JsonProperty("integrationRules")]
        public List<IntegrationRule> IntegrationRules { get; set; }
            = new List<IntegrationRule>();

        [JsonProperty("conflictResolution")]
        public ConflictResolutionStrategy ConflictResolution { get; set; }
            = new ConflictResolutionStrategy();

        [JsonProperty("optimizationGoals")]
        public List<CombinationGoal> Goals { get; set; }
            = new List<CombinationGoal>();

        [JsonProperty("validationRules")]
        public List<CombinationValidationRule> ValidationRules { get; set; }
            = new List<CombinationValidationRule>();
    }

    public enum CombinationType;
    {
        Sequential,
        Parallel,
        Hierarchical,
        Hybrid,
        MetaPattern;
    }

    public class IntegrationRule;
    {
        public string RuleId { get; set; }
        public string Pattern1 { get; set; }
        public string Pattern2 { get; set; }
        public string IntegrationMethod { get; set; }
        public List<string> Preconditions { get; set; } = new List<string>();
        public List<string> Postconditions { get; set; } = new List<string>();
        public double CompatibilityScore { get; set; }
    }

    public class ConflictResolutionStrategy;
    {
        public ConflictResolutionMethod Method { get; set; } = ConflictResolutionMethod.PriorityBased;
        public Dictionary<string, int> PatternPriorities { get; set; }
            = new Dictionary<string, int>();
        public List<ConflictRule> Rules { get; set; } = new List<ConflictRule>();
        public List<MediationStrategy> MediationStrategies { get; set; }
            = new List<MediationStrategy>();
    }

    public enum ConflictResolutionMethod;
    {
        PriorityBased,
        Voting,
        Negotiation,
        Mediation,
        Compromise,
        Custom;
    }

    public class ConflictRule;
    {
        public string RuleId { get; set; }
        public string ConflictType { get; set; }
        public string Resolution { get; set; }
        public double Effectiveness { get; set; }
    }

    public class MediationStrategy;
    {
        public string StrategyId { get; set; }
        public string Description { get; set; }
        public List<string> ApplicableConflicts { get; set; }
            = new List<string>();
        public List<string> Steps { get; set; } = new List<string>();
        public double SuccessRate { get; set; }
    }

    public class CombinationGoal;
    {
        public string Goal { get; set; }
        public string Metric { get; set; }
        public double Weight { get; set; }
        public double Target { get; set; }
    }

    public class CombinationValidationRule;
    {
        public string RuleId { get; set; }
        public string Condition { get; set; }
        public string ValidationMethod { get; set; }
        public bool IsCritical { get; set; }
    }

    /// <summary>
    /// Birleştirilmiş pattern;
    /// </summary>
    public class CombinedPattern;
    {
        [JsonProperty("combinedPatternId")]
        public string CombinedPatternId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("componentPatterns")]
        public List<SolutionPattern> ComponentPatterns { get; set; }
            = new List<SolutionPattern>();

        [JsonProperty("strategy")]
        public CombinationStrategy Strategy { get; set; }

        [JsonProperty("integratedPattern")]
        public SolutionPattern IntegratedPattern { get; set; }

        [JsonProperty("integrationMetrics")]
        public IntegrationMetrics Metrics { get; set; }

        [JsonProperty("conflictsResolved")]
        public List<ResolvedConflict> ConflictsResolved { get; set; }
            = new List<ResolvedConflict>();

        [JsonProperty("validationResults")]
        public CombinationValidationResults ValidationResults { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class IntegrationMetrics;
    {
        public double CompatibilityScore { get; set; }
        public double IntegrationEfficiency { get; set; }
        public double PatternCohesion { get; set; }
        public double ComplexityIncrease { get; set; }
        public double ExpectedPerformanceGain { get; set; }
    }

    public class ResolvedConflict;
    {
        public string ConflictId { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public string ResolutionMethod { get; set; }
        public string Outcome { get; set; }
        public double ResolutionQuality { get; set; }
    }

    public class CombinationValidationResults;
    {
        public List<ValidationTest> Tests { get; set; } = new List<ValidationTest>();
        public bool IsValid { get; set; }
        public double OverallScore { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
    }

    public class ValidationTest;
    {
        public string TestId { get; set; }
        public string Description { get; set; }
        public bool Passed { get; set; }
        public string Result { get; set; }
        public double Score { get; set; }
    }

    /// <summary>
    /// Bağımlılık analizi;
    /// </summary>
    public class DependencyAnalysis;
    {
        [JsonProperty("analysisId")]
        public string AnalysisId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("solution")]
        public AppliedSolution Solution { get; set; }

        [JsonProperty("dependencies")]
        public List<SolutionDependency> Dependencies { get; set; }
            = new List<SolutionDependency>();

        [JsonProperty("dependencyGraph")]
        public DependencyGraph Graph { get; set; }

        [JsonProperty("criticalPaths")]
        public List<CriticalPath> CriticalPaths { get; set; }
            = new List<CriticalPath>();

        [JsonProperty("vulnerabilityAnalysis")]
        public VulnerabilityAnalysis Vulnerabilities { get; set; }

        [JsonProperty("recommendations")]
        public List<DependencyRecommendation> Recommendations { get; set; }
            = new List<DependencyRecommendation>();

        [JsonProperty("analyzedAt")]
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    }

    public class SolutionDependency;
    {
        public string DependencyId { get; set; }
        public string Type { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string Description { get; set; }
        public double Strength { get; set; }
        public DependencyDirection Direction { get; set; }
        public bool IsCritical { get; set; }
        public List<string> Attributes { get; set; } = new List<string>();
    }

    public enum DependencyDirection;
    {
        Unidirectional,
        Bidirectional,
        Circular;
    }

    public class DependencyGraph;
    {
        public List<GraphNode> Nodes { get; set; } = new List<GraphNode>();
        public List<GraphEdge> Edges { get; set; } = new List<GraphEdge>();
        public Dictionary<string, List<string>> AdjacencyList { get; set; }
            = new Dictionary<string, List<string>>();
        public GraphMetrics Metrics { get; set; }
    }

    public class GraphNode;
    {
        public string NodeId { get; set; }
        public string Type { get; set; }
        public Dictionary<string, object> Properties { get; set; }
            = new Dictionary<string, object>();
        public double Importance { get; set; }
    }

    public class GraphEdge;
    {
        public string EdgeId { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string Type { get; set; }
        public double Weight { get; set; }
        public Dictionary<string, object> Properties { get; set; }
            = new Dictionary<string, object>();
    }

    public class GraphMetrics;
    {
        public int NodeCount { get; set; }
        public int EdgeCount { get; set; }
        public double Density { get; set; }
        public double Diameter { get; set; }
        public double AveragePathLength { get; set; }
        public double ClusteringCoefficient { get; set; }
        public List<CentralityMetrics> Centralities { get; set; }
            = new List<CentralityMetrics>();
    }

    public class CentralityMetrics;
    {
        public string NodeId { get; set; }
        public double DegreeCentrality { get; set; }
        public double BetweennessCentrality { get; set; }
        public double ClosenessCentrality { get; set; }
        public double EigenvectorCentrality { get; set; }
    }

    public class CriticalPath;
    {
        public string PathId { get; set; }
        public List<string> Nodes { get; set; } = new List<string>();
        public double TotalLength { get; set; }
        public double Slack { get; set; }
        public bool IsCritical { get; set; }
        public List<string> Bottlenecks { get; set; } = new List<string>();
    }

    public class VulnerabilityAnalysis;
    {
        public List<DependencyVulnerability> Vulnerabilities { get; set; }
            = new List<DependencyVulnerability>();
        public double OverallRisk { get; set; }
        public List<SinglePointOfFailure> SinglePointsOfFailure { get; set; }
            = new List<SinglePointOfFailure>();
        public List<CascadingFailureRisk> CascadingRisks { get; set; }
            = new List<CascadingFailureRisk>();
    }

    public class DependencyVulnerability;
    {
        public string VulnerabilityId { get; set; }
        public string DependencyId { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public double Severity { get; set; }
        public double Probability { get; set; }
        public List<string> MitigationStrategies { get; set; }
            = new List<string>();
    }

    public class SinglePointOfFailure;
    {
        public string NodeId { get; set; }
        public string Description { get; set; }
        public double Impact { get; set; }
        public List<string> RedundancyOptions { get; set; }
            = new List<string>();
    }

    public class CascadingFailureRisk;
    {
        public string RiskId { get; set; }
        public string Trigger { get; set; }
        public List<string> AffectedNodes { get; set; }
            = new List<string>();
        public double PropagationProbability { get; set; }
        public double TotalImpact { get; set; }
        public List<string> ContainmentStrategies { get; set; }
            = new List<string>();
    }

    public class DependencyRecommendation;
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public double Priority { get; set; }
        public List<string> AffectedDependencies { get; set; }
            = new List<string>();
        public double ExpectedBenefit { get; set; }
    }

    /// <summary>
    /// Ölçeklendirme isteği;
    /// </summary>
    public class ScalingRequest;
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("scalingType")]
        public ScalingType Type { get; set; } = ScalingType.Vertical;

        [JsonProperty("scaleFactor")]
        public double ScaleFactor { get; set; } = 2.0;

        [JsonProperty("targetMetrics")]
        public Dictionary<string, double> TargetMetrics { get; set; }
            = new Dictionary<string, double>();

        [JsonProperty("constraints")]
        public List<ScalingConstraint> Constraints { get; set; }
            = new List<ScalingConstraint>();

        [JsonProperty("scalingStrategy")]
        public ScalingStrategy Strategy { get; set; } = new ScalingStrategy();

        [JsonProperty("validationRequirements")]
        public List<ScalingValidation> ValidationRequirements { get; set; }
            = new List<ScalingValidation>();
    }

    public enum ScalingType;
    {
        Vertical,
        Horizontal,
        Diagonal,
        Functional,
        Geographic;
    }

    public class ScalingConstraint;
    {
        public string ConstraintId { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public object Value { get; set; }
        public bool IsHard { get; set; } = true;
    }

    public class ScalingStrategy;
    {
        public string Approach { get; set; }
        public List<string> Steps { get; set; } = new List<string>();
        public Dictionary<string, object> Parameters { get; set; }
            = new Dictionary<string, object>();
        public TimeSpan EstimatedDuration { get; set; }
        public double RiskLevel { get; set; }
    }

    public class ScalingValidation;
    {
        public string ValidationId { get; set; }
        public string Description { get; set; }
        public string Method { get; set; }
        public Dictionary<string, object> Criteria { get; set; }
            = new Dictionary<string, object>();
        public bool IsCritical { get; set; }
    }

    /// <summary>
    /// Ölçeklendirilmiş çözüm;
    /// </summary>
    public class ScaledSolution;
    {
        [JsonProperty("scalingId")]
        public string ScalingId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("originalSolution")]
        public AppliedSolution OriginalSolution { get; set; }

        [JsonProperty("scaledSolution")]
        public AppliedSolution ScaledSolution { get; set; }

        [JsonProperty("scalingRequest")]
        public ScalingRequest Request { get; set; }

        [JsonProperty("scalingMetrics")]
        public ScalingMetrics Metrics { get; set; }

        [JsonProperty("performanceComparison")]
        public Dictionary<string, PerformanceComparison> PerformanceComparison { get; set; }
            = new Dictionary<string, PerformanceComparison>();

        [JsonProperty("issuesEncountered")]
        public List<ScalingIssue> Issues { get; set; } = new List<ScalingIssue>();

        [JsonProperty("recommendations")]
        public List<ScalingRecommendation> Recommendations { get; set; }
            = new List<ScalingRecommendation>();

        [JsonProperty("scaledAt")]
        public DateTime ScaledAt { get; set; } = DateTime.UtcNow;
    }

    public class ScalingMetrics;
    {
        public double ScalingEfficiency { get; set; }
        public double PerformanceGain { get; set; }
        public double ResourceUtilization { get; set; }
        public double CostEffectiveness { get; set; }
        public double ScalabilityIndex { get; set; }
    }

    public class PerformanceComparison;
    {
        public string Metric { get; set; }
        public double OriginalValue { get; set; }
        public double ScaledValue { get; set; }
        public double Improvement { get; set; }
        public string Unit { get; set; }
        public string Interpretation { get; set; }
    }

    public class ScalingIssue;
    {
        public string IssueId { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public SeverityLevel Severity { get; set; }
        public string Resolution { get; set; }
        public bool Resolved { get; set; }
    }

    public class ScalingRecommendation;
    {
        public string Area { get; set; }
        public string Recommendation { get; set; }
        public double Priority { get; set; }
        public double ExpectedBenefit { get; set; }
        public string Implementation { get; set; }
    }

    /// <summary>
    /// Genelleştirme isteği;
    /// </summary>
    public class GeneralizationRequest;
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("generalizationType")]
        public GeneralizationType Type { get; set; } = GeneralizationType.Domain;

        [JsonProperty("targetDomains")]
        public List<string> TargetDomains { get; set; } = new List<string>();

        [JsonProperty("abstractionLevel")]
        public AbstractionLevel Level { get; set; } = AbstractionLevel.Medium;

        [JsonProperty("constraints")]
        public List<GeneralizationConstraint> Constraints { get; set; }
            = new List<GeneralizationConstraint>();

        [JsonProperty("validationRequirements")]
        public List<GeneralizationValidation> ValidationRequirements { get; set; }
            = new List<GeneralizationValidation>();

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; }
            = new Dictionary<string, object>();
    }

    public enum GeneralizationType;
    {
        Domain,
        Problem,
        Solution,
        Process,
        Hybrid;
    }

    public enum AbstractionLevel;
    {
        Low,
        Medium,
        High,
        VeryHigh;
    }

    public class GeneralizationConstraint;
    {
        public string ConstraintId { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public object Value { get; set; }
        public bool IsHard { get; set; } = true;
    }

    public class GeneralizationValidation;
    {
        public string ValidationId { get; set; }
        public string Description { get; set; }
        public string Method { get; set; }
        public Dictionary<string, object> Criteria { get; set; }
            = new Dictionary<string, object>();
        public bool IsCritical { get; set; }
    }

    /// <summary>
    /// Genelleştirilmiş çözüm;
    /// </summary>
    public class GeneralizedSolution;
    {
        [JsonProperty("generalizationId")]
        public string GeneralizationId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("originalSolution")]
        public AppliedSolution OriginalSolution { get; set; }

        [JsonProperty("generalizedSolution")]
        public AppliedSolution GeneralizedSolution { get; set; }

        [JsonProperty("generalizationRequest")]
        public GeneralizationRequest Request { get; set; }

        [JsonProperty("generalizationMetrics")]
        public GeneralizationMetrics Metrics { get; set; }

        [JsonProperty("applicabilityAnalysis")]
        public ApplicabilityAnalysis Applicability { get; set; }

        [JsonProperty("abstractionsApplied")]
        public List<GeneralizationAbstraction> Abstractions { get; set; }
            = new List<GeneralizationAbstraction>();

        [JsonProperty("validationResults")]
        public GeneralizationValidationResults ValidationResults { get; set; }

        [JsonProperty("generalizedAt")]
        public DateTime GeneralizedAt { get; set; } = DateTime.UtcNow;
    }

    public class GeneralizationMetrics;
    {
        public double GeneralityScore { get; set; }
        public double ApplicabilityRange { get; set; }
        public double AbstractionLevel { get; set; }
        public double PreservationRate { get; set; }
        public double NoveltyScore { get; set; }
    }

    public class ApplicabilityAnalysis;
    {
        public List<ApplicableDomain> Domains { get; set; }
            = new List<ApplicableDomain>();
        public Dictionary<string, double> SimilarityScores { get; set; }
            = new Dictionary<string, double>();
        public List<string> RequiredAdaptations { get; set; }
            = new List<string>();
        public double OverallApplicability { get; set; }
    }

    public class ApplicableDomain;
    {
        public string Domain { get; set; }
        public double ApplicabilityScore { get; set; }
        public List<string> MatchingCharacteristics { get; set; }
            = new List<string>();
        public List<string> RequiredChanges { get; set; }
            = new List<string>();
    }

    public class GeneralizationAbstraction;
    {
        public string AbstractionId { get; set; }
        public string OriginalElement { get; set; }
        public string AbstractedElement { get; set; }
        public string AbstractionRule { get; set; }
        public double Confidence { get; set; }
        public string Impact { get; set; }
    }

    public class GeneralizationValidationResults;
    {
        public List<ValidationTest> Tests { get; set; } = new List<ValidationTest>();
        public bool IsValid { get; set; }
        public double OverallScore { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
    }

    /// <summary>
    /// Analiz parametreleri;
    /// </summary>
    public class AnalysisParameters;
    {
        [JsonProperty("analysisType")]
        public MetaAnalysisType Type { get; set; } = MetaAnalysisType.Performance;

        [JsonProperty("timePeriod")]
        public TimePeriod Period { get; set; }

        [JsonProperty("groupingCriteria")]
        public List<GroupingCriterion> GroupingCriteria { get; set; }
            = new List<GroupingCriterion>();

        [JsonProperty("metrics")]
        public List<AnalysisMetric> Metrics { get; set; }
            = new List<AnalysisMetric>();

        [JsonProperty("statisticalTests")]
        public List<StatisticalTest> StatisticalTests { get; set; }
            = new List<StatisticalTest>();

        [JsonProperty("visualizationOptions")]
        public VisualizationOptions Visualization { get; set; }
            = new VisualizationOptions();
    }

    public enum MetaAnalysisType;
    {
        Performance,
        SuccessRate,
        Efficiency,
        CostEffectiveness,
        Comparative,
        Trend,
        Correlation;
    }

    public class TimePeriod;
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public TimeGranularity Granularity { get; set; } = TimeGranularity.Daily;
    }

    public enum TimeGranularity;
    {
        Hourly,
        Daily,
        Weekly,
        Monthly,
        Quarterly,
        Yearly;
    }

    public class GroupingCriterion;
    {
        public string Field { get; set; }
        public GroupingMethod Method { get; set; } = GroupingMethod.Category;
        public List<string> Categories { get; set; } = new List<string>();
    }

    public enum GroupingMethod;
    {
        Category,
        Range,
        Cluster,
        Percentile;
    }

    public class AnalysisMetric;
    {
        public string Metric { get; set; }
        public string Description { get; set; }
        public AggregationFunction Aggregation { get; set; }
            = AggregationFunction.Average;
        public string Unit { get; set; }
        public bool IncludeTrend { get; set; } = true;
    }

    public enum AggregationFunction;
    {
        Sum,
        Average,
        Median,
        Min,
        Max,
        Count,
        StandardDeviation;
    }

    public class StatisticalTest;
    {
        public string Test { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
            = new Dictionary<string, object>();
        public double SignificanceLevel { get; set; } = 0.05;
    }

    public class VisualizationOptions;
    {
        public List<string> ChartTypes { get; set; } = new List<string>();
        public Dictionary<string, object> Style { get; set; }
            = new Dictionary<string, object>();
        public List<string> Dimensions { get; set; } = new List<string>();
        public List<string> Measures { get; set; } = new List<string>();
    }

    /// <summary>
    /// Meta analiz;
    /// </summary>
    public class MetaAnalysis;
    {
        [JsonProperty("analysisId")]
        public string AnalysisId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("solutions")]
        public List<AppliedSolution> Solutions { get; set; }
            = new List<AppliedSolution>();

        [JsonProperty("parameters")]
        public AnalysisParameters Parameters { get; set; }

        [JsonProperty("results")]
        public AnalysisResults Results { get; set; }

        [JsonProperty("insights")]
        public List<Insight> Insights { get; set; } = new List<Insight>();

        [JsonProperty("recommendations")]
        public List<MetaRecommendation> Recommendations { get; set; }
            = new List<MetaRecommendation>();

        [JsonProperty("visualizations")]
        public Dictionary<string, object> Visualizations { get; set; }
            = new Dictionary<string, object>();

        [JsonProperty("analyzedAt")]
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    }

    public class AnalysisResults;
    {
        public Dictionary<string, object> SummaryStatistics { get; set; }
            = new Dictionary<string, object>();
        public Dictionary<string, TrendAnalysis> Trends { get; set; }
            = new Dictionary<string, TrendAnalysis>();
        public Dictionary<string, CorrelationAnalysis> Correlations { get; set; }
            = new Dictionary<string, CorrelationAnalysis>();
        public List<StatisticalTestResult> StatisticalTestResults { get; set; }
            = new List<StatisticalTestResult>();
        public List<ClusterAnalysis> Clusters { get; set; }
            = new List<ClusterAnalysis>();
    }

    public class TrendAnalysis;
    {
        public string Metric { get; set; }
        public List<TimePoint> TimeSeries { get; set; }
            = new List<TimePoint>();
        public double TrendSlope { get; set; }
        public double R2 { get; set; }
        public bool IsSignificant { get; set; }
        public string TrendType { get; set; }
    }

    public class TimePoint;
    {
        public DateTime Time { get; set; }
        public double Value { get; set; }
        public int Count { get; set; }
    }

    public class CorrelationAnalysis;
    {
        public string Variable1 { get; set; }
        public string Variable2 { get; set; }
        public double CorrelationCoefficient { get; set; }
        public double PValue { get; set; }
        public bool IsSignificant { get; set; }
        public string Relationship { get; set; }
    }

    public class StatisticalTestResult;
    {
        public string Test { get; set; }
        public string Hypothesis { get; set; }
        public double TestStatistic { get; set; }
        public double PValue { get; set; }
        public bool IsSignificant { get; set; }
        public string Conclusion { get; set; }
    }

    public class ClusterAnalysis;
    {
        public int ClusterId { get; set; }
        public List<string> SolutionIds { get; set; } = new List<string>();
        public Dictionary<string, double> Characteristics { get; set; }
            = new Dictionary<string, double>();
        public string Label { get; set; }
        public double Size { get; set; }
    }

    public class Insight;
    {
        public string InsightId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public double Confidence { get; set; }
        public List<string> Evidence { get; set; } = new List<string>();
        public List<string> Implications { get; set; } = new List<string>();
    }

    public class MetaRecommendation;
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public double Priority { get; set; }
        public List<string> AffectedAreas { get; set; }
            = new List<string>();
        public double ExpectedImpact { get; set; }
        public string Implementation { get; set; }
    }

    /// <summary>
    /// Güncelleme stratejisi;
    /// </summary>
    public class UpdateStrategy;
    {
        [JsonProperty("updateType")]
        public UpdateType Type { get; set; } = UpdateType.Incremental;

        [JsonProperty("validationRequired")]
        public bool ValidationRequired { get; set; } = true;

        [JsonProperty("backupRequired")]
        public bool BackupRequired { get; set; } = true;

        [JsonProperty("rollbackPlan")]
        public RollbackStrategy RollbackPlan { get; set; }

        [JsonProperty("notificationSettings")]
        public NotificationSettings Notifications { get; set; }
            = new NotificationSettings();

        [JsonProperty("updateSchedule")]
        public UpdateSchedule Schedule { get; set; }
    }

    public enum UpdateType;
    {
        Full,
        Incremental,
        Partial,
        Emergency,
        Scheduled;
    }

    public class NotificationSettings;
    {
        public List<string> Recipients { get; set; } = new List<string>();
        public string Channel { get; set; }
        public string Format { get; set; }
        public List<string> Events { get; set; } = new List<string>();
    }

    public class UpdateSchedule;
    {
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public string Timezone { get; set; }
    }

    /// <summary>
    /// Çözüm istatistikleri;
    /// </summary>
    public class ResolutionStatistics;
    {
        [JsonProperty("statisticsId")]
        public string StatisticsId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("timePeriod")]
        public TimePeriod Period { get; set; }

        [JsonProperty("overallStats")]
        public OverallStatistics Overall { get; set; }

        [JsonProperty("patternStats")]
        public Dictionary<string, PatternStatistics> PatternStatistics { get; set; }
            = new Dictionary<string, PatternStatistics>();

        [JsonProperty("categoryStats")]
        public Dictionary<string, CategoryStatistics> CategoryStatistics { get; set; }
            = new Dictionary<string, CategoryStatistics>();

        [JsonProperty("trends")]
        public List<StatisticsTrend> Trends { get; set; }
            = new List<StatisticsTrend>();

        [JsonProperty("performanceDistribution")]
        public PerformanceDistribution Distribution { get; set; }

        [JsonProperty("generatedAt")]
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }

    public class OverallStatistics;
    {
        public int TotalProblems { get; set; }
        public int TotalSolutions { get; set; }
        public double AverageSuccessRate { get; set; }
        public double AverageExecutionTime { get; set; }
        public double AverageCost { get; set; }
        public double AverageComplexity { get; set; }
        public Dictionary<string, int> StatusDistribution { get; set; }
            = new Dictionary<string, int>();
    }

    public class PatternStatistics;
    {
        public string PatternId { get; set; }
        public int UsageCount { get; set; }
        public double SuccessRate { get; set; }
        public double AverageScore { get; set; }
        public double AverageExecutionTime { get; set; }
        public Dictionary<string, int> ProblemTypeDistribution { get; set; }
            = new Dictionary<string, int>();
        public List<PerformanceOverTime> PerformanceOverTime { get; set; }
            = new List<PerformanceOverTime>();
    }

    public class PerformanceOverTime;
    {
        public DateTime Period { get; set; }
        public double SuccessRate { get; set; }
        public int UsageCount { get; set; }
        public double AverageScore { get; set; }
    }

    public class CategoryStatistics;
    {
        public string Category { get; set; }
        public int ProblemCount { get; set; }
        public int SolutionCount { get; set; }
        public double AverageSuccessRate { get; set; }
        public double AverageComplexity { get; set; }
        public Dictionary<string, int> PatternDistribution { get; set; }
            = new Dictionary<string, int>();
    }

    public class StatisticsTrend;
    {
        public string Metric { get; set; }
        public List<TrendPoint> DataPoints { get; set; }
            = new List<TrendPoint>();
        public double Trend { get; set; }
        public string Direction { get; set; }
    }

    public class TrendPoint;
    {
        public DateTime Time { get; set; }
        public double Value { get; set; }
    }

    public class PerformanceDistribution;
    {
        public Dictionary<string, Histogram> Histograms { get; set; }
            = new Dictionary<string, Histogram>();
        public Dictionary<string, PercentileData> Percentiles { get; set; }
            = new Dictionary<string, PercentileData>();
        public Dictionary<string, double> Outliers { get; set; }
            = new Dictionary<string, double>();
    }

    public class Histogram;
    {
        public string Metric { get; set; }
        public List<Bin> Bins { get; set; } = new List<Bin>();
        public double Min { get; set; }
        public double Max { get; set; }
        public int BinCount { get; set; }
    }

    public class Bin;
    {
        public double LowerBound { get; set; }
        public double UpperBound { get; set; }
        public int Count { get; set; }
        public double Frequency { get; set; }
    }

    public class PercentileData;
    {
        public double P25 { get; set; }
        public double P50 { get; set; }
        public double P75 { get; set; }
        public double P90 { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
    }

    /// <summary>
    /// Dışa aktarma formatı;
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Xml,
        Csv,
        Excel,
        Pdf,
        Custom;
    }

    /// <summary>
    /// Çözüm dışa aktarma;
    /// </summary>
    public class SolutionExport;
    {
        [JsonProperty("exportId")]
        public string ExportId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("format")]
        public ExportFormat Format { get; set; }

        [JsonProperty("content")]
        public object Content { get; set; }

        [JsonProperty("metadata")]
        public ExportMetadata Metadata { get; set; }

        [JsonProperty("validation")]
        public ExportValidation Validation { get; set; }

        [JsonProperty("exportedAt")]
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    }

    public class ExportMetadata;
    {
        public int SolutionCount { get; set; }
        public int PatternCount { get; set; }
        public DateTime ExportStart { get; set; }
        public DateTime ExportEnd { get; set; }
        public string Version { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; }
            = new Dictionary<string, object>();
    }

    public class ExportValidation;
    {
        public bool IsValid { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        public string Checksum { get; set; }
        public string IntegrityCheck { get; set; }
    }

    /// <summary>
    /// Çözüm içe aktarma;
    /// </summary>
    public class SolutionImport;
    {
        [JsonProperty("importId")]
        public string ImportId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("format")]
        public ExportFormat Format { get; set; }

        [JsonProperty("content")]
        public object Content { get; set; }

        [JsonProperty("validationRules")]
        public List<ImportValidationRule> ValidationRules { get; set; }
            = new List<ImportValidationRule>();

        [JsonProperty("conflictResolution")]
        public ImportConflictResolution ConflictResolution { get; set; }
            = new ImportConflictResolution();

        [JsonProperty("metadata")]
        public ImportMetadata Metadata { get; set; }
    }

    public class ImportValidationRule;
    {
        public string RuleId { get; set; }
        public string Condition { get; set; }
        public string Message { get; set; }
        public SeverityLevel Severity { get; set; }
        public bool IsCritical { get; set; }
    }

    public class ImportConflictResolution;
    {
        public ConflictResolutionStrategy Strategy { get; set; }
            = new ConflictResolutionStrategy();
        public List<ConflictRule> Rules { get; set; } = new List<ConflictRule>();
        public List<string> BackupActions { get; set; } = new List<string>();
    }

    public class ImportMetadata;
    {
        public string Source { get; set; }
        public DateTime ImportDate { get; set; }
        public string Version { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; }
            = new Dictionary<string, object>();
    }

    /// <summary>
    /// Past Problem (Geçmiş Problem)
    /// </summary>
    public class PastProblem;
    {
        public string ProblemId { get; set; }
        public string Title { get; set; }
        public DateTime OccurredAt { get; set; }
        public string Category { get; set; }
        public double Complexity { get; set; }
    }

    /// <summary>
    /// Previous Solution (Önceki Çözüm)
    /// </summary>
    public class PreviousSolution;
    {
        public string SolutionId { get; set; }
        public string ProblemId { get; set; }
        public string PatternUsed { get; set; }
        public double SuccessRate { get; set; }
        public DateTime AppliedAt { get; set; }
    }

    /// <summary>
    /// Historical Pattern (Tarihsel Pattern)
    /// </summary>
    public class HistoricalPattern;
    {
        public string PatternId { get; set; }
        public string Name { get; set; }
        public double HistoricalSuccessRate { get; set; }
        public int UsageCount { get; set; }
        public DateTime FirstUsed { get; set; }
        public DateTime LastUsed { get; set; }
    }
}
