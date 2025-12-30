// NEDA.Brain/DecisionMaking/RiskAssessment/ImpactAssessor.cs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.Versioning;
using NEDA.Automation.ScenarioPlanner;
using NEDA.Brain.DecisionMaking.EthicalChecker;
using NEDA.Brain.DecisionMaking.RiskAssessment;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.NeuralNetwork;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Communication.EmotionalIntelligence;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.MetricsCollector;
using NEDA.SecurityModules.AdvancedSecurity.Authentication;
using NEDA.SecurityModules.Monitoring;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Brain.DecisionMaking.RiskAssessment;
{
    /// <summary>
    /// NEDA sisteminin etki değerlendirme ve sonuç analizi motoru.
    /// Kararların, eylemlerin ve değişikliklerin potansiyel etkilerini analiz eder ve değerlendirir.
    /// </summary>
    public class ImpactAssessor : IImpactAssessor, IDisposable;
    {
        private readonly ILogger<ImpactAssessor> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IKnowledgeBase _knowledgeBase;
        private readonly IImpactPredictionModel _predictionModel;
        private readonly IRiskAnalyzer _riskAnalyzer;
        private readonly IEmotionDetector _emotionDetector;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IMetricsCollector _metricsCollector;
        private readonly ISecurityMonitor _securityMonitor;
        private readonly IMemoryRecall _memoryRecall;

        // Impact assessment models and caches;
        private readonly Dictionary<string, ImpactModel> _impactModels;
        private readonly Dictionary<string, ImpactAssessmentResult> _assessmentCache;
        private readonly Dictionary<string, List<HistoricalImpact>> _impactHistory;

        // Configuration;
        private ImpactAssessorConfig _config;
        private readonly ImpactAssessorOptions _options;
        private bool _isInitialized;
        private readonly object _lockObject = new object();
        private readonly SemaphoreSlim _assessmentSemaphore;

        // Impact prediction models;
        private readonly Dictionary<ImpactDomain, IImpactPredictionModel> _domainModels;

        // Events;
        public event EventHandler<ImpactAssessmentStartedEventArgs> OnAssessmentStarted;
        public event EventHandler<ImpactAssessmentProgressEventArgs> OnAssessmentProgress;
        public event EventHandler<ImpactAssessmentCompletedEventArgs> OnAssessmentCompleted;
        public event EventHandler<CriticalImpactDetectedEventArgs> OnCriticalImpactDetected;
        public event EventHandler<ImpactPredictionUpdatedEventArgs> OnImpactPredictionUpdated;

        /// <summary>
        /// ImpactAssessor constructor;
        /// </summary>
        public ImpactAssessor(
            ILogger<ImpactAssessor> logger,
            IServiceProvider serviceProvider,
            IKnowledgeBase knowledgeBase,
            IImpactPredictionModel predictionModel,
            IRiskAnalyzer riskAnalyzer,
            IEmotionDetector emotionDetector,
            IDiagnosticTool diagnosticTool,
            IMetricsCollector metricsCollector,
            ISecurityMonitor securityMonitor,
            IMemoryRecall memoryRecall,
            IOptions<ImpactAssessorOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _predictionModel = predictionModel ?? throw new ArgumentNullException(nameof(predictionModel));
            _riskAnalyzer = riskAnalyzer ?? throw new ArgumentNullException(nameof(riskAnalyzer));
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _memoryRecall = memoryRecall ?? throw new ArgumentNullException(nameof(memoryRecall));
            _options = options?.Value ?? new ImpactAssessorOptions();

            // Initialize storage;
            _impactModels = new Dictionary<string, ImpactModel>();
            _assessmentCache = new Dictionary<string, ImpactAssessmentResult>();
            _impactHistory = new Dictionary<string, List<HistoricalImpact>>();

            // Default configuration;
            _config = new ImpactAssessorConfig();
            _isInitialized = false;

            // Initialize domain-specific models;
            _domainModels = InitializeDomainModels();

            // Concurrency control;
            _assessmentSemaphore = new SemaphoreSlim(
                _config.MaxConcurrentAssessments,
                _config.MaxConcurrentAssessments);

            _logger.LogInformation("ImpactAssessor initialized successfully");
        }

        /// <summary>
        /// Domain-specific modelleri başlatır;
        /// </summary>
        private Dictionary<ImpactDomain, IImpactPredictionModel> InitializeDomainModels()
        {
            return new Dictionary<ImpactDomain, IImpactPredictionModel>
            {
                [ImpactDomain.Technical] = new TechnicalImpactModel(),
                [ImpactDomain.Operational] = new OperationalImpactModel(),
                [ImpactDomain.Financial] = new FinancialImpactModel(),
                [ImpactDomain.Security] = new SecurityImpactModel(),
                [ImpactDomain.Reputational] = new ReputationalImpactModel(),
                [ImpactDomain.Legal] = new LegalImpactModel(),
                [ImpactDomain.Environmental] = new EnvironmentalImpactModel(),
                [ImpactDomain.Social] = new SocialImpactModel(),
                [ImpactDomain.Human] = new HumanImpactModel(),
                [ImpactDomain.Strategic] = new StrategicImpactModel()
            };
        }

        /// <summary>
        /// ImpactAssessor'ı belirtilen konfigürasyon ile başlatır;
        /// </summary>
        public async Task InitializeAsync(ImpactAssessorConfig config = null)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("ImpactAssessor already initialized");
                    return;
                }

                _logger.LogInformation("Initializing ImpactAssessor...");

                _config = config ?? new ImpactAssessorConfig();

                // Load impact assessment models;
                await LoadImpactModelsAsync();

                // Load historical impact data;
                await LoadHistoricalImpactDataAsync();

                // Initialize prediction model;
                await _predictionModel.InitializeAsync();

                // Initialize domain models;
                await InitializeDomainModelsAsync();

                // Initialize risk analyzer;
                await _riskAnalyzer.InitializeAsync();

                // Load impact assessment rules;
                await LoadAssessmentRulesAsync();

                // Warm up prediction models;
                await WarmUpModelsAsync();

                _isInitialized = true;

                _logger.LogInformation("ImpactAssessor initialized successfully with {ModelCount} models",
                    _impactModels.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ImpactAssessor");
                throw new ImpactAssessorException("ImpactAssessor initialization failed", ex);
            }
        }

        /// <summary>
        /// Etki modellerini yükler;
        /// </summary>
        private async Task LoadImpactModelsAsync()
        {
            try
            {
                // Load default impact models;
                var defaultModels = GetDefaultImpactModels();
                foreach (var model in defaultModels)
                {
                    _impactModels[model.Id] = model;
                }

                // Load models from knowledge base;
                var kbModels = await _knowledgeBase.GetImpactModelsAsync();
                foreach (var model in kbModels)
                {
                    if (!_impactModels.ContainsKey(model.Id))
                    {
                        _impactModels[model.Id] = model;
                    }
                }

                // Load learned models from memory;
                var learnedModels = await _memoryRecall.GetImpactModelsAsync();
                foreach (var model in learnedModels)
                {
                    var modelId = $"LEARNED_{model.Id}";
                    _impactModels[modelId] = model;
                }

                _logger.LogDebug("Loaded {ModelCount} impact models", _impactModels.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load impact models");
                throw;
            }
        }

        /// <summary>
        /// Varsayılan etki modellerini döndürür;
        /// </summary>
        private IEnumerable<ImpactModel> GetDefaultImpactModels()
        {
            return new List<ImpactModel>
            {
                new ImpactModel;
                {
                    Id = "MODEL_SYSTEM_PERFORMANCE_IMPACT",
                    Name = "System Performance Impact Model",
                    Description = "Assesses impact on system performance, latency, throughput, and resource utilization",
                    Domain = ImpactDomain.Technical,
                    ImpactTypes = new List<ImpactType> { ImpactType.Performance, ImpactType.Availability, ImpactType.Scalability },
                    Metrics = new List<string> { "Response_Time", "Throughput", "CPU_Usage", "Memory_Usage", "Error_Rate" },
                    PredictionHorizon = TimeSpan.FromHours(24),
                    ConfidenceThreshold = 0.7,
                    Weight = 0.25,
                    IsActive = true;
                },
                new ImpactModel;
                {
                    Id = "MODEL_SECURITY_IMPACT",
                    Name = "Security Impact Model",
                    Description = "Assesses security implications including vulnerabilities, threats, and compliance",
                    Domain = ImpactDomain.Security,
                    ImpactTypes = new List<ImpactType> { ImpactType.Security, ImpactType.Compliance, ImpactType.Privacy },
                    Metrics = new List<string> { "Vulnerability_Count", "Threat_Level", "Compliance_Score", "Data_Exposure_Risk" },
                    PredictionHorizon = TimeSpan.FromDays(7),
                    ConfidenceThreshold = 0.8,
                    Weight = 0.2,
                    IsActive = true;
                },
                new ImpactModel;
                {
                    Id = "MODEL_FINANCIAL_IMPACT",
                    Name = "Financial Impact Model",
                    Description = "Assesses financial implications including costs, revenue, and ROI",
                    Domain = ImpactDomain.Financial,
                    ImpactTypes = new List<ImpactType> { ImpactType.Cost, ImpactType.Revenue, ImpactType.ROI, ImpactType.Investment },
                    Metrics = new List<string> { "Implementation_Cost", "Operational_Cost", "Revenue_Impact", "ROI", "Payback_Period" },
                    PredictionHorizon = TimeSpan.FromDays(30),
                    ConfidenceThreshold = 0.75,
                    Weight = 0.15,
                    IsActive = true;
                },
                new ImpactModel;
                {
                    Id = "MODEL_OPERATIONAL_IMPACT",
                    Name = "Operational Impact Model",
                    Description = "Assesses impact on business operations, workflows, and processes",
                    Domain = ImpactDomain.Operational,
                    ImpactTypes = new List<ImpactType> { ImpactType.Operational, ImpactType.Efficiency, ImpactType.Productivity },
                    Metrics = new List<string> { "Process_Efficiency", "Workflow_Disruption", "Staff_Productivity", "Service_Quality" },
                    PredictionHorizon = TimeSpan.FromHours(12),
                    ConfidenceThreshold = 0.65,
                    Weight = 0.15,
                    IsActive = true;
                },
                new ImpactModel;
                {
                    Id = "MODEL_REPUTATIONAL_IMPACT",
                    Name = "Reputational Impact Model",
                    Description = "Assesses impact on brand reputation, customer trust, and public perception",
                    Domain = ImpactDomain.Reputational,
                    ImpactTypes = new List<ImpactType> { ImpactType.Reputational, ImpactType.Brand, ImpactType.Customer_Satisfaction },
                    Metrics = new List<string> { "Brand_Perception", "Customer_Satisfaction", "Social_Sentiment", "Media_Coverage" },
                    PredictionHorizon = TimeSpan.FromDays(14),
                    ConfidenceThreshold = 0.6,
                    Weight = 0.1,
                    IsActive = true;
                },
                new ImpactModel;
                {
                    Id = "MODEL_HUMAN_IMPACT",
                    Name = "Human Impact Model",
                    Description = "Assesses impact on employees, stakeholders, and end-users",
                    Domain = ImpactDomain.Human,
                    ImpactTypes = new List<ImpactType> { ImpactType.Human, ImpactType.Stakeholder, ImpactType.User_Experience },
                    Metrics = new List<string> { "Employee_Satisfaction", "Stakeholder_Approval", "User_Experience", "Training_Requirements" },
                    PredictionHorizon = TimeSpan.FromDays(7),
                    ConfidenceThreshold = 0.7,
                    Weight = 0.1,
                    IsActive = true;
                },
                new ImpactModel;
                {
                    Id = "MODEL_STRATEGIC_IMPACT",
                    Name = "Strategic Impact Model",
                    Description = "Assesses long-term strategic implications and alignment with goals",
                    Domain = ImpactDomain.Strategic,
                    ImpactTypes = new List<ImpactType> { ImpactType.Strategic, ImpactType.Competitive, ImpactType.Innovation },
                    Metrics = new List<string> { "Strategic_Alignment", "Competitive_Advantage", "Innovation_Potential", "Market_Position" },
                    PredictionHorizon = TimeSpan.FromDays(90),
                    ConfidenceThreshold = 0.6,
                    Weight = 0.05,
                    IsActive = true;
                }
            };
        }

        /// <summary>
        /// Tarihsel etki verilerini yükler;
        /// </summary>
        private async Task LoadHistoricalImpactDataAsync()
        {
            try
            {
                var historicalData = await _knowledgeBase.GetHistoricalImpactsAsync();

                foreach (var impact in historicalData)
                {
                    var key = $"{impact.Domain}_{impact.SourceSystem}";
                    if (!_impactHistory.ContainsKey(key))
                    {
                        _impactHistory[key] = new List<HistoricalImpact>();
                    }
                    _impactHistory[key].Add(impact);
                }

                _logger.LogDebug("Loaded {ImpactCount} historical impact records", historicalData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load historical impact data");
                // Non-critical, continue without historical data;
            }
        }

        /// <summary>
        /// Domain modellerini başlatır;
        /// </summary>
        private async Task InitializeDomainModelsAsync()
        {
            foreach (var model in _domainModels.Values)
            {
                await model.InitializeAsync();
            }
        }

        /// <summary>
        /// Değerlendirme kurallarını yükler;
        /// </summary>
        private async Task LoadAssessmentRulesAsync()
        {
            try
            {
                var assessmentRules = new List<ImpactAssessmentRule>
                {
                    new ImpactAssessmentRule;
                    {
                        Id = "RULE_CRITICAL_IMPACT_001",
                        Name = "Critical Security Impact",
                        Description = "Triggers when security impact score exceeds critical threshold",
                        Condition = "Security_Impact_Score > 0.8 AND Confidence > 0.7",
                        Action = "TriggerSecurityAlert AND EscalateToSecurityTeam",
                        Priority = AssessmentPriority.Critical,
                        NotificationChannels = new List<string> { "SecurityTeam", "Management", "Audit" }
                    },
                    new ImpactAssessmentRule;
                    {
                        Id = "RULE_HIGH_FINANCIAL_IMPACT",
                        Name = "High Financial Impact",
                        Description = "Triggers when financial impact exceeds budget threshold",
                        Condition = "Financial_Impact_Score > 0.6 AND Cost_Impact > Budget_Threshold",
                        Action = "RequireFinancialApproval AND NotifyStakeholders",
                        Priority = AssessmentPriority.High,
                        NotificationChannels = new List<string> { "Finance", "Management", "Stakeholders" }
                    },
                    new ImpactAssessmentRule;
                    {
                        Id = "RULE_SYSTEM_DISRUPTION",
                        Name = "System Disruption Impact",
                        Description = "Triggers when system availability impact is significant",
                        Condition = "Availability_Impact > 0.7 AND Duration > 1 hour",
                        Action = "ScheduleMaintenanceWindow AND NotifyUsers",
                        Priority = AssessmentPriority.High,
                        NotificationChannels = new List<string> { "Operations", "Support", "Users" }
                    }
                };

                foreach (var rule in assessmentRules)
                {
                    await _knowledgeBase.StoreImpactAssessmentRuleAsync(rule);
                }

                _logger.LogDebug("Loaded {RuleCount} impact assessment rules", assessmentRules.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load assessment rules");
                throw;
            }
        }

        /// <summary>
        /// Modelleri ısıtır;
        /// </summary>
        private async Task WarmUpModelsAsync()
        {
            try
            {
                _logger.LogDebug("Warming up impact assessment models...");

                // Warm up with common scenarios;
                var warmupTasks = new List<Task>
                {
                    WarmUpTechnicalModelAsync(),
                    WarmUpSecurityModelAsync(),
                    WarmUpFinancialModelAsync()
                };

                await Task.WhenAll(warmupTasks);

                _logger.LogDebug("Impact assessment models warmed up successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to warm up models");
                // Non-critical, continue;
            }
        }

        /// <summary>
        /// Etki değerlendirmesi yapar;
        /// </summary>
        public async Task<ImpactAssessmentResult> AssessImpactAsync(
            ImpactAssessmentRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                // Check cache first;
                var cacheKey = GenerateCacheKey(request);
                if (_config.EnableCaching && _assessmentCache.TryGetValue(cacheKey, out var cachedResult))
                {
                    if (cachedResult.Timestamp.Add(_config.CacheDuration) > DateTime.UtcNow)
                    {
                        _logger.LogDebug("Returning cached impact assessment for request: {RequestId}", request.Id);
                        return cachedResult;
                    }
                }

                // Acquire semaphore for concurrency control;
                await _assessmentSemaphore.WaitAsync(cancellationToken);

                try
                {
                    var assessmentId = Guid.NewGuid().ToString();

                    // Event: Assessment started;
                    OnAssessmentStarted?.Invoke(this, new ImpactAssessmentStartedEventArgs;
                    {
                        AssessmentId = assessmentId,
                        Request = request,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Starting impact assessment {AssessmentId} for request: {RequestId}",
                        assessmentId, request.Id);

                    var startTime = DateTime.UtcNow;

                    // Progress report;
                    await ReportProgressAsync(assessmentId, 0, "Initializing assessment", request.Context);

                    // 1. Analyze the action/decision;
                    var actionAnalysis = await AnalyzeActionAsync(request.Action, request.Context, cancellationToken);
                    await ReportProgressAsync(assessmentId, 10, "Action analysis completed", request.Context);

                    // 2. Identify affected domains;
                    var affectedDomains = await IdentifyAffectedDomainsAsync(actionAnalysis, request.Context, cancellationToken);
                    await ReportProgressAsync(assessmentId, 20, "Affected domains identified", request.Context);

                    // 3. Collect relevant data;
                    var assessmentData = await CollectAssessmentDataAsync(
                        affectedDomains,
                        request.Context,
                        cancellationToken);
                    await ReportProgressAsync(assessmentId, 40, "Assessment data collected", request.Context);

                    // 4. Apply impact models;
                    var modelResults = await ApplyImpactModelsAsync(
                        affectedDomains,
                        actionAnalysis,
                        assessmentData,
                        cancellationToken);
                    await ReportProgressAsync(assessmentId, 60, "Impact models applied", request.Context);

                    // 5. Predict impacts;
                    var impactPredictions = await PredictImpactsAsync(
                        modelResults,
                        request.PredictionHorizon,
                        cancellationToken);
                    await ReportProgressAsync(assessmentId, 70, "Impact predictions generated", request.Context);

                    // 6. Assess risks;
                    var riskAssessment = await AssessRisksAsync(
                        impactPredictions,
                        request.Context,
                        cancellationToken);
                    await ReportProgressAsync(assessmentId, 80, "Risk assessment completed", request.Context);

                    // 7. Evaluate severity;
                    var severityEvaluation = await EvaluateSeverityAsync(
                        impactPredictions,
                        riskAssessment,
                        cancellationToken);
                    await ReportProgressAsync(assessmentId, 90, "Severity evaluation completed", request.Context);

                    // 8. Generate mitigation strategies;
                    var mitigationStrategies = await GenerateMitigationStrategiesAsync(
                        impactPredictions,
                        riskAssessment,
                        severityEvaluation,
                        cancellationToken);

                    // 9. Build final result;
                    var result = await BuildAssessmentResultAsync(
                        assessmentId,
                        request,
                        actionAnalysis,
                        affectedDomains,
                        modelResults,
                        impactPredictions,
                        riskAssessment,
                        severityEvaluation,
                        mitigationStrategies,
                        startTime,
                        cancellationToken);

                    // Update cache;
                    if (_config.EnableCaching)
                    {
                        lock (_lockObject)
                        {
                            _assessmentCache[cacheKey] = result;
                            CleanupExpiredCacheEntries();
                        }
                    }

                    // Record in history;
                    await RecordAssessmentHistoryAsync(result, cancellationToken);

                    // Check for critical impacts;
                    await CheckCriticalImpactsAsync(result, cancellationToken);

                    // Event: Assessment completed;
                    OnAssessmentCompleted?.Invoke(this, new ImpactAssessmentCompletedEventArgs;
                    {
                        AssessmentId = assessmentId,
                        Request = request,
                        Result = result,
                        Timestamp = DateTime.UtcNow;
                    });

                    await ReportProgressAsync(assessmentId, 100, "Impact assessment completed", request.Context);

                    _logger.LogInformation(
                        "Completed impact assessment {AssessmentId} in {AssessmentTime}ms. Overall impact score: {ImpactScore}",
                        assessmentId, result.AssessmentTime.TotalMilliseconds, result.OverallImpactScore);

                    return result;
                }
                finally
                {
                    _assessmentSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Impact assessment cancelled for request: {RequestId}", request.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in impact assessment for request: {RequestId}", request.Id);
                throw new ImpactAssessmentException($"Impact assessment failed for request: {request.Id}", ex);
            }
        }

        /// <summary>
        /// Eylemi analiz eder;
        /// </summary>
        private async Task<ActionAnalysis> AnalyzeActionAsync(
            ActionDescription action,
            AssessmentContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                var analysis = new ActionAnalysis;
                {
                    ActionId = action.Id,
                    ActionType = action.Type,
                    Description = action.Description,
                    Scope = DetermineActionScope(action),
                    Complexity = EstimateActionComplexity(action),
                    Dependencies = await IdentifyActionDependenciesAsync(action, context, cancellationToken),
                    ResourcesRequired = await IdentifyRequiredResourcesAsync(action, context, cancellationToken),
                    PotentialConflicts = await IdentifyPotentialConflictsAsync(action, context, cancellationToken),
                    Timeline = EstimateActionTimeline(action),
                    SuccessCriteria = action.SuccessCriteria ?? new List<string>()
                };

                // Analyze action characteristics;
                analysis.Characteristics = await AnalyzeActionCharacteristicsAsync(action, context, cancellationToken);

                // Determine action category;
                analysis.Category = DetermineActionCategory(action, analysis.Characteristics);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing action");
                throw;
            }
        }

        /// <summary>
        /// Eylem kapsamını belirler;
        /// </summary>
        private ActionScope DetermineActionScope(ActionDescription action)
        {
            // Simplified scope determination;
            if (action.Parameters?.ContainsKey("system_wide") == true &&
                (bool)action.Parameters["system_wide"])
                return ActionScope.SystemWide;

            if (action.Parameters?.ContainsKey("department") == true)
                return ActionScope.Departmental;

            if (action.Parameters?.ContainsKey("team") == true)
                return ActionScope.Team;

            if (action.Parameters?.ContainsKey("individual") == true)
                return ActionScope.Individual;

            return ActionScope.Unknown;
        }

        /// <summary>
        /// Etkilenen domain'leri belirler;
        /// </summary>
        private async Task<List<ImpactDomain>> IdentifyAffectedDomainsAsync(
            ActionAnalysis actionAnalysis,
            AssessmentContext context,
            CancellationToken cancellationToken)
        {
            var affectedDomains = new List<ImpactDomain>();

            try
            {
                // Based on action type;
                switch (actionAnalysis.ActionType)
                {
                    case ActionType.SystemChange:
                    case ActionType.Deployment:
                    case ActionType.Configuration:
                        affectedDomains.Add(ImpactDomain.Technical);
                        affectedDomains.Add(ImpactDomain.Operational);
                        affectedDomains.Add(ImpactDomain.Security);
                        break;

                    case ActionType.PolicyChange:
                    case ActionType.ProcessChange:
                        affectedDomains.Add(ImpactDomain.Operational);
                        affectedDomains.Add(ImpactDomain.Human);
                        affectedDomains.Add(ImpactDomain.Legal);
                        break;

                    case ActionType.FinancialDecision:
                    case ActionType.Investment:
                        affectedDomains.Add(ImpactDomain.Financial);
                        affectedDomains.Add(ImpactDomain.Strategic);
                        break;

                    case ActionType.SecurityUpdate:
                    case ActionType.AccessChange:
                        affectedDomains.Add(ImpactDomain.Security);
                        affectedDomains.Add(ImpactDomain.Compliance);
                        break;
                }

                // Based on action scope;
                switch (actionAnalysis.Scope)
                {
                    case ActionScope.SystemWide:
                        affectedDomains.Add(ImpactDomain.Strategic);
                        affectedDomains.Add(ImpactDomain.Reputational);
                        break;

                    case ActionScope.Departmental:
                        affectedDomains.Add(ImpactDomain.Operational);
                        break;
                }

                // Based on action characteristics;
                if (actionAnalysis.Characteristics.Contains("high_risk"))
                    affectedDomains.Add(ImpactDomain.Security);

                if (actionAnalysis.Characteristics.Contains("customer_facing"))
                    affectedDomains.Add(ImpactDomain.Reputational);

                if (actionAnalysis.Characteristics.Contains("regulatory"))
                    affectedDomains.Add(ImpactDomain.Legal);

                if (actionAnalysis.Characteristics.Contains("environmental"))
                    affectedDomains.Add(ImpactDomain.Environmental);

                // Remove duplicates and return;
                return affectedDomains.Distinct().ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error identifying affected domains");
                return new List<ImpactDomain> { ImpactDomain.Technical, ImpactDomain.Operational };
            }
        }

        /// <summary>
        /// Değerlendirme verilerini toplar;
        /// </summary>
        private async Task<AssessmentData> CollectAssessmentDataAsync(
            List<ImpactDomain> domains,
            AssessmentContext context,
            CancellationToken cancellationToken)
        {
            var data = new AssessmentData;
            {
                Timestamp = DateTime.UtcNow,
                DomainData = new Dictionary<ImpactDomain, DomainAssessmentData>(),
                SystemMetrics = new Dictionary<string, MetricValue>(),
                StakeholderInfo = new List<Stakeholder>(),
                Constraints = new List<Constraint>()
            };

            try
            {
                // Collect data for each domain;
                foreach (var domain in domains)
                {
                    var domainData = await CollectDomainDataAsync(domain, context, cancellationToken);
                    data.DomainData[domain] = domainData;
                }

                // Collect system metrics;
                data.SystemMetrics = await CollectSystemMetricsAsync(cancellationToken);

                // Collect stakeholder information;
                data.StakeholderInfo = await CollectStakeholderInfoAsync(context, cancellationToken);

                // Collect constraints;
                data.Constraints = await CollectConstraintsAsync(context, cancellationToken);

                // Collect historical data for similar actions;
                data.HistoricalData = await CollectHistoricalDataAsync(domains, context, cancellationToken);

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting assessment data");
                throw;
            }
        }

        /// <summary>
        /// Domain verilerini toplar;
        /// </summary>
        private async Task<DomainAssessmentData> CollectDomainDataAsync(
            ImpactDomain domain,
            AssessmentContext context,
            CancellationToken cancellationToken)
        {
            var domainData = new DomainAssessmentData;
            {
                Domain = domain,
                CurrentState = new Dictionary<string, object>(),
                KeyMetrics = new Dictionary<string, MetricValue>(),
                Dependencies = new List<string>(),
                Vulnerabilities = new List<Vulnerability>()
            };

            try
            {
                switch (domain)
                {
                    case ImpactDomain.Technical:
                        domainData.CurrentState = await CollectTechnicalStateAsync(context, cancellationToken);
                        domainData.KeyMetrics = await CollectTechnicalMetricsAsync(cancellationToken);
                        break;

                    case ImpactDomain.Security:
                        domainData.CurrentState = await CollectSecurityStateAsync(context, cancellationToken);
                        domainData.KeyMetrics = await CollectSecurityMetricsAsync(cancellationToken);
                        domainData.Vulnerabilities = await _securityMonitor.GetCurrentVulnerabilitiesAsync(cancellationToken);
                        break;

                    case ImpactDomain.Financial:
                        domainData.CurrentState = await CollectFinancialStateAsync(context, cancellationToken);
                        domainData.KeyMetrics = await CollectFinancialMetricsAsync(cancellationToken);
                        break;

                    case ImpactDomain.Operational:
                        domainData.CurrentState = await CollectOperationalStateAsync(context, cancellationToken);
                        domainData.KeyMetrics = await CollectOperationalMetricsAsync(cancellationToken);
                        break;
                }

                return domainData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting data for domain: {Domain}", domain);
                return domainData;
            }
        }

        /// <summary>
        /// Etki modellerini uygular;
        /// </summary>
        private async Task<Dictionary<ImpactDomain, ImpactModelResult>> ApplyImpactModelsAsync(
            List<ImpactDomain> domains,
            ActionAnalysis actionAnalysis,
            AssessmentData assessmentData,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<ImpactDomain, ImpactModelResult>();

            foreach (var domain in domains)
            {
                try
                {
                    // Get relevant models for this domain;
                    var domainModels = _impactModels.Values;
                        .Where(m => m.Domain == domain && m.IsActive)
                        .ToList();

                    if (!domainModels.Any())
                    {
                        _logger.LogWarning("No active impact models found for domain: {Domain}", domain);
                        continue;
                    }

                    // Apply each model;
                    var domainResults = new List<ModelApplicationResult>();
                    foreach (var model in domainModels)
                    {
                        var result = await ApplyImpactModelAsync(
                            model,
                            actionAnalysis,
                            assessmentData,
                            cancellationToken);

                        domainResults.Add(result);
                    }

                    // Combine domain results;
                    var domainResult = await CombineDomainResultsAsync(
                        domain,
                        domainResults,
                        actionAnalysis,
                        cancellationToken);

                    results[domain] = domainResult;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error applying impact models for domain: {Domain}", domain);
                    // Continue with other domains;
                }
            }

            return results;
        }

        /// <summary>
        /// Etki modelini uygular;
        /// </summary>
        private async Task<ModelApplicationResult> ApplyImpactModelAsync(
            ImpactModel model,
            ActionAnalysis actionAnalysis,
            AssessmentData assessmentData,
            CancellationToken cancellationToken)
        {
            try
            {
                var startTime = DateTime.UtcNow;

                // Get domain-specific data;
                var domainData = assessmentData.DomainData.GetValueOrDefault(model.Domain);
                if (domainData == null)
                {
                    return new ModelApplicationResult;
                    {
                        ModelId = model.Id,
                        Success = false,
                        Error = $"No data available for domain: {model.Domain}"
                    };
                }

                // Extract required metrics;
                var metrics = ExtractModelMetrics(model, domainData, assessmentData);

                // Calculate impact using the model;
                var impactCalculation = await CalculateImpactAsync(
                    model,
                    actionAnalysis,
                    metrics,
                    cancellationToken);

                // Apply domain-specific model if available;
                if (_domainModels.TryGetValue(model.Domain, out var domainModel))
                {
                    var domainImpact = await domainModel.PredictImpactAsync(
                        actionAnalysis,
                        domainData,
                        cancellationToken);

                    // Combine with calculated impact;
                    impactCalculation = CombineImpactCalculations(impactCalculation, domainImpact);
                }

                var result = new ModelApplicationResult;
                {
                    ModelId = model.Id,
                    ModelName = model.Name,
                    Domain = model.Domain,
                    Success = true,
                    ImpactCalculation = impactCalculation,
                    MetricsUsed = metrics.Keys.ToList(),
                    Confidence = CalculateModelConfidence(model, metrics),
                    CalculationTime = DateTime.UtcNow - startTime;
                };

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying impact model: {ModelId}", model.Id);
                return new ModelApplicationResult;
                {
                    ModelId = model.Id,
                    Success = false,
                    Error = ex.Message;
                };
            }
        }

        /// <summary>
        /// Etkiyi hesaplar;
        /// </summary>
        private async Task<ImpactCalculation> CalculateImpactAsync(
            ImpactModel model,
            ActionAnalysis actionAnalysis,
            Dictionary<string, MetricValue> metrics,
            CancellationToken cancellationToken)
        {
            try
            {
                var calculation = new ImpactCalculation;
                {
                    ModelId = model.Id,
                    ImpactTypes = model.ImpactTypes,
                    ImpactScores = new Dictionary<ImpactType, double>(),
                    ComponentImpacts = new Dictionary<string, double>(),
                    Timeline = new ImpactTimeline()
                };

                // Calculate impact for each impact type;
                foreach (var impactType in model.ImpactTypes)
                {
                    var impactScore = await CalculateImpactTypeScoreAsync(
                        impactType,
                        model,
                        actionAnalysis,
                        metrics,
                        cancellationToken);

                    calculation.ImpactScores[impactType] = impactScore;
                }

                // Calculate overall impact score for this model;
                calculation.OverallImpactScore = calculation.ImpactScores.Values.Average();

                // Generate timeline predictions;
                calculation.Timeline = await GenerateImpactTimelineAsync(
                    calculation.OverallImpactScore,
                    model.PredictionHorizon,
                    cancellationToken);

                // Identify key drivers;
                calculation.KeyDrivers = await IdentifyKeyDriversAsync(
                    calculation,
                    metrics,
                    cancellationToken);

                return calculation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating impact for model: {ModelId}", model.Id);
                throw;
            }
        }

        /// <summary>
        /// Etki tipi skorunu hesaplar;
        /// </summary>
        private async Task<double> CalculateImpactTypeScoreAsync(
            ImpactType impactType,
            ImpactModel model,
            ActionAnalysis actionAnalysis,
            Dictionary<string, MetricValue> metrics,
            CancellationToken cancellationToken)
        {
            // Simplified calculation - in practice, this would use complex algorithms;
            var baseScore = 0.0;

            switch (impactType)
            {
                case ImpactType.Performance:
                    baseScore = CalculatePerformanceImpact(actionAnalysis, metrics);
                    break;

                case ImpactType.Security:
                    baseScore = CalculateSecurityImpact(actionAnalysis, metrics);
                    break;

                case ImpactType.Cost:
                    baseScore = CalculateCostImpact(actionAnalysis, metrics);
                    break;

                case ImpactType.Operational:
                    baseScore = CalculateOperationalImpact(actionAnalysis, metrics);
                    break;

                case ImpactType.Reputational:
                    baseScore = CalculateReputationalImpact(actionAnalysis, metrics);
                    break;

                default:
                    baseScore = 0.5; // Default medium impact;
                    break;
            }

            // Adjust based on action characteristics;
            baseScore = AdjustImpactScore(baseScore, actionAnalysis, metrics);

            // Apply confidence factor;
            var confidence = model.ConfidenceThreshold;
            baseScore *= confidence;

            return Math.Max(0, Math.Min(1, baseScore));
        }

        /// <summary>
        /// Performans etkisini hesaplar;
        /// </summary>
        private double CalculatePerformanceImpact(ActionAnalysis actionAnalysis, Dictionary<string, MetricValue> metrics)
        {
            var impactFactors = new List<double>();

            // System load factor;
            if (metrics.TryGetValue("CPU_Usage", out var cpuUsage))
                impactFactors.Add((double)cpuUsage.Value * 0.3);

            if (metrics.TryGetValue("Memory_Usage", out var memoryUsage))
                impactFactors.Add((double)memoryUsage.Value * 0.3);

            // Action complexity factor;
            impactFactors.Add(actionAnalysis.Complexity * 0.2);

            // Scope factor;
            var scopeFactor = actionAnalysis.Scope switch;
            {
                ActionScope.SystemWide => 0.8,
                ActionScope.Departmental => 0.6,
                ActionScope.Team => 0.4,
                ActionScope.Individual => 0.2,
                _ => 0.5;
            };
            impactFactors.Add(scopeFactor * 0.2);

            return impactFactors.Average();
        }

        /// <summary>
        /// Domain sonuçlarını birleştirir;
        /// </summary>
        private async Task<ImpactModelResult> CombineDomainResultsAsync(
            ImpactDomain domain,
            List<ModelApplicationResult> modelResults,
            ActionAnalysis actionAnalysis,
            CancellationToken cancellationToken)
        {
            var successfulResults = modelResults.Where(r => r.Success).ToList();

            if (!successfulResults.Any())
            {
                return new ImpactModelResult;
                {
                    Domain = domain,
                    Success = false,
                    Error = "No successful model applications"
                };
            }

            var domainResult = new ImpactModelResult;
            {
                Domain = domain,
                Success = true,
                ModelResults = successfulResults,
                OverallImpactScore = successfulResults.Average(r => r.ImpactCalculation?.OverallImpactScore ?? 0),
                Confidence = successfulResults.Average(r => r.Confidence),
                PrimaryImpactTypes = ExtractPrimaryImpactTypes(successfulResults)
            };

            // Calculate domain-specific metrics;
            domainResult.Metrics = await CalculateDomainMetricsAsync(
                domain,
                successfulResults,
                actionAnalysis,
                cancellationToken);

            return domainResult;
        }

        /// <summary>
        /// Etkileri tahmin eder;
        /// </summary>
        private async Task<ImpactPredictions> PredictImpactsAsync(
            Dictionary<ImpactDomain, ImpactModelResult> modelResults,
            TimeSpan? predictionHorizon,
            CancellationToken cancellationToken)
        {
            var predictions = new ImpactPredictions;
            {
                Timestamp = DateTime.UtcNow,
                PredictionHorizon = predictionHorizon ?? TimeSpan.FromDays(30),
                DomainPredictions = new Dictionary<ImpactDomain, DomainPrediction>(),
                OverallPrediction = new OverallImpactPrediction()
            };

            try
            {
                // Predict impacts for each domain;
                foreach (var kvp in modelResults)
                {
                    var domain = kvp.Key;
                    var result = kvp.Value;

                    if (!result.Success)
                        continue;

                    var domainPrediction = await PredictDomainImpactAsync(
                        domain,
                        result,
                        predictions.PredictionHorizon,
                        cancellationToken);

                    predictions.DomainPredictions[domain] = domainPrediction;
                }

                // Calculate overall predictions;
                predictions.OverallPrediction = await CalculateOverallPredictionAsync(
                    predictions.DomainPredictions.Values.ToList(),
                    predictions.PredictionHorizon,
                    cancellationToken);

                // Identify critical impacts;
                predictions.CriticalImpacts = await IdentifyCriticalImpactsAsync(
                    predictions.DomainPredictions,
                    cancellationToken);

                // Generate impact scenarios;
                predictions.Scenarios = await GenerateImpactScenariosAsync(
                    predictions.DomainPredictions,
                    predictions.PredictionHorizon,
                    cancellationToken);

                return predictions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error predicting impacts");
                throw;
            }
        }

        /// <summary>
        /// Riskleri değerlendirir;
        /// </summary>
        private async Task<RiskAssessmentResult> AssessRisksAsync(
            ImpactPredictions predictions,
            AssessmentContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                var riskAssessment = await _riskAnalyzer.AnalyzeRisksAsync(
                    new RiskAnalysisRequest;
                    {
                        ImpactPredictions = predictions,
                        Context = context;
                    },
                    cancellationToken);

                return riskAssessment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assessing risks");
                return new RiskAssessmentResult;
                {
                    Success = false,
                    Error = ex.Message;
                };
            }
        }

        /// <summary>
        /// Şiddeti değerlendirir;
        /// </summary>
        private async Task<SeverityEvaluation> EvaluateSeverityAsync(
            ImpactPredictions predictions,
            RiskAssessmentResult riskAssessment,
            CancellationToken cancellationToken)
        {
            var evaluation = new SeverityEvaluation;
            {
                Timestamp = DateTime.UtcNow,
                ImpactSeverity = CalculateImpactSeverity(predictions),
                RiskSeverity = riskAssessment.OverallRiskLevel,
                CombinedSeverity = CalculateCombinedSeverity(predictions, riskAssessment)
            };

            // Classify severity levels;
            evaluation.SeverityLevel = DetermineSeverityLevel(evaluation.CombinedSeverity);

            // Identify critical areas;
            evaluation.CriticalAreas = await IdentifyCriticalAreasAsync(
                predictions,
                riskAssessment,
                cancellationToken);

            // Determine urgency;
            evaluation.Urgency = DetermineUrgency(evaluation.SeverityLevel, predictions);

            return evaluation;
        }

        /// <summary>
        /// Hafifletme stratejileri oluşturur;
        /// </summary>
        private async Task<List<MitigationStrategy>> GenerateMitigationStrategiesAsync(
            ImpactPredictions predictions,
            RiskAssessmentResult riskAssessment,
            SeverityEvaluation severityEvaluation,
            CancellationToken cancellationToken)
        {
            var strategies = new List<MitigationStrategy>();

            try
            {
                // Generate strategies for critical impacts;
                foreach (var criticalImpact in predictions.CriticalImpacts)
                {
                    var strategy = await CreateMitigationStrategyAsync(
                        criticalImpact,
                        predictions,
                        riskAssessment,
                        cancellationToken);

                    if (strategy != null)
                    {
                        strategies.Add(strategy);
                    }
                }

                // Generate overall mitigation strategy;
                if (severityEvaluation.SeverityLevel >= SeverityLevel.High)
                {
                    var overallStrategy = await CreateOverallMitigationStrategyAsync(
                        predictions,
                        riskAssessment,
                        severityEvaluation,
                        cancellationToken);

                    strategies.Add(overallStrategy);
                }

                // Prioritize strategies;
                strategies = strategies;
                    .OrderByDescending(s => s.Priority)
                    .ThenByDescending(s => s.ExpectedEffectiveness)
                    .ToList();

                return strategies;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating mitigation strategies");
                return new List<MitigationStrategy>();
            }
        }

        /// <summary>
        /// Değerlendirme sonucunu oluşturur;
        /// </summary>
        private async Task<ImpactAssessmentResult> BuildAssessmentResultAsync(
            string assessmentId,
            ImpactAssessmentRequest request,
            ActionAnalysis actionAnalysis,
            List<ImpactDomain> affectedDomains,
            Dictionary<ImpactDomain, ImpactModelResult> modelResults,
            ImpactPredictions impactPredictions,
            RiskAssessmentResult riskAssessment,
            SeverityEvaluation severityEvaluation,
            List<MitigationStrategy> mitigationStrategies,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            var result = new ImpactAssessmentResult;
            {
                Id = assessmentId,
                RequestId = request.Id,
                ActionAnalysis = actionAnalysis,
                AffectedDomains = affectedDomains,
                ModelResults = modelResults,
                ImpactPredictions = impactPredictions,
                RiskAssessment = riskAssessment,
                SeverityEvaluation = severityEvaluation,
                MitigationStrategies = mitigationStrategies,
                OverallImpactScore = CalculateOverallImpactScore(modelResults, impactPredictions, severityEvaluation),
                Confidence = CalculateOverallConfidence(modelResults, impactPredictions),
                Recommendations = await GenerateRecommendationsAsync(
                    modelResults,
                    impactPredictions,
                    riskAssessment,
                    severityEvaluation,
                    cancellationToken),
                AssessmentTime = DateTime.UtcNow - startTime,
                Timestamp = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    ["assessment_version"] = "1.0",
                    ["models_used"] = modelResults.Count,
                    ["prediction_horizon"] = request.PredictionHorizon?.ToString() ?? "30 days"
                }
            };

            return result;
        }

        /// <summary>
        /// Çoklu etki değerlendirmesi yapar;
        /// </summary>
        public async Task<BatchImpactAssessmentResult> AssessImpactsBatchAsync(
            List<ImpactAssessmentRequest> requests,
            BatchAssessmentConfig config = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (requests == null || !requests.Any())
                throw new ArgumentException("Requests cannot be null or empty", nameof(requests));

            config ??= new BatchAssessmentConfig();

            try
            {
                _logger.LogInformation("Starting batch impact assessment for {RequestCount} requests", requests.Count);

                var batchId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                var results = new List<ImpactAssessmentResult>();
                var failedRequests = new List<FailedAssessment>();

                // Process in batches;
                var batchSize = Math.Min(config.MaxBatchSize, _config.MaxConcurrentAssessments);
                var batches = requests.Chunk(batchSize);

                foreach (var batch in batches)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var batchTasks = batch.Select(async request =>
                    {
                        try
                        {
                            var result = await AssessImpactAsync(request, cancellationToken);
                            return (Success: true, Result: result, Request: request, Error: null as string);
                        }
                        catch (Exception ex)
                        {
                            return (Success: false, Result: null, Request: request, Error: ex.Message);
                        }
                    }).ToList();

                    var batchResults = await Task.WhenAll(batchTasks);

                    foreach (var batchResult in batchResults)
                    {
                        if (batchResult.Success)
                        {
                            results.Add(batchResult.Result);
                        }
                        else;
                        {
                            failedRequests.Add(new FailedAssessment;
                            {
                                Request = batchResult.Request,
                                Error = batchResult.Error,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                    }

                    _logger.LogDebug("Completed batch of {BatchSize} assessments", batch.Length);
                }

                var batchResult = new BatchImpactAssessmentResult;
                {
                    BatchId = batchId,
                    TotalRequests = requests.Count,
                    SuccessfulAssessments = results,
                    FailedAssessments = failedRequests,
                    OverallStatistics = CalculateBatchStatistics(results),
                    ProcessingTime = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation(
                    "Batch impact assessment completed: {SuccessCount}/{TotalCount} successful",
                    results.Count, requests.Count);

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch impact assessment");
                throw new ImpactAssessmentException("Batch impact assessment failed", ex);
            }
        }

        /// <summary>
        /// ImpactAssessor istatistiklerini getirir;
        /// </summary>
        public async Task<ImpactAssessorStatistics> GetStatisticsAsync()
        {
            ValidateInitialization();

            try
            {
                var stats = new ImpactAssessorStatistics;
                {
                    TotalModels = _impactModels.Count,
                    ActiveModels = _impactModels.Values.Count(m => m.IsActive),
                    CacheHitRate = CalculateCacheHitRate(),
                    AverageAssessmentTime = CalculateAverageAssessmentTime(),
                    TotalAssessments = await GetTotalAssessmentsAsync(),
                    SuccessRate = await CalculateSuccessRateAsync(),
                    DomainDistribution = GetDomainDistribution(),
                    SeverityDistribution = await GetSeverityDistributionAsync(),
                    Uptime = DateTime.UtcNow - _startTime,
                    MemoryUsage = GC.GetTotalMemory(false),
                    CurrentLoad = _config.MaxConcurrentAssessments - _assessmentSemaphore.CurrentCount;
                };

                // Prediction accuracy;
                stats.PredictionAccuracy = await CalculatePredictionAccuracyAsync();

                // Model performance;
                stats.ModelPerformance = await CalculateModelPerformanceAsync();

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                throw;
            }
        }

        /// <summary>
        /// ImpactAssessor'ı durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                _logger.LogInformation("Shutting down ImpactAssessor...");

                // Wait for ongoing assessments;
                await Task.Delay(1000);

                // Clear caches;
                ClearCaches();

                // Shutdown dependencies;
                await _predictionModel.ShutdownAsync();
                await _riskAnalyzer.ShutdownAsync();

                foreach (var model in _domainModels.Values)
                {
                    await model.ShutdownAsync();
                }

                _isInitialized = false;

                _logger.LogInformation("ImpactAssessor shutdown completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during shutdown");
                throw;
            }
        }

        /// <summary>
        /// Dispose pattern implementation;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    ShutdownAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during disposal");
                }

                _assessmentSemaphore?.Dispose();
                ClearCaches();

                // Clear event handlers;
                OnAssessmentStarted = null;
                OnAssessmentProgress = null;
                OnAssessmentCompleted = null;
                OnCriticalImpactDetected = null;
                OnImpactPredictionUpdated = null;
            }
        }

        // Helper methods;
        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new ImpactAssessorNotInitializedException(
                    "ImpactAssessor must be initialized before use. Call InitializeAsync() first.");
            }
        }

        private string GenerateCacheKey(ImpactAssessmentRequest request)
        {
            return $"{request.Id}_{request.Action.Id}_{request.Context?.SystemId ?? "system"}_{DateTime.UtcNow:yyyyMMddHH}";
        }

        private async Task ReportProgressAsync(
            string assessmentId,
            int progressPercentage,
            string message,
            AssessmentContext context)
        {
            try
            {
                OnAssessmentProgress?.Invoke(this, new ImpactAssessmentProgressEventArgs;
                {
                    AssessmentId = assessmentId,
                    ProgressPercentage = progressPercentage,
                    Message = message,
                    Context = context,
                    Timestamp = DateTime.UtcNow;
                });

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reporting progress");
            }
        }

        private void CleanupExpiredCacheEntries()
        {
            var expiredKeys = _assessmentCache;
                .Where(kvp => kvp.Value.Timestamp.Add(_config.CacheDuration) < DateTime.UtcNow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _assessmentCache.Remove(key);
            }

            if (expiredKeys.Any())
            {
                _logger.LogDebug("Cleaned up {ExpiredCount} expired cache entries", expiredKeys.Count);
            }
        }

        private async Task RecordAssessmentHistoryAsync(
            ImpactAssessmentResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                var historicalImpact = new HistoricalImpact;
                {
                    Id = Guid.NewGuid().ToString(),
                    AssessmentId = result.Id,
                    ActionId = result.ActionAnalysis.ActionId,
                    Domains = result.AffectedDomains,
                    OverallImpactScore = result.OverallImpactScore,
                    SeverityLevel = result.SeverityEvaluation.SeverityLevel,
                    Timestamp = result.Timestamp,
                    SourceSystem = result.RequestId;
                };

                await _knowledgeBase.RecordHistoricalImpactAsync(historicalImpact, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error recording assessment history");
            }
        }

        private async Task CheckCriticalImpactsAsync(
            ImpactAssessmentResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                if (result.SeverityEvaluation.SeverityLevel >= SeverityLevel.Critical)
                {
                    OnCriticalImpactDetected?.Invoke(this, new CriticalImpactDetectedEventArgs;
                    {
                        AssessmentId = result.Id,
                        ImpactResult = result,
                        Severity = result.SeverityEvaluation.SeverityLevel,
                        CriticalAreas = result.SeverityEvaluation.CriticalAreas,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking critical impacts");
            }
        }

        private void ClearCaches()
        {
            lock (_lockObject)
            {
                _assessmentCache.Clear();
                _impactHistory.Clear();
            }

            _logger.LogDebug("ImpactAssessor caches cleared");
        }

        private DateTime _startTime = DateTime.UtcNow;

        // Additional calculation methods would continue here...
    }

    #region Supporting Classes and Enums;

    public enum ImpactDomain;
    {
        Technical,
        Operational,
        Financial,
        Security,
        Reputational,
        Legal,
        Environmental,
        Social,
        Human,
        Strategic,
        Compliance,
        Quality,
        Innovation,
        Competitive,
        Market;
    }

    public enum ImpactType;
    {
        Performance,
        Availability,
        Scalability,
        Security,
        Compliance,
        Privacy,
        Cost,
        Revenue,
        ROI,
        Investment,
        Operational,
        Efficiency,
        Productivity,
        Reputational,
        Brand,
        Customer_Satisfaction,
        Human,
        Stakeholder,
        User_Experience,
        Strategic,
        Competitive,
        Innovation,
        Environmental,
        Social,
        Legal,
        Regulatory;
    }

    public enum ActionType;
    {
        SystemChange,
        Deployment,
        Configuration,
        PolicyChange,
        ProcessChange,
        FinancialDecision,
        Investment,
        SecurityUpdate,
        AccessChange,
        DataMigration,
        SoftwareUpdate,
        HardwareChange,
        OrganizationalChange,
        StrategicDecision,
        EmergencyAction;
    }

    public enum ActionScope;
    {
        SystemWide,
        Departmental,
        Team,
        Individual,
        Unknown;
    }

    public enum AssessmentPriority;
    {
        Critical,
        High,
        Medium,
        Low,
        Informational;
    }

    public enum SeverityLevel;
    {
        Critical,
        High,
        Medium,
        Low,
        Negligible;
    }

    public enum UrgencyLevel;
    {
        Immediate,
        High,
        Medium,
        Low,
        Planned;
    }

    public class ImpactAssessorConfig;
    {
        public bool EnableCaching { get; set; } = true;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
        public int MaxConcurrentAssessments { get; set; } = Environment.ProcessorCount * 2;
        public int MaxHistorySize { get; set; } = 1000;
        public double CriticalImpactThreshold { get; set; } = 0.8;
        public TimeSpan DefaultPredictionHorizon { get; set; } = TimeSpan.FromDays(30);
        public bool EnableRealTimeMonitoring { get; set; } = true;
        public TimeSpan AssessmentTimeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    public class ImpactAssessorOptions;
    {
        public Dictionary<string, double> DomainWeights { get; set; } = new()
        {
            ["Technical"] = 0.25,
            ["Security"] = 0.2,
            ["Financial"] = 0.15,
            ["Operational"] = 0.15,
            ["Reputational"] = 0.1,
            ["Human"] = 0.1,
            ["Strategic"] = 0.05;
        };
        public bool EnableDetailedLogging { get; set; } = true;
        public int MaxPredictionSteps { get; set; } = 10;
        public double ConfidenceThreshold { get; set; } = 0.7;
    }

    public class ImpactAssessmentRequest;
    {
        public string Id { get; set; }
        public ActionDescription Action { get; set; }
        public AssessmentContext Context { get; set; }
        public TimeSpan? PredictionHorizon { get; set; }
        public List<ImpactDomain> SpecificDomains { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public DateTime RequestTime { get; set; }
    }

    public class ImpactAssessmentResult;
    {
        public string Id { get; set; }
        public string RequestId { get; set; }
        public ActionAnalysis ActionAnalysis { get; set; }
        public List<ImpactDomain> AffectedDomains { get; set; }
        public Dictionary<ImpactDomain, ImpactModelResult> ModelResults { get; set; }
        public ImpactPredictions ImpactPredictions { get; set; }
        public RiskAssessmentResult RiskAssessment { get; set; }
        public SeverityEvaluation SeverityEvaluation { get; set; }
        public List<MitigationStrategy> MitigationStrategies { get; set; }
        public List<Recommendation> Recommendations { get; set; }
        public double OverallImpactScore { get; set; }
        public double Confidence { get; set; }
        public TimeSpan AssessmentTime { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ImpactModel;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ImpactDomain Domain { get; set; }
        public List<ImpactType> ImpactTypes { get; set; }
        public List<string> Metrics { get; set; }
        public TimeSpan PredictionHorizon { get; set; }
        public double ConfidenceThreshold { get; set; }
        public double Weight { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsed { get; set; }
        public int UsageCount { get; set; }
    }

    // Additional supporting classes would be defined here...
    // Due to length constraints, I'm showing the main structure;

    #endregion;

    #region Exceptions;

    public class ImpactAssessorException : Exception
    {
        public ImpactAssessorException(string message) : base(message) { }
        public ImpactAssessorException(string message, Exception inner) : base(message, inner) { }
    }

    public class ImpactAssessorNotInitializedException : ImpactAssessorException;
    {
        public ImpactAssessorNotInitializedException(string message) : base(message) { }
    }

    public class ImpactAssessmentException : ImpactAssessorException;
    {
        public ImpactAssessmentException(string message) : base(message) { }
        public ImpactAssessmentException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;

    #region Events;

    public class ImpactAssessmentStartedEventArgs : EventArgs;
    {
        public string AssessmentId { get; set; }
        public ImpactAssessmentRequest Request { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ImpactAssessmentProgressEventArgs : EventArgs;
    {
        public string AssessmentId { get; set; }
        public int ProgressPercentage { get; set; }
        public string Message { get; set; }
        public AssessmentContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ImpactAssessmentCompletedEventArgs : EventArgs;
    {
        public string AssessmentId { get; set; }
        public ImpactAssessmentRequest Request { get; set; }
        public ImpactAssessmentResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CriticalImpactDetectedEventArgs : EventArgs;
    {
        public string AssessmentId { get; set; }
        public ImpactAssessmentResult ImpactResult { get; set; }
        public SeverityLevel Severity { get; set; }
        public List<string> CriticalAreas { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ImpactPredictionUpdatedEventArgs : EventArgs;
    {
        public string PredictionId { get; set; }
        public ImpactDomain Domain { get; set; }
        public ImpactPredictions UpdatedPredictions { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}

// Interface definition for dependency injection;
public interface IImpactAssessor : IDisposable
{
    Task InitializeAsync(ImpactAssessorConfig config = null);
    Task<ImpactAssessmentResult> AssessImpactAsync(
        ImpactAssessmentRequest request,
        CancellationToken cancellationToken = default);
    Task<BatchImpactAssessmentResult> AssessImpactsBatchAsync(
        List<ImpactAssessmentRequest> requests,
        BatchAssessmentConfig config = null,
        CancellationToken cancellationToken = default);
    Task<ImpactAssessorStatistics> GetStatisticsAsync();
    Task ShutdownAsync();

    bool IsInitialized { get; }

    event EventHandler<ImpactAssessmentStartedEventArgs> OnAssessmentStarted;
    event EventHandler<ImpactAssessmentProgressEventArgs> OnAssessmentProgress;
    event EventHandler<ImpactAssessmentCompletedEventArgs> OnAssessmentCompleted;
    event EventHandler<CriticalImpactDetectedEventArgs> OnCriticalImpactDetected;
    event EventHandler<ImpactPredictionUpdatedEventArgs> OnImpactPredictionUpdated;
}

// Domain-specific model interfaces;
public interface IImpactPredictionModel : IDisposable
{
    Task InitializeAsync();
    Task<ImpactPrediction> PredictImpactAsync(
        ActionAnalysis action,
        DomainAssessmentData domainData,
        CancellationToken cancellationToken);
    Task ShutdownAsync();
}

public class TechnicalImpactModel : IImpactPredictionModel;
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task<ImpactPrediction> PredictImpactAsync(
        ActionAnalysis action,
        DomainAssessmentData domainData,
        CancellationToken cancellationToken)
    {
        // Implementation for technical impact prediction;
        return Task.FromResult(new ImpactPrediction());
    }

    public Task ShutdownAsync() => Task.CompletedTask;
    public void Dispose() { }
}
