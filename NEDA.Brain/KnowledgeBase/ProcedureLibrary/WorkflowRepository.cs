using NEDA.Automation.WorkflowEngine.ActivityLibrary;
using NEDA.Automation.WorkflowEngine.ExecutionEngine;
using NEDA.Brain.KnowledgeBase.CreativePatterns;
using NEDA.Core.Common;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Transactions;

namespace NEDA.Brain.KnowledgeBase.ProcedureLibrary;
{
    /// <summary>
    /// Central repository for workflow definitions, templates, and procedural knowledge;
    /// with versioning, collaboration, and intelligent retrieval capabilities.
    /// </summary>
    public class WorkflowRepository : IWorkflowRepository, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IWorkflowStorage _storage;
        private readonly IWorkflowValidator _validator;
        private readonly IWorkflowAnalyzer _analyzer;
        private readonly WorkflowRepositoryConfiguration _configuration;
        private bool _disposed = false;
        private readonly object _syncLock = new object();

        // Core data structures;
        private readonly Dictionary<string, WorkflowDefinition> _workflowDefinitions;
        private readonly Dictionary<string, WorkflowTemplate> _workflowTemplates;
        private readonly Dictionary<string, Procedure> _procedures;
        private readonly Dictionary<string, WorkflowExecution> _executionHistory;

        // Indexing and search;
        private readonly WorkflowIndex _workflowIndex;
        private readonly TemplateIndex _templateIndex;
        private readonly ProcedureIndex _procedureIndex;
        private readonly WorkflowCache _workflowCache;

        // Version control;
        private readonly WorkflowVersionController _versionController;
        private readonly WorkflowCollaborationEngine _collaborationEngine;

        // Analytics and monitoring;
        private readonly WorkflowMetricsCollector _metricsCollector;
        private readonly WorkflowAuditLogger _auditLogger;
        private readonly WorkflowRepositoryStatistics _statistics;

        /// <summary>
        /// Initializes a new instance of WorkflowRepository;
        /// </summary>
        public WorkflowRepository(
            ILogger logger,
            IWorkflowStorage storage,
            IWorkflowValidator validator,
            IWorkflowAnalyzer analyzer,
            WorkflowRepositoryConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
            _configuration = configuration ?? WorkflowRepositoryConfiguration.Default;

            // Initialize core data structures;
            _workflowDefinitions = new Dictionary<string, WorkflowDefinition>();
            _workflowTemplates = new Dictionary<string, WorkflowTemplate>();
            _procedures = new Dictionary<string, Procedure>();
            _executionHistory = new Dictionary<string, WorkflowExecution>();

            // Initialize indexing and search;
            _workflowIndex = new WorkflowIndex();
            _templateIndex = new TemplateIndex();
            _procedureIndex = new ProcedureIndex();
            _workflowCache = new WorkflowCache(_configuration.CacheConfiguration);

            // Initialize version control and collaboration;
            _versionController = new WorkflowVersionController();
            _collaborationEngine = new WorkflowCollaborationEngine();

            // Initialize analytics;
            _metricsCollector = new WorkflowMetricsCollector();
            _auditLogger = new WorkflowAuditLogger();
            _statistics = new WorkflowRepositoryStatistics();

            // Load workflows from storage;
            LoadWorkflowsFromStorage();
            LoadTemplates();
            LoadProcedures();

            // Build indices;
            BuildIndices();

            _logger.LogInformation($"WorkflowRepository initialized with {_workflowDefinitions.Count} workflows, {_workflowTemplates.Count} templates, {_procedures.Count} procedures");
        }

        /// <summary>
        /// Loads workflows from persistent storage;
        /// </summary>
        private void LoadWorkflowsFromStorage()
        {
            try
            {
                var workflows = _storage.LoadAllWorkflows();
                foreach (var workflow in workflows)
                {
                    _workflowDefinitions[workflow.Id] = workflow;
                }

                _logger.LogDebug($"Loaded {workflows.Count} workflows from storage");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load workflows from storage: {ex.Message}");
                throw new WorkflowRepositoryException("Failed to initialize workflow repository", ex);
            }
        }

        /// <summary>
        /// Loads workflow templates;
        /// </summary>
        private void LoadTemplates()
        {
            try
            {
                // Load built-in templates;
                LoadBuiltInTemplates();

                // Load user templates from storage;
                var userTemplates = _storage.LoadTemplates();
                foreach (var template in userTemplates)
                {
                    _workflowTemplates[template.Id] = template;
                }

                _logger.LogDebug($"Loaded {_workflowTemplates.Count} workflow templates");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load templates: {ex.Message}");
                // Continue without templates;
            }
        }

        /// <summary>
        /// Loads built-in workflow templates;
        /// </summary>
        private void LoadBuiltInTemplates()
        {
            // Data Processing Template;
            AddTemplate(new WorkflowTemplate;
            {
                Id = "template_data_processing",
                Name = "Data Processing Pipeline",
                Description = "Standard template for ETL (Extract, Transform, Load) workflows",
                Category = WorkflowCategory.DataProcessing,
                Complexity = WorkflowComplexity.Medium,
                Structure = new WorkflowStructure;
                {
                    Activities = new List<ActivityDefinition>
                    {
                        new ActivityDefinition { Type = "Extract", Name = "Extract Data", Order = 1 },
                        new ActivityDefinition { Type = "Validate", Name = "Validate Data", Order = 2 },
                        new ActivityDefinition { Type = "Transform", Name = "Transform Data", Order = 3 },
                        new ActivityDefinition { Type = "Load", Name = "Load Data", Order = 4 },
                        new ActivityDefinition { Type = "Notify", Name = "Send Completion Notification", Order = 5 }
                    },
                    Transitions = new List<TransitionDefinition>
                    {
                        new TransitionDefinition { From = 1, To = 2, Condition = "OnSuccess" },
                        new TransitionDefinition { From = 2, To = 3, Condition = "ValidationPassed" },
                        new TransitionDefinition { From = 3, To = 4, Condition = "OnSuccess" },
                        new TransitionDefinition { From = 4, To = 5, Condition = "OnSuccess" }
                    }
                },
                Parameters = new Dictionary<string, TemplateParameter>
                {
                    { "DataSource", new TemplateParameter { Type = "string", Required = true, Description = "Data source connection string" } },
                    { "Destination", new TemplateParameter { Type = "string", Required = true, Description = "Destination connection string" } }
                },
                Tags = new List<string> { "etl", "data", "pipeline", "processing" },
                UsageCount = 0;
            });

            // Approval Workflow Template;
            AddTemplate(new WorkflowTemplate;
            {
                Id = "template_approval",
                Name = "Approval Workflow",
                Description = "Multi-level approval process with escalation",
                Category = WorkflowCategory.BusinessProcess,
                Complexity = WorkflowComplexity.Medium,
                Structure = new WorkflowStructure;
                {
                    Activities = new List<ActivityDefinition>
                    {
                        new ActivityDefinition { Type = "Submit", Name = "Submit Request", Order = 1 },
                        new ActivityDefinition { Type = "Review", Name = "Manager Review", Order = 2 },
                        new ActivityDefinition { Type = "Approve", Name = "Director Approval", Order = 3 },
                        new ActivityDefinition { Type = "Finalize", Name = "Finalize Request", Order = 4 }
                    },
                    Transitions = new List<TransitionDefinition>
                    {
                        new TransitionDefinition { From = 1, To = 2, Condition = "OnSuccess" },
                        new TransitionDefinition { From = 2, To = 3, Condition = "Approved" },
                        new TransitionDefinition { From = 2, To = 1, Condition = "Rejected" },
                        new TransitionDefinition { From = 3, To = 4, Condition = "Approved" },
                        new TransitionDefinition { From = 3, To = 2, Condition = "Rejected" }
                    }
                },
                Tags = new List<string> { "approval", "business", "process", "review" }
            });

            // CI/CD Pipeline Template;
            AddTemplate(new WorkflowTemplate;
            {
                Id = "template_ci_cd",
                Name = "CI/CD Pipeline",
                Description = "Continuous Integration and Deployment pipeline",
                Category = WorkflowCategory.DevOps,
                Complexity = WorkflowComplexity.High,
                Tags = new List<string> { "ci", "cd", "pipeline", "devops", "automation" }
            });

            // Machine Learning Pipeline Template;
            AddTemplate(new WorkflowTemplate;
            {
                Id = "template_ml_pipeline",
                Name = "ML Training Pipeline",
                Description = "End-to-end machine learning model training pipeline",
                Category = WorkflowCategory.MachineLearning,
                Complexity = WorkflowComplexity.High,
                Tags = new List<string> { "machine-learning", "ai", "training", "pipeline" }
            });

            _logger.LogDebug("Built-in workflow templates loaded");
        }

        /// <summary>
        /// Loads procedural knowledge;
        /// </summary>
        private void LoadProcedures()
        {
            try
            {
                // Load standard operating procedures;
                LoadStandardProcedures();

                // Load best practices;
                LoadBestPractices();

                _logger.LogDebug($"Loaded {_procedures.Count} procedures");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load procedures: {ex.Message}");
                // Continue without procedures;
            }
        }

