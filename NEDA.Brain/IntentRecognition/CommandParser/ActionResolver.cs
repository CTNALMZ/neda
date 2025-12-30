// NEDA.Brain/IntentRecognition/CommandParser/ActionResolver.cs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Automation.ScenarioPlanner;
using NEDA.Brain.IntentRecognition.ActionExtractor;
using NEDA.Brain.IntentRecognition.CommandParser;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.Commands;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Security;
using NEDA.Services.FileService;
using NEDA.Services.ProjectService;
using NEDA.SystemControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Brain.IntentRecognition.CommandParser;
{
    /// <summary>
    /// NEDA sisteminin eylem çözümleme ve komut yürütme motoru.
    /// Ayrıştırılmış eylemleri gerçek sistem komutlarına dönüştürür ve yürütür.
    /// </summary>
    public class ActionResolver : IActionResolver, IDisposable;
    {
        private readonly ILogger<ActionResolver> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IKnowledgeBase _knowledgeBase;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IMemoryRecall _memoryRecall;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IProjectManager _projectManager;
        private readonly IFileManager _fileManager;
        private readonly ISystemManager _systemManager;
        private readonly ISecurityManager _securityManager;

        // Action resolution models and caches;
        private readonly Dictionary<string, ActionMapping> _actionMappings;
        private readonly Dictionary<string, CommandTemplate> _commandTemplates;
        private readonly Dictionary<string, ExecutionPolicy> _executionPolicies;
        private readonly LRUCache<string, ResolvedAction> _resolutionCache;

        // Configuration;
        private ActionResolverConfig _config;
        private readonly ActionResolverOptions _options;
        private bool _isInitialized;
        private readonly object _lockObject = new object();
        private readonly SemaphoreSlim _resolutionSemaphore;

        // Service resolvers;
        private readonly Dictionary<ActionDomain, IServiceResolver> _serviceResolvers;
        private readonly Dictionary<string, ICommandExecutor> _commandExecutors;

        // Events;
        public event EventHandler<ActionResolutionStartedEventArgs> OnResolutionStarted;
        public event EventHandler<ActionResolutionProgressEventArgs> OnResolutionProgress;
        public event EventHandler<ActionResolutionCompletedEventArgs> OnResolutionCompleted;
        public event EventHandler<CommandExecutionStartedEventArgs> OnExecutionStarted;
        public event EventHandler<CommandExecutionCompletedEventArgs> OnExecutionCompleted;
        public event EventHandler<ActionValidationFailedEventArgs> OnValidationFailed;

        /// <summary>
        /// ActionResolver constructor;
        /// </summary>
        public ActionResolver(
            ILogger<ActionResolver> logger,
            IServiceProvider serviceProvider,
            IKnowledgeBase knowledgeBase,
            ISemanticAnalyzer semanticAnalyzer,
            IMemoryRecall memoryRecall,
            IDiagnosticTool diagnosticTool,
            IMetricsCollector metricsCollector,
            IProjectManager projectManager,
            IFileManager fileManager,
            ISystemManager systemManager,
            ISecurityManager securityManager,
            IOptions<ActionResolverOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _memoryRecall = memoryRecall ?? throw new ArgumentNullException(nameof(memoryRecall));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _options = options?.Value ?? new ActionResolverOptions();

            // Initialize storage;
            _actionMappings = new Dictionary<string, ActionMapping>();
            _commandTemplates = new Dictionary<string, CommandTemplate>();
            _executionPolicies = new Dictionary<string, ExecutionPolicy>();
            _resolutionCache = new LRUCache<string, ResolvedAction>(capacity: 1000);

            // Default configuration;
            _config = new ActionResolverConfig();
            _isInitialized = false;

            // Initialize service resolvers;
            _serviceResolvers = new Dictionary<ActionDomain, IServiceResolver>();
            _commandExecutors = new Dictionary<string, ICommandExecutor>();

            // Concurrency control;
            _resolutionSemaphore = new SemaphoreSlim(
                _config.MaxConcurrentResolutions,
                _config.MaxConcurrentResolutions);

            _logger.LogInformation("ActionResolver initialized successfully");
        }

        /// <summary>
        /// ActionResolver'ı belirtilen konfigürasyon ile başlatır;
        /// </summary>
        public async Task InitializeAsync(ActionResolverConfig config = null)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("ActionResolver already initialized");
                    return;
                }

                _logger.LogInformation("Initializing ActionResolver...");

                _config = config ?? new ActionResolverConfig();

                // Load action mappings;
                await LoadActionMappingsAsync();

                // Load command templates;
                await LoadCommandTemplatesAsync();

                // Load execution policies;
                await LoadExecutionPoliciesAsync();

                // Initialize service resolvers;
                await InitializeServiceResolversAsync();

                // Initialize command executors;
                await InitializeCommandExecutorsAsync();

                // Load domain-specific resolvers;
                await LoadDomainResolversAsync();

                // Load historical resolutions;
                await LoadHistoricalResolutionsAsync();

                // Initialize security context;
                await InitializeSecurityContextAsync();

                // Warm up resolution models;
                await WarmUpModelsAsync();

                _isInitialized = true;

                _logger.LogInformation("ActionResolver initialized successfully with {MappingCount} action mappings and {TemplateCount} command templates",
                    _actionMappings.Count, _commandTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ActionResolver");
                throw new ActionResolverException("ActionResolver initialization failed", ex);
            }
        }

        /// <summary>
        /// Eylem eşleştirmelerini yükler;
        /// </summary>
        private async Task LoadActionMappingsAsync()
        {
            try
            {
                // Load default action mappings;
                var defaultMappings = GetDefaultActionMappings();
                foreach (var mapping in defaultMappings)
                {
                    _actionMappings[mapping.Id] = mapping;
                }

                // Load mappings from knowledge base;
                var kbMappings = await _knowledgeBase.GetActionMappingsAsync();
                foreach (var mapping in kbMappings)
                {
                    if (!_actionMappings.ContainsKey(mapping.Id))
                    {
                        _actionMappings[mapping.Id] = mapping;
                    }
                }

                // Load learned mappings from memory;
                var learnedMappings = await _memoryRecall.GetActionMappingsAsync();
                foreach (var mapping in learnedMappings)
                {
                    var mappingId = $"LEARNED_{mapping.Id}";
                    _actionMappings[mappingId] = mapping;
                }

                _logger.LogDebug("Loaded {MappingCount} action mappings", _actionMappings.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load action mappings");
                throw;
            }
        }

        /// <summary>
        /// Varsayılan eylem eşleştirmelerini döndürür;
        /// </summary>
        private IEnumerable<ActionMapping> GetDefaultActionMappings()
        {
            return new List<ActionMapping>
            {
                new ActionMapping;
                {
                    Id = "MAPPING_CREATE_FILE",
                    Name = "Create File Mapping",
                    Description = "Maps create file actions to file system operations",
                    ActionType = ActionType.Creation,
                    Domain = ActionDomain.FileSystem,
                    VerbPatterns = new List<string> { "create", "make", "generate", "new" },
                    ObjectPatterns = new List<string> { "file", "document", "text", "data" },
                    TargetService = "FileManager",
                    TargetMethod = "CreateFileAsync",
                    RequiredPermissions = new List<string> { "File.Create" },
                    SuccessCriteria = new List<string> { "FileCreated", "NoErrors" },
                    Confidence = 0.95,
                    IsActive = true;
                },
                new ActionMapping;
                {
                    Id = "MAPPING_DELETE_FILE",
                    Name = "Delete File Mapping",
                    Description = "Maps delete file actions to file system operations",
                    ActionType = ActionType.Deletion,
                    Domain = ActionDomain.FileSystem,
                    VerbPatterns = new List<string> { "delete", "remove", "erase", "trash" },
                    ObjectPatterns = new List<string> { "file", "document", "data", "item" },
                    TargetService = "FileManager",
                    TargetMethod = "DeleteFileAsync",
                    RequiredPermissions = new List<string> { "File.Delete" },
                    SuccessCriteria = new List<string> { "FileDeleted", "NoErrors" },
                    Confidence = 0.9,
                    IsActive = true;
                },
                new ActionMapping;
                {
                    Id = "MAPPING_OPEN_PROJECT",
                    Name = "Open Project Mapping",
                    Description = "Maps open project actions to project management operations",
                    ActionType = ActionType.Access,
                    Domain = ActionDomain.ProjectManagement,
                    VerbPatterns = new List<string> { "open", "load", "start", "launch" },
                    ObjectPatterns = new List<string> { "project", "solution", "workspace", "application" },
                    TargetService = "ProjectManager",
                    TargetMethod = "OpenProjectAsync",
                    RequiredPermissions = new List<string> { "Project.Open" },
                    SuccessCriteria = new List<string> { "ProjectOpened", "NoErrors" },
                    Confidence = 0.85,
                    IsActive = true;
                },
                new ActionMapping;
                {
                    Id = "MAPPING_ANALYZE_DATA",
                    Name = "Analyze Data Mapping",
                    Description = "Maps analyze data actions to data analysis operations",
                    ActionType = ActionType.Analysis,
                    Domain = ActionDomain.DataAnalysis,
                    VerbPatterns = new List<string> { "analyze", "process", "examine", "study" },
                    ObjectPatterns = new List<string> { "data", "information", "dataset", "results" },
                    TargetService = "DataAnalyzer",
                    TargetMethod = "AnalyzeDataAsync",
                    RequiredPermissions = new List<string> { "Data.Analyze" },
                    SuccessCriteria = new List<string> { "AnalysisCompleted", "ResultsGenerated" },
                    Confidence = 0.8,
                    IsActive = true;
                },
                new ActionMapping;
                {
                    Id = "MAPPING_SYSTEM_STATUS",
                    Name = "System Status Mapping",
                    Description = "Maps system status actions to system monitoring operations",
                    ActionType = ActionType.Monitoring,
                    Domain = ActionDomain.SystemControl,
                    VerbPatterns = new List<string> { "check", "monitor", "status", "health" },
                    ObjectPatterns = new List<string> { "system", "server", "service", "process" },
                    TargetService = "SystemManager",
                    TargetMethod = "GetSystemStatusAsync",
                    RequiredPermissions = new List<string> { "System.Monitor" },
                    SuccessCriteria = new List<string> { "StatusRetrieved", "NoErrors" },
                    Confidence = 0.9,
                    IsActive = true;
                },
                new ActionMapping;
                {
                    Id = "MAPPING_COMPILE_CODE",
                    Name = "Compile Code Mapping",
                    Description = "Maps compile actions to build system operations",
                    ActionType = ActionType.Execution,
                    Domain = ActionDomain.Development,
                    VerbPatterns = new List<string> { "compile", "build", "make", "assemble" },
                    ObjectPatterns = new List<string> { "code", "project", "solution", "program" },
                    TargetService = "BuildManager",
                    TargetMethod = "CompileProjectAsync",
                    RequiredPermissions = new List<string> { "Build.Execute" },
                    SuccessCriteria = new List<string> { "BuildCompleted", "NoErrors" },
                    Confidence = 0.85,
                    IsActive = true;
                }
            };
        }

        /// <summary>
        /// Komut şablonlarını yükler;
        /// </summary>
        private async Task LoadCommandTemplatesAsync()
        {
            try
            {
                var templates = new List<CommandTemplate>
                {
                    new CommandTemplate;
                    {
                        Id = "TEMPLATE_FILE_CREATE",
                        Name = "File Create Template",
                        Description = "Template for creating files",
                        Domain = ActionDomain.FileSystem,
                        Template = "{Service}.{Method}(path: \"{Path}\", content: \"{Content}\")",
                        Parameters = new Dictionary<string, ParameterTemplate>
                        {
                            ["Path"] = new ParameterTemplate;
                            {
                                Name = "Path",
                                Type = ParameterType.String,
                                Required = true,
                                Source = ParameterSource.Object,
                                Validation = new ParameterValidation;
                                {
                                    Regex = @"^[a-zA-Z0-9_\\\/\.\-]+$",
                                    MinLength = 1,
                                    MaxLength = 260;
                                }
                            },
                            ["Content"] = new ParameterTemplate;
                            {
                                Name = "Content",
                                Type = ParameterType.String,
                                Required = false,
                                Source = ParameterSource.Context,
                                DefaultValue = string.Empty;
                            }
                        },
                        SuccessConditions = new List<string> { "FileExists", "NoException" }
                    },
                    new CommandTemplate;
                    {
                        Id = "TEMPLATE_PROJECT_OPEN",
                        Name = "Project Open Template",
                        Description = "Template for opening projects",
                        Domain = ActionDomain.ProjectManagement,
                        Template = "{Service}.{Method}(projectPath: \"{Path}\", options: {Options})",
                        Parameters = new Dictionary<string, ParameterTemplate>
                        {
                            ["Path"] = new ParameterTemplate;
                            {
                                Name = "Path",
                                Type = ParameterType.String,
                                Required = true,
                                Source = ParameterSource.Object,
                                Validation = new ParameterValidation;
                                {
                                    Regex = @"^[a-zA-Z0-9_\\\/\.\-]+\.(sln|csproj|vbproj)$",
                                    MinLength = 1,
                                    MaxLength = 260;
                                }
                            },
                            ["Options"] = new ParameterTemplate;
                            {
                                Name = "Options",
                                Type = ParameterType.Object,
                                Required = false,
                                Source = ParameterSource.Context,
                                DefaultValue = "new OpenProjectOptions()"
                            }
                        },
                        SuccessConditions = new List<string> { "ProjectLoaded", "NoErrors" }
                    },
                    new CommandTemplate;
                    {
                        Id = "TEMPLATE_SYSTEM_STATUS",
                        Name = "System Status Template",
                        Description = "Template for checking system status",
                        Domain = ActionDomain.SystemControl,
                        Template = "{Service}.{Method}()",
                        Parameters = new Dictionary<string, ParameterTemplate>(),
                        SuccessConditions = new List<string> { "StatusReturned", "NoException" }
                    }
                };

                foreach (var template in templates)
                {
                    _commandTemplates[template.Id] = template;
                }

                _logger.LogDebug("Loaded {TemplateCount} command templates", _commandTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load command templates");
                throw;
            }
        }

        /// <summary>
        /// Yürütme politikalarını yükler;
        /// </summary>
        private async Task LoadExecutionPoliciesAsync()
        {
            try
            {
                var policies = new List<ExecutionPolicy>
                {
                    new ExecutionPolicy;
                    {
                        Id = "POLICY_SAFE_EXECUTION",
                        Name = "Safe Execution Policy",
                        Description = "Policy for safe command execution with validation",
                        Domain = ActionDomain.All,
                        ValidationRules = new List<ValidationRule>
                        {
                            new ValidationRule;
                            {
                                Type = ValidationType.PermissionCheck,
                                Condition = "UserHasPermission(RequiredPermissions)",
                                Message = "User lacks required permissions",
                                Severity = ValidationSeverity.Error;
                            },
                            new ValidationRule;
                            {
                                Type = ValidationType.ParameterValidation,
                                Condition = "ParametersValid(Parameters)",
                                Message = "Invalid parameters provided",
                                Severity = ValidationSeverity.Error;
                            },
                            new ValidationRule;
                            {
                                Type = ValidationType.ImpactAssessment,
                                Condition = "ImpactScore < 0.7",
                                Message = "High impact action requires confirmation",
                                Severity = ValidationSeverity.Warning;
                            }
                        },
                        ExecutionRules = new List<ExecutionRule>
                        {
                            new ExecutionRule;
                            {
                                Type = ExecutionRuleType.Timeout,
                                Value = "30",
                                Unit = "seconds"
                            },
                            new ExecutionRule;
                            {
                                Type = ExecutionRuleType.Retry,
                                MaxAttempts = 3,
                                Delay = 1000;
                            }
                        },
                        IsActive = true;
                    },
                    new ExecutionPolicy;
                    {
                        Id = "POLICY_CRITICAL_OPERATIONS",
                        Name = "Critical Operations Policy",
                        Description = "Policy for critical system operations",
                        Domain = ActionDomain.SystemControl,
                        ValidationRules = new List<ValidationRule>
                        {
                            new ValidationRule;
                            {
                                Type = ValidationType.AdministratorCheck,
                                Condition = "UserIsAdministrator()",
                                Message = "Administrator privileges required",
                                Severity = ValidationSeverity.Error;
                            },
                            new ValidationRule;
                            {
                                Type = ValidationType.ConfirmationRequired,
                                Condition = "Always",
                                Message = "Confirmation required for critical operations",
                                Severity = ValidationSeverity.Warning;
                            }
                        },
                        ExecutionRules = new List<ExecutionRule>
                        {
                            new ExecutionRule;
                            {
                                Type = ExecutionRuleType.Logging,
                                Level = "Detailed"
                            },
                            new ExecutionRule;
                            {
                                Type = ExecutionRuleType.Audit,
                                Required = true;
                            }
                        },
                        IsActive = true;
                    }
                };

                foreach (var policy in policies)
                {
                    _executionPolicies[policy.Id] = policy;
                }

                _logger.LogDebug("Loaded {PolicyCount} execution policies", _executionPolicies.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load execution policies");
                throw;
            }
        }

        /// <summary>
        /// Servis çözümleyicilerini başlatır;
        /// </summary>
        private async Task InitializeServiceResolversAsync()
        {
            try
            {
                _serviceResolvers[ActionDomain.FileSystem] = new FileSystemServiceResolver(_serviceProvider);
                _serviceResolvers[ActionDomain.ProjectManagement] = new ProjectServiceResolver(_serviceProvider);
                _serviceResolvers[ActionDomain.SystemControl] = new SystemServiceResolver(_serviceProvider);
                _serviceResolvers[ActionDomain.DataAnalysis] = new DataServiceResolver(_serviceProvider);
                _serviceResolvers[ActionDomain.Development] = new DevelopmentServiceResolver(_serviceProvider);

                foreach (var resolver in _serviceResolvers.Values)
                {
                    await resolver.InitializeAsync();
                }

                _logger.LogDebug("Initialized {ResolverCount} service resolvers", _serviceResolvers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize service resolvers");
                throw;
            }
        }

        /// <summary>
        /// Komut yürütücülerini başlatır;
        /// </summary>
        private async Task InitializeCommandExecutorsAsync()
        {
            try
            {
                _commandExecutors["FileManager"] = new FileCommandExecutor(_serviceProvider);
                _commandExecutors["ProjectManager"] = new ProjectCommandExecutor(_serviceProvider);
                _commandExecutors["SystemManager"] = new SystemCommandExecutor(_serviceProvider);
                _commandExecutors["DataAnalyzer"] = new DataCommandExecutor(_serviceProvider);
                _commandExecutors["BuildManager"] = new BuildCommandExecutor(_serviceProvider);

                foreach (var executor in _commandExecutors.Values)
                {
                    await executor.InitializeAsync();
                }

                _logger.LogDebug("Initialized {ExecutorCount} command executors", _commandExecutors.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize command executors");
                throw;
            }
        }

        /// <summary>
        /// Domain çözümleyicilerini yükler;
        /// </summary>
        private async Task LoadDomainResolversAsync()
        {
            try
            {
                var domainResolvers = new List<IDomainResolver>
                {
                    new FileSystemDomainResolver(_serviceProvider),
                    new ProjectDomainResolver(_serviceProvider),
                    new SystemDomainResolver(_serviceProvider),
                    new SecurityDomainResolver(_serviceProvider),
                    new DevelopmentDomainResolver(_serviceProvider)
                };

                foreach (var resolver in domainResolvers)
                {
                    await resolver.InitializeAsync();
                }

                _logger.LogDebug("Loaded {ResolverCount} domain resolvers", domainResolvers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load domain resolvers");
                throw;
            }
        }

        /// <summary>
        /// Tarihsel çözümlemeleri yükler;
        /// </summary>
        private async Task LoadHistoricalResolutionsAsync()
        {
            try
            {
                var historicalResolutions = await _knowledgeBase.GetHistoricalResolutionsAsync();

                // Cache successful resolutions for quick reference;
                foreach (var resolution in historicalResolutions.Where(r => r.Success))
                {
                    var resolutionKey = GenerateResolutionKey(resolution.ActionId, resolution.Domain);
                    if (!_resolutionCache.Contains(resolutionKey))
                    {
                        _resolutionCache.Put(resolutionKey, resolution);
                    }
                }

                _logger.LogDebug("Loaded {ResolutionCount} historical resolutions", historicalResolutions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load historical resolutions");
                // Non-critical, continue without historical data;
            }
        }

        /// <summary>
        /// Güvenlik bağlamını başlatır;
        /// </summary>
        private async Task InitializeSecurityContextAsync()
        {
            try
            {
                // Load security policies for action resolution;
                var securityContext = new SecurityContext;
                {
                    UserId = Environment.UserName,
                    SessionId = Guid.NewGuid().ToString(),
                    Permissions = await _securityManager.GetUserPermissionsAsync(Environment.UserName),
                    SecurityLevel = SecurityLevel.Normal;
                };

                // Store security context;
                await _knowledgeBase.StoreSecurityContextAsync(securityContext);

                _logger.LogDebug("Security context initialized for user: {UserId}", securityContext.UserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize security context");
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
                _logger.LogDebug("Warming up action resolution models...");

                var warmupTasks = new List<Task>
                {
                    WarmUpMappingCacheAsync(),
                    WarmUpServiceResolutionAsync(),
                    WarmUpParameterResolutionAsync()
                };

                await Task.WhenAll(warmupTasks);

                _logger.LogDebug("Action resolution models warmed up successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to warm up models");
                // Non-critical, continue;
            }
        }

        /// <summary>
        /// Eylemleri çözümler;
        /// </summary>
        public async Task<ResolvedAction> ResolveActionAsync(
            ActionResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                // Check cache first;
                var cacheKey = GenerateCacheKey(request);
                if (_config.EnableCaching && _resolutionCache.TryGet(cacheKey, out var cachedResolution))
                {
                    _logger.LogDebug("Returning cached action resolution for request: {RequestId}", request.Id);
                    return cachedResolution;
                }

                // Acquire semaphore for concurrency control;
                await _resolutionSemaphore.WaitAsync(cancellationToken);

                try
                {
                    var resolutionId = Guid.NewGuid().ToString();

                    // Event: Resolution started;
                    OnResolutionStarted?.Invoke(this, new ActionResolutionStartedEventArgs;
                    {
                        ResolutionId = resolutionId,
                        Request = request,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Starting action resolution {ResolutionId} for request: {RequestId}",
                        resolutionId, request.Id);

                    var startTime = DateTime.UtcNow;

                    // Progress report;
                    await ReportProgressAsync(resolutionId, 0, "Initializing action resolution", request.Context);

                    // 1. Validate parsed action;
                    var validationResult = await ValidateParsedActionAsync(request.ParsedAction, request.Context, cancellationToken);
                    if (!validationResult.IsValid)
                    {
                        throw new ActionValidationException($"Action validation failed: {validationResult.Error}");
                    }
                    await ReportProgressAsync(resolutionId, 10, "Action validation completed", request.Context);

                    // 2. Map action to service operation;
                    var mappingResult = await MapActionToServiceAsync(request.ParsedAction, request.Context, cancellationToken);
                    await ReportProgressAsync(resolutionId, 20, "Action mapping completed", request.Context);

                    // 3. Resolve service and method;
                    var serviceResolution = await ResolveServiceAndMethodAsync(mappingResult, request.Context, cancellationToken);
                    await ReportProgressAsync(resolutionId, 30, "Service resolution completed", request.Context);

                    // 4. Resolve parameters;
                    var parameterResolution = await ResolveParametersAsync(
                        request.ParsedAction,
                        mappingResult,
                        serviceResolution,
                        request.Context,
                        cancellationToken);
                    await ReportProgressAsync(resolutionId, 40, "Parameter resolution completed", request.Context);

                    // 5. Apply execution policy;
                    var policyApplication = await ApplyExecutionPolicyAsync(
                        mappingResult,
                        serviceResolution,
                        parameterResolution,
                        request.Context,
                        cancellationToken);
                    await ReportProgressAsync(resolutionId, 50, "Execution policy applied", request.Context);

                    // 6. Generate command;
                    var commandGeneration = await GenerateCommandAsync(
                        serviceResolution,
                        parameterResolution,
                        policyApplication,
                        cancellationToken);
                    await ReportProgressAsync(resolutionId, 60, "Command generation completed", request.Context);

                    // 7. Validate command;
                    var commandValidation = await ValidateCommandAsync(commandGeneration, request.Context, cancellationToken);
                    if (!commandValidation.IsValid)
                    {
                        throw new CommandValidationException($"Command validation failed: {commandValidation.Error}");
                    }
                    await ReportProgressAsync(resolutionId, 70, "Command validation completed", request.Context);

                    // 8. Build execution plan;
                    var executionPlan = await BuildExecutionPlanAsync(
                        commandGeneration,
                        policyApplication,
                        request.Context,
                        cancellationToken);
                    await ReportProgressAsync(resolutionId, 80, "Execution plan built", request.Context);

                    // 9. Build resolved action;
                    var resolvedAction = await BuildResolvedActionAsync(
                        resolutionId,
                        request,
                        mappingResult,
                        serviceResolution,
                        parameterResolution,
                        policyApplication,
                        commandGeneration,
                        executionPlan,
                        startTime,
                        cancellationToken);

                    // 10. Update cache;
                    if (_config.EnableCaching && resolvedAction.ResolutionConfidence >= _config.CacheThreshold)
                    {
                        _resolutionCache.Put(cacheKey, resolvedAction);
                    }

                    // 11. Record resolution;
                    await RecordResolutionAsync(resolvedAction, cancellationToken);

                    // Event: Resolution completed;
                    OnResolutionCompleted?.Invoke(this, new ActionResolutionCompletedEventArgs;
                    {
                        ResolutionId = resolutionId,
                        Request = request,
                        ResolvedAction = resolvedAction,
                        Timestamp = DateTime.UtcNow;
                    });

                    await ReportProgressAsync(resolutionId, 90, "Action resolution completed", request.Context);

                    _logger.LogInformation(
                        "Completed action resolution {ResolutionId} in {ResolutionTime}ms. Confidence: {Confidence}",
                        resolutionId, resolvedAction.ResolutionTime.TotalMilliseconds, resolvedAction.ResolutionConfidence);

                    return resolvedAction;
                }
                finally
                {
                    _resolutionSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Action resolution cancelled for request: {RequestId}", request.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in action resolution for request: {RequestId}", request.Id);
                throw new ActionResolutionException($"Action resolution failed for request: {request.Id}", ex);
            }
        }

        /// <summary>
        /// Ayrıştırılmış eylemi doğrular;
        /// </summary>
        private async Task<ValidationResult> ValidateParsedActionAsync(
            ParsedAction parsedAction,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var result = new ValidationResult;
            {
                IsValid = false,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                // Check if parsed action exists;
                if (parsedAction == null)
                {
                    result.Errors.Add("Parsed action is null");
                    return result;
                }

                // Check primary verb;
                if (parsedAction.ComposedAction?.PrimaryVerb == null)
                {
                    result.Errors.Add("No primary verb found in parsed action");
                    return result;
                }

                // Check action confidence;
                if (parsedAction.Confidence < _config.MinimumActionConfidence)
                {
                    result.Errors.Add($"Action confidence too low: {parsedAction.Confidence}");
                    return result;
                }

                // Check for ambiguities;
                if (parsedAction.Ambiguities?.Any() == true)
                {
                    result.Warnings.Add($"Action has {parsedAction.Ambiguities.Count} ambiguities");
                }

                // Semantic validation;
                var semanticValidation = await ValidateSemanticsAsync(parsedAction, cancellationToken);
                if (!semanticValidation.IsValid)
                {
                    result.Errors.AddRange(semanticValidation.Errors);
                }

                // Domain-specific validation;
                var domainValidation = await ValidateDomainAsync(parsedAction, context, cancellationToken);
                if (!domainValidation.IsValid)
                {
                    result.Errors.AddRange(domainValidation.Errors);
                }

                result.IsValid = !result.Errors.Any();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating parsed action");
                result.Errors.Add($"Validation error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Eylemi servis operasyonuna eşler;
        /// </summary>
        private async Task<ActionMappingResult> MapActionToServiceAsync(
            ParsedAction parsedAction,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var result = new ActionMappingResult;
            {
                OriginalAction = parsedAction,
                CandidateMappings = new List<ActionMapping>(),
                SelectedMapping = null,
                MappingConfidence = 0.0;
            };

            try
            {
                // Get candidate mappings;
                var candidates = await FindCandidateMappingsAsync(parsedAction, context, cancellationToken);

                if (!candidates.Any())
                {
                    _logger.LogWarning("No candidate mappings found for action");
                    return result;
                }

                result.CandidateMappings = candidates;

                // Select best mapping;
                var bestMapping = await SelectBestMappingAsync(candidates, parsedAction, context, cancellationToken);

                if (bestMapping != null)
                {
                    result.SelectedMapping = bestMapping;
                    result.MappingConfidence = CalculateMappingConfidence(bestMapping, parsedAction);

                    _logger.LogDebug("Selected mapping: {MappingId} with confidence: {Confidence}",
                        bestMapping.Id, result.MappingConfidence);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error mapping action to service");
                return result;
            }
        }

        /// <summary>
        /// Aday eşleştirmeleri bulur;
        /// </summary>
        private async Task<List<ActionMapping>> FindCandidateMappingsAsync(
            ParsedAction parsedAction,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var candidates = new List<ActionMapping>();

            try
            {
                var primaryVerb = parsedAction.ComposedAction?.PrimaryVerb?.BaseForm?.ToLower();
                var primaryObject = parsedAction.ComposedAction?.PrimaryObject?.Text?.ToLower();
                var actionType = parsedAction.ComposedAction?.ActionType ?? ActionType.Unknown;
                var domain = parsedAction.Domain ?? context?.Domain ?? "General";

                // Find mappings by verb;
                var verbMatches = _actionMappings.Values;
                    .Where(m => m.IsActive &&
                               m.VerbPatterns.Any(vp =>
                                   primaryVerb?.Contains(vp.ToLower()) == true ||
                                   vp.ToLower().Contains(primaryVerb ?? "")))
                    .ToList();

                // Find mappings by object;
                var objectMatches = _actionMappings.Values;
                    .Where(m => m.IsActive &&
                               m.ObjectPatterns.Any(op =>
                                   primaryObject?.Contains(op.ToLower()) == true ||
                                   op.ToLower().Contains(primaryObject ?? "")))
                    .ToList();

                // Find mappings by action type;
                var typeMatches = _actionMappings.Values;
                    .Where(m => m.IsActive && m.ActionType == actionType)
                    .ToList();

                // Find mappings by domain;
                var domainMatches = _actionMappings.Values;
                    .Where(m => m.IsActive &&
                               (m.Domain.ToString().Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                                m.Domain == ActionDomain.All))
                    .ToList();

                // Combine and prioritize matches;
                var allMatches = verbMatches;
                    .Union(objectMatches)
                    .Union(typeMatches)
                    .Union(domainMatches)
                    .Distinct()
                    .ToList();

                // Filter by context if available;
                if (context != null)
                {
                    allMatches = await FilterByContextAsync(allMatches, context, cancellationToken);
                }

                // Sort by confidence and priority;
                candidates = allMatches;
                    .OrderByDescending(m => m.Confidence)
                    .ThenByDescending(m => CalculateMatchScore(m, parsedAction))
                    .Take(_config.MaxCandidateMappings)
                    .ToList();

                return candidates;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding candidate mappings");
                return candidates;
            }
        }

        /// <summary>
        /// Servis ve metodu çözümler;
        /// </summary>
        private async Task<ServiceResolutionResult> ResolveServiceAndMethodAsync(
            ActionMappingResult mappingResult,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var result = new ServiceResolutionResult;
            {
                Mapping = mappingResult.SelectedMapping,
                ServiceInstance = null,
                MethodInfo = null,
                ServiceType = null,
                ResolutionConfidence = 0.0;
            };

            try
            {
                if (mappingResult.SelectedMapping == null)
                {
                    _logger.LogWarning("No mapping selected for service resolution");
                    return result;
                }

                var mapping = mappingResult.SelectedMapping;

                // Resolve service;
                result.ServiceInstance = await ResolveServiceAsync(mapping.TargetService, context, cancellationToken);
                if (result.ServiceInstance == null)
                {
                    throw new ServiceResolutionException($"Service not found: {mapping.TargetService}");
                }

                // Resolve method;
                result.MethodInfo = await ResolveMethodAsync(
                    result.ServiceInstance,
                    mapping.TargetMethod,
                    cancellationToken);

                if (result.MethodInfo == null)
                {
                    throw new MethodResolutionException($"Method not found: {mapping.TargetMethod}");
                }

                result.ServiceType = result.ServiceInstance.GetType();
                result.ResolutionConfidence = mappingResult.MappingConfidence;

                _logger.LogDebug("Resolved service: {ServiceName}, method: {MethodName}",
                    mapping.TargetService, mapping.TargetMethod);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving service and method");
                throw;
            }
        }

        /// <summary>
        /// Servisi çözümler;
        /// </summary>
        private async Task<object> ResolveServiceAsync(
            string serviceName,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                // Try to resolve from service resolvers first;
                foreach (var resolver in _serviceResolvers.Values)
                {
                    var service = await resolver.ResolveServiceAsync(serviceName, context, cancellationToken);
                    if (service != null)
                    {
                        return service;
                    }
                }

                // Try to resolve from DI container;
                var serviceType = Type.GetType($"NEDA.Services.{serviceName}, NEDA.Services") ??
                                 Type.GetType($"NEDA.SystemControl.{serviceName}, NEDA.SystemControl") ??
                                 Type.GetType($"NEDA.{serviceName}, NEDA");

                if (serviceType != null)
                {
                    return _serviceProvider.GetService(serviceType);
                }

                // Try command executors;
                if (_commandExecutors.TryGetValue(serviceName, out var executor))
                {
                    return executor;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving service: {ServiceName}", serviceName);
                throw;
            }
        }

        /// <summary>
        /// Parametreleri çözümler;
        /// </summary>
        private async Task<ParameterResolutionResult> ResolveParametersAsync(
            ParsedAction parsedAction,
            ActionMappingResult mappingResult,
            ServiceResolutionResult serviceResolution,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var result = new ParameterResolutionResult;
            {
                Parameters = new Dictionary<string, object>(),
                ResolvedParameters = new Dictionary<string, object>(),
                ValidationResults = new Dictionary<string, ParameterValidationResult>(),
                ResolutionConfidence = 0.0;
            };

            try
            {
                if (mappingResult.SelectedMapping == null || serviceResolution.MethodInfo == null)
                {
                    return result;
                }

                // Get parameter templates for this action;
                var parameterTemplates = await GetParameterTemplatesAsync(
                    mappingResult.SelectedMapping,
                    parsedAction,
                    cancellationToken);

                // Resolve each parameter;
                foreach (var template in parameterTemplates)
                {
                    var parameterValue = await ResolveParameterValueAsync(
                        template.Key,
                        template.Value,
                        parsedAction,
                        context,
                        cancellationToken);

                    if (parameterValue != null)
                    {
                        result.Parameters[template.Key] = parameterValue;

                        // Validate parameter;
                        var validationResult = await ValidateParameterAsync(
                            template.Key,
                            parameterValue,
                            template.Value,
                            cancellationToken);

                        result.ValidationResults[template.Key] = validationResult;

                        if (validationResult.IsValid)
                        {
                            result.ResolvedParameters[template.Key] = parameterValue;
                        }
                    }
                }

                // Check for required parameters;
                var missingRequired = parameterTemplates;
                    .Where(t => t.Value.Required &&
                               !result.ResolvedParameters.ContainsKey(t.Key))
                    .ToList();

                if (missingRequired.Any())
                {
                    throw new ParameterResolutionException(
                        $"Missing required parameters: {string.Join(", ", missingRequired.Select(m => m.Key))}");
                }

                // Calculate resolution confidence;
                result.ResolutionConfidence = CalculateParameterResolutionConfidence(
                    result.ResolvedParameters,
                    result.ValidationResults);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving parameters");
                throw;
            }
        }

        /// <summary>
        /// Yürütme politikasını uygular;
        /// </summary>
        private async Task<PolicyApplicationResult> ApplyExecutionPolicyAsync(
            ActionMappingResult mappingResult,
            ServiceResolutionResult serviceResolution,
            ParameterResolutionResult parameterResolution,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var result = new PolicyApplicationResult;
            {
                AppliedPolicies = new List<ExecutionPolicy>(),
                ValidationResults = new List<PolicyValidationResult>(),
                ExecutionRules = new List<ExecutionRule>(),
                IsAllowed = false;
            };

            try
            {
                if (mappingResult.SelectedMapping == null)
                {
                    return result;
                }

                // Get applicable policies;
                var applicablePolicies = await GetApplicablePoliciesAsync(
                    mappingResult.SelectedMapping,
                    context,
                    cancellationToken);

                foreach (var policy in applicablePolicies)
                {
                    // Validate against policy rules;
                    var validationResult = await ValidateAgainstPolicyAsync(
                        policy,
                        mappingResult,
                        serviceResolution,
                        parameterResolution,
                        context,
                        cancellationToken);

                    result.ValidationResults.Add(validationResult);
                    result.AppliedPolicies.Add(policy);

                    if (validationResult.IsValid)
                    {
                        result.ExecutionRules.AddRange(policy.ExecutionRules);
                    }
                    else;
                    {
                        // Check if validation failure is critical;
                        if (validationResult.FailedRules.Any(r => r.Severity == ValidationSeverity.Error))
                        {
                            result.IsAllowed = false;
                            return result;
                        }
                    }
                }

                // Check permissions;
                var permissionCheck = await CheckPermissionsAsync(
                    mappingResult.SelectedMapping.RequiredPermissions,
                    context,
                    cancellationToken);

                if (!permissionCheck.IsAllowed)
                {
                    result.ValidationResults.Add(new PolicyValidationResult;
                    {
                        PolicyId = "PERMISSION_CHECK",
                        IsValid = false,
                        FailedRules = new List<ValidationRule>
                        {
                            new ValidationRule;
                            {
                                Type = ValidationType.PermissionCheck,
                                Condition = "UserHasPermission",
                                Message = $"Missing permissions: {string.Join(", ", permissionCheck.MissingPermissions)}",
                                Severity = ValidationSeverity.Error;
                            }
                        }
                    });

                    result.IsAllowed = false;
                    return result;
                }

                result.IsAllowed = result.ValidationResults.All(v => v.IsValid) ||
                                  result.ValidationResults.Where(v => !v.IsValid)
                                    .All(v => v.FailedRules.All(r => r.Severity != ValidationSeverity.Error));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying execution policy");
                throw;
            }
        }

        /// <summary>
        /// Komut oluşturur;
        /// </summary>
        private async Task<CommandGenerationResult> GenerateCommandAsync(
            ServiceResolutionResult serviceResolution,
            ParameterResolutionResult parameterResolution,
            PolicyApplicationResult policyApplication,
            CancellationToken cancellationToken)
        {
            var result = new CommandGenerationResult;
            {
                CommandId = Guid.NewGuid().ToString(),
                Service = serviceResolution.ServiceInstance,
                Method = serviceResolution.MethodInfo,
                Parameters = parameterResolution.ResolvedParameters,
                ExecutionRules = policyApplication.ExecutionRules,
                GeneratedAt = DateTime.UtcNow;
            };

            try
            {
                // Get command template;
                var template = await GetCommandTemplateAsync(
                    serviceResolution.Mapping,
                    cancellationToken);

                if (template != null)
                {
                    // Generate command from template;
                    result.CommandText = await GenerateCommandFromTemplateAsync(
                        template,
                        serviceResolution,
                        parameterResolution,
                        cancellationToken);

                    result.TemplateId = template.Id;
                }
                else;
                {
                    // Generate dynamic command;
                    result.CommandText = await GenerateDynamicCommandAsync(
                        serviceResolution,
                        parameterResolution,
                        cancellationToken);
                }

                // Add execution rules to command;
                result.CommandText = await AddExecutionRulesToCommandAsync(
                    result.CommandText,
                    policyApplication.ExecutionRules,
                    cancellationToken);

                result.GenerationConfidence = CalculateCommandGenerationConfidence(
                    serviceResolution,
                    parameterResolution,
                    template);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating command");
                throw;
            }
        }

        /// <summary>
        /// Komutu doğrular;
        /// </summary>
        private async Task<CommandValidationResult> ValidateCommandAsync(
            CommandGenerationResult commandGeneration,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var result = new CommandValidationResult;
            {
                IsValid = false,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                ValidationRules = new List<CommandValidationRule>()
            };

            try
            {
                // Syntax validation;
                var syntaxValidation = await ValidateCommandSyntaxAsync(commandGeneration, cancellationToken);
                if (!syntaxValidation.IsValid)
                {
                    result.Errors.AddRange(syntaxValidation.Errors);
                }

                // Semantic validation;
                var semanticValidation = await ValidateCommandSemanticsAsync(commandGeneration, context, cancellationToken);
                if (!semanticValidation.IsValid)
                {
                    result.Errors.AddRange(semanticValidation.Errors);
                }

                // Safety validation;
                var safetyValidation = await ValidateCommandSafetyAsync(commandGeneration, context, cancellationToken);
                if (!safetyValidation.IsValid)
                {
                    result.Errors.AddRange(safetyValidation.Errors);
                    result.Warnings.AddRange(safetyValidation.Warnings);
                }

                // Impact assessment;
                var impactAssessment = await AssessCommandImpactAsync(commandGeneration, context, cancellationToken);
                if (impactAssessment.ImpactScore > _config.MaxImpactThreshold)
                {
                    result.Warnings.Add($"High impact command detected: {impactAssessment.ImpactScore}");
                }

                result.IsValid = !result.Errors.Any();
                result.ValidationRules = syntaxValidation.ValidationRules;
                    .Union(semanticValidation.ValidationRules)
                    .Union(safetyValidation.ValidationRules)
                    .ToList();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating command");
                result.Errors.Add($"Validation error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Yürütme planı oluşturur;
        /// </summary>
        private async Task<ExecutionPlan> BuildExecutionPlanAsync(
            CommandGenerationResult commandGeneration,
            PolicyApplicationResult policyApplication,
            ResolutionContext context,
            CancellationToken cancellationToken)
        {
            var plan = new ExecutionPlan;
            {
                PlanId = Guid.NewGuid().ToString(),
                Command = commandGeneration,
                Steps = new List<ExecutionStep>(),
                Dependencies = new List<ExecutionDependency>(),
                EstimatedDuration = TimeSpan.Zero,
                RiskLevel = RiskLevel.Low;
            };

            try
            {
                // Add pre-execution steps;
                plan.Steps.Add(new ExecutionStep;
                {
                    StepId = "STEP_PRE_VALIDATION",
                    Description = "Pre-execution validation",
                    Type = ExecutionStepType.Validation,
                    Order = 1,
                    Required = true;
                });

                // Add permission check step;
                plan.Steps.Add(new ExecutionStep;
                {
                    StepId = "STEP_PERMISSION_CHECK",
                    Description = "Permission verification",
                    Type = ExecutionStepType.Security,
                    Order = 2,
                    Required = true;
                });

                // Add main execution step;
                plan.Steps.Add(new ExecutionStep;
                {
                    StepId = "STEP_MAIN_EXECUTION",
                    Description = "Command execution",
                    Type = ExecutionStepType.Execution,
                    Order = 3,
                    Required = true;
                });

                // Add post-execution steps;
                plan.Steps.Add(new ExecutionStep;
                {
                    StepId = "STEP_POST_VALIDATION",
                    Description = "Post-execution validation",
                    Type = ExecutionStepType.Validation,
                    Order = 4,
                    Required = true;
                });

                // Add logging step;
                plan.Steps.Add(new ExecutionStep;
                {
                    StepId = "STEP_LOGGING",
                    Description = "Execution logging",
                    Type = ExecutionStepType.Logging,
                    Order = 5,
                    Required = true;
                });

                // Analyze dependencies;
                plan.Dependencies = await AnalyzeDependenciesAsync(commandGeneration, context, cancellationToken);

                // Estimate duration;
                plan.EstimatedDuration = await EstimateExecutionDurationAsync(commandGeneration, cancellationToken);

                // Assess risk;
                plan.RiskLevel = await AssessExecutionRiskAsync(commandGeneration, policyApplication, cancellationToken);

                // Set success criteria;
                plan.SuccessCriteria = await DetermineSuccessCriteriaAsync(commandGeneration, cancellationToken);

                return plan;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building execution plan");
                throw;
            }
        }

        /// <summary>
        /// Çözümlenmiş eylemi oluşturur;
        /// </summary>
        private async Task<ResolvedAction> BuildResolvedActionAsync(
            string resolutionId,
            ActionResolutionRequest request,
            ActionMappingResult mappingResult,
            ServiceResolutionResult serviceResolution,
            ParameterResolutionResult parameterResolution,
            PolicyApplicationResult policyApplication,
            CommandGenerationResult commandGeneration,
            ExecutionPlan executionPlan,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            var resolvedAction = new ResolvedAction;
            {
                Id = resolutionId,
                RequestId = request.Id,
                ParsedAction = request.ParsedAction,
                MappingResult = mappingResult,
                ServiceResolution = serviceResolution,
                ParameterResolution = parameterResolution,
                PolicyApplication = policyApplication,
                CommandGeneration = commandGeneration,
                ExecutionPlan = executionPlan,
                ResolutionTime = DateTime.UtcNow - startTime,
                ResolutionConfidence = CalculateOverallConfidence(
                    mappingResult,
                    serviceResolution,
                    parameterResolution,
                    policyApplication,
                    commandGeneration),
                Timestamp = DateTime.UtcNow,
                Status = ActionResolutionStatus.Resolved,
                Metadata = new Dictionary<string, object>
                {
                    ["resolution_version"] = "1.0",
                    ["policies_applied"] = policyApplication.AppliedPolicies.Count,
                    ["parameters_resolved"] = parameterResolution.ResolvedParameters.Count;
                }
            };

            // Generate human-readable summary;
            resolvedAction.Summary = await GenerateResolutionSummaryAsync(resolvedAction, cancellationToken);

            // Determine next steps;
            resolvedAction.NextSteps = await DetermineNextStepsAsync(resolvedAction, cancellationToken);

            // Set resolution flags;
            resolvedAction.CanExecute = policyApplication.IsAllowed &&
                                       commandGeneration.GenerationConfidence >= _config.MinimumExecutionConfidence;

            resolvedAction.RequiresConfirmation = resolvedAction.ExecutionPlan?.RiskLevel >= RiskLevel.Medium ||
                                                 policyApplication.ValidationResults.Any(v =>
                                                     v.FailedRules.Any(r => r.Severity == ValidationSeverity.Warning));

            return resolvedAction;
        }

        /// <summary>
        /// Eylemi yürütür;
        /// </summary>
        public async Task<ActionExecutionResult> ExecuteActionAsync(
            ActionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var executionId = Guid.NewGuid().ToString();

                // Event: Execution started;
                OnExecutionStarted?.Invoke(this, new CommandExecutionStartedEventArgs;
                {
                    ExecutionId = executionId,
                    Request = request,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Starting action execution {ExecutionId} for request: {RequestId}",
                    executionId, request.Id);

                var startTime = DateTime.UtcNow;

                // Get resolved action;
                var resolvedAction = request.ResolvedAction;
                if (resolvedAction == null)
                {
                    throw new ArgumentException("Resolved action is required for execution");
                }

                // Check if action can be executed;
                if (!resolvedAction.CanExecute && !request.ForceExecution)
                {
                    throw new ActionExecutionException("Action cannot be executed due to validation failures");
                }

                // Check confirmation if required;
                if (resolvedAction.RequiresConfirmation && !request.Confirmed && !request.ForceExecution)
                {
                    throw new ConfirmationRequiredException("Action requires confirmation before execution");
                }

                // Execute according to plan;
                var executionResult = await ExecuteWithPlanAsync(
                    executionId,
                    resolvedAction,
                    request,
                    cancellationToken);

                // Update resolved action status;
                resolvedAction.Status = executionResult.Success ?
                    ActionResolutionStatus.Executed :
                    ActionResolutionStatus.Failed;

                resolvedAction.ExecutionResult = executionResult;

                // Record execution;
                await RecordExecutionAsync(resolvedAction, executionResult, cancellationToken);

                // Event: Execution completed;
                OnExecutionCompleted?.Invoke(this, new CommandExecutionCompletedEventArgs;
                {
                    ExecutionId = executionId,
                    Request = request,
                    Result = executionResult,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation(
                    "Completed action execution {ExecutionId} in {ExecutionTime}ms. Success: {Success}",
                    executionId, executionResult.ExecutionTime.TotalMilliseconds, executionResult.Success);

                return executionResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Action execution cancelled for request: {RequestId}", request.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in action execution for request: {RequestId}", request.Id);
                throw new ActionExecutionException($"Action execution failed for request: {request.Id}", ex);
            }
        }

        /// <summary>
        /// Plan ile yürütür;
        /// </summary>
        private async Task<ActionExecutionResult> ExecuteWithPlanAsync(
            string executionId,
            ResolvedAction resolvedAction,
            ActionExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var result = new ActionExecutionResult;
            {
                ExecutionId = executionId,
                ResolvedActionId = resolvedAction.Id,
                Steps = new List<ExecutionStepResult>(),
                StartTime = DateTime.UtcNow,
                Success = false;
            };

            try
            {
                var plan = resolvedAction.ExecutionPlan;

                // Execute each step in order;
                foreach (var step in plan.Steps.OrderBy(s => s.Order))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var stepResult = await ExecuteStepAsync(
                        step,
                        resolvedAction,
                        request,
                        cancellationToken);

                    result.Steps.Add(stepResult);

                    // Check if step failed and is required;
                    if (!stepResult.Success && step.Required)
                    {
                        result.Error = stepResult.Error;
                        break;
                    }
                }

                result.EndTime = DateTime.UtcNow;
                result.ExecutionTime = result.EndTime - result.StartTime;
                result.Success = result.Steps.All(s => s.Success || !s.Step.Required);

                // Collect outputs;
                result.Outputs = result.Steps;
                    .Where(s => s.Output != null)
                    .ToDictionary(s => s.Step.StepId, s => s.Output);

                // Calculate execution metrics;
                result.Metrics = await CalculateExecutionMetricsAsync(result, cancellationToken);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing with plan");
                result.Error = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.ExecutionTime = result.EndTime - result.StartTime;
                return result;
            }
        }

        /// <summary>
        /// ActionResolver istatistiklerini getirir;
        /// </summary>
        public async Task<ActionResolverStatistics> GetStatisticsAsync()
        {
            ValidateInitialization();

            try
            {
                var stats = new ActionResolverStatistics;
                {
                    TotalMappings = _actionMappings.Count,
                    ActiveMappings = _actionMappings.Values.Count(m => m.IsActive),
                    CommandTemplates = _commandTemplates.Count,
                    ExecutionPolicies = _executionPolicies.Count,
                    CacheHitRate = CalculateCacheHitRate(),
                    AverageResolutionTime = CalculateAverageResolutionTime(),
                    AverageExecutionTime = CalculateAverageExecutionTime(),
                    TotalResolutions = await GetTotalResolutionsAsync(),
                    TotalExecutions = await GetTotalExecutionsAsync(),
                    SuccessRate = await CalculateSuccessRateAsync(),
                    DomainDistribution = GetDomainDistribution(),
                    ActionTypeDistribution = GetActionTypeDistribution(),
                    Uptime = DateTime.UtcNow - _startTime,
                    MemoryUsage = GC.GetTotalMemory(false),
                    CurrentLoad = _config.MaxConcurrentResolutions - _resolutionSemaphore.CurrentCount;
                };

                // Service resolution statistics;
                stats.ServiceResolutionStats = await GetServiceResolutionStatsAsync();

                // Policy application statistics;
                stats.PolicyApplicationStats = await GetPolicyApplicationStatsAsync();

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                throw;
            }
        }

        /// <summary>
        /// ActionResolver'ı durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                _logger.LogInformation("Shutting down ActionResolver...");

                // Wait for ongoing resolutions and executions;
                await Task.Delay(1000);

                // Clear caches;
                ClearCaches();

                // Shutdown dependencies;
                await _semanticAnalyzer.ShutdownAsync();

                foreach (var resolver in _serviceResolvers.Values)
                {
                    await resolver.ShutdownAsync();
                }

                foreach (var executor in _commandExecutors.Values)
                {
                    await executor.ShutdownAsync();
                }

                _isInitialized = false;

                _logger.LogInformation("ActionResolver shutdown completed");
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

                _resolutionSemaphore?.Dispose();
                ClearCaches();

                // Clear event handlers;
                OnResolutionStarted = null;
                OnResolutionProgress = null;
                OnResolutionCompleted = null;
                OnExecutionStarted = null;
                OnExecutionCompleted = null;
                OnValidationFailed = null;
            }
        }

        // Helper methods;
        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new ActionResolverNotInitializedException(
                    "ActionResolver must be initialized before use. Call InitializeAsync() first.");
            }
        }

        private string GenerateCacheKey(ActionResolutionRequest request)
        {
            var actionId = request.ParsedAction?.Id ?? "unknown";
            var verb = request.ParsedAction?.ComposedAction?.PrimaryVerb?.BaseForm ?? "unknown";
            var domain = request.Context?.Domain ?? "general";
            return $"{actionId}_{verb}_{domain}";
        }

        private string GenerateResolutionKey(string actionId, string domain)
        {
            return $"{actionId}_{domain}";
        }

        private async Task ReportProgressAsync(
            string resolutionId,
            int progressPercentage,
            string message,
            ResolutionContext context)
        {
            try
            {
                OnResolutionProgress?.Invoke(this, new ActionResolutionProgressEventArgs;
                {
                    ResolutionId = resolutionId,
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

        private async Task RecordResolutionAsync(ResolvedAction resolvedAction, CancellationToken cancellationToken)
        {
            try
            {
                var historicalResolution = new HistoricalResolution;
                {
                    Id = Guid.NewGuid().ToString(),
                    ActionId = resolvedAction.ParsedAction.Id,
                    ResolutionId = resolvedAction.Id,
                    Domain = resolvedAction.ParsedAction.Domain,
                    ActionType = resolvedAction.ParsedAction.ComposedAction.ActionType,
                    ResolutionConfidence = resolvedAction.ResolutionConfidence,
                    Timestamp = resolvedAction.Timestamp,
                    Success = true;
                };

                await _knowledgeBase.RecordHistoricalResolutionAsync(historicalResolution, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error recording resolution");
            }
        }

        private async Task RecordExecutionAsync(ResolvedAction resolvedAction, ActionExecutionResult executionResult, CancellationToken cancellationToken)
        {
            try
            {
                var historicalExecution = new HistoricalExecution;
                {
                    Id = Guid.NewGuid().ToString(),
                    ResolutionId = resolvedAction.Id,
                    ExecutionId = executionResult.ExecutionId,
                    Success = executionResult.Success,
                    ExecutionTime = executionResult.ExecutionTime,
                    Error = executionResult.Error,
                    Timestamp = DateTime.UtcNow;
                };

                await _knowledgeBase.RecordHistoricalExecutionAsync(historicalExecution, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error recording execution");
            }
        }

        private void ClearCaches()
        {
            _resolutionCache.Clear();
            _logger.LogDebug("ActionResolver caches cleared");
        }

        private DateTime _startTime = DateTime.UtcNow;

        // Additional helper and calculation methods would continue here...
    }

    #region Supporting Classes and Enums;

    public enum ActionDomain;
    {
        FileSystem,
        ProjectManagement,
        SystemControl,
        DataAnalysis,
        Development,
        Security,
        Network,
        Database,
        Cloud,
        AI,
        All;
    }

    public enum ActionResolutionStatus;
    {
        Pending,
        Resolved,
        Validated,
        Executed,
        Failed,
        Cancelled;
    }

    public enum ParameterSource;
    {
        Object,
        Context,
        User,
        System,
        Default,
        Calculated;
    }

    public enum ParameterType;
    {
        String,
        Number,
        Boolean,
        Object,
        Array,
        File,
        Path,
        Url,
        Command;
    }

    public enum ValidationType;
    {
        PermissionCheck,
        ParameterValidation,
        ImpactAssessment,
        AdministratorCheck,
        ConfirmationRequired,
        SafetyCheck;
    }

    public enum ValidationSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    public enum ExecutionRuleType;
    {
        Timeout,
        Retry,
        Logging,
        Audit,
        Rollback,
        Notification;
    }

    public enum RiskLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum ExecutionStepType;
    {
        Validation,
        Security,
        Execution,
        Logging,
        Cleanup,
        Notification;
    }

    public class ActionResolverConfig;
    {
        public bool EnableCaching { get; set; } = true;
        public double CacheThreshold { get; set; } = 0.7;
        public int MaxConcurrentResolutions { get; set; } = Environment.ProcessorCount * 2;
        public int MaxConcurrentExecutions { get; set; } = Environment.ProcessorCount;
        public int MaxCandidateMappings { get; set; } = 10;
        public double MinimumActionConfidence { get; set; } = 0.5;
        public double MinimumExecutionConfidence { get; set; } = 0.7;
        public double MaxImpactThreshold { get; set; } = 0.8;
        public bool EnableAutomaticExecution { get; set; } = false;
        public TimeSpan ResolutionTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    public class ActionResolverOptions;
    {
        public bool EnableDetailedLogging { get; set; } = true;
        public bool EnablePerformanceMetrics { get; set; } = true;
        public bool EnableSecurityValidation { get; set; } = true;
        public bool EnableImpactAssessment { get; set; } = true;
        public int MaxParameterResolutionDepth { get; set; } = 3;
        public double MinimumMappingConfidence { get; set; } = 0.6;
    }

    public class ActionResolutionRequest;
    {
        public string Id { get; set; }
        public ParsedAction ParsedAction { get; set; }
        public ResolutionContext Context { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public DateTime RequestTime { get; set; }
    }

    public class ResolvedAction;
    {
        public string Id { get; set; }
        public string RequestId { get; set; }
        public string Summary { get; set; }
        public ParsedAction ParsedAction { get; set; }
        public ActionMappingResult MappingResult { get; set; }
        public ServiceResolutionResult ServiceResolution { get; set; }
        public ParameterResolutionResult ParameterResolution { get; set; }
        public PolicyApplicationResult PolicyApplication { get; set; }
        public CommandGenerationResult CommandGeneration { get; set; }
        public ExecutionPlan ExecutionPlan { get; set; }
        public ActionExecutionResult ExecutionResult { get; set; }
        public List<string> NextSteps { get; set; }
        public double ResolutionConfidence { get; set; }
        public TimeSpan ResolutionTime { get; set; }
        public DateTime Timestamp { get; set; }
        public ActionResolutionStatus Status { get; set; }
        public bool CanExecute { get; set; }
        public bool RequiresConfirmation { get; set; }
        public string Domain { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ActionMapping;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ActionType ActionType { get; set; }
        public ActionDomain Domain { get; set; }
        public List<string> VerbPatterns { get; set; }
        public List<string> ObjectPatterns { get; set; }
        public string TargetService { get; set; }
        public string TargetMethod { get; set; }
        public List<string> RequiredPermissions { get; set; }
        public List<string> SuccessCriteria { get; set; }
        public double Confidence { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsed { get; set; }
        public int UsageCount { get; set; }
    }

    public class CommandTemplate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ActionDomain Domain { get; set; }
        public string Template { get; set; }
        public Dictionary<string, ParameterTemplate> Parameters { get; set; }
        public List<string> SuccessConditions { get; set; }
    }

    // Additional supporting classes would be defined here...
    // Due to length constraints, I'm showing the main structure;

    #endregion;

    #region Exceptions;

    public class ActionResolverException : Exception
    {
        public ActionResolverException(string message) : base(message) { }
        public ActionResolverException(string message, Exception inner) : base(message, inner) { }
    }

    public class ActionResolverNotInitializedException : ActionResolverException;
    {
        public ActionResolverNotInitializedException(string message) : base(message) { }
    }

    public class ActionResolutionException : ActionResolverException;
    {
        public ActionResolutionException(string message) : base(message) { }
        public ActionResolutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class ActionValidationException : ActionResolverException;
    {
        public ActionValidationException(string message) : base(message) { }
        public ActionValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ServiceResolutionException : ActionResolverException;
    {
        public ServiceResolutionException(string message) : base(message) { }
        public ServiceResolutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class MethodResolutionException : ActionResolverException;
    {
        public MethodResolutionException(string message) : base(message) { }
        public MethodResolutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class ParameterResolutionException : ActionResolverException;
    {
        public ParameterResolutionException(string message) : base(message) { }
        public ParameterResolutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class CommandValidationException : ActionResolverException;
    {
        public CommandValidationException(string message) : base(message) { }
        public CommandValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ActionExecutionException : ActionResolverException;
    {
        public ActionExecutionException(string message) : base(message) { }
        public ActionExecutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfirmationRequiredException : ActionResolverException;
    {
        public ConfirmationRequiredException(string message) : base(message) { }
    }

    #endregion;

    #region Events;

    public class ActionResolutionStartedEventArgs : EventArgs;
    {
        public string ResolutionId { get; set; }
        public ActionResolutionRequest Request { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActionResolutionProgressEventArgs : EventArgs;
    {
        public string ResolutionId { get; set; }
        public int ProgressPercentage { get; set; }
        public string Message { get; set; }
        public ResolutionContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActionResolutionCompletedEventArgs : EventArgs;
    {
        public string ResolutionId { get; set; }
        public ActionResolutionRequest Request { get; set; }
        public ResolvedAction ResolvedAction { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CommandExecutionStartedEventArgs : EventArgs;
    {
        public string ExecutionId { get; set; }
        public ActionExecutionRequest Request { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CommandExecutionCompletedEventArgs : EventArgs;
    {
        public string ExecutionId { get; set; }
        public ActionExecutionRequest Request { get; set; }
        public ActionExecutionResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActionValidationFailedEventArgs : EventArgs;
    {
        public string ValidationId { get; set; }
        public ValidationResult ValidationResult { get; set; }
        public ActionResolutionRequest Request { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}

// Interface definition for dependency injection;
public interface IActionResolver : IDisposable
{
    Task InitializeAsync(ActionResolverConfig config = null);
    Task<ResolvedAction> ResolveActionAsync(
        ActionResolutionRequest request,
        CancellationToken cancellationToken = default);
    Task<ActionExecutionResult> ExecuteActionAsync(
        ActionExecutionRequest request,
        CancellationToken cancellationToken = default);
    Task<ActionResolverStatistics> GetStatisticsAsync();
    Task ShutdownAsync();

    bool IsInitialized { get; }

    event EventHandler<ActionResolutionStartedEventArgs> OnResolutionStarted;
    event EventHandler<ActionResolutionProgressEventArgs> OnResolutionProgress;
    event EventHandler<ActionResolutionCompletedEventArgs> OnResolutionCompleted;
    event EventHandler<CommandExecutionStartedEventArgs> OnExecutionStarted;
    event EventHandler<CommandExecutionCompletedEventArgs> OnExecutionCompleted;
    event EventHandler<ActionValidationFailedEventArgs> OnValidationFailed;
}

// Supporting interfaces;
public interface IServiceResolver : IDisposable
{
    Task InitializeAsync();
    Task<object> ResolveServiceAsync(string serviceName, ResolutionContext context, CancellationToken cancellationToken);
    Task ShutdownAsync();
}

public interface ICommandExecutor : IDisposable
{
    Task InitializeAsync();
    Task<ExecutionResult> ExecuteCommandAsync(Command command, CancellationToken cancellationToken);
    Task ShutdownAsync();
}

public interface IDomainResolver : IDisposable
{
    Task InitializeAsync();
    Task<bool> CanResolveAsync(ActionDomain domain, CancellationToken cancellationToken);
    Task<object> ResolveAsync(ParsedAction action, ResolutionContext context, CancellationToken cancellationToken);
    Task ShutdownAsync();
}

// Example implementations;
public class FileSystemServiceResolver : IServiceResolver;
{
    private readonly IServiceProvider _serviceProvider;

    public FileSystemServiceResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<object> ResolveServiceAsync(string serviceName, ResolutionContext context, CancellationToken cancellationToken)
    {
        if (serviceName == "FileManager")
        {
            return Task.FromResult(_serviceProvider.GetService<IFileManager>());
        }

        return Task.FromResult<object>(null);
    }

    public Task ShutdownAsync() => Task.CompletedTask;
    public void Dispose() { }
}
