using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Brain.IntentRecognition.ContextBuilder;
using NEDA.Common.Utilities;
using NEDA.Interface.InteractionManager.ContextKeeper;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.Messaging.EventBus;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.Brain.IntentRecognition.PriorityAssigner;
{
    /// <summary>
    /// Priority assignment engine for tasks, intents, and system operations;
    /// </summary>
    public interface IPriorityEngine : IDisposable
    {
        /// <summary>
        /// Calculates priority for a task based on multiple factors;
        /// </summary>
        Task<PriorityAssignment> CalculatePriorityAsync(
            TaskMetadata task,
            PriorityContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Assigns priority to an intent based on user input and context;
        /// </summary>
        Task<IntentPriority> AssignIntentPriorityAsync(
            IntentMetadata intent,
            UserContext userContext,
            SystemContext systemContext,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Dynamically adjusts priorities based on changing conditions;
        /// </summary>
        Task<IEnumerable<PriorityAdjustment>> AdjustPrioritiesAsync(
            PriorityAdjustmentRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Optimizes priority distribution among competing tasks;
        /// </summary>
        Task<PriorityOptimizationResult> OptimizePrioritiesAsync(
            IEnumerable<PrioritizableItem> items,
            OptimizationConstraints constraints,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculates urgency level for time-sensitive operations;
        /// </summary>
        Task<UrgencyAssessment> CalculateUrgencyAsync(
            UrgencyRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Evaluates the importance of a task relative to others;
        /// </summary>
        Task<ImportanceEvaluation> EvaluateImportanceAsync(
            ImportanceCriteria criteria,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Schedules tasks based on priority and resource availability;
        /// </summary>
        Task<TaskSchedule> ScheduleByPriorityAsync(
            IEnumerable<SchedulableTask> tasks,
            ResourceConstraints resources,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Balances priorities between competing systems or users;
        /// </summary>
        Task<PriorityBalance> BalancePrioritiesAsync(
            IEnumerable<PriorityConflict> conflicts,
            BalancingStrategy strategy,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Predicts priority changes based on historical patterns;
        /// </summary>
        Task<PriorityPrediction> PredictPriorityTrendAsync(
            string itemId,
            TimeSpan predictionWindow,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates priority assignments for consistency and fairness;
        /// </summary>
        Task<PriorityValidation> ValidatePriorityAsync(
            PriorityAssignment assignment,
            ValidationRules rules,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Advanced priority engine with machine learning and real-time adjustment capabilities;
    /// </summary>
    public class PriorityEngine : IPriorityEngine;
    {
        private readonly ILogger<PriorityEngine> _logger;
        private readonly IOptions<PriorityEngineOptions> _options;
        private readonly IContextManager _contextManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IEventBus _eventBus;
        private readonly PriorityLearningModel _learningModel;
        private readonly PriorityCache _priorityCache;
        private readonly SemaphoreSlim _calculationLock = new(1, 1);
        private readonly ConcurrentDictionary<string, PriorityHistory> _priorityHistory;
        private readonly System.Timers.Timer _optimizationTimer;
        private bool _disposed;
        private long _totalCalculations;

        public PriorityEngine(
            ILogger<PriorityEngine> logger,
            IOptions<PriorityEngineOptions> options,
            IContextManager contextManager,
            IPerformanceMonitor performanceMonitor,
            IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _learningModel = new PriorityLearningModel(_options.Value);
            _priorityCache = new PriorityCache(options.Value.CacheSettings);
            _priorityHistory = new ConcurrentDictionary<string, PriorityHistory>();

            // Initialize optimization timer;
            _optimizationTimer = new System.Timers.Timer(_options.Value.OptimizationIntervalMs);
            _optimizationTimer.Elapsed += async (s, e) => await RunPeriodicOptimizationAsync();
            _optimizationTimer.Start();

            SubscribeToEvents();
            _logger.LogInformation("PriorityEngine initialized with optimization interval: {Interval}ms",
                _options.Value.OptimizationIntervalMs);
        }

        /// <summary>
        /// Calculates comprehensive priority for a task;
        /// </summary>
        public async Task<PriorityAssignment> CalculatePriorityAsync(
            TaskMetadata task,
            PriorityContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            await _calculationLock.WaitAsync(cancellationToken);
            try
            {
                Interlocked.Increment(ref _totalCalculations);
                _logger.LogDebug("Calculating priority for task: {TaskId} ({Type})", task.Id, task.Type);

                // Check cache first;
                var cacheKey = GenerateCacheKey(task, context);
                if (_priorityCache.TryGet(cacheKey, out PriorityAssignment cached))
                {
                    _logger.LogTrace("Cache hit for task priority: {TaskId}", task.Id);
                    return cached;
                }

                // Build evaluation context;
                var evaluationContext = await BuildEvaluationContextAsync(task, context, cancellationToken);

                // Calculate priority components;
                var components = await CalculatePriorityComponentsAsync(task, evaluationContext, cancellationToken);

                // Apply weighting;
                var weightedScore = ApplyWeighting(components, evaluationContext.WeightingProfile);

                // Apply modifiers;
                var modifiedScore = ApplyModifiers(weightedScore, evaluationContext.Modifiers);

                // Determine priority level;
                var priorityLevel = DeterminePriorityLevel(modifiedScore.FinalScore);

                // Create priority assignment;
                var assignment = new PriorityAssignment;
                {
                    TaskId = task.Id,
                    TaskType = task.Type,
                    PriorityScore = modifiedScore.FinalScore,
                    PriorityLevel = priorityLevel,
                    Components = components,
                    WeightedScore = weightedScore,
                    ModifiedScore = modifiedScore,
                    CalculationTime = DateTime.UtcNow,
                    ContextSnapshot = evaluationContext.Snapshot(),
                    Confidence = CalculateConfidence(components, evaluationContext),
                    Recommendations = await GenerateRecommendationsAsync(task, priorityLevel, evaluationContext, cancellationToken)
                };

                // Validate assignment;
                var validation = await ValidatePriorityAsync(assignment, _options.Value.ValidationRules, cancellationToken);
                assignment.ValidationResult = validation;

                if (!validation.IsValid)
                {
                    _logger.LogWarning("Priority validation failed for task {TaskId}: {Errors}",
                        task.Id, string.Join(", ", validation.Errors));

                    // Apply corrections if validation failed;
                    assignment = ApplyValidationCorrections(assignment, validation);
                }

                // Cache the result;
                _priorityCache.Set(cacheKey, assignment, TimeSpan.FromMinutes(_options.Value.CacheDurationMinutes));

                // Record in history;
                RecordPriorityHistory(task.Id, assignment);

                // Publish priority assigned event;
                await PublishPriorityEventAsync(assignment, cancellationToken);

                _logger.LogInformation("Calculated priority for task {TaskId}: {Priority} (score: {Score})",
                    task.Id, priorityLevel, assignment.PriorityScore);

                return assignment;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Priority calculation cancelled for task {TaskId}", task.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating priority for task {TaskId}", task.Id);
                throw new PriorityCalculationException($"Failed to calculate priority: {ex.Message}", ex);
            }
            finally
            {
                _calculationLock.Release();
            }
        }

        /// <summary>
        /// Assigns priority to an intent;
        /// </summary>
        public async Task<IntentPriority> AssignIntentPriorityAsync(
            IntentMetadata intent,
            UserContext userContext,
            SystemContext systemContext,
            CancellationToken cancellationToken = default)
        {
            if (intent == null)
                throw new ArgumentNullException(nameof(intent));

            try
            {
                _logger.LogDebug("Assigning priority to intent: {IntentId} ({Domain})", intent.Id, intent.Domain);

                // Calculate base priority from intent metadata;
                var basePriority = CalculateIntentBasePriority(intent);

                // Apply user context factors;
                var userFactors = await EvaluateUserContextFactorsAsync(userContext, cancellationToken);
                var userAdjusted = ApplyUserContextAdjustment(basePriority, userFactors);

                // Apply system context factors;
                var systemFactors = await EvaluateSystemContextFactorsAsync(systemContext, cancellationToken);
                var systemAdjusted = ApplySystemContextAdjustment(userAdjusted, systemFactors);

                // Apply urgency factors;
                var urgency = await CalculateUrgencyForIntentAsync(intent, userContext, systemContext, cancellationToken);
                var urgencyAdjusted = ApplyUrgencyAdjustment(systemAdjusted, urgency);

                // Apply business rules;
                var ruleAdjusted = ApplyBusinessRules(urgencyAdjusted, intent, userContext);

                // Apply learning model adjustments;
                var learnedAdjustment = await _learningModel.PredictAdjustmentAsync(intent, userContext, cancellationToken);
                var finalPriority = ApplyLearnedAdjustment(ruleAdjusted, learnedAdjustment);

                // Determine execution order;
                var executionOrder = DetermineExecutionOrder(finalPriority, intent, systemContext);

                // Create intent priority result;
                var result = new IntentPriority;
                {
                    IntentId = intent.Id,
                    IntentName = intent.Name,
                    Domain = intent.Domain,
                    BasePriority = basePriority,
                    FinalPriority = finalPriority,
                    PriorityLevel = MapToPriorityLevel(finalPriority),
                    UrgencyLevel = urgency.Level,
                    UserContextWeight = userFactors.Weight,
                    SystemContextWeight = systemFactors.Weight,
                    LearnedAdjustment = learnedAdjustment,
                    ExecutionOrder = executionOrder,
                    EstimatedProcessingTime = EstimateProcessingTime(intent, finalPriority),
                    ResourceRequirements = DetermineResourceRequirements(intent, finalPriority),
                    AssignmentTime = DateTime.UtcNow,
                    ExpiryTime = CalculatePriorityExpiry(intent, finalPriority),
                    ConfidenceScore = CalculateIntentPriorityConfidence(basePriority, userFactors, systemFactors, urgency)
                };

                // Validate and adjust if needed;
                result = await ValidateAndAdjustIntentPriorityAsync(result, cancellationToken);

                _logger.LogInformation("Assigned priority to intent {IntentId}: {Priority} (level: {Level})",
                    intent.Id, result.FinalPriority, result.PriorityLevel);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning priority to intent {IntentId}", intent.Id);
                throw new IntentPriorityException($"Failed to assign intent priority: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Dynamically adjusts priorities based on changing conditions;
        /// </summary>
        public async Task<IEnumerable<PriorityAdjustment>> AdjustPrioritiesAsync(
            PriorityAdjustmentRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _calculationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Adjusting priorities for {Count} items (trigger: {Trigger})",
                    request.Items.Count(), request.Trigger);

                var adjustments = new List<PriorityAdjustment>();
                var adjustmentStrategies = SelectAdjustmentStrategies(request.Trigger, request.Severity);

                foreach (var item in request.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var adjustment = await CalculateSingleAdjustmentAsync(
                            item,
                            request,
                            adjustmentStrategies,
                            cancellationToken);

                        if (adjustment != null)
                        {
                            adjustments.Add(adjustment);

                            // Apply the adjustment;
                            await ApplyPriorityAdjustmentAsync(adjustment, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error adjusting priority for item {ItemId}", item.ItemId);
                    }
                }

                // Optimize adjustments if needed;
                if (request.Optimize)
                {
                    adjustments = await OptimizeAdjustmentsAsync(adjustments, request.Constraints, cancellationToken);
                }

                // Publish adjustment summary;
                await PublishAdjustmentSummaryAsync(adjustments, request, cancellationToken);

                _logger.LogInformation("Completed priority adjustments: {Count} items adjusted", adjustments.Count);

                return adjustments;
            }
            finally
            {
                _calculationLock.Release();
            }
        }

        /// <summary>
        /// Optimizes priority distribution;
        /// </summary>
        public async Task<PriorityOptimizationResult> OptimizePrioritiesAsync(
            IEnumerable<PrioritizableItem> items,
            OptimizationConstraints constraints,
            CancellationToken cancellationToken = default)
        {
            if (items == null || !items.Any())
                throw new ArgumentException("Items cannot be null or empty", nameof(items));

            await _calculationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("Optimizing priorities for {Count} items", items.Count());

                var itemList = items.ToList();
                var optimizationStart = DateTime.UtcNow;

                // Analyze current state;
                var currentState = AnalyzeCurrentPriorityState(itemList);

                // Generate optimization scenarios;
                var scenarios = await GenerateOptimizationScenariosAsync(itemList, constraints, cancellationToken);

                // Evaluate scenarios;
                var evaluatedScenarios = await EvaluateOptimizationScenariosAsync(scenarios, cancellationToken);

                // Select best scenario;
                var bestScenario = SelectBestScenario(evaluatedScenarios, constraints);

                // Calculate optimization metrics;
                var metrics = CalculateOptimizationMetrics(currentState, bestScenario);

                // Create optimization plan;
                var plan = CreateOptimizationPlan(bestScenario, constraints);

                // Validate optimization;
                var validation = ValidateOptimizationPlan(plan, constraints);

                var result = new PriorityOptimizationResult;
                {
                    OptimizationId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - optimizationStart,
                    OriginalState = currentState,
                    OptimizedState = bestScenario.FinalState,
                    SelectedScenario = bestScenario,
                    AllScenarios = evaluatedScenarios,
                    OptimizationPlan = plan,
                    Metrics = metrics,
                    ValidationResult = validation,
                    IsFeasible = validation.IsFeasible,
                    EstimatedImprovement = metrics.ImprovementScore,
                    ResourceImpact = metrics.ResourceImpact,
                    RiskAssessment = await AssessOptimizationRiskAsync(bestScenario, cancellationToken)
                };

                if (result.IsFeasible && constraints.AutoApply)
                {
                    await ApplyOptimizationPlanAsync(plan, cancellationToken);
                    result.Applied = true;
                    result.ApplicationTime = DateTime.UtcNow;
                }

                _logger.LogInformation("Priority optimization completed: {Id} (improvement: {Improvement:P2})",
                    result.OptimizationId, result.EstimatedImprovement);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing priorities");
                throw new PriorityOptimizationException($"Failed to optimize priorities: {ex.Message}", ex);
            }
            finally
            {
                _calculationLock.Release();
            }
        }

        /// <summary>
        /// Calculates urgency level;
        /// </summary>
        public async Task<UrgencyAssessment> CalculateUrgencyAsync(
            UrgencyRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogDebug("Calculating urgency for {ItemType}: {ItemId}", request.ItemType, request.ItemId);

                // Calculate time-based urgency;
                var timeUrgency = CalculateTimeBasedUrgency(request);

                // Calculate impact-based urgency;
                var impactUrgency = await CalculateImpactBasedUrgencyAsync(request, cancellationToken);

                // Calculate dependency-based urgency;
                var dependencyUrgency = await CalculateDependencyUrgencyAsync(request, cancellationToken);

                // Calculate context-based urgency;
                var contextUrgency = await CalculateContextUrgencyAsync(request, cancellationToken);

                // Combine urgency factors;
                var combinedUrgency = CombineUrgencyFactors(timeUrgency, impactUrgency, dependencyUrgency, contextUrgency);

                // Apply urgency modifiers;
                var modifiedUrgency = ApplyUrgencyModifiers(combinedUrgency, request.Modifiers);

                // Determine urgency level;
                var urgencyLevel = DetermineUrgencyLevel(modifiedUrgency.FinalScore);

                // Calculate escalation threshold;
                var escalationThreshold = CalculateEscalationThreshold(urgencyLevel, request);

                var assessment = new UrgencyAssessment;
                {
                    RequestId = request.RequestId,
                    ItemId = request.ItemId,
                    ItemType = request.ItemType,
                    TimeUrgency = timeUrgency,
                    ImpactUrgency = impactUrgency,
                    DependencyUrgency = dependencyUrgency,
                    ContextUrgency = contextUrgency,
                    CombinedUrgency = combinedUrgency,
                    ModifiedUrgency = modifiedUrgency,
                    UrgencyLevel = urgencyLevel,
                    UrgencyScore = modifiedUrgency.FinalScore,
                    IsCritical = urgencyLevel >= UrgencyLevel.Critical,
                    RequiresImmediateAction = modifiedUrgency.FinalScore >= _options.Value.ImmediateActionThreshold,
                    EscalationThreshold = escalationThreshold,
                    RecommendedActions = GenerateUrgencyActions(urgencyLevel, request),
                    AssessmentTime = DateTime.UtcNow,
                    ValidUntil = CalculateUrgencyValidity(urgencyLevel, request),
                    Confidence = CalculateUrgencyConfidence(timeUrgency, impactUrgency, dependencyUrgency, contextUrgency)
                };

                // Track urgency assessment;
                await TrackUrgencyAssessmentAsync(assessment, cancellationToken);

                _logger.LogInformation("Urgency assessment for {ItemId}: {Level} (score: {Score})",
                    request.ItemId, urgencyLevel, assessment.UrgencyScore);

                return assessment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating urgency for {ItemId}", request.ItemId);
                throw new UrgencyCalculationException($"Failed to calculate urgency: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Evaluates task importance;
        /// </summary>
        public async Task<ImportanceEvaluation> EvaluateImportanceAsync(
            ImportanceCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            try
            {
                _logger.LogDebug("Evaluating importance for criteria: {CriteriaId}", criteria.CriteriaId);

                // Evaluate business value;
                var businessValue = await EvaluateBusinessValueAsync(criteria, cancellationToken);

                // Evaluate strategic alignment;
                var strategicAlignment = await EvaluateStrategicAlignmentAsync(criteria, cancellationToken);

                // Evaluate stakeholder impact;
                var stakeholderImpact = await EvaluateStakeholderImpactAsync(criteria, cancellationToken);

                // Evaluate compliance requirements;
                var complianceImportance = await EvaluateComplianceImportanceAsync(criteria, cancellationToken);

                // Evaluate risk implications;
                var riskImportance = await EvaluateRiskImportanceAsync(criteria, cancellationToken);

                // Combine importance factors;
                var combinedImportance = CombineImportanceFactors(
                    businessValue,
                    strategicAlignment,
                    stakeholderImpact,
                    complianceImportance,
                    riskImportance);

                // Apply importance modifiers;
                var modifiedImportance = ApplyImportanceModifiers(combinedImportance, criteria.Modifiers);

                // Determine importance category;
                var importanceCategory = DetermineImportanceCategory(modifiedImportance.FinalScore);

                // Calculate priority weight;
                var priorityWeight = CalculatePriorityWeight(modifiedImportance, criteria);

                var evaluation = new ImportanceEvaluation;
                {
                    CriteriaId = criteria.CriteriaId,
                    ItemId = criteria.ItemId,
                    BusinessValue = businessValue,
                    StrategicAlignment = strategicAlignment,
                    StakeholderImpact = stakeholderImpact,
                    ComplianceImportance = complianceImportance,
                    RiskImportance = riskImportance,
                    CombinedImportance = combinedImportance,
                    ModifiedImportance = modifiedImportance,
                    ImportanceCategory = importanceCategory,
                    ImportanceScore = modifiedImportance.FinalScore,
                    PriorityWeight = priorityWeight,
                    IsMissionCritical = importanceCategory == ImportanceCategory.MissionCritical,
                    RequiresExecutiveAttention = modifiedImportance.FinalScore >= _options.Value.ExecutiveAttentionThreshold,
                    RecommendedPriority = MapImportanceToPriority(modifiedImportance.FinalScore),
                    ResourceAllocationRecommendation = GenerateResourceRecommendation(modifiedImportance, criteria),
                    EvaluationTime = DateTime.UtcNow,
                    ValidFor = TimeSpan.FromHours(_options.Value.ImportanceValidityHours),
                    Confidence = CalculateImportanceConfidence(
                        businessValue,
                        strategicAlignment,
                        stakeholderImpact,
                        complianceImportance,
                        riskImportance)
                };

                // Store evaluation for future reference;
                await StoreImportanceEvaluationAsync(evaluation, cancellationToken);

                _logger.LogInformation("Importance evaluation for {ItemId}: {Category} (score: {Score})",
                    criteria.ItemId, importanceCategory, evaluation.ImportanceScore);

                return evaluation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating importance for criteria {CriteriaId}", criteria.CriteriaId);
                throw new ImportanceEvaluationException($"Failed to evaluate importance: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Schedules tasks by priority;
        /// </summary>
        public async Task<TaskSchedule> ScheduleByPriorityAsync(
            IEnumerable<SchedulableTask> tasks,
            ResourceConstraints resources,
            CancellationToken cancellationToken = default)
        {
            if (tasks == null || !tasks.Any())
                throw new ArgumentException("Tasks cannot be null or empty", nameof(tasks));

            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            await _calculationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("Scheduling {Count} tasks by priority", tasks.Count());

                var taskList = tasks.ToList();
                var schedulingStart = DateTime.UtcNow;

                // Calculate priorities for all tasks;
                var prioritizedTasks = new List<PrioritizedTask>();
                foreach (var task in taskList)
                {
                    var priority = await CalculatePriorityAsync(
                        new TaskMetadata { Id = task.Id, Type = task.Type },
                        new PriorityContext { SchedulingContext = true },
                        cancellationToken);

                    prioritizedTasks.Add(new PrioritizedTask;
                    {
                        Task = task,
                        Priority = priority,
                        ResourceRequirements = task.ResourceRequirements;
                    });
                }

                // Sort by priority;
                var sortedTasks = prioritizedTasks;
                    .OrderByDescending(t => t.Priority.PriorityScore)
                    .ThenBy(t => t.Task.EstimatedDuration)
                    .ToList();

                // Apply scheduling algorithm;
                var schedule = ApplySchedulingAlgorithm(sortedTasks, resources);

                // Optimize schedule;
                var optimizedSchedule = await OptimizeScheduleAsync(schedule, resources, cancellationToken);

                // Validate schedule feasibility;
                var feasibilityCheck = ValidateScheduleFeasibility(optimizedSchedule, resources);

                // Calculate schedule metrics;
                var metrics = CalculateScheduleMetrics(optimizedSchedule, schedulingStart);

                var result = new TaskSchedule;
                {
                    ScheduleId = Guid.NewGuid().ToString(),
                    CreationTime = DateTime.UtcNow,
                    TimeWindow = resources.TimeWindow,
                    ScheduledTasks = optimizedSchedule.ScheduledTasks,
                    UnscheduledTasks = optimizedSchedule.UnscheduledTasks,
                    ResourceUtilization = optimizedSchedule.ResourceUtilization,
                    PriorityDistribution = CalculatePriorityDistribution(optimizedSchedule),
                    EstimatedCompletionTime = optimizedSchedule.EstimatedCompletion,
                    Makespan = metrics.Makespan,
                    AverageWaitTime = metrics.AverageWaitTime,
                    PriorityEfficiency = metrics.PriorityEfficiency,
                    ResourceEfficiency = metrics.ResourceEfficiency,
                    IsFeasible = feasibilityCheck.IsFeasible,
                    FeasibilityIssues = feasibilityCheck.Issues,
                    OptimizationLevel = metrics.OptimizationLevel,
                    ScheduleHash = CalculateScheduleHash(optimizedSchedule)
                };

                // Apply schedule if auto-scheduling is enabled;
                if (_options.Value.AutoApplySchedules && result.IsFeasible)
                {
                    await ApplyScheduleAsync(result, cancellationToken);
                    result.Applied = true;
                }

                _logger.LogInformation("Schedule created: {Id} ({Scheduled}/{Total} tasks scheduled)",
                    result.ScheduleId, result.ScheduledTasks.Count, taskList.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling tasks by priority");
                throw new TaskSchedulingException($"Failed to schedule tasks: {ex.Message}", ex);
            }
            finally
            {
                _calculationLock.Release();
            }
        }

        /// <summary>
        /// Balances competing priorities;
        /// </summary>
        public async Task<PriorityBalance> BalancePrioritiesAsync(
            IEnumerable<PriorityConflict> conflicts,
            BalancingStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            if (conflicts == null || !conflicts.Any())
                throw new ArgumentException("Conflicts cannot be null or empty", nameof(conflicts));

            await _calculationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Balancing {Count} priority conflicts using {Strategy}",
                    conflicts.Count(), strategy);

                var conflictList = conflicts.ToList();
                var balanceStart = DateTime.UtcNow;

                // Analyze conflicts;
                var conflictAnalysis = AnalyzeConflicts(conflictList);

                // Select balancing algorithm based on strategy;
                var algorithm = SelectBalancingAlgorithm(strategy, conflictAnalysis);

                // Apply balancing algorithm;
                var balanceResult = await ApplyBalancingAlgorithmAsync(
                    algorithm,
                    conflictList,
                    conflictAnalysis,
                    cancellationToken);

                // Optimize balance if needed;
                if (strategy.Optimize)
                {
                    balanceResult = await OptimizeBalanceAsync(balanceResult, strategy, cancellationToken);
                }

                // Validate balance;
                var balanceValidation = ValidateBalance(balanceResult, strategy);

                // Calculate balance metrics;
                var metrics = CalculateBalanceMetrics(conflictAnalysis, balanceResult);

                var result = new PriorityBalance;
                {
                    BalanceId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - balanceStart,
                    OriginalConflicts = conflictList,
                    ResolutionStrategy = strategy,
                    AppliedAlgorithm = algorithm.GetType().Name,
                    BalanceResult = balanceResult,
                    ResolutionPlan = CreateResolutionPlan(balanceResult),
                    BalanceMetrics = metrics,
                    ValidationResult = balanceValidation,
                    IsBalanced = balanceValidation.IsValid,
                    FairnessScore = metrics.FairnessScore,
                    EfficiencyScore = metrics.EfficiencyScore,
                    StakeholderSatisfaction = metrics.StakeholderSatisfaction,
                    Recommendations = GenerateBalanceRecommendations(balanceResult, metrics)
                };

                // Apply balance if auto-apply is enabled;
                if (strategy.AutoApply && result.IsBalanced)
                {
                    await ApplyBalanceAsync(result, cancellationToken);
                    result.Applied = true;
                }

                _logger.LogInformation("Priority balance completed: {Id} (fairness: {Fairness:P2})",
                    result.BalanceId, result.FairnessScore);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error balancing priorities");
                throw new PriorityBalanceException($"Failed to balance priorities: {ex.Message}", ex);
            }
            finally
            {
                _calculationLock.Release();
            }
        }

        /// <summary>
        /// Predicts priority trends;
        /// </summary>
        public async Task<PriorityPrediction> PredictPriorityTrendAsync(
            string itemId,
            TimeSpan predictionWindow,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(itemId))
                throw new ArgumentException("Item ID cannot be null or empty", nameof(itemId));

            try
            {
                _logger.LogDebug("Predicting priority trend for {ItemId} over {Window}",
                    itemId, predictionWindow);

                // Retrieve historical data;
                var history = await GetPriorityHistoryAsync(itemId, cancellationToken);

                if (history == null || !history.Records.Any())
                {
                    _logger.LogWarning("No historical data found for item {ItemId}", itemId);
                    return new PriorityPrediction;
                    {
                        ItemId = itemId,
                        HasData = false,
                        PredictionTime = DateTime.UtcNow;
                    };
                }

                // Analyze historical patterns;
                var patternAnalysis = AnalyzeHistoricalPatterns(history);

                // Apply prediction model;
                var predictionModel = SelectPredictionModel(patternAnalysis, predictionWindow);
                var trendPrediction = await predictionModel.PredictAsync(history, predictionWindow, cancellationToken);

                // Calculate confidence intervals;
                var confidenceIntervals = CalculateConfidenceIntervals(trendPrediction, patternAnalysis);

                // Identify trend points;
                var trendPoints = IdentifyTrendPoints(trendPrediction, patternAnalysis);

                // Generate predictions;
                var predictions = GenerateTimeBasedPredictions(trendPrediction, predictionWindow);

                // Assess prediction reliability;
                var reliability = AssessPredictionReliability(patternAnalysis, trendPrediction);

                var result = new PriorityPrediction;
                {
                    ItemId = itemId,
                    PredictionId = Guid.NewGuid().ToString(),
                    PredictionTime = DateTime.UtcNow,
                    PredictionWindow = predictionWindow,
                    HistoricalData = history,
                    PatternAnalysis = patternAnalysis,
                    TrendPrediction = trendPrediction,
                    ConfidenceIntervals = confidenceIntervals,
                    TrendPoints = trendPoints,
                    Predictions = predictions,
                    ExpectedPriorityAtEnd = predictions.LastOrDefault()?.PriorityScore ?? 0,
                    TrendDirection = DetermineTrendDirection(trendPoints),
                    ChangeMagnitude = CalculateChangeMagnitude(trendPoints),
                    ReliabilityScore = reliability.Score,
                    ReliabilityFactors = reliability.Factors,
                    IsReliable = reliability.Score >= _options.Value.PredictionReliabilityThreshold,
                    Recommendations = GeneratePredictionRecommendations(trendPrediction, reliability),
                    ModelUsed = predictionModel.GetType().Name,
                    ModelVersion = predictionModel.Version;
                };

                // Store prediction for future validation;
                await StorePriorityPredictionAsync(result, cancellationToken);

                _logger.LogInformation("Priority trend prediction for {ItemId}: {Direction} (reliability: {Reliability:P2})",
                    itemId, result.TrendDirection, result.ReliabilityScore);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error predicting priority trend for {ItemId}", itemId);
                throw new PriorityPredictionException($"Failed to predict priority trend: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Validates priority assignments;
        /// </summary>
        public async Task<PriorityValidation> ValidatePriorityAsync(
            PriorityAssignment assignment,
            ValidationRules rules,
            CancellationToken cancellationToken = default)
        {
            if (assignment == null)
                throw new ArgumentNullException(nameof(assignment));

            if (rules == null)
                rules = _options.Value.DefaultValidationRules;

            try
            {
                _logger.LogDebug("Validating priority assignment for task {TaskId}", assignment.TaskId);

                var validation = new PriorityValidation;
                {
                    AssignmentId = assignment.TaskId,
                    ValidationTime = DateTime.UtcNow,
                    AppliedRules = rules;
                };

                // Apply validation rules;
                foreach (var rule in rules.Rules)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ruleResult = await ApplyValidationRuleAsync(rule, assignment, cancellationToken);
                    validation.RuleResults.Add(ruleResult);

                    if (!ruleResult.IsValid)
                    {
                        validation.Errors.AddRange(ruleResult.Errors);
                        if (rule.Severity == RuleSeverity.Critical)
                        {
                            validation.IsValid = false;
                        }
                    }
                }

                // Check consistency;
                var consistencyCheck = await CheckConsistencyAsync(assignment, cancellationToken);
                validation.ConsistencyCheck = consistencyCheck;
                if (!consistencyCheck.IsConsistent)
                {
                    validation.Errors.AddRange(consistencyCheck.Inconsistencies);
                }

                // Check fairness;
                var fairnessCheck = await CheckFairnessAsync(assignment, cancellationToken);
                validation.FairnessCheck = fairnessCheck;
                if (!fairnessCheck.IsFair)
                {
                    validation.Warnings.AddRange(fairnessCheck.Issues);
                }

                // Calculate validation score;
                validation.ValidationScore = CalculateValidationScore(validation);
                validation.IsValid = validation.IsValid &&
                    validation.ValidationScore >= rules.MinimumValidationScore;

                // Generate recommendations;
                validation.Recommendations = GenerateValidationRecommendations(validation);

                _logger.LogDebug("Priority validation for task {TaskId}: {IsValid} (score: {Score})",
                    assignment.TaskId, validation.IsValid, validation.ValidationScore);

                return validation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating priority assignment for task {TaskId}", assignment.TaskId);
                throw new PriorityValidationException($"Failed to validate priority: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _calculationLock?.Dispose();
            _optimizationTimer?.Stop();
            _optimizationTimer?.Dispose();
            _learningModel?.Dispose();
            _priorityCache?.Dispose();

            UnsubscribeFromEvents();
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private async Task<EvaluationContext> BuildEvaluationContextAsync(
            TaskMetadata task,
            PriorityContext context,
            CancellationToken cancellationToken)
        {
            var evalContext = new EvaluationContext;
            {
                Task = task,
                UserContext = context?.UserContext,
                SystemContext = context?.SystemContext,
                BusinessContext = context?.BusinessContext,
                TimeContext = new TimeContext;
                {
                    CurrentTime = DateTime.UtcNow,
                    TimeOfDay = DateTime.UtcNow.TimeOfDay,
                    DayOfWeek = DateTime.UtcNow.DayOfWeek,
                    IsBusinessHours = IsBusinessHours(DateTime.UtcNow)
                }
            };

            // Load additional context if needed;
            if (context?.LoadAdditionalContext == true)
            {
                evalContext.AdditionalContext = await LoadAdditionalContextAsync(task, cancellationToken);
            }

            // Apply context weighting profile;
            evalContext.WeightingProfile = await DetermineWeightingProfileAsync(evalContext, cancellationToken);

            // Gather context modifiers;
            evalContext.Modifiers = await GatherContextModifiersAsync(evalContext, cancellationToken);

            return evalContext;
        }

        private async Task<PriorityComponents> CalculatePriorityComponentsAsync(
            TaskMetadata task,
            EvaluationContext context,
            CancellationToken cancellationToken)
        {
            var components = new PriorityComponents();

            // Calculate urgency component;
            components.Urgency = await CalculateUrgencyComponentAsync(task, context, cancellationToken);

            // Calculate importance component;
            components.Importance = await CalculateImportanceComponentAsync(task, context, cancellationToken);

            // Calculate effort component (inverse relationship)
            components.Effort = await CalculateEffortComponentAsync(task, context, cancellationToken);

            // Calculate dependency component;
            components.Dependency = await CalculateDependencyComponentAsync(task, context, cancellationToken);

            // Calculate resource component;
            components.Resource = await CalculateResourceComponentAsync(task, context, cancellationToken);

            // Calculate risk component;
            components.Risk = await CalculateRiskComponentAsync(task, context, cancellationToken);

            // Calculate value component;
            components.Value = await CalculateValueComponentAsync(task, context, cancellationToken);

            // Calculate SLA component;
            components.SLA = await CalculateSLAComponentAsync(task, context, cancellationToken);

            // Calculate user component;
            components.User = await CalculateUserComponentAsync(task, context, cancellationToken);

            // Calculate system component;
            components.System = await CalculateSystemComponentAsync(task, context, cancellationToken);

            // Normalize components;
            components = NormalizeComponents(components);

            return components;
        }

        private WeightedPriority ApplyWeighting(PriorityComponents components, WeightingProfile profile)
        {
            var weighted = new WeightedPriority();

            weighted.UrgencyScore = components.Urgency * profile.UrgencyWeight;
            weighted.ImportanceScore = components.Importance * profile.ImportanceWeight;
            weighted.EffortScore = components.Effort * profile.EffortWeight;
            weighted.DependencyScore = components.Dependency * profile.DependencyWeight;
            weighted.ResourceScore = components.Resource * profile.ResourceWeight;
            weighted.RiskScore = components.Risk * profile.RiskWeight;
            weighted.ValueScore = components.Value * profile.ValueWeight;
            weighted.SLAScore = components.SLA * profile.SLAWeight;
            weighted.UserScore = components.User * profile.UserWeight;
            weighted.SystemScore = components.System * profile.SystemWeight;

            weighted.TotalScore = weighted.UrgencyScore + weighted.ImportanceScore + weighted.EffortScore +
                                weighted.DependencyScore + weighted.ResourceScore + weighted.RiskScore +
                                weighted.ValueScore + weighted.SLAScore + weighted.UserScore + weighted.SystemScore;

            weighted.MaxPossibleScore = profile.TotalWeight;
            weighted.NormalizedScore = weighted.TotalScore / weighted.MaxPossibleScore;

            return weighted;
        }

        private ModifiedPriority ApplyModifiers(WeightedPriority weighted, IEnumerable<PriorityModifier> modifiers)
        {
            var modified = new ModifiedPriority;
            {
                BaseScore = weighted.NormalizedScore,
                AppliedModifiers = new List<ModifierApplication>()
            };

            var currentScore = weighted.NormalizedScore;

            foreach (var modifier in modifiers.OrderBy(m => m.ApplicationOrder))
            {
                var application = new ModifierApplication;
                {
                    ModifierId = modifier.Id,
                    ModifierType = modifier.Type,
                    Description = modifier.Description;
                };

                switch (modifier.Operation)
                {
                    case ModifierOperation.Add:
                        currentScore += modifier.Value;
                        application.Operation = "+";
                        application.Value = modifier.Value;
                        break;

                    case ModifierOperation.Multiply:
                        currentScore *= modifier.Value;
                        application.Operation = "×";
                        application.Value = modifier.Value;
                        break;

                    case ModifierOperation.Cap:
                        currentScore = Math.Min(currentScore, modifier.Value);
                        application.Operation = "cap";
                        application.Value = modifier.Value;
                        break;

                    case ModifierOperation.Floor:
                        currentScore = Math.Max(currentScore, modifier.Value);
                        application.Operation = "floor";
                        application.Value = modifier.Value;
                        break;
                }

                application.ResultingScore = currentScore;
                modified.AppliedModifiers.Add(application);
            }

            // Ensure score is within valid range;
            modified.FinalScore = Math.Max(0, Math.Min(1, currentScore));
            modified.ModificationCount = modified.AppliedModifiers.Count;
            modified.EffectiveModification = modified.FinalScore - weighted.NormalizedScore;

            return modified;
        }

        private PriorityLevel DeterminePriorityLevel(float score)
        {
            if (score >= _options.Value.CriticalThreshold) return PriorityLevel.Critical;
            if (score >= _options.Value.HighThreshold) return PriorityLevel.High;
            if (score >= _options.Value.MediumThreshold) return PriorityLevel.Medium;
            if (score >= _options.Value.LowThreshold) return PriorityLevel.Low;
            return PriorityLevel.Minimal;
        }

        private float CalculateConfidence(PriorityComponents components, EvaluationContext context)
        {
            var confidenceFactors = new List<float>();

            // Data completeness factor;
            var completeness = CalculateDataCompleteness(context);
            confidenceFactors.Add(completeness * 0.3f);

            // Component consistency factor;
            var consistency = CalculateComponentConsistency(components);
            confidenceFactors.Add(consistency * 0.3f);

            // Historical accuracy factor;
            var historicalAccuracy = await CalculateHistoricalAccuracyAsync(context.Task.Id);
            confidenceFactors.Add(historicalAccuracy * 0.2f);

            // Model confidence factor;
            var modelConfidence = _learningModel.GetConfidence();
            confidenceFactors.Add(modelConfidence * 0.2f);

            return confidenceFactors.Sum();
        }

        private async Task<IEnumerable<PriorityRecommendation>> GenerateRecommendationsAsync(
            TaskMetadata task,
            PriorityLevel level,
            EvaluationContext context,
            CancellationToken cancellationToken)
        {
            var recommendations = new List<PriorityRecommendation>();

            // Generate level-specific recommendations;
            switch (level)
            {
                case PriorityLevel.Critical:
                    recommendations.Add(new PriorityRecommendation;
                    {
                        Type = RecommendationType.ImmediateAction,
                        Description = "Execute immediately with maximum resources",
                        Impact = "Prevents system failure or major business impact",
                        Effort = "High",
                        Urgency = UrgencyLevel.Critical;
                    });
                    break;

                case PriorityLevel.High:
                    recommendations.Add(new PriorityRecommendation;
                    {
                        Type = RecommendationType.ScheduleSoon,
                        Description = "Schedule within next 2 hours",
                        Impact = "Addresses significant business needs",
                        Effort = "Medium-High",
                        Urgency = UrgencyLevel.High;
                    });
                    break;

                case PriorityLevel.Medium:
                    recommendations.Add(new PriorityRecommendation;
                    {
                        Type = RecommendationType.NormalSchedule,
                        Description = "Schedule within next 24 hours",
                        Impact = "Standard business operation",
                        Effort = "Medium",
                        Urgency = UrgencyLevel.Medium;
                    });
                    break;
            }

            // Add optimization recommendations;
            var optimizationRecs = await GenerateOptimizationRecommendationsAsync(task, context, cancellationToken);
            recommendations.AddRange(optimizationRecs);

            // Add risk mitigation recommendations;
            var riskRecs = await GenerateRiskRecommendationsAsync(task, context, cancellationToken);
            recommendations.AddRange(riskRecs);

            return recommendations.OrderByDescending(r => r.Urgency);
        }

        private async Task RunPeriodicOptimizationAsync()
        {
            try
            {
                _logger.LogDebug("Running periodic priority optimization");

                // Get items needing optimization;
                var itemsNeedingOptimization = await GetItemsNeedingOptimizationAsync();

                if (itemsNeedingOptimization.Any())
                {
                    var constraints = new OptimizationConstraints;
                    {
                        MaxOptimizationTime = TimeSpan.FromSeconds(30),
                        ResourceConstraints = await GetCurrentResourceConstraintsAsync(),
                        OptimizationGoals = _options.Value.PeriodicOptimizationGoals;
                    };

                    var result = await OptimizePrioritiesAsync(itemsNeedingOptimization, constraints);

                    if (result.IsFeasible && result.EstimatedImprovement > 0.01f)
                    {
                        await ApplyOptimizationPlanAsync(result.OptimizationPlan);
                        _logger.LogInformation("Periodic optimization applied: {Improvement:P2} improvement",
                            result.EstimatedImprovement);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic priority optimization");
            }
        }

        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<SystemLoadChangedEvent>(HandleSystemLoadChanged);
            _eventBus.Subscribe<ResourceAvailabilityChangedEvent>(HandleResourceAvailabilityChanged);
            _eventBus.Subscribe<UserPriorityPreferenceChangedEvent>(HandleUserPreferenceChanged);
            _eventBus.Subscribe<BusinessPriorityChangedEvent>(HandleBusinessPriorityChanged);
        }

        private void UnsubscribeFromEvents()
        {
            _eventBus.Unsubscribe<SystemLoadChangedEvent>(HandleSystemLoadChanged);
            _eventBus.Unsubscribe<ResourceAvailabilityChangedEvent>(HandleResourceAvailabilityChanged);
            _eventBus.Unsubscribe<UserPriorityPreferenceChangedEvent>(HandleUserPreferenceChanged);
            _eventBus.Unsubscribe<BusinessPriorityChangedEvent>(HandleBusinessPriorityChanged);
        }

        private async Task HandleSystemLoadChanged(SystemLoadChangedEvent @event)
        {
            try
            {
                _logger.LogInformation("System load changed: {Old} -> {New}", @event.OldLoad, @event.NewLoad);

                // Adjust priorities based on system load;
                var affectedItems = await GetItemsAffectedBySystemLoadAsync(@event.NewLoad);

                if (affectedItems.Any())
                {
                    var request = new PriorityAdjustmentRequest;
                    {
                        Trigger = AdjustmentTrigger.SystemLoadChange,
                        Items = affectedItems,
                        Parameters = new Dictionary<string, object>
                        {
                            ["old_load"] = @event.OldLoad,
                            ["new_load"] = @event.NewLoad,
                            ["change_direction"] = @event.NewLoad > @event.OldLoad ? "increase" : "decrease"
                        }
                    };

                    await AdjustPrioritiesAsync(request);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling system load change");
            }
        }

        private async Task HandleResourceAvailabilityChanged(ResourceAvailabilityChangedEvent @event)
        {
            // Similar implementation for resource changes;
        }

        private async Task HandleUserPreferenceChanged(UserPriorityPreferenceChangedEvent @event)
        {
            // Similar implementation for user preference changes;
        }

        private async Task HandleBusinessPriorityChanged(BusinessPriorityChangedEvent @event)
        {
            // Similar implementation for business priority changes;
        }

        private string GenerateCacheKey(TaskMetadata task, PriorityContext context)
        {
            var keyComponents = new List<string>
            {
                task.Id,
                task.Type,
                task.Category ?? "default",
                context?.UserContext?.UserId ?? "system",
                DateTime.UtcNow.ToString("yyyyMMddHH")
            };

            return string.Join("|", keyComponents);
        }

        private void RecordPriorityHistory(string taskId, PriorityAssignment assignment)
        {
            var history = _priorityHistory.GetOrAdd(taskId, _ => new PriorityHistory { TaskId = taskId });

            var record = new PriorityRecord;
            {
                Timestamp = DateTime.UtcNow,
                PriorityScore = assignment.PriorityScore,
                PriorityLevel = assignment.PriorityLevel,
                Components = assignment.Components,
                ContextHash = assignment.ContextSnapshot?.GetHashCode() ?? 0;
            };

            history.Records.Add(record);

            // Trim history if too large;
            if (history.Records.Count > _options.Value.MaxHistoryRecords)
            {
                history.Records = history.Records;
                    .Skip(history.Records.Count - _options.Value.MaxHistoryRecords)
                    .ToList();
            }
        }

        private async Task PublishPriorityEventAsync(PriorityAssignment assignment, CancellationToken cancellationToken)
        {
            var @event = new PriorityAssignedEvent;
            {
                TaskId = assignment.TaskId,
                TaskType = assignment.TaskType,
                PriorityScore = assignment.PriorityScore,
                PriorityLevel = assignment.PriorityLevel,
                CalculationTime = assignment.CalculationTime,
                Confidence = assignment.Confidence,
                Components = assignment.Components.ToDictionary()
            };

            await _eventBus.PublishAsync(@event, cancellationToken);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public class PriorityEngineOptions;
    {
        public float CriticalThreshold { get; set; } = 0.9f;
        public float HighThreshold { get; set; } = 0.7f;
        public float MediumThreshold { get; set; } = 0.5f;
        public float LowThreshold { get; set; } = 0.3f;
        public float ImmediateActionThreshold { get; set; } = 0.85f;
        public float ExecutiveAttentionThreshold { get; set; } = 0.8f;
        public float PredictionReliabilityThreshold { get; set; } = 0.7f;
        public int MaxHistoryRecords { get; set; } = 1000;
        public int CacheDurationMinutes { get; set; } = 30;
        public int OptimizationIntervalMs { get; set; } = 300000; // 5 minutes;
        public int ImportanceValidityHours { get; set; } = 24;
        public bool AutoApplySchedules { get; set; } = true;
        public ValidationRules DefaultValidationRules { get; set; } = new();
        public OptimizationGoals PeriodicOptimizationGoals { get; set; } = new();
        public CacheSettings CacheSettings { get; set; } = new();
    }

    public class PriorityAssignment;
    {
        public string TaskId { get; set; }
        public string TaskType { get; set; }
        public float PriorityScore { get; set; } // 0-1;
        public PriorityLevel PriorityLevel { get; set; }
        public PriorityComponents Components { get; set; }
        public WeightedPriority WeightedScore { get; set; }
        public ModifiedPriority ModifiedScore { get; set; }
        public DateTime CalculationTime { get; set; }
        public ContextSnapshot ContextSnapshot { get; set; }
        public float Confidence { get; set; }
        public IEnumerable<PriorityRecommendation> Recommendations { get; set; }
        public PriorityValidation ValidationResult { get; set; }
    }

    public class IntentPriority;
    {
        public string IntentId { get; set; }
        public string IntentName { get; set; }
        public string Domain { get; set; }
        public float BasePriority { get; set; }
        public float FinalPriority { get; set; }
        public PriorityLevel PriorityLevel { get; set; }
        public UrgencyLevel UrgencyLevel { get; set; }
        public float UserContextWeight { get; set; }
        public float SystemContextWeight { get; set; }
        public float LearnedAdjustment { get; set; }
        public int ExecutionOrder { get; set; }
        public TimeSpan EstimatedProcessingTime { get; set; }
        public ResourceRequirements ResourceRequirements { get; set; }
        public DateTime AssignmentTime { get; set; }
        public DateTime? ExpiryTime { get; set; }
        public float ConfidenceScore { get; set; }
    }

    public class PriorityAdjustment;
    {
        public string AdjustmentId { get; set; }
        public string ItemId { get; set; }
        public string ItemType { get; set; }
        public float OldPriority { get; set; }
        public float NewPriority { get; set; }
        public float Delta { get; set; }
        public AdjustmentTrigger Trigger { get; set; }
        public string Reason { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public DateTime AdjustmentTime { get; set; }
        public string AppliedBy { get; set; }
        public float Confidence { get; set; }
    }

    public class PriorityOptimizationResult;
    {
        public string OptimizationId { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public PriorityState OriginalState { get; set; }
        public PriorityState OptimizedState { get; set; }
        public OptimizationScenario SelectedScenario { get; set; }
        public IEnumerable<OptimizationScenario> AllScenarios { get; set; }
        public OptimizationPlan OptimizationPlan { get; set; }
        public OptimizationMetrics Metrics { get; set; }
        public OptimizationValidation ValidationResult { get; set; }
        public bool IsFeasible { get; set; }
        public float EstimatedImprovement { get; set; }
        public ResourceImpact ResourceImpact { get; set; }
        public RiskAssessment RiskAssessment { get; set; }
        public bool Applied { get; set; }
        public DateTime? ApplicationTime { get; set; }
    }

    public class UrgencyAssessment;
    {
        public string RequestId { get; set; }
        public string ItemId { get; set; }
        public string ItemType { get; set; }
        public UrgencyComponent TimeUrgency { get; set; }
        public UrgencyComponent ImpactUrgency { get; set; }
        public UrgencyComponent DependencyUrgency { get; set; }
        public UrgencyComponent ContextUrgency { get; set; }
        public CombinedUrgency CombinedUrgency { get; set; }
        public ModifiedUrgency ModifiedUrgency { get; set; }
        public UrgencyLevel UrgencyLevel { get; set; }
        public float UrgencyScore { get; set; }
        public bool IsCritical { get; set; }
        public bool RequiresImmediateAction { get; set; }
        public TimeSpan? EscalationThreshold { get; set; }
        public IEnumerable<UrgencyAction> RecommendedActions { get; set; }
        public DateTime AssessmentTime { get; set; }
        public DateTime? ValidUntil { get; set; }
        public float Confidence { get; set; }
    }

    public class ImportanceEvaluation;
    {
        public string CriteriaId { get; set; }
        public string ItemId { get; set; }
        public ImportanceComponent BusinessValue { get; set; }
        public ImportanceComponent StrategicAlignment { get; set; }
        public ImportanceComponent StakeholderImpact { get; set; }
        public ImportanceComponent ComplianceImportance { get; set; }
        public ImportanceComponent RiskImportance { get; set; }
        public CombinedImportance CombinedImportance { get; set; }
        public ModifiedImportance ModifiedImportance { get; set; }
        public ImportanceCategory ImportanceCategory { get; set; }
        public float ImportanceScore { get; set; }
        public float PriorityWeight { get; set; }
        public bool IsMissionCritical { get; set; }
        public bool RequiresExecutiveAttention { get; set; }
        public PriorityLevel RecommendedPriority { get; set; }
        public ResourceRecommendation ResourceAllocationRecommendation { get; set; }
        public DateTime EvaluationTime { get; set; }
        public TimeSpan ValidFor { get; set; }
        public float Confidence { get; set; }
    }

    public class TaskSchedule;
    {
        public string ScheduleId { get; set; }
        public DateTime CreationTime { get; set; }
        public TimeSpan TimeWindow { get; set; }
        public IEnumerable<ScheduledTask> ScheduledTasks { get; set; }
        public IEnumerable<UnscheduledTask> UnscheduledTasks { get; set; }
        public ResourceUtilization ResourceUtilization { get; set; }
        public PriorityDistribution PriorityDistribution { get; set; }
        public DateTime EstimatedCompletionTime { get; set; }
        public TimeSpan Makespan { get; set; }
        public TimeSpan AverageWaitTime { get; set; }
        public float PriorityEfficiency { get; set; }
        public float ResourceEfficiency { get; set; }
        public bool IsFeasible { get; set; }
        public IEnumerable<string> FeasibilityIssues { get; set; }
        public float OptimizationLevel { get; set; }
        public string ScheduleHash { get; set; }
        public bool Applied { get; set; }
    }

    public class PriorityBalance;
    {
        public string BalanceId { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public IEnumerable<PriorityConflict> OriginalConflicts { get; set; }
        public BalancingStrategy ResolutionStrategy { get; set; }
        public string AppliedAlgorithm { get; set; }
        public BalanceResult BalanceResult { get; set; }
        public ResolutionPlan ResolutionPlan { get; set; }
        public BalanceMetrics BalanceMetrics { get; set; }
        public BalanceValidation ValidationResult { get; set; }
        public bool IsBalanced { get; set; }
        public float FairnessScore { get; set; }
        public float EfficiencyScore { get; set; }
        public float StakeholderSatisfaction { get; set; }
        public IEnumerable<BalanceRecommendation> Recommendations { get; set; }
        public bool Applied { get; set; }
    }

    public class PriorityPrediction;
    {
        public string ItemId { get; set; }
        public string PredictionId { get; set; }
        public DateTime PredictionTime { get; set; }
        public TimeSpan PredictionWindow { get; set; }
        public PriorityHistory HistoricalData { get; set; }
        public PatternAnalysis PatternAnalysis { get; set; }
        public TrendPrediction TrendPrediction { get; set; }
        public ConfidenceIntervals ConfidenceIntervals { get; set; }
        public IEnumerable<TrendPoint> TrendPoints { get; set; }
        public IEnumerable<TimeBasedPrediction> Predictions { get; set; }
        public float ExpectedPriorityAtEnd { get; set; }
        public TrendDirection TrendDirection { get; set; }
        public float ChangeMagnitude { get; set; }
        public float ReliabilityScore { get; set; }
        public ReliabilityFactors ReliabilityFactors { get; set; }
        public bool IsReliable { get; set; }
        public IEnumerable<PredictionRecommendation> Recommendations { get; set; }
        public string ModelUsed { get; set; }
        public string ModelVersion { get; set; }
        public bool HasData { get; set; } = true;
    }

    public class PriorityValidation;
    {
        public string AssignmentId { get; set; }
        public DateTime ValidationTime { get; set; }
        public ValidationRules AppliedRules { get; set; }
        public List<RuleValidationResult> RuleResults { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public ConsistencyCheck ConsistencyCheck { get; set; }
        public FairnessCheck FairnessCheck { get; set; }
        public float ValidationScore { get; set; }
        public bool IsValid { get; set; }
        public IEnumerable<ValidationRecommendation> Recommendations { get; set; }
    }

    public enum PriorityLevel;
    {
        Minimal = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    public enum UrgencyLevel;
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    public enum ImportanceCategory;
    {
        Negligible = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4,
        MissionCritical = 5;
    }

    public enum TrendDirection;
    {
        Decreasing = -1,
        Stable = 0,
        Increasing = 1,
        Volatile = 2;
    }

    public enum AdjustmentTrigger;
    {
        Manual,
        SystemLoadChange,
        ResourceChange,
        DeadlineApproaching,
        DependencyChanged,
        BusinessPriorityChanged,
        UserPreferenceChanged,
        SLAViolationImminent,
        RiskLevelChanged,
        PeriodicReview;
    }

    public enum RecommendationType;
    {
        ImmediateAction,
        ScheduleSoon,
        NormalSchedule,
        Defer,
        Reject,
        SplitTask,
        IncreaseResources,
        DecreaseResources,
        Recalculate,
        Monitor;
    }

    public enum RuleSeverity;
    {
        Information,
        Warning,
        Critical;
    }

    public enum ModifierOperation;
    {
        Add,
        Multiply,
        Cap,
        Floor;
    }

    // Supporting classes (simplified for brevity)
    public class TaskMetadata { }
    public class PriorityContext { }
    public class UserContext { }
    public class SystemContext { }
    public class IntentMetadata { }
    public class PriorityAdjustmentRequest { }
    public class PrioritizableItem { }
    public class OptimizationConstraints { }
    public class UrgencyRequest { }
    public class ImportanceCriteria { }
    public class SchedulableTask { }
    public class ResourceConstraints { }
    public class PriorityConflict { }
    public class BalancingStrategy { }
    public class ValidationRules { }
    public class EvaluationContext { }
    public class PriorityComponents { }
    public class WeightingProfile { }
    public class PriorityModifier { }
    public class WeightedPriority { }
    public class ModifiedPriority { }
    public class ContextSnapshot { }
    public class PriorityRecommendation { }
    public class PriorityHistory { }
    public class PriorityRecord { }
    public class PriorityAssignedEvent { }
    public class SystemLoadChangedEvent { }
    public class ResourceAvailabilityChangedEvent { }
    public class UserPriorityPreferenceChangedEvent { }
    public class BusinessPriorityChangedEvent { }
    public class PriorityLearningModel { }
    public class PriorityCache { }
    public class CacheSettings { }
    public class TimeContext { }
    public class BusinessContext { }
    public class ModifierApplication { }
    public class PrioritizedTask { }
    public class ScheduledTask { }
    public class UnscheduledTask { }
    public class ResourceUtilization { }
    public class PriorityDistribution { }
    public class ScheduleMetrics { }
    public class PriorityState { }
    public class OptimizationScenario { }
    public class OptimizationPlan { }
    public class OptimizationMetrics { }
    public class OptimizationValidation { }
    public class ResourceImpact { }
    public class RiskAssessment { }
    public class UrgencyComponent { }
    public class CombinedUrgency { }
    public class ModifiedUrgency { }
    public class UrgencyAction { }
    public class ImportanceComponent { }
    public class CombinedImportance { }
    public class ModifiedImportance { }
    public class ResourceRecommendation { }
    public class BalanceResult { }
    public class ResolutionPlan { }
    public class BalanceMetrics { }
    public class BalanceValidation { }
    public class BalanceRecommendation { }
    public class PatternAnalysis { }
    public class TrendPrediction { }
    public class ConfidenceIntervals { }
    public class TrendPoint { }
    public class TimeBasedPrediction { }
    public class ReliabilityFactors { }
    public class PredictionRecommendation { }
    public class RuleValidationResult { }
    public class ConsistencyCheck { }
    public class FairnessCheck { }
    public class ValidationRecommendation { }
    public class OptimizationGoals { }
    public class ResourceRequirements { }

    // Exception classes;
    public class PriorityCalculationException : Exception
    {
        public PriorityCalculationException(string message) : base(message) { }
        public PriorityCalculationException(string message, Exception inner) : base(message, inner) { }
    }

    public class IntentPriorityException : Exception
    {
        public IntentPriorityException(string message) : base(message) { }
        public IntentPriorityException(string message, Exception inner) : base(message, inner) { }
    }

    public class PriorityOptimizationException : Exception
    {
        public PriorityOptimizationException(string message) : base(message) { }
        public PriorityOptimizationException(string message, Exception inner) : base(message, inner) { }
    }

    public class UrgencyCalculationException : Exception
    {
        public UrgencyCalculationException(string message) : base(message) { }
        public UrgencyCalculationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ImportanceEvaluationException : Exception
    {
        public ImportanceEvaluationException(string message) : base(message) { }
        public ImportanceEvaluationException(string message, Exception inner) : base(message, inner) { }
    }

    public class TaskSchedulingException : Exception
    {
        public TaskSchedulingException(string message) : base(message) { }
        public TaskSchedulingException(string message, Exception inner) : base(message, inner) { }
    }

    public class PriorityBalanceException : Exception
    {
        public PriorityBalanceException(string message) : base(message) { }
        public PriorityBalanceException(string message, Exception inner) : base(message, inner) { }
    }

    public class PriorityPredictionException : Exception
    {
        public PriorityPredictionException(string message) : base(message) { }
        public PriorityPredictionException(string message, Exception inner) : base(message, inner) { }
    }

    public class PriorityValidationException : Exception
    {
        public PriorityValidationException(string message) : base(message) { }
        public PriorityValidationException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