        /// <summary>
        /// Loads standard operating procedures;
        /// </summary>
        private void LoadStandardProcedures()
        {
            // Code Review Procedure;
            AddProcedure(new Procedure;
            {
                Id = "procedure_code_review",
                Name = "Code Review Process",
                Description = "Standard procedure for conducting code reviews",
                Category = ProcedureCategory.Development,
                Steps = new List<ProcedureStep>
                {
                    new ProcedureStep;
                    {
                        Order = 1,
                        Action = "Prepare Review",
                        Description = "Ensure code is ready for review with proper comments and tests",
                        EstimatedTime = TimeSpan.FromMinutes(15)
                    },
                    new ProcedureStep;
                    {
                        Order = 2,
                        Action = "Conduct Review",
                        Description = "Review code for quality, standards, and potential issues",
                        EstimatedTime = TimeSpan.FromMinutes(30)
                    },
                    new ProcedureStep;
                    {
                        Order = 3,
                        Action = "Provide Feedback",
                        Description = "Document findings and suggestions for improvement",
                        EstimatedTime = TimeSpan.FromMinutes(15)
                    },
                    new ProcedureStep;
                    {
                        Order = 4,
                        Action = "Address Feedback",
                        Description = "Implement required changes based on review feedback",
                        EstimatedTime = TimeSpan.FromHours(1)
                    },
                    new ProcedureStep;
                    {
                        Order = 5,
                        Action = "Final Approval",
                        Description = "Obtain final approval before merging",
                        EstimatedTime = TimeSpan.FromMinutes(5)
                    }
                },
                Tags = new List<string> { "code-review", "development", "quality", "process" },
                SuccessCriteria = new List<string>
                {
                    "All critical issues addressed",
                    "Code follows team standards",
                    "Tests pass successfully",
                    "Documentation updated"
                }
            });

            // Incident Response Procedure;
            AddProcedure(new Procedure;
            {
                Id = "procedure_incident_response",
                Name = "Incident Response",
                Description = "Procedure for handling system incidents and outages",
                Category = ProcedureCategory.Operations,
                Tags = new List<string> { "incident", "response", "operations", "recovery" }
            });

            // Deployment Procedure;
            AddProcedure(new Procedure;
            {
                Id = "procedure_deployment",
                Name = "Production Deployment",
                Description = "Standard procedure for deploying to production environment",
                Category = ProcedureCategory.DevOps,
                Tags = new List<string> { "deployment", "production", "devops", "release" }
            });
        }

        /// <summary>
        /// Loads best practices;
        /// </summary>
        private void LoadBestPractices()
        {
            AddProcedure(new Procedure;
            {
                Id = "best_practice_error_handling",
                Name = "Error Handling Best Practices",
                Description = "Recommended practices for robust error handling in workflows",
                Category = ProcedureCategory.BestPractice,
                Tags = new List<string> { "error-handling", "best-practice", "reliability", "robustness" }
            });

            AddProcedure(new Procedure;
            {
                Id = "best_practice_performance",
                Name = "Performance Optimization",
                Description = "Best practices for optimizing workflow performance",
                Category = ProcedureCategory.BestPractice,
                Tags = new List<string> { "performance", "optimization", "best-practice", "efficiency" }
            });
        }

        /// <summary>
        /// Builds search indices;
        /// </summary>
        private void BuildIndices()
        {
            foreach (var workflow in _workflowDefinitions.Values)
            {
                _workflowIndex.AddWorkflow(workflow);
            }

            foreach (var template in _workflowTemplates.Values)
            {
                _templateIndex.AddTemplate(template);
            }

            foreach (var procedure in _procedures.Values)
            {
                _procedureIndex.AddProcedure(procedure);
            }

            _logger.LogDebug($"Indices built: {_workflowIndex.Count} workflows, {_templateIndex.Count} templates, {_procedureIndex.Count} procedures");
        }

        /// <summary>
        /// Searches for workflows matching criteria;
        /// </summary>
        public WorkflowSearchResult SearchWorkflows(WorkflowSearchQuery query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Searching workflows: {query.SearchText}");

                    // Check cache first;
                    var cacheKey = query.GetCacheKey();
                    if (_workflowCache.TryGetSearchResult(cacheKey, out var cachedResult))
                    {
                        _logger.LogDebug($"Cache hit for workflow search: {query.SearchText}");
                        _metricsCollector.RecordCacheHit();
                        return cachedResult;
                    }

                    var startTime = DateTime.UtcNow;

                    // Search using multiple strategies;
                    var searchResults = new List<WorkflowMatch>();

                    // 1. Full-text search;
                    if (!string.IsNullOrWhiteSpace(query.SearchText))
                    {
                        var textMatches = _workflowIndex.SearchByText(query.SearchText);
                        searchResults.AddRange(textMatches);
                    }

                    // 2. Category filter;
                    if (query.Categories != null && query.Categories.Any())
                    {
                        var categoryMatches = _workflowIndex.SearchByCategory(query.Categories);
                        searchResults.AddRange(categoryMatches);
                    }

                    // 3. Tag filter;
                    if (query.Tags != null && query.Tags.Any())
                    {
                        var tagMatches = _workflowIndex.SearchByTags(query.Tags);
                        searchResults.AddRange(tagMatches);
                    }

                    // 4. Complexity filter;
                    if (query.MinComplexity.HasValue || query.MaxComplexity.HasValue)
                    {
                        var complexityMatches = _workflowIndex.SearchByComplexity(
                            query.MinComplexity,
                            query.MaxComplexity);
                        searchResults.AddRange(complexityMatches);
                    }

                    // Combine and deduplicate results;
                    var combinedResults = CombineSearchResults(searchResults);

                    // Score and rank results;
                    var scoredResults = ScoreWorkflows(combinedResults, query);

                    // Apply filters;
                    var filteredResults = ApplyWorkflowFilters(scoredResults, query);

                    // Sort results;
                    var sortedResults = SortWorkflowResults(filteredResults, query.SortBy);

                    // Paginate results;
                    var paginatedResults = PaginateResults(sortedResults, query);

                    var searchResult = new WorkflowSearchResult;
                    {
                        Query = query,
                        Matches = paginatedResults,
                        TotalCount = filteredResults.Count,
                        SearchDuration = DateTime.UtcNow - startTime,
                        SearchId = Guid.NewGuid().ToString(),
                        Suggestions = GenerateSearchSuggestions(query, filteredResults)
                    };

                    // Cache the result;
                    _workflowCache.CacheSearchResult(cacheKey, searchResult);

                    // Update metrics;
                    _metricsCollector.RecordSearch(searchResult);

                    // Audit log;
                    _auditLogger.LogSearch(query, searchResult);

                    _logger.LogInformation($"Workflow search completed: {filteredResults.Count} matches found");

                    return searchResult;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Workflow search failed: {ex.Message}");
                    throw new WorkflowSearchException($"Search failed: {query.SearchText}", ex);
                }
            }
        }

        /// <summary>
        /// Gets a workflow definition by ID;
        /// </summary>
        public WorkflowDefinition GetWorkflow(string workflowId, bool includeVersions = false)
        {
            if (string.IsNullOrWhiteSpace(workflowId))
                throw new ArgumentException("Workflow ID cannot be null or empty", nameof(workflowId));

            lock (_syncLock)
            {
                if (_workflowDefinitions.TryGetValue(workflowId, out var workflow))
                {
                    // Record access;
                    RecordWorkflowAccess(workflowId, WorkflowAccessType.Retrieval);

                    // Increment view count;
                    workflow.ViewCount++;
                    workflow.LastAccessed = DateTime.UtcNow;

                    // Load versions if requested;
                    if (includeVersions)
                    {
                        workflow.Versions = _versionController.GetWorkflowVersions(workflowId);
                    }

                    _logger.LogDebug($"Retrieved workflow: {workflowId}");

                    return workflow;
                }

                throw new WorkflowNotFoundException($"Workflow not found: {workflowId}");
            }
        }

        /// <summary>
        /// Creates a new workflow from a template;
        /// </summary>
        public WorkflowDefinition CreateWorkflowFromTemplate(string templateId, CreateWorkflowRequest request)
        {
            if (string.IsNullOrWhiteSpace(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Creating workflow from template: {templateId}");

                    // Get template;
                    if (!_workflowTemplates.TryGetValue(templateId, out var template))
                        throw new TemplateNotFoundException($"Template not found: {templateId}");

                    // Validate request parameters;
                    ValidateTemplateParameters(template, request.Parameters);

                    // Create workflow from template;
                    var workflow = InstantiateTemplate(template, request);

                    // Validate workflow;
                    var validationResult = _validator.Validate(workflow);
                    if (!validationResult.IsValid)
                    {
                        throw new WorkflowValidationException($"Workflow validation failed: {string.Join(", ", validationResult.Errors)}");
                    }

                    // Analyze workflow;
                    var analysis = _analyzer.Analyze(workflow);
                    workflow.Analysis = analysis;

                    // Generate unique ID;
                    workflow.Id = GenerateWorkflowId(workflow.Name);

                    // Set metadata;
                    workflow.CreatedBy = request.CreatedBy;
                    workflow.CreatedAt = DateTime.UtcNow;
                    workflow.UpdatedAt = DateTime.UtcNow;
                    workflow.Version = 1;

                    // Add to repository;
                    AddWorkflow(workflow);

                    // Store in persistent storage;
                    _storage.SaveWorkflow(workflow);

                    // Update template usage;
                    template.UsageCount++;
                    template.LastUsed = DateTime.UtcNow;

                    // Record audit;
                    _auditLogger.LogWorkflowCreation(workflow, templateId, request);

                    _logger.LogInformation($"Workflow created: {workflow.Id} from template: {templateId}");

                    return workflow;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Workflow creation failed: {ex.Message}");
                    throw new WorkflowCreationException($"Failed to create workflow from template: {templateId}", ex);
                }
            }
        }

        /// <summary>
        /// Saves a workflow definition;
        /// </summary>
        public WorkflowDefinition SaveWorkflow(WorkflowDefinition workflow, SaveOptions options = null)
        {
            if (workflow == null)
                throw new ArgumentNullException(nameof(workflow));

            options ??= new SaveOptions();

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Saving workflow: {workflow.Name}");

                    // Validate workflow;
                    var validationResult = _validator.Validate(workflow);
                    if (!validationResult.IsValid && options.RequireValidation)
                    {
                        throw new WorkflowValidationException($"Workflow validation failed: {string.Join(", ", validationResult.Errors)}");
                    }

                    // Analyze workflow;
                    var analysis = _analyzer.Analyze(workflow);
                    workflow.Analysis = analysis;

                    // Check if workflow exists;
                    bool isNew = string.IsNullOrWhiteSpace(workflow.Id) || !_workflowDefinitions.ContainsKey(workflow.Id);

                    if (isNew)
                    {
                        // Generate ID for new workflow;
                        workflow.Id = GenerateWorkflowId(workflow.Name);
                        workflow.CreatedAt = DateTime.UtcNow;
                        workflow.Version = 1;

                        // Add to repository;
                        AddWorkflow(workflow);

                        _logger.LogDebug($"Created new workflow: {workflow.Id}");
                    }
                    else;
                    {
                        // Create version snapshot before update;
                        if (options.CreateVersion)
                        {
                            var previousVersion = _workflowDefinitions[workflow.Id];
                            _versionController.CreateVersion(previousVersion);
                        }

                        // Update existing workflow;
                        UpdateWorkflow(workflow);

                        _logger.LogDebug($"Updated existing workflow: {workflow.Id}");
                    }

                    // Update metadata;
                    workflow.UpdatedAt = DateTime.UtcNow;
                    workflow.UpdatedBy = options.UpdatedBy;

                    if (!isNew)
                    {
                        workflow.Version++;
                    }

                    // Store in persistent storage;
                    _storage.SaveWorkflow(workflow);

                    // Record audit;
                    _auditLogger.LogWorkflowSave(workflow, isNew, options);

                    _logger.LogInformation($"Workflow saved: {workflow.Id} (v{workflow.Version})");

                    return workflow;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Workflow save failed: {ex.Message}");
                    throw new WorkflowSaveException($"Failed to save workflow: {workflow.Name}", ex);
                }
            }
        }

        /// <summary>
        /// Executes a workflow;
        /// </summary>
        public WorkflowExecution ExecuteWorkflow(string workflowId, ExecutionRequest request)
        {
            if (string.IsNullOrWhiteSpace(workflowId))
                throw new ArgumentException("Workflow ID cannot be null or empty", nameof(workflowId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Executing workflow: {workflowId}");

                    // Get workflow definition;
                    var workflow = GetWorkflow(workflowId);

                    // Create execution instance;
                    var execution = new WorkflowExecution;
                    {
                        ExecutionId = Guid.NewGuid().ToString(),
                        WorkflowId = workflowId,
                        WorkflowVersion = workflow.Version,
                        Status = ExecutionStatus.Pending,
                        StartedBy = request.StartedBy,
                        StartedAt = DateTime.UtcNow,
                        InputParameters = request.Parameters,
                        Context = request.Context;
                    };

                    // Store execution record;
                    _executionHistory[execution.ExecutionId] = execution;

                    // Validate execution prerequisites;
                    var validationResult = ValidateExecutionPrerequisites(workflow, request);
                    if (!validationResult.IsValid)
                    {
                        execution.Status = ExecutionStatus.Failed;
                        execution.Error = $"Prerequisite validation failed: {string.Join(", ", validationResult.Errors)}";
                        execution.CompletedAt = DateTime.UtcNow;

                        _logger.LogWarning($"Workflow execution failed validation: {workflowId}");
                        return execution;
                    }

                    // Start execution (in background)
                    execution.Status = ExecutionStatus.Running;

                    // In production, this would delegate to a workflow engine;
                    // For now, we'll simulate execution;
                    SimulateWorkflowExecution(execution, workflow);

                    // Record execution metrics;
                    workflow.ExecutionCount++;
                    workflow.LastExecuted = DateTime.UtcNow;

                    // Update workflow success rate;
                    UpdateWorkflowSuccessRate(workflow, execution);

                    // Store execution history;
                    _storage.SaveExecution(execution);

                    // Record audit;
                    _auditLogger.LogWorkflowExecution(execution);

                    _logger.LogInformation($"Workflow execution started: {workflowId} => {execution.ExecutionId}");

                    return execution;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Workflow execution failed: {ex.Message}");
                    throw new WorkflowExecutionException($"Failed to execute workflow: {workflowId}", ex);
                }
            }
        }

        /// <summary>
        /// Gets execution history for a workflow;
        /// </summary>
        public List<WorkflowExecution> GetExecutionHistory(string workflowId, TimePeriod period = null)
        {
            if (string.IsNullOrWhiteSpace(workflowId))
                throw new ArgumentException("Workflow ID cannot be null or empty", nameof(workflowId));

            period ??= TimePeriod.AllTime;

            lock (_syncLock)
            {
                var executions = _executionHistory.Values;
                    .Where(e => e.WorkflowId == workflowId)
                    .Where(e => IsInPeriod(e.StartedAt, period))
                    .OrderByDescending(e => e.StartedAt)
                    .ToList();

                return executions;
            }
        }

        /// <summary>
        /// Gets workflow templates by category;
        /// </summary>
        public List<WorkflowTemplate> GetTemplates(WorkflowCategory? category = null, int maxResults = 20)
        {
            lock (_syncLock)
            {
                var templates = _workflowTemplates.Values;

                if (category.HasValue)
                {
                    templates = templates.Where(t => t.Category == category.Value);
                }

                return templates;
                    .OrderByDescending(t => t.UsageCount)
                    .ThenByDescending(t => t.LastUsed ?? DateTime.MinValue)
                    .Take(maxResults)
                    .ToList();
            }
        }

        /// <summary>
        /// Gets procedures by category;
        /// </summary>
        public List<Procedure> GetProcedures(ProcedureCategory? category = null, int maxResults = 20)
        {
            lock (_syncLock)
            {
                var procedures = _procedures.Values;

                if (category.HasValue)
                {
                    procedures = procedures.Where(p => p.Category == category.Value);
                }

                return procedures;
                    .OrderByDescending(p => p.UsageCount)
                    .Take(maxResults)
                    .ToList();
            }
        }

        /// <summary>
        /// Recommends workflows based on context;
        /// </summary>
        public List<WorkflowRecommendation> RecommendWorkflows(RecommendationContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogDebug($"Generating workflow recommendations for context: {context.Description}");

                    var recommendations = new List<WorkflowRecommendation>();

                    // Analyze context;
                    var contextAnalysis = AnalyzeRecommendationContext(context);

                    // Find matching workflows;
                    var matchingWorkflows = FindMatchingWorkflows(contextAnalysis);

                    // Score workflows;
                    foreach (var workflow in matchingWorkflows)
                    {
                        var score = CalculateRecommendationScore(workflow, contextAnalysis);
                        var reason = GenerateRecommendationReason(workflow, contextAnalysis);

                        recommendations.Add(new WorkflowRecommendation;
                        {
                            Workflow = workflow,
                            Score = score,
                            Reason = reason,
                            Confidence = CalculateConfidence(workflow, contextAnalysis),
                            ContextMatch = CalculateContextMatch(workflow, contextAnalysis)
                        });
                    }

                    // Sort by score;
                    recommendations = recommendations;
                        .OrderByDescending(r => r.Score)
                        .ThenByDescending(r => r.Confidence)
                        .Take(context.MaxRecommendations)
                        .ToList();

                    _logger.LogInformation($"Generated {recommendations.Count} workflow recommendations");

                    return recommendations;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Workflow recommendation failed: {ex.Message}");
                    return new List<WorkflowRecommendation>();
                }
            }
        }

        /// <summary>
        /// Exports workflows in specified format;
        /// </summary>
        public string ExportWorkflows(ExportFormat format, ExportOptions options = null)
        {
            lock (_syncLock)
            {
                options ??= ExportOptions.Default;

                try
                {
                    _logger.LogInformation($"Exporting workflows in {format} format");

                    switch (format)
                    {
                        case ExportFormat.JSON:
                            return ExportToJson(options);

                        case ExportFormat.XML:
                            return ExportToXml(options);

                        case ExportFormat.YAML:
                            return ExportToYaml(options);

                        case ExportFormat.Markdown:
                            return ExportToMarkdown(options);

                        default:
                            throw new NotSupportedException($"Export format not supported: {format}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Workflow export failed: {ex.Message}");
                    throw new WorkflowExportException($"Failed to export workflows in {format} format", ex);
                }
            }
        }

        /// <summary>
        /// Imports workflows from file;
        /// </summary>
        public ImportResult ImportWorkflows(string content, ImportFormat format, ImportOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Import content cannot be null or empty", nameof(content));

            options ??= new ImportOptions();

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Importing workflows in {format} format");

                    var result = new ImportResult;
                    {
                        ImportId = Guid.NewGuid().ToString(),
                        StartedAt = DateTime.UtcNow,
                        Format = format;
                    };

                    List<WorkflowDefinition> workflows;

                    // Parse based on format;
                    switch (format)
                    {
                        case ImportFormat.JSON:
                            workflows = ParseJsonImport(content, options);
                            break;

                        case ImportFormat.XML:
                            workflows = ParseXmlImport(content, options);
                            break;

                        case ImportFormat.YAML:
                            workflows = ParseYamlImport(content, options);
                            break;

                        default:
                            throw new NotSupportedException($"Import format not supported: {format}");
                    }

                    // Process each workflow;
                    foreach (var workflow in workflows)
                    {
                        try
                        {
                            if (options.OverwriteExisting || !_workflowDefinitions.ContainsKey(workflow.Id))
                            {
                                // Validate workflow;
                                var validationResult = _validator.Validate(workflow);
                                if (!validationResult.IsValid && options.SkipInvalid)
                                {
                                    result.Skipped++;
                                    result.Errors.Add($"Workflow {workflow.Name}: Validation failed - {string.Join(", ", validationResult.Errors)}");
                                    continue;
                                }

                                // Save workflow;
                                SaveWorkflow(workflow, new SaveOptions;
                                {
                                    CreateVersion = true,
                                    UpdatedBy = options.ImportedBy,
                                    RequireValidation = !options.SkipInvalid;
                                });

                                result.Imported++;
                            }
                            else;
                            {
                                result.Skipped++;
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Failed++;
                            result.Errors.Add($"Workflow {workflow.Name}: {ex.Message}");
                        }
                    }

                    result.CompletedAt = DateTime.UtcNow;
                    result.Success = result.Failed == 0;

                    _logger.LogInformation($"Import completed: {result.Imported} imported, {result.Skipped} skipped, {result.Failed} failed");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Workflow import failed: {ex.Message}");
                    throw new WorkflowImportException($"Failed to import workflows", ex);
                }
            }
        }

        /// <summary>
        /// Gets repository statistics;
        /// </summary>
        public WorkflowRepositoryStatistics GetStatistics()
        {
            lock (_syncLock)
            {
                // Update statistics with current data;
                _statistics.TotalWorkflows = _workflowDefinitions.Count;
                _statistics.TotalTemplates = _workflowTemplates.Count;
                _statistics.TotalProcedures = _procedures.Count;
                _statistics.TotalExecutions = _executionHistory.Count;
                _statistics.ActiveWorkflows = _workflowDefinitions.Values.Count(w => w.IsActive);
                _statistics.LastUpdated = DateTime.UtcNow;

                return _statistics.Clone();
            }
        }

        /// <summary>
        /// Clears all caches;
        /// </summary>
        public void ClearCaches()
        {
            lock (_syncLock)
            {
                _workflowCache.Clear();
                _logger.LogInformation("Workflow caches cleared");
            }
        }

        #region Private Helper Methods;

        private void AddTemplate(WorkflowTemplate template)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
                template.Id = GenerateTemplateId(template.Name);

            _workflowTemplates[template.Id] = template;
            _templateIndex.AddTemplate(template);
        }

        private void AddProcedure(Procedure procedure)
        {
            if (string.IsNullOrWhiteSpace(procedure.Id))
                procedure.Id = GenerateProcedureId(procedure.Name);

            _procedures[procedure.Id] = procedure;
            _procedureIndex.AddProcedure(procedure);
        }

        private void AddWorkflow(WorkflowDefinition workflow)
        {
            _workflowDefinitions[workflow.Id] = workflow;
            _workflowIndex.AddWorkflow(workflow);
            _statistics.TotalWorkflows++;
        }

        private void UpdateWorkflow(WorkflowDefinition workflow)
        {
            _workflowDefinitions[workflow.Id] = workflow;
            _workflowIndex.UpdateWorkflow(workflow);
        }

        private string GenerateWorkflowId(string workflowName)
        {
            var baseId = workflowName.ToLower()
                .Replace(" ", "_")
                .Replace("-", "_")
                .Replace(".", "_");

            var suffix = 1;
            var candidateId = $"wf_{baseId}";

            while (_workflowDefinitions.ContainsKey(candidateId))
            {
                suffix++;
                candidateId = $"wf_{baseId}_{suffix}";
            }

            return candidateId;
        }

        private string GenerateTemplateId(string templateName)
        {
            var baseId = templateName.ToLower()
                .Replace(" ", "_")
                .Replace("-", "_");

            return $"template_{baseId}";
        }

        private string GenerateProcedureId(string procedureName)
        {
            var baseId = procedureName.ToLower()
                .Replace(" ", "_")
                .Replace("-", "_");

            return $"procedure_{baseId}";
        }

        private List<WorkflowMatch> CombineSearchResults(List<WorkflowMatch> results)
        {
            var combined = new Dictionary<string, WorkflowMatch>();

            foreach (var match in results)
            {
                var workflowId = match.Workflow.Id;

                if (combined.TryGetValue(workflowId, out var existing))
                {
                    // Merge match strategies;
                    existing.MatchStrategies.Add(match.MatchStrategy);
                    existing.Score = Math.Max(existing.Score, match.Score);
                }
                else;
                {
                    match.MatchStrategies = new List<MatchStrategy> { match.MatchStrategy };
                    combined[workflowId] = match;
                }
            }

            return combined.Values.ToList();
        }

        private List<WorkflowMatch> ScoreWorkflows(List<WorkflowMatch> matches, WorkflowSearchQuery query)
        {
            foreach (var match in matches)
            {
                var relevanceScore = CalculateRelevanceScore(match.Workflow, query);
                var qualityScore = CalculateQualityScore(match.Workflow);

                match.RelevanceScore = relevanceScore;
                match.QualityScore = qualityScore;
                match.OverallScore = (relevanceScore * 0.6) + (qualityScore * 0.4);
            }

            return matches;
        }

        private double CalculateRelevanceScore(WorkflowDefinition workflow, WorkflowSearchQuery query)
        {
            double score = 0.0;

            // Text match;
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var textMatchScore = CalculateTextMatchScore(workflow, query.SearchText);
                score += textMatchScore * 0.4;
            }

            // Category match;
            if (query.Categories != null && query.Categories.Contains(workflow.Category))
            {
                score += 0.3;
            }

            // Tag match;
            if (query.Tags != null && workflow.Tags != null)
            {
                var tagMatches = workflow.Tags.Count(t => query.Tags.Contains(t));
                score += (tagMatches / (double)Math.Max(query.Tags.Count, 1)) * 0.2;
            }

            // Complexity match;
            if (query.MinComplexity.HasValue && workflow.Complexity >= query.MinComplexity.Value)
            {
                score += 0.05;
            }
            if (query.MaxComplexity.HasValue && workflow.Complexity <= query.MaxComplexity.Value)
            {
                score += 0.05;
            }

            return Math.Min(score, 1.0);
        }

        private double CalculateQualityScore(WorkflowDefinition workflow)
        {
            double score = 0.0;

            // Success rate;
            score += workflow.SuccessRate * 0.4;

            // Popularity;
            score += Math.Log(workflow.ExecutionCount + 1) * 0.3;

            // Recent usage;
            if (workflow.LastExecuted.HasValue)
            {
                var daysSinceExecution = (DateTime.UtcNow - workflow.LastExecuted.Value).TotalDays;
                score += (1.0 / (daysSinceExecution + 1)) * 0.2;
            }

            // Completeness;
            if (workflow.Analysis != null)
            {
                score += workflow.Analysis.CompletenessScore * 0.1;
            }

            return Math.Min(score, 1.0);
        }

        private double CalculateTextMatchScore(WorkflowDefinition workflow, string searchText)
        {
            var searchTerms = searchText.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var workflowText = (workflow.Name + " " + workflow.Description).ToLower();

            int matches = 0;
            foreach (var term in searchTerms)
            {
                if (workflowText.Contains(term))
                    matches++;
            }

            return matches / (double)searchTerms.Length;
        }

        private List<WorkflowMatch> ApplyWorkflowFilters(List<WorkflowMatch> matches, WorkflowSearchQuery query)
        {
            var filtered = matches;

            // Filter by status;
            if (query.Status.HasValue)
            {
                filtered = filtered.Where(m => m.Workflow.Status == query.Status.Value).ToList();
            }

            // Filter by activity;
            if (!string.IsNullOrWhiteSpace(query.ActivityType))
            {
                filtered = filtered.Where(m =>
                    m.Workflow.Activities.Any(a => a.Type == query.ActivityType)).ToList();
            }

            return filtered;
        }

        private List<WorkflowMatch> SortWorkflowResults(List<WorkflowMatch> matches, SortOption sortBy)
        {
            return sortBy switch;
            {
                SortOption.Relevance => matches.OrderByDescending(m => m.OverallScore).ToList(),
                SortOption.Name => matches.OrderBy(m => m.Workflow.Name).ToList(),
                SortOption.Recent => matches.OrderByDescending(m => m.Workflow.UpdatedAt).ToList(),
                SortOption.Popular => matches.OrderByDescending(m => m.Workflow.ExecutionCount).ToList(),
                SortOption.Success => matches.OrderByDescending(m => m.Workflow.SuccessRate).ToList(),
                _ => matches.OrderByDescending(m => m.OverallScore).ToList()
            };
        }

        private List<WorkflowMatch> PaginateResults(List<WorkflowMatch> matches, WorkflowSearchQuery query)
        {
            return matches;
                .Skip(query.Skip)
                .Take(query.Take)
                .ToList();
        }

        private List<string> GenerateSearchSuggestions(WorkflowSearchQuery query, List<WorkflowMatch> results)
        {
            var suggestions = new List<string>();

            if (results.Count == 0)
            {
                suggestions.Add("Try broadening your search terms");
                suggestions.Add("Check spelling or use different keywords");
            }

            // Suggest related categories;
            var topCategories = results;
                .Select(r => r.Workflow.Category)
                .Distinct()
                .Take(3);

            foreach (var category in topCategories)
            {
                suggestions.Add($"Try searching in {category} category");
            }

            return suggestions;
        }

        private void RecordWorkflowAccess(string workflowId, WorkflowAccessType accessType)
        {
            // Record access for analytics;
            _metricsCollector.RecordAccess(workflowId, accessType);
        }

        private void ValidateTemplateParameters(WorkflowTemplate template, Dictionary<string, object> parameters)
        {
            foreach (var requiredParam in template.Parameters.Values.Where(p => p.Required))
            {
                if (!parameters.ContainsKey(requiredParam.Name) || parameters[requiredParam.Name] == null)
                {
                    throw new InvalidParameterException($"Required parameter missing: {requiredParam.Name}");
                }
            }
        }

        private WorkflowDefinition InstantiateTemplate(WorkflowTemplate template, CreateWorkflowRequest request)
        {
            var workflow = new WorkflowDefinition;
            {
                Name = request.Name ?? template.Name,
                Description = request.Description ?? template.Description,
                Category = template.Category,
                Complexity = template.Complexity,
                Tags = template.Tags?.ToList() ?? new List<string>(),
                Activities = template.Structure?.Activities?.Select(a => new Activity;
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = a.Type,
                    Name = a.Name,
                    Description = a.Description,
                    Order = a.Order,
                    Parameters = new Dictionary<string, object>()
                }).ToList() ?? new List<Activity>(),
                Transitions = template.Structure?.Transitions?.Select(t => new Transition;
                {
                    FromActivityId = t.From.ToString(),
                    ToActivityId = t.To.ToString(),
                    Condition = t.Condition;
                }).ToList() ?? new List<Transition>(),
                Parameters = request.Parameters ?? new Dictionary<string, object>(),
                CreatedFromTemplate = template.Id;
            };

            return workflow;
        }

        private ValidationResult ValidateExecutionPrerequisites(WorkflowDefinition workflow, ExecutionRequest request)
        {
            var result = new ValidationResult();

            // Check if workflow is active;
            if (!workflow.IsActive)
            {
                result.Errors.Add("Workflow is not active");
            }

            // Check required parameters;
            foreach (var param in workflow.Parameters.Where(p => p.Value.Required))
            {
                if (!request.Parameters.ContainsKey(param.Key) || request.Parameters[param.Key] == null)
                {
                    result.Errors.Add($"Required parameter missing: {param.Key}");
                }
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        private void SimulateWorkflowExecution(WorkflowExecution execution, WorkflowDefinition workflow)
        {
            // Simulate execution - in production this would use a real workflow engine;
            try
            {
                // Simulate processing time;
                var processingTime = TimeSpan.FromSeconds(new Random().Next(1, 10));

                // 90% success rate for simulation;
                var success = new Random().NextDouble() < 0.9;

                execution.Status = success ? ExecutionStatus.Completed : ExecutionStatus.Failed;
                execution.CompletedAt = DateTime.UtcNow;
                execution.Duration = execution.CompletedAt - execution.StartedAt;

                if (!success)
                {
                    execution.Error = "Simulated execution failure";
                }
                else;
                {
                    execution.Output = new Dictionary<string, object>
                    {
                        { "result", "success" },
                        { "processed_items", new Random().Next(1, 1000) }
                    };
                }
            }
            catch (Exception ex)
            {
                execution.Status = ExecutionStatus.Failed;
                execution.Error = $"Simulation error: {ex.Message}";
                execution.CompletedAt = DateTime.UtcNow;
            }
        }

        private void UpdateWorkflowSuccessRate(WorkflowDefinition workflow, WorkflowExecution execution)
        {
            // Update success rate based on execution result;
            var successful = execution.Status == ExecutionStatus.Completed;

            // Simple moving average calculation;
            var totalExecutions = workflow.ExecutionCount;
            var previousSuccessRate = workflow.SuccessRate;

            workflow.SuccessRate = (previousSuccessRate * (totalExecutions - 1) + (successful ? 1 : 0)) / totalExecutions;
        }

        private bool IsInPeriod(DateTime timestamp, TimePeriod period)
        {
            var now = DateTime.UtcNow;

            return period switch;
            {
                TimePeriod.LastHour => timestamp > now.AddHours(-1),
                TimePeriod.Last24Hours => timestamp > now.AddHours(-24),
                TimePeriod.LastWeek => timestamp > now.AddDays(-7),
                TimePeriod.LastMonth => timestamp > now.AddDays(-30),
                TimePeriod.LastYear => timestamp > now.AddDays(-365),
                _ => true // AllTime;
            };
        }

        private ContextAnalysis AnalyzeRecommendationContext(RecommendationContext context)
        {
            return new ContextAnalysis;
            {
                Keywords = ExtractKeywords(context.Description),
                DetectedDomain = DetectDomainFromContext(context),
                RequiredComplexity = EstimateRequiredComplexity(context),
                Constraints = ExtractConstraints(context)
            };
        }

        private List<WorkflowDefinition> FindMatchingWorkflows(ContextAnalysis analysis)
        {
            var matchingWorkflows = new List<WorkflowDefinition>();

            // Find by domain;
            var domainMatches = _workflowDefinitions.Values;
                .Where(w => w.Category.ToString() == analysis.DetectedDomain)
                .ToList();

            matchingWorkflows.AddRange(domainMatches);

            // Find by keywords;
            foreach (var keyword in analysis.Keywords)
            {
                var keywordMatches = _workflowDefinitions.Values;
                    .Where(w => w.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                               w.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                matchingWorkflows.AddRange(keywordMatches);
            }

            // Remove duplicates;
            return matchingWorkflows;
                .GroupBy(w => w.Id)
                .Select(g => g.First())
                .ToList();
        }

        private double CalculateRecommendationScore(WorkflowDefinition workflow, ContextAnalysis analysis)
        {
            double score = 0.0;

            // Domain match;
            if (workflow.Category.ToString() == analysis.DetectedDomain)
                score += 0.3;

            // Keyword match;
            var keywordMatches = analysis.Keywords.Count(k =>
                workflow.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                workflow.Description.Contains(k, StringComparison.OrdinalIgnoreCase));

            score += (keywordMatches / (double)Math.Max(analysis.Keywords.Count, 1)) * 0.3;

            // Complexity match;
            var complexityMatch = 1.0 - Math.Abs((int)workflow.Complexity - (int)analysis.RequiredComplexity) / 4.0;
            score += complexityMatch * 0.2;

            // Quality factors;
            score += workflow.SuccessRate * 0.1;
            score += Math.Log(workflow.ExecutionCount + 1) * 0.1;

            return Math.Min(score, 1.0);
        }

        private string GenerateRecommendationReason(WorkflowDefinition workflow, ContextAnalysis analysis)
        {
            var reasons = new List<string>();

            if (workflow.Category.ToString() == analysis.DetectedDomain)
                reasons.Add("Matches your domain");

            if (workflow.SuccessRate > 0.8)
                reasons.Add("High success rate");

            if (workflow.ExecutionCount > 10)
                reasons.Add("Frequently used");

            return reasons.Count > 0 ? string.Join(", ", reasons) : "Recommended based on similar workflows";
        }

        private double CalculateConfidence(WorkflowDefinition workflow, ContextAnalysis analysis)
        {
            // Confidence based on workflow maturity and match quality;
            var maturityScore = Math.Min(workflow.ExecutionCount / 100.0, 1.0);
            var successScore = workflow.SuccessRate;

            return (maturityScore * 0.6) + (successScore * 0.4);
        }

        private double CalculateContextMatch(WorkflowDefinition workflow, ContextAnalysis analysis)
        {
            // How well the workflow matches the context;
            double match = 0.0;

            // Check constraints;
            if (analysis.Constraints.Any(c => workflow.Tags?.Contains(c) == true))
                match += 0.3;

            // Check complexity;
            if (workflow.Complexity <= analysis.RequiredComplexity)
                match += 0.3;

            // Check domain;
            if (workflow.Category.ToString() == analysis.DetectedDomain)
                match += 0.4;

            return match;
        }

        private List<string> ExtractKeywords(string text)
        {
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLower())
                .Distinct()
                .ToList();
        }

        private string DetectDomainFromContext(RecommendationContext context)
        {
            // Simple domain detection;
            if (context.Description.Contains("data", StringComparison.OrdinalIgnoreCase))
                return "DataProcessing";

            if (context.Description.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
                context.Description.Contains("review", StringComparison.OrdinalIgnoreCase))
                return "BusinessProcess";

            if (context.Description.Contains("deploy", StringComparison.OrdinalIgnoreCase) ||
                context.Description.Contains("ci", StringComparison.OrdinalIgnoreCase) ||
                context.Description.Contains("cd", StringComparison.OrdinalIgnoreCase))
                return "DevOps";

            return "General";
        }

        private WorkflowComplexity EstimateRequiredComplexity(RecommendationContext context)
        {
            // Estimate complexity based on description length and keywords;
            var wordCount = context.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (wordCount < 10) return WorkflowComplexity.Low;
            if (wordCount < 20) return WorkflowComplexity.Medium;
            if (wordCount < 30) return WorkflowComplexity.High;
            return WorkflowComplexity.VeryHigh;
        }

        private List<string> ExtractConstraints(RecommendationContext context)
        {
            // Extract constraints from context;
            var constraints = new List<string>();

            if (context.Description.Contains("real-time", StringComparison.OrdinalIgnoreCase))
                constraints.Add("real-time");

            if (context.Description.Contains("parallel", StringComparison.OrdinalIgnoreCase))
                constraints.Add("parallel");

            if (context.Description.Contains("secure", StringComparison.OrdinalIgnoreCase))
                constraints.Add("security");

            return constraints;
        }

        private string ExportToJson(ExportOptions options)
        {
            var exportData = new;
            {
                Metadata = new;
                {
                    ExportDate = DateTime.UtcNow,
                    TotalWorkflows = _workflowDefinitions.Count,
                    Version = "1.0"
                },
                Workflows = options.IncludeWorkflows ? _workflowDefinitions.Values : null,
                Templates = options.IncludeTemplates ? _workflowTemplates.Values : null,
                Statistics = options.IncludeStatistics ? _statistics : null;
            };

            return JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = options.PrettyPrint,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });
        }

        private string ExportToMarkdown(ExportOptions options)
        {
            var markdown = new System.Text.StringBuilder();
            markdown.AppendLine($"# Workflow Repository Export");
            markdown.AppendLine($"**Export Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            markdown.AppendLine($"**Total Workflows:** {_workflowDefinitions.Count}");
            markdown.AppendLine($"**Total Templates:** {_workflowTemplates.Count}");
            markdown.AppendLine();

            if (options.IncludeWorkflows)
            {
                markdown.AppendLine("## Workflows");
                markdown.AppendLine();
                foreach (var workflow in _workflowDefinitions.Values.Take(options.MaxItems))
                {
                    markdown.AppendLine($"### {workflow.Name}");
                    markdown.AppendLine($"**ID:** {workflow.Id}");
                    markdown.AppendLine($"**Category:** {workflow.Category}");
                    markdown.AppendLine($"**Status:** {workflow.Status}");
                    markdown.AppendLine($"**Success Rate:** {workflow.SuccessRate:P0}");
                    markdown.AppendLine($"**Executions:** {workflow.ExecutionCount}");
                    markdown.AppendLine();
                }
            }

            return markdown.ToString();
        }

        private string ExportToXml(ExportOptions options)
        {
            // Simplified XML export;
            return $"<WorkflowRepository exportDate=\"{DateTime.UtcNow:O}\" totalWorkflows=\"{_workflowDefinitions.Count}\" />";
        }

        private string ExportToYaml(ExportOptions options)
        {
            // Simplified YAML export;
            var yaml = new System.Text.StringBuilder();
            yaml.AppendLine($"# Workflow Repository Export");
            yaml.AppendLine($"exportDate: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            yaml.AppendLine($"totalWorkflows: {_workflowDefinitions.Count}");
            return yaml.ToString();
        }

        private List<WorkflowDefinition> ParseJsonImport(string content, ImportOptions options)
        {
            try
            {
                var importData = JsonSerializer.Deserialize<WorkflowImportData>(content);
                return importData?.Workflows ?? new List<WorkflowDefinition>();
            }
            catch (JsonException ex)
            {
                throw new ImportFormatException("Invalid JSON format", ex);
            }
        }

        private List<WorkflowDefinition> ParseXmlImport(string content, ImportOptions options)
        {
            // XML parsing implementation;
            return new List<WorkflowDefinition>();
        }

        private List<WorkflowDefinition> ParseYamlImport(string content, ImportOptions options)
        {
            // YAML parsing implementation;
            return new List<WorkflowDefinition>();
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources;
                    _workflowDefinitions.Clear();
                    _workflowTemplates.Clear();
                    _procedures.Clear();
                    _executionHistory.Clear();

                    _workflowCache.Dispose();
                    _workflowIndex.Dispose();
                    _templateIndex.Dispose();
                    _procedureIndex.Dispose();

                    _logger.LogInformation("WorkflowRepository disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~WorkflowRepository()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IWorkflowRepository;
    {
        WorkflowSearchResult SearchWorkflows(WorkflowSearchQuery query);
        WorkflowDefinition GetWorkflow(string workflowId, bool includeVersions);
        WorkflowDefinition CreateWorkflowFromTemplate(string templateId, CreateWorkflowRequest request);
        WorkflowDefinition SaveWorkflow(WorkflowDefinition workflow, SaveOptions options);
        WorkflowExecution ExecuteWorkflow(string workflowId, ExecutionRequest request);
        List<WorkflowExecution> GetExecutionHistory(string workflowId, TimePeriod period);
        List<WorkflowTemplate> GetTemplates(WorkflowCategory? category, int maxResults);
        List<Procedure> GetProcedures(ProcedureCategory? category, int maxResults);
        List<WorkflowRecommendation> RecommendWorkflows(RecommendationContext context);
        string ExportWorkflows(ExportFormat format, ExportOptions options);
        ImportResult ImportWorkflows(string content, ImportFormat format, ImportOptions options);
        WorkflowRepositoryStatistics GetStatistics();
        void ClearCaches();
    }

    public interface IWorkflowStorage;
    {
        List<WorkflowDefinition> LoadAllWorkflows();
        List<WorkflowTemplate> LoadTemplates();
        void SaveWorkflow(WorkflowDefinition workflow);
        void SaveExecution(WorkflowExecution execution);
        List<WorkflowExecution> LoadExecutions(string workflowId);
    }

    public interface IWorkflowValidator;
    {
        ValidationResult Validate(WorkflowDefinition workflow);
        ValidationResult ValidateTemplate(WorkflowTemplate template);
    }

    public interface IWorkflowAnalyzer;
    {
        WorkflowAnalysis Analyze(WorkflowDefinition workflow);
        ComplexityAnalysis AnalyzeComplexity(WorkflowDefinition workflow);
        RiskAssessment AssessRisks(WorkflowDefinition workflow);
    }

    /// <summary>
    /// Workflow definition;
    /// </summary>
    public class WorkflowDefinition;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public WorkflowCategory Category { get; set; }
        public WorkflowStatus Status { get; set; }
        public WorkflowComplexity Complexity { get; set; }
        public List<Activity> Activities { get; set; }
        public List<Transition> Transitions { get; set; }
        public Dictionary<string, ParameterDefinition> Parameters { get; set; }
        public List<string> Tags { get; set; }
        public int Version { get; set; }
        public bool IsActive { get; set; }
        public double SuccessRate { get; set; }
        public int ExecutionCount { get; set; }
        public int ViewCount { get; set; }
        public DateTime? LastExecuted { get; set; }
        public DateTime? LastAccessed { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string CreatedBy { get; set; }
        public string UpdatedBy { get; set; }
        public string CreatedFromTemplate { get; set; }
        public WorkflowAnalysis Analysis { get; set; }
        public List<WorkflowVersion> Versions { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public WorkflowDefinition()
        {
            Activities = new List<Activity>();
            Transitions = new List<Transition>();
            Parameters = new Dictionary<string, ParameterDefinition>();
            Tags = new List<string>();
            Versions = new List<WorkflowVersion>();
            Metadata = new Dictionary<string, object>();
            Status = WorkflowStatus.Draft;
            IsActive = true;
            SuccessRate = 0.0;
        }
    }

    /// <summary>
    /// Workflow template;
    /// </summary>
    public class WorkflowTemplate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public WorkflowCategory Category { get; set; }
        public WorkflowComplexity Complexity { get; set; }
        public WorkflowStructure Structure { get; set; }
        public Dictionary<string, TemplateParameter> Parameters { get; set; }
        public List<string> Tags { get; set; }
        public int UsageCount { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public WorkflowTemplate()
        {
            Parameters = new Dictionary<string, TemplateParameter>();
            Tags = new List<string>();
            Metadata = new Dictionary<string, object>();
            CreatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Procedure definition;
    /// </summary>
    public class Procedure;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ProcedureCategory Category { get; set; }
        public List<ProcedureStep> Steps { get; set; }
        public List<string> Tags { get; set; }
        public List<string> SuccessCriteria { get; set; }
        public int UsageCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public Procedure()
        {
            Steps = new List<ProcedureStep>();
            Tags = new List<string>();
            SuccessCriteria = new List<string>();
            Metadata = new Dictionary<string, object>();
            CreatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Workflow execution record;
    /// </summary>
    public class WorkflowExecution;
    {
        public string ExecutionId { get; set; }
        public string WorkflowId { get; set; }
        public int WorkflowVersion { get; set; }
        public ExecutionStatus Status { get; set; }
        public string StartedBy { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan? Duration { get; set; }
        public Dictionary<string, object> InputParameters { get; set; }
        public Dictionary<string, object> Output { get; set; }
        public string Error { get; set; }
        public Dictionary<string, object> Context { get; set; }
        public List<ActivityExecution> ActivityExecutions { get; set; }

        public WorkflowExecution()
        {
            InputParameters = new Dictionary<string, object>();
            Output = new Dictionary<string, object>();
            Context = new Dictionary<string, object>();
            ActivityExecutions = new List<ActivityExecution>();
        }
    }

    /// <summary>
    /// Workflow search result;
    /// </summary>
    public class WorkflowSearchResult;
    {
        public WorkflowSearchQuery Query { get; set; }
        public List<WorkflowMatch> Matches { get; set; }
        public int TotalCount { get; set; }
        public TimeSpan SearchDuration { get; set; }
        public string SearchId { get; set; }
        public List<string> Suggestions { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public WorkflowSearchResult()
        {
            Matches = new List<WorkflowMatch>();
            Suggestions = new List<string>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Workflow match in search results;
    /// </summary>
    public class WorkflowMatch;
    {
        public WorkflowDefinition Workflow { get; set; }
        public List<MatchStrategy> MatchStrategies { get; set; }
        public double RelevanceScore { get; set; }
        public double QualityScore { get; set; }
        public double OverallScore { get; set; }
        public string MatchExplanation { get; set; }

        public WorkflowMatch()
        {
            MatchStrategies = new List<MatchStrategy>();
        }
    }

    /// <summary>
    /// Workflow recommendation;
    /// </summary>
    public class WorkflowRecommendation;
    {
        public WorkflowDefinition Workflow { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; }
        public double Confidence { get; set; }
        public double ContextMatch { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Import result;
    /// </summary>
    public class ImportResult;
    {
        public string ImportId { get; set; }
        public ImportFormat Format { get; set; }
        public bool Success { get; set; }
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public TimeSpan Duration => CompletedAt - StartedAt;

        public ImportResult()
        {
            Errors = new List<string>();
        }
    }

    /// <summary>
    /// Workflow repository statistics;
    /// </summary>
    public class WorkflowRepositoryStatistics;
    {
        public int TotalWorkflows { get; set; }
        public int TotalTemplates { get; set; }
        public int TotalProcedures { get; set; }
        public int TotalExecutions { get; set; }
        public int ActiveWorkflows { get; set; }
        public Dictionary<WorkflowCategory, int> WorkflowsByCategory { get; set; }
        public Dictionary<WorkflowStatus, int> WorkflowsByStatus { get; set; }
        public double AverageSuccessRate { get; set; }
        public DateTime LastUpdated { get; set; }

        public WorkflowRepositoryStatistics()
        {
            WorkflowsByCategory = new Dictionary<WorkflowCategory, int>();
            WorkflowsByStatus = new Dictionary<WorkflowStatus, int>();
        }

        public WorkflowRepositoryStatistics Clone()
        {
            return (WorkflowRepositoryStatistics)MemberwiseClone();
        }
    }

    /// <summary>
    /// Workflow search query;
    /// </summary>
    public class WorkflowSearchQuery;
    {
        public string SearchText { get; set; }
        public List<WorkflowCategory> Categories { get; set; }
        public List<string> Tags { get; set; }
        public WorkflowComplexity? MinComplexity { get; set; }
        public WorkflowComplexity? MaxComplexity { get; set; }
        public WorkflowStatus? Status { get; set; }
        public string ActivityType { get; set; }
        public SortOption SortBy { get; set; }
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 20;
        public Dictionary<string, object> Context { get; set; }

        public WorkflowSearchQuery()
        {
            Categories = new List<WorkflowCategory>();
            Tags = new List<string>();
            Context = new Dictionary<string, object>();
            SortBy = SortOption.Relevance;
        }

        public string GetCacheKey()
        {
            return $"{SearchText}_{Categories?.Count}_{Tags?.Count}_{MinComplexity}_{MaxComplexity}_{Status}_{SortBy}_{Skip}_{Take}";
        }
    }

    /// <summary>
    /// Create workflow request;
    /// </summary>
    public class CreateWorkflowRequest;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string CreatedBy { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public CreateWorkflowRequest()
        {
            Parameters = new Dictionary<string, object>();
            Context = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Execution request;
    /// </summary>
    public class ExecutionRequest;
    {
        public string StartedBy { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public ExecutionRequest()
        {
            Parameters = new Dictionary<string, object>();
            Context = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Save options;
    /// </summary>
    public class SaveOptions;
    {
        public bool CreateVersion { get; set; } = true;
        public bool RequireValidation { get; set; } = true;
        public string UpdatedBy { get; set; }
        public Dictionary<string, object> AdditionalOptions { get; set; }

        public SaveOptions()
        {
            AdditionalOptions = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Recommendation context;
    /// </summary>
    public class RecommendationContext;
    {
        public string Description { get; set; }
        public string Domain { get; set; }
        public List<string> Constraints { get; set; }
        public int MaxRecommendations { get; set; } = 5;
        public Dictionary<string, object> UserPreferences { get; set; }

        public RecommendationContext()
        {
            Constraints = new List<string>();
            UserPreferences = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Export options;
    /// </summary>
    public class ExportOptions;
    {
        public static ExportOptions Default => new ExportOptions();

        public bool IncludeWorkflows { get; set; } = true;
        public bool IncludeTemplates { get; set; } = true;
        public bool IncludeStatistics { get; set; } = true;
        public bool PrettyPrint { get; set; } = true;
        public int MaxItems { get; set; } = 1000;
        public List<string> SelectedCategories { get; set; }

        public ExportOptions()
        {
            SelectedCategories = new List<string>();
        }
    }

    /// <summary>
    /// Import options;
    /// </summary>
    public class ImportOptions;
    {
        public bool OverwriteExisting { get; set; }
        public bool SkipInvalid { get; set; } = true;
        public string ImportedBy { get; set; }
        public Dictionary<string, object> AdditionalOptions { get; set; }

        public ImportOptions()
        {
            AdditionalOptions = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Workflow structure definition;
    /// </summary>
    public class WorkflowStructure;
    {
        public List<ActivityDefinition> Activities { get; set; }
        public List<TransitionDefinition> Transitions { get; set; }

        public WorkflowStructure()
        {
            Activities = new List<ActivityDefinition>();
            Transitions = new List<TransitionDefinition>();
        }
    }

    /// <summary>
    /// Activity definition;
    /// </summary>
    public class ActivityDefinition;
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Order { get; set; }
        public Dictionary<string, object> Parameters { get; set; }

        public ActivityDefinition()
        {
            Parameters = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Transition definition;
    /// </summary>
    public class TransitionDefinition;
    {
        public int From { get; set; }
        public int To { get; set; }
        public string Condition { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Template parameter;
    /// </summary>
    public class TemplateParameter;
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
        public object DefaultValue { get; set; }
    }

    /// <summary>
    /// Procedure step;
    /// </summary>
    public class ProcedureStep;
    {
        public int Order { get; set; }
        public string Action { get; set; }
        public string Description { get; set; }
        public TimeSpan EstimatedTime { get; set; }
        public List<string> RequiredResources { get; set; }
        public Dictionary<string, object> Parameters { get; set; }

        public ProcedureStep()
        {
            RequiredResources = new List<string>();
            Parameters = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Workflow analysis;
    /// </summary>
    public class WorkflowAnalysis;
    {
        public ComplexityAnalysis Complexity { get; set; }
        public RiskAssessment Risks { get; set; }
        public double CompletenessScore { get; set; }
        public List<string> Recommendations { get; set; }
        public Dictionary<string, object> Metrics { get; set; }

        public WorkflowAnalysis()
        {
            Recommendations = new List<string>();
            Metrics = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Context analysis for recommendations;
    /// </summary>
    public class ContextAnalysis;
    {
        public List<string> Keywords { get; set; }
        public string DetectedDomain { get; set; }
        public WorkflowComplexity RequiredComplexity { get; set; }
        public List<string> Constraints { get; set; }

        public ContextAnalysis()
        {
            Keywords = new List<string>();
            Constraints = new List<string>();
        }
    }

    /// <summary>
    /// Workflow import data;
    /// </summary>
    public class WorkflowImportData;
    {
        public List<WorkflowDefinition> Workflows { get; set; }
        public DateTime ExportDate { get; set; }
        public string Version { get; set; }

        public WorkflowImportData()
        {
            Workflows = new List<WorkflowDefinition>();
        }
    }

    #region Enumerations;

    public enum WorkflowCategory;
    {
        General = 0,
        DataProcessing = 1,
        BusinessProcess = 2,
        DevOps = 3,
        MachineLearning = 4,
        Creative = 5,
        Scientific = 6,
        Administrative = 7;
    }

    public enum WorkflowStatus;
    {
        Draft = 0,
        Active = 1,
        Archived = 2,
        Deprecated = 3;
    }

    public enum WorkflowComplexity;
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        VeryHigh = 4;
    }

    public enum ProcedureCategory;
    {
        Development = 0,
        Operations = 1,
        DevOps = 2,
        BestPractice = 3,
        Troubleshooting = 4,
        Administration = 5;
    }

    public enum ExecutionStatus;
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4,
        Suspended = 5;
    }

    public enum MatchStrategy;
    {
        Text = 0,
        Category = 1,
        Tag = 2,
        Complexity = 3,
        Activity = 4;
    }

    public enum SortOption;
    {
        Relevance = 0,
        Name = 1,
        Recent = 2,
        Popular = 3,
        Success = 4,
        Complexity = 5;
    }

    public enum WorkflowAccessType;
    {
        Retrieval = 0,
        Creation = 1,
        Update = 2,
        Execution = 3,
        Deletion = 4;
    }

    public enum ExportFormat;
    {
        JSON = 0,
        XML = 1,
        YAML = 2,
        Markdown = 3;
    }

    public enum ImportFormat;
    {
        JSON = 0,
        XML = 1,
        YAML = 2;
    }

    public enum TimePeriod;
    {
        AllTime = 0,
        LastHour = 1,
        Last24Hours = 2,
        LastWeek = 3,
        LastMonth = 4,
        LastYear = 5;
    }

    #endregion;

    #region Internal Support Classes;

    internal class WorkflowCache : IDisposable
    {
        private readonly Dictionary<string, WorkflowSearchResult> _searchCache;
        private readonly Dictionary<string, WorkflowDefinition> _workflowCache;
        private readonly Queue<string> _searchCacheQueue;
        private readonly Queue<string> _workflowCacheQueue;
        private readonly int _maxSize;
        private readonly TimeSpan _timeToLive;

        public WorkflowCache(CacheConfiguration config)
        {
            _maxSize = config.MaxSize;
            _timeToLive = config.TimeToLive;
            _searchCache = new Dictionary<string, WorkflowSearchResult>();
            _workflowCache = new Dictionary<string, WorkflowDefinition>();
            _searchCacheQueue = new Queue<string>();
            _workflowCacheQueue = new Queue<string>();
        }

        public bool TryGetSearchResult(string key, out WorkflowSearchResult result)
        {
            return _searchCache.TryGetValue(key, out result);
        }

        public void CacheSearchResult(string key, WorkflowSearchResult result)
        {
            _searchCache[key] = result;
            _searchCacheQueue.Enqueue(key);

            EnforceSizeLimit(_searchCache, _searchCacheQueue);
        }

        public bool TryGetWorkflow(string workflowId, out WorkflowDefinition workflow)
        {
            return _workflowCache.TryGetValue(workflowId, out workflow);
        }

        public void CacheWorkflow(string workflowId, WorkflowDefinition workflow)
        {
            _workflowCache[workflowId] = workflow;
            _workflowCacheQueue.Enqueue(workflowId);

            EnforceSizeLimit(_workflowCache, _workflowCacheQueue);
        }

        private void EnforceSizeLimit<T>(Dictionary<string, T> cache, Queue<string> queue)
        {
            while (cache.Count > _maxSize && queue.Count > 0)
            {
                var oldestKey = queue.Dequeue();
                cache.Remove(oldestKey);
            }
        }

        public void Clear()
        {
            _searchCache.Clear();
            _workflowCache.Clear();
            _searchCacheQueue.Clear();
            _workflowCacheQueue.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }

    internal class WorkflowIndex : IDisposable
    {
        private readonly Dictionary<string, List<string>> _textIndex;
        private readonly Dictionary<WorkflowCategory, List<string>> _categoryIndex;
        private readonly Dictionary<string, List<string>> _tagIndex;
        private readonly Dictionary<WorkflowComplexity, List<string>> _complexityIndex;

        public int Count => GetTotalCount();

        public WorkflowIndex()
        {
            _textIndex = new Dictionary<string, List<string>>();
            _categoryIndex = new Dictionary<WorkflowCategory, List<string>>();
            _tagIndex = new Dictionary<string, List<string>>();
            _complexityIndex = new Dictionary<WorkflowComplexity, List<string>>();
        }

        public void AddWorkflow(WorkflowDefinition workflow)
        {
            // Index by text;
            var keywords = ExtractKeywords(workflow.Name + " " + workflow.Description);
            foreach (var keyword in keywords)
            {
                if (!_textIndex.ContainsKey(keyword))
                    _textIndex[keyword] = new List<string>();
                _textIndex[keyword].Add(workflow.Id);
            }

            // Index by category;
            if (!_categoryIndex.ContainsKey(workflow.Category))
                _categoryIndex[workflow.Category] = new List<string>();
            _categoryIndex[workflow.Category].Add(workflow.Id);

            // Index by tags;
            foreach (var tag in workflow.Tags)
            {
                if (!_tagIndex.ContainsKey(tag))
                    _tagIndex[tag] = new List<string>();
                _tagIndex[tag].Add(workflow.Id);
            }

            // Index by complexity;
            if (!_complexityIndex.ContainsKey(workflow.Complexity))
                _complexityIndex[workflow.Complexity] = new List<string>();
            _complexityIndex[workflow.Complexity].Add(workflow.Id);
        }

        public void UpdateWorkflow(WorkflowDefinition workflow)
        {
            // For simplicity, we'll remove and re-add;
            // In production, we would track and update indexes;
            RemoveWorkflow(workflow.Id);
            AddWorkflow(workflow);
        }

        public void RemoveWorkflow(string workflowId)
        {
            // Remove from all indexes;
            // Implementation would track which indexes contain the workflow;
        }

        public List<WorkflowMatch> SearchByText(string searchText)
        {
            var matches = new List<WorkflowMatch>();
            var keywords = ExtractKeywords(searchText);

            foreach (var keyword in keywords)
            {
                if (_textIndex.TryGetValue(keyword, out var workflowIds))
                {
                    foreach (var workflowId in workflowIds)
                    {
                        matches.Add(new WorkflowMatch;
                        {
                            MatchStrategy = MatchStrategy.Text,
                            MatchExplanation = $"Matches keyword: {keyword}"
                        });
                    }
                }
            }

            return matches;
        }

        public List<WorkflowMatch> SearchByCategory(List<WorkflowCategory> categories)
        {
            var matches = new List<WorkflowMatch>();

            foreach (var category in categories)
            {
                if (_categoryIndex.TryGetValue(category, out var workflowIds))
                {
                    foreach (var workflowId in workflowIds)
                    {
                        matches.Add(new WorkflowMatch;
                        {
                            MatchStrategy = MatchStrategy.Category,
                            MatchExplanation = $"Category: {category}"
                        });
                    }
                }
            }

            return matches;
        }

        public List<WorkflowMatch> SearchByTags(List<string> tags)
        {
            var matches = new List<WorkflowMatch>();

            foreach (var tag in tags)
            {
                if (_tagIndex.TryGetValue(tag, out var workflowIds))
                {
                    foreach (var workflowId in workflowIds)
                    {
                        matches.Add(new WorkflowMatch;
                        {
                            MatchStrategy = MatchStrategy.Tag,
                            MatchExplanation = $"Tag: {tag}"
                        });
                    }
                }
            }

            return matches;
        }

        public List<WorkflowMatch> SearchByComplexity(WorkflowComplexity? min, WorkflowComplexity? max)
        {
            var matches = new List<WorkflowMatch>();
            var complexities = _complexityIndex.Keys;

            foreach (var complexity in complexities)
            {
                if ((!min.HasValue || complexity >= min.Value) &&
                    (!max.HasValue || complexity <= max.Value))
                {
                    if (_complexityIndex.TryGetValue(complexity, out var workflowIds))
                    {
                        foreach (var workflowId in workflowIds)
                        {
                            matches.Add(new WorkflowMatch;
                            {
                                MatchStrategy = MatchStrategy.Complexity,
                                MatchExplanation = $"Complexity: {complexity}"
                            });
                        }
                    }
                }
            }

            return matches;
        }

        private List<string> ExtractKeywords(string text)
        {
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLower())
                .Distinct()
                .ToList();
        }

        private int GetTotalCount()
        {
            var allIds = new HashSet<string>();
            foreach (var ids in _textIndex.Values) allIds.UnionWith(ids);
            foreach (var ids in _categoryIndex.Values) allIds.UnionWith(ids);
            foreach (var ids in _tagIndex.Values) allIds.UnionWith(ids);
            foreach (var ids in _complexityIndex.Values) allIds.UnionWith(ids);
            return allIds.Count;
        }

        public void Dispose()
        {
            _textIndex.Clear();
            _categoryIndex.Clear();
            _tagIndex.Clear();
            _complexityIndex.Clear();
        }
    }

    // Additional internal classes would be defined here...

    #endregion;

    #region Exceptions;

    public class WorkflowRepositoryException : Exception
    {
        public WorkflowRepositoryException(string message) : base(message) { }
        public WorkflowRepositoryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class WorkflowNotFoundException : Exception
    {
        public string WorkflowId { get; }

        public WorkflowNotFoundException(string workflowId)
            : base($"Workflow not found: {workflowId}")
        {
            WorkflowId = workflowId;
        }
    }

    public class TemplateNotFoundException : Exception
    {
        public string TemplateId { get; }

        public TemplateNotFoundException(string templateId)
            : base($"Template not found: {templateId}")
        {
            TemplateId = templateId;
        }
    }

    public class WorkflowSearchException : Exception
    {
        public WorkflowSearchException(string message) : base(message) { }
        public WorkflowSearchException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class WorkflowCreationException : Exception
    {
        public WorkflowCreationException(string message) : base(message) { }
        public WorkflowCreationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class WorkflowSaveException : Exception
    {
        public WorkflowSaveException(string message) : base(message) { }
        public WorkflowSaveException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class WorkflowExecutionException : Exception
    {
        public WorkflowExecutionException(string message) : base(message) { }
        public WorkflowExecutionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class WorkflowValidationException : Exception
    {
        public WorkflowValidationException(string message) : base(message) { }
        public WorkflowValidationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class WorkflowExportException : Exception
    {
        public WorkflowExportException(string message) : base(message) { }
        public WorkflowExportException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class WorkflowImportException : Exception
    {
        public WorkflowImportException(string message) : base(message) { }
        public WorkflowImportException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ImportFormatException : Exception
    {
        public ImportFormatException(string message) : base(message) { }
        public ImportFormatException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class InvalidParameterException : Exception
    {
        public InvalidParameterException(string message) : base(message) { }
    }

    #endregion;
}
